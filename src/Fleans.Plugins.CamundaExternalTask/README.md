# Fleans.Plugins.CamundaExternalTask

Camunda 7 External Task compatibility for Fleans. A `<serviceTask type="camunda:<topic>">` is
queued in the plugin's own `camunda_external_tasks` table and served through the Camunda 7
`/engine-rest/external-task` REST API (`fetchAndLock`, `complete`, `failure`, `bpmnError`,
`extendLock`, `lock`, `unlock`, plus the operator endpoints for queries, `retries` and `priority`). Existing Camunda external-task
workers keep working with only their base URL changed. The engine has no Camunda-specific code.

**For migration only:** this is a backward-compatibility bridge for existing Camunda 7 workers.
For new tasks, use a native [Fleans custom-task plugin](https://nightbaker.github.io/fleans/guides/writing-custom-tasks/).

## Minimum consumer usage

In a plugin host (`Fleans:Role=Plugin`) `Program.cs`, one line. It reads
`ConnectionStrings:camunda-external-tasks` and the `CamundaExternalTasks:*` settings, and maps
`/engine-rest/external-task/*`:

```csharp
using Fleans.Plugins.CamundaExternalTask;

builder.AddCamundaExternalTaskPlugin();
```

BPMN: replace `camunda:type="external" camunda:topic="charge-card"` with `type="camunda:charge-card"`.
A `camunda:taskPriority` becomes the input `<zeebe:input source="=10" target="taskPriority"/>`.

See the [repository README](https://github.com/nightBaker/fleans-plugins/blob/main/docs/camunda-external-task/README.md)
for the ready-to-run host, configuration, and differences from Camunda 7. For example, incidents
are opt-in (`CamundaExternalTasks:Incidents = true`); otherwise `retries = 0`
fails the activity.
Requires Fleans 0.7.0 or later.

