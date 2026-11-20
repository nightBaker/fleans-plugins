# Fleans.Plugins.JavaScript

Runs Fleans `<scriptTask scriptFormat="javascript">` (or `js`) on an external worker instead of
inside the engine: the worker for task type `fleans.script.javascript`. Scripts run on
[Jint](https://github.com/sebastienros/jint) with no .NET access, a 10 s timeout, a 64 MB
allocation limit and a call depth of 256; `_context` is a JSON copy of the variables, and only
the top-level variables a script changes are sent back.

```csharp
builder.Services.AddFleansExternalTaskWorker();
builder.Services.AddJavaScriptPlugin();
```

Guide, hardened Docker Compose and Kubernetes snippets:
[docs/javascript](https://github.com/nightBaker/fleans-plugins/blob/main/docs/javascript/README.md).
