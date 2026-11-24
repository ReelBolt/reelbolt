using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Services.Messaging;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// WP D12: a result produced by the ORGANIZATION'S OWN runner is tagged and never enters the
/// step-result cache, while a CloudPool or Local result keeps caching exactly as before.
///
/// <para>
/// Both halves matter. The exclusion is what makes the tag meaningful — a cached OrgRunner result
/// would replay one customer's machine-produced bytes into a later run that never verified them —
/// and its NARROWNESS is what keeps the deliberate <c>VideoGenerate</c>/<c>VideoCompile</c>
/// cacheability (a repeat run must not pay twice) intact for our own workers.
/// </para>
/// </summary>
public class StepCacheRunnerExclusionTests
{
    private static readonly Guid RunnerId = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");
    private static readonly Guid RunnerJobId = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444");

    private sealed class NoOpEventPublisher : IWorkflowEventPublisher
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

    /// <summary>An executor that reports where ""it"" ran, the way a runner-aware executor will.</summary>
    private sealed class ProvenanceReportingExecutor(ComputeTargetKind target) : IStepExecutor
    {
        public StepType StepType => StepType.Extract;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context) =>
            Task.FromResult(new StepExecutionResult
            {
                Output = "{\"ok\":true}",
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                Status = StepStatus.Completed,
                ComputeTarget = target,
                RunnerId = target == ComputeTargetKind.OrgRunner || target == ComputeTargetKind.CloudPool ? RunnerId : null,
                RunnerJobId = target == ComputeTargetKind.OrgRunner || target == ComputeTargetKind.CloudPool ? RunnerJobId : null
            });
    }

    private sealed class RecordingCache : IStepResultCache
    {
        public int Stores { get; private set; }
        public int Lookups { get; private set; }
        public StepExecutionResult? LastStored { get; private set; }

        public Task<StepCacheLookupResult> TryGetAsync(StepCacheKeyInputs inputs, CancellationToken ct)
        {
            Lookups++;
            return Task.FromResult(StepCacheLookupResult.Miss);
        }

        public Task StoreAsync(StepCacheKeyInputs inputs, StepExecutionResult result, CancellationToken ct)
        {
            Stores++;
            LastStored = result;
            return Task.CompletedTask;
        }
    }

    private sealed class StubFingerprintProvider : IProjectFileFingerprintProvider
    {
        public Task<string> GetFingerprintAsync(Guid projectId, CancellationToken ct) => Task.FromResult(new string('a', 64));
    }

    private sealed class StubKeyBuilder : IStepCacheKeyBuilder
    {
        public string Build(StepCacheKeyInputs inputs) => new('b', 64);
    }

    private sealed class ScopeFactory(WorkflowEngineDbContext db, IStepResultCache? cache) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(db, cache);

        private sealed class Scope(WorkflowEngineDbContext db, IStepResultCache? cache) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new Provider(db, cache);
            public void Dispose() { }
        }

        private sealed class Provider(WorkflowEngineDbContext db, IStepResultCache? cache) : IServiceProvider
        {
            public object? GetService(Type serviceType)
                => serviceType == typeof(WorkflowEngineDbContext) ? db
                : serviceType == typeof(IStepResultCache) ? cache
                : serviceType == typeof(IProjectFileFingerprintProvider) ? new StubFingerprintProvider()
                : null;
        }
    }


    private static async Task<(WorkflowStepResult Row, RecordingCache Cache)> RunAsync(ComputeTargetKind target)
    {
        DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new WorkflowEngineDbContext(options);

        Guid projectId = Guid.NewGuid();
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "A", AgentType = AgentType.Custom, SystemPrompt = "" };
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.Extract,
            AgentDefinitionId = agent.Id,
            AgentDefinition = agent
        };
        WorkflowDefinition definition = new() { Id = Guid.NewGuid(), Name = "t", ProjectId = projectId, Steps = [step] };
        WorkflowExecution execution = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = definition.Id,
            ProjectId = projectId,
            Status = ExecutionStatus.Queued,
            WorkflowDefinition = definition
        };
        db.AgentDefinitions.Add(agent);
        db.WorkflowDefinitions.Add(definition);
        db.WorkflowExecutions.Add(execution);
        await db.SaveChangesAsync();

        var cache = new RecordingCache();
        var service = new WorkflowExecutorService(
            scopeFactory: new ScopeFactory(db, cache),
            eventPublisher: new NoOpEventPublisher(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: [new ProvenanceReportingExecutor(target)],
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry(),
            cachePolicy: new StepCachePolicy(),
            cacheKeyBuilder: new StubKeyBuilder());

        await service.ExecuteAsync(execution.Id, "corr", CancellationToken.None);

        WorkflowStepResult row = await db.WorkflowStepResults.AsNoTracking().SingleAsync(r => r.WorkflowExecutionId == execution.Id);
        return (row, cache);
    }

    [Fact]
    public async Task A_runner_result_is_tagged_and_never_reaches_the_step_cache()
    {
        (WorkflowStepResult row, RecordingCache cache) = await RunAsync(ComputeTargetKind.OrgRunner);

        cache.Stores.Should().Be(0, "an OrgRunner result must never be written to the step-result cache");
        cache.LastStored.Should().BeNull();

        row.Status.Should().Be(StepStatus.Completed);
        row.FromRunner.Should().BeTrue();
        row.ComputeTarget.Should().Be(ComputeTargetKind.OrgRunner);
        row.RunnerId.Should().Be(RunnerId);
        row.RunnerJobId.Should().Be(RunnerJobId);
    }

    [Fact]
    public async Task A_cloud_pool_result_is_verified_but_still_cached()
    {
        // Precise, not blanket: CloudPool is OUR hardware. Excluding it would silently delete the
        // "an identical re-run does not pay twice" property VideoGenerate and VideoCompile are
        // deliberately cacheable for.
        (WorkflowStepResult row, RecordingCache cache) = await RunAsync(ComputeTargetKind.CloudPool);

        cache.Stores.Should().Be(1);
        cache.LastStored!.FromRunner.Should().BeFalse();
        cache.LastStored.ComputeTarget.Should().Be(ComputeTargetKind.CloudPool);

        row.FromRunner.Should().BeFalse();
        row.ComputeTarget.Should().Be(ComputeTargetKind.CloudPool);
        row.RunnerId.Should().Be(RunnerId);
        row.RunnerJobId.Should().Be(RunnerJobId);
    }

    [Fact]
    public async Task A_local_result_is_unchanged()
    {
        (WorkflowStepResult row, RecordingCache cache) = await RunAsync(ComputeTargetKind.Local);

        cache.Stores.Should().Be(1);
        row.FromRunner.Should().BeFalse();
        row.ComputeTarget.Should().Be(ComputeTargetKind.Local);
        row.RunnerId.Should().BeNull();
        row.RunnerJobId.Should().BeNull();
    }

    [Fact]
    public void FromRunner_is_derived_from_the_compute_target_and_cannot_drift_from_it()
    {
        // The tag is not an independent flag: D12 derives it from the target (plan step 5), so a
        // result can never claim a provenance its target contradicts.
        foreach (ComputeTargetKind target in Enum.GetValues<ComputeTargetKind>())
        {
            new StepExecutionResult { Output = "{}", NextStepIndex = 0, ComputeTarget = target }
                .FromRunner.Should().Be(target == ComputeTargetKind.OrgRunner);
        }

        // ...and a result that never set one is Local, which is what every executor predating the
        // compute fabric produces.
        new StepExecutionResult { Output = "{}", NextStepIndex = 0 }.ComputeTarget.Should().Be(ComputeTargetKind.Local);
    }

    [Fact]
    public void The_policy_admits_everything_except_a_runner_result()
    {
        IStepCachePolicy policy = new StepCachePolicy();

        policy.IsAdmissible(new StepExecutionResult { Output = "{}", NextStepIndex = 0 }).Should().BeTrue();
        policy.IsAdmissible(new StepExecutionResult { Output = "{}", NextStepIndex = 0, ComputeTarget = ComputeTargetKind.CloudPool }).Should().BeTrue();
        policy.IsAdmissible(new StepExecutionResult { Output = "{}", NextStepIndex = 0, ComputeTarget = ComputeTargetKind.OrgRunner }).Should().BeFalse();
    }

    [Fact]
    public async Task The_cache_itself_refuses_an_inadmissible_result_even_when_a_caller_skips_the_policy()
    {
        // The executor checks admissibility before calling in; this is the same rule at the cache
        // boundary, so a future caller that forgets cannot store one.
        DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new WorkflowEngineDbContext(options);

        var cache = new StepResultCache(
            db,
            new StubKeyBuilder(),
            new StepCachePolicy(),
            Options.Create(new StepCacheOptions { Enabled = true, TtlHours = 24 }),
            NullLogger<StepResultCache>.Instance);

        StepCacheKeyInputs inputs = new()
        {
            ProjectId = Guid.NewGuid(),
            StepType = StepType.VideoCompile,
            AgentType = AgentType.Custom,
            AgentDefinitionId = Guid.NewGuid(),
            AgentInputContextMode = AgentInputContextMode.FullWorkflow,
            MaxIterations = 1,
            ProjectFileFingerprint = new string('a', 64)
        };

        await cache.StoreAsync(inputs, new StepExecutionResult
        {
            Output = "{\"ok\":true}",
            NextStepIndex = 1,
            Status = StepStatus.Completed,
            ComputeTarget = ComputeTargetKind.OrgRunner
        }, CancellationToken.None);

        (await db.WorkflowStepCacheEntries.CountAsync()).Should().Be(0);

        // The same result from our own pool worker IS stored — the refusal is about the runner, not
        // about the shape of the row.
        await cache.StoreAsync(inputs, new StepExecutionResult
        {
            Output = "{\"ok\":true}",
            NextStepIndex = 1,
            Status = StepStatus.Completed,
            ComputeTarget = ComputeTargetKind.CloudPool
        }, CancellationToken.None);

        (await db.WorkflowStepCacheEntries.CountAsync()).Should().Be(1);
    }

    [Fact]
    public void D12_does_not_bump_the_step_cache_schema_version()
    {
        // R10: A10 took the schema to v4 for the resolved-provider fingerprint and D12 changes
        // ADMISSIBILITY only, so the two land on ONE version, and P2-18 took it to v6 for the
        // design-system component. Bumping it here would orphan every existing cache row for a change
        // that alters no key component at all.
        // (v5 is the composition-replay upstream digest, v7 the resolved StepResult sources — key
        // components of their own; v8 is the prompted-region-effects VideoSegmentation line joining the
        // resolved-provider fingerprint, which changes the VALUE of an already-hashed field; v9 is the
        // T-024 remediation — the mask content key is segmented by ObjectMatchScope, so a VideoAnalyze
        // row's meaning moved without any field in THIS record changing; v10 is the AudioIsolation line joining
        // the resolved-provider fingerprint, v11 the VideoUpscale and FrameInterpolation lines and v12 the VideoOutpaint, v13 the LipSync
        // line, each of which changes every step's key.)
        StepCacheKeyInputs.SchemaVersion.Should().Be("v13");
    }
}
