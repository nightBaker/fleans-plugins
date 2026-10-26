using Fleans.Plugins.CamundaExternalTask;
using Fleans.Plugins.RestCaller;
using Fleans.Streaming.Kafka;
using Fleans.Worker.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Streams;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;

// The plugin host for every plugin in this repo. Joins the Fleans engine's Orleans cluster as a
// Plugin-role silo (same contract as github.com/nightBaker/fleans-custom-worker-example), so
// Orleans places the plugins' stream-handler grains only here.
//
// Membership, reminders and streams use the engine's providers, read from the same keys the
// engine reads — Fleans:Clustering:Provider (Redis | Postgres), Fleans:Reminders:Provider
// (Redis | Postgres), Fleans:Streaming:Provider (Redis | Kafka | Memory) — with the engine's
// Orleans__ClusterId / Orleans__ServiceId. A mismatch forms a separate cluster or listens on
// queues the engine never writes to, so set them exactly as on the engine silos.

var builder = WebApplication.CreateBuilder(args);
builder.Configuration["Fleans:Role"] ??= "Plugin";

const string StreamProviderName = "StreamProvider";
static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

var clusteringProvider = builder.Configuration["Fleans:Clustering:Provider"] ?? "Redis";
var remindersProvider = builder.Configuration["Fleans:Reminders:Provider"] ?? "Redis";
var streamingProvider = builder.Configuration["Fleans:Streaming:Provider"] ?? "Redis";
// Under Aspire the AppHost injects Orleans:Clustering:* and that wins over Fleans:Clustering:Provider.
var aspireClustering = builder.Configuration.GetSection("Orleans:Clustering");

foreach (var (key, value, allowed) in new[]
{
    ("Fleans:Clustering:Provider", clusteringProvider, new[] { "Redis", "Postgres" }),
    ("Fleans:Reminders:Provider", remindersProvider, new[] { "Redis", "Postgres" }),
    ("Fleans:Streaming:Provider", streamingProvider, new[] { "Redis", "Kafka", "Memory" }),
})
{
    if (!allowed.Any(a => Is(value, a)))
        throw new InvalidOperationException(
            $"{key}='{value}' is not supported by this host. Use one of: {string.Join(", ", allowed)} — the same value as the engine.");
}

// The orleans-redis client only when a Redis-backed provider is configured, like the engine.
var usesRedis = Is(streamingProvider, "Redis") || Is(remindersProvider, "Redis")
    || (aspireClustering.Exists() ? Is(aspireClustering["ProviderType"], "Redis") : Is(clusteringProvider, "Redis"));
if (usesRedis)
    builder.AddKeyedRedisClient("orleans-redis");
string OrleansRedis() => builder.Configuration.GetConnectionString("orleans-redis")
    ?? throw new InvalidOperationException("ConnectionStrings:orleans-redis is required while a provider is Redis.");
string PostgresFor(string dedicated, string key) => builder.Configuration.GetConnectionString(dedicated)
    ?? builder.Configuration.GetConnectionString("fleans")
    ?? throw new InvalidOperationException(
        $"{key}=Postgres needs ConnectionStrings:{dedicated} or ConnectionStrings:fleans — the engine's database.");

if (Is(streamingProvider, "Redis"))
{
    // The Redis stream provider resolves a non-keyed multiplexer; alias the keyed one.
    builder.Services.TryAddSingleton<IConnectionMultiplexer>(sp =>
        sp.GetRequiredKeyedService<IConnectionMultiplexer>("orleans-redis"));

    // Must match the engine's stream settings — a different TotalQueueCount hashes stream ids onto
    // different queues than the engine silos publish to. Same keys and defaults as the engine.
    builder.Services.AddOptions<HashRingStreamQueueMapperOptions>(StreamProviderName)
        .Configure(o => o.TotalQueueCount = builder.Configuration.GetValue("Fleans:Streaming:Redis:TotalQueueCount", 8));
    builder.Services.AddOptions<SimpleQueueCacheOptions>(StreamProviderName);
    builder.Services.AddOptions<RedisStreamReceiverOptions>(StreamProviderName)
        .Configure(o =>
        {
            o.MaxStreamLength = builder.Configuration.GetValue("Fleans:Streaming:Redis:MaxStreamLength", 1000);
            o.TrimTimeMinutes = builder.Configuration.GetValue("Fleans:Streaming:Redis:TrimTimeMinutes", 5);
        });
}

builder.UseOrleans(silo =>
{
    silo.AddFleansPluginHost(builder.Configuration);

    if (!aspireClustering.Exists())
    {
        if (Is(clusteringProvider, "Postgres"))
            silo.UseAdoNetClustering(o =>
            {
                o.Invariant = "Npgsql";
                o.ConnectionString = PostgresFor("fleans-cluster", "Fleans:Clustering:Provider");
            });
        else
            silo.UseRedisClustering(OrleansRedis());
    }

    // Every silo serves a slice of the reminder ring, including engine reminders (BPMN timers);
    // use the engine's reminder table so those keep their durability.
    if (Is(remindersProvider, "Postgres"))
        silo.UseAdoNetReminderService(o =>
        {
            o.Invariant = "Npgsql";
            o.ConnectionString = PostgresFor("fleans-reminders", "Fleans:Reminders:Provider");
        });
    else
        silo.UseRedisReminderService(o => o.ConfigurationOptions = ConfigurationOptions.Parse(OrleansRedis()));
    silo.Configure<ReminderOptions>(builder.Configuration.GetSection("Fleans:Reminders"));

    // The engine subscribes through implicit subscriptions only (no PubSubStore), and every silo
    // of a cluster must use the same pub-sub type.
    if (Is(streamingProvider, "Kafka"))
        silo.AddKafkaStreams(StreamProviderName, builder.Configuration.GetSection("Fleans:Streaming:Kafka"));
    else if (Is(streamingProvider, "Memory"))
        silo.AddMemoryStreams(StreamProviderName, b => b.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly));
    else
        silo.AddPersistentStreams(StreamProviderName, RedisStreamFactory.Create,
            b => b.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly));
});

// Plugins — one line each; each reads its own configuration (see its README).
builder.Services.AddRestCallerPlugin();
builder.AddCamundaExternalTaskPlugin();

builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health");
app.Run();
