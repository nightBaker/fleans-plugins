# Fleans REST Caller

Backs `<serviceTask type="rest-call">` with an outbound HTTP request. The handler puts the
response into `__response` (`status`, `statusCode`, `ok`, `body`, `headers`) and fails the
activity with an HTTP-shaped error code on non-success, so workflows route with boundary error
events (`errorCode="404"`, `errorCode="504"`, …).

| Path | What |
|---|---|
| `src/Fleans.Plugins.RestCaller` | The NuGet package `Fleans.Plugins.RestCaller` (`services.AddRestCallerPlugin()`). |
| `src/Fleans.Plugins.Host` | The repo's worker (every plugin) and its container image. |
| `tests/Fleans.Plugins.RestCaller.Tests` | Handler tests against an in-process HTTP server. |

The Fleans engine images do not bundle this plugin. Run it in this repo's worker image or add it
to your own worker with one line.

**Compatibility:** built against `Fleans.ExternalTasks.Sdk` `$(FleansVersion)` from
`Directory.Build.props` (0.15.0 at the time of writing). Run it against an engine of the same
Fleans version or newer. 0.11.0 is the first version that runs as a gRPC worker; older engines
need the 0.10.x plugin host.

The worker registers `rest-call` with the package version (for example `0.15.0`), so a process can
pin it. It receives no workflow variables: the task's input mappings are all it reads.

## Run the worker

The repo's worker image `ghcr.io/nightbaker/fleans-plugins` includes this plugin; see
[Run the worker](../../README.md#run-the-worker). The plugin has no settings of its own.

## Use it in your own worker

```csharp
using Fleans.Plugins.RestCaller;

builder.Services.AddRestCallerPlugin();
```

See [Writing custom tasks](https://nightbaker.github.io/fleans/guides/writing-custom-tasks/) for
the handler contract and [External workers](https://nightbaker.github.io/fleans/guides/external-workers/)
for the worker process, or start from
[fleans-custom-worker-example](https://github.com/nightBaker/fleans-custom-worker-example).

## Parameters

| Name | Type | Required | Default | Notes |
|---|---|---|---|---|
| `url` | `String` | yes | — | Absolute URI |
| `method` | `String` | yes | `GET` | One of GET / POST / PUT / PATCH / DELETE / HEAD / OPTIONS |
| `headers` | `Map<String>` | no | `null` | Each `(name, value)` pair sent as a header. Names must be RFC 7230 tokens (letters, digits, and ``!#$%&'*+-.^_`|~``); names and values must not contain CR or LF — otherwise the activity fails with `code="400"` before any request is sent. **v1 only sources from a workflow variable** (e.g. `<fleans:input source="=requestHeaders" target="headers" />`). |
| `body` | `MultilineString` | no | `null` | Sent verbatim. If non-empty and no `Content-Type` header is supplied, defaults to `application/json` |
| `successCodes` | `List<Integer>` | no | `null` | When null/empty, defaults to `200..299`. Pass an explicit list (e.g. `[200, 201, 404]`) when you want non-2xx codes treated as success. **v1 only sources from a workflow variable.** |
| `timeoutSec` | `Integer` | yes | `30` | Whole seconds; clamped to `[1, 300]`. Timeout fails the activity with `code="504"` |
| `idempotencyKeyHeader` | `String` | no | `Idempotency-Key` on POST/PUT/PATCH/DELETE, none on GET/HEAD/OPTIONS | Header that carries `<activityInstanceId-guid>`, so server-side dedupe is keyed on the activity instance id (mitigates redelivery after a worker failure). Name another header to use it on any method; `none` sends nothing. The default keeps an `Idempotency-Key` already in `headers` |
| `maxResponseBytes` | `Integer` | no | `null` | Per-task cap on the buffered response body. Null or empty uses the worker ceiling (8 MiB by default). Can only **lower** the ceiling — a larger value is clamped to it. Must be ≥ 1. A success response whose body is over the cap fails the activity with `code="502"`; a non-success response keeps its own status code |

**Required means mapped.** `method` and `timeoutSec` have defaults, but the defaults only prefill
the editor: every required parameter needs a `<fleans:input>`. A hand-written BPMN that leaves one
out is rejected at deploy. Every input is checked against its type before dispatch, so `Integer`
parameters (`timeoutSec`, `maxResponseBytes`, `successCodes` entries) take whole numbers only —
`1.5` or `true` fails the task with `PLUGIN_INPUTS_INVALID` — and an input that is not a parameter
above is rejected the same way.

## Failure semantics

| Outcome | `ErrorState.Code` | Message |
|---|---|---|
| HTTP status outside `successCodes` | `<status>` (e.g. `"404"`) | response body, truncated to 1024 chars (`"(response body … exceeds limit of N bytes)"` if the body is over the cap) |
| Network error (`HttpRequestException` / `IOException`, including a connection drop while the body downloads) | `"502"` | `ex.Message` |
| Success response with a body larger than `maxResponseBytes` (or the 8 MiB worker ceiling) | `"502"` | `"response body … exceeds limit of N bytes"` |
| Timeout (per `timeoutSec`, covering the body download too) | `"504"` | `"timeout after Ns calling <uri>"` |
| Bad URL / unsupported method / `timeoutSec` out of `[1, 300]` / `maxResponseBytes` < 1 / invalid header (CR or LF, non-token name, unparseable `Content-Type`) | `"400"` | descriptive message |

Workflow authors route via boundary error events with `errorCode="404"`, `errorCode="504"`, etc.

Response bodies are buffered in memory and capped at **8 MiB** (`RestCallerServiceCollectionExtensions.DefaultMaxResponseContentBufferSize`) so a hostile or misbehaving upstream can't exhaust the worker heap. A declared `Content-Length` over the cap fails before the body is read; chunked responses fail as soon as the cap is crossed. `HEAD`, `204`, `205` and `304` responses carry no body and are never capped, even when they declare a `Content-Length`. Individual tasks can tighten the cap with `maxResponseBytes`, but only the host can raise the ceiling: a host that legitimately needs larger payloads can raise it by configuring the typed client **after** registering the plugin (client configuration runs in registration order, so a call placed before `AddRestCallerPlugin()` is overwritten):

```csharp
services.AddRestCallerPlugin();
services.AddHttpClient<RestCallerHandler>(c => c.MaxResponseContentBufferSize = 64L * 1024 * 1024);
```

## Worked example

```xml
<!-- Requires xmlns:fleans="https://fleans.io/schema/bpmn/1.0" on <bpmn:definitions> -->
<bpmn:serviceTask id="getUser" type="rest-call">
  <bpmn:extensionElements>
    <fleans:ioMapping>
      <fleans:input  source="=userApiUrl"        target="url" />
      <fleans:input  source="GET"                target="method" />
      <fleans:input  source="=requestHeaders"    target="headers" />
      <fleans:input  source="10"                 target="timeoutSec" />
      <fleans:output source="=__response.body"   target="user" />
      <fleans:output source="=__response.statusCode" target="status" />
    </fleans:ioMapping>
  </bpmn:extensionElements>
</bpmn:serviceTask>
```

Start the workflow with the variables that populate the inputs:

```json
{
  "WorkflowId": "fetch-user",
  "Variables": {
    "userApiUrl": "https://api.example.com/users/42",
    "requestHeaders": { "Authorization": "Bearer abc", "Accept": "application/json" }
  }
}
```

The activity completes with `user` (the parsed JSON body) and `status` (the integer status code) merged into the workflow scope. Author can route subsequent gateways on `status`.

The engine evaluates the mappings before the request is sent and after it returns. Sources after
`=` are C# expressions (`=baseUrl + "/users/" + userId`, `=__response.body.items.Count`); see
[I/O mapping](https://nightbaker.github.io/fleans/concepts/activities/io-mapping/). A source that
does not evaluate fails the activity with code `500`, naming the expression and the target.

**Delivery is at-least-once.** A task can run again after a dropped connection or a timeout, so a
non-idempotent call can reach the upstream twice. Every request other than GET, HEAD and OPTIONS
therefore carries `Idempotency-Key: <activityInstanceId>` by default, the same value on every
retry, so an upstream that dedupes on it does the work once. Set `idempotencyKeyHeader` to rename
the header, or to `none` to send nothing. An `Idempotency-Key` the workflow already sends in
`headers` is kept; a header named in `idempotencyKeyHeader` always carries the activity instance id.

A retry can overlap the first request when the worker died while the upstream was still
processing it. Upstreams that follow the IETF Idempotency-Key draft (or Stripe) answer the
overlapping request with `409`, which fails the activity with code `409` even though the original
call succeeds. Route on `errorCode="409"` with an error boundary, or add `409` to `successCodes`
if the upstream's 409 always means "already in progress".

**Declare outputs.** Without any `<fleans:output>`, the whole result reaches the workflow scope:
a `__response` variable with the full body and every response header (`Set-Cookie` included).
Map just what the process needs.
