using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Messaging;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>
/// Resume (plan decision 6): a Queued run with <see cref="WorkflowExecution.PendingResume"/> continues from the
/// first dispatch whose recorded result is missing or unusable, reusing every completed result before it with no
/// executor call and no second charge. A Retry (<c>PendingResume = false</c>) runs the snapshot from step 1 and
/// ignores superseded rows. Every scenario here drives the real engine twice: once to produce the history the
/// way a real run does, once to resume it.
/// </summary>
public class ExecutionResumeTests
{
    // ------------------------------------------------------------------ harness

    /// <summary>One fresh DbContext per scope over the same database, as in production.</summary>
    internal sealed class Scopes(Func<WorkflowEngineDbContext> newDb, IAmazonS3? s3 = null, IStepResultCache? cache = null) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(newDb(), s3, cache);

        private sealed class Scope(WorkflowEngineDbContext db, IAmazonS3? s3, IStepResultCache? cache) : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;
            public void Dispose() => db.Dispose();

            public object? GetService(Type serviceType)
                => serviceType == typeof(WorkflowEngineDbContext) ? db
                : serviceType == typeof(IAmazonS3) ? s3
                : serviceType == typeof(IStepResultCache) ? cache
                : serviceType == typeof(IProjectFileFingerprintProvider) && cache is not null ? new FixedFingerprint()
                : null;
        }
    }

    private sealed class FixedFingerprint : IProjectFileFingerprintProvider
    {
        public Task<string> GetFingerprintAsync(Guid projectId, CancellationToken ct) => Task.FromResult(new string('f', 64));
    }

    /// <summary>A step cache that never hits and records what it was asked to look up and to store.</summary>
    internal sealed class RecordingCache : IStepResultCache
    {
        public ConcurrentQueue<StepCacheKeyInputs> Lookups { get; } = new();
        public ConcurrentQueue<string> Stored { get; } = new();

        public Task<StepCacheLookupResult> TryGetAsync(StepCacheKeyInputs inputs, CancellationToken ct)
        {
            Lookups.Enqueue(inputs);
            return Task.FromResult(StepCacheLookupResult.Miss);
        }

        public Task StoreAsync(StepCacheKeyInputs inputs, StepExecutionResult result, CancellationToken ct)
        {
            Stored.Enqueue(result.Output);
            return Task.CompletedTask;
        }
    }

    internal sealed class Executor(StepType type, Func<StepExecutionContext, Task<StepExecutionResult>> run) : IStepExecutor
    {
        public StepType StepType { get; } = type;
        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context) => run(context);
    }

    internal sealed class NoOpPublisher : IWorkflowEventPublisher
    {
        public int StepCompletedCount;
        public Task PublishExecutionRunningAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepStartedAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, string? inputPreview, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepCompletedAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, StepExecutionResult stepExecutionResult, CancellationToken ct)
        {
            Interlocked.Increment(ref StepCompletedCount);
            return Task.CompletedTask;
        }
        public Task PublishStepProgressAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, string stage, int? percentComplete, CancellationToken ct, int? tokensUsedSoFar = null, int? inputTokensSoFar = null, int? outputTokensSoFar = null) => Task.CompletedTask;
        public Task PublishStepChatTurnAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, int turnIndex, int? totalTurns, string speaker, string speakerRole, string text, IReadOnlyList<string> idsMentioned, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepDiagnosticsAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, StepExecutionResult stepExecutionResult, CancellationToken ct) => Task.CompletedTask;
        public Task PublishExecutionCompletedAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
        public Task PublishExecutionFailedAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Records every reserve/settle/release the engine asks for.</summary>
    internal sealed class RecordingGate : ICreditGate
    {
        public ConcurrentQueue<Guid> Reserved { get; } = new();
        public ConcurrentQueue<Guid> Settled { get; } = new();
        public ConcurrentQueue<(Guid Id, FailureClass Class)> Released { get; } = new();
        private readonly ConcurrentDictionary<Guid, Guid> _byReservation = new();

        public Task<CreditReservation> ReserveAsync(Guid organizationId, Guid executionId, Guid stepResultId, long estimateMillis,
            Guid? projectId = null, CreditEntitlement requires = CreditEntitlement.None, int attempt = 0, CancellationToken ct = default, long? overdraftBasisMillis = null, Guid? apiKeyId = null)
        {
            Reserved.Enqueue(stepResultId);
            Guid reservationId = Guid.NewGuid();
            _byReservation[reservationId] = stepResultId;
            return Task.FromResult(new CreditReservation(CreditReserveOutcome.Allowed, reservationId, ReservedMillis: estimateMillis));
        }

        public Task<CreditSettlement> SettleAsync(Guid? reservationId, long actualMillis, CancellationToken ct = default)
        {
            if (reservationId is { } id)
                Settled.Enqueue(_byReservation[id]);
            return Task.FromResult(new CreditSettlement(true, ChargedMillis: 7));
        }

        public Task<CreditSettlement> ReleaseAsync(Guid? reservationId, FailureClass failureClass, long actualMillis = 0, CancellationToken ct = default)
        {
            if (reservationId is { } id)
                Released.Enqueue((_byReservation[id], failureClass));
            return Task.FromResult(new CreditSettlement(true));
        }

        public Task<int> ReleaseExecutionAsync(Guid executionId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<CreditSettlement> SettleOpenAsync(Guid reserveId, long? recordedMillis, CancellationToken ct = default) => Task.FromResult(new CreditSettlement(true));
        public Task<CreditSettlement> ReleaseOpenAsync(Guid reserveId, FailureClass failureClass, long? recordedMillis, CancellationToken ct = default) => Task.FromResult(new CreditSettlement(true));
    }

    internal static WorkflowExecutorService Service(
        IServiceScopeFactory scopes, ICreditGate? gate, IWorkflowEventPublisher? publisher, ExecutionCancellationRegistry? registry, params IStepExecutor[] executors)
        => Service(scopes, gate, publisher, registry, cached: false, executors);

    internal static WorkflowExecutorService Service(
        IServiceScopeFactory scopes, ICreditGate? gate, IWorkflowEventPublisher? publisher, ExecutionCancellationRegistry? registry, bool cached, params IStepExecutor[] executors) => new(
        scopeFactory: scopes,
        eventPublisher: publisher ?? new NoOpPublisher(),
        logger: NullLogger<WorkflowExecutorService>.Instance,
        executors: executors,
        rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
        hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
        cancellationRegistry: registry ?? new ExecutionCancellationRegistry(),
        cachePolicy: cached ? new StepCachePolicy() : null,
        cacheKeyBuilder: cached ? new StepCacheKeyBuilder() : null,
        cacheOptions: cached ? Options.Create(new StepCacheOptions { Enabled = true, TtlHours = 24 }) : null,
        creditGate: gate)
        {
            CreditCloseRetryDelays = []
        };

    internal sealed record StepSpec(StepType Type, string Label, Action<WorkflowStep>? Configure = null, AgentType AgentType = AgentType.Custom);

    internal sealed record Seeded(Guid ExecutionId, Guid OrganizationId, Guid ProjectId);

    /// <summary>A project with one Standard workflow of <paramref name="specs"/> (StepOrder 1..n) and a queued run of it.</summary>
    internal static Seeded Seed(DbContext db, params StepSpec[] specs) => SeedIn(db, Guid.NewGuid(), specs);

    /// <summary><see cref="Seed"/> in an existing organization (a relational database enforces the FK).</summary>
    internal static Seeded SeedIn(DbContext db, Guid orgId, params StepSpec[] specs)
    {
        Guid ownerId = Guid.NewGuid();
        db.Add(new ApplicationUser { Id = ownerId, Email = $"{ownerId:N}@example.com", DisplayName = "owner" });
        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = orgId };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), Name = "Promo", ProjectId = project.Id, Kind = WorkflowDefinitionKind.Standard };
        Dictionary<AgentType, AgentDefinition> agents = new();
        for (int i = 0; i < specs.Length; i++)
        {
            StepSpec spec = specs[i];
            if (!agents.TryGetValue(spec.AgentType, out AgentDefinition? agent))
            {
                agent = new AgentDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = spec.AgentType + " agent",
                    AgentType = spec.AgentType,
                    OrganizationId = orgId,
                    SystemPrompt = "Do the thing."
                };
                agents[spec.AgentType] = agent;
                db.Add(agent);
            }

            WorkflowStep step = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflow.Id,
                AgentDefinitionId = agent.Id,
                StepOrder = i + 1,
                StepType = spec.Type,
                Label = spec.Label
            };
            spec.Configure?.Invoke(step);
            workflow.Steps.Add(step);
        }

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
        db.Add(workflow);
        db.Add(execution);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return new Seeded(execution.Id, orgId, project.Id);
    }

    /// <summary>What the resume route (T-004) does to the row: Queued, intent on the row, error/completion/lease cleared.</summary>
    internal static async Task RequeueAsync(Func<WorkflowEngineDbContext> newDb, Guid executionId, bool resume, bool supersede = false)
    {
        using WorkflowEngineDbContext db = newDb();
        WorkflowExecution run = await db.WorkflowExecutions.SingleAsync(e => e.Id == executionId);
        run.Status = ExecutionStatus.Queued;
        run.PendingResume = resume;
        run.ResumeCount += 1;
        run.ErrorMessage = null;
        run.CompletedAt = null;
        run.LeaseOwner = null;
        run.LeaseExpiresAt = null;
        if (supersede)
        {
            DateTime now = DateTime.UtcNow;
            foreach (WorkflowStepResult row in await db.WorkflowStepResults.Where(r => r.WorkflowExecutionId == executionId).ToListAsync())
                row.SupersededAt = now;
        }

        await db.SaveChangesAsync();
    }

    internal static StepExecutionResult Done(StepExecutionContext c, string output, string? outputStorageKey = null) => new()
    {
        Output = output,
        NextStepIndex = c.CurrentStepIndex + 1,
        NewIterationCount = c.IterationCount,
        Status = StepStatus.Completed,
        OutputStorageKey = outputStorageKey
    };

    private static Func<WorkflowEngineDbContext> InMemory() => InMemory("resume-" + Guid.NewGuid().ToString("N"));

    private static Func<WorkflowEngineDbContext> InMemory(string name, params IInterceptor[] interceptors) =>
        () => new WorkflowEngineDbContext(
            new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(name).AddInterceptors(interceptors).Options);

    private static async Task<WorkflowExecution> RunRow(Func<WorkflowEngineDbContext> newDb, Guid executionId)
    {
        using WorkflowEngineDbContext db = newDb();
        return await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == executionId);
    }

    private static async Task<List<(string? Label, WorkflowStepResult Row)>> Rows(Func<WorkflowEngineDbContext> newDb, Guid executionId)
    {
        using WorkflowEngineDbContext db = newDb();
        return (await db.WorkflowStepResults.AsNoTracking().Include(r => r.WorkflowStep)
                .Where(r => r.WorkflowExecutionId == executionId).ToListAsync())
            .OrderBy(r => r.ExecutedAt)
            .Select(r => (r.WorkflowStep.Label, r))
            .ToList();
    }

    private static async Task RunExpectingFailure(WorkflowExecutorService service, Guid executionId)
    {
        Func<Task> act = () => service.ExecuteAsync(executionId, "corr", CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();
    }

    // ------------------------------------------------------------------ (a) resume from the failed step, no second charge

    [Fact]
    public async Task A_resume_of_a_run_failed_at_step_3_runs_steps_3_and_4_only_and_charges_only_them()
    {
        Func<WorkflowEngineDbContext> newDb = InMemory();
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
            s = Seed(seed, new(StepType.Agent, "S1"), new(StepType.Agent, "S2"), new(StepType.Agent, "S3"), new(StepType.Agent, "S4"));
        Scopes scopes = new(newDb);

        RecordingGate firstGate = new();
        await RunExpectingFailure(Service(scopes, firstGate, null, null, new Executor(StepType.Agent, c =>
            c.Step.StepOrder == 3
                ? throw new InvalidOperationException("provider down")
                : Task.FromResult(Done(c, "{\"from\":\"" + c.Step.Label + "\"}")))), s.ExecutionId);
        (await RunRow(newDb, s.ExecutionId)).Status.Should().Be(ExecutionStatus.Failed);
        List<Guid> firstRunRowIds = (await Rows(newDb, s.ExecutionId)).Select(r => r.Row.Id).ToList();
        firstRunRowIds.Should().HaveCount(3);

        await RequeueAsync(newDb, s.ExecutionId, resume: true);

        RecordingGate gate = new();
        NoOpPublisher publisher = new();
        List<(string? Label, string Accumulated, List<int> History)> ran = [];
        await Service(scopes, gate, publisher, null, new Executor(StepType.Agent, c =>
        {
            ran.Add((c.Step.Label, c.AccumulatedOutput, c.StepOutputHistory.Select(h => h.StepOrder).ToList()));
            return Task.FromResult(Done(c, "{\"from\":\"" + c.Step.Label + "\"}"));
        })).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        WorkflowExecution run = await RunRow(newDb, s.ExecutionId);
        run.Status.Should().Be(ExecutionStatus.Passed, run.ErrorMessage);
        run.PendingResume.Should().BeFalse("the intent is consumed by the dispatch that honoured it");
        run.ResultJson.Should().Contain("S4");

        ran.Select(r => r.Label).Should().Equal(["S3", "S4"], "steps 1 and 2 are reused, never executed again");
        ran[0].Accumulated.Should().Be("{\"from\":\"S2\"}", "the reused step 2 output feeds step 3 exactly as a live one would");
        ran[0].History.Should().Equal([1, 2]);

        List<(string? Label, WorkflowStepResult Row)> rows = await Rows(newDb, s.ExecutionId);
        rows.Select(r => r.Label).Should().Equal(["S1", "S2", "S3", "S3", "S4"], "a reused result is not copied; the failed row stays");
        Guid[] liveIds = [rows[3].Row.Id, rows[4].Row.Id];
        liveIds.Should().NotIntersectWith(firstRunRowIds, "live steps after the frontier get new step-result ids");
        gate.Reserved.Should().BeEquivalentTo(liveIds, "only steps 3 and 4 hold credits on the resume");
        gate.Settled.Should().BeEquivalentTo(liveIds);
        gate.Released.Should().BeEmpty();
        publisher.StepCompletedCount.Should().Be(2, "a reused result is not re-published as a new completion");
    }

    // ------------------------------------------------------------------ (b) review loop: iteration 1 reused, count continues

    [Theory]
    [InlineData("author")]
    [InlineData("review")]
    public async Task A_review_loop_resumed_in_iteration_2_reuses_iteration_1_and_counts_on_to_MaxIterations(string failIn)
    {
        Func<WorkflowEngineDbContext> newDb = InMemory();
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
        {
            s = Seed(seed,
                new(StepType.Agent, "Author"),
                new(StepType.ReviewLoop, "Review", st =>
                {
                    st.LoopTargetStepOrder = 1;
                    st.MaxIterations = 3;
                    st.MinScore = 9;
                }));
        }
        Scopes scopes = new(newDb);
        const string Review = "{\"overallScore\":4,\"summary\":\"Tighten the hook in the first two seconds.\"}";

        // A faithful stand-in for ReviewLoopStepExecutor's iteration rule: the count goes up by one per review,
        // and a failing score loops back while the count is under MaxIterations.
        StepExecutionResult Reviewed(StepExecutionContext c)
        {
            int next = c.IterationCount + 1;
            bool loop = next < c.Step.MaxIterations;
            return new StepExecutionResult
            {
                Output = Review,
                NextStepIndex = loop ? 0 : c.CurrentStepIndex + 1,
                NewIterationCount = next,
                IterationNumber = next,
                Status = StepStatus.Completed
            };
        }

        await RunExpectingFailure(Service(scopes, new RecordingGate(), null, null,
            new Executor(StepType.Agent, c => failIn == "author" && c.IterationCount == 1
                ? throw new InvalidOperationException("author lost")
                : Task.FromResult(Done(c, "{\"cut\":" + c.IterationCount + "}"))),
            new Executor(StepType.ReviewLoop, c => failIn == "review" && c.IterationCount == 1
                ? throw new InvalidOperationException("reviewer lost")
                : Task.FromResult(Reviewed(c)))), s.ExecutionId);

        await RequeueAsync(newDb, s.ExecutionId, resume: true);

        RecordingGate gate = new();
        List<(string Kind, int Iteration, string Guidance)> ran = [];
        await Service(scopes, gate, null, null,
            new Executor(StepType.Agent, c =>
            {
                ran.Add(("author", c.IterationCount, c.WithRetryGuidance("PROMPT")));
                return Task.FromResult(Done(c, "{\"cut\":" + c.IterationCount + "}"));
            }),
            new Executor(StepType.ReviewLoop, c =>
            {
                ran.Add(("review", c.IterationCount, string.Empty));
                return Task.FromResult(Reviewed(c));
            })).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        WorkflowExecution run = await RunRow(newDb, s.ExecutionId);
        run.Status.Should().Be(ExecutionStatus.Passed, run.ErrorMessage);
        run.IterationCount.Should().Be(3, "the loop stops at MaxIterations, counting the reused iteration");

        if (failIn == "author")
        {
            ran.Select(r => (r.Kind, r.Iteration)).Should().Equal([("author", 1), ("review", 1), ("author", 2), ("review", 2)]);
            ran[0].Guidance.Should().Contain("Tighten the hook", "the reused review's critique reaches the re-run author");
        }
        else
        {
            ran.Select(r => (r.Kind, r.Iteration)).Should().Equal([("review", 1), ("author", 2), ("review", 2)]);
        }

        using WorkflowEngineDbContext check = newDb();
        List<int> scored = await check.ReviewScores.AsNoTracking()
            .Where(x => x.WorkflowExecutionId == s.ExecutionId).Select(x => x.IterationNumber).ToListAsync();
        scored.Order().Should().Equal([1, 2, 3], "each iteration is scored once: the reused review adds no second score");
    }

    [Fact]
    public async Task A_resume_that_re_runs_review_iteration_2_leaves_one_current_score_per_iteration()
    {
        Func<WorkflowEngineDbContext> newDb = InMemory();
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
        {
            s = Seed(seed,
                new(StepType.Agent, "Author"),
                new(StepType.ReviewLoop, "Review", st =>
                {
                    st.LoopTargetStepOrder = 1;
                    st.MaxIterations = 3;
                    st.MinScore = 9;
                }));
        }
        HashSet<string> goneKeys = [];
        Scopes scopes = new(newDb, Storage(goneKeys));

        StepExecutionResult Reviewed(StepExecutionContext c, int score)
        {
            int next = c.IterationCount + 1;
            return new StepExecutionResult
            {
                Output = "{\"overallScore\":" + score + ",\"summary\":\"again\"}",
                NextStepIndex = next < c.Step.MaxIterations ? 0 : c.CurrentStepIndex + 1,
                NewIterationCount = next,
                IterationNumber = next,
                Status = StepStatus.Completed
            };
        }

        // Run: Author, Review 1, Author (render R2), Review 2, Author fails in iteration 3.
        List<string> renders = [];
        await RunExpectingFailure(Service(scopes, null, null, null,
            new Executor(StepType.Agent, c =>
            {
                if (c.IterationCount == 2)
                    throw new InvalidOperationException("lost");
                string key = RenderKey(c);
                renders.Add(key);
                return Task.FromResult(Done(c, "{}", key));
            }),
            new Executor(StepType.ReviewLoop, c => Task.FromResult(Reviewed(c, 4)))), s.ExecutionId);

        // The iteration-2 render is deleted, so the resume re-runs the Author's iteration 2 and its review.
        goneKeys.Add(renders[1]);
        await RequeueAsync(newDb, s.ExecutionId, resume: true);
        await Service(scopes, null, null, null,
            new Executor(StepType.Agent, c => Task.FromResult(Done(c, "{}", RenderKey(c)))),
            new Executor(StepType.ReviewLoop, c => Task.FromResult(Reviewed(c, 6)))).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        (await RunRow(newDb, s.ExecutionId)).Status.Should().Be(ExecutionStatus.Passed);
        using WorkflowEngineDbContext check = newDb();
        List<ReviewScore> scores = await check.ReviewScores.AsNoTracking().Where(x => x.WorkflowExecutionId == s.ExecutionId).ToListAsync();
        List<WorkflowStepResult> results = await check.WorkflowStepResults.AsNoTracking().Where(x => x.WorkflowExecutionId == s.ExecutionId).ToListAsync();
        scores.Select(x => x.IterationNumber).Order().Should().Equal([1, 2, 2, 3], "the replaced iteration-2 score is kept for audit");

        List<ReviewScore> current = [.. ReelBolt.Shared.Workflows.ReviewScoreLineage.Current(scores, x => x.CreatedAt, results)];
        current.Select(x => (x.IterationNumber, x.Score)).OrderBy(x => x.IterationNumber)
            .Should().Equal([(1, 4), (2, 6), (3, 6)], "one score per iteration: iteration 1 reused, 2 and 3 from the resume");

        // Every score now records the ReviewLoop result that produced it (review_scores.step_result_id), and
        // resolving by that id gives the same current set as the timestamp link.
        HashSet<Guid> reviewSteps = [.. await check.WorkflowSteps.AsNoTracking().Where(x => x.StepType == StepType.ReviewLoop).Select(x => x.Id).ToListAsync()];
        scores.Should().OnlyContain(x => x.StepResultId != null
            && results.Any(r => r.Id == x.StepResultId && reviewSteps.Contains(r.WorkflowStepId)));
        List<ReviewScore> byId = [.. ReelBolt.Shared.Workflows.ReviewScoreLineage.Current(scores, x => x.CreatedAt, results, x => x.StepResultId)];
        byId.Select(x => x.Id).Should().BeEquivalentTo(current.Select(x => x.Id));
    }

    // ------------------------------------------------------------------ (c) conditional replays the recorded branch

    [Fact]
    public async Task A_reused_conditional_takes_the_branch_it_took_before_without_re_deciding()
    {
        Func<WorkflowEngineDbContext> newDb = InMemory();
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
        {
            s = Seed(seed,
                new(StepType.Agent, "Draft"),
                new(StepType.Conditional, "Branch", st =>
                {
                    st.TrueBranchStepOrder = "4";
                    st.FalseBranchStepOrder = "3";
                }),
                new(StepType.Agent, "False path"),
                new(StepType.Agent, "True path"),
                new(StepType.Agent, "End"));
        }
        Scopes scopes = new(newDb);

        StepExecutionResult Branch(StepExecutionContext c, int toOrder) => new()
        {
            Output = c.AccumulatedOutput,
            NextStepIndex = c.AllSteps.FindIndex(x => x.StepOrder == toOrder),
            NewIterationCount = c.IterationCount,
            Status = StepStatus.Completed
        };

        await RunExpectingFailure(Service(scopes, new RecordingGate(), null, null,
            new Executor(StepType.Agent, c => c.Step.Label == "True path"
                ? throw new InvalidOperationException("lost")
                : Task.FromResult(Done(c, "{}"))),
            new Executor(StepType.Conditional, c => Task.FromResult(Branch(c, 4)))), s.ExecutionId);

        await RequeueAsync(newDb, s.ExecutionId, resume: true);

        int conditionalCalls = 0;
        List<string?> ran = [];
        await Service(scopes, new RecordingGate(), null, null,
            new Executor(StepType.Agent, c =>
            {
                ran.Add(c.Step.Label);
                return Task.FromResult(Done(c, "{}"));
            }),
            new Executor(StepType.Conditional, c =>
            {
                conditionalCalls++;
                return Task.FromResult(Branch(c, 3)); // would now pick the other branch
            })).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        (await RunRow(newDb, s.ExecutionId)).Status.Should().Be(ExecutionStatus.Passed);
        conditionalCalls.Should().Be(0);
        ran.Should().Equal(["True path", "End"]);
    }

    // ------------------------------------------------------------------ (d) a re-published Queued resume resumes

    /// <summary>Throws on the first save that moves an execution to Running: the engine dies at the start transition.</summary>
    private sealed class DieOnFirstRunningSave : SaveChangesInterceptor
    {
        private int _fired;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            bool starting = eventData.Context!.ChangeTracker.Entries<WorkflowExecution>()
                .Any(e => e.State == EntityState.Modified && e.Entity.Status == ExecutionStatus.Running);
            if (starting && Interlocked.Exchange(ref _fired, 1) == 0)
                throw new InvalidOperationException("engine lost before the Running save");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task The_sweepers_Queued_re_publish_of_a_pending_resume_resumes_and_never_restarts()
    {
        string store = "resume-" + Guid.NewGuid().ToString("N");
        Func<WorkflowEngineDbContext> newDb = InMemory(store);
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
            s = Seed(seed, new(StepType.Agent, "S1"), new(StepType.Agent, "S2"), new(StepType.Agent, "S3"));
        Scopes scopes = new(newDb);

        await RunExpectingFailure(Service(scopes, null, null, null, new Executor(StepType.Agent, c =>
            c.Step.StepOrder == 2 ? throw new InvalidOperationException("lost") : Task.FromResult(Done(c, "{}")))), s.ExecutionId);
        await RequeueAsync(newDb, s.ExecutionId, resume: true);

        // The same database, behind an engine that dies at its first start transition.
        Func<WorkflowEngineDbContext> dyingDb = InMemory(store, new DieOnFirstRunningSave());
        List<string?> ran = [];
        WorkflowExecutorService service = Service(new Scopes(dyingDb), null, null, null, new Executor(StepType.Agent, c =>
        {
            ran.Add(c.Step.Label);
            return Task.FromResult(Done(c, "{}"));
        }));

        // Delivery 1 dies at the start transition: the row stays Queued with its resume intent.
        await RunExpectingFailure(service, s.ExecutionId);
        WorkflowExecution afterLoss = await RunRow(newDb, s.ExecutionId);
        afterLoss.Status.Should().Be(ExecutionStatus.Queued);
        afterLoss.PendingResume.Should().BeTrue("the intent lives on the row until a dispatch actually starts");

        // Delivery 2 (the sweeper's re-publish) resumes; delivery 3 (a duplicate) is a no-op.
        await service.ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);
        await service.ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        WorkflowExecution run = await RunRow(newDb, s.ExecutionId);
        run.Status.Should().Be(ExecutionStatus.Passed, run.ErrorMessage);
        run.PendingResume.Should().BeFalse();
        ran.Should().Equal(["S2", "S3"], "S1 is reused: the re-publish never restarts the run from step 1");
        (await Rows(newDb, s.ExecutionId)).Count(r => r.Label == "S1").Should().Be(1);
    }

    // ------------------------------------------------------------------ (e) a reused row whose storage is gone re-runs

    /// <summary>A render key in T-008's shape: one object per step result, so a re-run never overwrites a deleted one.</summary>
    private static string RenderKey(StepExecutionContext c) =>
        $"projects/{c.Execution.ProjectId}/outputFiles/{c.Execution.Id}/{c.StepResultId:N}/cut.mp4";

    /// <summary>Fake storage: every key HEADs except the ones in <paramref name="gone"/>.</summary>
    private static IAmazonS3 Storage(HashSet<string> gone)
    {
        Mock<IAmazonS3> s3 = new();
        s3.Setup(x => x.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string key, CancellationToken _) => gone.Contains(key)
                ? throw new AmazonS3Exception("gone") { StatusCode = HttpStatusCode.NotFound }
                : Task.FromResult(new GetObjectMetadataResponse()));
        return s3.Object;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_reused_row_whose_stored_object_is_gone_re_runs_that_step(bool gone)
    {
        Func<WorkflowEngineDbContext> newDb = InMemory();
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
            s = Seed(seed, new(StepType.Agent, "Render"), new(StepType.Agent, "Caption"), new(StepType.Agent, "Publish"));
        HashSet<string> goneKeys = [];
        Scopes scopes = new(newDb, Storage(goneKeys));

        List<string> firstRenders = [];
        await RunExpectingFailure(Service(scopes, null, null, null, new Executor(StepType.Agent, c =>
        {
            if (c.Step.StepOrder == 3)
                throw new InvalidOperationException("lost");
            string? key = c.Step.StepOrder == 1 ? RenderKey(c) : null;
            if (key is not null)
                firstRenders.Add(key);
            return Task.FromResult(Done(c, "{\"render\":true}", key));
        })), s.ExecutionId);
        if (gone)
            goneKeys.Add(firstRenders.Single()); // a retention sweep (or a hand cleanup) deleted the first render
        await RequeueAsync(newDb, s.ExecutionId, resume: true);

        List<string?> ran = [];
        await Service(scopes, null, null, null, new Executor(StepType.Agent, c =>
        {
            ran.Add(c.Step.Label);
            return Task.FromResult(Done(c, "{}", c.Step.StepOrder == 1 ? RenderKey(c) : null));
        })).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        (await RunRow(newDb, s.ExecutionId)).Status.Should().Be(ExecutionStatus.Passed);
        if (gone)
            ran.Should().Equal(["Render", "Caption", "Publish"], "the first miss switches to live mode for the rest of the run");
        else
            ran.Should().Equal(["Publish"]);
    }

    [Theory]
    [InlineData("other-project")]
    [InlineData("no-prefix")]
    [InlineData("prefix-lookalike")]
    public async Task A_reused_row_whose_stored_key_is_outside_this_project_is_a_miss_and_re_runs(string shape)
    {
        Func<WorkflowEngineDbContext> newDb = InMemory();
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
            s = Seed(seed, new(StepType.Agent, "Render"), new(StepType.Agent, "Publish"));
        Mock<IAmazonS3> s3 = new();
        s3.Setup(x => x.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectMetadataResponse()); // every object exists: only the scope can make it a miss
        Scopes scopes = new(newDb, s3.Object);

        string foreignKey = shape switch
        {
            "other-project" => $"projects/{Guid.NewGuid()}/outputFiles/{s.ExecutionId}/{Guid.NewGuid():N}/cut.mp4",
            "no-prefix" => $"outputFiles/{s.ExecutionId}/{Guid.NewGuid():N}/cut.mp4",
            _ => $"projects/{s.ProjectId}x/outputFiles/{s.ExecutionId}/cut.mp4"
        };
        await RunExpectingFailure(Service(scopes, null, null, null, new Executor(StepType.Agent, c =>
            c.Step.StepOrder == 2
                ? throw new InvalidOperationException("lost")
                : Task.FromResult(Done(c, "{\"render\":true}", foreignKey)))), s.ExecutionId);
        await RequeueAsync(newDb, s.ExecutionId, resume: true);

        List<string?> ran = [];
        await Service(scopes, null, null, null, new Executor(StepType.Agent, c =>
        {
            ran.Add(c.Step.Label);
            return Task.FromResult(Done(c, "{}", c.Step.StepOrder == 1 ? RenderKey(c) : null));
        })).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        (await RunRow(newDb, s.ExecutionId)).Status.Should().Be(ExecutionStatus.Passed);
        ran.Should().Equal(["Render", "Publish"], "a key outside projects/{projectId}/ is never handed on as this run's output");
        s3.Verify(x => x.GetObjectMetadataAsync(It.IsAny<string>(), foreignKey, It.IsAny<CancellationToken>()), Times.Never,
            "a foreign key is refused before any request is made for it");
    }

    [Fact]
    public async Task A_second_resume_after_a_mid_run_miss_charges_nothing_for_what_the_first_resume_completed()
    {
        Func<WorkflowEngineDbContext> newDb = InMemory();
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
            s = Seed(seed, new(StepType.Agent, "Plan"), new(StepType.Agent, "Render"), new(StepType.Agent, "Caption"), new(StepType.Agent, "Publish"));
        HashSet<string> goneKeys = [];
        Scopes scopes = new(newDb, Storage(goneKeys));
        StepExecutionResult Step(StepExecutionContext c) => Done(c, "{\"step\":" + c.Step.StepOrder + "}", c.Step.StepOrder == 2 ? RenderKey(c) : null);

        // Run: Plan, Render (key K1), Caption, Publish fails.
        List<string> renders = [];
        await RunExpectingFailure(Service(scopes, new RecordingGate(), null, null, new Executor(StepType.Agent, c =>
        {
            if (c.Step.StepOrder == 4)
                throw new InvalidOperationException("lost");
            StepExecutionResult r = Step(c);
            if (r.OutputStorageKey is { } k)
                renders.Add(k);
            return Task.FromResult(r);
        })), s.ExecutionId);

        // K1 is deleted. Resume 1: Plan reused, Render misses (K1 gone), Render/Caption re-run live, Publish fails again.
        goneKeys.Add(renders.Single());
        await RequeueAsync(newDb, s.ExecutionId, resume: true);
        List<string?> first = [];
        await RunExpectingFailure(Service(scopes, new RecordingGate(), null, null, new Executor(StepType.Agent, c =>
        {
            first.Add(c.Step.Label);
            return c.Step.StepOrder == 4 ? throw new InvalidOperationException("lost again") : Task.FromResult(Step(c));
        })), s.ExecutionId);
        first.Should().Equal(["Render", "Caption", "Publish"]);

        // Resume 2: everything resume 1 completed is reused; only Publish runs and holds credits.
        await RequeueAsync(newDb, s.ExecutionId, resume: true);
        RecordingGate gate = new();
        List<string?> second = [];
        await Service(scopes, gate, null, null, new Executor(StepType.Agent, c =>
        {
            second.Add(c.Step.Label);
            return Task.FromResult(Step(c));
        })).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        (await RunRow(newDb, s.ExecutionId)).Status.Should().Be(ExecutionStatus.Passed);
        second.Should().Equal(["Publish"], "Render and Caption completed in resume 1 and are reused, not re-run");
        gate.Reserved.Should().HaveCount(1, "a repeat resume never re-charges a step the previous resume completed");

        List<(string? Label, WorkflowStepResult Row)> rows = await Rows(newDb, s.ExecutionId);
        rows.Where(r => r.Row.SupersededAt is not null).Select(r => r.Label)
            .Should().Equal(["Render", "Caption", "Publish", "Publish"],
                "resume 1's live run replaced the old attempt from its miss (Render) on, and resume 2's replaced its failed Publish");
        rows.Should().HaveCount(8, "nothing is deleted");
    }

    // ------------------------------------------------------------------ (f) superseded rows are ignored

    [Fact]
    public async Task A_retry_runs_from_step_1_and_ignores_its_superseded_rows()
    {
        Func<WorkflowEngineDbContext> newDb = InMemory();
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
            s = Seed(seed, new(StepType.Agent, "S1"), new(StepType.Agent, "S2"), new(StepType.Agent, "S3"));
        Scopes scopes = new(newDb);

        await RunExpectingFailure(Service(scopes, null, null, null, new Executor(StepType.Agent, c =>
            c.Step.StepOrder == 3 ? throw new InvalidOperationException("lost") : Task.FromResult(Done(c, "{}")))), s.ExecutionId);

        // Retry (Retry): every existing row superseded, no resume intent. Step 2 fails this time.
        await RequeueAsync(newDb, s.ExecutionId, resume: false, supersede: true);
        List<string?> retried = [];
        await RunExpectingFailure(Service(scopes, null, null, null, new Executor(StepType.Agent, c =>
        {
            retried.Add(c.Step.Label);
            return c.Step.StepOrder == 2 ? throw new InvalidOperationException("lost again") : Task.FromResult(Done(c, "{}"));
        })), s.ExecutionId);
        retried.Should().Equal(["S1", "S2"], "a retry runs the snapshot from step 1");

        // A resume after that retry must not reuse the superseded step-2 success from the first attempt.
        await RequeueAsync(newDb, s.ExecutionId, resume: true);
        List<string?> resumed = [];
        await Service(scopes, null, null, null, new Executor(StepType.Agent, c =>
        {
            resumed.Add(c.Step.Label);
            return Task.FromResult(Done(c, "{}"));
        })).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        (await RunRow(newDb, s.ExecutionId)).Status.Should().Be(ExecutionStatus.Passed);
        resumed.Should().Equal(["S2", "S3"]);
        List<(string? Label, WorkflowStepResult Row)> rows = await Rows(newDb, s.ExecutionId);
        rows.Should().HaveCount(7, "nothing in the engine deletes a result");
        rows.Count(r => r.Row.SupersededAt is not null).Should().Be(4,
            "the retry superseded the first attempt's 3 rows, and the resume's live run replaced the retry's failed S2");
    }

    [Fact]
    public async Task A_retry_does_not_count_a_superseded_author_render_as_this_attempts_artifact()
    {
        Func<WorkflowEngineDbContext> newDb = InMemory();
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
            s = Seed(seed, new StepSpec(StepType.Agent, "Author", AgentType: AgentType.AuthorAgent));
        Scopes scopes = new(newDb);

        await Service(scopes, null, null, null, new Executor(StepType.Agent, c => Task.FromResult(Done(c, "{}", "projects/x/render.mp4"))))
            .ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);
        (await RunRow(newDb, s.ExecutionId)).Status.Should().Be(ExecutionStatus.Passed);

        await RequeueAsync(newDb, s.ExecutionId, resume: false, supersede: true);
        await RunExpectingFailure(Service(scopes, null, null, null, new Executor(StepType.Agent, c => Task.FromResult(Done(c, "{}")))), s.ExecutionId);

        WorkflowExecution run = await RunRow(newDb, s.ExecutionId);
        run.Status.Should().Be(ExecutionStatus.Failed);
        run.ErrorMessage.Should().Contain("without producing a rendered video");
    }

    // ------------------------------------------------------------------ (g) a cancelled run resumes from the cancelled step

    [Fact]
    public async Task A_run_the_user_cancelled_resumes_from_the_cancelled_step()
    {
        Func<WorkflowEngineDbContext> newDb = InMemory();
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
            s = Seed(seed, new(StepType.Agent, "S1"), new(StepType.Agent, "S2"), new(StepType.Agent, "S3"));
        Scopes scopes = new(newDb);
        ExecutionCancellationRegistry registry = new();

        WorkflowExecutorService? first = null;
        first = Service(scopes, new RecordingGate(), null, registry, new Executor(StepType.Agent, async c =>
        {
            if (c.Step.StepOrder == 2)
            {
                await first!.CancelExecutionAsync(s.ExecutionId, Guid.NewGuid());
                c.CancellationToken.ThrowIfCancellationRequested();
            }

            return Done(c, "{}");
        }));
        await first.ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        (await RunRow(newDb, s.ExecutionId)).Status.Should().Be(ExecutionStatus.Cancelled);
        (await Rows(newDb, s.ExecutionId)).Single(r => r.Label == "S2").Row.FailureClass.Should().Be(FailureClass.UserCancelled);

        await RequeueAsync(newDb, s.ExecutionId, resume: true);
        List<string?> ran = [];
        await Service(scopes, new RecordingGate(), null, null, new Executor(StepType.Agent, c =>
        {
            ran.Add(c.Step.Label);
            return Task.FromResult(Done(c, "{}"));
        })).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        (await RunRow(newDb, s.ExecutionId)).Status.Should().Be(ExecutionStatus.Passed);
        ran.Should().Equal(["S2", "S3"]);
    }

    // ------------------------------------------------------------------ a second resume

    [Fact]
    public async Task A_second_resume_reuses_what_the_first_resume_completed_and_skips_the_failed_attempts()
    {
        Func<WorkflowEngineDbContext> newDb = InMemory();
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
            s = Seed(seed, new(StepType.Agent, "S1"), new(StepType.Agent, "S2"), new(StepType.Agent, "S3"), new(StepType.Agent, "S4"));
        Scopes scopes = new(newDb);

        // Run: S1, S2, S3 fails. Resume 1: S3 fails again. Resume 2: S3 passes, S4 fails.
        await RunExpectingFailure(Service(scopes, null, null, null, new Executor(StepType.Agent, c =>
            c.Step.StepOrder == 3 ? throw new InvalidOperationException("lost") : Task.FromResult(Done(c, "{}")))), s.ExecutionId);
        await RequeueAsync(newDb, s.ExecutionId, resume: true);
        await RunExpectingFailure(Service(scopes, null, null, null, new Executor(StepType.Agent, c =>
            c.Step.StepOrder == 3 ? throw new InvalidOperationException("lost again") : Task.FromResult(Done(c, "{}")))), s.ExecutionId);
        await RequeueAsync(newDb, s.ExecutionId, resume: true);
        List<string?> second = [];
        await RunExpectingFailure(Service(scopes, null, null, null, new Executor(StepType.Agent, c =>
        {
            second.Add(c.Step.Label);
            return c.Step.StepOrder == 4 ? throw new InvalidOperationException("lost") : Task.FromResult(Done(c, "{\"s3\":\"new\"}"));
        })), s.ExecutionId);
        second.Should().Equal(["S3", "S4"]);

        await RequeueAsync(newDb, s.ExecutionId, resume: true);
        List<(string? Label, string Accumulated)> third = [];
        await Service(scopes, null, null, null, new Executor(StepType.Agent, c =>
        {
            third.Add((c.Step.Label, c.AccumulatedOutput));
            return Task.FromResult(Done(c, "{}"));
        })).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        (await RunRow(newDb, s.ExecutionId)).Status.Should().Be(ExecutionStatus.Passed);
        third.Should().Equal([("S4", "{\"s3\":\"new\"}")], "S3's completed row from the second resume is reused past its failed attempts");
    }

    // ------------------------------------------------------------------ a branch with no recorded successor

    [Fact]
    public async Task A_conditional_whose_next_step_was_never_recorded_is_re_decided()
    {
        Func<WorkflowEngineDbContext> newDb = InMemory();
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
        {
            s = Seed(seed,
                new(StepType.Agent, "Draft"),
                new(StepType.Conditional, "Branch", st =>
                {
                    st.TrueBranchStepOrder = "3";
                    st.FalseBranchStepOrder = "3";
                }),
                new(StepType.Agent, "After"));
        }
        Scopes scopes = new(newDb);
        StepExecutionResult Branch(StepExecutionContext c) => new()
        {
            Output = c.AccumulatedOutput,
            NextStepIndex = 2,
            NewIterationCount = c.IterationCount,
            Status = StepStatus.Completed
        };

        await RunExpectingFailure(Service(scopes, null, null, null,
            new Executor(StepType.Agent, c => c.Step.Label == "After" ? throw new InvalidOperationException("lost") : Task.FromResult(Done(c, "{}"))),
            new Executor(StepType.Conditional, c => Task.FromResult(Branch(c)))), s.ExecutionId);

        // Hide the successor row (as if the engine died between the branch and the next dispatch).
        using (WorkflowEngineDbContext db = newDb())
        {
            WorkflowStepResult after = await db.WorkflowStepResults.Include(r => r.WorkflowStep)
                .SingleAsync(r => r.WorkflowExecutionId == s.ExecutionId && r.WorkflowStep.Label == "After");
            after.SupersededAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        await RequeueAsync(newDb, s.ExecutionId, resume: true);
        List<string?> ran = [];
        await Service(scopes, null, null, null,
            new Executor(StepType.Agent, c =>
            {
                ran.Add(c.Step.Label);
                return Task.FromResult(Done(c, "{}"));
            }),
            new Executor(StepType.Conditional, c =>
            {
                ran.Add(c.Step.Label);
                return Task.FromResult(Branch(c));
            })).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        ran.Should().Equal(["Branch", "After"], "an unknown decision is taken again rather than guessed");
    }

    // ------------------------------------------------------------------ encode once after the review

    /// <summary>Records the hold each step result was asked to reserve.</summary>
    private sealed class EstimateGate : ICreditGate
    {
        public ConcurrentDictionary<Guid, long> Estimates { get; } = new();

        /// <summary>Every released reservation with the class it was refunded under.</summary>
        public ConcurrentQueue<(Guid? ReservationId, FailureClass FailureClass, long ActualMillis)> Releases { get; } = new();

        public Task<CreditReservation> ReserveAsync(Guid organizationId, Guid executionId, Guid stepResultId, long estimateMillis,
            Guid? projectId = null, CreditEntitlement requires = CreditEntitlement.None, int attempt = 0, CancellationToken ct = default, long? overdraftBasisMillis = null, Guid? apiKeyId = null)
        {
            Estimates[stepResultId] = estimateMillis;
            return Task.FromResult(new CreditReservation(CreditReserveOutcome.Allowed, Guid.NewGuid(), ReservedMillis: estimateMillis));
        }

        public Task<CreditSettlement> SettleAsync(Guid? reservationId, long actualMillis, CancellationToken ct = default) => Task.FromResult(new CreditSettlement(true, ChargedMillis: 7));
        public Task<CreditSettlement> ReleaseAsync(Guid? reservationId, FailureClass failureClass, long actualMillis = 0, CancellationToken ct = default)
        {
            Releases.Enqueue((reservationId, failureClass, actualMillis));
            return Task.FromResult(new CreditSettlement(true));
        }
        public Task<int> ReleaseExecutionAsync(Guid executionId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<CreditSettlement> SettleOpenAsync(Guid reserveId, long? recordedMillis, CancellationToken ct = default) => Task.FromResult(new CreditSettlement(true));
        public Task<CreditSettlement> ReleaseOpenAsync(Guid reserveId, FailureClass failureClass, long? recordedMillis, CancellationToken ct = default) => Task.FromResult(new CreditSettlement(true));
    }

    private const string PlanConfig = """{"version":1,"decision":{"from":"Previous"},"analysisStepOrder":1,"encodeStage":"Plan"}""";
    private const string FinalConfig = """{"version":1,"decision":{"from":"Step","stepOrder":1},"analysisStepOrder":1,"encodeStage":"Final"}""";

    private static Seeded SeedEncodeOnce(DbContext db, int maxIterations = 3) => Seed(db,
        new(StepType.Agent, "Editor"),
        new(StepType.VideoCompile, "Compile", st => st.VideoCompileConfigJson = PlanConfig),
        new(StepType.ReviewLoop, "Review", st =>
        {
            st.LoopTargetStepOrder = 1;
            st.MaxIterations = maxIterations;
            st.MinScore = 9;
        }),
        new(StepType.VideoCompile, "Render", st => st.VideoCompileConfigJson = FinalConfig));

    /// <summary>The compile stand-in: a plan pass writes no file, the final one does. Counts both.</summary>
    private sealed class FakeCompile(Action<string> ran, Func<StepExecutionContext, bool>? failFinal = null)
    {
        public int Planned;
        public int Encoded;

        public Task<StepExecutionResult> Run(StepExecutionContext c)
        {
            ran(c.Step.Label!);
            if (CompileEncodeStages.IsPlanStage(c.Step))
            {
                Planned++;
                return Task.FromResult(Done(c, "{\"status\":\"completed\",\"planOnly\":true,\"outputDurationSec\":42}"));
            }

            if (failFinal?.Invoke(c) == true)
                throw new InvalidOperationException("encode lost");

            Encoded++;
            return Task.FromResult(Done(c, "{\"status\":\"completed\"}"));
        }
    }

    private static StepExecutionResult Reviewed(StepExecutionContext c)
    {
        int next = c.IterationCount + 1;
        bool loop = next < c.Step.MaxIterations;
        return new StepExecutionResult
        {
            Output = "{\"overallScore\":4,\"summary\":\"Tighten the hook.\"}",
            NextStepIndex = loop ? 0 : c.CurrentStepIndex + 1,
            NewIterationCount = next,
            IterationNumber = next,
            Status = StepStatus.Completed,
            ReviewExhausted = loop ? null : new ReviewExhausted(c.Step.StepOrder, 4, 9, next)
        };
    }

    [Fact]
    public async Task An_encode_once_run_plans_every_review_pass_encodes_once_after_the_review_and_holds_only_that_encode()
    {
        Func<WorkflowEngineDbContext> newDb = InMemory();
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
            s = SeedEncodeOnce(seed);
        List<string> ran = [];
        FakeCompile compile = new(ran.Add);
        EstimateGate gate = new();

        await Service(new Scopes(newDb), gate, null, null,
            new Executor(StepType.Agent, c => { ran.Add(c.Step.Label!); return Task.FromResult(Done(c, "{\"keep\":[]}")); }),
            new Executor(StepType.VideoCompile, compile.Run),
            new Executor(StepType.ReviewLoop, c => { ran.Add(c.Step.Label!); return Task.FromResult(Reviewed(c)); }))
            .ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        WorkflowExecution run = await RunRow(newDb, s.ExecutionId);
        run.Status.Should().Be(ExecutionStatus.Passed, run.ErrorMessage);
        ran.Should().Equal(
            "Editor", "Compile", "Review", "Editor", "Compile", "Review", "Editor", "Compile", "Review", "Render");
        compile.Planned.Should().Be(3);
        compile.Encoded.Should().Be(1, "three review passes, one encode");
        run.AttentionJson.Should().NotBeNullOrEmpty("the exhausted review still needs attention, with the encode after it");

        List<(string? Label, WorkflowStepResult Row)> rows = await Rows(newDb, s.ExecutionId);
        rows.Where(r => r.Label == "Render").Should().ContainSingle().Which.Row.Status.Should().Be(StepStatus.Completed);
        foreach ((string? label, WorkflowStepResult row) in rows.Where(r => r.Label == "Compile"))
            gate.Estimates[row.Id].Should().Be(0, "a plan pass reserves nothing");
        gate.Estimates[rows.Single(r => r.Label == "Render").Row.Id].Should().BePositive("the one encode is held");
    }

    [Fact]
    public async Task A_failed_final_encode_resumes_at_the_encode_alone_without_planning_or_reviewing_again()
    {
        Func<WorkflowEngineDbContext> newDb = InMemory();
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
            s = SeedEncodeOnce(seed, maxIterations: 2);
        Scopes scopes = new(newDb);
        List<string> first = [];
        FakeCompile failing = new(first.Add, _ => true);
        EstimateGate firstGate = new();

        await RunExpectingFailure(Service(scopes, firstGate, null, null,
            new Executor(StepType.Agent, c => { first.Add(c.Step.Label!); return Task.FromResult(Done(c, "{}")); }),
            new Executor(StepType.VideoCompile, failing.Run),
            new Executor(StepType.ReviewLoop, c => { first.Add(c.Step.Label!); return Task.FromResult(Reviewed(c)); })), s.ExecutionId);
        failing.Encoded.Should().Be(0);
        firstGate.Releases.Should().ContainSingle("the one encode's hold is the only one left open when it fails")
            .Which.FailureClass.Should().Be(FailureClass.PlatformInternal, "an encode failure is ours, so the hold is refunded");
        firstGate.Releases.Single().ActualMillis.Should().Be(0, "the failed encode consumed nothing billable");

        await RequeueAsync(newDb, s.ExecutionId, resume: true);
        List<string> second = [];
        FakeCompile compile = new(second.Add);
        await Service(scopes, new EstimateGate(), null, null,
            new Executor(StepType.Agent, c => { second.Add(c.Step.Label!); return Task.FromResult(Done(c, "{}")); }),
            new Executor(StepType.VideoCompile, compile.Run),
            new Executor(StepType.ReviewLoop, c => { second.Add(c.Step.Label!); return Task.FromResult(Reviewed(c)); }))
            .ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        (await RunRow(newDb, s.ExecutionId)).Status.Should().Be(ExecutionStatus.Passed);
        second.Should().Equal(["Render"], "the recorded plans and reviews are reused; only the encode runs");
        compile.Planned.Should().Be(0);
        compile.Encoded.Should().Be(1);
    }

    [Fact]
    public async Task A_resume_that_lands_between_the_review_and_the_final_encode_decides_the_review_again_then_encodes_once()
    {
        Func<WorkflowEngineDbContext> newDb = InMemory();
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
            s = SeedEncodeOnce(seed, maxIterations: 1);
        Scopes scopes = new(newDb);
        FakeCompile failing = new(_ => { }, _ => true);

        await RunExpectingFailure(Service(scopes, null, null, null,
            new Executor(StepType.Agent, c => Task.FromResult(Done(c, "{}"))),
            new Executor(StepType.VideoCompile, failing.Run),
            new Executor(StepType.ReviewLoop, c => Task.FromResult(Reviewed(c)))), s.ExecutionId);

        // As if the engine died after the review row and before the encode's row was written.
        using (WorkflowEngineDbContext db = newDb())
        {
            WorkflowStepResult render = await db.WorkflowStepResults.Include(r => r.WorkflowStep)
                .SingleAsync(r => r.WorkflowExecutionId == s.ExecutionId && r.WorkflowStep.Label == "Render");
            render.SupersededAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        await RequeueAsync(newDb, s.ExecutionId, resume: true);
        List<string> ran = [];
        FakeCompile compile = new(ran.Add);
        await Service(scopes, null, null, null,
            new Executor(StepType.Agent, c => { ran.Add(c.Step.Label!); return Task.FromResult(Done(c, "{}")); }),
            new Executor(StepType.VideoCompile, compile.Run),
            new Executor(StepType.ReviewLoop, c => { ran.Add(c.Step.Label!); return Task.FromResult(Reviewed(c)); }))
            .ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        (await RunRow(newDb, s.ExecutionId)).Status.Should().Be(ExecutionStatus.Passed);
        ran.Should().Equal(["Review", "Render"], "an unrecorded decision is taken again, and the encode then runs exactly once");
        compile.Encoded.Should().Be(1);
    }

    // ------------------------------------------------------------------ T-002 archives are never run from

    /// <summary>
    /// What T-002's WorkflowStepReplacer does to a legacy (snapshot-less) run when its workflow is edited: the steps
    /// its results reference move to a kind-5 archive with no owning execution, and the run points at it. Returns
    /// the archive's step ids; the run has one completed result on the archived first step.
    /// </summary>
    private static async Task<List<Guid>> ArchiveAsync(Func<WorkflowEngineDbContext> newDb, Seeded s, bool deleteSource)
    {
        using WorkflowEngineDbContext db = newDb();
        WorkflowExecution run = await db.WorkflowExecutions.Include(e => e.WorkflowDefinition).ThenInclude(w => w.Steps)
            .SingleAsync(e => e.Id == s.ExecutionId);
        WorkflowDefinition archive = new() { Id = Guid.NewGuid(), Name = "Promo (archived steps)", ProjectId = s.ProjectId, Kind = WorkflowDefinitionKind.ExecutionSnapshot };
        foreach (WorkflowStep step in run.WorkflowDefinition.Steps)
        {
            archive.Steps.Add(new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = archive.Id,
                AgentDefinitionId = step.AgentDefinitionId,
                StepOrder = step.StepOrder,
                StepType = step.StepType,
                Label = "archived " + step.Label
            });
        }

        db.WorkflowDefinitions.Add(archive);
        WorkflowStep first = archive.Steps.Single(x => x.StepOrder == 1);
        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = first.Id,
            Output = "{}",
            Status = StepStatus.Completed,
            ExecutedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow.AddMinutes(-5)
        });
        run.SnapshotDefinitionId = archive.Id;
        if (deleteSource)
            run.WorkflowDefinitionId = archive.Id;
        run.Status = ExecutionStatus.Failed;
        await db.SaveChangesAsync();
        return archive.Steps.Select(x => x.Id).ToList();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_run_pointing_at_a_T002_archive_takes_a_fresh_snapshot_and_never_resumes_from_the_archive(bool resume)
    {
        Func<WorkflowEngineDbContext> newDb = InMemory();
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
            s = Seed(seed, new(StepType.Agent, "S1"), new(StepType.Agent, "S2"));
        List<Guid> archiveSteps = await ArchiveAsync(newDb, s, deleteSource: false);
        await RequeueAsync(newDb, s.ExecutionId, resume);

        List<(string? Label, Guid StepId)> ran = [];
        await Service(new Scopes(newDb), null, null, null, new Executor(StepType.Agent, c =>
        {
            ran.Add((c.Step.Label, c.Step.Id));
            return Task.FromResult(Done(c, "{}"));
        })).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        WorkflowExecution run = await RunRow(newDb, s.ExecutionId);
        run.Status.Should().Be(ExecutionStatus.Passed, run.ErrorMessage);
        ran.Select(r => r.Label).Should().Equal(["S1", "S2"], "the run starts at step 1 on the source's steps, archive results unused");
        ran.Select(r => r.StepId).Should().NotIntersectWith(archiveSteps);
        ResumeCursor.IsEngineTakenSnapshot(run.Id, WorkflowEngineDbContextKind(newDb, run.SnapshotDefinitionId), OwnerOf(newDb, run.SnapshotDefinitionId))
            .Should().BeTrue("the run now has an engine-taken snapshot of its own");
    }

    [Fact]
    public async Task A_run_whose_archive_outlived_its_deleted_workflow_fails_instead_of_running_the_archive()
    {
        Func<WorkflowEngineDbContext> newDb = InMemory();
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
            s = Seed(seed, new(StepType.Agent, "S1"), new(StepType.Agent, "S2"));
        await ArchiveAsync(newDb, s, deleteSource: true);
        await RequeueAsync(newDb, s.ExecutionId, resume: false);

        List<string?> ran = [];
        await Service(new Scopes(newDb), null, null, null, new Executor(StepType.Agent, c =>
        {
            ran.Add(c.Step.Label);
            return Task.FromResult(Done(c, "{}"));
        })).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        WorkflowExecution run = await RunRow(newDb, s.ExecutionId);
        run.Status.Should().Be(ExecutionStatus.Failed);
        run.ErrorMessage.Should().Contain("archive");
        ran.Should().BeEmpty();
    }

    private static WorkflowDefinitionKind WorkflowEngineDbContextKind(Func<WorkflowEngineDbContext> newDb, Guid? definitionId)
    {
        using WorkflowEngineDbContext db = newDb();
        return db.WorkflowDefinitions.AsNoTracking().Single(d => d.Id == definitionId).Kind;
    }

    private static Guid? OwnerOf(Func<WorkflowEngineDbContext> newDb, Guid? definitionId)
    {
        using WorkflowEngineDbContext db = newDb();
        return db.WorkflowDefinitions.AsNoTracking().Where(d => d.Id == definitionId)
            .Select(d => EF.Property<Guid?>(d, WorkflowEngineDbContext.SnapshotOfExecutionIdColumn)).Single();
    }

    // ------------------------------------------------------------------ containers are reused only whole

    [Theory]
    [InlineData(StepType.ForEach, false)]
    [InlineData(StepType.ForEach, true)]
    [InlineData(StepType.Parallel, false)]
    [InlineData(StepType.Parallel, true)]
    public async Task A_container_is_reused_as_one_whole_row_and_a_partial_container_re_runs_whole(StepType container, bool failInside)
    {
        Func<WorkflowEngineDbContext> newDb = InMemory();
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
            s = Seed(seed, new(StepType.Agent, "Brief"), new(container, "Fan out"), new(StepType.Agent, "Assemble"));
        Scopes scopes = new(newDb);

        // A container runs every child inside ONE executor call and returns one aggregate: the loop writes one row.
        List<string> firstChildren = [];
        await RunExpectingFailure(Service(scopes, null, null, null,
            new Executor(StepType.Agent, c => c.Step.Label == "Assemble" && !failInside
                ? throw new InvalidOperationException("lost")
                : Task.FromResult(Done(c, "{}"))),
            new Executor(container, c =>
            {
                firstChildren.Add("child-0");
                if (failInside)
                    throw new InvalidOperationException("lost after child 0 of 3"); // a partial container
                firstChildren.Add("child-1");
                firstChildren.Add("child-2");
                return Task.FromResult(Done(c, "[\"a\",\"b\",\"c\"]"));
            })), s.ExecutionId);

        List<(string? Label, WorkflowStepResult Row)> history = await Rows(newDb, s.ExecutionId);
        history.Count(r => r.Label == "Fan out").Should().Be(1, "children are never rows of their own");

        await RequeueAsync(newDb, s.ExecutionId, resume: true);
        int containerCalls = 0;
        List<(string? Label, string Accumulated)> ran = [];
        await Service(scopes, null, null, null,
            new Executor(StepType.Agent, c =>
            {
                ran.Add((c.Step.Label, c.AccumulatedOutput));
                return Task.FromResult(Done(c, "{}"));
            }),
            new Executor(container, c =>
            {
                containerCalls++;
                return Task.FromResult(Done(c, "[\"a\",\"b\",\"c\"]"));
            })).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        (await RunRow(newDb, s.ExecutionId)).Status.Should().Be(ExecutionStatus.Passed);
        if (failInside)
        {
            containerCalls.Should().Be(1, "a partial container is never resumed mid-way: it runs again from its first child");
        }
        else
        {
            containerCalls.Should().Be(0, "a completed container is reused as its one whole row");
            ran.Should().Equal([("Assemble", "[\"a\",\"b\",\"c\"]")]);
        }
    }

    // ------------------------------------------------------------------ rooms are reused only as whole rows

    [Theory]
    [InlineData(StepType.EditRoom, false)]
    [InlineData(StepType.EditRoom, true)]
    [InlineData(StepType.GraphicsRoom, false)]
    [InlineData(StepType.GraphicsRoom, true)]
    [InlineData(StepType.ColorGradeRoom, false)]
    [InlineData(StepType.ColorGradeRoom, true)]
    [InlineData(StepType.DailiesRoom, false)]
    [InlineData(StepType.DailiesRoom, true)]
    public async Task A_completed_room_is_reused_as_one_whole_row_and_a_partial_room_re_runs_whole(StepType room, bool failMidRoom)
    {
        Func<WorkflowEngineDbContext> newDb = InMemory();
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
            s = Seed(seed, new(StepType.Agent, "Brief"), new(room, "Room"), new(StepType.Agent, "Assemble"));
        Scopes scopes = new(newDb);

        // A room runs every seat's turns inside ONE executor call and returns one aggregate: the loop writes one row.
        // Either the room itself dies after a few turns (partial) or the step after it does (the room completed).
        await RunExpectingFailure(Service(scopes, null, null, null,
            new Executor(StepType.Agent, c => c.Step.Label == "Assemble" && !failMidRoom
                ? throw new InvalidOperationException("lost after the room")
                : Task.FromResult(Done(c, "{}"))),
            new Executor(room, c => failMidRoom
                ? throw new InvalidOperationException("lost after turn 2 of 6")
                : Task.FromResult(Done(c, "{\"timeline\":\"final cut\"}")))), s.ExecutionId);

        List<(string? Label, WorkflowStepResult Row)> history = await Rows(newDb, s.ExecutionId);
        history.Count(r => r.Label == "Room" && r.Row.Status == StepStatus.Completed).Should().Be(failMidRoom ? 0 : 1);
        history.Count(r => r.Label == "Room").Should().Be(1, "turns are never rows of their own");

        await RequeueAsync(newDb, s.ExecutionId, resume: true);
        int roomCalls = 0;
        List<(string? Label, string Accumulated)> ran = [];
        await Service(scopes, null, null, null,
            new Executor(StepType.Agent, c =>
            {
                ran.Add((c.Step.Label, c.AccumulatedOutput));
                return Task.FromResult(Done(c, "{}"));
            }),
            new Executor(room, c =>
            {
                roomCalls++;
                return Task.FromResult(Done(c, "{\"timeline\":\"final cut\"}"));
            })).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        (await RunRow(newDb, s.ExecutionId)).Status.Should().Be(ExecutionStatus.Passed);
        if (failMidRoom)
        {
            roomCalls.Should().Be(1, "a partial room is never resumed mid-way: it runs again from its first turn");
            ran.Select(r => r.Label).Should().Equal(["Assemble"]);
        }
        else
        {
            roomCalls.Should().Be(0, "a completed room is reused as its one whole row");
            ran.Should().Equal([("Assemble", "{\"timeline\":\"final cut\"}")], "the reused room output feeds the next step");
        }
    }

    // ------------------------------------------------------------------ reuse never touches the step cache

    [Fact]
    public async Task A_reused_result_is_neither_looked_up_in_nor_written_to_the_step_cache()
    {
        Func<WorkflowEngineDbContext> newDb = InMemory();
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
        {
            s = Seed(seed,
                new(StepType.Agent, "Cached", st => st.CacheMode = StepCacheMode.Always),
                new(StepType.Agent, "Tail", st => st.CacheMode = StepCacheMode.Never));
        }
        RecordingCache cache = new();
        Scopes scopes = new(newDb, cache: cache);

        await RunExpectingFailure(Service(scopes, null, null, null, cached: true, new Executor(StepType.Agent, c =>
            c.Step.Label == "Tail" ? throw new InvalidOperationException("lost") : Task.FromResult(Done(c, "{\"cached\":1}")))), s.ExecutionId);
        cache.Stored.Should().Equal(["{\"cached\":1}"], "the live dispatch stores its result as today");
        int lookupsBefore = cache.Lookups.Count;

        await RequeueAsync(newDb, s.ExecutionId, resume: true);
        List<string?> ran = [];
        await Service(scopes, null, null, null, cached: true, new Executor(StepType.Agent, c =>
        {
            ran.Add(c.Step.Label);
            return Task.FromResult(Done(c, "{}"));
        })).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        (await RunRow(newDb, s.ExecutionId)).Status.Should().Be(ExecutionStatus.Passed);
        ran.Should().Equal(["Tail"]);
        cache.Stored.Should().HaveCount(1, "the reused step writes nothing to the cache");
        cache.Lookups.Count.Should().Be(lookupsBefore, "nor is the cache consulted for it (Tail is never cacheable)");
    }

    // ------------------------------------------------------------------ rows left Running are not reusable

    [Fact]
    public async Task A_row_a_lost_engine_left_Running_is_a_miss_even_with_an_output()
    {
        Func<WorkflowEngineDbContext> newDb = InMemory();
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
            s = Seed(seed, new(StepType.Agent, "S1"), new(StepType.Agent, "S2"));
        Scopes scopes = new(newDb);

        await RunExpectingFailure(Service(scopes, null, null, null, new Executor(StepType.Agent, c =>
            c.Step.StepOrder == 2 ? throw new InvalidOperationException("lost") : Task.FromResult(Done(c, "{}")))), s.ExecutionId);
        using (WorkflowEngineDbContext db = newDb())
        {
            WorkflowStepResult first = await db.WorkflowStepResults.Include(r => r.WorkflowStep)
                .SingleAsync(r => r.WorkflowExecutionId == s.ExecutionId && r.WorkflowStep.StepOrder == 1);
            first.Status = StepStatus.Running;
            await db.SaveChangesAsync();
        }

        await RequeueAsync(newDb, s.ExecutionId, resume: true);
        List<string?> ran = [];
        await Service(scopes, null, null, null, new Executor(StepType.Agent, c =>
        {
            ran.Add(c.Step.Label);
            return Task.FromResult(Done(c, "{}"));
        })).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        ran.Should().Equal(["S1", "S2"]);
    }
}
