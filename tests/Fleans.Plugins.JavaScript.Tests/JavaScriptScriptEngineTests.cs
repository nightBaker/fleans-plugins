using System.Dynamic;
using Jint.Runtime;

namespace Fleans.Plugins.JavaScript.Tests;

[TestClass]
public class JavaScriptScriptEngineTests
{
    private JavaScriptScriptEngine _engine = null!;

    [TestInitialize]
    public void Setup() => _engine = new JavaScriptScriptEngine();

    private static ExpandoObject Vars(params (string Key, object? Value)[] values)
    {
        var vars = new ExpandoObject();
        foreach (var (key, value) in values)
            ((IDictionary<string, object?>)vars)[key] = value;
        return vars;
    }

    [TestMethod]
    public void Run_ReadsTheContextAndReturnsOnlyWhatTheScriptChanged()
    {
        // Arrange
        var variables = Vars(("amount", 21L), ("name", "Ada"));

        // Act
        var changed = _engine.Run(
            "_context.total = _context.amount * 2; _context.greeting = `Hi ${_context.name}`;", variables);

        // Assert — the untouched variables are not sent back, so the engine keeps their type
        Assert.HasCount(2, changed);
        Assert.AreEqual(42L, changed["total"]);
        Assert.AreEqual("Hi Ada", changed["greeting"]);
    }

    [TestMethod]
    public void Run_ReassigningTheSameValue_IsNotAChange()
    {
        // Act
        var changed = _engine.Run("_context.x = 1; _context.y = 2;", Vars(("x", 1L)));

        // Assert
        CollectionAssert.AreEqual(new[] { "y" }, changed.Keys.ToList());
    }

    [TestMethod]
    public void Run_SupportsFunctionsLoopsAndNestedValues()
    {
        // Arrange
        var variables = Vars(("items", new List<object> { 1L, 2L, 3L }));

        // Act
        var changed = _engine.Run("""
            function square(x) { return x * x; }
            let sum = 0;
            for (const item of _context.items) { sum += square(item); }
            _context.order = { sum: sum, tags: ['a', 'b'], ratio: 0.5 };
            delete _context.items;
            """, variables);

        // Assert — a deleted key is not reported: results merge into the scope
        Assert.IsFalse(changed.ContainsKey("items"));
        var order = (IDictionary<string, object?>)changed["order"]!;
        Assert.AreEqual(14L, order["sum"]);
        Assert.AreEqual(0.5, order["ratio"]);
        CollectionAssert.AreEqual(new List<object?> { "a", "b" }, (List<object?>)order["tags"]!);
    }

    [TestMethod]
    public void Run_ANestedChange_ReturnsTheWholeTopLevelValue()
    {
        // Arrange
        var variables = Vars(("order", Vars(("id", "o-1"), ("total", 10L))));

        // Act
        var changed = _engine.Run("_context.order.total += 5;", variables);

        // Assert
        var order = (IDictionary<string, object?>)changed["order"]!;
        Assert.AreEqual("o-1", order["id"]);
        Assert.AreEqual(15L, order["total"]);
    }

    [TestMethod]
    public void Run_UntouchedValuesJsonCannotCarryExactly_AreNotReported()
    {
        // Arrange — a long above 2^53 changes on its way into JavaScript; untouched, it must not look written
        var variables = Vars(("externalId", 9007199254740993L), ("amount", 19.99m), ("code", "2024-01-01T00:00:00"));

        // Act
        var changed = _engine.Run("_context.seen = true;", variables);

        // Assert
        CollectionAssert.AreEqual(new[] { "seen" }, changed.Keys.ToList());
    }

    [TestMethod]
    public void Run_AWrittenDateLikeString_StaysAString()
    {
        // Act
        var changed = _engine.Run(
            "_context.day = '1990-05-01'; _context.stamp = new Date(Date.UTC(2026, 0, 2)).toJSON();", Vars());

        // Assert
        Assert.AreEqual("1990-05-01", changed["day"]);
        Assert.AreEqual("2026-01-02T00:00:00.000Z", changed["stamp"]);
    }

    [TestMethod]
    public void Run_ANonFiniteVariableInScope_DoesNotBreakTheScript()
    {
        // Act
        var changed = _engine.Run("_context.ok = 1;", Vars(("ratio", double.PositiveInfinity)));

        // Assert
        Assert.AreEqual(1L, changed["ok"]);
        Assert.IsFalse(changed.ContainsKey("ratio"));
    }

    [TestMethod]
    [DataRow("_context = new Date();")]
    [DataRow("_context = new String('x');")]
    [DataRow("_context = [1, 2];")]
    [DataRow("_context = undefined;")]
    [DataRow("_context = 5;")]
    public void Run_ContextNotAPlainObject_Throws(string script)
    {
        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => _engine.Run(script, Vars()));
        StringAssert.Contains(ex.Message, "must leave _context an object");
    }

    [TestMethod]
    public void Run_ReplacingTheContextObject_ReturnsItsKeys()
    {
        // Act
        var changed = _engine.Run("_context = { only: 'this' };", Vars(("x", 1L)));

        // Assert
        Assert.HasCount(1, changed);
        Assert.AreEqual("this", changed["only"]);
    }

    [TestMethod]
    public void Run_AThrownError_SurfacesItsMessage()
    {
        // Act & Assert
        var ex = Assert.Throws<JavaScriptException>(() => _engine.Run("throw new Error('bad input');", Vars()));
        StringAssert.Contains(ex.Message, "bad input");
    }

    [TestMethod]
    public void Run_AnInfiniteLoop_IsStoppedByTheTimeout()
    {
        // Arrange
        var engine = new JavaScriptScriptEngine(TimeSpan.FromMilliseconds(100));

        // Act & Assert — Jint ends the run itself, so the thread is not left spinning
        Assert.Throws<TimeoutException>(() => engine.Run("while (true) {}", Vars()));
    }

    [TestMethod]
    public void Run_AllocatingPastTheMemoryLimit_IsStopped()
    {
        // Act & Assert
        Assert.Throws<MemoryLimitExceededException>(() => _engine.Run(
            "const a = []; while (true) { a.push('x'.repeat(1024 * 1024)); }", Vars()));
    }

    [TestMethod]
    public void Run_RecursingPastTheLimit_IsStopped()
    {
        // Act & Assert
        Assert.Throws<RecursionDepthOverflowException>(() => _engine.Run(
            "function f(n) { return f(n + 1); } f(0);", Vars()));
    }

    [TestMethod]
    public void Run_Cancelled_ThrowsOperationCanceled()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act & Assert — the SDK delivers a cancelled task again instead of failing it
        Assert.Throws<OperationCanceledException>(() => _engine.Run("while (true) {}", Vars(), cts.Token));
    }

    [TestMethod]
    public void Run_HasNoDotNetAccess()
    {
        // Act & Assert — no CLR interop is enabled, so .NET types are not reachable
        Assert.Throws<JavaScriptException>(
            () => _engine.Run("_context.x = System.IO.File.ReadAllText('/etc/passwd');", Vars()));
    }

    [TestMethod]
    public void Run_RunsDoNotShareGlobals()
    {
        // Arrange
        _engine.Run("globalThis.leaked = 1;", Vars());

        // Act
        var changed = _engine.Run("_context.leaked = typeof leaked;", Vars());

        // Assert
        Assert.AreEqual("undefined", changed["leaked"]);
    }

    [TestMethod]
    public void Run_TheSameScriptTwice_ParsesOnce()
    {
        // Arrange
        const string script = "_context.y = _context.x + 1;";

        // Act
        _engine.Run(script, Vars(("x", 1L)));
        var changed = _engine.Run(script, Vars(("x", 10L)));

        // Assert
        Assert.AreEqual(1, _engine.CachedScriptCount);
        Assert.AreEqual(11L, changed["y"]);
    }

    [TestMethod]
    public void Run_AnEmptyScript_ChangesNothing()
    {
        // Act
        var changed = _engine.Run("  ", Vars(("x", 1L)));

        // Assert
        Assert.IsEmpty(changed);
        Assert.AreEqual(0, _engine.CachedScriptCount);
    }

    [TestMethod]
    public void Compile_ASyntaxError_ThrowsAndIsNotCached()
    {
        // Act & Assert
        Assert.Throws<Exception>(() => _engine.Compile("_context.y = ;"));
        Assert.AreEqual(0, _engine.CachedScriptCount);
    }
}
