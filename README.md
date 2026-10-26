# Fleans plugins

Custom-task plugins for [Fleans](https://github.com/nightBaker/fleans), the Orleans-based BPMN
workflow engine. Each plugin ships as a NuGet package (to add to your own plugin host) and as a
ready-to-run plugin host image that joins the engine's Orleans cluster with `Fleans:Role=Plugin`.
The engine itself bundles no plugins.

| Plugin | BPMN | NuGet | Image | Guide |
|---|---|---|---|---|
| REST Caller | `<serviceTask type="rest-call">` | `Fleans.Plugins.RestCaller` | `ghcr.io/nightbaker/fleans-rest-caller` | [docs/rest-caller](docs/rest-caller/README.md) |
| Camunda 7 External Task | `<serviceTask type="camunda:<topic>">` | `Fleans.Plugins.CamundaExternalTask` | `ghcr.io/nightbaker/fleans-camunda-external-task` | [docs/camunda-external-task](docs/camunda-external-task/README.md) |

To write your own plugin, see
[Writing custom tasks](https://nightbaker.github.io/fleans/guides/writing-custom-tasks/) and the
[fleans-custom-worker-example](https://github.com/nightBaker/fleans-custom-worker-example) template.

## Build & test

```bash
dotnet build
dotnet test                         # PostgreSQL tests run with FLEANS_PG_TESTS=1 (Docker required)
docker build -t fleans-camunda-external-task .                                 # default HOST
docker build --build-arg HOST=Fleans.RestCaller.Host -t fleans-rest-caller .
```

`FleansVersion` in `Directory.Build.props` pins the `Fleans.Worker` package every plugin compiles
against; bump it after each Fleans release (a stable plugin can't depend on a prerelease).

## Releasing

All plugins share one version. Push a tag `v<version>` that matches `VersionPrefix`/`VersionSuffix`
in `Directory.Build.props`. `release.yml` runs the tests, publishes every NuGet package and every
host image to `ghcr.io/nightbaker/*`, then creates the GitHub Release (notes generated from the
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
