using System.Collections.Concurrent;
using System.Dynamic;
using Fleans.ExternalTasks.Sdk;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fleans.Plugins.CamundaExternalTask.Tests;

/// <summary>
/// The engine side of the plugin: a delivered task reaches <see cref="CamundaExternalTaskHandler"/>,
/// which queues it and defers, and worker reports reach <see cref="IFleansEngineClient"/>. The engine
/// is a fake; delivery over gRPC is covered by the SDK's tests in the engine repo.
/// </summary>
[TestClass]
[DoNotParallelize]
public class CamundaExternalTaskHandlerTests
{
    private const string ConnectionString = "DataSource=file:handler-tests?mode=memory&cache=shared";
    private static SqliteConnection _keepAlive = null!;
    private static ServiceProvider _services = null!;
    private static readonly FakeEngine Engine = new();

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        _keepAlive = new SqliteConnection(ConnectionString);
        _keepAlive.Open();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFleansEngineClient>(Engine);
        services.AddCamundaExternalTasks(o => o.UseSqlite(ConnectionString));
        _services = services.BuildServiceProvider();
        foreach (var hosted in _services.GetServices<IHostedService>())
            await hosted.StartAsync(default);
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        await _services.DisposeAsync();
        _keepAlive.Dispose();
    }

    private static ExternalTaskService Service => _services.GetRequiredService<ExternalTaskService>();

    [TestMethod]
    public async Task Delivered_task_is_queued_fetched_and_completed_back_into_the_workflow()
    {
        // Arrange — an active activity whose scope holds amount=42 and the engine-resolved input.
        var task = NewTask("charge-card", inputs: [("toCharge", 42L)]);
        Engine.Activate(task, ("amount", 42L));

        // Act
        var outcome = await Deliver(task);
        var locked = (await Service.FetchAndLock(Fetch("charge-card"), default)).Single();
        await Service.Complete(locked.Id, new CompleteExternalTaskDto("w1",
            new Dictionary<string, VariableValueDto> { ["charged"] = new(true, "Boolean", null) }, null));

        // Assert
        Assert.AreSame(ExternalTaskHandler.Deferred, outcome, "The activity stays open until a worker reports.");
        Assert.AreEqual(task.Context.WorkflowInstanceId.ToString("D"), locked.ProcessInstanceId);
        Assert.AreEqual("charge-card", locked.TopicName);
        Assert.AreEqual(42, locked.Variables["toCharge"].Value);
        Assert.Contains($"complete:{task.Context.ActivityId}:charged=True", Engine.Calls);
        Assert.AreEqual(0, await RowCount(task));
    }

    [TestMethod]
    public async Task BpmnError_fails_the_activity_with_its_error_code()
    {
        // Arrange
        var task = NewTask("check-fraud");
        Engine.Activate(task);
        await Deliver(task);
        var locked = (await Service.FetchAndLock(Fetch("check-fraud"), default)).Single();

        // Act
        await Service.HandleBpmnError(locked.Id, new ExternalTaskBpmnErrorDto("w1", "FRAUD", "fishy",
            new Dictionary<string, VariableValueDto> { ["score"] = new(97, "Integer", null) }));

        // Assert
        Assert.Contains($"fail-vars:{task.Context.ActivityId}:score=97", Engine.Calls);
        Assert.Contains($"fail:{task.Context.ActivityId}:FRAUD", Engine.Calls);
    }

    [TestMethod]
    public async Task Duplicate_delivery_of_a_waiting_task_keeps_a_single_row()
    {
        // Arrange
        var task = NewTask("charge-card-twice");
        Engine.Activate(task);

        // Act
        var first = await Deliver(task);
        var second = await Deliver(task);

        // Assert
        Assert.AreSame(ExternalTaskHandler.Deferred, first);
        Assert.AreSame(ExternalTaskHandler.Deferred, second);
        Assert.AreEqual(1, await RowCount(task));
    }

    [TestMethod]
    public async Task Priority_input_is_read_when_the_task_is_queued()
    {
        // Arrange — a bare BPMN literal arrives as a string, an expression as a number.
        var literal = NewTask("ship-order", inputs: [(ExternalTaskPriority.InputTarget, "10")]);
        var expression = NewTask("ship-order", inputs: [(ExternalTaskPriority.InputTarget, 25L)]);
        Engine.Activate(literal);
        Engine.Activate(expression);

        // Act
        await Deliver(literal);
        await Deliver(expression);
        var tasks = await Service.FetchAndLock(
            Fetch("ship-order") with { MaxTasks = 2, UsePriority = true }, default);

        // Assert
        CollectionAssert.AreEqual(new long[] { 25, 10 }, tasks.Select(t => t.Priority).ToArray());
        CollectionAssert.AreEqual(
            new[] { expression.Context.WorkflowInstanceId.ToString("D"), literal.Context.WorkflowInstanceId.ToString("D") },
            tasks.Select(t => t.ProcessInstanceId).ToArray());
    }

    [TestMethod]
    public async Task Business_key_and_tenant_id_are_read_when_the_task_is_queued()
    {
        // Arrange
        var task = NewTask("bill-order-1", inputs:
        [
            (ReservedInputs.BusinessKey, 1001L),
            (ReservedInputs.TenantId, "acme"),
        ]);
        Engine.Activate(task);

        // Act
        await Deliver(task);
        var locked = (await Service.FetchAndLock(Fetch("bill-order-1"), default)).Single();

        // Assert
        Assert.AreEqual("1001", locked.BusinessKey);
        Assert.AreEqual("acme", locked.TenantId);
    }

    [TestMethod]
    public async Task Business_key_is_the_instance_business_id()
    {
        // Arrange
        var task = NewTask("bill-order-2", businessId: "order-1001");
        Engine.Activate(task);

        // Act
        await Deliver(task);
        var locked = (await Service.FetchAndLock(Fetch("bill-order-2"), default)).Single();

        // Assert
        Assert.AreEqual("order-1001", locked.BusinessKey);
    }

    [TestMethod]
    public async Task Business_key_input_overrides_the_instance_business_id()
    {
        // Arrange
        var task = NewTask("bill-order-3", inputs: [(ReservedInputs.BusinessKey, "invoice-7")], businessId: "order-1001");
        Engine.Activate(task);

        // Act
        await Deliver(task);
        var locked = (await Service.FetchAndLock(Fetch("bill-order-3"), default)).Single();

        // Assert
        Assert.AreEqual("invoice-7", locked.BusinessKey);
    }

    [TestMethod]
    public async Task Business_key_input_evaluating_to_null_still_replaces_the_instance_business_id()
    {
        // Arrange — the process opts out of keying its tasks
        var task = NewTask("bill-order-4", inputs: [(ReservedInputs.BusinessKey, null)], businessId: "order-1001");
        Engine.Activate(task);

        // Act
        await Deliver(task);
        var locked = (await Service.FetchAndLock(Fetch("bill-order-4"), default)).Single();

        // Assert
        Assert.IsNull(locked.BusinessKey);
    }

    [TestMethod]
    public async Task Business_key_is_null_without_input_or_instance_business_id()
    {
        // Arrange
        var task = NewTask("bill-order-5");
        Engine.Activate(task);

        // Act
        await Deliver(task);
        var locked = (await Service.FetchAndLock(Fetch("bill-order-5"), default)).Single();

        // Assert
        Assert.IsNull(locked.BusinessKey);
    }

    [TestMethod]
    public async Task Instance_business_id_too_long_for_the_column_fails_the_task_instead_of_queuing_it()
    {
        // Arrange — guards the column even if the engine ever allows longer business ids
        var task = NewTask("bill-order-6", businessId: new string('x', ReservedInputs.MaxLength + 1));

        // Act
        var ex = await Assert.ThrowsExactlyAsync<ExternalTaskFailedException>(() => Deliver(task));

        // Assert
        Assert.AreEqual("400", ex.Code);
        Assert.AreEqual(0, await RowCount(task));
    }

    [TestMethod]
    public async Task Business_key_too_long_for_the_column_fails_the_task_instead_of_queuing_it()
    {
        // Arrange
        var task = NewTask("bill-order-7",
            inputs: [(ReservedInputs.BusinessKey, new string('x', ReservedInputs.MaxLength + 1))]);

        // Act
        var ex = await Assert.ThrowsExactlyAsync<ExternalTaskFailedException>(() => Deliver(task));

        // Assert
        Assert.AreEqual("400", ex.Code);
        Assert.AreEqual(0, await RowCount(task));
    }

    [TestMethod]
    public async Task Priority_that_is_not_a_number_fails_the_task_instead_of_queuing_it()
    {
        // Arrange
        var task = NewTask("ship-order-bad", inputs: [(ExternalTaskPriority.InputTarget, "urgent")]);

        // Act
        var ex = await Assert.ThrowsExactlyAsync<ExternalTaskFailedException>(() => Deliver(task));

        // Assert
        Assert.AreEqual("400", ex.Code);
        Assert.AreEqual(0, await RowCount(task));
    }

    [TestMethod]
    public async Task Task_type_without_a_topic_fails_the_task()
    {
        // Arrange
        var task = NewTask("");

        // Act
        var ex = await Assert.ThrowsExactlyAsync<ExternalTaskFailedException>(() => Deliver(task));

        // Assert
        Assert.AreEqual("400", ex.Code);
    }

    private static DeliveredTask NewTask(
        string topic, (string Target, object? Value)[]? inputs = null, string? businessId = null)
    {
        var resolved = new Dictionary<string, object?>();
        foreach (var (target, value) in inputs ?? [])
            resolved[target] = value;
        return new DeliveredTask(resolved, new ExternalTaskContext(
            WorkflowInstanceId: Guid.NewGuid(),
            WorkflowId: "order-process",
            ProcessDefinitionId: "order-process:1",
            ActivityInstanceId: Guid.NewGuid(),
            ActivityId: "task-" + topic,
            TaskType: CamundaExternalTaskConventions.TaskTypeFor(topic),
            BusinessId: businessId));
    }

    /// <summary>Runs the handler as the SDK's worker does: resolved from a fresh scope.</summary>
    private static async Task<IDictionary<string, object?>> Deliver(DeliveredTask task)
    {
        await using var scope = _services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<CamundaExternalTaskHandler>();
        return await handler.ExecuteAsync(task.Inputs, new ExpandoObject(), task.Context, default);
    }

    private static async Task<int> RowCount(DeliveredTask task)
    {
        await using var db = await _services
            .GetRequiredService<IDbContextFactory<ExternalTaskDbContext>>().CreateDbContextAsync();
        return await db.ExternalTasks.CountAsync(t => t.WorkflowInstanceId == task.Context.WorkflowInstanceId);
    }

    private static FetchExternalTasksDto Fetch(string topic) =>
        new("w1", MaxTasks: 1, AsyncResponseTimeout: 10_000,
            [new FetchExternalTaskTopicDto(topic, 60_000, null, false, null, null, null, null)]);

    private sealed record DeliveredTask(Dictionary<string, object?> Inputs, ExternalTaskContext Context);

    /// <summary>Stands in for the engine's REST API.</summary>
    private sealed class FakeEngine : IFleansEngineClient
    {
        private readonly ConcurrentDictionary<Guid, ExpandoObject> _active = new();
        public ConcurrentBag<string> Calls { get; } = [];

        /// <summary>Activates the task's activity; like the engine, its scope also holds the inputs.</summary>
        public void Activate(DeliveredTask task, params (string Name, object? Value)[] variables)
        {
            var scope = (IDictionary<string, object?>)new ExpandoObject();
            foreach (var (name, value) in variables)
                scope[name] = value;
            foreach (var (name, value) in task.Inputs)
                scope[name] = value;
            _active[task.Context.ActivityInstanceId] = (ExpandoObject)scope;
        }

        public Task<ExpandoObject?> GetActiveActivityVariables(
            Guid workflowInstanceId, Guid activityInstanceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_active.GetValueOrDefault(activityInstanceId));

        public Task CompleteActivity(
            Guid workflowInstanceId, string activityId, Guid activityInstanceId, ExpandoObject variables,
            CancellationToken cancellationToken = default)
        {
            _active.TryRemove(activityInstanceId, out _);
            Calls.Add($"complete:{activityId}:{Format(variables)}");
            return Task.CompletedTask;
        }

        public Task FailActivity(
            Guid workflowInstanceId, string activityId, Guid activityInstanceId, string code, string message,
            ExpandoObject? variables = null, CancellationToken cancellationToken = default)
        {
            _active.TryRemove(activityInstanceId, out _);
            if (variables is not null && Format(variables).Length > 0)
                Calls.Add($"fail-vars:{activityId}:{Format(variables)}");
            Calls.Add($"fail:{activityId}:{code}");
            return Task.CompletedTask;
        }

        private static string Format(ExpandoObject variables) =>
            string.Join(",", ((IDictionary<string, object?>)variables).Select(kv => $"{kv.Key}={kv.Value}"));
    }
}
