using Fleans.ExternalTasks.Sdk;

namespace Fleans.Plugins.RestCaller;

/// <summary>
/// The parameter schema the management UI's BPMN editor will render for
/// <c>&lt;serviceTask type="rest-call"&gt;</c>. Static so tests and the DI extension
/// can both reference the same instance.
/// </summary>
public static class RestCallerSchema
{
    public static readonly ExternalTaskParameterSchema Default = new(new[]
    {
        new ExternalTaskParameter(
            Name: "url",
            DisplayName: "URL",
            Type: ExternalTaskParameterType.String,
            Required: true,
            Description: "Absolute URI. Use =workflowVar to source dynamically.",
            DefaultValue: null),

        new ExternalTaskParameter(
            Name: "method",
            DisplayName: "HTTP Method",
            Type: ExternalTaskParameterType.String,
            Required: true,
            Description: "GET, POST, PUT, PATCH, DELETE, HEAD, or OPTIONS.",
            DefaultValue: "GET"),

        new ExternalTaskParameter(
            Name: "headers",
            DisplayName: "Request Headers",
            Type: ExternalTaskParameterType.Map,
            Required: false,
            Description: "Each (header-name, header-value) pair. In v1, must be sourced from a workflow variable holding a map.",
            DefaultValue: null,
            ItemType: ExternalTaskParameterType.String),

        new ExternalTaskParameter(
            Name: "body",
            DisplayName: "Request Body",
            Type: ExternalTaskParameterType.MultilineString,
            Required: false,
            Description: "Sent verbatim. If non-empty and no Content-Type header is supplied, defaults to application/json.",
            DefaultValue: null),

        new ExternalTaskParameter(
            Name: "successCodes",
            DisplayName: "Success Codes",
            Type: ExternalTaskParameterType.List,
            Required: false,
            Description: "HTTP status codes treated as success. Empty/null defaults to 200..299. In v1, must be sourced from a workflow variable holding a list.",
            DefaultValue: null,
            ItemType: ExternalTaskParameterType.Integer),

        new ExternalTaskParameter(
            Name: "timeoutSec",
            DisplayName: "Timeout (seconds)",
            Type: ExternalTaskParameterType.Integer,
            Required: true,
            Description: "Whole seconds; clamped to [1, 300]. Timeout fails the activity with code=504.",
            DefaultValue: "30"),

        new ExternalTaskParameter(
            Name: "idempotencyKeyHeader",
            DisplayName: "Idempotency Key Header",
            Type: ExternalTaskParameterType.String,
            Required: false,
            Description: "When set, plugin sends <header>: <activityInstanceId-guid> so server-side dedupe is keyed on the activity instance id (mitigates redelivery after a worker failure).",
            DefaultValue: null),

        new ExternalTaskParameter(
            Name: "maxResponseBytes",
            DisplayName: "Max Response Bytes",
            Type: ExternalTaskParameterType.Integer,
            Required: false,
            Description: "Caps the buffered response body. Null/empty uses the worker ceiling (8 MiB by default). " +
                         "Can only lower that ceiling, never raise it; an oversized success response fails with code=502 " +
                         "(a non-success response keeps its own status code).",
            DefaultValue: null),
    });
}
