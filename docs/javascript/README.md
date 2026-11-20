# Fleans JavaScript script tasks

Runs `<scriptTask scriptFormat="javascript">` (or `js`) **outside the engine**. The engine sends
every script task in a format other than C# to an external worker as task type
`fleans.script.<format>` ([nightBaker/fleans#1105](https://github.com/nightBaker/fleans/issues/1105)).
This plugin is that worker for JavaScript. A runaway, memory-hungry or malicious script then
cannot hurt the engine's silos, and the worker scales and upgrades on its own.

| Path | What |
|---|---|
| `src/Fleans.Plugins.JavaScript` | The NuGet package `Fleans.Plugins.JavaScript` (`services.AddJavaScriptPlugin()`). |
| `src/Fleans.Plugins.Host` | The repo's worker (every plugin) and its container image. |
| `tests/Fleans.Plugins.JavaScript.Tests` | Engine and handler tests. |

**Compatibility:** built against `Fleans.ExternalTasks.Sdk` `$(FleansVersion)` from
`Directory.Build.props`. It needs an engine that routes script formats to workers — the first
Fleans release after 0.14.0. Older engines reject `scriptFormat="javascript"` when the process is deployed.

## What a script sees

```xml
<bpmn:scriptTask id="compute" scriptFormat="javascript">
  <bpmn:script><![CDATA[
    function square(n) { return n * n; }
    let total = 0;
    for (const n of _context.items) { total += square(n); }
    _context.total = total;
    _context.label = `order-${_context.orderId}`;
  ]]></bpmn:script>
</bpmn:scriptTask>
```

- The script runs on [Jint](https://github.com/sebastienros/jint), an ECMAScript interpreter
  written in .NET, with **no access to .NET types**, the file system or the network.
- `_context` is a plain object: a JSON copy of every variable visible to the task.
- Only the **top-level variables the script changed** go back to the engine. A variable the
  script doesn't touch keeps its engine-side value and type (a Guid, a decimal, a long above
  2^53, a date). A written value is plain JSON: a string stays a string, even one that looks like a
  date. `delete _context.x` does not remove `x` — results merge into the scope.
- `_context` must stay an object; assigning `_context = { … }` reports every key of the new object.
- `${…}` is template-literal syntax here; the engine does not rewrite it.

| Limit | Value | On breach |
|---|---|---|
| Run time | 10 s | `TimeoutException` |
| Allocations | 64 MB | `MemoryLimitExceededException` |
| Call depth | 256 | `RecursionDepthOverflowException` |

A script that throws, or breaches a limit, fails the task with code **`500`** and the error's
message, like a failing C# script. Each run gets a fresh engine, so runs share no globals; parsed
scripts are cached by their text for the life of the process. A syntax error shows up when the
task runs, not at deploy.

The worker registers `fleans.script.javascript` with the package version and asks for all
variables. A deploy with a JavaScript script task and no connected worker succeeds **with a
warning**; the task waits until a worker connects. Each run is an external-task round trip (tens of
milliseconds) rather than an in-process call.

## Run the worker

The repo's worker image `ghcr.io/nightbaker/fleans-plugins` includes this plugin; see
[Run the worker](../../README.md#run-the-worker). The plugin has no settings of its own.

For isolation, run JavaScript in **its own deployment** of the image with
`Plugins__Enabled=javascript`: it then loads only this plugin, needs no database, and talks only to
the Fleans API. Harden it — non-root, read-only root filesystem, CPU and memory limits, and egress
only to the API.

### Docker Compose

Next to the [engine compose bundle](https://nightbaker.github.io/fleans/guides/self-host-docker-compose/)
(started first, with `COMPOSE_PROJECT_NAME=fleans` in `.env`), in `docker-compose.override.yaml`:

```yaml
services:
  fleans-javascript:
    image: ghcr.io/nightbaker/fleans-plugins:latest
    networks: [fleans_default]
    environment:
      Plugins__Enabled: "javascript"
      Fleans__ExternalTaskWorker__EngineUrl: "http://fleans-core:8080"
    user: "1654:1654"            # the image's non-root "app" user
    read_only: true
    tmpfs: [/tmp]
    cap_drop: [ALL]
    security_opt: ["no-new-privileges:true"]
    cpus: 1
    mem_limit: 512m
    depends_on: [fleans-core]
networks:
  fleans_default:
    external: true
    name: fleans_default
```

### Kubernetes

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: fleans-javascript
spec:
  replicas: 2
  selector:
    matchLabels: { app: fleans-javascript }
  template:
    metadata:
      labels: { app: fleans-javascript }
    spec:
      automountServiceAccountToken: false
      securityContext:
        runAsNonRoot: true
        runAsUser: 1654
        runAsGroup: 1654
        seccompProfile: { type: RuntimeDefault }
      containers:
        - name: worker
          image: ghcr.io/nightbaker/fleans-plugins:latest
          env:
            - { name: Plugins__Enabled, value: "javascript" }
            - { name: Fleans__ExternalTaskWorker__EngineUrl, value: "http://fleans-api:8080" }
          ports: [{ containerPort: 8080 }]
          readinessProbe: { httpGet: { path: /health, port: 8080 } }
          securityContext:
            readOnlyRootFilesystem: true
            allowPrivilegeEscalation: false
            capabilities: { drop: [ALL] }
          resources:
            requests: { cpu: 250m, memory: 256Mi }
            limits: { cpu: "1", memory: 512Mi }
          volumeMounts: [{ name: tmp, mountPath: /tmp }]
      volumes: [{ name: tmp, emptyDir: {} }]
---
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: fleans-javascript
spec:
  podSelector:
    matchLabels: { app: fleans-javascript }
  policyTypes: [Ingress, Egress]
  ingress: []                       # nothing calls the worker
  egress:
    - to:
        - podSelector:
            matchLabels: { app.kubernetes.io/component: api }   # match your Fleans API pods
      ports: [{ protocol: TCP, port: 8080 }]
    - to:
        - namespaceSelector: {}
          podSelector:
            matchLabels: { k8s-app: kube-dns }
      ports: [{ protocol: UDP, port: 53 }, { protocol: TCP, port: 53 }]
```

Check the API service name, port and pod labels against your Fleans install. If the kubelet's
readiness probe is blocked by `ingress: []` in your cluster, allow port 8080 from the node range or
drop the probe. Memory: each running script may allocate up to 64 MB, and the worker runs up to
`Fleans__ExternalTaskWorker__MaxConcurrentTasks` (32) tasks at a time. Size the limit for both, or
lower the concurrency.

## Use it in your own worker

```csharp
using Fleans.Plugins.JavaScript;

builder.Services.AddJavaScriptPlugin();
```

## Manual test

The engine repo's fixture
[`tests/manual/02-script-tasks/javascript-variables.bpmn`](https://github.com/nightBaker/fleans/blob/main/tests/manual/02-script-tasks/javascript-variables.bpmn)
(a C# task sets `x = 10`, a JavaScript task adds 1² + 2² + 3²) completes with `x = 24` and
`greeting = "hello 24"` when this worker is connected, and waits with a deploy warning when it is
not. `javascript-throws.bpmn` next to it fails its script task with code `500` and the thrown
message. `JavaScriptScriptHandlerTests` runs the fixture's script through the handler.
