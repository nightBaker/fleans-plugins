using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fleans.Plugins.CamundaExternalTask;

public static class CamundaExternalTaskPluginExtensions
{
    /// <summary>
    /// Adds the whole Camunda external-task plugin to a plugin host in one call, configured from
    /// <paramref name="builder"/>'s configuration:
    /// <list type="bullet">
    /// <item><c>ConnectionStrings:camunda-external-tasks</c> (required) and
    /// <c>CamundaExternalTasks:Provider</c> = <c>Postgres</c> (default) | <c>Sqlite</c> — the external-task table;</item>
    /// <item><c>CamundaExternalTasks:Incidents</c> — see <see cref="CamundaExternalTaskOptions.Incidents"/>;</item>
    /// <item><c>CamundaExternalTasks:BasicAuth:Username</c> / <c>:Password</c> — both or neither; anonymous when unset.</item>
    /// </list>
    /// The <c>/engine-rest</c> API is mapped when the host's pipeline is built. Hosts that need more
    /// control use <see cref="CamundaExternalTaskServiceCollectionExtensions.AddCamundaExternalTasks"/>
    /// and <see cref="CamundaExternalTaskEndpoints.MapCamundaExternalTaskApi"/> directly.
    /// </summary>
    public static IHostApplicationBuilder AddCamundaExternalTaskPlugin(this IHostApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var connectionString = configuration.GetConnectionString("camunda-external-tasks")
            ?? throw new InvalidOperationException("ConnectionStrings:camunda-external-tasks is required.");
        var provider = configuration["CamundaExternalTasks:Provider"] ?? "Postgres";
        Action<DbContextOptionsBuilder> configureDb =
            provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase) ? o => o.UseSqlite(connectionString)
            : provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase) ? o => o.UseNpgsql(connectionString)
            : throw new InvalidOperationException(
                $"CamundaExternalTasks:Provider must be 'Postgres' or 'Sqlite' (got '{provider}').");

        var username = configuration["CamundaExternalTasks:BasicAuth:Username"];
        var password = configuration["CamundaExternalTasks:BasicAuth:Password"];
        if (string.IsNullOrEmpty(username) != string.IsNullOrEmpty(password))
            throw new InvalidOperationException(
                "Set both CamundaExternalTasks:BasicAuth:Username and CamundaExternalTasks:BasicAuth:Password, or neither.");

        builder.Services.AddCamundaExternalTasks(configureDb,
            o => o.Incidents = configuration.GetValue("CamundaExternalTasks:Incidents", false));
        builder.Services.AddSingleton<IStartupFilter>(new ApiStartupFilter(username, password));
        return builder;
    }

    // Maps the API after the host's own pipeline, so a request the host doesn't handle falls
    // through to it.
    private sealed class ApiStartupFilter(string? username, string? password) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                var api = endpoints.MapCamundaExternalTaskApi();
                if (!string.IsNullOrEmpty(username))
                    api.RequireBasicAuthentication(username, password!);
            });
        };
    }
}
