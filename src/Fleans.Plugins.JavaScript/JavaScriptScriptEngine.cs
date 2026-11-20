using System.Collections.Concurrent;
using System.Dynamic;
using Acornima.Ast;
using Jint;
using Jint.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using JsJsonParser = Jint.Native.Json.JsonParser;
using JsJsonSerializer = Jint.Native.Json.JsonSerializer;

namespace Fleans.Plugins.JavaScript;

/// <summary>
/// Runs a script task's ECMAScript on Jint, an interpreter with no .NET access (nightBaker/fleans#1105).
/// The variables reach the script as the plain object <c>_context</c>, a JSON copy. The result holds
/// only the top-level keys whose JSON the script changed, so the engine keeps the value and type of
/// every variable the script did not touch (a Guid, a decimal, a long above 2^53). Written values are
/// plain JSON: a string stays a string, even one that looks like a date. Deleting a key does not
/// remove the variable: results merge into the scope.
///
/// Each run gets a fresh engine (no shared globals) limited to <see cref="DefaultTimeout"/>,
/// <see cref="MemoryLimitBytes"/> of allocations and <see cref="RecursionLimit"/> nested calls; Jint
/// stops a run that exceeds them. Parsed scripts are cached by their text.
/// </summary>
public sealed class JavaScriptScriptEngine
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    public const long MemoryLimitBytes = 64 * 1024 * 1024;
    public const int RecursionLimit = 256;

    private static readonly JsonSerializerSettings VariableJson = new()
    {
        Converters = { new ExpandoObjectConverter() },
        // NaN / Infinity as strings: Jint's JSON parser rejects the bare literals.
        FloatFormatHandling = FloatFormatHandling.String,
        DateParseHandling = DateParseHandling.None,
    };

    private readonly TimeSpan _timeout;

    // Parsed scripts, keyed by script text. A prepared script is immutable and shared by the per-run
    // engines. Unbounded: scripts come from deployed process definitions, not per-instance data.
    private readonly ConcurrentDictionary<string, Prepared<Script>> _scriptCache = new();

    public JavaScriptScriptEngine()
        : this(DefaultTimeout)
    {
    }

    public JavaScriptScriptEngine(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    internal int CachedScriptCount => _scriptCache.Count;

    /// <summary>
    /// Runs <paramref name="script"/> against <paramref name="variables"/> and returns the top-level
    /// variables it changed. Throws what the script throws (<see cref="JavaScriptException"/>), a
    /// <see cref="TimeoutException"/>, a <see cref="MemoryLimitExceededException"/> or a
    /// <see cref="RecursionDepthOverflowException"/> past the limits, and an
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> fires.
    /// </summary>
    public IDictionary<string, object?> Run(string script, ExpandoObject variables, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(script))
            return new Dictionary<string, object?>();

        var prepared = _scriptCache.GetOrAdd(script, static text => Engine.PrepareScript(text));

        // An Engine is not thread-safe, and a fresh one per run keeps runs from seeing each other's globals.
        using var engine = new Engine(options => options
            .TimeoutInterval(_timeout)
            .LimitMemory(MemoryLimitBytes)
            .LimitRecursion(RecursionLimit)
            .CancellationToken(cancellationToken));
        var serializer = new JsJsonSerializer(engine);

        var context = new JsJsonParser(engine).Parse(JsonConvert.SerializeObject(variables, VariableJson));
        // The input as JavaScript sees it: comparing against this, not the .NET JSON, keeps a value
        // that only changed by crossing into JavaScript (a long above 2^53) from looking written.
        var before = ParseObject(serializer.Serialize(context).AsString());

        engine.SetValue("_context", context);
        try
        {
            engine.Execute(prepared);
        }
        catch (ExecutionCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The worker SDK treats cancellation as "deliver again", not as a failure.
            throw new OperationCanceledException(cancellationToken);
        }

        var result = serializer.Serialize(engine.GetValue("_context"));
        if (!result.IsString() || !result.AsString().TrimStart().StartsWith('{'))
            throw new InvalidOperationException("A JavaScript script must leave _context an object.");

        var json = result.AsString();
        var after = ParseObject(json);
        var written = (IDictionary<string, object?>)JsonConvert.DeserializeObject<ExpandoObject>(json, VariableJson)!;
        var changed = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in after)
        {
            if (!before.TryGetValue(key, out var old) || !JToken.DeepEquals(old, value))
                changed[key] = written[key];
        }
        return changed;
    }

    /// <summary>Parses <paramref name="script"/> into the cache; throws on a syntax error.</summary>
    public void Compile(string script)
    {
        if (!string.IsNullOrWhiteSpace(script))
            _scriptCache.GetOrAdd(script, static text => Engine.PrepareScript(text));
    }

    private static JObject ParseObject(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }
}
