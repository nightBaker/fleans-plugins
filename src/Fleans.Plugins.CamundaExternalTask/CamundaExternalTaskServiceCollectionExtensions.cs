using System.Data.Common;
using Fleans.ExternalTasks.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Fleans.Plugins.CamundaExternalTask;

public static class CamundaExternalTaskServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Camunda external-task plugin in a worker process (next to
    /// <c>AddFleansExternalTaskWorker</c>, whose engine URL and access token it shares): the handler
    /// of <c>camunda:*</c>, the external-task table and the service behind
    /// <see cref="CamundaExternalTaskEndpoints.MapCamundaExternalTaskApi"/>. An
    /// <see cref="IFleansEngineClient"/> registered before this call replaces the HTTP one.
    /// </summary>
    /// <param name="configureDb">Provider for the external-task table, e.g. <c>o =&gt; o.UseNpgsql(cs)</c>.</param>
    /// <param name="configure">Behaviour switches, e.g. <c>o =&gt; o.Incidents = true</c>.</param>
    public static IServiceCollection AddCamundaExternalTasks(
        this IServiceCollection services, Action<DbContextOptionsBuilder> configureDb,
        Action<CamundaExternalTaskOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configureDb);
        var options = services.AddOptions<CamundaExternalTaskOptions>();
        if (configure is not null)
            options.Configure(configure);
        services.AddDbContextFactory<ExternalTaskDbContext>(configureDb);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ExternalTaskSignal>();
        services.AddExternalTaskHandler<CamundaExternalTaskHandler>(CamundaExternalTaskConventions.TaskTypeWildcard);
        services.AddHttpClient(HttpFleansEngineClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.TryAddSingleton<IFleansEngineClient, HttpFleansEngineClient>();
        services.TryAddSingleton<ExternalTaskService>();
        services.AddHostedService<ExternalTaskSchemaInitializer>();
        return services;
    }
}

/// <summary>
/// Creates the <c>camunda_external_tasks</c> table and its indexes at startup if they do not exist
/// yet. Works on a dedicated database and on one shared with other tables (where
/// <c>EnsureCreated</c> would be a no-op), and is safe when several replicas start at once.
/// Relies on <c>CREATE TABLE/INDEX IF NOT EXISTS</c> (SQLite, PostgreSQL).
/// </summary>
internal sealed class ExternalTaskSchemaInitializer(IDbContextFactory<ExternalTaskDbContext> dbFactory) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Two replicas creating the same object at once can still collide inside the database
        // (e.g. PostgreSQL's pg_type unique index), even with IF NOT EXISTS — the loser retries,
        // and by then the object exists.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await CreateSchema(cancellationToken);
                return;
            }
            catch (Exception) when (attempt < MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }
    }

    private const int MaxAttempts = 3;

    private async Task CreateSchema(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(cancellationToken))
            await creator.CreateAsync(cancellationToken);

        var script = db.Database.GenerateCreateScript()
            .Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal)
            .Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ", StringComparison.Ordinal);
        await db.Database.ExecuteSqlRawAsync(script, cancellationToken);
        await AddMissingColumns(db, cancellationToken);
    }

    /// <summary>Columns added after 0.6.0, the first release, in the order they were added.</summary>
    private static readonly string[] AddedColumns =
    [
        nameof(ExternalTaskEntity.Priority),
        nameof(ExternalTaskEntity.BusinessKey),
        nameof(ExternalTaskEntity.TenantId),
    ];

    /// <summary>
    /// <c>CREATE TABLE IF NOT EXISTS</c> leaves an older table as it is, so columns added since
    /// 0.6.0 are added here. A replica that loses the race to add one fails, retries, and finds it.
    /// </summary>
    private static async Task AddMissingColumns(ExternalTaskDbContext db, CancellationToken cancellationToken)
    {
        var entity = db.Model.FindEntityType(typeof(ExternalTaskEntity))!;
        var table = StoreObjectIdentifier.Table(ExternalTaskDbContext.TableName, entity.GetSchema());
        var sql = db.GetService<ISqlGenerationHelper>();
        var tableName = sql.DelimitIdentifier(ExternalTaskDbContext.TableName, entity.GetSchema());
        foreach (var name in AddedColumns)
        {
            var property = entity.FindProperty(name)!;
            var column = sql.DelimitIdentifier(property.GetColumnName(table)!);
            try
            {
                // Qualified: SQLite reads an unknown bare "name" as a string literal, not an error.
                await db.Database.ExecuteSqlRawAsync($"SELECT t.{column} FROM {tableName} t WHERE 1 = 0", cancellationToken);
                continue;
            }
            catch (DbException)
            {
                // Column missing — add it below.
            }

            var nullability = property.IsNullable ? "NULL" : "NOT NULL DEFAULT 0";
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE {tableName} ADD COLUMN {column} {property.GetColumnType()} {nullability}",
                cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
