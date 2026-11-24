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
using ReelBolt.WorkflowEngine.Services.Video.Enhance;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using ReelBolt.WorkflowEngine.Tests.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The plan's billing rules for <c>StepType.VideoEnhance</c> asserted on the LEDGER, not on mock call counts. The REAL
/// <see cref="VideoEnhanceStepExecutor"/> runs inside the REAL <see cref="WorkflowExecutorService"/>, whose hold comes from the
/// REAL <see cref="UsageHistoryCreditHoldEstimator"/> (<see cref="VideoEnhanceStepPricing"/>), whose usage goes through a REAL
/// <see cref="UsageRecorder"/> into the step's usage scope, and whose gate is the REAL <see cref="CreditGate"/> over a Sqlite
/// credit ledger. Only the vendor, the media session, the ffmpeg engines and the file store are fakes. Price: 1 credit
/// (1000 millis) per billed unit (an output second, or a compute second).
/// </summary>
public sealed class VideoEnhanceBillingLedgerTests : IDisposable
{
    private const long MillisPerUnit = 1_000;
    private const long Granted = 100_000;

    private readonly SqliteConnection _ledgerConnection = new("Data Source=:memory:;Foreign Keys=False");
    private readonly DbContextOptions<WorkflowEngineDbContext> _ledgerOptions;
    private readonly Guid _org = Guid.NewGuid();
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _fileId = Guid.NewGuid();

    public VideoEnhanceBillingLedgerTests()
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

    private string HostedUpscale(bool consent = true, int maxSeconds = 30) =>
        $$$"""{"op":"Upscale","engine":"Hosted","upscaleFactor":2,"source":{"kind":"ProjectFile","projectFileId":"{{{_fileId}}}"},"allowSourceMediaEgress":{{{(consent ? "true" : "false")}}},"maxSourceSeconds":{{{maxSeconds}}}}""";

    private string HostedInterpolate(int maxSeconds = 30) =>
        $$$"""{"op":"Interpolate","engine":"Hosted","frameMultiplier":2,"source":{"kind":"ProjectFile","projectFileId":"{{{_fileId}}}"},"allowSourceMediaEgress":true,"maxSourceSeconds":{{{maxSeconds}}}}""";

    private string LocalConfig() =>
        $$$"""{"op":"Denoise","engine":"Local","denoiseStrength":"Light","source":{"kind":"ProjectFile","projectFileId":"{{{_fileId}}}"}}""";

    // ------------------------------------------------------------------ the rules

    [Fact]
    public async Task Billing_on_holds_the_length_bound_before_any_upload_and_settles_the_billed_output_seconds()
    {
        Harness h = new(this) { SourceSeconds = 12.2 };
        Run run = await h.RunAsync(HostedUpscale(maxSeconds: 30));

        run.StepResult!.Status.Should().Be(StepStatus.Completed, run.StepResult.Output);
        h.Uploaded.Should().ContainSingle();
        h.Deleted.Should().Equal(h.Uploaded);
        h.SubmitCalls.Should().Be(1);

        // Hold = the config's 30-second bound at the price book; settle = 12.2 s = 13 billed seconds.
        run.Ledger.Single(e => e.Kind == CreditLedgerEntryKind.Reserve).ReservedDeltaMillis.Should().Be(30 * MillisPerUnit);
        run.Ledger.Single(e => e.Kind == CreditLedgerEntryKind.Settle).BalanceDeltaMillis.Should().Be(-13 * MillisPerUnit);
        run.StepResult.CreditsChargedMillis.Should().Be(13 * MillisPerUnit);
        run.Balance.BalanceMillis.Should().Be(Granted - 13 * MillisPerUnit);
        run.Balance.ReservedMillis.Should().Be(0);

        // The reservation exists before the vendor ever saw a byte.
        h.ReservedWhenUploaded.Should().Be(30 * MillisPerUnit, "the hold is taken before the upload");
        h.Jobs.Should().ContainSingle().Which.Status.Should().Be(ExternalGenerationJobStatus.Succeeded);
    }

    [Fact]
    public async Task A_hosted_interpolation_settles_the_compute_second_hold_not_a_vendor_figure()
    {
        Harness h = new(this) { SourceSeconds = 10, Grant = 500_000 };
        Run run = await h.RunAsync(HostedInterpolate(maxSeconds: 30));

        run.StepResult!.Status.Should().Be(StepStatus.Completed, run.StepResult.Output);
        // Hold: 30 s at the 60 fps reference x2 = 3600 output frames x 0.05 s = 180 compute seconds.
        run.Ledger.Single(e => e.Kind == CreditLedgerEntryKind.Reserve).ReservedDeltaMillis.Should().Be(180 * MillisPerUnit);
        // Settle: 10 s at 30 fps x2 = 600 frames x 0.05 s = 30 compute seconds (the hold is the settlement).
        run.Ledger.Single(e => e.Kind == CreditLedgerEntryKind.Settle).BalanceDeltaMillis.Should().Be(-30 * MillisPerUnit);
        run.Balance.BalanceMillis.Should().Be(500_000 - 30 * MillisPerUnit);
        run.Balance.ReservedMillis.Should().Be(0);
    }

    [Fact]
    public async Task Billing_off_the_hosted_path_is_free_and_writes_nothing_to_the_ledger()
    {
        Harness h = new(this) { SourceSeconds = 12.2, BillingEnabled = false };
        Run run = await h.RunAsync(HostedUpscale());

        run.StepResult!.Status.Should().Be(StepStatus.Completed, run.StepResult.Output);
        h.Uploaded.Should().ContainSingle();
        run.Ledger.Select(e => e.Kind).Should().Equal(CreditLedgerEntryKind.PlanRefill);
        run.Balance.BalanceMillis.Should().Be(Granted);
        run.StepResult.CreditsChargedMillis.Should().Be(0);
    }

    [Fact]
    public async Task A_local_operation_takes_no_hold_and_charges_nothing()
    {
        Harness h = new(this) { SourceSeconds = 12.2 };
        Run run = await h.RunAsync(LocalConfig());

        run.StepResult!.Status.Should().Be(StepStatus.Completed, run.StepResult.Output);
        h.Uploaded.Should().BeEmpty();
        h.Jobs.Should().BeEmpty();
        run.Ledger.Select(e => e.Kind).Should().Equal(CreditLedgerEntryKind.PlanRefill);
        run.Balance.BalanceMillis.Should().Be(Granted);
    }

    [Fact]
    public async Task A_platform_internal_failure_after_the_upload_is_refunded_in_full()
    {
        Harness h = new(this) { SourceSeconds = 12.2, RemuxThrows = new EnhanceException(EnhanceErrorCodes.FfmpegFailed, "ffmpeg", "the remux failed") };
        Run run = await h.RunAsync(HostedUpscale());

        run.StepResult!.Status.Should().Be(StepStatus.Failed);
        run.StepResult.FailureCode.Should().Be("ENHANCE_FFMPEG_FAILED");
        run.StepResult.FailureClass.Should().Be(FailureClass.PlatformInternal);
        h.Uploaded.Should().ContainSingle();
        h.Deleted.Should().Equal(h.Uploaded);
        run.Ledger.Single(e => e.Kind == CreditLedgerEntryKind.Release).Note.Should().Be(nameof(FailureClass.PlatformInternal));
        run.Ledger.Should().NotContain(e => e.Kind == CreditLedgerEntryKind.Settle);
        run.Balance.BalanceMillis.Should().Be(Granted);
        run.Balance.ReservedMillis.Should().Be(0);
    }

    [Fact]
    public async Task ENHANCE_DURATION_MISMATCH_is_refunded_in_full()
    {
        Harness h = new(this) { SourceSeconds = 12.2, RemuxThrows = new EnhanceException(EnhanceErrorCodes.DurationMismatch, "output", "12.9s versus 12.2s") };
        Run run = await h.RunAsync(HostedUpscale());

        run.StepResult!.Status.Should().Be(StepStatus.Failed);
        run.StepResult.FailureCode.Should().Be("ENHANCE_DURATION_MISMATCH");
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
        Harness h = new(this) { SourceSeconds = 12.2, CancelDuringPoll = true };
        Run run = await h.RunAsync(HostedUpscale());

        h.Uploaded.Should().ContainSingle();
        h.Deleted.Should().Equal(h.Uploaded);
        run.Execution.Status.Should().Be(ExecutionStatus.Cancelled);
        run.Ledger.Should().NotContain(e => e.Kind == CreditLedgerEntryKind.Settle && e.BalanceDeltaMillis < 0);
        run.Balance.BalanceMillis.Should().Be(Granted);
        run.Balance.ReservedMillis.Should().Be(0);
    }

    [Fact]
    public async Task A_refused_consent_takes_nothing_and_uploads_nothing()
    {
        Harness h = new(this) { SourceSeconds = 12.2 };
        Run run = await h.RunAsync(HostedUpscale(consent: false));

        run.StepResult!.FailureCode.Should().Be("EGRESS_REFUSED");
        run.StepResult.FailureClass.Should().Be(FailureClass.UserInput);
        h.Uploaded.Should().BeEmpty();
        h.SubmitCalls.Should().Be(0);
        h.Jobs.Should().BeEmpty();
        run.Balance.BalanceMillis.Should().Be(Granted);
        run.Balance.ReservedMillis.Should().Be(0);
    }

    [Fact]
    public async Task Too_few_credits_for_the_hold_fails_INSUFFICIENT_CREDITS_before_any_upload()
    {
        Harness h = new(this) { SourceSeconds = 12.2, Grant = 1_000 };
        Run run = await h.RunAsync(HostedUpscale(maxSeconds: 30));

        run.StepResult!.FailureCode.Should().Be("INSUFFICIENT_CREDITS");
        h.Uploaded.Should().BeEmpty();
        h.SubmitCalls.Should().Be(0);
        run.Ledger.Should().NotContain(e => e.Kind == CreditLedgerEntryKind.Settle);
        run.Balance.BalanceMillis.Should().Be(1_000);
    }

    [Fact]
    public void Only_a_hosted_operation_asks_the_credit_gate_for_the_plan_feature()
    {
        WorkflowStep Step(string json) => new() { StepType = StepType.VideoEnhance, VideoEnhanceConfigJson = json };
        WorkflowExecutorService.ResolveCreditEntitlement(Step(HostedUpscale())).Should().Be(CreditEntitlement.VideoEnhance);
        WorkflowExecutorService.ResolveCreditEntitlement(Step(LocalConfig())).Should().Be(CreditEntitlement.None);
        WorkflowExecutorService.ResolveCreditEntitlement(Step("{broken")).Should().Be(CreditEntitlement.VideoEnhance, "an unreadable config is treated as hosted");
        WorkflowExecutorService.ResolveCreditEntitlement(new WorkflowStep { StepType = StepType.AudioGenerate }).Should().Be(CreditEntitlement.AudioGeneration);
    }

    [Fact]
    public async Task A_plan_without_the_feature_is_denied_by_the_gate_for_a_hosted_step_and_never_for_a_local_one()
    {
        Mock<IOrganizationEntitlements> trial = new();
        trial.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(new OrganizationEntitlementSet { Tier = "trial", VideoEnhanceAllowed = false }));

        Harness hosted = new(this) { SourceSeconds = 12.2, GateEntitlements = trial.Object };
        Run denied = await hosted.RunAsync(HostedUpscale());
        denied.StepResult!.Status.Should().Be(StepStatus.Failed);
        hosted.Uploaded.Should().BeEmpty();
        denied.Balance.BalanceMillis.Should().Be(Granted);

        Harness local = new(this) { SourceSeconds = 12.2, GateEntitlements = trial.Object };
        Run free = await local.RunAsync(LocalConfig());
        free.StepResult!.Status.Should().Be(StepStatus.Completed, free.StepResult.Output);
    }

    // ------------------------------------------------------------------ harness

    private sealed record Run(WorkflowExecution Execution, WorkflowStepResult? StepResult, IReadOnlyList<CreditLedgerEntry> Ledger, CreditLedgerResult Balance);

    private sealed class Harness(VideoEnhanceBillingLedgerTests t)
    {
        public double SourceSeconds { get; init; } = 12.2;
        public bool BillingEnabled { get; init; } = true;
        public bool CancelDuringPoll { get; init; }
        public long Grant { get; init; } = Granted;
        public Exception? RemuxThrows { get; init; }
        public IOrganizationEntitlements? GateEntitlements { get; init; }

        public List<string> Uploaded { get; } = [];
        public List<string> Deleted { get; } = [];
        public List<ExternalGenerationJob> Jobs { get; private set; } = [];
        public int SubmitCalls { get; private set; }
        public long? ReservedWhenUploaded { get; private set; }

        public async Task<Run> RunAsync(string configJson)
        {
            LedgerFactory ledger = new(t._ledgerOptions);
            await using (WorkflowEngineDbContext g = ledger.CreateDbContext())
            {
                (await new CreditLedger(g).GrantAsync(t._org, CreditGrantSource.Plan, Grant, null, Guid.NewGuid().ToString("N"))).Ok.Should().BeTrue();
            }

            CreditGate gate = new(ledger, Options.Create(new CreditGateOptions { Enabled = BillingEnabled }), NullLogger<CreditGate>.Instance, GateEntitlements);
            PerUnitPrice prices = new();
            bool interpolate = configJson.Contains("\"Interpolate\"", StringComparison.Ordinal);
            var row = new ResolvedAudioGenerationProvider(Guid.NewGuid(), "row", InferenceProviderKind.FalAi,
                interpolate ? InferenceProviderCapability.FrameInterpolation : InferenceProviderCapability.VideoUpscale, "",
                interpolate ? VideoEnhanceModelCatalog.RifeId : VideoEnhanceModelCatalog.TopazId, "test", 60, AccountId: Guid.NewGuid());
            Mock<IInferenceProviderResolver> resolver = new();
            resolver.Setup(r => r.ResolveVideoUpscaleAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(row);
            resolver.Setup(r => r.ResolveFrameInterpolationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(row);

            ExecutionCancellationRegistry registry = new();
            DbContextOptions<WorkflowEngineDbContext> engineOptions = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            using var db = new WorkflowEngineDbContext(engineOptions);

            AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "A", AgentType = AgentType.VideoTransform, SystemPrompt = "" };
            WorkflowStep step = new()
            {
                Id = Guid.NewGuid(),
                StepOrder = 1,
                StepType = StepType.VideoEnhance,
                AgentDefinitionId = agent.Id,
                AgentDefinition = agent,
                VideoEnhanceConfigJson = configJson,
                CacheMode = StepCacheMode.Never
            };
            WorkflowDefinition definition = new() { Id = Guid.NewGuid(), Name = "t", ProjectId = t._projectId, Steps = [step] };
            WorkflowExecution execution = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = definition.Id,
                ProjectId = t._projectId,
                OrganizationId = t._org,
                Status = ExecutionStatus.Queued,
                WorkflowDefinition = definition
            };
            db.Projects.Add(new Project { Id = t._projectId, Name = "p", OwnerId = Guid.NewGuid(), OrganizationId = t._org, VideoEnhanceConsentAt = DateTime.UtcNow });
            db.AgentDefinitions.Add(agent);
            db.WorkflowDefinitions.Add(definition);
            db.WorkflowExecutions.Add(execution);
            await db.SaveChangesAsync();

            // ---- the executor's collaborators (fakes for the store, the session, the engines and the vendor) ----
            string sourceKey = $"projects/{t._projectId}/userFiles/take.mp4";
            Mock<IProjectFileWorkspace> files = new();
            files.Setup(w => w.ListFilesAsync(t._projectId, It.IsAny<CancellationToken>()))
                .ReturnsAsync([new ProjectWorkspaceFile(t._fileId, t._projectId, "take", null, "userFiles", sourceKey, "video/mp4", 1024, DateTime.UtcNow, null)]);
            files.Setup(w => w.RegisterStoredObjectAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                    It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid p, string key, string name, string type, long size, SummaryStatus s, FileIndexingStatus i, CancellationToken c, string cat) =>
                    new ProjectWorkspaceFile(Guid.NewGuid(), p, name, null, cat, key, type, size, DateTime.UtcNow, null));
            files.Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid p, string local, string name, string type, CancellationToken c, string cat) =>
                {
                    string key = $"projects/{p}/{cat}/{name}";
                    Uploaded.Add(key);
                    using WorkflowEngineDbContext l = ledger.CreateDbContext();
                    ReservedWhenUploaded = new CreditLedger(l).GetBalanceAsync(t._org).GetAwaiter().GetResult().ReservedMillis;
                    return key;
                });
            files.Setup(w => w.DeleteArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<Guid, string, CancellationToken>((_, key, _) => Deleted.Add(key))
                .Returns(Task.CompletedTask);

            Mock<IPresignedUrlService> presigner = new();
            presigner.Setup(p => p.CreateGetAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>())).ReturnsAsync("https://signed.example/get");

            VideoEnhanceJobHandle handle = new("req-1", "https://queue.fal.run/m/requests/req-1/status", "https://queue.fal.run/m/requests/req-1");
            Task<VideoEnhanceJobHandle> Submit()
            {
                SubmitCalls++;
                return Task.FromResult(handle);
            }

            Task<VideoEnhanceResult> Poll(CancellationToken ct)
            {
                if (CancelDuringPoll)
                {
                    registry.TryCancel(execution.Id, "Stopped by user qa");
                    ct.ThrowIfCancellationRequested();
                }

                return Task.FromResult(new VideoEnhanceResult([1, 2, 3], "video/mp4", ".mp4", null, "m", null, "req-1"));
            }

            Mock<IVideoUpscaleClient> upscale = new();
            upscale.Setup(c => c.SubmitAsync(It.IsAny<VideoUpscaleRequest>(), It.IsAny<CancellationToken>())).Returns(() => Submit());
            upscale.Setup(c => c.ResumeAsync(It.IsAny<VideoEnhanceJobHandle>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((VideoEnhanceJobHandle _, string _, CancellationToken ct) => Poll(ct));
            Mock<IVideoUpscaleClientFactory> upscaleFactory = new();
            upscaleFactory.Setup(c => c.Get(It.IsAny<ResolvedAudioGenerationProvider>())).Returns(upscale.Object);
            Mock<IFrameInterpolationClient> interp = new();
            interp.Setup(c => c.SubmitAsync(It.IsAny<FrameInterpolationRequest>(), It.IsAny<CancellationToken>())).Returns(() => Submit());
            interp.Setup(c => c.ResumeAsync(It.IsAny<VideoEnhanceJobHandle>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((VideoEnhanceJobHandle _, string _, CancellationToken ct) => Poll(ct));
            Mock<IFrameInterpolationClientFactory> interpFactory = new();
            interpFactory.Setup(c => c.Get(It.IsAny<ResolvedAudioGenerationProvider>())).Returns(interp.Object);

            Mock<IOrganizationEntitlements> entitlements = new();
            entitlements.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(() => ValueTask.FromResult(new OrganizationEntitlementSet { Tier = "test", VideoEnhanceAllowed = true, MaxOutputHeight = 2160, StorageGb = OrganizationEntitlementSet.UnlimitedInt }));
            ServiceProvider services = new ServiceCollection()
                .AddSingleton(entitlements.Object)
                .AddSingleton(files.Object)
                .AddScoped(_ => new WorkflowEngineDbContext(engineOptions))
                .BuildServiceProvider();

            SessionWorkspace session = new() { SourceSeconds = SourceSeconds };
            UsageRecorder recorder = new(prices, new NullUsageSink(), NullLogger<UsageRecorder>.Instance);
            var executor = new VideoEnhanceStepExecutor(
                new Mock<IVideoToolRunner>(MockBehavior.Strict).Object,
                files.Object,
                services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new VideoEditingOptions()),
                Options.Create(new LocalEnhanceOptions()),
                new FakeEnhancer(session, SourceSeconds),
                new FakeRemux(session, SourceSeconds, RemuxThrows),
                resolver.Object,
                upscaleFactory.Object,
                interpFactory.Object,
                new VideoGenerationBudgetGate(ledger, Options.Create(new VideoGenerationOptions())),
                ledger,
                NullLogger<VideoEnhanceStepExecutor>.Instance,
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
            Jobs = await read.ExternalGenerationJobs.AsNoTracking().ToListAsync();
            CreditLedgerResult balance = await new CreditLedger(read).GetBalanceAsync(t._org);
            return new Run(after, stepResult, entries, balance);
        }
    }

    private sealed class PerUnitPrice : IModelPriceService
    {
        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default)
            => ValueTask.FromResult(new PriceQuote((long)(request.Quantity * MillisPerUnit), 0.01m, Guid.NewGuid()));

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

    private sealed class FakeEnhancer(SessionWorkspace session, double seconds) : ILocalVideoEnhancer
    {
        public Task<LocalEnhanceResult> EnhanceAsync(string sourcePath, string outputPath, LocalEnhanceRequest request, IProgress<double>? progress, CancellationToken ct)
        {
            session.Files[outputPath] = new byte[2048];
            return Task.FromResult(new LocalEnhanceResult(true, "denoise-light", 1920, 1080, 30, seconds, 2048, []));
        }
    }

    private sealed class FakeRemux(SessionWorkspace session, double seconds, Exception? fail) : IEnhancedAudioRemux
    {
        public Task<LocalEnhanceResult> RemuxAsync(string sourcePath, string picturePath, string outputPath, RemuxTarget? target, CancellationToken ct)
        {
            if (fail is not null)
                throw fail;
            session.Files[outputPath] = new byte[4096];
            return Task.FromResult(new LocalEnhanceResult(true, "remux+copy", target!.Width, target.Height, target.Fps, seconds, 4096, []));
        }
    }

    private sealed class SessionWorkspace : IMediaWorkspace
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public double SourceSeconds { get; init; }

        public string RootPath => "session:/";

        public Task StageAsync(string storageKey, string virtualPath, CancellationToken ct) => Task.CompletedTask;

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct) =>
            RunFfmpegAsync(argv, timeout, ct, null!);

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine) =>
            Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct)
        {
            string json = "{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":1920,\"height\":1080,\"r_frame_rate\":\"30/1\"},"
                + "{\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"sample_rate\":\"48000\",\"channels\":2}],"
                + $"\"format\":{{\"duration\":\"{SourceSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"}}}}";
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

        public Task<MediaWorkspaceStat> StatAsync(string virtualPath, CancellationToken ct) =>
            Task.FromResult(Files.TryGetValue(virtualPath, out byte[]? bytes) ? new MediaWorkspaceStat(true, bytes.Length) : new MediaWorkspaceStat(false, 0));

        public Task<string> PublishAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct) =>
            Task.FromResult(finalStorageKey);

        public Task<string> UploadArtifactAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct) =>
            Task.FromResult(finalStorageKey);
    }

    private sealed class StubFactory(SessionWorkspace session) : IMediaWorkspaceFactory
    {
        public Task<MediaWorkspaceOpenResult> OpenAsync(MediaWorkspaceRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(MediaWorkspaceOpenResult.Opened(new MediaWorkspaceLease(
                session, scratch: null, ComputeDecision.Local(ComputeDecisionReasons.LocalMode),
                job: null, request.ExecutionId, request.StepOrder, NullLogger.Instance)));
    }
}
