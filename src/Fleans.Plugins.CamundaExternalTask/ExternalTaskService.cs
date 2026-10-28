using System.Data.Common;
using System.Dynamic;
using System.Globalization;
using System.Text.Json;
using Fleans.Application.Grains;
using Fleans.Domain.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Fleans.Plugins.CamundaExternalTask;

/// <summary>A Camunda-shaped REST error: HTTP status + <c>{type, message}</c> body.</summary>
public sealed class ExternalTaskApiException(int statusCode, string type, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Type { get; } = type;
}

/// <summary>
/// Camunda 7 external-task semantics on top of <see cref="ExternalTaskDbContext"/> and the engine's
/// narrow <see cref="IWorkflowInstanceCallback"/>. Locks are claimed with a conditional
/// <c>UPDATE … WHERE lock is free</c>, so any number of API replicas can share one table.
/// </summary>
public sealed partial class ExternalTaskService(
    IDbContextFactory<ExternalTaskDbContext> dbFactory,
    IGrainFactory grainFactory,
    ExternalTaskSignal signal,
    TimeProvider timeProvider,
    IOptions<CamundaExternalTaskOptions> options,
    ILogger<ExternalTaskService> logger)
{
    /// <summary>Camunda's upper bound for <c>asyncResponseTimeout</c>.</summary>
    private static readonly TimeSpan MaxAsyncResponseTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Long polls re-check the table this often even without a wake-up — it catches tasks queued by
    /// another replica's stream handler and expired locks, neither of which signals this process.
    /// </summary>
    private static readonly TimeSpan LongPollFallbackInterval = TimeSpan.FromSeconds(2);

    /// <summary>How long a task stays locked while a worker's report is written into the workflow.</summary>
    private static readonly TimeSpan ReportLockDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Input-mapping target prefix for Camunda's <c>camunda:properties</c>:
    /// <c>target="extensionProperty_region"</c> is returned as extension property <c>region</c>
    /// to workers that fetch with <c>includeExtensionProperties</c>, and is not a variable.
    /// </summary>
    public const string ExtensionPropertyPrefix = "extensionProperty_";

    /// <summary>Error code a Fleans error boundary sees when a worker exhausts its retries.</summary>
    public const string RetriesExhaustedErrorCode = "500";

    // ── fetch & lock ─────────────────────────────────────────────────────────

    public async Task<List<LockedExternalTaskDto>> FetchAndLock(FetchExternalTasksDto request, CancellationToken ct)
    {
        var workerId = RequireWorkerId(request.WorkerId);
        if (request.MaxTasks < 0)
            throw BadRequest("maxTasks must not be negative.");
        var topics = request.Topics ?? [];
        foreach (var topic in topics)
        {
            if (string.IsNullOrEmpty(topic.TopicName))
                throw BadRequest("topicName is required.");
            if (topic.LockDuration <= 0)
                throw BadRequest($"lockDuration of topic '{topic.TopicName}' must be greater than 0.");
            AddMilliseconds(timeProvider.GetUtcNow().UtcDateTime, topic.LockDuration, "lockDuration"); // validates the range
        }

        var newestFirst = NewestFirst(request.Sorting);

        var timeout = request.AsyncResponseTimeout is > 0
            ? TimeSpan.FromMilliseconds(Math.Min(request.AsyncResponseTimeout.Value, MaxAsyncResponseTimeout.TotalMilliseconds))
            : TimeSpan.Zero;
        var deadline = timeProvider.GetUtcNow() + timeout;

        while (true)
        {
            var wake = signal.Next; // before the query, so a task queued during it is not missed
            var locked = await TryFetchAndLock(workerId, request.MaxTasks, topics, request.UsePriority == true, newestFirst, ct);
            var remaining = deadline - timeProvider.GetUtcNow();
            if (locked.Count > 0 || remaining <= TimeSpan.Zero)
                return locked;

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(remaining < LongPollFallbackInterval ? remaining : LongPollFallbackInterval,
                timeProvider, delayCts.Token);
            await Task.WhenAny(wake, delay);
            delayCts.Cancel();
            ct.ThrowIfCancellationRequested();
        }
    }

    private async Task<List<LockedExternalTaskDto>> TryFetchAndLock(
        string workerId, int maxTasks, List<FetchExternalTaskTopicDto> topics, bool usePriority, bool newestFirst, CancellationToken ct)
    {
        var result = new List<LockedExternalTaskDto>();
        if (maxTasks == 0 || topics.Count == 0) return result;

        // Rows this call could not hand out (lost race, stale, failed) — stays small.
        var skipped = new List<string>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        try
        {
            while (result.Count < maxTasks)
            {
                var now = UtcNow();
                var want = maxTasks - result.Count;
                var candidates = new List<(ExternalTaskEntity Task, FetchExternalTaskTopicDto Topic)>();
                foreach (var topic in topics)
                {
                    var rows = await Order(Available(db, topic, now, skipped), usePriority, newestFirst)
                        .Take(want).ToListAsync(ct);
                    candidates.AddRange(rows.Select(r => (r, topic)));
                }
                if (candidates.Count == 0) break;

                // Same order across topics as within one.
                var byPriority = candidates.OrderByDescending(c => usePriority ? c.Task.Priority : 0);
                var batch = (newestFirst
                        ? byPriority.ThenByDescending(c => c.Task.CreateTime)
                        : byPriority.ThenBy(c => c.Task.CreateTime))
                    .DistinctBy(c => c.Task.Id)
                    .Take(want);
                foreach (var (candidate, topic) in batch)
                {
                    var locked = await TryClaim(db, candidate, topic, workerId, ct);
                    if (locked is null) skipped.Add(candidate.Id);
                    else result.Add(locked);
                }
            }
        }
        catch
        {
            // The worker will never see these (client gone, database error): hand them back now
            // instead of leaving them locked for the whole lockDuration.
            foreach (var locked in result)
                await TryRelease(locked.Id, workerId);
            throw;
        }

        return result;
    }

    /// <summary>
    /// Highest priority first with <c>usePriority</c>; then by creation time — oldest first unless
    /// the worker sorts by <c>createTime desc</c>.
    /// </summary>
    private static IOrderedQueryable<ExternalTaskEntity> Order(
        IQueryable<ExternalTaskEntity> tasks, bool usePriority, bool newestFirst)
    {
        if (usePriority)
        {
            var byPriority = tasks.OrderByDescending(t => t.Priority);
            return newestFirst ? byPriority.ThenByDescending(t => t.CreateTime) : byPriority.ThenBy(t => t.CreateTime);
        }
        return newestFirst ? tasks.OrderByDescending(t => t.CreateTime) : tasks.OrderBy(t => t.CreateTime);
    }

    /// <summary>Validates Camunda's <c>sorting</c>; true for <c>createTime desc</c>.</summary>
    private static bool NewestFirst(List<SortingDto>? sorting)
    {
        var newestFirst = false;
        foreach (var sort in sorting ?? [])
        {
            if (sort.SortBy != "createTime")
                throw BadRequest($"Cannot sort by '{sort.SortBy}': only 'createTime' is supported.");
            newestFirst = sort.SortOrder switch
            {
                "asc" or null => false,
                "desc" => true,
                _ => throw BadRequest($"sortOrder must be 'asc' or 'desc', not '{sort.SortOrder}'."),
            };
        }
        return newestFirst;
    }

    /// <summary>Fetchable rows of one topic, with the topic's process filters applied in SQL.</summary>
    private static IQueryable<ExternalTaskEntity> Available(
        ExternalTaskDbContext db, FetchExternalTaskTopicDto topic, DateTime now, List<string> skipped)
    {
        var q = db.ExternalTasks.AsNoTracking().Where(t =>
            t.TopicName == topic.TopicName
            && (t.Retries == null || t.Retries > 0)
            && (t.LockExpirationTime == null || t.LockExpirationTime <= now));
        if (skipped.Count > 0)
            q = q.Where(t => !skipped.Contains(t.Id));
        if (topic.ProcessDefinitionKey is { } key)
            q = q.Where(t => t.ProcessDefinitionKey == key);
        if (topic.ProcessDefinitionKeyIn is { Count: > 0 } keys)
            q = q.Where(t => keys.Contains(t.ProcessDefinitionKey));
        if (topic.ProcessDefinitionId is { } definitionId)
            q = q.Where(t => t.ProcessDefinitionId == definitionId);
        if (topic.ProcessDefinitionIdIn is { Count: > 0 } definitionIds)
            q = q.Where(t => t.ProcessDefinitionId != null && definitionIds.Contains(t.ProcessDefinitionId));
        if (topic.BusinessKey is { } businessKey)
            q = q.Where(t => t.BusinessKey == businessKey);
        // As in Camunda, tenantIdIn and withoutTenantId together match either.
        if (topic.TenantIdIn is { Count: > 0 } tenants)
            q = topic.WithoutTenantId
                ? q.Where(t => t.TenantId == null || tenants.Contains(t.TenantId))
                : q.Where(t => t.TenantId != null && tenants.Contains(t.TenantId));
        else if (topic.WithoutTenantId)
            q = q.Where(t => t.TenantId == null);
        return q;
    }

    /// <summary>
    /// Locks one row for <paramref name="workerId"/> and builds its response. Returns null when the
    /// row cannot be handed out; a failure here never aborts the rest of the fetch.
    /// </summary>
    private async Task<LockedExternalTaskDto?> TryClaim(
        ExternalTaskDbContext db, ExternalTaskEntity candidate, FetchExternalTaskTopicDto topic,
        string workerId, CancellationToken ct)
    {
        var claimNow = UtcNow(); // fresh per claim: earlier grain calls may have taken a while
        var expires = AddMilliseconds(claimNow, topic.LockDuration, "lockDuration");
        var claimed = await db.ExternalTasks
            .Where(t => t.Id == candidate.Id && (t.LockExpirationTime == null || t.LockExpirationTime <= claimNow))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.WorkerId, workerId)
                .SetProperty(t => t.LockExpirationTime, expires)
                .SetProperty(t => t.ReportingUntil, (DateTime?)null), ct);
        if (claimed == 0) return null; // another worker won the race

        var callback = grainFactory.GetGrain<IWorkflowInstanceCallback>(candidate.WorkflowInstanceId);
        try
        {
            var variables = await callback.GetVariablesIfActive(candidate.ActivityInstanceId, candidate.VariablesId);
            if (variables is null)
            {
                // The activity ended without a worker (cancelled, interrupted by a boundary event, …).
                await Delete(candidate.Id);
                LogStaleTaskRemoved(candidate.Id, candidate.TopicName);
                return null;
            }

            candidate.WorkerId = workerId;
            candidate.LockExpirationTime = expires;
            var locked = ToLockedDto(candidate, variables, topic); // throws on a value Camunda cannot carry
            LogLocked(candidate.Id, candidate.TopicName, workerId);
            return locked;
        }
        catch (OperationCanceledException)
        {
            await TryRelease(candidate.Id, workerId);
            throw;
        }
        catch (Exception ex) when (!IsTransient(ex) && ex is not DbException)
        {
            // Deterministic (unknown variable scope, unconvertible value): no retry can fix it —
            // fail the activity visibly instead of handing the task out again and again.
            LogClaimFailed(ex, candidate.Id, candidate.TopicName);
            try
            {
                await callback.FailActivity(candidate.ActivityId, candidate.ActivityInstanceId, ex);
                await Delete(candidate.Id);
            }
            catch (Exception failEx)
            {
                LogClaimFailed(failEx, candidate.Id, candidate.TopicName);
                await TryRelease(candidate.Id, workerId);
            }
            return null;
        }
        catch (Exception ex)
        {
            // Engine or database briefly unavailable: give the task back and serve the others.
            LogClaimFailed(ex, candidate.Id, candidate.TopicName);
            await TryRelease(candidate.Id, workerId);
            return null;
        }
    }

    private static LockedExternalTaskDto ToLockedDto(
        ExternalTaskEntity task, ExpandoObject processVariables, FetchExternalTaskTopicDto topic)
    {
        var all = (IDictionary<string, object?>)processVariables;
        var locals = ResolveInputs(task, all, topic.IncludeExtensionProperties, out var extensionProperties);

        var visible = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (topic.LocalVariables)
            foreach (var (name, value) in locals) visible[name] = value;
        else
        {
            // The scope holds every input, reserved ones included; those are task metadata.
            var reserved = InputTargets(task).Where(ReservedInputs.IsReserved).ToHashSet(StringComparer.Ordinal);
            foreach (var (name, value) in all)
                if (!reserved.Contains(name)) visible[name] = value;
        }

        var selected = topic.Variables is null
            ? visible
            : visible.Where(kv => topic.Variables.Contains(kv.Key));

        return new LockedExternalTaskDto(
            Id: task.Id,
            TopicName: task.TopicName,
            WorkerId: task.WorkerId!,
            LockExpirationTime: CamundaVariables.FormatDate(task.LockExpirationTime!.Value),
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
            ErrorDetails: task.ErrorDetails,
            TenantId: task.TenantId,
            Priority: task.Priority,
            BusinessKey: task.BusinessKey,
            Variables: CamundaVariables.ToCamundaVariables(selected),
            ExtensionProperties: extensionProperties);
    }

    /// <summary>
    /// The worker's local variables (the inputs) and, when asked for, the extension properties.
    /// The engine evaluated the inputs into the task's scope, so their values come from
    /// <paramref name="scope"/>. Other reserved inputs are task metadata read at queue time, not variables.
    /// </summary>
    private static Dictionary<string, object?> ResolveInputs(
        ExternalTaskEntity task, IDictionary<string, object?> scope, bool includeExtensionProperties,
        out Dictionary<string, string> extensionProperties)
    {
        var locals = new Dictionary<string, object?>(StringComparer.Ordinal);
        extensionProperties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var target in InputTargets(task))
        {
            scope.TryGetValue(target, out var value);
            if (target.StartsWith(ExtensionPropertyPrefix, StringComparison.Ordinal))
            {
                if (includeExtensionProperties && value is not null)
                    extensionProperties[target[ExtensionPropertyPrefix.Length..]] = ReservedInputs.ToText(value) ?? "";
                continue;
            }
            if (!ReservedInputs.IsReserved(target))
                locals[target] = value;
        }
        return locals;
    }

    private static List<string> InputTargets(ExternalTaskEntity task) => Deserialize<string>(task.InputTargetsJson);

    // ── file variables ──────────────────────────────────────────────────────

    public sealed record BinaryContent(byte[] Content, string? MimeType, string? Filename);

    /// <summary>
    /// Content of a <c>File</c> or <c>Bytes</c> variable the task sees, for Camunda clients that
    /// download file variables lazily. <paramref name="executionId"/> is the task id.
    /// </summary>
    public async Task<BinaryContent> GetBinaryVariable(string executionId, string name)
    {
        var task = await Load(executionId);
        var variables = await Callback(task).GetVariablesIfActive(task.ActivityInstanceId, task.VariablesId)
            ?? throw NotFound(executionId);
        var all = (IDictionary<string, object?>)variables;
        var locals = ResolveInputs(task, all, includeExtensionProperties: false, out _);
        if (!locals.TryGetValue(name, out var value) && !all.TryGetValue(name, out value))
            throw new ExternalTaskApiException(404, "InvalidRequestException",
                $"Variable '{name}' does not exist for execution {executionId}");
        if (!CamundaVariables.TryGetBinary(value, out var binary))
            throw BadRequest($"Variable '{name}' is not a File or Bytes variable.");
        return new BinaryContent(Convert.FromBase64String(binary.Base64), binary.MimeType, binary.Filename);
    }

    // ── report back ──────────────────────────────────────────────────────────

    public async Task Complete(string id, CompleteExternalTaskDto request)
    {
        var variables = ToFleans(request.Variables);
        var localVariables = ToFleans(request.LocalVariables);
        var task = await ClaimForReport(id, RequireWorkerId(request.WorkerId), "completed");

        await Report(task, failActivityOnRejection: true, () =>
            Callback(task).CompleteActivity(task.ActivityId, task.ActivityInstanceId,
                ToResult(variables, localVariables)));
        LogCompleted(id, task.TopicName, task.WorkerId);
    }

    /// <summary>
    /// The task's result: <c>variables</c> overlaid with <c>localVariables</c>. The engine applies
    /// the output mappings to it; without any, all of it reaches the workflow scope.
    /// </summary>
    private static ExpandoObject ToResult(
        Dictionary<string, object?> variables, Dictionary<string, object?> localVariables)
    {
        var result = new ExpandoObject();
        var resultDict = (IDictionary<string, object?>)result;
        foreach (var (name, value) in variables) resultDict[name] = value;
        foreach (var (name, value) in localVariables) resultDict[name] = value;
        return result;
    }

    public async Task HandleFailure(string id, ExternalTaskFailureDto request)
    {
        if (request.Retries is not { } retries || retries < 0)
            throw BadRequest("retries must be a number greater than or equal to 0.");
        if (request.RetryTimeout < 0)
            throw BadRequest("retryTimeout must not be negative.");
        var worker = RequireWorkerId(request.WorkerId);

        if (retries == 0)
        {
            var task = await ClaimForReport(id, worker, "failed");
            if (options.Value.Incidents)
                await CreateIncident(task, request.ErrorMessage, request.ErrorDetails);
            else
                await FailRetriesExhausted(task, request.ErrorMessage, request.ErrorDetails);
            return;
        }

        var now = UtcNow();
        var retryAt = AddMilliseconds(now, request.RetryTimeout, "retryTimeout");
        await using var db = await dbFactory.CreateDbContextAsync();
        var updated = await db.ExternalTasks
            .Where(t => t.Id == id && t.WorkerId == worker && (t.ReportingUntil == null || t.ReportingUntil <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Retries, retries)
                .SetProperty(t => t.ErrorMessage, request.ErrorMessage)
                .SetProperty(t => t.ErrorDetails, request.ErrorDetails)
                .SetProperty(t => t.WorkerId, (string?)null)
                .SetProperty(t => t.LockExpirationTime, retryAt));
        if (updated == 0)
            throw await Rejected(id, worker, "failed");
        LogFailureRetry(id, retries, request.RetryTimeout);
        if (request.RetryTimeout == 0)
            signal.Pulse();
    }

    public async Task HandleBpmnError(string id, ExternalTaskBpmnErrorDto request)
    {
        if (string.IsNullOrEmpty(request.ErrorCode))
            throw BadRequest("errorCode is required.");
        var variables = new ExpandoObject();
        foreach (var (name, value) in ToFleans(request.Variables))
            ((IDictionary<string, object?>)variables)[name] = value;
        var task = await ClaimForReport(id, RequireWorkerId(request.WorkerId), "reported a BPMN error for");

        // The variables land in the activity's scope before the error propagates, so the
        // catching error boundary (or error event sub-process) sees them, as in Camunda.
        // failActivityOnRejection: if the engine refuses the variables outright, a retry would be
        // refused the same way — fail the activity instead of looping on 500s.
        await Report(task, failActivityOnRejection: true, () =>
            Callback(task).FailActivity(task.ActivityId, task.ActivityInstanceId,
                new CustomTaskFailedActivityException(request.ErrorCode, request.ErrorMessage ?? request.ErrorCode),
                variables));
        LogBpmnError(id, task.TopicName, request.ErrorCode);
    }

    // ── lock management ──────────────────────────────────────────────────────

    public async Task ExtendLock(string id, ExtendLockDto request)
    {
        var workerId = RequireWorkerId(request.WorkerId);
        if (request.NewDuration <= 0)
            throw BadRequest("newDuration must be greater than 0.");
        await Load(id);
        var now = UtcNow();
        var expires = AddMilliseconds(now, request.NewDuration, "newDuration");
        await using var db = await dbFactory.CreateDbContextAsync();
        var updated = await db.ExternalTasks
            .Where(t => t.Id == id && t.WorkerId == workerId && t.LockExpirationTime > now
                && (t.ReportingUntil == null || t.ReportingUntil <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LockExpirationTime, expires));
        if (updated == 0)
            throw BadRequest($"The lock of the External Task {id} cannot be extended by worker '{workerId}'. " +
                             "It is not locked by this worker, the lock has expired, or its result is being reported.");
    }

    public async Task Lock(string id, LockExternalTaskDto request)
    {
        var workerId = RequireWorkerId(request.WorkerId);
        if (request.LockDuration <= 0)
            throw BadRequest("lockDuration must be greater than 0.");
        await Load(id);
        var now = UtcNow();
        var expires = AddMilliseconds(now, request.LockDuration, "lockDuration");
        await using var db = await dbFactory.CreateDbContextAsync();
        // Free (never locked, lock expired, retry backoff over) or re-locked by its own holder.
        // A task waiting out a retryTimeout has no worker but a future expiry — not free.
        // An incident (retries 0) waits for an operator, so it cannot be locked either.
        var updated = await db.ExternalTasks
            .Where(t => t.Id == id && (t.Retries == null || t.Retries > 0)
                && (t.LockExpirationTime == null || t.LockExpirationTime <= now
                    || (t.WorkerId == workerId && (t.ReportingUntil == null || t.ReportingUntil <= now))))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.WorkerId, workerId)
                .SetProperty(t => t.LockExpirationTime, expires)
                .SetProperty(t => t.ReportingUntil, (DateTime?)null));
        if (updated == 0)
            throw BadRequest($"External Task {id} is already locked, waiting for a retry, an incident, or its result is being reported.");
    }

    public async Task Unlock(string id)
    {
        await Load(id);
        var now = UtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();
        var updated = await db.ExternalTasks
            .Where(t => t.Id == id && (t.ReportingUntil == null || t.ReportingUntil <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.WorkerId, (string?)null)
                .SetProperty(t => t.LockExpirationTime, (DateTime?)null)
                .SetProperty(t => t.ReportingUntil, (DateTime?)null));
        if (updated == 0)
            throw await Rejected(id, null, "unlocked");
        signal.Pulse();
    }

    /// <summary>
    /// Operator override of the priority evaluated from the BPMN. Takes effect on the next fetch;
    /// a worker already holding the task keeps it.
    /// </summary>
    public async Task SetPriority(string id, PriorityDto request)
    {
        var priority = request.Priority;
        await using var db = await dbFactory.CreateDbContextAsync();
        var updated = await db.ExternalTasks.Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Priority, priority));
        if (updated == 0)
            throw NotFound(id);
        LogPriorityChanged(id, priority);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task FailRetriesExhausted(ExternalTaskEntity task, string? errorMessage, string? errorDetails)
    {
        var message = errorMessage ?? "External task failed with no retries left";
        if (!string.IsNullOrEmpty(errorDetails))
            message += Environment.NewLine + errorDetails;
        await Report(task, failActivityOnRejection: false, () =>
            Callback(task).FailActivity(task.ActivityId, task.ActivityInstanceId,
                new CustomTaskFailedActivityException(RetriesExhaustedErrorCode, message)));
        LogFailedNoRetries(task.Id, task.TopicName);
    }

    /// <summary>
    /// Marks the task as "result being reported" (<see cref="ExternalTaskEntity.ReportingUntil"/>)
    /// and pushes its lock out, atomically and only if <paramref name="workerId"/> holds it and no
    /// other report is in flight. Then checks the activity is still waiting:
    /// if it ended meanwhile (cancelled, interrupted by a boundary event), the task is gone — 404,
    /// as in Camunda — instead of a success the engine would silently ignore.
    /// </summary>
    /// <param name="workerId">The worker that must hold the lock; null for an operator, who may act on any task.</param>
    private async Task<ExternalTaskEntity> ClaimForReport(string id, string? workerId, string action)
    {
        var now = UtcNow();
        var until = now + ReportLockDuration;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var claimable = db.ExternalTasks.Where(t => t.Id == id && (t.ReportingUntil == null || t.ReportingUntil <= now));
            if (workerId is not null)
                claimable = claimable.Where(t => t.WorkerId == workerId);
            var updated = await claimable
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.ReportingUntil, until)
                    .SetProperty(t => t.LockExpirationTime, until));
            if (updated == 0)
                throw await Rejected(id, workerId, action);
        }

        var task = await Load(id);
        ExpandoObject? active;
        try
        {
            active = await Callback(task).GetVariablesIfActive(task.ActivityInstanceId, task.VariablesId);
        }
        catch
        {
            await EndReport(id);
            throw;
        }
        if (active is null)
        {
            await Delete(id);
            LogStaleTaskRemoved(id, task.TopicName);
            throw NotFound(id);
        }
        return task;
    }

    /// <summary>
    /// Writes a claimed report into the workflow and deletes the task. A transient engine failure
    /// releases the report so the worker can send it again. When the engine rejects a completion
    /// outright (e.g. outputs it cannot store), the activity is failed — as
    /// <c>CustomTaskHandlerBase</c> does — instead of handing the task to another worker later.
    /// </summary>
    private async Task Report(ExternalTaskEntity task, bool failActivityOnRejection, Func<Task> writeToWorkflow)
    {
        try
        {
            await writeToWorkflow();
        }
        catch (Exception ex) when (failActivityOnRejection && !IsTransient(ex) && ex is not OperationCanceledException)
        {
            LogReportRejected(ex, task.Id, task.TopicName);
            try
            {
                await Callback(task).FailActivity(task.ActivityId, task.ActivityInstanceId, ex);
            }
            catch
            {
                await EndReport(task.Id);
                throw;
            }
            await Delete(task.Id);
            throw BadRequest($"The workflow rejected the result of External Task {task.Id}: {ex.Message} " +
                             "The activity was failed.");
        }
        catch
        {
            await EndReport(task.Id);
            throw;
        }
        await Delete(task.Id);
    }

    /// <summary>Clears the report marker; the task stays locked by its worker, who may report again.</summary>
    private async Task EndReport(string id)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.ExternalTasks.Where(t => t.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ReportingUntil, (DateTime?)null));
        }
        catch (Exception ex)
        {
            LogReleaseFailed(ex, id); // the marker expires on its own after ReportLockDuration
        }
    }

    /// <summary>Best-effort unlock of a task this call locked for <paramref name="workerId"/>.</summary>
    private async Task TryRelease(string id, string workerId)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.ExternalTasks.Where(t => t.Id == id && t.WorkerId == workerId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.WorkerId, (string?)null)
                    .SetProperty(t => t.LockExpirationTime, (DateTime?)null));
            signal.Pulse();
        }
        catch (Exception ex)
        {
            LogReleaseFailed(ex, id); // the lock expires on its own
        }
    }

    /// <summary>Why a conditional update on task <paramref name="id"/> changed nothing.</summary>
    private async Task<ExternalTaskApiException> Rejected(string id, string? workerId, string action)
    {
        var task = await Load(id);
        if (task.ReportingUntil > UtcNow())
            return new ExternalTaskApiException(500, "BadUserRequestException",
                $"External Task {id} cannot be {action}: its result is being reported right now.");
        return new ExternalTaskApiException(500, "BadUserRequestException",
            $"External Task {id} cannot be {action} by worker '{workerId}'. " +
            $"It is locked by worker '{task.WorkerId}'.");
    }

    private async Task<ExternalTaskEntity> Load(string id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ExternalTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id)
            ?? throw NotFound(id);
    }

    private static ExternalTaskApiException NotFound(string id) =>
        new(404, "InvalidRequestException", $"External task with id {id} does not exist");

    private async Task Delete(string id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.ExternalTasks.Where(t => t.Id == id).ExecuteDeleteAsync();
    }

    private IWorkflowInstanceCallback Callback(ExternalTaskEntity task) =>
        grainFactory.GetGrain<IWorkflowInstanceCallback>(task.WorkflowInstanceId);

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private static DateTime AddMilliseconds(DateTime now, long milliseconds, string name) =>
        milliseconds <= (DateTime.MaxValue - now).TotalMilliseconds
            ? now.AddMilliseconds(milliseconds)
            : throw BadRequest($"{name} is too large.");

    /// <summary>Same split as <c>CustomTaskHandlerBase</c>: infrastructure failures a retry can fix.</summary>
    private static bool IsTransient(Exception ex) => ex is TimeoutException or OrleansException;

    private static Dictionary<string, object?> ToFleans(Dictionary<string, VariableValueDto>? variables)
    {
        try { return CamundaVariables.ToFleans(variables); }
        catch (Exception ex) when (ex is CamundaVariables.UnsupportedVariableException or FormatException
                                       or OverflowException or InvalidOperationException or JsonException)
        {
            throw BadRequest(ex.Message);
        }
    }

    private static List<T> Deserialize<T>(string json) => JsonSerializer.Deserialize<List<T>>(json) ?? [];

    private static string RequireWorkerId(string? workerId) =>
        string.IsNullOrEmpty(workerId) ? throw BadRequest("workerId is required.") : workerId;

    private static ExternalTaskApiException BadRequest(string message) =>
        new(400, "InvalidRequestException", message);

    [LoggerMessage(EventId = 9360, Level = LogLevel.Information,
        Message = "External task {ExternalTaskId} (topic '{TopicName}') locked by worker '{WorkerId}'")]
    private partial void LogLocked(string externalTaskId, string topicName, string workerId);

    [LoggerMessage(EventId = 9361, Level = LogLevel.Information,
        Message = "External task {ExternalTaskId} (topic '{TopicName}') completed by worker '{WorkerId}'")]
    private partial void LogCompleted(string externalTaskId, string topicName, string? workerId);

    [LoggerMessage(EventId = 9362, Level = LogLevel.Warning,
        Message = "External task {ExternalTaskId} failed; {Retries} retries left, next attempt in {RetryTimeoutMs} ms")]
    private partial void LogFailureRetry(string externalTaskId, int retries, long retryTimeoutMs);

    [LoggerMessage(EventId = 9363, Level = LogLevel.Error,
        Message = "External task {ExternalTaskId} (topic '{TopicName}') has no retries left — failing the activity")]
    private partial void LogFailedNoRetries(string externalTaskId, string topicName);

    [LoggerMessage(EventId = 9364, Level = LogLevel.Information,
        Message = "External task {ExternalTaskId} (topic '{TopicName}') reported BPMN error '{ErrorCode}'")]
    private partial void LogBpmnError(string externalTaskId, string topicName, string errorCode);

    [LoggerMessage(EventId = 9365, Level = LogLevel.Information,
        Message = "External task {ExternalTaskId} (topic '{TopicName}') removed — its activity is no longer active")]
    private partial void LogStaleTaskRemoved(string externalTaskId, string topicName);

    [LoggerMessage(EventId = 9367, Level = LogLevel.Error,
        Message = "External task {ExternalTaskId} (topic '{TopicName}') could not be handed out")]
    private partial void LogClaimFailed(Exception ex, string externalTaskId, string topicName);

    [LoggerMessage(EventId = 9368, Level = LogLevel.Error,
        Message = "The workflow rejected the result of external task {ExternalTaskId} (topic '{TopicName}') — failing the activity")]
    private partial void LogReportRejected(Exception ex, string externalTaskId, string topicName);

    [LoggerMessage(EventId = 9369, Level = LogLevel.Warning,
        Message = "Releasing external task {ExternalTaskId} failed; its lock expires on its own")]
    private partial void LogReleaseFailed(Exception ex, string externalTaskId);

    [LoggerMessage(EventId = 9357, Level = LogLevel.Information,
        Message = "External task {ExternalTaskId} priority set to {Priority}")]
    private partial void LogPriorityChanged(string externalTaskId, long priority);

}
