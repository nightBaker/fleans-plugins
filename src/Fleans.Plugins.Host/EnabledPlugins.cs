namespace Fleans.Plugins.Host;

/// <summary>
/// Which plugins the host runs: <c>Plugins:Enabled</c>, a comma-separated list of plugin names
/// (<c>rest-caller</c>, <c>camunda-external-task</c>, <c>javascript</c>). Unset or empty runs them
/// all. An unknown name fails startup, so a typo doesn't silently start a worker with no plugins.
/// </summary>
public sealed class EnabledPlugins
{
    public static readonly IReadOnlyList<string> Known = ["rest-caller", "camunda-external-task", "javascript"];

    private readonly HashSet<string>? _names;

    private EnabledPlugins(HashSet<string>? names) => _names = names;

    public static EnabledPlugins From(IConfiguration configuration)
    {
        var names = (configuration["Plugins:Enabled"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0)
            return new EnabledPlugins(null);

        var unknown = names.Where(n => !Known.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                $"Plugins:Enabled names unknown plugin(s) {string.Join(", ", unknown)}; known: {string.Join(", ", Known)}.");
        return new EnabledPlugins(names);
    }

    public bool Has(string name) => _names is null || _names.Contains(name);
}
