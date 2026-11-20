using System.Dynamic;
using Fleans.ExternalTasks.Sdk;
using Fleans.Plugins.Host;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace Fleans.Plugins.JavaScript.Tests;

[TestClass]
public class JavaScriptScriptHandlerTests
{
    private static readonly ExternalTaskContext Context = new(
        Guid.NewGuid(), "javascript-variables", "javascript-variables:1:abc", Guid.NewGuid(), "compute",
        JavaScriptScriptHandler.TaskType);

    private static JavaScriptScriptHandler Handler() =>
        new(new JavaScriptScriptEngine(), NullLogger<JavaScriptScriptHandler>.Instance);

    // What the SDK hands a handler: inputs_json and variables_json parsed by Newtonsoft.
    private static ExpandoObject FromJson(string json) => JsonConvert.DeserializeObject<ExpandoObject>(json)!;

    [TestMethod]
    public async Task The_manual_test_fixture_script_completes_with_its_changed_variables()
    {
        // Arrange — the script of tests/manual/02-script-tasks/javascript-variables.bpmn (fleans repo)
        var inputs = new Dictionary<string, object?>
        {
            ["script"] = """
                function square(n) { return n * n; }
                let total = 0;
                for (const n of [1, 2, 3]) { total += square(n); }
                _context.x = _context.x + total;
                _context.greeting = `hello ${_context.x}`;
                """,
        };

        // Act
        var result = await Handler().ExecuteAsync(inputs, FromJson("""{"x":10,"untouched":"keep"}"""), Context, CancellationToken.None);

        // Assert
        Assert.HasCount(2, result);
        Assert.AreEqual(24L, result["x"]);
        Assert.AreEqual("hello 24", result["greeting"]);
        Assert.AreEqual("""{"x":24,"greeting":"hello 24"}""", JsonConvert.SerializeObject(result));
    }

    [TestMethod]
    public async Task A_throwing_script_fails_with_its_message()
    {
        // Arrange — the SDK fails a task with code 500 and the message of any exception
        var inputs = new Dictionary<string, object?> { ["script"] = "throw new Error('amount must be positive');" };

        // Act
        var ex = await Assert.ThrowsAsync<Jint.Runtime.JavaScriptException>(
            () => Handler().ExecuteAsync(inputs, FromJson("{}"), Context, CancellationToken.None));

        // Assert
        StringAssert.Contains(ex.Message, "amount must be positive");
    }

    [TestMethod]
    public async Task A_task_without_a_script_changes_nothing()
    {
        // Act
        var result = await Handler().ExecuteAsync(new Dictionary<string, object?>(), FromJson("""{"x":1}"""), Context, CancellationToken.None);

        // Assert
        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void The_plugin_registers_its_engine_and_handler()
    {
        // Arrange
        var services = new ServiceCollection().AddLogging();

        // Act
        services.AddJavaScriptPlugin();
        using var provider = services.BuildServiceProvider();

        // Assert — one engine for the process, so the parsed-script cache is shared
        Assert.AreSame(provider.GetRequiredService<JavaScriptScriptEngine>(), provider.GetRequiredService<JavaScriptScriptEngine>());
        using var scope = provider.CreateScope();
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<JavaScriptScriptHandler>());
    }

    [TestMethod]
    public void Host_runs_every_plugin_unless_Plugins_Enabled_lists_some()
    {
        // Act
        var all = EnabledPlugins.From(Config(null));
        var jsOnly = EnabledPlugins.From(Config(" JavaScript "));

        // Assert
        Assert.IsTrue(all.Has("rest-caller") && all.Has("camunda-external-task") && all.Has("javascript"));
        Assert.IsTrue(jsOnly.Has("javascript"));
        Assert.IsFalse(jsOnly.Has("camunda-external-task"));
        Assert.IsFalse(jsOnly.Has("rest-caller"));
    }

    [TestMethod]
    public void Host_fails_fast_on_an_unknown_plugin_name()
    {
        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => EnabledPlugins.From(Config("javascript,pyhton")));
        StringAssert.Contains(ex.Message, "pyhton");
    }

    private static IConfiguration Config(string? enabled) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Plugins:Enabled"] = enabled }).Build();
}
