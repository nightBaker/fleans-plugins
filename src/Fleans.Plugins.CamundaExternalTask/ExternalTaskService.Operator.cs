using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleans.Plugins.CamundaExternalTask;

// Operator side of the Camunda 7 API: incidents, retries and the read-only task queries.
public sealed partial class ExternalTaskService
{
    /// <summary>
    /// Parks a claimed task as an incident: retries 0, unlocked, not fetchable. The activity keeps
    /// waiting until an operator sets its retries again.
    /// </summary>
    private async Task CreateIncident(ExternalTaskEntity task, string? errorMessage, string? errorDetails)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.ExternalTasks.Where(t => t.Id == task.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Retries, 0)
                .SetProperty(t => t.ErrorMessage, errorMessage)
                .SetProperty(t => t.ErrorDetails, errorDetails)
                .SetProperty(t => t.WorkerId, (string?)null)
                .SetProperty(t => t.LockExpirationTime, (DateTime?)null)
                .SetProperty(t => t.ReportingUntil, (DateTime?)null));
        LogIncidentCreated(task.Id, task.TopicName, errorMessage);
    }

    /// <summary>
    /// <c>PUT /external-task/{id}/retries</c>. A positive value resolves an incident (the task can be
    /// fetched again). Zero creates an incident, or fails the activity when incidents are off.
    /// </summary>
    public async Task SetRetries(string id, RetriesDto request)
    {
        if (request.Retries is not { } retries || retries < 0)
            throw BadRequest("retries must be a number greater than or equal to 0.");

        if (retries == 0)
        {
            var task = await ClaimForReport(id, workerId: null, "updated");
            if (options.Value.Incidents)
                await CreateIncident(task, task.ErrorMessage, task.ErrorDetails);
            else
                await FailRetriesExhausted(task, task.ErrorMessage ?? "Retries set to 0 by an operator", task.ErrorDetails);
            return;
        }

        var now = UtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();
        var updated = await db.ExternalTasks
            .Where(t => t.Id == id && (t.ReportingUntil == null || t.ReportingUntil <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Retries, retries));
        if (updated == 0)
            throw await Rejected(id, null, "updated");
        LogRetriesSet(id, retries);
        signal.Pulse(); // a resolved incident is fetchable now
    }

    public async Task<List<ExternalTaskDto>> Query(ExternalTaskQueryDto query)
    {
        if (query.FirstResult is < 0 || query.MaxResults is < 0)
            throw BadRequest("firstResult and maxResults must not be negative.");
        await PruneStaleIncidents(query);
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = Filter(db, query).OrderBy(t => t.CreateTime).ThenBy(t => t.Id).Skip(query.FirstResult ?? 0);
        if (query.MaxResults is { } max)
            rows = rows.Take(max);
        return (await rows.ToListAsync()).Select(ToDto).ToList();
    }

    public async Task<CountResultDto> Count(ExternalTaskQueryDto query)
    {
        await PruneStaleIncidents(query);
        await using var db = await dbFactory.CreateDbContextAsync();
        return new CountResultDto(await Filter(db, query).LongCountAsync());
    }

    public async Task<ExternalTaskDto> Get(string id) => ToDto(await LoadLive(id));

    public async Task<string?> GetErrorDetails(string id) => (await LoadLive(id)).ErrorDetails;

    /// <summary>Like <c>Load</c>, but an incident whose activity has ended is removed and is a 404.</summary>
    private async Task<ExternalTaskEntity> LoadLive(string id)
    {
        var task = await Load(id);
        if (task.Retries == 0 && await RemoveIfStale(task))
            throw NotFound(id);
        return task;
    }

    /// <summary>
    /// Incidents are never fetched, so the fetch-time stale cleanup never sees them. Before the
    /// operator queries answer, drop the matching incidents whose activity has ended (workflow
    /// cancelled, activity interrupted by a boundary event). Incidents are few, so one engine call each.
    /// </summary>
    private async Task PruneStaleIncidents(ExternalTaskQueryDto query)
    {
        List<ExternalTaskEntity> incidents;
        await using (var db = await dbFactory.CreateDbContextAsync())
            incidents = await Filter(db, query).Where(t => t.Retries == 0).ToListAsync();
        foreach (var incident in incidents)
            await RemoveIfStale(incident);
    }

    /// <summary>Deletes the task when its activity is no longer active; a transient check error keeps it.</summary>
    private async Task<bool> RemoveIfStale(ExternalTaskEntity task)
    {
        try
        {
            if (await engine.GetActiveActivityVariables(task.WorkflowInstanceId, task.ActivityInstanceId) is not null)
                return false;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return false;
        }
        await Delete(task.Id);
        LogStaleTaskRemoved(task.Id, task.TopicName);
        return true;
    }

    public async Task<List<string>> TopicNames(bool withLockedTasks, bool withUnlockedTasks, bool withRetriesLeft)
    {
        var now = UtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.ExternalTasks.AsNoTracking();
        if (withLockedTasks)
            q = q.Where(t => t.LockExpirationTime > now);
        if (withUnlockedTasks)
            q = q.Where(t => t.LockExpirationTime == null || t.LockExpirationTime <= now);
        if (withRetriesLeft)
            q = q.Where(t => t.Retries == null || t.Retries > 0);
        return await q.Select(t => t.TopicName).Distinct().OrderBy(n => n).ToListAsync();
    }

    private IQueryable<ExternalTaskEntity> Filter(ExternalTaskDbContext db, ExternalTaskQueryDto query)
    {
        var now = UtcNow();
        var q = db.ExternalTasks.AsNoTracking();
        if (query.ExternalTaskId is { } id)
            q = q.Where(t => t.Id == id);
        if (query.TopicName is { } topic)
            q = q.Where(t => t.TopicName == topic);
        if (query.WorkerId is { } worker)
            q = q.Where(t => t.WorkerId == worker);
        if (query.Locked == true)
            q = q.Where(t => t.LockExpirationTime > now);
        if (query.NotLocked == true)
            q = q.Where(t => t.LockExpirationTime == null || t.LockExpirationTime <= now);
        if (query.WithRetriesLeft == true)
            q = q.Where(t => t.Retries == null || t.Retries > 0);
        if (query.NoRetriesLeft == true)
            q = q.Where(t => t.Retries == 0);
        if (query.ActivityId is { } activityId)
            q = q.Where(t => t.ActivityId == activityId);
        if (query.ProcessDefinitionId is { } definitionId)
            q = q.Where(t => t.ProcessDefinitionId == definitionId);
        if (query.ProcessInstanceId is { } instanceId && Guid.TryParse(instanceId, out var instance))
            q = q.Where(t => t.WorkflowInstanceId == instance);
        else if (query.ProcessInstanceId is not null)
            q = q.Where(_ => false);
        if (query.TenantIdIn is { } tenants)
        {
            var ids = tenants.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            q = q.Where(t => t.TenantId != null && ids.Contains(t.TenantId));
        }
        return q;
    }

    private ExternalTaskDto ToDto(ExternalTaskEntity task) => new(
        Id: task.Id,
        TopicName: task.TopicName,
        WorkerId: task.WorkerId,
        LockExpirationTime: task.LockExpirationTime is { } expires ? CamundaVariables.FormatDate(expires) : null,
        CreateTime: CamundaVariables.FormatDate(task.CreateTime),
        ActivityId: task.ActivityId,
        ActivityInstanceId: task.ActivityInstanceId.ToString("D"),
        ExecutionId: task.ActivityInstanceId.ToString("D"),
        ProcessInstanceId: task.WorkflowInstanceId.ToString("D"),
        ProcessDefinitionId: task.ProcessDefinitionId,
        ProcessDefinitionKey: task.ProcessDefinitionKey,
        ProcessDefinitionVersionTag: null,
        Retries: task.Retries,
        ErrorMessage: task.ErrorMessage,
        Suspended: false,
        TenantId: task.TenantId,
        Priority: task.Priority,
        BusinessKey: task.BusinessKey);

    [LoggerMessage(EventId = 9358, Level = LogLevel.Error,
        Message = "External task {ExternalTaskId} (topic '{TopicName}') has no retries left — incident created: {ErrorMessage}")]
    private partial void LogIncidentCreated(string externalTaskId, string topicName, string? errorMessage);

    [LoggerMessage(EventId = 9359, Level = LogLevel.Information,
        Message = "External task {ExternalTaskId} retries set to {Retries}")]
    private partial void LogRetriesSet(string externalTaskId, int retries);
}
