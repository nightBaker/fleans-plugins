using System.Dynamic;
using System.Text.Json;
using Fleans.Application.Grains;
using Fleans.Domain.Errors;
using Fleans.Domain.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Streams.Core;

namespace Fleans.Plugins.CamundaExternalTask;

/// <summary>
/// Turns every <c>&lt;serviceTask type="camunda:&lt;topic&gt;"&gt;</c> the engine starts into a
/// row in <see cref="ExternalTaskDbContext"/>, where Camunda 7 external-task workers pick it up
/// through <see cref="CamundaExternalTaskEndpoints"/>. Unlike <c>CustomTaskHandlerBase</c> it does
/// not complete the activity — the REST API does that when a worker reports back.
///
/// Topics are open-ended, so the grain subscribes by regex to every
/// <c>events.ExecuteCustomTaskEvent.camunda:*</c> namespace instead of one literal namespace, and
/// resumes each implicit subscription through <see cref="IStreamSubscriptionObserver"/>. It is
/// therefore not registered through <c>AddCustomTaskPlugin</c> (which requires a literal
/// namespace) and does not appear in the custom-task catalog.
///
/// Default placement, like other plugins: the grain only activates on silos that load this assembly.
/// </summary>
[RegexImplicitStreamSubscription(CamundaExternalTaskConventions.StreamNamespacePattern)]
public sealed partial class CamundaExternalTaskHandler(
    IDbContextFactory<ExternalTaskDbContext> dbFactory,
    ExternalTaskSignal signal,
    TimeProvider timeProvider,
    ILogger<CamundaExternalTaskHandler> logger)
    : Grain, IGrainWithStringKey, IAsyncObserver<ExecuteCustomTaskEvent>, IStreamSubscriptionObserver
{
    public async Task OnSubscribed(IStreamSubscriptionHandleFactory handleFactory)
    {
        var handle = handleFactory.Create<ExecuteCustomTaskEvent>();
        await handle.ResumeAsync(this);
    }

    public async Task OnNextAsync(ExecuteCustomTaskEvent item, StreamSequenceToken? token = null)
    {
        var topic = CamundaExternalTaskConventions.TryGetTopic(item.TaskType);
        if (topic is null)
        {
            LogNotCamundaTaskType(item.TaskType);
            return;
        }

        // At-least-once stream: skip a redelivery that arrives after the task already finished.
        // Same error split as CustomTaskHandlerBase: transient failures propagate so the stream
        // provider redelivers; a deterministic one (e.g. unknown variable scope) fails the activity.
        var callback = GrainFactory.GetGrain<IWorkflowInstanceCallback>(item.WorkflowInstanceId);
        ExpandoObject? variables;
        try
        {
            variables = await callback.GetVariablesIfActive(item.ActivityInstanceId, item.VariablesId);
        }
        catch (Exception ex) when (ex is not (TimeoutException or OrleansException))
        {
            LogActiveCheckFailed(ex, item.ActivityId);
            await callback.FailActivity(item.ActivityId, item.ActivityInstanceId, ex);
            return;
        }

        if (variables is null)
        {
            LogSkippedInactive(item.ActivityId, item.ActivityInstanceId);
            return;
        }

        var id = item.ActivityInstanceId.ToString("D");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.ExternalTasks.AnyAsync(t => t.Id == id))
            return; // redelivered while still waiting — the row (and its priority) is already there

        // The engine already evaluated the input mappings; reserved ones carry task metadata.
        var inputs = (IDictionary<string, object?>)item.Inputs;
        long priority;
        string? businessKey, tenantId;
        try
        {
            priority = ExternalTaskPriority.Evaluate(inputs);
            // The reserved input, when mapped, replaces the instance's business id (e.g. a call activity's child, which has none).
            businessKey = ReservedInputs.EvaluateBusinessKey(inputs, item.BusinessId);
            tenantId = ReservedInputs.EvaluateString(inputs, ReservedInputs.TenantId);
        }
        catch (CustomTaskFailedActivityException ex)
        {
            // Deterministic (priority not a number, key too long, broken path): a redelivery would fail the same way.
            LogReservedInputInvalid(ex, item.ActivityId);
            await callback.FailActivity(item.ActivityId, item.ActivityInstanceId, ex);
            return;
        }

        db.ExternalTasks.Add(new ExternalTaskEntity
        {
            Id = id,
            TopicName = topic,
            WorkflowInstanceId = item.WorkflowInstanceId,
            ProcessDefinitionKey = item.WorkflowId,
            ProcessDefinitionId = item.ProcessDefinitionId,
            ActivityId = item.ActivityId,
            ActivityInstanceId = item.ActivityInstanceId,
            VariablesId = item.VariablesId,
            InputTargetsJson = JsonSerializer.Serialize(inputs.Keys.ToList()),
            Priority = priority,
            BusinessKey = businessKey,
            TenantId = tenantId,
            CreateTime = timeProvider.GetUtcNow().UtcDateTime,
        });
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            await using var check = await dbFactory.CreateDbContextAsync();
            if (await check.ExternalTasks.AnyAsync(t => t.Id == id))
                return; // a concurrent duplicate delivery inserted it first
            throw;
        }

        LogQueued(topic, item.ActivityId, id);
        signal.Pulse(); // wake long-polling fetchAndLock requests in this process
    }

    public Task OnCompletedAsync() => Task.CompletedTask;

    public Task OnErrorAsync(Exception ex)
    {
        LogStreamError(ex);
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 9350, Level = LogLevel.Information,
        Message = "Queued Camunda external task {ExternalTaskId} on topic '{TopicName}' for activity {ActivityId}")]
    private partial void LogQueued(string topicName, string activityId, string externalTaskId);

    [LoggerMessage(EventId = 9351, Level = LogLevel.Information,
        Message = "Camunda external-task event for activity {ActivityId} (instance {ActivityInstanceId}) skipped — activity is no longer active")]
    private partial void LogSkippedInactive(string activityId, Guid activityInstanceId);

    [LoggerMessage(EventId = 9352, Level = LogLevel.Warning,
        Message = "Camunda external-task handler received task type '{TaskType}' without the 'camunda:' prefix — dropping")]
    private partial void LogNotCamundaTaskType(string taskType);

    [LoggerMessage(EventId = 9354, Level = LogLevel.Error,
        Message = "Camunda external-task event for activity {ActivityId} could not be checked — failing the activity")]
    private partial void LogActiveCheckFailed(Exception ex, string activityId);

    [LoggerMessage(EventId = 9356, Level = LogLevel.Error,
        Message = "Task priority, business key or tenant id of Camunda external-task activity {ActivityId} is invalid — failing the activity")]
    private partial void LogReservedInputInvalid(Exception ex, string activityId);

    [LoggerMessage(EventId = 9353, Level = LogLevel.Error,
        Message = "Camunda external-task event stream error")]
    private partial void LogStreamError(Exception ex);
}
