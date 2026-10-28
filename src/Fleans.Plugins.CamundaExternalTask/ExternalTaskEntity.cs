namespace Fleans.Plugins.CamundaExternalTask;

/// <summary>
/// One row per waiting Camunda-style external task. Written by
/// <see cref="CamundaExternalTaskHandler"/> when the engine publishes the custom-task event,
/// locked by <c>fetchAndLock</c>, and deleted once the task is completed or failed back into
/// the workflow. <see cref="Id"/> is the activity instance id, so a redelivered stream event
/// maps onto the same row.
/// </summary>
public sealed class ExternalTaskEntity
{
    public required string Id { get; set; }
    public required string TopicName { get; set; }
    public required Guid WorkflowInstanceId { get; set; }
    public required string ProcessDefinitionKey { get; set; }
    public string? ProcessDefinitionId { get; set; }
    public required string ActivityId { get; set; }
    public required Guid ActivityInstanceId { get; set; }
    public required Guid VariablesId { get; set; }

    /// <summary>
    /// JSON array of the activity's input targets. The engine evaluates the inputs into the
    /// activity's own scope (<see cref="VariablesId"/>), so their values are read from there on fetch.
    /// </summary>
    public required string InputTargetsJson { get; set; }

    /// <summary>Higher is fetched first when a worker asks for <c>usePriority</c>. See <see cref="ExternalTaskPriority"/>.</summary>
    public long Priority { get; set; }

    /// <summary>
    /// The instance's business id, or the reserved <c>businessKey</c> input when mapped; set when
    /// the task is queued. See <see cref="ReservedInputs.EvaluateBusinessKey"/>.
    /// </summary>
    public string? BusinessKey { get; set; }

    /// <summary>From the reserved <c>tenantId</c> input, evaluated when the task is queued.</summary>
    public string? TenantId { get; set; }

    public string? WorkerId { get; set; }

    /// <summary>UTC. Fetchable when null or in the past.</summary>
    public DateTime? LockExpirationTime { get; set; }

    /// <summary>
    /// UTC. Set while a worker's result (complete / failure / BPMN error) or an operator's
    /// <c>retries=0</c> is being written into the workflow; every other state change waits until
    /// it is cleared or has passed, so a report cannot race an unlock, a lock or a second report.
    /// </summary>
    public DateTime? ReportingUntil { get; set; }

    /// <summary>Null until a worker reports a failure (Camunda semantics).</summary>
    public int? Retries { get; set; }

    public string? ErrorMessage { get; set; }
    public string? ErrorDetails { get; set; }

    /// <summary>UTC.</summary>
    public DateTime CreateTime { get; set; }
}
