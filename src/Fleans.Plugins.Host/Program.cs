using Fleans.ExternalTasks.Sdk;
using Fleans.Plugins.CamundaExternalTask;
using Fleans.Plugins.RestCaller;

// The worker for every plugin in this repo: a gRPC client of the Fleans API
// (Fleans:ExternalTaskWorker:EngineUrl). It joins no cluster and takes no Orleans dependency; it is
// a web host only because the Camunda plugin serves /engine-rest.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFleansExternalTaskWorker();

// Plugins — one line each; each reads its own configuration (see its README).
builder.Services.AddRestCallerPlugin();
builder.AddCamundaExternalTaskPlugin();

builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health");
app.Run();
