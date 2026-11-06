using System.Dynamic;
using System.Net;
using System.Text;
using Fleans.ExternalTasks.Sdk;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fleans.Plugins.CamundaExternalTask;

/// <summary>
/// <see cref="IFleansEngineClient"/> over the Fleans REST API, at the URL and with the access token
/// the worker itself uses (<c>Fleans:ExternalTaskWorker</c>).
/// </summary>
internal sealed class HttpFleansEngineClient(
    IHttpClientFactory httpClientFactory, IOptions<ExternalTaskWorkerOptions> options) : IFleansEngineClient
{
    public const string HttpClientName = "fleans-engine";

    public async Task<ExpandoObject?> GetActiveActivityVariables(
        Guid workflowInstanceId, Guid activityInstanceId, CancellationToken cancellationToken = default)
    {
        using var response = await Send(HttpMethod.Get,
            $"Execution/{workflowInstanceId:D}/activities/{activityInstanceId:D}/variables", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // The caller deletes the task on null, so only the engine's own answer may mean "gone":
            // a proxy, a gateway or a wrong URL answers 404 for a task that is still waiting.
            var notFound = await response.Content.ReadAsStringAsync(cancellationToken);
            return IsEngineErrorResponse(notFound)
                ? null
                : throw new EngineUnavailableException($"404 from something other than the Fleans engine's API: {notFound}");
        }
        await EnsureSuccess(response, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonConvert.DeserializeObject<ExpandoObject>(json) ?? new ExpandoObject();
    }

    private static bool IsEngineErrorResponse(string body)
    {
        try
        {
            return JToken.Parse(body) is JObject answer && answer["error"]?.Type == JTokenType.String;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task CompleteActivity(
        Guid workflowInstanceId, string activityId, Guid activityInstanceId, ExpandoObject variables,
        CancellationToken cancellationToken = default)
    {
        using var response = await Send(HttpMethod.Post, "Execution/complete-activity", new
        {
            workflowInstanceId,
            activityId,
            activityInstanceId,
            variables,
        }, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
    }

    public async Task FailActivity(
        Guid workflowInstanceId, string activityId, Guid activityInstanceId, string code, string message,
        ExpandoObject? variables = null, CancellationToken cancellationToken = default)
    {
        using var response = await Send(HttpMethod.Post, "Execution/fail-activity", new
        {
            workflowInstanceId,
            activityId,
            activityInstanceId,
            errorCode = code,
            errorMessage = message,
            variables,
        }, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> Send(
        HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var worker = options.Value;
        var engineUrl = worker.EngineUrl
            ?? throw new InvalidOperationException("Fleans:ExternalTaskWorker:EngineUrl is required.");
        // Without the trailing slash, a relative Uri would replace the last segment of a path prefix.
        var baseUrl = engineUrl.AbsoluteUri.EndsWith('/') ? engineUrl : new Uri(engineUrl.AbsoluteUri + "/");
        using var request = new HttpRequestMessage(method, new Uri(baseUrl, path));
        if (!worker.UseGrpcWeb)
        {
            // Native gRPC means the endpoint speaks HTTP/2, possibly nothing else (plain http, Protocols=Http2).
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }
        if (body is not null)
            request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        try
        {
            var token = worker.AccessTokenProvider is { } provider
                ? await provider(cancellationToken)
                : worker.AccessToken;
            if (!string.IsNullOrEmpty(token))
                request.Headers.Authorization = new("Bearer", token);
            return await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new EngineUnavailableException($"The Fleans engine at {engineUrl} is unreachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EngineUnavailableException($"The Fleans engine at {engineUrl} did not answer in time.", ex);
        }
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var detail = $"The Fleans engine answered {(int)response.StatusCode} {response.ReasonPhrase}: {body}";
        // Refused credentials say nothing about the task: an operator or a token refresh fixes them.
        throw (int)response.StatusCode >= 500
            || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? new EngineUnavailableException(detail)
            : new EngineRejectedException(detail);
    }
}
