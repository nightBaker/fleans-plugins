using System.Globalization;
using Fleans.Domain.Errors;

namespace Fleans.Plugins.CamundaExternalTask;

/// <summary>
/// Task priority, Camunda's <c>camunda:taskPriority</c>: the reserved input mapping
/// <c>&lt;zeebe:input source="=10" target="taskPriority"/&gt;</c>. The engine evaluates the source;
/// its value is read once when the task is queued. Without the mapping the priority is 0, as in Camunda.
/// </summary>
public static class ExternalTaskPriority
{
    /// <summary>Input-mapping target that sets the priority instead of a worker variable.</summary>
    public const string InputTarget = "taskPriority";

    /// <summary>
    /// The priority from the activity's resolved inputs. Throws
    /// <see cref="CustomTaskFailedActivityException"/> (code <c>400</c>) when it is not a whole
    /// number (or a string of digits) — no retry can fix that, so the caller fails the activity.
    /// </summary>
    public static long Evaluate(IDictionary<string, object?> inputs)
    {
        if (!inputs.TryGetValue(InputTarget, out var value)) return 0;

        return value switch
        {
            long l => l,
            int or short or sbyte or byte or ushort or uint => Convert.ToInt64(value),
            ulong u when u <= long.MaxValue => (long)u,
            double d when IsWhole(d) => (long)d,
            float f when IsWhole(f) => (long)f,
            decimal m when m == decimal.Truncate(m) && m is >= long.MinValue and <= long.MaxValue => (long)m,
            // A bare BPMN literal (source="10") resolves to a string.
            string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            var other => throw new CustomTaskFailedActivityException("400",
                $"Task priority evaluated to '{other ?? "null"}', which is not a whole number."),
        };
    }

    private static bool IsWhole(double d) =>
        double.IsFinite(d) && d == Math.Truncate(d) && d >= long.MinValue && d < long.MaxValue;
}
