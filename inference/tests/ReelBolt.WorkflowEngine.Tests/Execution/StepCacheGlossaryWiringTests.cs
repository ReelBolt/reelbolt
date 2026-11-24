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
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Voiceover;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// How the run service builds the step-cache key for a Voiceover step with a project glossary: the LOOKUP uses the
/// glossary as it is before the step runs, the STORE uses the digest of the entries the step reports it used, so a
/// glossary edited while the step ran cannot attach this run's audio to the new glossary's key.
/// </summary>
public class StepCacheGlossaryWiringTests
{
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

    private sealed class ReportingVoiceoverExecutor(string output) : IStepExecutor
    {
        public StepType StepType => StepType.Voiceover;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context) =>
            Task.FromResult(new StepExecutionResult
            {
                Output = output,
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                Status = StepStatus.Completed
            });
    }

    private sealed class RecordingCache : IStepResultCache
    {
        public List<StepCacheKeyInputs> Lookups { get; } = [];
        public List<StepCacheKeyInputs> Stores { get; } = [];

        public Task<StepCacheLookupResult> TryGetAsync(StepCacheKeyInputs inputs, CancellationToken ct)
        {
            Lookups.Add(inputs);
            return Task.FromResult(StepCacheLookupResult.Miss);
        }

        public Task StoreAsync(StepCacheKeyInputs inputs, StepExecutionResult result, CancellationToken ct)
        {
            Stores.Add(inputs);
            return Task.CompletedTask;
        }
    }

    /// <summary>The glossary changes between reads: the first read is the lookup, the next ones are "after the edit".</summary>
    private sealed class ChangingGlossary(IReadOnlyList<GlossaryEntry> first, IReadOnlyList<GlossaryEntry> later) : IProjectPronunciationResolver
    {
        public int Reads { get; private set; }

        public Task<IReadOnlyList<GlossaryEntry>> GetEntriesAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult(Reads++ == 0 ? first : later);
    }

    private sealed class StubFingerprintProvider : IProjectFileFingerprintProvider
    {
        public Task<string> GetFingerprintAsync(Guid projectId, CancellationToken ct) => Task.FromResult(new string('a', 64));
    }

    private sealed class StubKeyBuilder : IStepCacheKeyBuilder
    {
        public string Build(StepCacheKeyInputs inputs) => new('b', 64);
    }

    private sealed class ScopeFactory(WorkflowEngineDbContext db, IStepResultCache cache, IProjectPronunciationResolver resolver) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(new Provider(db, cache, resolver));

        private sealed class Scope(IServiceProvider provider) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = provider;
            public void Dispose() { }
        }

        private sealed class Provider(WorkflowEngineDbContext db, IStepResultCache cache, IProjectPronunciationResolver resolver) : IServiceProvider
        {
            public object? GetService(Type serviceType)
                => serviceType == typeof(WorkflowEngineDbContext) ? db
                : serviceType == typeof(IStepResultCache) ? cache
                : serviceType == typeof(IProjectFileFingerprintProvider) ? new StubFingerprintProvider()
                : serviceType == typeof(IProjectPronunciationResolver) ? resolver
                : null;
        }
    }

    private static async Task<RecordingCache> RunAsync(string output, IProjectPronunciationResolver resolver)
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
            StepType = StepType.Voiceover,
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
            scopeFactory: new ScopeFactory(db, cache, resolver),
            eventPublisher: new NoOpEventPublisher(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: [new ReportingVoiceoverExecutor(output)],
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry(),
            cachePolicy: new StepCachePolicy(),
            cacheKeyBuilder: new StubKeyBuilder());

        await service.ExecuteAsync(execution.Id, "corr", CancellationToken.None);
        return cache;
    }

    private static string Output(string? digest) =>
        "{\"meta\":{\"pronunciation\":{\"glossaryCount\":1,\"glossaryDigest\":" + (digest is null ? "null" : "\"" + digest + "\"") + "}}}";

    [Fact]
    public async Task The_lookup_uses_the_glossary_before_the_step_and_the_store_the_digest_the_step_used()
    {
        GlossaryEntry[] before = [new("Novara", "Novàra", false)];
        GlossaryEntry[] editedMidRun = [new("Novara", "Nòvara", false)];
        var resolver = new ChangingGlossary(before, editedMidRun);
        string used = PronunciationRespeller.Digest(before)!;

        RecordingCache cache = await RunAsync(Output(used), resolver);

        cache.Lookups.Should().ContainSingle().Which.ProjectPronunciation.Should().Be(PronunciationRespeller.Digest(before));
        cache.Stores.Should().ContainSingle().Which.ProjectPronunciation.Should().Be(used)
            .And.NotBe(PronunciationRespeller.Digest(editedMidRun), "the glossary edited mid-run must not name this run's audio");
        resolver.Reads.Should().Be(1, "the store never re-reads the glossary");
    }

    [Fact]
    public async Task A_step_that_used_no_glossary_stores_under_no_glossary_even_if_one_appeared_meanwhile()
    {
        var resolver = new ChangingGlossary([], [new("Novara", "Novàra", false)]);

        RecordingCache cache = await RunAsync(Output(null), resolver);

        cache.Lookups.Should().ContainSingle().Which.ProjectPronunciation.Should().BeNull();
        cache.Stores.Should().ContainSingle().Which.ProjectPronunciation.Should().BeNull();
    }

    [Theory]
    [InlineData(StepType.Voiceover, "not json", null)]
    [InlineData(StepType.Voiceover, "{}", null)]
    [InlineData(StepType.Voiceover, "{\"meta\":{\"pronunciation\":{\"glossaryDigest\":5}}}", null)]
    [InlineData(StepType.Voiceover, "{\"meta\":{\"pronunciation\":{\"glossaryDigest\":\"abc\"}}}", "abc")]
    [InlineData(StepType.Extract, "{\"meta\":{\"pronunciation\":{\"glossaryDigest\":\"abc\"}}}", null)]
    public void The_used_digest_is_read_only_from_a_voiceover_result(StepType type, string output, string? expected)
    {
        var step = new WorkflowStep { StepType = type };
        WorkflowExecutorService.GlossaryDigestUsedBy(step, new StepExecutionResult { Output = output, NextStepIndex = 1 })
            .Should().Be(expected);
    }
}
