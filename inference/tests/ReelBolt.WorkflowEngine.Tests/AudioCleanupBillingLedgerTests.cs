using System;
using System.Collections.Generic;
using System.IO;
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
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Messaging;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Cleanup;
using ReelBolt.WorkflowEngine.Tests.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// QA (audio-cleanup T-011): the plan's billing rules for <c>StepType.AudioCleanup</c> asserted on the LEDGER, not on mock
/// call counts. The REAL <see cref="AudioCleanupStepExecutor"/> runs inside the REAL <see cref="WorkflowExecutorService"/>,
/// whose hold comes from the REAL <see cref="UsageHistoryCreditHoldEstimator"/> (AudioCleanupCreditPricing), whose
/// usage goes through a REAL <see cref="UsageRecorder"/> into the step's usage scope, and whose gate is the REAL
/// <see cref="CreditGate"/> over a Sqlite credit ledger. Only the vendor, the media session and the file store are fakes.
/// Price: 1 credit (1000 millis) per started audio minute.
/// </summary>
public sealed class AudioCleanupBillingLedgerTests : IDisposable
{
    private const long MillisPerMinute = 1_000;
    private const long Granted = 100_000;
    private const string Model = "fal-ai/elevenlabs/audio-isolation";

    private readonly SqliteConnection _ledgerConnection = new("Data Source=:memory:;Foreign Keys=False");
    private readonly DbContextOptions<WorkflowEngineDbContext> _ledgerOptions;
    private readonly Guid _org = Guid.NewGuid();
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _fileId = Guid.NewGuid();

    public AudioCleanupBillingLedgerTests()
    {
        _ledgerConnection.Open();
        _ledgerOptions = new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseSqlite(_ledgerConnection).Options;
        using (InferenceApiDbContext owner = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_ledgerConnection).Options))
        {
            owner.Database.EnsureCreated();
        }

        UnsettledHoldTestKit.AddEngineTables(_ledgerConnection);
    }

    public void Dispose() => _ledgerConnection.Dispose();

    private sealed class LedgerFactory(DbContextOptions<WorkflowEngineDbContext> options) : IDbContextFactory<WorkflowEngineDbContext>
    {
        public WorkflowEngineDbContext CreateDbContext() => new(options);
    }

    private string HostedConfig(bool consent = true, int maxMinutes = 5) =>
        $$$"""{"target":"IsolateVoice","source":{"kind":"ProjectFile","projectFileId":"{{{_fileId}}}"},"allowSourceMediaEgress":{{{(consent ? "true" : "false")}}},"maxSourceMinutes":{{{maxMinutes}}}}""";

    private string LocalConfig() =>
        $$$"""{"target":"Wind","source":{"kind":"ProjectFile","projectFileId":"{{{_fileId}}}"}}""";

    // ------------------------------------------------------------------ the rules

    [Fact]
    public async Task Billing_on_holds_the_step_cap_before_any_upload_and_settles_the_billed_started_minutes()
    {
        Harness h = new(this) { SourceSeconds = 95 };
        Run run = await h.RunAsync(HostedConfig(maxMinutes: 5));

        run.StepResult!.Status.Should().Be(StepStatus.Completed, run.StepResult.Output);
        h.Uploaded.Should().ContainSingle();
        h.Deleted.Should().Equal(h.Uploaded);

        // Hold = the step's cap (5 minutes) at the price book; settle = 95 s = 2 started minutes.
        run.Ledger.Single(e => e.Kind == CreditLedgerEntryKind.Reserve).ReservedDeltaMillis.Should().Be(5 * MillisPerMinute);
        run.Ledger.Single(e => e.Kind == CreditLedgerEntryKind.Settle).BalanceDeltaMillis.Should().Be(-2 * MillisPerMinute);
        run.StepResult.CreditsChargedMillis.Should().Be(2 * MillisPerMinute);
        run.Balance.BalanceMillis.Should().Be(Granted - 2 * MillisPerMinute);
        run.Balance.ReservedMillis.Should().Be(0);

        // The reservation exists before the vendor ever saw a byte.
        h.ReservedWhenUploaded.Should().Be(5 * MillisPerMinute, "the hold is taken before the upload");
    }

    [Fact]
    public async Task Billing_off_the_hosted_path_is_free_and_writes_nothing_to_the_ledger()
    {
        Harness h = new(this) { SourceSeconds = 95, BillingEnabled = false };
        Run run = await h.RunAsync(HostedConfig());

        run.StepResult!.Status.Should().Be(StepStatus.Completed, run.StepResult.Output);
        h.Uploaded.Should().ContainSingle();
        run.Ledger.Select(e => e.Kind).Should().Equal(CreditLedgerEntryKind.PlanRefill);
        run.Balance.BalanceMillis.Should().Be(Granted);
        run.StepResult.CreditsChargedMillis.Should().Be(0);
    }

    [Fact]
    public async Task A_local_target_takes_no_hold_and_charges_nothing()
    {
        Harness h = new(this) { SourceSeconds = 95 };
        Run run = await h.RunAsync(LocalConfig());

        run.StepResult!.Status.Should().Be(StepStatus.Completed, run.StepResult.Output);
        h.Uploaded.Should().BeEmpty();
        run.Ledger.Select(e => e.Kind).Should().Equal(CreditLedgerEntryKind.PlanRefill);
        run.Balance.BalanceMillis.Should().Be(Granted);
    }

    [Fact]
    public async Task A_platform_internal_failure_after_the_upload_is_refunded_in_full()
    {
        // The remux of a video source fails: AUDIO_CLEANUP_FAILED, PlatformInternal.
        Harness h = new(this) { SourceSeconds = 95, Video = true, FailArgvContaining = "copy" };
        Run run = await h.RunAsync(HostedConfig());

        run.StepResult!.Status.Should().Be(StepStatus.Failed);
        run.StepResult.FailureCode.Should().Be("AUDIO_CLEANUP_FAILED");
        run.StepResult.FailureClass.Should().Be(FailureClass.PlatformInternal);
        h.Uploaded.Should().ContainSingle();
        run.Ledger.Single(e => e.Kind == CreditLedgerEntryKind.Release).Note.Should().Be(nameof(FailureClass.PlatformInternal));
        run.Ledger.Should().NotContain(e => e.Kind == CreditLedgerEntryKind.Settle);
        run.Balance.BalanceMillis.Should().Be(Granted);
        run.Balance.ReservedMillis.Should().Be(0);
    }

    [Fact]
    public async Task AUDIO_DURATION_MISMATCH_is_refunded_in_full()
    {
        Harness h = new(this) { SourceSeconds = 95, StemSeconds = 95.2 };
        Run run = await h.RunAsync(HostedConfig());

        run.StepResult!.Status.Should().Be(StepStatus.Failed);
        run.StepResult.FailureCode.Should().Be("AUDIO_DURATION_MISMATCH");
        run.StepResult.FailureClass.Should().Be(FailureClass.PlatformProviderError);
        h.Uploaded.Should().ContainSingle();
        run.Ledger.Should().NotContain(e => e.Kind == CreditLedgerEntryKind.Settle);
        run.Ledger.Single(e => e.Kind == CreditLedgerEntryKind.Release).Note.Should().Be(nameof(FailureClass.PlatformProviderError));
        run.Balance.BalanceMillis.Should().Be(Granted);
        run.Balance.ReservedMillis.Should().Be(0);
    }

    [Fact]
    public async Task A_user_cancel_after_the_upload_charges_nothing_and_still_deletes_the_upload()
    {
        Harness h = new(this) { SourceSeconds = 95, CancelDuringIsolation = true };
        Run run = await h.RunAsync(HostedConfig());

        h.Uploaded.Should().ContainSingle();
        h.Deleted.Should().Equal(h.Uploaded);
        run.Execution.Status.Should().Be(ExecutionStatus.Cancelled);
        run.Ledger.Should().NotContain(e => e.Kind == CreditLedgerEntryKind.Settle && e.BalanceDeltaMillis < 0);
        run.Balance.BalanceMillis.Should().Be(Granted);
        run.Balance.ReservedMillis.Should().Be(0);
    }

    [Fact]
    public async Task Without_consent_nothing_is_uploaded_and_nothing_is_charged()
    {
        Harness h = new(this) { SourceSeconds = 95 };
        Run run = await h.RunAsync(HostedConfig(consent: false));

        run.StepResult!.FailureCode.Should().Be("EGRESS_REFUSED");
        run.StepResult.FailureClass.Should().Be(FailureClass.UserInput);
        h.Uploaded.Should().BeEmpty();
        h.IsolationCalls.Should().Be(0);
        run.Balance.BalanceMillis.Should().Be(Granted);
        run.Balance.ReservedMillis.Should().Be(0);
    }

    [Fact]
    public async Task Too_few_credits_for_the_hold_fails_INSUFFICIENT_CREDITS_before_any_upload()
    {
        Harness h = new(this) { SourceSeconds = 95, Grant = 1_000 };
        Run run = await h.RunAsync(HostedConfig(maxMinutes: 5));

        run.StepResult!.FailureCode.Should().Be("INSUFFICIENT_CREDITS");
        h.Uploaded.Should().BeEmpty();
        h.IsolationCalls.Should().Be(0);
        run.Ledger.Should().NotContain(e => e.Kind == CreditLedgerEntryKind.Settle);
        run.Balance.BalanceMillis.Should().Be(1_000);
    }

    // ------------------------------------------------------------------ harness

    private sealed record Run(WorkflowExecution Execution, WorkflowStepResult? StepResult, IReadOnlyList<CreditLedgerEntry> Ledger, CreditLedgerResult Balance);

    private sealed class Harness(AudioCleanupBillingLedgerTests t)
    {
        public double SourceSeconds { get; init; } = 95;
        public double? StemSeconds { get; init; }
        public bool Video { get; init; }
        public string? FailArgvContaining { get; init; }
        public bool BillingEnabled { get; init; } = true;
        public bool CancelDuringIsolation { get; init; }
        public long Grant { get; init; } = Granted;

        public List<string> Uploaded { get; } = [];
        public List<string> Deleted { get; } = [];
        public int IsolationCalls { get; private set; }
        public long? ReservedWhenUploaded { get; private set; }

        public async Task<Run> RunAsync(string configJson)
        {
            LedgerFactory ledger = new(t._ledgerOptions);
            await using (WorkflowEngineDbContext g = ledger.CreateDbContext())
            {
                (await new CreditLedger(g).GrantAsync(t._org, CreditGrantSource.Plan, Grant, null, Guid.NewGuid().ToString("N"))).Ok.Should().BeTrue();
            }

            CreditGate gate = new(ledger, Options.Create(new CreditGateOptions { Enabled = BillingEnabled }), NullLogger<CreditGate>.Instance);
            PerMinutePrice prices = new();
            var row = new ResolvedAudioGenerationProvider(Guid.NewGuid(), "row", InferenceProviderKind.FalAi, InferenceProviderCapability.AudioIsolation,
                "", Model, "test", 60, AccountId: Guid.NewGuid());
            Mock<IInferenceProviderResolver> resolver = new();
            resolver.Setup(r => r.ResolveAudioIsolationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(row);

            ExecutionCancellationRegistry registry = new();
            DbContextOptions<WorkflowEngineDbContext> engineOptions = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            using var db = new WorkflowEngineDbContext(engineOptions);

            AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "A", AgentType = AgentType.VideoTransform, SystemPrompt = "" };
            WorkflowStep step = new()
            {
                Id = Guid.NewGuid(),
                StepOrder = 1,
                StepType = StepType.AudioCleanup,
                AgentDefinitionId = agent.Id,
                AgentDefinition = agent,
                AudioCleanupConfigJson = configJson,
                CacheMode = StepCacheMode.Never
            };
            WorkflowDefinition definition = new() { Id = Guid.NewGuid(), Name = "t", ProjectId = t._projectId, Steps = [step], Kind = WorkflowDefinitionKind.AudioCleanup };
            WorkflowExecution execution = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = definition.Id,
                ProjectId = t._projectId,
                OrganizationId = t._org,
                Status = ExecutionStatus.Queued,
                WorkflowDefinition = definition
            };
            db.AgentDefinitions.Add(agent);
            db.WorkflowDefinitions.Add(definition);
            db.WorkflowExecutions.Add(execution);
            await db.SaveChangesAsync();

            // ---- the executor's collaborators (fakes for the store, the session and the vendor) ----
            string sourceKey = $"projects/{t._projectId}/userFiles/take.{(Video ? "mp4" : "wav")}";
            Mock<IProjectFileWorkspace> files = new();
            files.Setup(w => w.ListFilesAsync(t._projectId, It.IsAny<CancellationToken>()))
                .ReturnsAsync([new ProjectWorkspaceFile(t._fileId, t._projectId, "take", null, "userFiles", sourceKey, Video ? "video/mp4" : "audio/wav", 1024, DateTime.UtcNow, null)]);
            files.Setup(w => w.RegisterStoredObjectAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                    It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid p, string key, string name, string type, long size, SummaryStatus s, FileIndexingStatus i, CancellationToken c, string cat) =>
                    new ProjectWorkspaceFile(Guid.NewGuid(), p, name, null, cat, key, type, size, DateTime.UtcNow, null));
            files.Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid p, string local, string name, string type, CancellationToken c, string cat) =>
                {
                    string key = $"projects/{p}/{cat}/{name}";
                    RecordUpload(key);
                    return key;
                });

            void RecordUpload(string key)
            {
                Uploaded.Add(key);
                using WorkflowEngineDbContext l = ledger.CreateDbContext();
                ReservedWhenUploaded = new CreditLedger(l).GetBalanceAsync(t._org).GetAwaiter().GetResult().ReservedMillis;
            }
            files.Setup(w => w.DeleteArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<Guid, string, CancellationToken>((_, key, _) => Deleted.Add(key))
                .Returns(Task.CompletedTask);

            Mock<IPresignedUrlService> presigner = new();
            presigner.Setup(p => p.CreateGetAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>())).ReturnsAsync("https://signed.example/get");

            Mock<IAudioIsolationClient> client = new();
            client.Setup(c => c.IsolateAsync(It.IsAny<AudioIsolationRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>()))
                .Returns((AudioIsolationRequest r, CancellationToken ct, Func<string, Task>? _) =>
                {
                    IsolationCalls++;
                    if (CancelDuringIsolation)
                    {
                        registry.TryCancel(execution.Id, "Stopped by user qa");
                        ct.ThrowIfCancellationRequested();
                    }

                    return Task.FromResult(new AudioIsolationResult([1, 2, 3], "audio/mpeg", ".mp3", null, r.SourceDurationSec, Model, null, "req-1"));
                });
            Mock<IAudioIsolationClientFactory> clients = new();
            clients.Setup(c => c.Get(It.IsAny<ResolvedAudioGenerationProvider>())).Returns(client.Object);

            Mock<IOrganizationEntitlements> entitlements = new();
            entitlements.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(() => ValueTask.FromResult(new OrganizationEntitlementSet { Tier = "test", AudioCleanupAllowed = true }));
            ServiceProvider services = new ServiceCollection().AddSingleton(entitlements.Object).BuildServiceProvider();

            SessionWorkspace session = new() { SourceSeconds = SourceSeconds, StemSeconds = StemSeconds, Video = Video, FailArgvContaining = FailArgvContaining, OnUpload = RecordUpload };
            UsageRecorder recorder = new(prices, new NullUsageSink(), NullLogger<UsageRecorder>.Instance);
            var executor = new AudioCleanupStepExecutor(
                new Mock<IVideoToolRunner>(MockBehavior.Strict).Object,
                files.Object,
                services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new VideoEditingOptions()),
                new FakeCleaner(),
                resolver.Object,
                clients.Object,
                NullLogger<AudioCleanupStepExecutor>.Instance,
                prices,
                recorder,
                presigner.Object,
                configuration: null,
                mediaWorkspaces: new StubFactory(session));

            var estimator = new UsageHistoryCreditHoldEstimator(
                ledger, Options.Create(new StepCreditEstimateOptions()), NullLogger<UsageHistoryCreditHoldEstimator>.Instance,
                videoProviders: resolver.Object, prices: prices);

            var service = new WorkflowExecutorService(
                scopeFactory: new EngineScopeFactory(db),
                eventPublisher: new NoOpEventPublisher(),
                logger: NullLogger<WorkflowExecutorService>.Instance,
                executors: [executor],
                rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
                hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
                cancellationRegistry: registry,
                cachePolicy: null,
                cacheKeyBuilder: null,
                creditGate: gate,
                creditOptions: Options.Create(new StepCreditOptions()),
                creditHoldEstimator: estimator);

            try
            {
                await service.ExecuteAsync(execution.Id, "corr", CancellationToken.None);
            }
            catch (Exception)
            {
                // a failed step or a user stop surfaces as an exception from ExecuteAsync; the ledger is what is asserted
            }

            WorkflowStepResult? stepResult = await db.WorkflowStepResults.AsNoTracking()
                .FirstOrDefaultAsync(r => r.WorkflowExecutionId == execution.Id);
            WorkflowExecution after = await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == execution.Id);

            await using WorkflowEngineDbContext read = ledger.CreateDbContext();
            List<CreditLedgerEntry> entries = await read.CreditLedgerEntries.AsNoTracking().ToListAsync();
            CreditLedgerResult balance = await new CreditLedger(read).GetBalanceAsync(t._org);
            return new Run(after, stepResult, entries, balance);
        }
    }

    private sealed class PerMinutePrice : IModelPriceService
    {
        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default)
            => ValueTask.FromResult(new PriceQuote((long)(request.Quantity * MillisPerMinute), 0.01m, Guid.NewGuid()));

        public ValueTask<PriceQuote> PriceQuotedUsdAsync(decimal costUsd, UsageKind unit, UsageBillingMode billingMode,
            DateTime? at = null, CancellationToken ct = default)
            => ValueTask.FromResult(new PriceQuote(0, costUsd, null));

        public void Invalidate() { }
    }

    private sealed class NullUsageSink : IUsageEventSink
    {
        public Task WriteAsync(IReadOnlyList<UsageEvent> events, CancellationToken ct) => Task.CompletedTask;
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

    private sealed class EngineScopeFactory(WorkflowEngineDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(db);

        private sealed class Scope(WorkflowEngineDbContext db) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new Provider(db);
            public void Dispose() { }
        }

        private sealed class Provider(WorkflowEngineDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType)
                => serviceType == typeof(WorkflowEngineDbContext) ? db
                : serviceType == typeof(IProjectFileFingerprintProvider) ? new StubFingerprintProvider()
                : null;
        }
    }

    private sealed class FakeCleaner : ILocalAudioCleaner
    {
        public Task<LocalCleanupResult> CleanAsync(string sourcePath, string outputPath, LocalCleanupTarget target, CancellationToken ct)
            => Task.FromResult(new LocalCleanupResult(true, null, 95, 48_000, 2, CleanupMeasurements.None));
    }

    private sealed class SessionWorkspace : IMediaWorkspace
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public double SourceSeconds { get; init; }
        public double? StemSeconds { get; init; }
        public bool Video { get; init; }
        public string? FailArgvContaining { get; init; }

        public string RootPath => "session:/";

        public Task StageAsync(string storageKey, string virtualPath, CancellationToken ct) => Task.CompletedTask;

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct) =>
            RunFfmpegAsync(argv, timeout, ct, null!);

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine)
        {
            if (FailArgvContaining is not null && argv.Contains(FailArgvContaining))
                return Task.FromResult(new VideoToolResult(1, string.Empty, "boom", false));
            Files[argv[^1]] = new byte[256];
            return Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));
        }

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct)
        {
            bool stem = argv[^1].StartsWith("audio-cleanup-stem", StringComparison.Ordinal);
            double dur = stem ? StemSeconds ?? SourceSeconds : SourceSeconds;
            bool video = Video && !stem;
            string streams = (video ? "{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":1920,\"height\":1080,\"r_frame_rate\":\"30/1\"}," : "")
                + "{\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"sample_rate\":\"48000\",\"channels\":2}";
            string json = $"{{\"streams\":[{streams}],\"format\":{{\"duration\":\"{dur.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"}}}}";
            return Task.FromResult(new VideoToolResult(0, json, string.Empty, false));
        }

        public Task<string> MaterializeAsync(string virtualPath, CancellationToken ct) => Task.FromResult(virtualPath);

        public Task WriteBytesAsync(string virtualPath, byte[] content, CancellationToken ct)
        {
            Files[virtualPath] = content;
            return Task.CompletedTask;
        }

        public Task WriteTextAsync(string virtualPath, string content, System.Text.Encoding encoding, CancellationToken ct) =>
            WriteBytesAsync(virtualPath, encoding.GetBytes(content), ct);

        public Task<byte[]> ReadAsync(string virtualPath, CancellationToken ct) => ReadAsync(virtualPath, int.MaxValue, ct);

        public Task<byte[]> ReadAsync(string virtualPath, int maxBytes, CancellationToken ct) =>
            Files.TryGetValue(virtualPath, out byte[]? bytes) ? Task.FromResult(bytes) : throw new FileNotFoundException(virtualPath);

        public Task<MediaWorkspaceStat> StatAsync(string virtualPath, CancellationToken ct)
        {
            if (virtualPath == "audio-cleanup-clean.wav" && !Files.ContainsKey(virtualPath))
                Files[virtualPath] = new byte[512];
            return Task.FromResult(Files.TryGetValue(virtualPath, out byte[]? bytes) ? new MediaWorkspaceStat(true, bytes.Length) : new MediaWorkspaceStat(false, 0));
        }

        public Task<string> PublishAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct) =>
            Task.FromResult(finalStorageKey);

        /// <summary>Called with the key of every session upload (the vendor's source audio goes this way).</summary>
        public Action<string>? OnUpload { get; set; }

        public Task<string> UploadArtifactAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct)
        {
            OnUpload?.Invoke(finalStorageKey);
            return Task.FromResult(finalStorageKey);
        }
    }

    private sealed class StubFactory(SessionWorkspace session) : IMediaWorkspaceFactory
    {
        public Task<MediaWorkspaceOpenResult> OpenAsync(MediaWorkspaceRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(MediaWorkspaceOpenResult.Opened(new MediaWorkspaceLease(
                session, scratch: null, ComputeDecision.Local(ComputeDecisionReasons.LocalMode),
                job: null, request.ExecutionId, request.StepOrder, NullLogger.Instance)));
    }
}
