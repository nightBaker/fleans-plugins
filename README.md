# Fleans plugins

Custom-task plugins for [Fleans](https://github.com/nightBaker/fleans), the BPMN workflow engine.
Each plugin ships as a NuGet package that a worker adds with one line. This repo's worker,
`ghcr.io/nightbaker/fleans-plugins`, runs all of them: it connects to the Fleans API over gRPC,
with no Orleans dependency. The engine itself bundles no plugins.

| Plugin | BPMN | NuGet | Host line | Guide |
|---|---|---|---|---|
| REST Caller | `<serviceTask type="rest-call">` | `Fleans.Plugins.RestCaller` | `builder.Services.AddRestCallerPlugin();` | [docs/rest-caller](docs/rest-caller/README.md) |
| Camunda 7 External Task | `<serviceTask type="camunda:<topic>">` | `Fleans.Plugins.CamundaExternalTask` | `builder.AddCamundaExternalTaskPlugin();` | [docs/camunda-external-task](docs/camunda-external-task/README.md) |

## Run the worker

Released images are published as `ghcr.io/nightbaker/fleans-plugins:<version>`; the worker serves
HTTP (the Camunda `/engine-rest` API, `/health`) on port 8080 and connects out to the engine. All it
needs from the engine is the URL of its API. With the
[engine compose bundle](https://nightbaker.github.io/fleans/guides/self-host-docker-compose/),
start the engine first with `COMPOSE_PROJECT_NAME=fleans` in `.env` (that names the
`fleans_default` network), then add a `docker-compose.override.yaml` next to the bundle's
`docker-compose.yaml`:

```yaml
services:
  fleans-plugins:
    image: ghcr.io/nightbaker/fleans-plugins:latest
    networks: [fleans_default]
    ports: ["8090:8080"]
    environment:
      Fleans__ExternalTaskWorker__EngineUrl: "http://fleans-core:8080"
      ConnectionStrings__camunda-external-tasks: "Host=postgres;Database=camunda_external_tasks;Username=postgres;Password=${POSTGRES_PASSWORD}"
    depends_on: [postgres]
networks:
  fleans_default:
    external: true
    name: fleans_default
```

and run `docker compose up -d` again. If you skip the engine-first step or the project name, it
fails with `network fleans_default declared as external, but could not be found`. The plugins
appear in the engine's custom-task catalog (`GET /custom-tasks`, the *Custom tasks* page and the
editor's plugin picker) once the worker has connected. Check the service name against your bundle.
The Helm chart runs this image with `plugins.enabled=true`.

| Setting | Default | Meaning |
|---|---|---|
| `Fleans__ExternalTaskWorker__EngineUrl` | *required* | The Fleans API base URL. |
| `Fleans__ExternalTaskWorker__AccessToken` | — | Bearer token, for an API with authentication on. Also used for the Camunda plugin's REST calls. |
| `Fleans__ExternalTaskWorker__UseGrpcWeb` | `true` | gRPC-Web over HTTP/1.1, which works against any API endpoint. `false` for HTTP/2 endpoints; the Camunda plugin's REST calls then use HTTP/2 too. |
| `Fleans__ExternalTaskWorker__MaxConcurrentTasks` | `32` | Tasks run at the same time in this process. |

Delivery is at-least-once: a task can run again after a dropped connection or a timeout, which is
why a plugin keys side effects by the activity instance id. A task whose type has no connected
worker waits for one within the engine's stream delivery window. Each plugin's own settings are in
its guide; the Camunda plugin's database connection string is required, so the worker needs it even
if you only use the REST Caller (its database is created at startup and can live on the engine's
PostgreSQL server).

To write your own plugin, see
[Writing custom tasks](https://nightbaker.github.io/fleans/guides/writing-custom-tasks/) and
[External workers](https://nightbaker.github.io/fleans/guides/external-workers/).

## Build & test

```bash
dotnet build
dotnet test                         # PostgreSQL tests run with FLEANS_PG_TESTS=1 (Docker required)
docker build -t fleans-plugins .
```

`FleansVersion` in `Directory.Build.props` pins the `Fleans.ExternalTasks.Sdk` package every plugin compiles
against; bump it after each Fleans release (a stable plugin can't depend on a prerelease).

## Releasing

All plugins share one version. Push a tag `v<version>` that matches `VersionPrefix`/`VersionSuffix`
in `Directory.Build.props`. `release.yml` runs the tests, publishes every NuGet package and the
worker image to `ghcr.io/nightbaker/fleans-plugins`, then creates the GitHub Release (notes generated from the
merged PRs, the `.nupkg` files attached; a version with a suffix such as `-rc.1` is marked as a
pre-release).

NuGet uses [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing),
so there is no API key secret. On nuget.org, the `nightBaker` account needs a Trusted Publishing
policy with Repository Owner `nightBaker`, Repository `fleans-plugins` and Workflow File
`release.yml`.

## History

The Camunda plugin was developed in `nightBaker/fleans-camunda-external-task` and the REST Caller
in `nightBaker/fleans` (`src/Fleans/Fleans.Plugins.RestCaller`); both histories are preserved here.

## License

[PolyForm Internal Use License 1.0.0](LICENSE), the same as Fleans.
