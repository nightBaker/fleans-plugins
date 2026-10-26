# Fleans plugins

Custom-task plugins for [Fleans](https://github.com/nightBaker/fleans), the Orleans-based BPMN
workflow engine. Each plugin ships as a NuGet package that a plugin host adds with one line. This
repo's plugin host, `ghcr.io/nightbaker/fleans-plugins`, runs all of them: it joins the engine's
Orleans cluster with `Fleans:Role=Plugin`. The engine itself bundles no plugins.

| Plugin | BPMN | NuGet | Host line | Guide |
|---|---|---|---|---|
| REST Caller | `<serviceTask type="rest-call">` | `Fleans.Plugins.RestCaller` | `builder.Services.AddRestCallerPlugin();` | [docs/rest-caller](docs/rest-caller/README.md) |
| Camunda 7 External Task | `<serviceTask type="camunda:<topic>">` | `Fleans.Plugins.CamundaExternalTask` | `builder.AddCamundaExternalTaskPlugin();` | [docs/camunda-external-task](docs/camunda-external-task/README.md) |

## Run the plugin host

Released images are published as `ghcr.io/nightbaker/fleans-plugins:<version>`; the host serves
HTTP (the Camunda `/engine-rest` API, `/health`) on port 8080. It joins the engine's Orleans
cluster with the engine's own membership, reminder and stream providers — set the same
`Fleans__Clustering__Provider`, `Fleans__Reminders__Provider` and `Fleans__Streaming__Provider`
(and their connection strings) as on the engine silos. The Fleans topology guides show it in a
[single-PostgreSQL install](https://nightbaker.github.io/fleans/guides/topology-single-postgres/)
and in the [scalable install](https://nightbaker.github.io/fleans/guides/topology-scalable/). With the
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
      ConnectionStrings__orleans-redis: "redis:6379,password=${ORLEANS_REDIS_PASSWORD}"
      Orleans__ClusterId: "fleans"          # must match the engine bundle's CLUSTER_CLUSTER_ID
      Orleans__ServiceId: "fleans"          # must match CLUSTER_SERVICE_ID
      ConnectionStrings__camunda-external-tasks: "Host=postgres;Database=camunda_external_tasks;Username=postgres;Password=${POSTGRES_PASSWORD}"
    depends_on: [redis, postgres]
networks:
  fleans_default:
    external: true
    name: fleans_default
```

and run `docker compose up -d` again. If you skip the engine-first step or the project name, it
fails with `network fleans_default declared as external, but could not be found`. The plugins
appear in the engine's custom-task catalog (`GET /custom-tasks`, the *Custom tasks* page and the
editor's plugin picker) once the host has joined.

| Setting | Default | Meaning |
|---|---|---|
| `Orleans__ClusterId`, `Orleans__ServiceId` | *required* | The engine's cluster ids. |
| `Fleans__Clustering__Provider` | `Redis` | `Redis` or `Postgres` — the engine's membership table. |
| `Fleans__Reminders__Provider` | `Redis` | `Redis` or `Postgres` — every silo serves a slice of the engine's reminders (BPMN timers). |
| `Fleans__Streaming__Provider` | `Redis` | `Redis`, `Kafka` or `Memory` — must be the engine's provider. |
| `ConnectionStrings__orleans-redis` | — | The engine's Redis; required while any provider above is `Redis`. |
| `ConnectionStrings__fleans` | — | The engine's PostgreSQL database; used by `Postgres` membership and reminders unless the next two are set. |
| `ConnectionStrings__fleans-cluster`, `ConnectionStrings__fleans-reminders` | — | The engine's dedicated membership / reminder databases, when it uses them. |
| `Fleans__Streaming__Kafka__*` | — | The engine's Kafka settings (`Brokers`, `QueueCount`, `TopicPrefix`, …), set to the same values. |
| `Fleans__Streaming__Redis__TotalQueueCount` | `8` | The same setting the engine reads. Set it to the same value as the engine, or stream events land on queues this host doesn't read. |

Like the engine, the host subscribes to streams through implicit subscriptions only, so it needs
no `PubSubStore`. Each plugin's own settings are in its guide; the Camunda plugin's database
connection string is required, so the host needs it even if you only use the REST Caller (its
database is created at startup and can live on the engine's PostgreSQL server).

To write your own plugin, see
[Writing custom tasks](https://nightbaker.github.io/fleans/guides/writing-custom-tasks/) and the
[fleans-custom-worker-example](https://github.com/nightBaker/fleans-custom-worker-example) template.

## Build & test

```bash
dotnet build
dotnet test                         # PostgreSQL tests run with FLEANS_PG_TESTS=1 (Docker required)
docker build -t fleans-plugins .
```

`FleansVersion` in `Directory.Build.props` pins the `Fleans.Worker` package every plugin compiles
against; bump it after each Fleans release (a stable plugin can't depend on a prerelease).

## Releasing

All plugins share one version. Push a tag `v<version>` that matches `VersionPrefix`/`VersionSuffix`
in `Directory.Build.props`. `release.yml` runs the tests, publishes every NuGet package and the
plugin host image to `ghcr.io/nightbaker/fleans-plugins`, then creates the GitHub Release (notes generated from the
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
