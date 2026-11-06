using System.Dynamic;
using System.Text.Json;
using Fleans.ExternalTasks.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleans.Plugins.CamundaExternalTask;

/// <summary>
/// Turns every <c>&lt;serviceTask type="camunda:&lt;topic&gt;"&gt;</c> the engine starts into a
/// row in <see cref="ExternalTaskDbContext"/>, where Camunda 7 external-task workers pick it up
/// through <see cref="CamundaExternalTaskEndpoints"/>. It does not settle the activity: it returns
/// <see cref="ExternalTaskHandler.Deferred"/>, and the REST API reports the outcome when a Camunda
/// worker does.
///
/// Topics are open-ended, so it is registered for the wildcard <c>camunda:*</c>, which the
/// custom-task catalog does not list.
/// </summary>
public sealed partial class CamundaExternalTaskHandler(
    IDbContextFactory<ExternalTaskDbContext> dbFactory,
    ExternalTaskSignal signal,
    TimeProvider timeProvider,
    ILogger<CamundaExternalTaskHandler> logger)
    : ExternalTaskHandler
{
    public override async Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        ExternalTaskContext context,
        CancellationToken cancellationToken)
    {
        var topic = CamundaExternalTaskConventions.TryGetTopic(context.TaskType)
            ?? throw new ExternalTaskFailedException("400", $"Task type '{context.TaskType}' names no Camunda topic.");

        var id = context.ActivityInstanceId.ToString("D");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // At-least-once delivery: a redelivery finds the row (and its priority) already there.
        if (await db.ExternalTasks.AnyAsync(t => t.Id == id, cancellationToken))
            return Deferred;

        // The engine already evaluated the input mappings; reserved ones carry task metadata. An
        // invalid one (priority not a number, key too long) is deterministic: the exception they
        // throw fails the activity.
        var priority = ExternalTaskPriority.Evaluate(resolvedInputs);
        // The reserved input, when mapped, replaces the instance's business id (e.g. a call activity's child, which has none).
        var businessKey = ReservedInputs.EvaluateBusinessKey(resolvedInputs, context.BusinessId);
        var tenantId = ReservedInputs.EvaluateString(resolvedInputs, ReservedInputs.TenantId);

        db.ExternalTasks.Add(new ExternalTaskEntity
        {
            Id = id,
            TopicName = topic,
            WorkflowInstanceId = context.WorkflowInstanceId,
            ProcessDefinitionKey = context.WorkflowId,
            ProcessDefinitionId = context.ProcessDefinitionId,
            ActivityId = context.ActivityId,
            ActivityInstanceId = context.ActivityInstanceId,
            InputTargetsJson = JsonSerializer.Serialize(resolvedInputs.Keys.ToList()),
            Priority = priority,
            BusinessKey = businessKey,
            TenantId = tenantId,
            CreateTime = timeProvider.GetUtcNow().UtcDateTime,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await using var check = await dbFactory.CreateDbContextAsync(cancellationToken);
            if (await check.ExternalTasks.AnyAsync(t => t.Id == id, cancellationToken))
                return Deferred; // a concurrent duplicate delivery inserted it first
            throw;
        }

        LogQueued(topic, context.ActivityId, id);
        signal.Pulse(); // wake long-polling fetchAndLock requests in this process
        return Deferred;
    }

    [LoggerMessage(EventId = 9350, Level = LogLevel.Information,
        Message = "Queued Camunda external task {ExternalTaskId} on topic '{TopicName}' for activity {ActivityId}")]
    private partial void LogQueued(string topicName, string activityId, string externalTaskId);
}
