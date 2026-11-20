using Fleans.ExternalTasks.Sdk;
using Fleans.Plugins.CamundaExternalTask;
using Fleans.Plugins.Host;
using Fleans.Plugins.JavaScript;
using Fleans.Plugins.RestCaller;

// The worker for every plugin in this repo: a gRPC client of the Fleans API
// (Fleans:ExternalTaskWorker:EngineUrl). It joins no cluster and takes no Orleans dependency; it is
// a web host only because the Camunda plugin serves /engine-rest.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFleansExternalTaskWorker();

// Plugins — one line each; each reads its own configuration (see its README). Plugins:Enabled
// (e.g. "javascript") runs only the listed ones, so one image can serve an isolated deployment.
var plugins = EnabledPlugins.From(builder.Configuration);
if (plugins.Has("rest-caller")) builder.Services.AddRestCallerPlugin();
if (plugins.Has("camunda-external-task")) builder.AddCamundaExternalTaskPlugin();
if (plugins.Has("javascript")) builder.Services.AddJavaScriptPlugin();

builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health");
app.Run();
