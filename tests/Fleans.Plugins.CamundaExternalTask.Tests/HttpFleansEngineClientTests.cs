using System.Dynamic;
using System.Net;
using System.Text;
using Fleans.ExternalTasks.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Fleans.Plugins.CamundaExternalTask.Tests;

/// <summary>
/// The REST contract with the engine, which every other test replaces with a fake: what each answer
/// of the engine means to the plugin, and where requests go.
/// </summary>
[TestClass]
public sealed class HttpFleansEngineClientTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ActivityInstanceId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [TestMethod]
    public async Task The_engines_not_active_answer_means_the_activity_is_gone()
    {
        // Arrange: the body ExecutionController sends (ErrorResponse, camelCase).
        var client = ClientAnswering(HttpStatusCode.NotFound, """{"error":"The activity instance is not active"}""");

        // Act
        var variables = await client.GetActiveActivityVariables(InstanceId, ActivityInstanceId);

        // Assert
        Assert.IsNull(variables);
    }

    [TestMethod]
    [DataRow("")] // ASP.NET Core: no such route, e.g. an engine older than this API
    [DataRow("<html><body>404 Not Found</body></html>")] // a proxy
    [DataRow("""{"message":"no Route matched with those values"}""")] // an API gateway
    [DataRow("[]")]
    public async Task A_404_the_engine_did_not_answer_is_not_a_finished_activity(string body)
    {
        // Arrange
        var client = ClientAnswering(HttpStatusCode.NotFound, body);

        // Act + Assert: null would make the caller delete a live task.
        await Assert.ThrowsExactlyAsync<EngineUnavailableException>(
            () => client.GetActiveActivityVariables(InstanceId, ActivityInstanceId));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.TooManyRequests)]
    [DataRow(HttpStatusCode.InternalServerError)]
    [DataRow(HttpStatusCode.ServiceUnavailable)]
    public async Task Answers_a_retry_can_fix_are_unavailable_on_every_call(HttpStatusCode status)
    {
        // Arrange: refused credentials say nothing about the task, so they must not fail the activity.
        var client = ClientAnswering(status, "");

        // Act + Assert
        await Assert.ThrowsExactlyAsync<EngineUnavailableException>(
            () => client.GetActiveActivityVariables(InstanceId, ActivityInstanceId));
        await Assert.ThrowsExactlyAsync<EngineUnavailableException>(
            () => client.CompleteActivity(InstanceId, "task", ActivityInstanceId, new ExpandoObject()));
        await Assert.ThrowsExactlyAsync<EngineUnavailableException>(
            () => client.FailActivity(InstanceId, "task", ActivityInstanceId, "500", "boom"));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.BadRequest)]
    [DataRow(HttpStatusCode.Conflict)]
    public async Task Refusals_of_the_call_itself_are_rejections(HttpStatusCode status)
    {
        // Arrange
        var client = ClientAnswering(status, """{"detail":"refused"}""");

        // Act + Assert
        await Assert.ThrowsExactlyAsync<EngineRejectedException>(
            () => client.CompleteActivity(InstanceId, "task", ActivityInstanceId, new ExpandoObject()));
    }

    [TestMethod]
    [DataRow("http://engine:8080", "http://engine:8080/Execution/complete-activity")]
    [DataRow("http://gateway/fleans", "http://gateway/fleans/Execution/complete-activity")]
    [DataRow("http://gateway/fleans/", "http://gateway/fleans/Execution/complete-activity")]
    public async Task Requests_go_under_the_engine_url(string engineUrl, string expected)
    {
        // Arrange
        var handler = new StubHandler(HttpStatusCode.OK, "");
        var client = Client(new StubFactory(handler), new ExternalTaskWorkerOptions { EngineUrl = new Uri(engineUrl) });

        // Act
        await client.CompleteActivity(InstanceId, "task", ActivityInstanceId, new ExpandoObject());

        // Assert
        Assert.AreEqual(expected, handler.LastRequestUri?.ToString());
    }

    /// <summary>
    /// Characterization, not a wish: the engine's REST intake turns ISO date-times into dates
    /// (Newtonsoft's default), so reading them back as <c>Date</c> is what the workflow really holds,
    /// and a <c>Date</c> a Camunda worker sends comes back as one. No other text may change type.
    /// </summary>
    [TestMethod]
    [DataRow("2026-01-01T00:00:00Z", "Date", "2026-01-01T00:00:00.000+0000")]
    [DataRow("2026-01-01T10:00:00+05:00", "Date", "2026-01-01T05:00:00.000+0000")]
    [DataRow("2026-01-01T00:00:00.000+0000", "Date", "2026-01-01T00:00:00.000+0000")]
    [DataRow("2026-01-01", "String", "2026-01-01")]
    [DataRow("hello", "String", "hello")]
    public async Task Only_iso_date_times_read_back_as_dates(string stored, string expectedType, string expectedValue)
    {
        // Arrange
        var body = JsonConvert.SerializeObject(new Dictionary<string, object> { ["v"] = stored });
        var client = ClientAnswering(HttpStatusCode.OK, body);

        // Act
        var variables = (IDictionary<string, object?>)(await client.GetActiveActivityVariables(InstanceId, ActivityInstanceId))!;
        var camunda = CamundaVariables.ToCamunda(variables["v"]);

        // Assert
        Assert.AreEqual(expectedType, camunda.Type);
        Assert.AreEqual(expectedValue, camunda.Value);
    }

    [TestMethod]
    public async Task With_native_grpc_it_reaches_an_http2_only_endpoint()
    {
        // Arrange: UseGrpcWeb=false is for an endpoint that speaks HTTP/2; over plain http that
        // means a Kestrel endpoint with Protocols=Http2, which refuses HTTP/1.1.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2));
        await using var engine = builder.Build();
        engine.MapGet("/Execution/{instance}/activities/{activity}/variables",
            () => Results.Content("""{"amount":5}""", "application/json"));
        await engine.StartAsync();
        var url = engine.Services.GetRequiredService<IServer>().Features
            .GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
        var client = Client(new RealFactory(),
            new ExternalTaskWorkerOptions { EngineUrl = new Uri(url), UseGrpcWeb = false });

        // Act
        var variables = (IDictionary<string, object?>)(await client.GetActiveActivityVariables(InstanceId, ActivityInstanceId))!;

        // Assert
        Assert.AreEqual(5L, variables["amount"]);
    }

    private static HttpFleansEngineClient ClientAnswering(HttpStatusCode status, string body) =>
        Client(new StubFactory(new StubHandler(status, body)),
            new ExternalTaskWorkerOptions { EngineUrl = new Uri("http://engine:8080") });

    private static HttpFleansEngineClient Client(IHttpClientFactory factory, ExternalTaskWorkerOptions options) =>
        new(factory, Options.Create(options));

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RealFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
