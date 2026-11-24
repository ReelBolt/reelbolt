using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Services;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Messaging;
using ReelBolt.WorkflowEngine.Tests.Execution;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A deploy must not kill a running job. A deploy stops the engine, StopAsync
/// cancelled the in-flight run and the executor wrote it <c>Cancelled</c>, a terminal state the recovery sweeper never
/// looks at, so the one automatic resume did not apply. A shutdown cancel must leave the row <c>Running</c>
/// for the sweeper; a user or plan stop must still end <c>Cancelled</c>.
/// </summary>
public sealed class ExecutionShutdownHandoverTests
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Blocks until its token is cancelled, then throws, as a real long step does.</summary>
    private sealed class BlockingExecutor : IStepExecutor
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public StepType StepType => StepType.Agent;

        public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _database = Guid.NewGuid().ToString();

        public ServiceProvider Services { get; }
        public BlockingExecutor Step { get; } = new();
        public Mock<IWorkflowEventPublisher> Events { get; } = new();
        public Mock<IPublishEndpoint> Publish { get; } = new();
        public List<WorkflowExecutionRequested> Published { get; } = new();
        public Guid OrganizationId { get; } = Guid.NewGuid();
        public ExecutionCancellationRegistry Registry => Services.GetRequiredService<ExecutionCancellationRegistry>();
        public WorkflowExecutionRunner Runner => Services.GetRequiredService<WorkflowExecutionRunner>();

        public Rig()
        {
            var gate = new Mock<ICreditGate>();
            gate.Setup(g => g.ReserveAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<Guid?>(),
                    It.IsAny<CreditEntitlement>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<long?>()))
                .ReturnsAsync(new CreditReservation(CreditReserveOutcome.Allowed, null));
            Publish.Setup(p => p.Publish(It.IsAny<WorkflowExecutionRequested>(), It.IsAny<CancellationToken>()))
                .Callback<WorkflowExecutionRequested, CancellationToken>((m, _) => Published.Add(m))
                .Returns(Task.CompletedTask);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["WorkflowEngine:MaxConcurrency"] = "2" })
                .Build());
            services.AddDbContext<WorkflowEngineDbContext>(o => o
                .UseInMemoryDatabase(_database)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddSingleton<ExecutionCancellationRegistry>();
            services.AddSingleton<IDecisionQuestionMemoizer, DecisionQuestionMemoizer>();
            services.AddSingleton<IPlatformSwitches, TestPlatformSwitches>();
            services.AddSingleton(sp => new ExecutionAdmissionGuard(
                sp.GetRequiredService<IPlatformSwitches>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ExecutionAdmissionGuard>.Instance));
            services.AddSingleton(Publish.Object);
            services.AddScoped(sp => new WorkflowExecutorService(
                scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
                eventPublisher: Events.Object,
                logger: NullLogger<WorkflowExecutorService>.Instance,
                executors: new IStepExecutor[] { Step },
                rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
                hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
                cancellationRegistry: sp.GetRequiredService<ExecutionCancellationRegistry>(),
                creditGate: gate.Object));
            services.AddSingleton<WorkflowExecutionRunner>();
            Services = services.BuildServiceProvider();
        }

        public WorkflowEngineDbContext NewDb() =>
            Services.CreateScope().ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

        public Guid AddQueuedExecution(int autoResumeCount = 0)
        {
            using WorkflowEngineDbContext db = NewDb();
            Guid projectId = Guid.NewGuid();
            AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "Agent", AgentType = AgentType.VideoStoryEditor, SystemPrompt = "" };
            WorkflowDefinition definition = new() { Id = Guid.NewGuid(), Name = "w", ProjectId = projectId };
            definition.Steps.Add(new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = definition.Id,
                StepOrder = 1,
                StepType = StepType.Agent,
                AgentDefinitionId = agent.Id
            });
            WorkflowExecution execution = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = definition.Id,
                ProjectId = projectId,
                OrganizationId = OrganizationId,
                Status = ExecutionStatus.Queued,
                CorrelationId = "corr-" + Guid.NewGuid().ToString("N")[..6],
                AutoResumeCount = autoResumeCount
            };
            db.AgentDefinitions.Add(agent);
            db.WorkflowDefinitions.Add(definition);
            db.WorkflowExecutions.Add(execution);
            db.SaveChanges();
            return execution.Id;
        }

        /// <summary>Dispatches the run and waits until its step is blocking.</summary>
        public async Task StartAsync(Guid id)
        {
            AdmissionDecision decision = await Runner.DispatchAsync(id, "corr", CancellationToken.None, organizationId: OrganizationId);
            decision.Admitted.Should().BeTrue();
            await Step.Started.WaitAsync(TimeSpan.FromSeconds(20));
        }

        public async Task WaitForRunnerIdleAsync()
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (Runner.RunningCount > 0)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("The run did not end within 30 s.");
                await Task.Delay(10);
            }
        }

        public WorkflowExecution Get(Guid id)
        {
            using WorkflowEngineDbContext db = NewDb();
            return db.WorkflowExecutions.AsNoTracking().Single(e => e.Id == id);
        }

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    // ── the registry's provenance rules ────────────────────────────────────

    [Fact]
    public void A_shutdown_cancel_records_the_shutdown_reason_marks_the_run_and_trips_the_token()
    {
        ExecutionCancellationRegistry registry = new();
        Guid id = Guid.NewGuid();
        using CancellationTokenSource cts = new();
        registry.Register(id, cts);

        registry.TryCancelForShutdown(id).Should().BeTrue();

        cts.IsCancellationRequested.Should().BeTrue();
        registry.IsShutdown(id).Should().BeTrue();
        registry.GetReason(id).Should().Be(ExecutionCancellationRegistry.ShutdownReason);
    }

    [Fact]
    public void A_user_stop_is_never_a_shutdown()
    {
        ExecutionCancellationRegistry registry = new();
        Guid id = Guid.NewGuid();
        using CancellationTokenSource cts = new();
        registry.Register(id, cts);

        registry.TryCancel(id, "Stopped by user 123");

        registry.IsShutdown(id).Should().BeFalse();
    }

    [Fact]
    public void A_stop_recorded_before_the_shutdown_stands()
    {
        ExecutionCancellationRegistry registry = new();
        Guid id = Guid.NewGuid();
        using CancellationTokenSource cts = new();
        registry.Register(id, cts);
        registry.TryCancel(id, "Stopped by user 123");

        registry.TryCancelForShutdown(id);

        registry.IsShutdown(id).Should().BeFalse("the user already stopped the run; the shutdown must not turn it into a resumable one");
        registry.GetReason(id).Should().Be("Stopped by user 123");
    }

    [Fact]
    public void A_stop_that_arrives_after_the_shutdown_wins()
    {
        ExecutionCancellationRegistry registry = new();
        Guid id = Guid.NewGuid();
        using CancellationTokenSource cts = new();
        registry.Register(id, cts);
        registry.TryCancelForShutdown(id);

        registry.TryCancel(id, "Stopped by user 123");

        registry.IsShutdown(id).Should().BeFalse();
        registry.GetReason(id).Should().Be("Stopped by user 123");
    }

    [Fact]
    public void A_new_registration_and_a_removal_clear_the_shutdown_mark()
    {
        ExecutionCancellationRegistry registry = new();
        Guid id = Guid.NewGuid();
        using CancellationTokenSource first = new();
        registry.Register(id, first);
        registry.TryCancelForShutdown(id);
        using CancellationTokenSource second = new();
        registry.Register(id, second);
        registry.IsShutdown(id).Should().BeFalse("a fresh run of the same id starts clean");

        registry.TryCancelForShutdown(id);
        registry.Remove(id);
        registry.IsShutdown(id).Should().BeFalse();
    }

    // ── the executor's outcome ──────────────────────────────────────────────

    [Fact]
    public async Task Stopping_the_engine_leaves_the_run_Running_and_publishes_no_failure()
    {
        await using Rig rig = new();
        Guid id = rig.AddQueuedExecution();
        await rig.StartAsync(id);

        await rig.Runner.StopAsync(CancellationToken.None);
        await rig.WaitForRunnerIdleAsync();

        WorkflowExecution row = rig.Get(id);
        row.Status.Should().Be(ExecutionStatus.Running, "a shutdown is not a verdict: the sweeper re-queues the run");
        row.CompletedAt.Should().BeNull();
        row.ErrorMessage.Should().BeNull();
        rig.Events.Verify(e => e.PublishExecutionFailedAsync(It.IsAny<WorkflowExecution>(), It.IsAny<CancellationToken>()), Times.Never);
        rig.Events.Verify(e => e.PublishExecutionCompletedAsync(It.IsAny<WorkflowExecution>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_user_stop_still_ends_the_run_Cancelled()
    {
        await using Rig rig = new();
        Guid id = rig.AddQueuedExecution();
        await rig.StartAsync(id);

        rig.Registry.TryCancel(id, "Stopped by user 7f1c").Should().BeTrue();
        await rig.WaitForRunnerIdleAsync();

        WorkflowExecution row = rig.Get(id);
        row.Status.Should().Be(ExecutionStatus.Cancelled);
        row.ErrorMessage.Should().Be(FailureMessages.UserStopMessage);
        row.CompletedAt.Should().NotBeNull();
        rig.Events.Verify(e => e.PublishExecutionFailedAsync(It.IsAny<WorkflowExecution>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_plan_style_stop_still_ends_the_run_Cancelled()
    {
        await using Rig rig = new();
        Guid id = rig.AddQueuedExecution();
        await rig.StartAsync(id);

        rig.Registry.TryCancel(id, "Stopped: the organization's plan no longer allows this run.");
        await rig.WaitForRunnerIdleAsync();

        rig.Get(id).Status.Should().Be(ExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task A_user_stop_racing_the_shutdown_still_wins()
    {
        await using Rig rig = new();
        Guid id = rig.AddQueuedExecution();
        await rig.StartAsync(id);

        // The registry's token is tripped by the first cancel; the later user stop overwrites the provenance
        // before the executor reads it only if it arrives first, so cancel through the user path first.
        rig.Registry.TryCancel(id, "Stopped by user 7f1c");
        await rig.Runner.StopAsync(CancellationToken.None);
        await rig.WaitForRunnerIdleAsync();

        rig.Get(id).Status.Should().Be(ExecutionStatus.Cancelled);
    }

    // ── the sweeper picks the interrupted run up ────────────────────────────

    private static (ExecutionRecoverySweeper Sweeper, FakeTime Time) NewSweeper(Rig rig)
    {
        FakeTime time = new();
        IServiceScopeFactory factory = rig.Services.GetRequiredService<IServiceScopeFactory>();
        ExecutionLeaseService leases = new(factory, new ConfigurationBuilder().Build(), NullLogger<ExecutionLeaseService>.Instance, time);
        return (new ExecutionRecoverySweeper(factory, leases, NullLogger<ExecutionRecoverySweeper>.Instance, time), time);
    }

    [Fact]
    public async Task The_sweeper_requeues_a_shutdown_interrupted_run_once_as_an_automatic_resume()
    {
        await using Rig rig = new();
        Guid id = rig.AddQueuedExecution();
        await rig.StartAsync(id);
        await rig.Runner.StopAsync(CancellationToken.None);
        await rig.WaitForRunnerIdleAsync();
        (ExecutionRecoverySweeper sweeper, FakeTime time) = NewSweeper(rig);
        time.Now = DateTimeOffset.UtcNow.AddMinutes(10);

        (int failed, _) = await sweeper.SweepOnceAsync(CancellationToken.None);

        failed.Should().Be(0, "an auto-resumed run is not counted as failed");
        WorkflowExecution row = rig.Get(id);
        row.Status.Should().Be(ExecutionStatus.Queued);
        row.PendingResume.Should().BeTrue();
        row.AutoResumeCount.Should().Be(1);
        rig.Published.Should().ContainSingle(m => m.ExecutionId == id);
    }

    [Fact]
    public async Task A_run_already_auto_resumed_once_is_failed_by_the_sweeper_with_resume_on_offer()
    {
        await using Rig rig = new();
        Guid id = rig.AddQueuedExecution(autoResumeCount: 1);
        await rig.StartAsync(id);
        await rig.Runner.StopAsync(CancellationToken.None);
        await rig.WaitForRunnerIdleAsync();
        (ExecutionRecoverySweeper sweeper, FakeTime time) = NewSweeper(rig);
        time.Now = DateTimeOffset.UtcNow.AddMinutes(10);

        (int failed, _) = await sweeper.SweepOnceAsync(CancellationToken.None);

        failed.Should().Be(1);
        WorkflowExecution row = rig.Get(id);
        row.Status.Should().Be(ExecutionStatus.Failed);
        row.ErrorMessage.Should().Contain("platform interruption, not a cancellation");
        rig.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_user_cancelled_run_is_never_touched_by_the_sweeper()
    {
        await using Rig rig = new();
        Guid id = rig.AddQueuedExecution();
        await rig.StartAsync(id);
        rig.Registry.TryCancel(id, "Stopped by user 7f1c");
        await rig.WaitForRunnerIdleAsync();
        (ExecutionRecoverySweeper sweeper, FakeTime time) = NewSweeper(rig);
        time.Now = DateTimeOffset.UtcNow.AddMinutes(10);

        await sweeper.SweepOnceAsync(CancellationToken.None);

        rig.Get(id).Status.Should().Be(ExecutionStatus.Cancelled);
        rig.Published.Should().BeEmpty();
    }
}
