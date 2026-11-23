using System.Dynamic;
using Fleans.ExternalTasks.Sdk;
using Microsoft.Extensions.Logging;

namespace Fleans.Plugins.JavaScript;

/// <summary>
/// The worker for <c>fleans.script.javascript</c>: every <c>&lt;scriptTask scriptFormat="javascript"&gt;</c>
/// (or <c>js</c>). The engine sends the script text as the reserved input <c>script</c> and every
/// visible variable; the handler returns only the variables the script changed. A script that throws
/// — or runs past a limit — fails the task with code <c>500</c> and the error message (the SDK does
/// that for any exception).
/// </summary>
public sealed partial class JavaScriptScriptHandler(JavaScriptScriptEngine engine, ILogger<JavaScriptScriptHandler> logger)
    : ExternalTaskHandler
{
    /// <summary>The task type the engine sends JavaScript script tasks as.</summary>
    public const string TaskType = "fleans.script.javascript";

    /// <summary>The reserved input that carries the script text.</summary>
    public const string ScriptInput = "script";

    public override async Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        ExternalTaskContext context,
        CancellationToken cancellationToken)
    {
        var script = resolvedInputs.TryGetValue(ScriptInput, out var value) ? value as string : null;
        LogRunning(context.ActivityId, context.ActivityInstanceId, script?.Length ?? 0);

        // Jint runs synchronously: keep it off the worker's receive loop. Its own limits end the run.
        var changed = await Task.Run(() => engine.Run(script ?? string.Empty, variables, cancellationToken), cancellationToken);

        LogCompleted(context.ActivityId, context.ActivityInstanceId, changed.Count);
        return changed;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Running the JavaScript of script task {ActivityId} ({ActivityInstanceId}), {Length} characters")]
    private partial void LogRunning(string activityId, Guid activityInstanceId, int length);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "JavaScript of script task {ActivityId} ({ActivityInstanceId}) completed, {ChangedCount} variables changed")]
    private partial void LogCompleted(string activityId, Guid activityInstanceId, int changedCount);
}
