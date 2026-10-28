using System.Dynamic;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleans.Application.Grains;
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Fleans.Plugins.CamundaExternalTask.Tests;

/// <summary>
/// The REST surface as a Camunda 7 external-task client sees it: request/response JSON shapes,
/// status codes and error bodies. The engine is replaced by a substitute callback grain.
/// </summary>
[TestClass]
public class CamundaExternalTaskApiTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private SqliteConnection? _keepAlive;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private ManualClock _clock = null!;
    private IWorkflowInstanceCallback _callback = null!;
    private ExpandoObject? _processVariables;

    /// <summary>The external-task table provider; SQLite here, PostgreSQL in the derived class.</summary>
    protected virtual Task<Action<DbContextOptionsBuilder>> CreateDatabase()
    {
        var connectionString = $"DataSource=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();
        return Task.FromResult<Action<DbContextOptionsBuilder>>(o => o.UseSqlite(connectionString));
    }

    [TestInitialize]
    public async Task Setup()
    {
        var configureDb = await CreateDatabase();

        _clock = new ManualClock(T0);
        _processVariables = Expando(("amount", 42L), ("customer", "bob"), ("vip", true));
        _callback = Substitute.For<IWorkflowInstanceCallback>();
        _callback.GetVariablesIfActive(Arg.Any<Guid>(), Arg.Any<Guid>())
            .Returns<ValueTask<ExpandoObject?>>(_ => ValueTask.FromResult<ExpandoObject?>(_processVariables));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IWorkflowInstanceCallback>(Arg.Any<Guid>(), Arg.Any<string?>()).Returns(_callback);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddSingleton(grainFactory);
        builder.Services.AddCamundaExternalTasks(configureDb);
        _app = builder.Build();
        _app.MapCamundaExternalTaskApi();
        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        if (_app is not null) // null when a derived class skipped the test in CreateDatabase
            await _app.DisposeAsync();
        _keepAlive?.Dispose();
    }

    [TestMethod]
    public async Task FetchAndLock_returns_camunda_shaped_tasks_with_typed_variables()
    {
        // Arrange
        var task = await Seed("charge-card", inputs: [("toCharge", 42L)]);

        // Act — the body camunda-external-task-client sends.
        var response = await _http.PostAsJsonAsync("/engine-rest/external-task/fetchAndLock", new
        {
            workerId = "w1",
            maxTasks = 10,
            usePriority = true,
            topics = new[] { new { topicName = "charge-card", lockDuration = 10_000, deserializeValues = false } },
        });

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.ToString(),
            "camunda-external-task-client-js parses only an exact 'application/json' content type.");
        var tasks = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        Assert.HasCount(1, tasks);
        var locked = tasks[0]!;
        Assert.AreEqual(task.Id, (string?)locked["id"]);
        Assert.AreEqual("charge-card", (string?)locked["topicName"]);
        Assert.AreEqual("w1", (string?)locked["workerId"]);
        // Seeding advanced the clock by 1 ms; the lock lasts 10 s from the fetch.
        Assert.AreEqual("2026-01-01T12:00:10.001+0000", (string?)locked["lockExpirationTime"]);
        Assert.AreEqual(task.WorkflowInstanceId.ToString("D"), (string?)locked["processInstanceId"]);
        Assert.AreEqual("order-process", (string?)locked["processDefinitionKey"]);
        Assert.AreEqual("ct1", (string?)locked["activityId"]);
        var variables = locked["variables"]!;
        Assert.AreEqual("Integer", (string?)variables["amount"]!["type"]);
        Assert.AreEqual(42, (int)variables["amount"]!["value"]!);
        Assert.AreEqual("String", (string?)variables["customer"]!["type"]);
        Assert.AreEqual("Boolean", (string?)variables["vip"]!["type"]);
        Assert.AreEqual(42, (int)variables["toCharge"]!["value"]!, "Inputs are exposed as variables.");
    }

    [TestMethod]
    public async Task FetchAndLock_honours_variable_names_and_local_variables()
    {
        // Arrange
        await Seed("charge-card", inputs: [("who", "bob")]);

        // Act
        var named = await Fetch("w1", "charge-card", variables: ["amount"]);
        await _http.PostAsync($"/engine-rest/external-task/{named[0]!["id"]}/unlock", null);
        var localOnly = await Fetch("w1", "charge-card", localVariables: true);

        // Assert
        CollectionAssert.AreEquivalent(new[] { "amount" }, Names(named[0]!["variables"]!));
        CollectionAssert.AreEquivalent(new[] { "who" }, Names(localOnly[0]!["variables"]!));
    }

    [TestMethod]
    public async Task Locked_task_becomes_fetchable_again_after_its_lock_expires()
    {
        // Arrange
        await Seed("charge-card");
        Assert.HasCount(1, await Fetch("w1", "charge-card", lockDuration: 1_000));

        // Act
        var whileLocked = await Fetch("w2", "charge-card");
        _clock.Advance(TimeSpan.FromSeconds(2));
        var afterExpiry = await Fetch("w2", "charge-card");

        // Assert
        Assert.HasCount(0, whileLocked);
        Assert.HasCount(1, afterExpiry);
        Assert.AreEqual("w2", (string?)afterExpiry[0]!["workerId"]);
    }

    [TestMethod]
    public async Task FetchAndLock_drops_tasks_whose_activity_is_no_longer_active()
    {
        // Arrange — the workflow moved on without the worker (e.g. interrupted by a boundary timer).
        await Seed("charge-card");
        _processVariables = null;

        // Act
        var tasks = await Fetch("w1", "charge-card");

        // Assert
        Assert.HasCount(0, tasks);
        Assert.HasCount(0, await Rows());
    }

    [TestMethod]
    public async Task Complete_converts_typed_variables_and_hands_them_with_the_local_ones_to_the_engine()
    {
        // Arrange — the engine applies the output mappings to the result, so they can read the local variables.
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card");
        ExpandoObject? completed = null;
        await _callback.CompleteActivity(Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Do<ExpandoObject>(v => completed = v));

        // Act
        var response = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/complete", new
        {
            workerId = "w1",
            variables = new Dictionary<string, object>
            {
                ["charged"] = new { value = true, type = "Boolean" },
                ["total"] = new { value = 99.5, type = "Double" },
                ["order"] = new { value = "{\"lines\":[1,2]}", type = "Json" },
                ["customerDto"] = new
                {
                    value = "{\"name\":\"bob\"}",
                    type = "Object",
                    valueInfo = new { objectTypeName = "com.acme.Customer", serializationDataFormat = "application/json" },
                },
            },
            localVariables = new Dictionary<string, object>
            {
                ["receipt"] = new { value = "{\"id\":\"R-1\"}", type = "Json" },
            },
        });

        // Assert
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        await _callback.Received(1).CompleteActivity("ct1", task.ActivityInstanceId, Arg.Any<ExpandoObject>());
        var outputs = (IDictionary<string, object?>)completed!;
        Assert.IsTrue((bool)outputs["charged"]!);
        Assert.AreEqual(99.5, outputs["total"]);
        CollectionAssert.AreEqual(new List<object?> { 1L, 2L }, (List<object?>)((IDictionary<string, object?>)outputs["order"]!)["lines"]!);
        Assert.AreEqual("bob", ((IDictionary<string, object?>)outputs["customerDto"]!)["name"]);
        Assert.AreEqual("R-1", ((IDictionary<string, object?>)outputs["receipt"]!)["id"], "Local variables are part of the result.");
        Assert.IsNull(await Row(task.Id), "A completed task leaves the table.");
    }

    [TestMethod]
    public async Task Errors_use_camunda_status_codes_and_bodies()
    {
        // Arrange
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card");

        // Act
        var missing = await _http.PostAsJsonAsync("/engine-rest/external-task/nope/complete", new { workerId = "w1" });
        var wrongWorker = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/complete", new { workerId = "w2" });
        var unsupported = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/complete", new
        {
            workerId = "w1",
            variables = new
            {
                customer = new
                {
                    value = "rO0AB",
                    type = "Object",
                    valueInfo = new { serializationDataFormat = "application/x-java-serialized-object" },
                },
            },
        });

        // Assert
        await AssertError(missing, HttpStatusCode.NotFound, "InvalidRequestException");
        await AssertError(wrongWorker, HttpStatusCode.InternalServerError, "BadUserRequestException");
        await AssertError(unsupported, HttpStatusCode.BadRequest, "InvalidRequestException");
        await _callback.DidNotReceive().CompleteActivity(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<ExpandoObject>());
    }

    [TestMethod]
    public async Task BpmnError_fails_the_activity_with_the_error_code()
    {
        // Arrange
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card");

        ExpandoObject? reported = null;
        await _callback.FailActivity(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Exception>(),
            Arg.Do<ExpandoObject>(v => reported = v));

        // Act
        var response = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/bpmnError", new
        {
            workerId = "w1",
            errorCode = "DECLINED",
            errorMessage = "no funds",
            variables = new Dictionary<string, object> { ["declineReason"] = new { value = "insufficient", type = "String" } },
        });

        // Assert
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        await _callback.Received(1).FailActivity("ct1", task.ActivityInstanceId,
            Arg.Is<Exception>(e => e is ActivityException
                && ((ActivityException)e).GetActivityErrorState().Code == "DECLINED"
                && ((ActivityException)e).GetActivityErrorState().Message == "no funds"),
            Arg.Any<ExpandoObject>());
        Assert.AreEqual("insufficient", ((IDictionary<string, object?>)reported!)["declineReason"],
            "bpmnError variables reach the workflow with the failure.");
    }

    [TestMethod]
    public async Task BpmnError_with_unsupported_variables_is_a_bad_request_and_keeps_the_task()
    {
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card");

        var response = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/bpmnError", new
        {
            workerId = "w1",
            errorCode = "DECLINED",
            variables = new Dictionary<string, object>
            {
                ["blob"] = new { value = "rO0AB", type = "Object", valueInfo = new { serializationDataFormat = "application/x-java-serialized-object" } },
            },
        });

        await AssertError(response, HttpStatusCode.BadRequest, "InvalidRequestException");
        Assert.IsNotNull(await Row(task.Id));
        await _callback.DidNotReceive().FailActivity(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Exception>(), Arg.Any<ExpandoObject>());
    }

    [TestMethod]
    public async Task Failure_with_retries_left_waits_for_the_retry_timeout()
    {
        // Arrange
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card");

        // Act
        var response = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/failure",
            new { workerId = "w1", errorMessage = "down", retries = 2, retryTimeout = 5_000 });
        var beforeTimeout = await Fetch("w1", "charge-card");
        _clock.Advance(TimeSpan.FromSeconds(5));
        var afterTimeout = await Fetch("w1", "charge-card");

        // Assert
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.HasCount(0, beforeTimeout);
        Assert.HasCount(1, afterTimeout);
        Assert.AreEqual(2, (int)afterTimeout[0]!["retries"]!);
        Assert.AreEqual("down", (string?)afterTimeout[0]!["errorMessage"]);
        await _callback.DidNotReceive().FailActivity(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Exception>());
    }

    [TestMethod]
    public async Task ExtendLock_keeps_the_task_locked_past_its_original_expiry()
    {
        // Arrange
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card", lockDuration: 1_000);

        // Act
        var extended = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/extendLock",
            new { workerId = "w1", newDuration = 60_000 });
        var byOther = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/extendLock",
            new { workerId = "w2", newDuration = 60_000 });
        _clock.Advance(TimeSpan.FromSeconds(2));
        var fetchedByOther = await Fetch("w2", "charge-card");

        // Assert
        Assert.AreEqual(HttpStatusCode.NoContent, extended.StatusCode);
        await AssertError(byOther, HttpStatusCode.BadRequest, "InvalidRequestException");
        Assert.HasCount(0, fetchedByOther);
    }

    [TestMethod]
    public async Task Late_report_from_a_worker_whose_lock_was_handed_over_is_rejected()
    {
        // Arrange — w1's lock expires and w2 takes the task over.
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card", lockDuration: 1_000);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.HasCount(1, await Fetch("w2", "charge-card"));

        // Act — w1 reports late.
        var lateFailure = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/failure",
            new { workerId = "w1", errorMessage = "late", retries = 3, retryTimeout = 0 });
        var lateComplete = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/complete", new { workerId = "w1" });
        var lateExtend = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/extendLock",
            new { workerId = "w1", newDuration = 60_000 });
        var ownerComplete = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/complete", new { workerId = "w2" });

        // Assert — w2 keeps its lock and its result is the one the workflow gets.
        await AssertError(lateFailure, HttpStatusCode.InternalServerError, "BadUserRequestException");
        await AssertError(lateComplete, HttpStatusCode.InternalServerError, "BadUserRequestException");
        await AssertError(lateExtend, HttpStatusCode.BadRequest, "InvalidRequestException");
        Assert.AreEqual(HttpStatusCode.NoContent, ownerComplete.StatusCode);
        await _callback.Received(1).CompleteActivity("ct1", task.ActivityInstanceId, Arg.Any<ExpandoObject>());
    }

    [TestMethod]
    public async Task A_task_whose_variables_cannot_be_read_is_failed_without_blocking_the_others()
    {
        // Arrange — the first task's variable scope is unknown to the engine (deterministic error).
        var poison = await Seed("charge-card");
        var healthy = await Seed("charge-card");
        _callback.GetVariablesIfActive(poison.ActivityInstanceId, Arg.Any<Guid>())
            .Returns<ValueTask<ExpandoObject?>>(_ => throw new InvalidOperationException("unknown scope"));

        // Act
        var tasks = await Fetch("w1", "charge-card");

        // Assert
        Assert.AreEqual(healthy.Id, (string?)tasks.Single()!["id"]);
        await _callback.Received(1).FailActivity("ct1", poison.ActivityInstanceId, Arg.Any<InvalidOperationException>());
        Assert.IsNull(await Row(poison.Id));
    }

    [TestMethod]
    public async Task FetchAndLock_rejects_a_lock_duration_out_of_range()
    {
        var response = await _http.PostAsJsonAsync("/engine-rest/external-task/fetchAndLock", new
        {
            workerId = "w1",
            maxTasks = 1,
            topics = new[] { new { topicName = "charge-card", lockDuration = long.MaxValue } },
        });

        await AssertError(response, HttpStatusCode.BadRequest, "InvalidRequestException");
    }

    [TestMethod]
    public async Task Schema_initializers_of_several_replicas_can_start_at_once()
    {
        var factory = _app.Services.GetRequiredService<IDbContextFactory<ExternalTaskDbContext>>();

        await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => new ExternalTaskSchemaInitializer(factory).StartAsync(CancellationToken.None)));

        await Seed("charge-card");
        Assert.HasCount(1, await Fetch("w1", "charge-card"));
    }

    [TestMethod]
    public async Task Unlock_waits_while_a_result_is_being_reported()
    {
        // Arrange — w1's complete is in flight inside the engine.
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card");
        var engineGate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        _callback.CompleteActivity(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<ExpandoObject>())
            .Returns(async _ => { entered.TrySetResult(); await engineGate.Task; });
        var complete = _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/complete", new { workerId = "w1" });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        var unlock = await _http.PostAsync($"/engine-rest/external-task/{task.Id}/unlock", null);
        var fetchedByOther = await Fetch("w2", "charge-card");
        engineGate.SetResult();
        var completed = await complete;

        // Assert — nothing interfered with the report, and the workflow saw exactly one outcome.
        await AssertError(unlock, HttpStatusCode.InternalServerError, "BadUserRequestException");
        Assert.HasCount(0, fetchedByOther);
        Assert.AreEqual(HttpStatusCode.NoContent, completed.StatusCode);
        await _callback.DidNotReceive().FailActivity(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Exception>());
    }

    [TestMethod]
    public async Task A_task_that_cannot_be_handed_out_does_not_break_the_fetch()
    {
        // Arrange — engine unreachable for one task; another one fails deterministically and the
        // attempt to fail its activity fails too.
        var transient = await Seed("charge-card");
        var poison = await Seed("charge-card");
        var healthy = await Seed("charge-card");
        _callback.GetVariablesIfActive(transient.ActivityInstanceId, Arg.Any<Guid>())
            .Returns<ValueTask<ExpandoObject?>>(_ => throw new TimeoutException("engine busy"));
        _callback.GetVariablesIfActive(poison.ActivityInstanceId, Arg.Any<Guid>())
            .Returns<ValueTask<ExpandoObject?>>(_ => throw new InvalidOperationException("unknown scope"));
        _callback.FailActivity(Arg.Any<string>(), poison.ActivityInstanceId, Arg.Any<Exception>())
            .Returns<Task>(_ => throw new TimeoutException("engine busy"));

        // Act
        var tasks = await Fetch("w1", "charge-card");

        // Assert — the healthy task is served; the other two are handed back, not left locked.
        Assert.AreEqual(healthy.Id, (string?)tasks.Single()!["id"]);
        foreach (var id in new[] { transient.Id, poison.Id })
        {
            var row = await Row(id);
            Assert.IsNotNull(row);
            Assert.IsNull(row.WorkerId);
            Assert.IsNull(row.LockExpirationTime);
        }
    }

    [TestMethod]
    public async Task Complete_rejected_by_the_engine_fails_the_activity()
    {
        // Arrange
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card");
        _callback.CompleteActivity(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<ExpandoObject>())
            .Returns<Task>(_ => throw new InvalidOperationException("outputs cannot be stored"));

        // Act
        var response = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/complete", new { workerId = "w1" });

        // Assert — failed visibly instead of being handed to another worker after the report lock.
        await AssertError(response, HttpStatusCode.BadRequest, "InvalidRequestException");
        await _callback.Received(1).FailActivity("ct1", task.ActivityInstanceId, Arg.Any<InvalidOperationException>());
        Assert.IsNull(await Row(task.Id));
    }

    [TestMethod]
    public async Task Complete_that_fails_transiently_can_be_sent_again()
    {
        // Arrange
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card");
        var calls = 0;
        _callback.CompleteActivity(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<ExpandoObject>())
            .Returns(_ => ++calls == 1 ? Task.FromException(new TimeoutException("engine busy")) : Task.CompletedTask);

        // Act
        var first = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/complete", new { workerId = "w1" });
        var second = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/complete", new { workerId = "w1" });

        // Assert
        Assert.AreEqual(HttpStatusCode.InternalServerError, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, second.StatusCode);
        await _callback.DidNotReceive().FailActivity(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Exception>());
    }

    [TestMethod]
    public async Task Complete_for_an_activity_that_already_ended_returns_404()
    {
        // Arrange — a boundary timer interrupted the task while the worker was busy.
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card");
        _processVariables = null;

        // Act
        var response = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/complete", new { workerId = "w1" });

        // Assert — the worker learns its result was not applied.
        await AssertError(response, HttpStatusCode.NotFound, "InvalidRequestException");
        await _callback.DidNotReceive().CompleteActivity(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<ExpandoObject>());
    }

    [TestMethod]
    public async Task Lock_respects_the_retry_backoff()
    {
        // Arrange — failed with retries left; waits 60 s before it may run again.
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card");
        await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/failure",
            new { workerId = "w1", errorMessage = "down", retries = 2, retryTimeout = 60_000 });

        // Act
        var lockByOther = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/lock",
            new { workerId = "w2", lockDuration = 10_000 });
        var fetchedByOther = await Fetch("w2", "charge-card");

        // Assert
        await AssertError(lockByOther, HttpStatusCode.BadRequest, "InvalidRequestException");
        Assert.HasCount(0, fetchedByOther);
    }

    [TestMethod]
    public async Task Process_definition_filter_finds_a_match_behind_many_other_tasks()
    {
        // Arrange — the only task of process B is queued behind 30 tasks of process A.
        for (var i = 0; i < 30; i++) await Seed("charge-card");
        var wanted = await Seed("charge-card", processDefinitionKey: "process-b");

        // Act
        var response = await _http.PostAsJsonAsync("/engine-rest/external-task/fetchAndLock", new
        {
            workerId = "w1",
            maxTasks = 1,
            topics = new[] { new { topicName = "charge-card", lockDuration = 10_000, processDefinitionKey = "process-b" } },
        });

        // Assert
        var tasks = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        Assert.AreEqual(wanted.Id, (string?)tasks.Single()!["id"]);
    }

    [TestMethod]
    public async Task Long_poll_wakes_up_as_soon_as_a_task_is_released()
    {
        // Arrange — w1 holds the only task; w2 long-polls.
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card");
        var waiting = _http.PostAsJsonAsync("/engine-rest/external-task/fetchAndLock", new
        {
            workerId = "w2",
            maxTasks = 1,
            asyncResponseTimeout = 30_000,
            topics = new[] { new { topicName = "charge-card", lockDuration = 10_000 } },
        });
        await Task.Delay(300);

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await _http.PostAsync($"/engine-rest/external-task/{task.Id}/unlock", null);
        var response = await waiting;

        // Assert — answered by the wake-up, not by the 2 s fallback poll.
        var tasks = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        Assert.AreEqual(task.Id, (string?)tasks.Single()!["id"]);
        Assert.IsLessThan(1500, stopwatch.ElapsedMilliseconds);
    }

    [TestMethod]
    public async Task Out_of_range_number_is_a_bad_request()
    {
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card");

        var response = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/complete", new
        {
            workerId = "w1",
            variables = new { n = new { value = "99999999999999999999", type = "Long" } },
        });

        await AssertError(response, HttpStatusCode.BadRequest, "InvalidRequestException");
    }

    [TestMethod]
    public async Task Schema_initializer_can_run_again_on_an_existing_table()
    {
        // Arrange — the host already created the table at startup; a second replica starts.
        await Seed("charge-card");
        var initializer = new ExternalTaskSchemaInitializer(
            _app.Services.GetRequiredService<IDbContextFactory<ExternalTaskDbContext>>());

        // Act
        await initializer.StartAsync(CancellationToken.None);

        // Assert — existing rows survive.
        Assert.HasCount(1, await Fetch("w1", "charge-card"));
    }

    [TestMethod]
    public async Task UsePriority_hands_out_higher_priority_tasks_first()
    {
        // Arrange — the oldest task has the lowest priority.
        var low = await Seed("charge-card", priority: 1);
        var high = await Seed("charge-card", priority: 50);
        var mid = await Seed("charge-card", priority: 10);

        // Act
        var byPriority = await Fetch("w1", "charge-card", usePriority: true);

        // Assert
        CollectionAssert.AreEqual(new[] { high.Id, mid.Id, low.Id }, Ids(byPriority));
        CollectionAssert.AreEqual(new long[] { 50, 10, 1 }, byPriority.Select(t => (long)t!["priority"]!).ToArray());
    }

    [TestMethod]
    public async Task UsePriority_null_is_accepted()
    {
        await Seed("charge-card");

        var response = await _http.PostAsync("/engine-rest/external-task/fetchAndLock", new StringContent(
            "{\"workerId\":\"w1\",\"maxTasks\":1,\"usePriority\":null,\"topics\":[{\"topicName\":\"charge-card\",\"lockDuration\":1000}]}",
            System.Text.Encoding.UTF8, "application/json"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task Without_usePriority_tasks_are_handed_out_oldest_first()
    {
        // Arrange
        var old = await Seed("charge-card", priority: 1);
        var young = await Seed("charge-card", priority: 50);

        // Act
        var first = await Fetch("w1", "charge-card", maxTasks: 1);

        // Assert
        CollectionAssert.AreEqual(new[] { old.Id }, Ids(first));
        Assert.IsNotNull(young);
    }

    [TestMethod]
    public async Task UsePriority_orders_across_topics()
    {
        // Arrange
        var card = await Seed("charge-card", priority: 1);
        var fraud = await Seed("check-fraud", priority: 5);

        // Act
        var response = await _http.PostAsJsonAsync("/engine-rest/external-task/fetchAndLock", new
        {
            workerId = "w1",
            maxTasks = 1,
            usePriority = true,
            topics = new[]
            {
                new { topicName = "charge-card", lockDuration = 10_000 },
                new { topicName = "check-fraud", lockDuration = 10_000 },
            },
        });

        // Assert
        var tasks = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        CollectionAssert.AreEqual(new[] { fraud.Id }, Ids(tasks));
        Assert.IsNotNull(card);
    }

    [TestMethod]
    public async Task The_priority_input_mapping_is_not_sent_to_the_worker_as_a_variable()
    {
        // Arrange
        await Seed("charge-card", inputs: [(ExternalTaskPriority.InputTarget, 7L)], priority: 7);

        // Act
        var tasks = await Fetch("w1", "charge-card", localVariables: true);

        // Assert
        Assert.AreEqual(7L, (long)tasks[0]!["priority"]!);
        Assert.HasCount(0, Names(tasks[0]!["variables"]!));
    }

    [TestMethod]
    public async Task Put_priority_changes_the_fetch_order()
    {
        // Arrange
        var first = await Seed("charge-card", priority: 10);
        var second = await Seed("charge-card", priority: 0);

        // Act
        var response = await _http.PutAsJsonAsync($"/engine-rest/external-task/{second.Id}/priority", new { priority = 100 });
        var tasks = await Fetch("w1", "charge-card", usePriority: true, maxTasks: 1);

        // Assert
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        CollectionAssert.AreEqual(new[] { second.Id }, Ids(tasks));
        Assert.AreEqual(100L, (await Row(second.Id))!.Priority);
        Assert.AreEqual(10L, (await Row(first.Id))!.Priority);
    }

    [TestMethod]
    public async Task Put_priority_follows_camunda_for_unknown_tasks_and_missing_values()
    {
        var task = await Seed("charge-card");

        await AssertError(
            await _http.PutAsJsonAsync($"/engine-rest/external-task/{Guid.NewGuid()}/priority", new { priority = 1 }),
            HttpStatusCode.NotFound, "InvalidRequestException");
        var omitted = await _http.PutAsJsonAsync($"/engine-rest/external-task/{task.Id}/priority", new { });
        Assert.AreEqual(HttpStatusCode.NoContent, omitted.StatusCode, "Camunda treats a missing priority as 0.");
    }

    [TestMethod]
    public async Task Schema_initializer_adds_the_columns_missing_from_a_0_6_0_table()
    {
        // Arrange — a table created by 0.6.0, before tasks had a priority.
        var factory = _app.Services.GetRequiredService<IDbContextFactory<ExternalTaskDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
            foreach (var column in new[] { "Priority", "BusinessKey", "TenantId" })
                await db.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE {ExternalTaskDbContext.TableName} DROP COLUMN \"{column}\"");

        // Act
        await new ExternalTaskSchemaInitializer(factory).StartAsync(CancellationToken.None);

        // Assert
        var task = await Seed("charge-card", priority: 3, businessKey: "order-1", tenantId: "acme");
        var row = (await Row(task.Id))!;
        Assert.AreEqual(3L, row.Priority);
        Assert.AreEqual("order-1", row.BusinessKey);
        Assert.AreEqual("acme", row.TenantId);
    }

    [TestMethod]
    public async Task Sorting_by_createTime_desc_hands_out_the_newest_first()
    {
        // Arrange
        var old = await Seed("charge-card");
        var young = await Seed("charge-card");

        // Act
        var tasks = await FetchSorted(usePriority: false, new { sortBy = "createTime", sortOrder = "desc" });

        // Assert
        CollectionAssert.AreEqual(new[] { young.Id, old.Id }, Ids(tasks));
    }

    [TestMethod]
    public async Task Sorting_by_createTime_breaks_priority_ties()
    {
        // Arrange
        var oldLow = await Seed("charge-card", priority: 1);
        var oldHigh = await Seed("charge-card", priority: 9);
        var youngHigh = await Seed("charge-card", priority: 9);

        // Act
        var asc = await FetchSorted(usePriority: true, new { sortBy = "createTime", sortOrder = "asc" });
        foreach (var id in Ids(asc))
            await _http.PostAsync($"/engine-rest/external-task/{id}/unlock", null);
        var desc = await FetchSorted(usePriority: true, new { sortBy = "createTime", sortOrder = "desc" });

        // Assert
        CollectionAssert.AreEqual(new[] { oldHigh.Id, youngHigh.Id, oldLow.Id }, Ids(asc));
        CollectionAssert.AreEqual(new[] { youngHigh.Id, oldHigh.Id, oldLow.Id }, Ids(desc));
    }

    [TestMethod]
    public async Task Unsupported_sorting_is_a_bad_request()
    {
        foreach (var sort in new object[] { new { sortBy = "priority", sortOrder = "asc" }, new { sortBy = "createTime", sortOrder = "up" } })
        {
            var response = await _http.PostAsJsonAsync("/engine-rest/external-task/fetchAndLock", new
            {
                workerId = "w1",
                maxTasks = 1,
                sorting = new[] { sort },
                topics = new[] { new { topicName = "charge-card", lockDuration = 1_000 } },
            });
            await AssertError(response, HttpStatusCode.BadRequest, "InvalidRequestException");
        }
    }

    [TestMethod]
    public async Task Extension_properties_come_from_reserved_inputs_and_are_not_variables()
    {
        // Arrange — a string and a number, as camunda:properties would carry.
        await Seed("charge-card", inputs:
        [
            ("extensionProperty_region", "eu-west"),
            ("extensionProperty_limit", 42L),
            ("who", "bob"),
        ]);

        // Act
        var without = await Fetch("w1", "charge-card", localVariables: true);
        await _http.PostAsync($"/engine-rest/external-task/{without[0]!["id"]}/unlock", null);
        var response = await _http.PostAsJsonAsync("/engine-rest/external-task/fetchAndLock", new
        {
            workerId = "w1",
            maxTasks = 1,
            topics = new[] { new { topicName = "charge-card", lockDuration = 10_000, localVariables = true, includeExtensionProperties = true } },
        });
        var with = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();

        // Assert
        Assert.HasCount(0, without[0]!["extensionProperties"]!.AsObject(), "Only returned on request, as in Camunda.");
        CollectionAssert.AreEquivalent(new[] { "who" }, Names(without[0]!["variables"]!));
        var properties = with[0]!["extensionProperties"]!;
        Assert.AreEqual("eu-west", (string?)properties["region"]);
        Assert.AreEqual("42", (string?)properties["limit"]);
        CollectionAssert.AreEquivalent(new[] { "who" }, Names(with[0]!["variables"]!));
    }

    [TestMethod]
    public async Task BusinessKey_and_tenantId_are_returned_and_filter_the_fetch()
    {
        // Arrange
        var acme = await Seed("charge-card", businessKey: "order-1", tenantId: "acme");
        var globex = await Seed("charge-card", businessKey: "order-2", tenantId: "globex");
        var noTenant = await Seed("charge-card", businessKey: "order-1");

        // Act
        var byKey = await FetchFiltered(new { businessKey = "order-1" });
        var byTenant = await FetchFiltered(new { tenantIdIn = new[] { "globex" } });
        var withoutTenant = await FetchFiltered(new { withoutTenantId = true });
        var either = await FetchFiltered(new { tenantIdIn = new[] { "acme" }, withoutTenantId = true });

        // Assert
        CollectionAssert.AreEquivalent(new[] { acme.Id, noTenant.Id }, Ids(byKey));
        CollectionAssert.AreEqual(new[] { globex.Id }, Ids(byTenant));
        Assert.AreEqual("order-2", (string?)byTenant[0]!["businessKey"]);
        Assert.AreEqual("globex", (string?)byTenant[0]!["tenantId"]);
        CollectionAssert.AreEqual(new[] { noTenant.Id }, Ids(withoutTenant));
        CollectionAssert.AreEquivalent(new[] { acme.Id, noTenant.Id }, Ids(either));
    }

    [TestMethod]
    public async Task Reserved_inputs_are_not_sent_as_variables()
    {
        // Arrange — the engine put every input, reserved ones included, into the task's scope.
        await Seed("charge-card", inputs:
        [
            (ReservedInputs.BusinessKey, "bob"),
            (ReservedInputs.TenantId, "acme"),
            (ExternalTaskPriority.InputTarget, 5L),
            ("extensionProperty_region", "eu-west"),
            ("who", "bob"),
        ]);

        // Act
        var local = await Fetch("w1", "charge-card", localVariables: true);
        await _http.PostAsync($"/engine-rest/external-task/{local[0]!["id"]}/unlock", null);
        var all = await Fetch("w1", "charge-card");

        // Assert
        CollectionAssert.AreEquivalent(new[] { "who" }, Names(local[0]!["variables"]!));
        CollectionAssert.AreEquivalent(new[] { "amount", "customer", "vip", "who" }, Names(all[0]!["variables"]!));
    }

    [TestMethod]
    public async Task File_variables_are_stored_returned_by_type_and_downloadable()
    {
        // Arrange — the worker completes with a File; the workflow then holds what Fleans stored.
        var task = await Seed("make-invoice");
        await Fetch("w1", "make-invoice");
        ExpandoObject? completed = null;
        await _callback.CompleteActivity(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Do<ExpandoObject>(v => completed = v));
        var complete = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/complete", new
        {
            workerId = "w1",
            variables = new Dictionary<string, object>
            {
                ["invoice"] = new
                {
                    value = Convert.ToBase64String("PDF-1.7"u8.ToArray()),
                    type = "File",
                    valueInfo = new { filename = "invoice.pdf", mimetype = "application/pdf", encoding = "" },
                },
            },
        });
        Assert.AreEqual(HttpStatusCode.NoContent, complete.StatusCode, await complete.Content.ReadAsStringAsync());
        _processVariables = Expando(("invoice", ((IDictionary<string, object?>)completed!)["invoice"]), ("customer", "bob"));
        var next = await Seed("send-invoice");

        // Act
        var fetched = await Fetch("w2", "send-invoice");
        var download = await _http.GetAsync($"/engine-rest/execution/{next.Id}/localVariables/invoice/data");
        var notBinary = await _http.GetAsync($"/engine-rest/execution/{next.Id}/localVariables/customer/data");
        var missing = await _http.GetAsync($"/engine-rest/execution/{next.Id}/localVariables/nope/data");

        // Assert
        var invoice = fetched[0]!["variables"]!["invoice"]!;
        Assert.AreEqual("File", (string?)invoice["type"]);
        Assert.AreEqual("invoice.pdf", (string?)invoice["valueInfo"]!["filename"]);
        Assert.AreEqual(HttpStatusCode.OK, download.StatusCode);
        Assert.AreEqual("application/pdf", download.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("PDF-1.7", await download.Content.ReadAsStringAsync());
        await AssertError(notBinary, HttpStatusCode.BadRequest, "InvalidRequestException");
        await AssertError(missing, HttpStatusCode.NotFound, "InvalidRequestException");
    }

    [TestMethod]
    public async Task With_incidents_a_failure_without_retries_parks_the_task_until_retries_are_set()
    {
        // Arrange
        EnableIncidents();
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card");

        // Act
        var failure = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/failure", new
        {
            workerId = "w1", errorMessage = "card declined", errorDetails = "stack…", retries = 0, retryTimeout = 0,
        });
        var whileIncident = await Fetch("w2", "charge-card");
        var incidents = await GetJson("/engine-rest/external-task?noRetriesLeft=true");
        var details = await _http.GetStringAsync($"/engine-rest/external-task/{task.Id}/errorDetails");
        var resolve = await _http.PutAsJsonAsync($"/engine-rest/external-task/{task.Id}/retries", new { retries = 2 });
        var afterResolve = await Fetch("w2", "charge-card");

        // Assert
        Assert.AreEqual(HttpStatusCode.NoContent, failure.StatusCode);
        await _callback.DidNotReceive().FailActivity(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Exception>());
        Assert.HasCount(0, whileIncident, "An incident is not fetchable.");
        Assert.HasCount(1, incidents.AsArray());
        Assert.AreEqual(0, (int)incidents[0]!["retries"]!);
        Assert.AreEqual("card declined", (string?)incidents[0]!["errorMessage"]);
        Assert.AreEqual("stack…", details);
        Assert.AreEqual(HttpStatusCode.NoContent, resolve.StatusCode);
        Assert.HasCount(1, afterResolve);
        Assert.AreEqual(2, (int)afterResolve[0]!["retries"]!);
    }

    [TestMethod]
    public async Task Without_incidents_setting_retries_to_zero_fails_the_activity()
    {
        // Arrange
        var task = await Seed("charge-card");

        // Act
        var response = await _http.PutAsJsonAsync($"/engine-rest/external-task/{task.Id}/retries", new { retries = 0 });

        // Assert
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        await _callback.Received(1).FailActivity("ct1", task.ActivityInstanceId,
            Arg.Is<ActivityException>(e => e.GetActivityErrorState().Code == ExternalTaskService.RetriesExhaustedErrorCode));
        Assert.IsNull(await Row(task.Id));
    }

    [TestMethod]
    public async Task With_incidents_setting_retries_to_zero_creates_an_incident()
    {
        EnableIncidents();
        var task = await Seed("charge-card");

        var response = await _http.PutAsJsonAsync($"/engine-rest/external-task/{task.Id}/retries", new { retries = 0 });

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.AreEqual(0, (await Row(task.Id))!.Retries);
        Assert.HasCount(0, await Fetch("w1", "charge-card"));
        await _callback.DidNotReceive().FailActivity(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Exception>());
    }

    [TestMethod]
    public async Task Query_api_lists_counts_and_gets_tasks()
    {
        // Arrange
        var card = await Seed("charge-card", priority: 4, businessKey: "order-1");
        await Seed("charge-card");
        var fraud = await Seed("check-fraud");
        await Fetch("w1", "check-fraud");

        // Act
        var all = await GetJson("/engine-rest/external-task");
        var page = await GetJson("/engine-rest/external-task?topicName=charge-card&firstResult=0&maxResults=1");
        var locked = await GetJson("/engine-rest/external-task?locked=true");
        var byInstance = await GetJson($"/engine-rest/external-task?processInstanceId={card.WorkflowInstanceId}");
        var count = await GetJson("/engine-rest/external-task/count?topicName=charge-card");
        var single = await GetJson($"/engine-rest/external-task/{card.Id}");
        var topics = await GetJson("/engine-rest/external-task/topic-names?withUnlockedTasks=true");
        var missing = await _http.GetAsync($"/engine-rest/external-task/{Guid.NewGuid()}");

        // Assert
        Assert.HasCount(3, all.AsArray());
        CollectionAssert.AreEqual(new[] { card.Id }, Ids(page.AsArray()));
        CollectionAssert.AreEqual(new[] { fraud.Id }, Ids(locked.AsArray()));
        Assert.AreEqual("w1", (string?)locked[0]!["workerId"]);
        CollectionAssert.AreEqual(new[] { card.Id }, Ids(byInstance.AsArray()));
        Assert.AreEqual(2L, (long)count["count"]!);
        Assert.AreEqual("order-1", (string?)single["businessKey"]);
        Assert.AreEqual(4L, (long)single["priority"]!);
        Assert.IsFalse((bool)single["suspended"]!);
        CollectionAssert.AreEqual(new[] { "charge-card" }, topics.AsArray().Select(n => (string)n!).ToArray());
        await AssertError(missing, HttpStatusCode.NotFound, "InvalidRequestException");
    }

    [TestMethod]
    public async Task Negative_retries_are_a_bad_request()
    {
        var task = await Seed("charge-card");

        var response = await _http.PutAsJsonAsync($"/engine-rest/external-task/{task.Id}/retries", new { retries = -1 });

        await AssertError(response, HttpStatusCode.BadRequest, "InvalidRequestException");
    }

    [TestMethod]
    public async Task An_incident_whose_activity_ended_is_removed_by_the_operator_queries()
    {
        // Arrange — an incident, then the workflow is cancelled.
        EnableIncidents();
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card");
        await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/failure",
            new { workerId = "w1", errorMessage = "declined", retries = 0, retryTimeout = 0 });
        _processVariables = null;

        // Act
        var list = await GetJson("/engine-rest/external-task?noRetriesLeft=true");
        var single = await _http.GetAsync($"/engine-rest/external-task/{task.Id}");

        // Assert
        Assert.HasCount(0, list.AsArray());
        await AssertError(single, HttpStatusCode.NotFound, "InvalidRequestException");
        Assert.IsNull(await Row(task.Id));
    }

    [TestMethod]
    public async Task An_incident_cannot_be_locked()
    {
        EnableIncidents();
        var task = await Seed("charge-card");
        await _http.PutAsJsonAsync($"/engine-rest/external-task/{task.Id}/retries", new { retries = 0 });

        var response = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/lock",
            new { workerId = "w1", lockDuration = 10_000 });

        await AssertError(response, HttpStatusCode.BadRequest, "InvalidRequestException");
        Assert.IsNull((await Row(task.Id))!.WorkerId);
    }

    [TestMethod]
    public async Task BpmnError_whose_variables_the_engine_rejects_fails_the_activity()
    {
        // Arrange — the engine refuses the variables outright (not a transient error).
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card");
        _callback.FailActivity(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Exception>(), Arg.Any<ExpandoObject?>())
            .Returns(Task.FromException(new InvalidOperationException("cannot store")));

        // Act
        var response = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/bpmnError", new
        {
            workerId = "w1",
            errorCode = "DECLINED",
            variables = new Dictionary<string, object> { ["x"] = new { value = "y", type = "String" } },
        });

        // Assert — no 500 loop: the activity is failed and the task is gone.
        await AssertError(response, HttpStatusCode.BadRequest, "InvalidRequestException");
        await _callback.Received(1).FailActivity("ct1", task.ActivityInstanceId, Arg.Any<InvalidOperationException>());
        Assert.IsNull(await Row(task.Id));
    }

    [TestMethod]
    public async Task Extension_properties_use_camunda_text_for_booleans_and_json_for_objects()
    {
        _processVariables = Expando(("express", true), ("order", Expando(("id", 7L))));
        await Seed("charge-card", inputs:
        [
            ("extensionProperty_express", true),
            ("extensionProperty_order", Expando(("id", 7L))),
        ]);

        var response = await _http.PostAsJsonAsync("/engine-rest/external-task/fetchAndLock", new
        {
            workerId = "w1",
            maxTasks = 1,
            topics = new[] { new { topicName = "charge-card", lockDuration = 10_000, includeExtensionProperties = true } },
        });

        var properties = JsonNode.Parse(await response.Content.ReadAsStringAsync())![0]!["extensionProperties"]!;
        Assert.AreEqual("true", (string?)properties["express"]);
        Assert.AreEqual("{\"id\":7}", (string?)properties["order"]);
    }

    private void EnableIncidents() =>
        _app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CamundaExternalTaskOptions>>().Value.Incidents = true;

    private async Task<JsonNode> GetJson(string url)
    {
        var response = await _http.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    /// <summary>Fetches with the given topic filters, then unlocks what it got.</summary>
    private async Task<JsonArray> FetchFiltered(object filters)
    {
        var topic = JsonSerializer.SerializeToNode(filters)!.AsObject();
        topic["topicName"] = "charge-card";
        topic["lockDuration"] = 10_000;
        var response = await _http.PostAsJsonAsync("/engine-rest/external-task/fetchAndLock",
            new JsonObject { ["workerId"] = "w1", ["maxTasks"] = 10, ["topics"] = new JsonArray(topic) });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var tasks = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        foreach (var id in Ids(tasks))
            await _http.PostAsync($"/engine-rest/external-task/{id}/unlock", null);
        return tasks;
    }

    private async Task<JsonArray> FetchSorted(bool usePriority, object sort)
    {
        var response = await _http.PostAsJsonAsync("/engine-rest/external-task/fetchAndLock", new
        {
            workerId = "w1",
            maxTasks = 10,
            usePriority,
            sorting = new[] { sort },
            topics = new[] { new { topicName = "charge-card", lockDuration = 10_000 } },
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
    }

    private static string[] Ids(JsonArray tasks) => tasks.Select(t => (string)t!["id"]!).ToArray();

    private async Task<ExternalTaskEntity> Seed(
        string topic, (string Target, object? Value)[]? inputs = null,
        string processDefinitionKey = "order-process", long priority = 0,
        string? businessKey = null, string? tenantId = null)
    {
        // Like the engine: the resolved inputs live in the task's scope, the row keeps their targets.
        foreach (var (target, value) in inputs ?? [])
            ((IDictionary<string, object?>)_processVariables!)[target] = value;
        var activityInstanceId = Guid.NewGuid();
        var entity = new ExternalTaskEntity
        {
            Id = activityInstanceId.ToString("D"),
            TopicName = topic,
            WorkflowInstanceId = Guid.NewGuid(),
            ProcessDefinitionKey = processDefinitionKey,
            ProcessDefinitionId = "order-process:1",
            ActivityId = "ct1",
            ActivityInstanceId = activityInstanceId,
            VariablesId = Guid.NewGuid(),
            InputTargetsJson = JsonSerializer.Serialize((inputs ?? []).Select(i => i.Target).ToList()),
            Priority = priority,
            BusinessKey = businessKey,
            TenantId = tenantId,
            CreateTime = _clock.GetUtcNow().UtcDateTime,
        };
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await using var scope = _app.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ExternalTaskDbContext>>().CreateDbContextAsync();
        db.ExternalTasks.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    private async Task<List<ExternalTaskEntity>> Rows()
    {
        await using var db = await _app.Services.GetRequiredService<IDbContextFactory<ExternalTaskDbContext>>()
            .CreateDbContextAsync();
        return await db.ExternalTasks.AsNoTracking().ToListAsync();
    }

    private async Task<ExternalTaskEntity?> Row(string id) => (await Rows()).SingleOrDefault(r => r.Id == id);

    private async Task<JsonArray> Fetch(
        string workerId, string topic, long lockDuration = 10_000, string[]? variables = null, bool localVariables = false,
        bool usePriority = false, int maxTasks = 10)
    {
        var response = await _http.PostAsJsonAsync("/engine-rest/external-task/fetchAndLock", new
        {
            workerId,
            maxTasks,
            usePriority,
            topics = new[] { new { topicName = topic, lockDuration, variables, localVariables } },
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
    }

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string type)
    {
        Assert.AreEqual(status, response.StatusCode);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.ToString());
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.AreEqual(type, (string?)body["type"]);
        Assert.IsFalse(string.IsNullOrEmpty((string?)body["message"]));
    }

    private static string[] Names(JsonNode variables) => variables.AsObject().Select(kv => kv.Key).ToArray();

    private static ExpandoObject Expando(params (string Name, object? Value)[] values)
    {
        var expando = new ExpandoObject();
        foreach (var (name, value) in values)
            ((IDictionary<string, object?>)expando)[name] = value;
        return expando;
    }

    private sealed class ManualClock(DateTime start) : TimeProvider
    {
        private DateTimeOffset _now = new(start);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
