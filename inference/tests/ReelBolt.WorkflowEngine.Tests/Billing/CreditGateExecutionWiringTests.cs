using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Messaging;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// WP B6b: the credit gate wired into the REAL <see cref="WorkflowExecutorService.ExecuteAsync"/> —
/// a step reserves before it runs, settles after it succeeds, and releases by failure class when it
/// fails. The gate itself is the real B6a <see cref="CreditGate"/> over a real (Sqlite) credit
/// ledger, so these tests assert the money, not the calls: what the org was actually charged and
/// left holding.
///
/// <para>
/// The engine's own tables live in an in-memory context while the ledger lives in Sqlite on a
/// separate connection: the gate is designed around a short-lived context of its own
/// (IDbContextFactory), so the two never interact.
/// </para>
/// </summary>
public sealed class CreditGateExecutionWiringTests : IDisposable
{
    private const long StepReserveMillis = 20_000;

    private readonly SqliteConnection _ledgerConnection = new("Data Source=:memory:;Foreign Keys=False");
    private readonly LedgerFactory _ledgerFactory;
    private readonly Guid _org = Guid.NewGuid();

    public CreditGateExecutionWiringTests()
    {
        _ledgerConnection.Open();
        DbContextOptions<WorkflowEngineDbContext> options =
            new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseSqlite(_ledgerConnection).Options;
        // The engine maps the credit tables ExcludeFromMigrations, so the schema comes from the owner.
        using (InferenceApiDbContext owner = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_ledgerConnection).Options))
        {
            owner.Database.EnsureCreated();
        }

        // S-3: the run-level release reads workflow_step_results next to the ledger, as on Postgres.
        UnsettledHoldTestKit.AddEngineTables(_ledgerConnection);
        _ledgerFactory = new LedgerFactory(options);
    }

    public void Dispose() => _ledgerConnection.Dispose();

    private sealed class LedgerFactory(DbContextOptions<WorkflowEngineDbContext> options) : IDbContextFactory<WorkflowEngineDbContext>
    {
        public WorkflowEngineDbContext CreateDbContext() => new(options);
    }

    // -----------------------------------------------------------------
    // The gate under test, and the ledger it writes to.
    // -----------------------------------------------------------------

    private CreditGate Gate(bool enabled = true)
        => new(_ledgerFactory, Options.Create(new CreditGateOptions { Enabled = enabled }), NullLogger<CreditGate>.Instance);

    private async Task GrantAsync(long millis)
    {
        await using WorkflowEngineDbContext db = _ledgerFactory.CreateDbContext();
        (await new CreditLedger(db).GrantAsync(_org, CreditGrantSource.Plan, millis, null, Guid.NewGuid().ToString("N")))
            .Ok.Should().BeTrue();
    }

    private async Task<CreditLedgerResult> BalanceAsync()
    {
        await using WorkflowEngineDbContext db = _ledgerFactory.CreateDbContext();
        return await new CreditLedger(db).GetBalanceAsync(_org);
    }

    private async Task<int> LedgerEntryCountAsync()
    {
        await using WorkflowEngineDbContext db = _ledgerFactory.CreateDbContext();
        return await db.CreditLedgerEntries.CountAsync();
    }

    private async Task<IReadOnlyList<CreditLedgerEntry>> EntriesAsync()
    {
        await using WorkflowEngineDbContext db = _ledgerFactory.CreateDbContext();
        return await db.CreditLedgerEntries.AsNoTracking().ToListAsync();
    }

    // -----------------------------------------------------------------
    // Harness: the real ExecuteAsync, a scripted step executor, in-memory engine tables.
    // -----------------------------------------------------------------

    private sealed record Run(
        WorkflowExecution Execution,
        WorkflowStepResult? StepResult,
        Exception? Thrown,
        IReadOnlyList<CreditLedgerEntry> Ledger,
        CreditLedgerResult Balance);

    private sealed class ScriptedExecutor(StepType stepType, Func<StepExecutionContext, int, Task<StepExecutionResult>> body) : IStepExecutor
    {
        private int _calls;

        public StepType StepType { get; } = stepType;
        public int Calls => _calls;
        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
            => body(context, Interlocked.Increment(ref _calls));
    }

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

    private sealed class StubFingerprintProvider : IProjectFileFingerprintProvider
    {
        public Task<string> GetFingerprintAsync(Guid projectId, CancellationToken ct) => Task.FromResult(new string('a', 64));
    }

    private sealed class StubKeyBuilder : IStepCacheKeyBuilder
    {
        public string Build(StepCacheKeyInputs inputs) => new('b', 64);
    }

    private sealed class StubCache(WorkflowStepCacheEntry? entry) : IStepResultCache
    {
        public int Lookups { get; private set; }

        public Task<StepCacheLookupResult> TryGetAsync(StepCacheKeyInputs inputs, CancellationToken ct)
        {
            Lookups++;
            return Task.FromResult(entry is null ? StepCacheLookupResult.Miss : new StepCacheLookupResult(true, entry));
        }

        public Task StoreAsync(StepCacheKeyInputs inputs, StepExecutionResult result, CancellationToken ct) => Task.CompletedTask;
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

    private static StepExecutionResult Completed(StepExecutionContext context, string output = "{\"ok\":true}") => new()
    {
        Output = output,
        NextStepIndex = context.CurrentStepIndex + 1,
        NewIterationCount = context.IterationCount,
        Status = StepStatus.Completed
    };

    private static StepExecutionResult Failed(StepExecutionContext context, string code, string message = "boom") => new()
    {
        Output = $"{{\"status\":\"failed\",\"error\":{{\"code\":\"{code}\",\"message\":\"{message}\"}}}}",
        NextStepIndex = context.CurrentStepIndex + 1,
        NewIterationCount = context.IterationCount,
        Status = StepStatus.Failed,
        ErrorDetails = message
    };

    /// <summary>The same, for an executor that reports its own failure envelope verbatim (VideoGenerate's <c>meta.failed</c>).</summary>
    private static StepExecutionResult FailedWithOutput(StepExecutionContext context, string output) => new()
    {
        Output = output,
        NextStepIndex = context.CurrentStepIndex + 1,
        NewIterationCount = context.IterationCount,
        Status = StepStatus.Failed,
        ErrorDetails = output
    };

    /// <summary>Records <paramref name="creditsMillis"/> into the AMBIENT usage scope, exactly as a real model call does.</summary>
    private static async Task RecordUsageAsync(long creditsMillis)
    {
        var recorder = new UsageRecorder(
            new FixedPriceService(creditsMillis), new NullUsageSink(), NullLogger<UsageRecorder>.Instance);
        await recorder.RecordAsync(new UsageRecordRequest(
            UsageKind.LlmOutputTokens, 1_000m, null, InferenceProviderKind.OpenAICompatible, "test-model", UsageBillingMode.Platform));
    }

    private sealed class FixedPriceService(long creditsMillis) : IModelPriceService
    {
        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default)
            => ValueTask.FromResult(new PriceQuote(creditsMillis, 0.001m, Guid.NewGuid()));

        public ValueTask<PriceQuote> PriceQuotedUsdAsync(decimal costUsd, UsageKind unit, UsageBillingMode billingMode,
            DateTime? at = null, CancellationToken ct = default)
            => ValueTask.FromResult(new PriceQuote(creditsMillis, costUsd, null));

        public void Invalidate() { }
    }

    private sealed class NullUsageSink : IUsageEventSink
    {
        public Task WriteAsync(IReadOnlyList<UsageEvent> events, CancellationToken ct) => Task.CompletedTask;
    }

    private async Task<Run> RunAsync(
        IStepExecutor executor,
        StepType stepType,
        ICreditGate? gate,
        StepCreditOptions? creditOptions = null,
        IStepResultCache? cache = null,
        WorkflowHardeningOptions? hardening = null,
        ICreditHoldEstimator? holdEstimator = null,
        string? videoGenerateConfigJson = null,
        string? audioGenerateConfigJson = null)
    {
        DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new WorkflowEngineDbContext(options);

        Guid projectId = Guid.NewGuid();
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "A", AgentType = AgentType.VideoStoryEditor, SystemPrompt = "" };
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = stepType,
            AgentDefinitionId = agent.Id,
            AgentDefinition = agent,
            VideoGenerateConfigJson = videoGenerateConfigJson,
            AudioGenerateConfigJson = audioGenerateConfigJson
        };
        WorkflowDefinition definition = new() { Id = Guid.NewGuid(), Name = "t", ProjectId = projectId, Steps = [step] };
        WorkflowExecution execution = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = definition.Id,
            ProjectId = projectId,
            OrganizationId = _org,
            Status = ExecutionStatus.Queued,
            WorkflowDefinition = definition
        };
        db.AgentDefinitions.Add(agent);
        db.WorkflowDefinitions.Add(definition);
        db.WorkflowExecutions.Add(execution);
        await db.SaveChangesAsync();

        var service = new WorkflowExecutorService(
            scopeFactory: new ScopeFactory(db, cache),
            eventPublisher: new NoOpEventPublisher(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: [executor],
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(hardening ?? new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry(),
            cachePolicy: cache is null ? null : new StepCachePolicy(),
            cacheKeyBuilder: cache is null ? null : new StubKeyBuilder(),
            creditGate: gate,
            creditOptions: Options.Create(creditOptions ?? new StepCreditOptions { StepReserveMillisOverride = StepReserveMillis }),
            creditHoldEstimator: holdEstimator);

        Exception? thrown = null;
        try
        {
            await service.ExecuteAsync(execution.Id, "corr", CancellationToken.None);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        WorkflowStepResult? stepResult = await db.WorkflowStepResults
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.WorkflowExecutionId == execution.Id);

        return new Run(execution, stepResult, thrown, await EntriesAsync(), await BalanceAsync());
    }

    // -----------------------------------------------------------------
    // Reserve → settle
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_completed_step_settles_the_metered_actual_and_releases_the_rest_of_the_hold()
    {
        await GrantAsync(100_000);
        var executor = new ScriptedExecutor(StepType.Agent, async (context, _) =>
        {
            await RecordUsageAsync(4_500);
            return Completed(context);
        });

        Run run = await RunAsync(executor, StepType.Agent, Gate());

        run.Thrown.Should().BeNull();
        run.Execution.Status.Should().Be(ExecutionStatus.Passed);
        run.StepResult!.Status.Should().Be(StepStatus.Completed);
        run.StepResult.FailureClass.Should().Be(FailureClass.None);
        run.StepResult.CreditsChargedMillis.Should().Be(4_500);

        // Charged the metered actual, not the reservation; the rest of the hold is gone.
        run.Balance.BalanceMillis.Should().Be(95_500);
        run.Balance.ReservedMillis.Should().Be(0);

        // One Reserve of the configured estimate, one Settle of the actual — nothing else.
        run.Ledger.Select(e => e.Kind).Should().BeEquivalentTo(
            [CreditLedgerEntryKind.PlanRefill, CreditLedgerEntryKind.Reserve, CreditLedgerEntryKind.Settle]);
        run.Ledger.Single(e => e.Kind == CreditLedgerEntryKind.Reserve).ReservedDeltaMillis.Should().Be(StepReserveMillis);
        run.Ledger.Single(e => e.Kind == CreditLedgerEntryKind.Settle).BalanceDeltaMillis.Should().Be(-4_500);
    }

    [Fact]
    public async Task A_step_with_no_metered_usage_is_held_and_then_released_in_full()
    {
        await GrantAsync(100_000);
        var executor = new ScriptedExecutor(StepType.Agent, (context, _) => Task.FromResult(Completed(context)));

        Run run = await RunAsync(executor, StepType.Agent, Gate());

        run.Thrown.Should().BeNull();
        run.StepResult!.CreditsChargedMillis.Should().Be(0);
        run.Balance.BalanceMillis.Should().Be(100_000);
        run.Balance.ReservedMillis.Should().Be(0);
    }

    // -----------------------------------------------------------------
    // Failure classes
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_platform_caused_failure_refunds_the_step_entirely()
    {
        await GrantAsync(100_000);
        // ENCODE_FAILED: first-party ffmpeg on a validated cut list — our fault (B1).
        var executor = new ScriptedExecutor(StepType.VideoCompile, async (context, _) =>
        {
            await RecordUsageAsync(6_000);
            return Failed(context, "ENCODE_FAILED");
        });

        Run run = await RunAsync(executor, StepType.VideoCompile, Gate());

        run.Thrown.Should().NotBeNull();
        run.Execution.Status.Should().Be(ExecutionStatus.Failed);
        run.StepResult!.Status.Should().Be(StepStatus.Failed);
        run.StepResult.FailureClass.Should().Be(FailureClass.PlatformInternal);
        run.StepResult.FailureCode.Should().Be("ENCODE_FAILED");
        run.StepResult.CreditsChargedMillis.Should().Be(0);

        run.Balance.BalanceMillis.Should().Be(100_000, "a platform-caused failure is refunded in full");
        run.Balance.ReservedMillis.Should().Be(0, "the hold is closed either way");
        run.Ledger.Select(e => e.Kind).Should().BeEquivalentTo(
            [CreditLedgerEntryKind.PlanRefill, CreditLedgerEntryKind.Reserve, CreditLedgerEntryKind.Release]);
        run.Ledger.Single(e => e.Kind == CreditLedgerEntryKind.Release).Note.Should().Be(nameof(FailureClass.PlatformInternal));
    }

    [Fact]
    public async Task A_user_caused_failure_charges_what_the_step_actually_consumed()
    {
        await GrantAsync(100_000);
        // CONFIG_INVALID: the workflow definition is the user's own data (B1).
        var executor = new ScriptedExecutor(StepType.VideoCompile, async (context, _) =>
        {
            await RecordUsageAsync(3_000);
            return Failed(context, "CONFIG_INVALID");
        });

        Run run = await RunAsync(executor, StepType.VideoCompile, Gate());

        run.Thrown.Should().NotBeNull();
        run.StepResult!.FailureClass.Should().Be(FailureClass.UserInput);
        run.StepResult.FailureCode.Should().Be("CONFIG_INVALID");
        run.StepResult.CreditsChargedMillis.Should().Be(3_000);

        run.Balance.BalanceMillis.Should().Be(97_000, "a user-caused failure keeps what it consumed");
        run.Balance.ReservedMillis.Should().Be(0);
        run.Ledger.Select(e => e.Kind).Should().BeEquivalentTo(
            [CreditLedgerEntryKind.PlanRefill, CreditLedgerEntryKind.Reserve, CreditLedgerEntryKind.Settle]);
        run.Ledger.Single(e => e.Kind == CreditLedgerEntryKind.Settle).Note.Should().Be(nameof(FailureClass.UserInput));
    }

    [Fact]
    public async Task An_unclassified_failure_refunds_by_default()
    {
        await GrantAsync(100_000);
        var executor = new ScriptedExecutor(StepType.VideoCompile, async (context, _) =>
        {
            await RecordUsageAsync(2_000);
            throw new InvalidOperationException("something we have never seen before");
        });

        Run run = await RunAsync(executor, StepType.VideoCompile, Gate());

        run.StepResult!.FailureClass.Should().Be(FailureClass.PlatformInternal);
        run.StepResult.CreditsChargedMillis.Should().Be(0);
        run.Balance.BalanceMillis.Should().Be(100_000);
    }

    [Fact]
    public async Task A_step_that_fails_on_an_unpriced_model_persists_MODEL_UNPRICED_and_is_refunded()
    {
        await GrantAsync(100_000);
        // The resolver refuses an unpriced Platform model (ModelUnpricedException): our catalog gap, never the user's.
        var executor = new ScriptedExecutor(StepType.VideoCompile, async (context, _) =>
        {
            await RecordUsageAsync(2_000);
            throw new ModelUnpricedException(InferenceProviderCapability.Chat, "gpt-unpriced");
        });

        Run run = await RunAsync(executor, StepType.VideoCompile, Gate());

        run.StepResult!.Status.Should().Be(StepStatus.Failed);
        run.StepResult.FailureCode.Should().Be(ModelUnpricedException.FailureCode);
        run.StepResult.FailureClass.Should().Be(FailureClass.PlatformInternal);
        run.StepResult.CreditsChargedMillis.Should().Be(0);
        run.Balance.BalanceMillis.Should().Be(100_000, "a step refused for an unpriced model is refunded in full");
        run.Balance.ReservedMillis.Should().Be(0);
    }

    // -----------------------------------------------------------------
    // Cache hits: no reservation, no charge
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_cache_hit_reserves_nothing_and_charges_nothing()
    {
        await GrantAsync(100_000);
        var cache = new StubCache(new WorkflowStepCacheEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            CacheKey = new string('c', 64),
            StepType = StepType.Extract,
            Output = "{\"cached\":true}",
            TokensUsed = 12_345
        });
        var executor = new ScriptedExecutor(StepType.Extract, (context, _) =>
            throw new InvalidOperationException("a cache hit must never reach the executor"));

        Run run = await RunAsync(executor, StepType.Extract, Gate(), cache: cache);

        run.Thrown.Should().BeNull();
        cache.Lookups.Should().Be(1);
        executor.Calls.Should().Be(0);
        run.StepResult!.FromCache.Should().BeTrue();
        run.StepResult.Status.Should().Be(StepStatus.Completed);
        run.StepResult.CreditsChargedMillis.Should().Be(0);

        run.Ledger.Should().ContainSingle("a cached step writes no ledger rows beyond the grant")
            .Which.Kind.Should().Be(CreditLedgerEntryKind.PlanRefill);
        run.Balance.ReservedMillis.Should().Be(0);
        run.Balance.BalanceMillis.Should().Be(100_000);
    }

    // -----------------------------------------------------------------
    // Retries: one hold for the whole step, so a retry can never double-charge
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_retried_step_reserves_once_and_settles_once_for_every_attempt()
    {
        await GrantAsync(100_000);
        var executor = new ScriptedExecutor(StepType.Agent, async (context, attempt) =>
        {
            await RecordUsageAsync(1_500);
            return attempt == 1 ? Failed(context, "UNEXPECTED_ERROR") : Completed(context);
        });

        Run run = await RunAsync(
            executor, StepType.Agent, Gate(),
            hardening: new WorkflowHardeningOptions { MaxStepRetries = 2, RetryBaseDelaySeconds = 0 });

        run.Thrown.Should().BeNull();
        executor.Calls.Should().Be(2);
        run.StepResult!.Status.Should().Be(StepStatus.Completed);
        run.StepResult.CreditsChargedMillis.Should().Be(3_000, "both attempts' usage is charged, once");

        run.Ledger.Select(e => e.Kind).Should().BeEquivalentTo(
            [CreditLedgerEntryKind.PlanRefill, CreditLedgerEntryKind.Reserve, CreditLedgerEntryKind.Settle]);
        run.Ledger.Single(e => e.Kind == CreditLedgerEntryKind.Reserve).ReservedDeltaMillis.Should().Be(StepReserveMillis);
        run.Balance.BalanceMillis.Should().Be(97_000);

        // The whole-run sweep finds nothing left to release: the step's own hold is already closed.
        (await Gate().ReleaseExecutionAsync(run.Execution.Id)).Should().Be(0);
        (await BalanceAsync()).ReservedMillis.Should().Be(0);
    }

    [Fact]
    public async Task The_reservation_is_idempotent_for_the_same_step_result()
    {
        await GrantAsync(100_000);
        CreditGate gate = Gate();
        Guid stepResultId = Guid.NewGuid();

        CreditReservation first = await gate.ReserveAsync(_org, Guid.NewGuid(), stepResultId, StepReserveMillis);
        CreditReservation second = await gate.ReserveAsync(_org, Guid.NewGuid(), stepResultId, StepReserveMillis);

        second.ReservationId.Should().Be(first.ReservationId);
        second.Reason.Should().Be("RESUMED");
        (await BalanceAsync()).ReservedMillis.Should().Be(StepReserveMillis);
        (await LedgerEntryCountAsync()).Should().Be(2); // one grant, one reserve
    }

    // -----------------------------------------------------------------
    // Refusal
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_refused_reservation_fails_the_step_with_INSUFFICIENT_CREDITS_and_runs_nothing()
    {
        // No grant at all: the org cannot cover a 20-credit hold.
        var executor = new ScriptedExecutor(StepType.Agent, (context, _) =>
            throw new InvalidOperationException("the executor must never run without credits"));

        Run run = await RunAsync(executor, StepType.Agent, Gate());

        executor.Calls.Should().Be(0);
        run.Thrown.Should().BeOfType<CreditRefusalException>();
        run.Thrown!.Message.Should().Contain("INSUFFICIENT_CREDITS");
        run.Execution.Status.Should().Be(ExecutionStatus.Failed);
        run.Execution.ErrorMessage.Should().Contain("INSUFFICIENT_CREDITS");
        run.StepResult!.Status.Should().Be(StepStatus.Failed);
        run.StepResult.FailureClass.Should().Be(FailureClass.InsufficientCredits);
        run.StepResult.FailureCode.Should().Be("INSUFFICIENT_CREDITS");
        run.StepResult.CreditsChargedMillis.Should().Be(0);
        run.Ledger.Should().BeEmpty("nothing was held and nothing ran");
    }

    [Fact]
    public async Task Insufficient_credits_are_not_retried()
    {
        var executor = new ScriptedExecutor(StepType.Agent, (context, _) => Task.FromResult(Completed(context)));

        await RunAsync(executor, StepType.Agent, Gate(), hardening: new WorkflowHardeningOptions { MaxStepRetries = 3 });

        executor.Calls.Should().Be(0);
    }

    // -----------------------------------------------------------------
    // The whole-run path, and the disabled no-op
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_failed_run_releases_any_hold_that_is_still_open()
    {
        await GrantAsync(100_000);
        var gate = new Mock<ICreditGate>();
        gate.Setup(g => g.ReserveAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<Guid?>(),
                It.IsAny<CreditEntitlement>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<long?>()))
            .ReturnsAsync(new CreditReservation(CreditReserveOutcome.Allowed, Guid.NewGuid(), null, StepReserveMillis, 100_000));
        gate.Setup(g => g.ReleaseAsync(It.IsAny<Guid?>(), It.IsAny<FailureClass>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditSettlement(true));
        gate.Setup(g => g.ReleaseExecutionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var executor = new ScriptedExecutor(StepType.VideoCompile, (context, _) => Task.FromResult(Failed(context, "ENCODE_FAILED")));

        Run run = await RunAsync(executor, StepType.VideoCompile, gate.Object);

        run.Execution.Status.Should().Be(ExecutionStatus.Failed);
        gate.Verify(g => g.ReleaseExecutionAsync(run.Execution.Id, It.IsAny<CancellationToken>()), Times.Once,
            "the whole-run path sweeps any hold a failed step's own catch did not close");
    }

    [Fact]
    public async Task Billing_disabled_is_a_strict_no_op()
    {
        // Billing:Enabled=false is the self-host default: the gate allows everything and writes
        // nothing, so an execution behaves exactly as it did before B6b existed.
        await GrantAsync(100_000);
        var executor = new ScriptedExecutor(StepType.Agent, async (context, _) =>
        {
            await RecordUsageAsync(4_500);
            return Completed(context);
        });

        Run run = await RunAsync(executor, StepType.Agent, Gate(enabled: false));

        run.Thrown.Should().BeNull();
        run.Execution.Status.Should().Be(ExecutionStatus.Passed);
        run.StepResult!.Status.Should().Be(StepStatus.Completed);
        run.StepResult.CreditsChargedMillis.Should().Be(0);
        run.StepResult.FailureClass.Should().Be(FailureClass.None);

        run.Ledger.Should().ContainSingle().Which.Kind.Should().Be(CreditLedgerEntryKind.PlanRefill);
        run.Balance.BalanceMillis.Should().Be(100_000);
        run.Balance.ReservedMillis.Should().Be(0);
    }

    [Fact]
    public async Task Billing_disabled_never_fails_a_run_that_would_otherwise_succeed()
    {
        // No grant, no credit account at all — with billing off that is not the user's problem.
        var executor = new ScriptedExecutor(StepType.VideoCompile, (context, _) => Task.FromResult(Failed(context, "ENCODE_FAILED")));

        Run run = await RunAsync(executor, StepType.VideoCompile, Gate(enabled: false));

        run.Execution.Status.Should().Be(ExecutionStatus.Failed);
        run.Thrown.Should().BeOfType<InvalidOperationException>();
        run.Thrown!.Message.Should().NotContain("INSUFFICIENT_CREDITS");
        run.Ledger.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unwired_gate_leaves_the_executor_exactly_as_it_was()
    {
        // No gate resolved at all (the shape every existing unit test constructs): nothing is held,
        // nothing is charged, and the run behaves identically.
        var executor = new ScriptedExecutor(StepType.Agent, async (context, _) =>
        {
            await RecordUsageAsync(4_500);
            return Completed(context);
        });

        Run run = await RunAsync(executor, StepType.Agent, gate: null);

        run.Thrown.Should().BeNull();
        run.Execution.Status.Should().Be(ExecutionStatus.Passed);
        run.StepResult!.CreditsChargedMillis.Should().Be(0);
        run.Ledger.Should().BeEmpty();
    }

    [Fact]
    public void The_retry_wrapper_carries_the_failure_classification_of_the_returned_attempt()
    {
        // R3: WithAttemptMetadata hand-copies every field, so anything missed here is silently
        // dropped on any step that goes through a retry attempt.
        var original = new StepExecutionResult
        {
            Output = "{}",
            NextStepIndex = 1,
            Status = StepStatus.Completed,
            FailureClass = FailureClass.UserInput,
            FailureCode = "CONFIG_INVALID"
        };

        System.Reflection.MethodInfo method = typeof(WorkflowExecutorService).GetMethod(
            "WithAttemptMetadata", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var copied = (StepExecutionResult)method.Invoke(null, [original, 2])!;

        copied.FailureClass.Should().Be(FailureClass.UserInput);
        copied.FailureCode.Should().Be("CONFIG_INVALID");
    }

    // -----------------------------------------------------------------
    // The estimate itself
    // -----------------------------------------------------------------

    [Fact]
    public void VideoGenerate_holds_its_own_default_since_B6c()
    {
        // Through B6b this step held 0, on the reasoning that it charges the USD
        // external_generation_jobs ledger directly and a credit hold would charge the same take
        // twice. B6c closed that leak the other way round: the credit hold is settled by the
        // MEASURED VideoGenSeconds the step records, which is precisely what makes it not a second
        // charge — and a zero hold meant the gate took no reservation and never settled anything.
        var options = new StepCreditOptions();

        options.DefaultReserveMillisFor(StepType.VideoGenerate)
            .Should().Be(StepCreditDefaults.MillisFor(StepType.VideoGenerate).Low)
            .And.BePositive();

        // B7 replaced B6b's flat Billing:StepReserveMillis placeholder with B0's per-step-type
        // defaults (and, when the estimator is wired, the organization's own p90). What is left of
        // the old knob is an explicit override that is null by default.
        options.DefaultReserveMillisFor(StepType.Agent).Should().BePositive();
        options.DefaultReserveMillisFor(StepType.EditRoom).Should().BePositive();

        var pinned = new StepCreditOptions { StepReserveMillisOverride = StepReserveMillis };
        pinned.DefaultReserveMillisFor(StepType.Agent).Should().Be(StepReserveMillis);
        pinned.DefaultReserveMillisFor(StepType.VideoGenerate).Should().Be(StepReserveMillis);
    }

    [Fact]
    public async Task The_hold_comes_from_the_estimator_when_one_is_wired()
    {
        await GrantAsync(100_000);
        var executor = new ScriptedExecutor(StepType.Agent, (context, _) => Task.FromResult(Completed(context)));
        var estimator = new StubHoldEstimator(37_500);

        // No StepCreditOptions override: the estimate is what sizes the hold.
        Run run = await RunAsync(executor, StepType.Agent, Gate(), creditOptions: new StepCreditOptions(), holdEstimator: estimator);

        run.Thrown.Should().BeNull();
        estimator.Calls.Should().Be(1);
        run.Ledger.Single(e => e.Kind == CreditLedgerEntryKind.Reserve).ReservedDeltaMillis.Should().Be(37_500);
    }

    [Fact]
    public async Task A_cold_start_run_reserves_the_low_edge_of_its_usual_range_through_the_real_estimator()
    {
        await GrantAsync(100_000);
        var executor = new ScriptedExecutor(StepType.Agent, (context, _) => Task.FromResult(Completed(context)));
        // No history, no provider resolver and no rate cards: the run plan prices the step's cold
        // profile (a VideoStoryEditor Agent step) at the seed constants.
        var estimator = new UsageHistoryCreditHoldEstimator(
            new LedgerFactory(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options),
            Options.Create(new StepCreditEstimateOptions()),
            NullLogger<UsageHistoryCreditHoldEstimator>.Instance);

        Run run = await RunAsync(executor, StepType.Agent, Gate(), creditOptions: new StepCreditOptions(), holdEstimator: estimator);

        // A one-step run: the low edge of its usual range is that step's own p25 (the cold-start hold re-plan).
        long expected = ColdStartStepPricing.Price(
            StepUsageProfiles.For(StepType.Agent, AgentType.VideoStoryEditor), null, SeedProfilePricer.Instance).Cost.P25Millis;
        run.Thrown.Should().BeNull();
        expected.Should().BePositive().And.NotBe(StepCreditDefaults.MillisFor(StepType.Agent).Low);
        run.Ledger.Single(e => e.Kind == CreditLedgerEntryKind.Reserve).ReservedDeltaMillis.Should().Be(expected);
    }

    [Fact]
    public async Task Without_an_override_or_an_estimator_the_hold_is_B0s_per_step_type_default()
    {
        await GrantAsync(100_000);
        var executor = new ScriptedExecutor(StepType.Agent, (context, _) => Task.FromResult(Completed(context)));

        Run run = await RunAsync(executor, StepType.Agent, Gate(), creditOptions: new StepCreditOptions());

        run.Thrown.Should().BeNull();

        // The flat Billing:StepReserveMillis placeholder B6b shipped is gone: what is held now is the
        // Agent step type's own B0 default, and it is not the old 20-credit figure by coincidence but
        // by the same derivation (p50 9,766 tokens at 0.5 millicredits per token).
        run.Ledger.Single(e => e.Kind == CreditLedgerEntryKind.Reserve).ReservedDeltaMillis
            .Should().Be(StepCreditDefaults.MillisFor(StepType.Agent).Low);
    }

    /// <summary>An estimator that answers one fixed figure and counts its calls.</summary>
    private sealed class StubHoldEstimator(long millis) : ICreditHoldEstimator
    {
        public int Calls { get; private set; }

        public ValueTask<long> HoldMillisAsync(
            Guid organizationId, StepType stepType, Guid? agentDefinitionId, CancellationToken ct = default)
        {
            Calls++;
            return ValueTask.FromResult(millis);
        }

        /// <summary>B6c's config-aware arm: not exercised here, so it declines and the fixed figure wins.</summary>
        public ValueTask<long?> TryVideoGenerateHoldMillisAsync(string? videoGenerateConfigJson, CancellationToken ct = default)
            => ValueTask.FromResult<long?>(null);
    }

    /// <summary>An estimator whose AudioGenerate arm prices the step from its own settings (T-108).</summary>
    private sealed class AudioHoldEstimator(long declared) : ICreditHoldEstimator
    {
        public string? SeenConfig { get; private set; }

        public ValueTask<long> HoldMillisAsync(
            Guid organizationId, StepType stepType, Guid? agentDefinitionId, CancellationToken ct = default) =>
            ValueTask.FromResult(1L);

        public ValueTask<long?> TryVideoGenerateHoldMillisAsync(string? videoGenerateConfigJson, CancellationToken ct = default)
            => ValueTask.FromResult<long?>(null);

        public ValueTask<long?> TryAudioGenerateHoldMillisAsync(string? audioGenerateConfigJson, CancellationToken ct = default)
        {
            SeenConfig = audioGenerateConfigJson;
            return ValueTask.FromResult<long?>(declared);
        }
    }

    [Fact]
    public async Task An_audio_generate_step_holds_the_price_of_its_own_settings()
    {
        await GrantAsync(1_000_000);
        var executor = new ScriptedExecutor(StepType.AudioGenerate, (context, _) => Task.FromResult(Completed(context)));
        var estimator = new AudioHoldEstimator(42_000);
        const string config = """{"kind":"Music","prompt":"x","durationSec":30,"takes":2}""";

        Run run = await RunAsync(executor, StepType.AudioGenerate, Gate(), creditOptions: new StepCreditOptions(),
            holdEstimator: estimator, audioGenerateConfigJson: config);

        run.Thrown.Should().BeNull();
        estimator.SeenConfig.Should().Be(config);
        run.Ledger.Single(e => e.Kind == CreditLedgerEntryKind.Reserve).ReservedDeltaMillis.Should().Be(42_000);
    }

    [Fact]
    public void Every_chargeable_step_type_has_a_non_zero_default()
    {
        var options = new StepCreditOptions();

        // VideoGenerate left this list in B6c: it used to hold nothing, which is why the
        // VideoGenSeconds credit rows B3c recorded for it were never settled. Only the step types
        // that make no provider call at all are still free; DesignSystem (P2) joins them because its
        // Parse/Compose/Require ops make no provider call of their own.
        // ExtractFrame (T-105) is one local ffmpeg frame grab: no provider call, so no hold either.
        // PinInsert (T-206) resolves references and writes a plan: no provider call, no hold.
        StepType[] free = [StepType.Conditional, StepType.Extract, StepType.DesignSystem, StepType.ExtractFrame, StepType.PinInsert];

        foreach (StepType type in Enum.GetValues<StepType>().Except(free))
        {
            // A zero hold short-circuits the gate to "no reservation", and a step with no reservation
            // is never settled: a chargeable type left at zero would be free rather than merely
            // unbilled up front. This is the guard against a newly appended enum member shipping
            // that way.
            options.DefaultReserveMillisFor(type).Should().BePositive($"{type} must hold something");
        }
    }

    [Fact]
    public void The_rooms_entitlement_comes_from_the_shared_plan_gate_mapping()
    {
        WorkflowExecutorService.ResolveCreditEntitlement(StepType.EditRoom).Should().Be(CreditEntitlement.Rooms);
        WorkflowExecutorService.ResolveCreditEntitlement(StepType.GraphicsRoom).Should().Be(CreditEntitlement.Rooms);
        WorkflowExecutorService.ResolveCreditEntitlement(StepType.ColorGradeRoom).Should().Be(CreditEntitlement.Rooms);
        WorkflowExecutorService.ResolveCreditEntitlement(StepType.Agent).Should().Be(CreditEntitlement.None);

        // B6c moved VideoGenerate onto credits, so its hold now carries the same entitlement the
        // runtime plan re-check applies (PLAN_NOT_ENTITLED when the plan excludes video generation or
        // its daily video budget is 0 — the reading VideoGenerationBudgetGate already gives).
        WorkflowExecutorService.ResolveCreditEntitlement(StepType.VideoGenerate)
            .Should().Be(CreditEntitlement.VideoGeneration);
    }

    // -----------------------------------------------------------------
    // B6c: VideoGenerate on the credit path
    // -----------------------------------------------------------------

    /// <summary>
    /// The step's hold comes from its own settings — 3 takes x 6 seconds at the model's per-second
    /// rate-card price — and the settle charges the measured <see cref="UsageKind.VideoGenSeconds"/>,
    /// exactly as every other metered step does. Before B6c this step held nothing, took no
    /// reservation, and was therefore never settled at all.
    /// </summary>
    [Fact]
    public async Task A_video_generate_step_reserves_its_per_take_price_and_settles_the_measured_seconds()
    {
        await GrantAsync(1_000_000);
        var estimator = VideoHoldEstimator();

        var executor = new ScriptedExecutor(StepType.VideoGenerate, async (context, _) =>
        {
            // What the executor's own RecordVideoGenUsageAsync writes once a take is delivered: the
            // platform-side job spec's seconds, priced by the rate card.
            await RecordVideoUsageAsync(seconds: 6, millisPerSecond: PerSecondMillis);
            return Completed(context);
        });

        Run run = await RunAsync(
            executor, StepType.VideoGenerate, Gate(),
            creditOptions: new StepCreditOptions(),
            holdEstimator: estimator,
            videoGenerateConfigJson: VideoConfigJson(takes: 3, durationSeconds: 6));

        run.Thrown.Should().BeNull();

        CreditLedgerEntry reserve = run.Ledger.Single(e => e.Kind == CreditLedgerEntryKind.Reserve);
        reserve.ReservedDeltaMillis.Should().Be(3 * 6 * PerSecondMillis);

        // One settled take of 6 seconds: the three-take hold is released down to what was measured.
        run.StepResult!.CreditsChargedMillis.Should().Be(6L * PerSecondMillis);
        run.Balance.AvailableMillis.Should().Be(1_000_000 - (6L * PerSecondMillis));
    }

    /// <summary>
    /// The refund rule for a platform-caused failure, on this step's own vocabulary: every take
    /// failing is <c>meta.failed = ALL_TAKES_FAILED</c> (the vendor's side), which B1 maps to
    /// <see cref="FailureClass.PlatformProviderError"/> — so the org pays nothing for the seconds the
    /// takes burned. The step never throws, so this also proves the release path is reached from a
    /// Failed RESULT and not only from an exception.
    /// </summary>
    [Fact]
    public async Task A_video_generate_step_whose_takes_all_failed_is_refunded()
    {
        await GrantAsync(1_000_000);
        var executor = new ScriptedExecutor(StepType.VideoGenerate, async (context, _) =>
        {
            await RecordVideoUsageAsync(seconds: 6, millisPerSecond: PerSecondMillis);
            return FailedWithOutput(
                context, """
                    {"clips":[],"meta":{"failed":"ALL_TAKES_FAILED","error":"every take failed at the provider"}}
                    """);
        });

        Run run = await RunAsync(
            executor, StepType.VideoGenerate, Gate(),
            creditOptions: new StepCreditOptions(),
            holdEstimator: VideoHoldEstimator(),
            videoGenerateConfigJson: VideoConfigJson(takes: 3, durationSeconds: 6));

        run.Thrown.Should().NotBeNull();
        run.Execution.Status.Should().Be(ExecutionStatus.Failed);
        run.StepResult!.FailureClass.Should().Be(FailureClass.PlatformProviderError);
        run.StepResult.FailureCode.Should().Be("ALL_TAKES_FAILED");
        run.StepResult.CreditsChargedMillis.Should().Be(0);

        run.Balance.BalanceMillis.Should().Be(1_000_000, "a platform-caused failure is refunded in full");
        run.Balance.ReservedMillis.Should().Be(0, "the hold is closed either way");
        run.Ledger.Select(e => e.Kind).Should().BeEquivalentTo(
            [CreditLedgerEntryKind.PlanRefill, CreditLedgerEntryKind.Reserve, CreditLedgerEntryKind.Release]);
    }

    private const long PerSecondMillis = 7_000;

    private static string VideoConfigJson(int takes, int durationSeconds) => $$"""
        {"planSource":"Inline","inlinePrompt":"a shot","takes":{{takes}},"maxClips":{{takes}},
         "durationSeconds":{{durationSeconds}},"resolution":"768P","ratio":"16:9","maxSpendCredits":5}
        """;

    /// <summary>
    /// The engine's real hold estimator with B6c's video arm wired to fakes. The history factory
    /// throws: the video arm answers before any history is read, which is the point — the hold is
    /// priced from the step's settings, not from a database.
    /// </summary>
    private static ICreditHoldEstimator VideoHoldEstimator()
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedVideoGenerationProvider(
                Guid.NewGuid(), "MiniMax", InferenceProviderKind.MiniMax, "https://api.minimax.io",
                "MiniMax-H3", "key", 60));

        var client = new Mock<IVideoGenerationClient>();
        client.Setup(c => c.Describe(It.IsAny<string>())).Returns(new VideoGenerationCapabilities(
            PricePerSecondByResolution: new Dictionary<string, decimal> { { "768P", 0.08m } },
            MinDurationSeconds: 4,
            MaxDurationSeconds: 15,
            AllowedRatios: ["16:9"]));

        var clients = new Mock<IVideoGenerationClientFactory>();
        clients.Setup(f => f.Get(It.IsAny<ResolvedVideoGenerationProvider>())).Returns(client.Object);

        return new UsageHistoryCreditHoldEstimator(
            new UnusedContextFactory(),
            Options.Create(new StepCreditEstimateOptions()),
            NullLogger<UsageHistoryCreditHoldEstimator>.Instance,
            time: null,
            videoProviders: resolver.Object,
            videoClients: clients.Object,
            prices: new PerSecondPriceService(PerSecondMillis));
    }

    /// <summary>Records a VideoGenSeconds row into the AMBIENT usage scope, as the executor's own metering does.</summary>
    private static async Task RecordVideoUsageAsync(int seconds, long millisPerSecond)
    {
        var recorder = new UsageRecorder(
            new PerSecondPriceService(millisPerSecond), new NullUsageSink(), NullLogger<UsageRecorder>.Instance);

        await recorder.RecordAsync(new UsageRecordRequest(
            UsageKind.VideoGenSeconds, seconds, null, InferenceProviderKind.MiniMax, "MiniMax-H3",
            UsageBillingMode.Platform));
    }

    /// <summary>Prices VideoGenSeconds at a fixed per-second figure and everything else at zero.</summary>
    private sealed class PerSecondPriceService(long millisPerSecond) : IModelPriceService
    {
        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default)
            => ValueTask.FromResult(new PriceQuote(
                request.Unit == UsageKind.VideoGenSeconds && request.BillingMode == UsageBillingMode.Platform
                    ? (long)(request.Quantity * millisPerSecond)
                    : 0,
                0m,
                Guid.NewGuid()));

        public ValueTask<PriceQuote> PriceQuotedUsdAsync(decimal costUsd, UsageKind unit, UsageBillingMode billingMode,
            DateTime? at = null, CancellationToken ct = default)
            => ValueTask.FromResult(new PriceQuote(0, 0m, null));

        public void Invalidate()
        {
        }
    }

    /// <summary>A factory that throws if a context is ever asked for.</summary>
    private sealed class UnusedContextFactory : IDbContextFactory<WorkflowEngineDbContext>
    {
        public WorkflowEngineDbContext CreateDbContext() => throw new InvalidOperationException("no database");
    }
}
