using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Fleans.Plugins.CamundaExternalTask.Tests;

[TestClass]
public class BasicAuthenticationTests
{
    private SqliteConnection _keepAlive = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    [TestInitialize]
    public async Task Setup()
    {
        var connectionString = $"DataSource=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(Substitute.For<IFleansEngineClient>());
        builder.Services.AddCamundaExternalTasks(o => o.UseSqlite(connectionString));
        _app = builder.Build();
        _app.MapCamundaExternalTaskApi().RequireBasicAuthentication("demo", "s3cret");
        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _app.DisposeAsync();
        _keepAlive.Dispose();
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("demo:wrong")]
    [DataRow("other:s3cret")]
    public async Task Missing_or_wrong_credentials_get_401_with_a_basic_challenge(string? credentials)
    {
        // Arrange
        var request = FetchRequest();
        if (credentials is not null)
            request.Headers.Authorization = Basic(credentials);

        // Act
        var response = await _http.SendAsync(request);

        // Assert
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual("Basic", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    [TestMethod]
    public async Task Right_credentials_reach_the_api()
    {
        var request = FetchRequest();
        request.Headers.Authorization = Basic("demo:s3cret");

        var response = await _http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    private static HttpRequestMessage FetchRequest() =>
        new(HttpMethod.Post, "/engine-rest/external-task/fetchAndLock")
        {
            Content = JsonContent.Create(new
            {
                workerId = "w1",
                maxTasks = 1,
                topics = new[] { new { topicName = "charge-card", lockDuration = 1_000 } },
            }),
        };

    private static AuthenticationHeaderValue Basic(string credentials) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
}
