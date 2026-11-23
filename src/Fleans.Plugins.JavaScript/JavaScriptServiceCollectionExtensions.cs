using Fleans.ExternalTasks.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace Fleans.Plugins.JavaScript;

/// <summary>
/// Wires the JavaScript plugin into a worker process (next to <c>AddFleansExternalTaskWorker</c>):
/// the shared <see cref="JavaScriptScriptEngine"/> (its parsed-script cache lives for the process)
/// and the <c>fleans.script.javascript</c> handler, which receives every workflow variable.
/// </summary>
public static class JavaScriptServiceCollectionExtensions
{
    // The package version (VersionPrefix in Directory.Build.props), shown in the catalog.
    private static readonly string PluginVersion =
        typeof(JavaScriptScriptHandler).Assembly.GetName().Version!.ToString(3);

    public static IServiceCollection AddJavaScriptPlugin(this IServiceCollection services)
    {
        services.AddSingleton<JavaScriptScriptEngine>();
        services.AddExternalTaskHandler<JavaScriptScriptHandler>(
            taskType: JavaScriptScriptHandler.TaskType,
            displayName: "JavaScript script tasks",
            category: "Scripts",
            variables: ExternalTaskVariables.All,
            version: PluginVersion);
        return services;
    }
}
