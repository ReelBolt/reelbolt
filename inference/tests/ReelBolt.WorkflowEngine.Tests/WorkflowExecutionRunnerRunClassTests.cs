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
using ReelBolt.WorkflowEngine.Consumers;
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
/// T-015: the RUN_CLASS_CHANGED re-check end to end, from <see cref="WorkflowExecutionRequestedConsumer.Consume"/>
/// through <see cref="WorkflowExecutionRunner"/> to the real <see cref="WorkflowExecutorService"/>. The admitted bucket is
/// read from the execution ROW (<see cref="WorkflowExecution.AdmittedRunRateBucket"/>), so every re-dispatch (the
/// recovery sweeper's re-publish, a REST retry) keeps the check without the message having to carry it.
/// </summary>
public sealed class WorkflowExecutionRunnerRunClassTests
{
    private sealed class CountingExecutor(StepType stepType) : IStepExecutor
    {
        private int _calls;

        public int CallCount => Volatile.Read(ref _calls);

        public StepType StepType { get; } = stepType;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new StepExecutionResult
            {
                Output = "{}",
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                Status = StepStatus.Completed
            });
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _database = Guid.NewGuid().ToString();

        public ServiceProvider Services { get; }
        public CountingExecutor Agent { get; } = new(StepType.Agent);
        public CountingExecutor Extract { get; } = new(StepType.Extract);
        public Mock<ICreditGate> Gate { get; } = new();
        public Mock<IPublishEndpoint> Publish { get; } = new();
        public List<WorkflowExecutionRequested> Published { get; } = new();
        public Guid OrganizationId { get; } = Guid.NewGuid();

        public Rig()
        {
            Gate.Setup(g => g.ReserveAsync(
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
            // InMemory, as the executor's own run-class tests use: agent_definitions is API-owned (excluded from the engine's
            // schema), so a relational EnsureCreated could not seed it. The sweeper's transaction is a no-op here.
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
                eventPublisher: new Mock<IWorkflowEventPublisher>().Object,
                logger: NullLogger<WorkflowExecutorService>.Instance,
                executors: new IStepExecutor[] { Agent, Extract },
                rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
                hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
                cancellationRegistry: sp.GetRequiredService<ExecutionCancellationRegistry>(),
                creditGate: Gate.Object));
            services.AddSingleton<WorkflowExecutionRunner>();
            Services = services.BuildServiceProvider();
        }

        public WorkflowEngineDbContext NewDb() =>
            Services.CreateScope().ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

        public Guid AddExecution(string? admittedBucket, DateTime? queuedAt, params StepType[] stepTypes)
        {
            using WorkflowEngineDbContext db = NewDb();
            Guid projectId = Guid.NewGuid();
            AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "Agent", AgentType = AgentType.VideoStoryEditor, SystemPrompt = "" };
            WorkflowDefinition definition = new() { Id = Guid.NewGuid(), Name = "w", ProjectId = projectId };
            int order = 1;
            foreach (StepType type in stepTypes)
            {
                definition.Steps.Add(new WorkflowStep
                {
                    Id = Guid.NewGuid(),
                    WorkflowDefinitionId = definition.Id,
                    StepOrder = order++,
                    StepType = type,
                    AgentDefinitionId = agent.Id
                });
            }

            WorkflowExecution execution = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = definition.Id,
                ProjectId = projectId,
                OrganizationId = OrganizationId,
                Status = ExecutionStatus.Queued,
                QueuedAt = queuedAt,
                CorrelationId = "corr-" + Guid.NewGuid().ToString("N")[..6],
                AdmittedRunRateBucket = admittedBucket
            };
            db.AgentDefinitions.Add(agent);
            db.WorkflowDefinitions.Add(definition);
            db.WorkflowExecutions.Add(execution);
            db.SaveChanges();
            return execution.Id;
        }

        public WorkflowExecutionRequested MessageFor(Guid executionId, string? hint) => new()
        {
            ExecutionId = executionId,
            OrganizationId = OrganizationId,
            CorrelationId = "corr",
            RequestedAt = DateTime.UtcNow,
            AdmittedRunRateBucket = hint
        };

        /// <summary>Consumes <paramref name="message"/> and waits for the run the consumer handed to the runner to end.</summary>
        public async Task ConsumeAndWaitAsync(WorkflowExecutionRequested message)
        {
            var runner = Services.GetRequiredService<WorkflowExecutionRunner>();
            var consumer = new WorkflowExecutionRequestedConsumer(runner, NullLogger<WorkflowExecutionRequestedConsumer>.Instance);
            var context = new Mock<ConsumeContext<WorkflowExecutionRequested>>();
            context.SetupGet(c => c.Message).Returns(message);
            context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);

            await consumer.Consume(context.Object).WaitAsync(TimeSpan.FromSeconds(10));

            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (runner.RunningCount > 0)
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

        public int StepResultCount(Guid id)
        {
            using WorkflowEngineDbContext db = NewDb();
            return db.WorkflowStepResults.Count(r => r.WorkflowExecutionId == id);
        }

        public void VerifyNoHold() => Gate.Verify(g => g.ReserveAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<Guid?>(),
            It.IsAny<CreditEntitlement>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    private void AssertRunClassChanged(Rig rig, Guid id)
    {
        WorkflowExecution final = rig.Get(id);
        final.Status.Should().Be(ExecutionStatus.Failed);
        final.ErrorMessage.Should().Be("This workflow changed after the run started; start it again.");
        rig.Agent.CallCount.Should().Be(0, "no step executor runs");
        rig.Extract.CallCount.Should().Be(0, "not even the free step before the paid one");
        rig.StepResultCount(id).Should().Be(0);
        rig.VerifyNoHold();
    }

    [Fact]
    public async Task A_free_admitted_row_whose_steps_are_paid_fails_RUN_CLASS_CHANGED_through_the_consumer()
    {
        await using var rig = new Rig();
        Guid id = rig.AddExecution("free", null, StepType.Extract, StepType.Agent);

        await rig.ConsumeAndWaitAsync(rig.MessageFor(id, hint: "free"));

        AssertRunClassChanged(rig, id);
    }

    [Fact]
    public async Task The_row_is_the_source_of_truth_when_the_message_carries_no_bucket()
    {
        await using var rig = new Rig();
        Guid id = rig.AddExecution("free", null, StepType.Agent);

        await rig.ConsumeAndWaitAsync(rig.MessageFor(id, hint: null));

        AssertRunClassChanged(rig, id);
    }

    [Fact]
    public async Task A_message_claiming_paid_does_not_lift_the_check_on_a_free_admitted_row()
    {
        await using var rig = new Rig();
        Guid id = rig.AddExecution("free", null, StepType.Agent);

        await rig.ConsumeAndWaitAsync(rig.MessageFor(id, hint: "paid"));

        AssertRunClassChanged(rig, id);
    }

    [Fact]
    public async Task A_row_with_no_admitted_bucket_runs_its_paid_steps_normally()
    {
        await using var rig = new Rig();
        Guid id = rig.AddExecution(null, null, StepType.Agent);

        await rig.ConsumeAndWaitAsync(rig.MessageFor(id, hint: null));

        rig.Agent.CallCount.Should().Be(1);
        rig.Get(id).Status.Should().Be(ExecutionStatus.Passed);
        rig.Gate.Verify(g => g.ReserveAsync(
            It.IsAny<Guid>(), id, It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<Guid?>(),
            It.IsAny<CreditEntitlement>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once,
            "the paid step is held as usual, which also proves this rig would see a hold");
    }

    [Fact]
    public async Task A_free_admitted_row_whose_steps_are_still_free_runs_normally()
    {
        await using var rig = new Rig();
        Guid id = rig.AddExecution("free", null, StepType.Extract);

        await rig.ConsumeAndWaitAsync(rig.MessageFor(id, hint: "free"));

        rig.Extract.CallCount.Should().Be(1);
        rig.Get(id).Status.Should().Be(ExecutionStatus.Passed);
    }

    [Fact]
    public async Task The_recovery_sweepers_republish_of_a_free_admitted_row_is_still_checked()
    {
        await using var rig = new Rig();
        Guid id = rig.AddExecution("free", DateTime.UtcNow.AddHours(-1), StepType.Agent);
        IServiceScopeFactory factory = rig.Services.GetRequiredService<IServiceScopeFactory>();
        var leases = new ExecutionLeaseService(factory, new ConfigurationBuilder().Build(), NullLogger<ExecutionLeaseService>.Instance);
        var sweeper = new ExecutionRecoverySweeper(factory, leases, NullLogger<ExecutionRecoverySweeper>.Instance);

        (await sweeper.SweepOnceAsync(CancellationToken.None)).Republished.Should().Be(1);
        WorkflowExecutionRequested republished = rig.Published.Should().ContainSingle().Subject;
        republished.AdmittedRunRateBucket.Should().Be("free", "the re-publish carries the row's bucket as a hint for logs");

        await rig.ConsumeAndWaitAsync(republished with { AdmittedRunRateBucket = null });

        AssertRunClassChanged(rig, id);
    }
}
