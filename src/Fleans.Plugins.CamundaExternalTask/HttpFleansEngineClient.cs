using System.Dynamic;
using System.Net;
using System.Text;
using Fleans.ExternalTasks.Sdk;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

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
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccess(response, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonConvert.DeserializeObject<ExpandoObject>(json) ?? new ExpandoObject();
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
        using var request = new HttpRequestMessage(method, new Uri(engineUrl, path));
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
        throw (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests
            ? new EngineUnavailableException(detail)
            : new EngineRejectedException(detail);
    }
}
