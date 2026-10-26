using Fleans.Plugins.RestCaller;
using Fleans.Worker.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;

// Plugin host for Fleans.Plugins.RestCaller. Joins the Fleans engine's Orleans cluster as a
// Plugin-role silo (same contract as github.com/nightBaker/fleans-custom-worker-example), so
// Orleans places the rest-call handler grain only here.
//
// Joins the cluster through the engine's Redis (ConnectionStrings:orleans-redis) with the engine's
// Orleans__ClusterId / Orleans__ServiceId.

var builder = WebApplication.CreateBuilder(args);
builder.Configuration["Fleans:Role"] ??= "Plugin";

builder.AddKeyedRedisClient("orleans-redis");

// Only the Redis stream provider (the engine's default) is wired. Fail fast rather than
// silently listening on a provider the engine isn't publishing to.
var streamingProvider = builder.Configuration["Fleans:Streaming:Provider"] ?? "Redis";
if (!streamingProvider.Equals("Redis", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException(
        $"This host supports only the Redis stream provider (Fleans:Streaming:Provider='{streamingProvider}').");

// The Redis stream provider resolves a non-keyed multiplexer; alias the keyed one.
builder.Services.TryAddSingleton<IConnectionMultiplexer>(sp =>
    sp.GetRequiredKeyedService<IConnectionMultiplexer>("orleans-redis"));

// Must match the engine's stream settings — a different TotalQueueCount hashes stream ids onto
// different queues than the engine silos publish to. Same key and default as the engine, so the
// engine's Fleans__Streaming__Redis__TotalQueueCount applies here unchanged.
const string StreamProviderName = "StreamProvider";
builder.Services.AddOptions<HashRingStreamQueueMapperOptions>(StreamProviderName)
    .Configure(o => o.TotalQueueCount = builder.Configuration.GetValue("Fleans:Streaming:Redis:TotalQueueCount", 8));
builder.Services.AddOptions<SimpleQueueCacheOptions>(StreamProviderName);
builder.Services.AddOptions<RedisStreamReceiverOptions>(StreamProviderName)
    .Configure(o =>
    {
        o.MaxStreamLength = 1000;
        o.TrimTimeMinutes = 5;
    });

builder.UseOrleans(silo =>
{
    silo.AddFleansPluginHost(builder.Configuration);

    // Every silo serves a slice of the reminder ring, including engine reminders (BPMN timers);
    // use the engine's Redis reminder table so those keep their durability.
    var orleansRedis = builder.Configuration.GetConnectionString("orleans-redis")
        ?? throw new InvalidOperationException("ConnectionStrings:orleans-redis is required.");
    silo.UseRedisReminderService(o => o.ConfigurationOptions = ConfigurationOptions.Parse(orleansRedis));

    // Aspire injects Orleans__Clustering__* / Orleans__GrainStorage__PubSubStore__*; the engine's
    // compose bundle and Helm chart only pass the Redis connection string and cluster ids.
    if (string.IsNullOrEmpty(builder.Configuration["Orleans:Clustering:ProviderType"]))
    {
        silo.UseRedisClustering(orleansRedis);
        silo.AddRedisGrainStorage("PubSubStore",
            o => o.ConfigurationOptions = ConfigurationOptions.Parse(orleansRedis));
    }

    silo.AddPersistentStreams(StreamProviderName, RedisStreamFactory.Create, null);
});

builder.Services.AddRestCallerPlugin();
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health");
app.Run();
