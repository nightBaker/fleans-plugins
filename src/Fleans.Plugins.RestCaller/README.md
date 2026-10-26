# Fleans.Plugins.RestCaller

Backs `<serviceTask type="rest-call">` BPMN service tasks with an outbound HTTP request.
Populates `__response` with `status`, `statusCode`, `ok`, `body`, and `headers`; throws a
typed `CustomTaskFailedActivityException` on non-success per the engine's failure-code
mapping.

## Minimum consumer usage

In your plugin host's `Program.cs`:

```csharp
using Fleans.Plugins.RestCaller;

builder.Services.AddRestCallerPlugin();
```

The plugin then claims `<serviceTask type="rest-call">` activities at runtime. See the
[REST Caller guide](https://github.com/nightBaker/fleans-plugins/blob/main/docs/rest-caller/README.md)
for the ready-to-run host image and the full input/output schema.
