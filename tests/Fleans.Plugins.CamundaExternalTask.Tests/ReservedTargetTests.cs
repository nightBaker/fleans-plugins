using System.Text.RegularExpressions;

namespace Fleans.Plugins.CamundaExternalTask.Tests;

[TestClass]
public class ReservedTargetTests
{
    /// <summary>The engine's rule for input targets (CustomTaskRoutingResolver), checked at BPMN deploy time.</summary>
    private static readonly Regex EngineTarget = new("^[a-zA-Z_][a-zA-Z0-9_]*$");

    [TestMethod]
    public void Reserved_targets_pass_the_engine_deploy_time_validation()
    {
        Assert.MatchesRegex(EngineTarget, ExternalTaskPriority.InputTarget);
        Assert.MatchesRegex(EngineTarget, ReservedInputs.BusinessKey);
        Assert.MatchesRegex(EngineTarget, ReservedInputs.TenantId);
        Assert.MatchesRegex(EngineTarget, ExternalTaskService.ExtensionPropertyPrefix + "region");
    }
}

[TestClass]
public class ReservedInputsTests
{
    [TestMethod]
    public void A_business_key_longer_than_the_column_fails_with_code_400()
    {
        var inputs = new Dictionary<string, object?> { [ReservedInputs.BusinessKey] = new string('x', ReservedInputs.MaxLength + 1) };

        var ex = Assert.ThrowsExactly<Fleans.Domain.Errors.CustomTaskFailedActivityException>(() =>
            ReservedInputs.EvaluateString(inputs, ReservedInputs.BusinessKey));

        Assert.AreEqual("400", ex.GetActivityErrorState().Code);
    }

    [TestMethod]
    public void ToText_writes_values_the_way_camunda_workers_read_them()
    {
        Assert.AreEqual("true", ReservedInputs.ToText(true));
        Assert.AreEqual("1.5", ReservedInputs.ToText(1.5));
        Assert.AreEqual("[1,2]", ReservedInputs.ToText(new List<object?> { 1L, 2L }));
        Assert.IsNull(ReservedInputs.ToText(null));
    }
}
