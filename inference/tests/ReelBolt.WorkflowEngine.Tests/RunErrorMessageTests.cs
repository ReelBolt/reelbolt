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
/// The run's error_message follows the failing step's own class and first-party message (issue #763),
/// while the step row keeps its persisted class and code.
/// </summary>
public sealed class RunErrorMessageTests
{
    private const string Italian = "La frase della scena 3 è troppo lunga per la sua scena. Accorcia il testo o allunga la scena.";

    private sealed class StubExecutor : IStepExecutor
    {
        private readonly FailureClass _class;

        public StubExecutor(FailureClass failureClass) => _class = failureClass;

        public StepType StepType => StepType.Agent;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context) =>
            Task.FromResult(new StepExecutionResult
            {
                Status = StepStatus.Failed,
                FailureClass = _class,
                FailureCode = "NARRATION_DOES_NOT_FIT",
                UserMessage = Italian,
                Retryable = false,
                Output = "{}",
                ErrorDetails = "NARRATION_DOES_NOT_FIT: internal detail",
                NextStepIndex = 1,
            });
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _database = Guid.NewGuid().ToString();

        public ServiceProvider Services { get; }
        public StubExecutor Step { get; }
        public Mock<IWorkflowEventPublisher> Events { get; } = new();
        public Mock<IPublishEndpoint> Publish { get; } = new();
        public List<WorkflowExecutionRequested> Published { get; } = new();
        public Guid OrganizationId { get; } = Guid.NewGuid();
        public ExecutionCancellationRegistry Registry => Services.GetRequiredService<ExecutionCancellationRegistry>();
        public WorkflowExecutionRunner Runner => Services.GetRequiredService<WorkflowExecutionRunner>();

        public Rig(StubExecutor step)
        {
            Step = step;
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

        public async Task StartAsync(Guid id)
        {
            AdmissionDecision decision = await Runner.DispatchAsync(id, "corr", CancellationToken.None, organizationId: OrganizationId);
            decision.Admitted.Should().BeTrue();
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

    [Theory]
    [InlineData(FailureClass.UserInput, true)]
    [InlineData(FailureClass.PlatformInternal, false)]
    public async Task The_run_message_shows_the_step_message_only_for_a_user_class(FailureClass failureClass, bool shown)
    {
        await using Rig rig = new(new StubExecutor(failureClass));
        Guid id = rig.AddQueuedExecution();

        await rig.StartAsync(id);
        await rig.WaitForRunnerIdleAsync();

        WorkflowExecution run = rig.Get(id);
        run.Status.Should().Be(ExecutionStatus.Failed);
        if (shown)
            run.ErrorMessage.Should().Be(Italian);
        else
            run.ErrorMessage.Should().Be(FailureMessages.UserMessage("NARRATION_DOES_NOT_FIT")).And.Contain("not charged");

        using WorkflowEngineDbContext db = rig.NewDb();
        WorkflowStepResult step = db.WorkflowStepResults.AsNoTracking().Single(s => s.WorkflowExecutionId == id);
        step.FailureClass.Should().Be(failureClass);
        step.FailureCode.Should().Be("NARRATION_DOES_NOT_FIT");
    }
}
