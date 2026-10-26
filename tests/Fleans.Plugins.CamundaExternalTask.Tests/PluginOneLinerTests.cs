using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Fleans.Plugins.CamundaExternalTask.Tests;

[TestClass]
public class PluginOneLinerTests
{
    private SqliteConnection _keepAlive = null!;
    private WebApplication? _app;

    [TestInitialize]
    public void Setup()
    {
        _keepAlive = new SqliteConnection(ConnectionString);
        _keepAlive.Open();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        if (_app is not null)
            await _app.DisposeAsync();
        _keepAlive.Dispose();
    }

    private string ConnectionString { get; } = $"DataSource=file:{Guid.NewGuid():N}?mode=memory&cache=shared";

    [TestMethod]
    public async Task One_line_serves_the_api_next_to_the_hosts_own_endpoints()
    {
        // Arrange
        var http = await StartHost();

        // Act
        var api = await http.SendAsync(FetchRequest());
        var own = await http.GetAsync("/health");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, api.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, own.StatusCode);
        Assert.AreEqual("ok", await own.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Basic_auth_from_configuration_protects_the_api()
    {
        // Arrange
        var http = await StartHost(("CamundaExternalTasks:BasicAuth:Username", "demo"),
            ("CamundaExternalTasks:BasicAuth:Password", "s3cret"));
        var authorized = FetchRequest();
        authorized.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("demo:s3cret")));

        // Act
        var anonymous = await http.SendAsync(FetchRequest());
        var withCredentials = await http.SendAsync(authorized);

        // Assert
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, withCredentials.StatusCode);
    }

    [TestMethod]
    [DataRow("CamundaExternalTasks:Provider", "Oracle")]
    [DataRow("CamundaExternalTasks:BasicAuth:Username", "demo")]
    public void Invalid_configuration_fails_at_startup(string key, string value)
    {
        var builder = Builder((key, value));

        Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddCamundaExternalTaskPlugin());
    }

    [TestMethod]
    public void Missing_connection_string_fails_at_startup()
    {
        var builder = WebApplication.CreateBuilder();

        Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddCamundaExternalTaskPlugin());
    }

    private async Task<HttpClient> StartHost(params (string Key, string Value)[] settings)
    {
        var builder = Builder(settings);
        builder.AddCamundaExternalTaskPlugin();
        _app = builder.Build();
        _app.MapGet("/health", () => "ok");
        await _app.StartAsync();
        return _app.GetTestClient();
    }

    private WebApplicationBuilder Builder(params (string Key, string Value)[] settings)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["ConnectionStrings:camunda-external-tasks"] = ConnectionString;
        builder.Configuration["CamundaExternalTasks:Provider"] = "Sqlite";
        foreach (var (key, value) in settings)
            builder.Configuration[key] = value;
        builder.Services.AddSingleton(Substitute.For<IGrainFactory>());
        return builder;
    }

    private static HttpRequestMessage FetchRequest() =>
        new(HttpMethod.Post, "/engine-rest/external-task/fetchAndLock")
        {
            Content = JsonContent.Create(new { workerId = "w1", maxTasks = 1, topics = new[] { new { topicName = "t", lockDuration = 1000 } } })
        };
}
