# Fleans Camunda External Task

Run your **Camunda 7 external-task workers on [Fleans](https://github.com/nightBaker/fleans)
without rewriting them.** This plugin serves the Camunda 7 `/engine-rest/external-task` REST API
on top of Fleans, so existing workers usually need only a new base URL. That covers
[`camunda-external-task-client-js`](https://github.com/camunda/camunda-external-task-client-js),
the Java `camunda-external-task-client`, and hand-written HTTP clients.

> **For migration only.** This plugin is a backward-compatibility bridge for existing Camunda 7
> workers. For new tasks, and whenever a worker gets rewritten anyway, use a native
> [Fleans custom-task plugin](https://nightbaker.github.io/fleans/guides/writing-custom-tasks/).
> Native plugins run inside the Fleans cluster with no polling, locks or extra database. They get
> typed input/output mappings, appear in the plugin catalog and editor, and support the full Fleans
> feature set rather than the Camunda subset described below.

The Fleans engine has no Camunda-specific code. The plugin is a regular Fleans custom-task
plugin that runs in its own silo:

```text
Fleans engine ──ExecuteCustomTaskEvent (camunda:<topic>)──▶ plugin host ──▶ camunda_external_tasks table
                                                                 ▲
Camunda workers ──fetchAndLock / complete / failure / bpmnError──┘ (/engine-rest/external-task)
       complete → CompleteActivity(variables)   failure(retries=0) / bpmnError → FailActivity(code)
```

| Path | What |
|---|---|
| `src/Fleans.Plugins.CamundaExternalTask` | The NuGet package: stream handler grain, table, REST endpoints. |
| `src/Fleans.Plugins.Host` | The repo's plugin host (`Fleans:Role=Plugin`, every plugin) and its container image (repo-root `Dockerfile`). |
| `tests/Fleans.Plugins.CamundaExternalTask.Tests` | REST wire-format tests (SQLite, plus PostgreSQL with `FLEANS_PG_TESTS=1`) and stream-handler tests on an Orleans test cluster. |
| `docs/camunda-external-task/manual-test-plan/` | End-to-end check with a real Camunda client against a real engine. |

**Compatibility:** requires **Fleans 0.8.0 or later** (the process instance business id on `ExecuteCustomTaskEvent`, returned as `businessKey`). Run it against an engine built from the same Fleans version or newer.
`FleansVersion` in `Directory.Build.props` pins the `Fleans.Worker` package the plugin compiles against.

## 1. Adapt the BPMN

```xml
<!-- Camunda 7 -->
<bpmn:serviceTask id="charge" camunda:type="external" camunda:topic="charge-card" />
<!-- Fleans -->
<bpmn:serviceTask id="charge" type="camunda:charge-card" />
```

The topic is everything after `camunda:`. Workers keep subscribing to `charge-card`.
A mechanical rewrite (check the diff afterwards):

```bash
sed -E -i.bak 's/camunda:type="external"[[:space:]]+camunda:topic="([^"]+)"/type="camunda:\1"/g' processes/*.bpmn
```

Camunda's `<camunda:inputOutput>` is not read. Use Fleans
[`<zeebe:ioMapping>`](https://nightbaker.github.io/fleans/concepts/custom-tasks/#mapping-grammar)
instead: inputs appear to the worker as local variables, and outputs read the variables the
worker sends on `complete`.

```xml
<!-- Camunda 7 -->
<bpmn:extensionElements>
  <camunda:inputOutput>
    <camunda:inputParameter name="toCharge">${order.amount}</camunda:inputParameter>
    <camunda:inputParameter name="currency">EUR</camunda:inputParameter>
    <camunda:outputParameter name="receiptId">${receipt.id}</camunda:outputParameter>
  </camunda:inputOutput>
</bpmn:extensionElements>
<!-- Fleans -->
<bpmn:extensionElements>
  <zeebe:ioMapping>
    <zeebe:input source="=order.amount" target="toCharge" />
    <zeebe:input source="EUR" target="currency" />
    <zeebe:output source="=receipt.id" target="receiptId" />
  </zeebe:ioMapping>
</bpmn:extensionElements>
```

| Camunda parameter | Fleans mapping |
|---|---|
| `${a}` or `${a.b.c}` | `source="=a"`, `source="=a.b.c"` |
| Constant text `EUR` | `source="EUR"` (or `source="=&quot;EUR&quot;"`) |
| `${42}`, `${true}`, `${null}` | `source="=42"`, `source="=true"`, `source="=null"` |
| `name` | `target` (letters, digits and `_` only) |
| Expressions with operators or method calls, `<camunda:script>`, `<camunda:list>`, `<camunda:map>` | No equivalent. Compute the value earlier (a script task) or in the worker. |

Declare `xmlns:zeebe="http://camunda.org/schema/zeebe/1.0"` on `<bpmn:definitions>`.

### Task priority

Camunda's `camunda:taskPriority` attribute becomes the reserved input `taskPriority`:

```xml
<!-- Camunda 7 -->
<bpmn:serviceTask id="ship" camunda:type="external" camunda:topic="ship-order" camunda:taskPriority="${order.priority}" />
<!-- Fleans -->
<bpmn:serviceTask id="ship" type="camunda:ship-order">
  <bpmn:extensionElements>
    <zeebe:ioMapping>
      <zeebe:input source="=order.priority" target="taskPriority" />   <!-- or a literal: source="=10" -->
    </zeebe:ioMapping>
  </bpmn:extensionElements>
</bpmn:serviceTask>
```

The source is a literal or a path over the workflow variables. It is evaluated once, when
the task is queued, and must give a whole number (a string of digits also counts). Otherwise
the activity fails with error code `400`. Without the input, the priority is `0`. `taskPriority` sets the priority
only and is not sent to the worker as a variable. Workers that fetch with `usePriority: true`
get the highest priority first, then the oldest. The Java and JS clients send that by default.

### Extension properties

Camunda's `<camunda:properties>` become inputs whose target starts with `extensionProperty_`:

```xml
<zeebe:ioMapping>
  <zeebe:input source="eu-west" target="extensionProperty_region" />
  <zeebe:input source="=order.limit" target="extensionProperty_limit" />
</zeebe:ioMapping>
```

Workers that fetch with `includeExtensionProperties: true` get them in `extensionProperties`
as strings (`region`, `limit`). Fleans only accepts identifiers as input targets
(`^[a-zA-Z_][a-zA-Z0-9_]*$`), so property names with `-` or `.` must be renamed. They are evaluated at fetch time, are not sent as variables,
and are left out when the source gives `null`.

### Business key and tenant id

The business key is the process instance's
[business id](https://nightbaker.github.io/fleans/reference/api/#business-id), set when the
instance is started (`"BusinessId": "order-123"` on `POST /Execution/start` or
`POST /Execution/message`). Fleans has no tenants, so the tenant id comes from a reserved input.
A mapped `businessKey` input replaces the business id, for example in a call activity's child
instance, which has none. It wins even when it evaluates to `null`, so a process can leave its
tasks unkeyed. Reserved inputs are evaluated when the task is queued:

```xml
<zeebe:input source="=order.id" target="businessKey" />
<zeebe:input source="acme" target="tenantId" />
```

Tasks return them as `businessKey` and `tenantId`. Fetch topics can filter on `businessKey`,
`tenantIdIn` and `withoutTenantId` (`tenantIdIn` plus `withoutTenantId` matches either).
They are not sent as variables. Each is at most 256 characters; a longer value fails the
activity with error code `400`. Booleans become `true`/`false` and objects or lists become JSON,
here and in extension properties.

**Upgrading from 0.6.0:** `taskPriority`, `businessKey`, `tenantId` and targets starting with
`extensionProperty_` were ordinary inputs before. If a process already maps a worker variable
to such a name, rename the target. Tasks queued by 0.6.0 under such a name no longer carry that
variable after the upgrade; drain or complete them first. The new columns are added to the
existing table at startup.

**Upgrading from 0.7.x:** the business key is set when a task is queued. Tasks queued by 0.7.x
keep a `null` business key even when their instance has a business id, so a `businessKey`
fetch filter does not match them. Drain or complete them first.

## 2. Run the plugin host

The repo's plugin host image `ghcr.io/nightbaker/fleans-plugins` includes this plugin and serves
the API on port 8080; [Run the plugin host](../../README.md#run-the-plugin-host) has the compose
override and the cluster settings. The plugin's own settings:

| Setting | Default | Meaning |
|---|---|---|
| `ConnectionStrings__camunda-external-tasks` | *required* | Database for the `camunda_external_tasks` table. The database and table are created at startup. |
| `CamundaExternalTasks__Provider` | `Postgres` | `Postgres` or `Sqlite`. Use PostgreSQL with more than one replica. |
| `CamundaExternalTasks__Incidents` | `false` | `true`: a `failure` with `retries = 0` becomes an incident, as in Camunda (see below). `false`: the activity fails with error code `500`. |
| `CamundaExternalTasks__BasicAuth__Username`, `CamundaExternalTasks__BasicAuth__Password` | *unset* | Set both to require HTTP basic authentication, as Camunda's `ProcessEngineAuthenticationFilter` does. Setting only one fails startup. |

Without the basic-auth settings the endpoints are anonymous, so put the host behind your gateway. Workers
then authenticate the Camunda way:

```java
ExternalTaskClient.create().baseUrl(url).addInterceptor(new BasicAuthProvider("demo", "s3cret")).build();
```

```js
new Client({ baseUrl: url, interceptors: new BasicAuthInterceptor({ username: "demo", password: "s3cret" }) });
```

Basic auth sends the password in every request, so serve the host over TLS (or behind a TLS
gateway).

To add the plugin to your own plugin host, reference the `Fleans.Plugins.CamundaExternalTask`
package and add one line; it reads the settings above and maps `/engine-rest/external-task/*`:

```csharp
builder.AddCamundaExternalTaskPlugin();
```

For other authentication schemes, register the parts yourself instead: `MapCamundaExternalTaskApi()`
returns the route group, so you can call `.RequireAuthorization()` on it.

```csharp
builder.Services.AddCamundaExternalTasks(o => o.UseNpgsql(connectionString));
var app = builder.Build();
app.MapCamundaExternalTaskApi().RequireAuthorization();   // → /engine-rest/external-task/*
```

## 3. Point the workers at the host

```java
ExternalTaskClient.create().baseUrl("http://camunda-external-task:8080/engine-rest").build();
```

```js
new Client({ baseUrl: "http://camunda-external-task:8080/engine-rest" });
```

## Supported endpoints

These are the endpoints the official Java and JS external-task clients call:

| Endpoint | Notes |
|---|---|
| `POST /external-task/fetchAndLock` | `workerId`, `maxTasks`, `usePriority`, `sorting` (`createTime` `asc`/`desc`; oldest first by default), `asyncResponseTimeout` (long polling, up to 30 min), per topic `topicName`, `lockDuration`, `variables`, `localVariables`, `includeExtensionProperties`, `processDefinitionId(In)`, `processDefinitionKey(In)`, `businessKey`, `tenantIdIn`, `withoutTenantId`. |
| `POST /external-task/{id}/complete` | `variables` go to the workflow scope. `localVariables` are visible only to output mappings. |
| `POST /external-task/{id}/failure` | `retries > 0`: the task is fetchable again after `retryTimeout`. `retries = 0`: the activity fails. |
| `POST /external-task/{id}/bpmnError` | Fails the activity with `errorCode`. An error boundary event with that code catches it. `variables` are set in the activity scope first, so the boundary path sees them. |
| `POST /external-task/{id}/extendLock`, `/lock`, `/unlock` | Same semantics as Camunda. |
| `GET /execution/{id}/localVariables/{name}/data` | Content of a `File` (or `Bytes`) variable. The Java and JS clients call it to load file variables. `{id}` is the task id. |
| `GET /external-task`, `GET /external-task/count` | Operator queries. Filters: `externalTaskId`, `topicName`, `workerId`, `locked`, `notLocked`, `withRetriesLeft`, `noRetriesLeft`, `activityId`, `processInstanceId`, `processDefinitionId`, `tenantIdIn`, plus `firstResult` and `maxResults`. Oldest first. |
| `GET /external-task/{id}`, `/{id}/errorDetails`, `GET /external-task/topic-names` | Same shapes as Camunda. `suspended` is always `false`. |
| `PUT /external-task/{id}/retries` | `> 0` resolves an incident, and the task can be fetched again. `0` creates an incident, or fails the activity when incidents are off. |
| `PUT /external-task/{id}/priority` | Operator override of the priority from the BPMN. Takes effect on the next fetch. |

Errors use Camunda's `{ "type", "message" }` body and status codes:
- `404` for an unknown task, including a task whose activity already ended, for example cancelled or interrupted by a boundary event. A worker that reports on such a task learns its result was not applied.
- `500` when the task is locked by another worker or its result is being reported.
- `400` for an invalid request. Responses use
the bare `application/json` content type, the only one the JS client parses.

## Differences from Camunda 7

- **Incidents are opt-in.** By default, `failure` with `retries = 0` fails the activity with
  error code `500` and the worker's `errorMessage` plus `errorDetails`. The failure is visible in
  Fleans, and an error boundary event can catch it. With `CamundaExternalTasks__Incidents=true`
  it behaves like Camunda: the task stays as an incident and the activity keeps waiting. Find
  incidents with `GET /external-task?noRetriesLeft=true` and resolve them with
  `PUT /external-task/{id}/retries`. Incidents do not show in the Fleans UI. An incident whose
  workflow was cancelled stays listed until its retries are set. The next fetch then removes it.
- **Variable types:** `String`, `Boolean`, `Integer`, `Short`, `Long`, `Double`, `Date` (kept as a
  string), `Null`, `Json`, `Bytes`, `File`, and `Object` with `serializationDataFormat: application/json`.
  `Bytes` and `File` are stored in the workflow as
  `{ "camundaType": "Bytes" | "File", "value": "<base64>", "filename", "mimeType", "encoding" }`
  and come back to workers with their original type. They live in the workflow state, so keep
  them small and pass large files by reference (a URL or storage key) instead.
  Objects and lists come back as `Json`. Whole numbers come back as `Integer` when they fit in
  32 bits and as `Long` otherwise, so Java workers should read them as `Number`. Java-serialized
  objects are rejected with `400`.
- **Identifiers:** task `id`, `executionId` and `activityInstanceId` are the Fleans activity
  instance id. `processInstanceId` is the workflow instance id, and `processDefinitionKey` is the
  process id. `businessKey` is the instance's business id and `tenantId` comes from a reserved input (see
  [Business key and tenant id](#business-key-and-tenant-id)). `processDefinitionVersionTag` is
  always `null`, and the fetch filter on it is ignored.
- **Stale tasks:** a task whose activity ended without a worker (cancelled, or interrupted by a
  boundary event) is removed the next time a worker fetches from its topic. Incidents are never
  fetched, so a stale incident is removed when an operator query (`GET /external-task`, `/count`,
  `/{id}`, `/{id}/errorDetails`) reaches it.
- **Incidents cannot be locked:** `POST /external-task/{id}/lock` on a task with `retries = 0`
  returns `400`. Set retries first.
- **Scope:** the external-task endpoints above. Start processes
  and correlate messages through the [Fleans REST API](https://nightbaker.github.io/fleans/reference/api/).
- **Rejected results:** if the engine refuses a `complete` outright (for example, outputs it cannot
  store), the activity is failed and the worker gets `400`. This is the same rule Fleans applies to
  its other plugins. A transient engine error returns `500`, and the worker can send `complete` again.
- **Priority** comes from the `taskPriority` input (see [Task priority](#task-priority)), not
  from `camunda:taskPriority`. Process-level `camunda:taskPriority` defaults are not read.
- **Clocks:** lock expiry uses each replica's clock, so keep replicas NTP-synced.
- **Long polling** wakes up immediately when a task becomes available on the same replica. Tasks
  that appear on another replica are found by a re-check every 2 seconds.

Logs use EventIds 9350–9369: 9350 queued, 9356 invalid task priority, 9357 priority changed, 9358 incident created, 9359 retries set, 9360 locked, 9361 completed, 9362 failure with
retries left, 9363 retries exhausted, 9364 BPMN error, 9365 stale task removed, 9367 task could
not be handed out, 9368 result rejected by the workflow.

## Development

```bash
dotnet build
dotnet test                         # SQLite + Orleans test cluster
FLEANS_PG_TESTS=1 dotnet test       # + PostgreSQL via Testcontainers (Docker)
```

To build against an unreleased Fleans, pack `Fleans.Domain.Abstractions`,
`Fleans.Application.Abstractions` and `Fleans.Worker` from a fleans checkout
(`dotnet pack -p:Version=<FleansVersion> -o <feed>`). Then build with
`-p:RestoreAdditionalProjectSources=<feed>`, using a separate `NUGET_PACKAGES` folder so the
stand-in packages don't shadow the published ones later.

### Schema changes

The table is created at startup with `CREATE TABLE/INDEX IF NOT EXISTS`, which leaves an existing
table as it is. Upgrades are therefore additive. A new column goes into `AddedColumns` in
`CamundaExternalTaskServiceCollectionExtensions.cs`, which adds it to older tables at startup.
It must be nullable or have a default of `0`. The schema-initializer tests drop those columns
to simulate an old table. Renames, type changes and indexes on new columns need a versioned
migration step: the create script runs before columns are added, so such an index would fail
on an old table.

## Releasing

Released together with the other plugins in this repository — see the
[repository README](../../README.md#releasing).

## License

[PolyForm Internal Use License 1.0.0](../../LICENSE), the same as Fleans.
