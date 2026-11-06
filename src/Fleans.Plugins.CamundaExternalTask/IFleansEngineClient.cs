using System.Dynamic;

namespace Fleans.Plugins.CamundaExternalTask;

/// <summary>
/// What this plugin needs from the engine once a task is deferred: is it still waiting (and with
/// which variables), and its outcome. Throws <see cref="EngineUnavailableException"/> or
/// <see cref="TimeoutException"/> when a retry can help, and anything else when it can't.
/// </summary>
public interface IFleansEngineClient
{
    /// <summary>The variables the activity instance sees; <c>null</c> once it is no longer active.</summary>
    Task<ExpandoObject?> GetActiveActivityVariables(
        Guid workflowInstanceId, Guid activityInstanceId, CancellationToken cancellationToken = default);

    Task CompleteActivity(
        Guid workflowInstanceId, string activityId, Guid activityInstanceId, ExpandoObject variables,
        CancellationToken cancellationToken = default);

    /// <summary>Fails the activity with a BPMN error <paramref name="code"/>; <paramref name="variables"/> go to the catching scope.</summary>
    Task FailActivity(
        Guid workflowInstanceId, string activityId, Guid activityInstanceId, string code, string message,
        ExpandoObject? variables = null, CancellationToken cancellationToken = default);
}

/// <summary>The engine could not answer (unreachable, timing out, overloaded): the call can be repeated.</summary>
public sealed class EngineUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>The engine refused the call: repeating it gets the same answer.</summary>
public sealed class EngineRejectedException(string message) : Exception(message);
