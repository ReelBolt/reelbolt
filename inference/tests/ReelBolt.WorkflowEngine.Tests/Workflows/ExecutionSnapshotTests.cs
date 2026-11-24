using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.Decision.CostRouting;
using ReelBolt.WorkflowEngine.Execution.Snapshots;
using ReelBolt.WorkflowEngine.Services.Messaging;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>
/// A Standard run executes from its own snapshot, taken by the engine when the run starts: a kind-5
/// clone of the workflow's steps plus the behaviour fields of every agent those steps use. Editing the
/// workflow (or an agent) while the run is in flight can therefore neither change what the run does
/// nor cascade-delete its results.
/// </summary>
public class ExecutionSnapshotTests
{
    private sealed class Executor(StepType type, Func<StepExecutionContext, Task<StepExecutionResult>> run) : IStepExecutor
    {
        public StepType StepType { get; } = type;
        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context) => run(context);
    }

    private static StepExecutionResult Done(StepExecutionContext c, string output) => new()
    {
        Output = output,
        NextStepIndex = c.CurrentStepIndex + 1,
        NewIterationCount = c.IterationCount,
        Status = StepStatus.Completed
    };

    private sealed class Scopes(WorkflowEngineDbContext db, IStepResultCache? cache = null) : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public void Dispose() { }

        public object? GetService(Type serviceType)
            => serviceType == typeof(WorkflowEngineDbContext) ? db
            : serviceType == typeof(IStepResultCache) ? cache
            : serviceType == typeof(IProjectFileFingerprintProvider) && cache is not null ? new FixedFingerprint()
            : null;
    }

    private sealed class FixedFingerprint : IProjectFileFingerprintProvider
    {
        public Task<string> GetFingerprintAsync(Guid projectId, CancellationToken ct) => Task.FromResult(new string('f', 64));
    }

    private sealed class NoOpPublisher : IWorkflowEventPublisher
    {
        public Task PublishExecutionRunningAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepStartedAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, string? inputPreview, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepCompletedAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, StepExecutionResult stepExecutionResult, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepProgressAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, string stage, int? percentComplete, CancellationToken ct, int? tokensUsedSoFar = null, int? inputTokensSoFar = null, int? outputTokensSoFar = null) => Task.CompletedTask;
        public Task PublishStepChatTurnAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, int turnIndex, int? totalTurns, string speaker, string speakerRole, string text, IReadOnlyList<string> idsMentioned, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepDiagnosticsAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, StepExecutionResult stepExecutionResult, CancellationToken ct) => Task.CompletedTask;
        public Task PublishExecutionCompletedAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
        public Task PublishExecutionFailedAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
    }

    private static WorkflowExecutorService Service(WorkflowEngineDbContext db, IStepResultCache? cache, params IStepExecutor[] executors) => new(
        scopeFactory: new Scopes(db, cache),
        eventPublisher: new NoOpPublisher(),
        logger: NullLogger<WorkflowExecutorService>.Instance,
        executors: executors,
        rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
        hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
        cancellationRegistry: new ExecutionCancellationRegistry(),
        cachePolicy: cache is null ? null : new StepCachePolicy(),
        cacheKeyBuilder: cache is null ? null : new StepCacheKeyBuilder(),
        cacheOptions: cache is null ? null : Options.Create(new StepCacheOptions { Enabled = true, TtlHours = 24 }));

    private sealed record Seeded(Guid ProjectId, Guid OrganizationId, AgentDefinition Agent, WorkflowDefinition Workflow, WorkflowExecution Execution);

    /// <summary>A project with one Standard two-step workflow ("Write", "Polish") on one custom agent, and a queued run of it.</summary>
    private static Seeded Seed(DbContext db, StepType stepType = StepType.Agent, WorkflowDefinitionKind kind = WorkflowDefinitionKind.Standard)
    {
        Guid orgId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        db.Add(new ApplicationUser { Id = ownerId, Email = $"{ownerId:N}@example.com", DisplayName = "owner" });
        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = orgId };
        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = "Writer",
            AgentType = AgentType.Custom,
            OrganizationId = orgId,
            SystemPrompt = "Write tightly.",
            ReasoningEffort = "low",
            InferenceProviderId = Guid.NewGuid(),
            AssignedSkillsJson = "[\"remotion-markup\"]"
        };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), Name = "Launch reel", ProjectId = project.Id, Kind = kind };
        workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = agent.Id, StepOrder = 1, StepType = stepType, Label = "Write" });
        workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = agent.Id, StepOrder = 2, StepType = stepType, Label = "Polish" });
        WorkflowExecution execution = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            ProjectId = project.Id,
            OrganizationId = orgId,
            Status = ExecutionStatus.Queued,
            QueuedAt = DateTime.UtcNow
        };

        db.Add(project);
        db.Add(agent);
        db.Add(workflow);
        db.Add(execution);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return new Seeded(project.Id, orgId, agent, workflow, execution);
    }

    private static WorkflowEngineDbContext InMemory(string name) => new(
        new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(name).Options);

    // ------------------------------------------------------------------ (a) snapshot at start

    [Fact]
    public async Task Start_clones_a_standard_workflow_into_a_hidden_snapshot_and_runs_the_clone()
    {
        using WorkflowEngineDbContext db = InMemory("snap-" + Guid.NewGuid().ToString("N"));
        Seeded s = Seed(db);
        List<(Guid StepId, Guid DefinitionId, string? Label)> ran = [];

        await Service(db, null, new Executor(StepType.Agent, c =>
        {
            ran.Add((c.Step.Id, c.Step.WorkflowDefinitionId, c.Step.Label));
            return Task.FromResult(Done(c, "{}"));
        })).ExecuteAsync(s.Execution.Id, "corr", CancellationToken.None);

        WorkflowExecution run = await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == s.Execution.Id);
        run.Status.Should().Be(ExecutionStatus.Passed, run.ErrorMessage);
        run.WorkflowDefinitionId.Should().Be(s.Workflow.Id, "lists, URLs and the step cache keep the source id");
        run.SnapshotDefinitionId.Should().NotBeNull();

        WorkflowDefinition snapshot = await db.WorkflowDefinitions.AsNoTracking().Include(w => w.Steps)
            .SingleAsync(w => w.Id == run.SnapshotDefinitionId);
        snapshot.Kind.Should().Be(WorkflowDefinitionKind.ExecutionSnapshot);
        snapshot.ProjectId.Should().Be(s.ProjectId);
        snapshot.Name.Should().Be("Launch reel", "a run shows its source workflow's name; Kind alone hides the snapshot");
        snapshot.Steps.OrderBy(x => x.StepOrder).Select(x => x.Label).Should().Equal("Write", "Polish");
        snapshot.Steps.Select(x => x.Id).Should().NotIntersectWith(s.Workflow.Steps.Select(x => x.Id));

        ran.Select(r => r.Label).Should().Equal("Write", "Polish");
        ran.Should().OnlyContain(r => r.DefinitionId == snapshot.Id, "every step runs from the clone");
        (await db.WorkflowStepResults.AsNoTracking().Where(r => r.WorkflowExecutionId == run.Id).Select(r => r.WorkflowStepId).ToListAsync())
            .Should().BeEquivalentTo(snapshot.Steps.Select(x => x.Id), "results reference the clone's steps, never the user's");

        AgentSnapshot agents = AgentSnapshot.Parse(run.AgentSnapshotJson)!;
        AgentSnapshotEntry writer = agents.Find(s.Agent.Id)!;
        writer.SystemPrompt.Should().Be("Write tightly.");
        writer.ReasoningEffort.Should().Be("low");
        writer.InferenceProviderId.Should().Be(s.Agent.InferenceProviderId);
        writer.AssignedSkillsJson.Should().Be("[\"remotion-markup\"]");
        writer.AgentType.Should().Be(AgentType.Custom);
    }

    [Fact]
    public async Task A_run_that_already_has_a_snapshot_reuses_it_instead_of_taking_another()
    {
        using WorkflowEngineDbContext db = InMemory("snap-" + Guid.NewGuid().ToString("N"));
        Seeded s = Seed(db);
        List<Guid> ran = [];
        WorkflowExecutorService service = Service(db, null, new Executor(StepType.Agent, c =>
        {
            ran.Add(c.Step.Id);
            return Task.FromResult(Done(c, "{}"));
        }));

        await service.ExecuteAsync(s.Execution.Id, "corr", CancellationToken.None);
        WorkflowExecution run = await db.WorkflowExecutions.SingleAsync(e => e.Id == s.Execution.Id);
        Guid snapshotId = run.SnapshotDefinitionId!.Value;
        string? agentJson = run.AgentSnapshotJson;

        // Re-queued (what a retry or resume does to the same row).
        run.Status = ExecutionStatus.Queued;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await service.ExecuteAsync(s.Execution.Id, "corr", CancellationToken.None);

        (await db.WorkflowDefinitions.CountAsync(w => w.Kind == WorkflowDefinitionKind.ExecutionSnapshot)).Should().Be(1);
        WorkflowExecution again = await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == s.Execution.Id);
        again.SnapshotDefinitionId.Should().Be(snapshotId);
        again.AgentSnapshotJson.Should().Be(agentJson);
        ran.Skip(2).Should().Equal(ran.Take(2), "the second dispatch runs the same snapshot steps");
    }

    [Theory]
    [InlineData(WorkflowDefinitionKind.TimelineRender)]
    [InlineData(WorkflowDefinitionKind.CompositionRevision)]
    public async Task A_hidden_system_workflow_runs_as_today_without_a_snapshot(WorkflowDefinitionKind kind)
    {
        using WorkflowEngineDbContext db = InMemory("snap-" + Guid.NewGuid().ToString("N"));
        Seeded s = Seed(db, kind: kind);
        List<Guid> ran = [];

        await Service(db, null, new Executor(StepType.Agent, c =>
        {
            ran.Add(c.Step.WorkflowDefinitionId);
            return Task.FromResult(Done(c, "{}"));
        })).ExecuteAsync(s.Execution.Id, "corr", CancellationToken.None);

        WorkflowExecution run = await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == s.Execution.Id);
        run.SnapshotDefinitionId.Should().BeNull();
        run.AgentSnapshotJson.Should().BeNull();
        ran.Should().OnlyContain(id => id == s.Workflow.Id);
        (await db.WorkflowDefinitions.CountAsync(w => w.Kind == WorkflowDefinitionKind.ExecutionSnapshot)).Should().Be(0);
    }

    // ------------------------------------------------------------------ (b) PUT between step 1 and 2

    /// <summary>
    /// Review Focus 1. Relational (SQLite, foreign keys ON) because the bug IS the cascade: the PUT
    /// clears the workflow's steps with <c>ExecuteDelete</c> (the InMemory provider supports neither
    /// that nor cascades) and <c>workflow_step_results → workflow_steps</c> is ON DELETE CASCADE.
    /// </summary>
    [Fact]
    public async Task Replacing_the_workflow_steps_mid_run_leaves_the_run_its_original_steps_and_every_result()
    {
        using SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        Exec(connection, "PRAGMA foreign_keys = OFF;");
        using (InferenceApiDbContext api = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(connection).Options))
            api.Database.EnsureCreated();
        DbContextOptions<WorkflowEngineDbContext> engineOptions = new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseSqlite(connection).Options;
        using (WorkflowEngineDbContext create = new(engineOptions))
            create.GetService<IRelationalDatabaseCreator>().CreateTables();

        Seeded s;
        using (WorkflowEngineDbContext seed = new(engineOptions))
            s = Seed(seed);
        Exec(connection, "PRAGMA foreign_keys = ON;");

        using WorkflowEngineDbContext db = new(engineOptions);
        List<string?> ran = [];
        WorkflowExecutorService service = Service(db, null, new Executor(StepType.Agent, async c =>
        {
            ran.Add(c.Step.Label);
            if (c.Step.StepOrder == 1)
            {
                // What WorkflowsController.Update does: ExecuteDelete the steps, then add the new list.
                using WorkflowEngineDbContext editor = new(engineOptions);
                await editor.WorkflowSteps.Where(x => x.WorkflowDefinitionId == s.Workflow.Id).ExecuteDeleteAsync();
                editor.WorkflowSteps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = s.Workflow.Id, AgentDefinitionId = s.Agent.Id, StepOrder = 1, StepType = StepType.Agent, Label = "Rewritten" });
                await editor.SaveChangesAsync();
            }

            return Done(c, "{\"step\":" + c.Step.StepOrder + "}");
        }));

        await service.ExecuteAsync(s.Execution.Id, "corr", CancellationToken.None);

        using WorkflowEngineDbContext check = new(engineOptions);
        WorkflowExecution run = await check.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == s.Execution.Id);
        run.Status.Should().Be(ExecutionStatus.Passed, run.ErrorMessage);
        ran.Should().Equal("Write", "Polish");
        List<WorkflowStepResult> results = await check.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == run.Id).ToListAsync();
        results.Should().HaveCount(2, "the edit must not cascade-delete the run's results");
        results.Should().OnlyContain(r => r.Status == StepStatus.Completed);
        (await check.WorkflowSteps.AsNoTracking().Where(x => x.WorkflowDefinitionId == s.Workflow.Id).Select(x => x.Label).ToListAsync())
            .Should().Equal(["Rewritten"], "the user's edit itself went through");
    }

    // ------------------------------------------------------------------ deleting the run removes its snapshot

    [Fact]
    public async Task Deleting_an_execution_deletes_its_snapshot_and_deleting_the_project_still_cascades()
    {
        using SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        Exec(connection, "PRAGMA foreign_keys = OFF;");
        using (InferenceApiDbContext api = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(connection).Options))
            api.Database.EnsureCreated();
        DbContextOptions<WorkflowEngineDbContext> engineOptions = new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseSqlite(connection).Options;
        using (WorkflowEngineDbContext create = new(engineOptions))
            create.GetService<IRelationalDatabaseCreator>().CreateTables();
        Seeded s;
        using (WorkflowEngineDbContext seed = new(engineOptions))
            s = Seed(seed);
        Exec(connection, "PRAGMA foreign_keys = ON;");

        using (WorkflowEngineDbContext db = new(engineOptions))
            await Service(db, null, new Executor(StepType.Agent, c => Task.FromResult(Done(c, "{}"))))
                .ExecuteAsync(s.Execution.Id, "corr", CancellationToken.None);

        using WorkflowEngineDbContext check = new(engineOptions);
        Guid snapshotId = (await check.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == s.Execution.Id)).SnapshotDefinitionId!.Value;

        await check.WorkflowExecutions.Where(e => e.Id == s.Execution.Id).ExecuteDeleteAsync();

        (await check.WorkflowDefinitions.AnyAsync(w => w.Id == snapshotId)).Should().BeFalse();
        (await check.WorkflowSteps.AnyAsync(x => x.WorkflowDefinitionId == snapshotId)).Should().BeFalse();
        (await check.WorkflowDefinitions.AnyAsync(w => w.Id == s.Workflow.Id)).Should().BeTrue("the user's workflow is untouched");

        // A second run, then the project goes: everything under it cascades, snapshot included.
        WorkflowExecution second = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = s.Workflow.Id, ProjectId = s.ProjectId, OrganizationId = s.OrganizationId, Status = ExecutionStatus.Queued };
        check.WorkflowExecutions.Add(second);
        await check.SaveChangesAsync();
        using (WorkflowEngineDbContext db = new(engineOptions))
            await Service(db, null, new Executor(StepType.Agent, c => Task.FromResult(Done(c, "{}"))))
                .ExecuteAsync(second.Id, "corr", CancellationToken.None);
        check.ChangeTracker.Clear();
        (await check.Projects.Where(p => p.Id == s.ProjectId).ExecuteDeleteAsync()).Should().Be(1);
        (await check.WorkflowDefinitions.CountAsync()).Should().Be(0);
        (await check.WorkflowExecutions.CountAsync()).Should().Be(0);
        (await check.WorkflowStepResults.CountAsync()).Should().Be(0);
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ (d) agent edited mid-run

    [Fact]
    public async Task An_agent_edited_mid_run_does_not_reach_the_later_steps_of_that_run()
    {
        string name = "snap-" + Guid.NewGuid().ToString("N");
        using WorkflowEngineDbContext db = InMemory(name);
        Seeded s = Seed(db);

        // The live seam the agents resolve their effort and chat client through, over a resolver that
        // reads the CURRENT row, the way the real resolver does.
        Guid livePinned = Guid.NewGuid();
        Mock<IAgentChatClientProvider> live = new();
        live.Setup(p => p.ResolveReasoningEffortAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                using WorkflowEngineDbContext read = InMemory(name);
                return ValueTask.FromResult(read.AgentDefinitions.AsNoTracking().Single(a => a.Id == s.Agent.Id).ReasoningEffort);
            });
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveChatAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => new ResolvedInferenceProvider(id, "p", InferenceProviderKind.OpenAICompatible, "http://x", "m", "k", 30));
        Mock<IChatClientFactory> factory = new();
        List<Guid?> builtFor = [];
        factory.Setup(f => f.Get(It.IsAny<ResolvedInferenceProvider>()))
            .Returns((ResolvedInferenceProvider p) => { builtFor.Add(p.ProviderId); return Mock.Of<IChatClient>(); });
        CostRoutingChatClientProvider seam = new(live.Object, resolver.Object, factory.Object, new CostRoutingAmbientState(),
            NullLogger<CostRoutingChatClientProvider>.Instance);

        string? promptAtStep2 = null;
        string? effortAtStep2 = null;
        await Service(db, null, new Executor(StepType.Agent, async c =>
        {
            if (c.Step.StepOrder == 1)
            {
                using WorkflowEngineDbContext editor = InMemory(name);
                AgentDefinition row = await editor.AgentDefinitions.SingleAsync(a => a.Id == s.Agent.Id);
                row.SystemPrompt = "Ramble on.";
                row.ReasoningEffort = "xhigh";
                row.InferenceProviderId = livePinned;
                await editor.SaveChangesAsync();
            }
            else
            {
                promptAtStep2 = c.Step.AgentDefinition.SystemPrompt;
                effortAtStep2 = await seam.ResolveReasoningEffortAsync(c.Step.AgentDefinition.AgentType, c.Step.AgentDefinitionId, CancellationToken.None);
                await seam.GetAsync(c.Step.AgentDefinition.AgentType, c.Step.AgentDefinitionId, CancellationToken.None);
            }

            return Done(c, "{}");
        })).ExecuteAsync(s.Execution.Id, "corr", CancellationToken.None);

        (await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == s.Execution.Id)).Status.Should().Be(ExecutionStatus.Passed);
        promptAtStep2.Should().Be("Write tightly.");
        effortAtStep2.Should().Be("low", "the run keeps the effort its agent had when it started");
        builtFor.Should().Equal([s.Agent.InferenceProviderId], "the run keeps the provider its agent had when it started");

        using WorkflowEngineDbContext after = InMemory(name);
        (await after.AgentDefinitions.AsNoTracking().SingleAsync(a => a.Id == s.Agent.Id)).SystemPrompt
            .Should().Be("Ramble on.", "the overlay is never written back over the live row");
    }

    [Fact]
    public async Task Outside_a_run_the_seam_resolves_live_as_today()
    {
        Mock<IAgentChatClientProvider> live = new();
        live.Setup(p => p.ResolveReasoningEffortAsync(AgentType.Custom, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult<string?>("medium"));
        CostRoutingChatClientProvider seam = new(live.Object, Mock.Of<IInferenceProviderResolver>(), Mock.Of<IChatClientFactory>(),
            new CostRoutingAmbientState(), NullLogger<CostRoutingChatClientProvider>.Instance);

        (await seam.ResolveReasoningEffortAsync(AgentType.Custom, Guid.NewGuid(), CancellationToken.None)).Should().Be("medium");
    }

    // ------------------------------------------------------------------ room seats and directors

    /// <summary>
    /// A room's custom seat and director agents are named in its step config, not in
    /// <see cref="WorkflowStep.AgentDefinitionId"/>. Room turns build their chat client (provider) and
    /// reasoning effort through the same seam as every other agent, keyed by that seat or director id,
    /// so the run's agent snapshot must record those agents too.
    /// </summary>
    private sealed record RoomRun(string DbName, Seeded Seed, AgentDefinition Seat, AgentDefinition Director);

    private static async Task<RoomRun> SeedRoomRunAsync(StepType roomType)
    {
        string name = "snap-room-" + Guid.NewGuid().ToString("N");
        using WorkflowEngineDbContext db = InMemory(name);
        Seeded s = Seed(db);
        AgentDefinition seat = new() { Id = Guid.NewGuid(), Name = "Skeptic", AgentType = AgentType.Custom, OrganizationId = s.OrganizationId, SystemPrompt = "Doubt everything.", ReasoningEffort = "low", InferenceProviderId = Guid.NewGuid() };
        AgentDefinition director = new() { Id = Guid.NewGuid(), Name = "Chair", AgentType = AgentType.Custom, OrganizationId = s.OrganizationId, SystemPrompt = "Decide.", ReasoningEffort = "medium", InferenceProviderId = Guid.NewGuid() };
        db.AgentDefinitions.AddRange(seat, director);

        string config = JsonSerializer.Serialize(new
        {
            seats = new object[] { new { name = "Skeptic", persona = "Doubts", agentDefinitionId = seat.Id }, new { name = "Builtin", persona = "Default seat" } },
            directorAgentDefinitionId = director.Id
        });
        WorkflowStep room = await db.WorkflowSteps.SingleAsync(x => x.WorkflowDefinitionId == s.Workflow.Id && x.StepOrder == 2);
        room.StepType = roomType;
        switch (roomType)
        {
            case StepType.EditRoom: room.EditRoomConfigJson = config; break;
            case StepType.GraphicsRoom: room.GraphicsRoomConfigJson = config; break;
            case StepType.ColorGradeRoom: room.ColorGradeRoomConfigJson = config; break;
            case StepType.DailiesRoom: room.DailiesRoomConfigJson = config; break;
        }
        await db.SaveChangesAsync();
        return new RoomRun(name, s, seat, director);
    }

    /// <summary>Runs the seeded room run: step 1 (Agent) edits both room agents' rows, step 2 (the room) asks the seam.</summary>
    private static async Task<(Guid? SeatProvider, string? SeatEffort, Guid? DirectorProvider, string? DirectorEffort)> RunRoomAsync(RoomRun r, StepType roomType)
    {
        Mock<IAgentChatClientProvider> live = new();
        live.Setup(p => p.ResolveReasoningEffortAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns((AgentType _, Guid? id, CancellationToken _) =>
            {
                using WorkflowEngineDbContext read = InMemory(r.DbName);
                return ValueTask.FromResult(read.AgentDefinitions.AsNoTracking().Single(a => a.Id == id).ReasoningEffort);
            });
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(x => x.ResolveChatAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => new ResolvedInferenceProvider(id, "p", InferenceProviderKind.OpenAICompatible, "http://x", "m", "k", 30));
        List<Guid?> built = [];
        Mock<IChatClientFactory> factory = new();
        factory.Setup(f => f.Get(It.IsAny<ResolvedInferenceProvider>()))
            .Returns((ResolvedInferenceProvider p) => { built.Add(p.ProviderId); return Mock.Of<IChatClient>(); });
        // An unpinned path would fall to the live chain; the live chain here answers with a sentinel.
        Guid liveChain = Guid.NewGuid();
        live.Setup(p => p.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns(() => { built.Add(liveChain); return ValueTask.FromResult(Mock.Of<IChatClient>()); });
        CostRoutingChatClientProvider seam = new(live.Object, resolver.Object, factory.Object, new CostRoutingAmbientState(),
            NullLogger<CostRoutingChatClientProvider>.Instance);

        Guid? seatProvider = null, directorProvider = null;
        string? seatEffort = null, directorEffort = null;
        using WorkflowEngineDbContext db = InMemory(r.DbName);
        await Service(db, null,
            new Executor(StepType.Agent, async c =>
            {
                using WorkflowEngineDbContext editor = InMemory(r.DbName);
                foreach (AgentDefinition row in await editor.AgentDefinitions.Where(a => a.Id == r.Seat.Id || a.Id == r.Director.Id).ToListAsync())
                {
                    row.SystemPrompt = "Edited mid-run.";
                    row.ReasoningEffort = "xhigh";
                    row.InferenceProviderId = Guid.NewGuid();
                }
                await editor.SaveChangesAsync();
                return Done(c, "{}");
            }),
            new Executor(roomType, async c =>
            {
                // What RoomStepExecutorBase does per seat and for the director: the room's agent TYPE
                // with the configured definition id.
                await seam.GetAsync(AgentType.VideoStoryEditor, r.Seat.Id, CancellationToken.None);
                seatProvider = built[^1];
                seatEffort = await seam.ResolveReasoningEffortAsync(AgentType.VideoStoryEditor, r.Seat.Id, CancellationToken.None);
                await seam.GetAsync(AgentType.VideoStoryEditor, r.Director.Id, CancellationToken.None);
                directorProvider = built[^1];
                directorEffort = await seam.ResolveReasoningEffortAsync(AgentType.VideoStoryEditor, r.Director.Id, CancellationToken.None);
                return Done(c, "{}");
            })).ExecuteAsync(r.Seed.Execution.Id, "corr", CancellationToken.None);

        (await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == r.Seed.Execution.Id)).Status.Should().Be(ExecutionStatus.Passed);
        return (seatProvider, seatEffort, directorProvider, directorEffort);
    }

    [Theory]
    [InlineData(StepType.EditRoom)]
    [InlineData(StepType.GraphicsRoom)]
    [InlineData(StepType.ColorGradeRoom)]
    [InlineData(StepType.DailiesRoom)]
    public async Task A_custom_seat_agent_edited_mid_run_does_not_reach_the_later_room_turns(StepType roomType)
    {
        RoomRun r = await SeedRoomRunAsync(roomType);

        (Guid? seatProvider, string? seatEffort, _, _) = await RunRoomAsync(r, roomType);

        seatProvider.Should().Be(r.Seat.InferenceProviderId, "the seat keeps the provider its agent had when the run started");
        seatEffort.Should().Be("low");
        using WorkflowEngineDbContext db = InMemory(r.DbName);
        AgentSnapshot agents = AgentSnapshot.Parse((await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == r.Seed.Execution.Id)).AgentSnapshotJson)!;
        agents.Find(r.Seat.Id)!.SystemPrompt.Should().Be("Doubt everything.", "the seat is recorded with the same fields as a step agent");
    }

    [Theory]
    [InlineData(StepType.EditRoom)]
    [InlineData(StepType.GraphicsRoom)]
    [InlineData(StepType.ColorGradeRoom)]
    [InlineData(StepType.DailiesRoom)]
    public async Task A_custom_director_edited_mid_run_does_not_reach_the_later_room_turns(StepType roomType)
    {
        RoomRun r = await SeedRoomRunAsync(roomType);

        (_, _, Guid? directorProvider, string? directorEffort) = await RunRoomAsync(r, roomType);

        directorProvider.Should().Be(r.Director.InferenceProviderId);
        directorEffort.Should().Be("medium");
        using WorkflowEngineDbContext db = InMemory(r.DbName);
        AgentSnapshot agents = AgentSnapshot.Parse((await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == r.Seed.Execution.Id)).AgentSnapshotJson)!;
        agents.Find(r.Director.Id)!.SystemPrompt.Should().Be("Decide.");
        agents.Agents.Select(a => a.Id).Should().BeEquivalentTo([r.Seed.Agent.Id, r.Seat.Id, r.Director.Id],
            "a built-in seat with no id adds nothing; it resolves by type as today");
    }

    [Fact]
    public async Task Another_organizations_agent_named_in_a_step_config_is_never_frozen_into_the_snapshot()
    {
        RoomRun r = await SeedRoomRunAsync(StepType.EditRoom);
        using WorkflowEngineDbContext db = InMemory(r.DbName);
        AgentDefinition foreign = new() { Id = Guid.NewGuid(), Name = "Theirs", AgentType = AgentType.Custom, OrganizationId = Guid.NewGuid(), SystemPrompt = "Secret prompt." };
        AgentDefinition builtIn = new() { Id = Guid.NewGuid(), Name = "VideoStoryEditor", AgentType = AgentType.VideoStoryEditor, IsBuiltIn = true };
        db.AgentDefinitions.AddRange(foreign, builtIn);
        // Step 1 becomes a Parallel step over a built-in and another org's agent; the room's director is foreign too.
        WorkflowStep first = await db.WorkflowSteps.SingleAsync(x => x.WorkflowDefinitionId == r.Seed.Workflow.Id && x.StepOrder == 1);
        first.StepType = StepType.Parallel;
        first.ParallelAgentIdsJson = JsonSerializer.Serialize(new[] { r.Seed.Agent.Id, builtIn.Id, foreign.Id });
        WorkflowStep room = await db.WorkflowSteps.SingleAsync(x => x.WorkflowDefinitionId == r.Seed.Workflow.Id && x.StepOrder == 2);
        room.EditRoomConfigJson = JsonSerializer.Serialize(new
        {
            seats = new object[] { new { name = "Skeptic", persona = "Doubts", agentDefinitionId = r.Seat.Id } },
            directorAgentDefinitionId = foreign.Id
        });
        await db.SaveChangesAsync();

        await Service(db, null,
            new Executor(StepType.Parallel, c => Task.FromResult(Done(c, "{}"))),
            new Executor(StepType.EditRoom, c => Task.FromResult(Done(c, "{}"))))
            .ExecuteAsync(r.Seed.Execution.Id, "corr", CancellationToken.None);

        AgentSnapshot agents = AgentSnapshot.Parse((await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == r.Seed.Execution.Id)).AgentSnapshotJson)!;
        agents.Agents.Select(a => a.Id).Should().BeEquivalentTo([r.Seed.Agent.Id, builtIn.Id, r.Seat.Id],
            "only platform built-ins and the run's own organization's agents are recorded");
        agents.Find(foreign.Id).Should().BeNull("a foreign id resolves fail-closed at run time, exactly as without a snapshot");
    }

    [Fact]
    public void An_agent_row_missing_at_run_time_is_rebuilt_from_its_snapshot()
    {
        Guid id = Guid.NewGuid();
        Guid org = Guid.NewGuid();
        AgentSnapshot snapshot = AgentSnapshot.Capture([new AgentDefinition
        {
            Id = id, Name = "Writer", AgentType = AgentType.Custom, OrganizationId = org, SystemPrompt = "Write tightly.", ReasoningEffort = "low"
        }]);
        AgentSnapshot parsed = AgentSnapshot.Parse(snapshot.Serialize())!;

        AgentDefinition rebuilt = parsed.Overlay(null, id)!;

        rebuilt.Id.Should().Be(id);
        rebuilt.AgentType.Should().Be(AgentType.Custom);
        rebuilt.OrganizationId.Should().Be(org);
        rebuilt.SystemPrompt.Should().Be("Write tightly.");
        rebuilt.ReasoningEffort.Should().Be("low");
        parsed.Overlay(null, Guid.NewGuid()).Should().BeNull("an agent the snapshot never saw falls back as today");
    }

    // ------------------------------------------------------------------ (e) the step cache survives snapshots

    [Fact]
    public async Task Two_runs_of_the_same_workflow_still_share_the_step_cache()
    {
        using WorkflowEngineDbContext db = InMemory("snap-" + Guid.NewGuid().ToString("N"));
        Seeded s = Seed(db, StepType.Extract);
        StepResultCache cache = new(db, new StepCacheKeyBuilder(), new StepCachePolicy(),
            Options.Create(new StepCacheOptions { Enabled = true, TtlHours = 24 }), NullLogger<StepResultCache>.Instance);
        int calls = 0;
        IStepExecutor extract = new Executor(StepType.Extract, c => { calls++; return Task.FromResult(Done(c, "{\"facts\":" + c.Step.StepOrder + "}")); });

        await Service(db, cache, extract).ExecuteAsync(s.Execution.Id, "corr", CancellationToken.None);
        WorkflowExecution second = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = s.Workflow.Id, ProjectId = s.ProjectId, OrganizationId = s.OrganizationId, Status = ExecutionStatus.Queued };
        db.WorkflowExecutions.Add(second);
        await db.SaveChangesAsync();
        await Service(db, cache, extract).ExecuteAsync(second.Id, "corr", CancellationToken.None);

        calls.Should().Be(2, "the second run's two steps are served from the first run's cache");
        List<WorkflowStepResult> secondRows = await db.WorkflowStepResults.AsNoTracking().Where(r => r.WorkflowExecutionId == second.Id).ToListAsync();
        secondRows.Should().HaveCount(2).And.OnlyContain(r => r.FromCache);
        WorkflowExecution[] runs = await db.WorkflowExecutions.AsNoTracking().ToArrayAsync();
        runs.Select(r => r.SnapshotDefinitionId).Distinct().Should().HaveCount(2, "each run has its own snapshot");
        (await db.WorkflowStepCacheEntries.AsNoTracking().Select(e => e.WorkflowDefinitionId).Distinct().ToListAsync())
            .Should().Equal([s.Workflow.Id], "cache entries are filed under the SOURCE workflow");
    }

    [Fact]
    public void The_agent_snapshot_records_the_behaviour_fields()
    {
        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = "Writer",
            AgentType = AgentType.Custom,
            OrganizationId = Guid.NewGuid(),
            SystemPrompt = "p",
            ConfigJson = "{\"c\":1}",
            OutputSchemaJson = "{}",
            OutputSchemaName = "S",
            AvailableToolsJson = "[\"t\"]",
            GeneratesOutput = true,
            ContextMode = ContextMode.LastN,
            ContextWindowSize = 3,
            InferenceProviderId = Guid.NewGuid(),
            AssignedSkillsJson = "[\"s\"]",
            ReasoningEffort = "medium"
        };

        using JsonDocument doc = JsonDocument.Parse(AgentSnapshot.Capture([agent]).Serialize());
        JsonElement entry = doc.RootElement.GetProperty("agents")[0];
        entry.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "name", "agentType", "isBuiltIn", "organizationId", "systemPrompt", "configJson", "outputSchemaJson",
            "outputSchemaName", "availableToolsJson", "generatesOutput", "contextMode", "contextWindowSize",
            "inferenceProviderId", "assignedSkillsJson", "reasoningEffort");
    }
}
