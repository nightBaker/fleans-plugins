using Fleans.Domain.Errors;

namespace Fleans.Plugins.CamundaExternalTask.Tests;

/// <summary>The engine evaluates the <c>taskPriority</c> input; these cover how its value becomes a priority.</summary>
[TestClass]
public class ExternalTaskPriorityTests
{
    [TestMethod]
    public void Without_the_input_the_priority_is_zero()
    {
        var priority = ExternalTaskPriority.Evaluate(Inputs(("toCharge", 42L)));

        Assert.AreEqual(0L, priority);
    }

    [TestMethod]
    public void A_bare_bpmn_literal_arrives_as_a_string_of_digits()
    {
        Assert.AreEqual(10L, ExternalTaskPriority.Evaluate(Priority("10")));
        Assert.AreEqual(-5L, ExternalTaskPriority.Evaluate(Priority("-5")));
    }

    [TestMethod]
    public void Whole_numbers_of_any_numeric_type_are_accepted()
    {
        foreach (var value in new object[] { 3, 3L, (short)3, (byte)3, (sbyte)3, (ushort)3, 3u, 3UL, 3.0f, 3.0, 3m })
            Assert.AreEqual(3L, ExternalTaskPriority.Evaluate(Priority(value)), value.GetType().Name);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("high")]
    [DataRow(2.5)]
    [DataRow(true)]
    public void Priority_that_is_not_a_whole_number_fails_with_code_400(object? value)
    {
        var ex = Assert.ThrowsExactly<CustomTaskFailedActivityException>(() =>
            ExternalTaskPriority.Evaluate(Priority(value)));

        Assert.AreEqual("400", ex.GetActivityErrorState().Code);
    }

    private static Dictionary<string, object?> Priority(object? value) => Inputs((ExternalTaskPriority.InputTarget, value));

    private static Dictionary<string, object?> Inputs(params (string Target, object? Value)[] inputs) =>
        inputs.ToDictionary(i => i.Target, i => i.Value);
}
