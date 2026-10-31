# fleans-plugins

Custom-task plugins for [Fleans](https://github.com/nightBaker/fleans). Overview, plugin table,
running the host and releasing: [`README.md`](README.md).

## Build & test

```bash
dotnet build
dotnet test                       # FLEANS_PG_TESTS=1 adds the PostgreSQL tests (Docker required)
docker build -t fleans-plugins .
```

## Rule: one host, plugins are NuGet packages added with one line

- **Every plugin is a NuGet package** (`src/Fleans.Plugins.<Name>`, `IsPackable=true`); `release.yml`
  packs every packable project, so a new plugin is published without workflow changes.
- **There is exactly one host**, `src/Fleans.Plugins.Host` (image `ghcr.io/nightbaker/fleans-plugins`).
  Never add a per-plugin host project, Dockerfile build arg or image.
- **A plugin is added to a host with one line** in `Program.cs` —
  `builder.Services.Add<Name>Plugin()` or `builder.Add<Name>Plugin()` when it needs configuration
  or HTTP endpoints. That call reads the plugin's own configuration section, fails fast on invalid
  settings, and maps its endpoints itself (see `CamundaExternalTaskPluginExtensions`). The host's
  `Program.cs` holds only cluster wiring plus those lines.

*Why:* operators deploy one container and get every plugin; anyone with their own host gets the
same plugin from nuget.org without copying setup code. *How to apply:* a new plugin = a package
project + its one-line extension + one line in the host + a row in the README plugin table and a
`docs/<plugin>/README.md`.

Samples (`samples/`) are not plugins: never packed (`IsPackable=false`), not added to the host.

## Conventions

- `FleansVersion` in `Directory.Build.props` pins the `Fleans.Worker` package plugins compile
  against; bump it after each Fleans release. All plugins share one version (`VersionPrefix`).
- Plugin code follows Fleans' conventions: `[LoggerMessage]` logging, MSTest with AAA.
- Docs are part of "done": update the plugin's guide and the root README in the same PR.
