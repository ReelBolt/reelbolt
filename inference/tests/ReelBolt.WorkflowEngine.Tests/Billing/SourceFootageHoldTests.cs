#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Briefs;
using ReelBolt.WorkflowEngine.Services.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// Initiative large-source-footage: a VideoAnalyze step whose source is a project file with a stored probe (or a size)
/// holds and shows the real analyse seconds at the source's tier, plus the ASR seconds when it transcribes. Holds only
/// grow, an unknown source is today's figure, a resolver failure never fails a run, and the API and the engine agree.
/// </summary>
public sealed class SourceFootageHoldTests(ITestOutputHelper output)
{
    private static readonly Guid Org = Guid.Parse("0b0b0b0b-0000-4000-8000-00000000000b");
    private static readonly Guid ProjectId = Guid.Parse("c1c1c1c1-0000-4000-8000-0000000000c1");
    private static readonly Guid DefinitionId = Guid.Parse("d1d1d1d1-0000-4000-8000-0000000000d1");
    private static readonly Guid FileId = Guid.Parse("f1f1f1f1-0000-4000-8000-0000000000f1");
    private const string Key = "projects/p/userFiles/long.mp4";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static WorkflowStep Analyze(VideoSourceRef source, VideoTranscriptionMode transcription = VideoTranscriptionMode.Optional) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowDefinitionId = DefinitionId,
        StepOrder = 1,
        StepType = StepType.VideoAnalyze,
        VideoAnalyzeConfigJson = JsonSerializer.Serialize(new VideoAnalyzeStepConfig(Version: 1, Source: source, Transcription: transcription), Json)
    };

    private static WorkflowStep OfFile(VideoTranscriptionMode t = VideoTranscriptionMode.Optional) =>
        Analyze(new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: FileId), t);

    private static IReadOnlyDictionary<int, IReadOnlyList<SourceFootage>> Footage(double seconds, int w, int h, bool audio = true) =>
        new Dictionary<int, IReadOnlyList<SourceFootage>> { [1] = [new SourceFootage(seconds, w, h, audio)] };

    private static long Hold(WorkflowStep step, IReadOnlyDictionary<int, IReadOnlyList<SourceFootage>>? footage) =>
        RunCreditHolds.Plan([step], new StepCreditEstimator([]), null, new Dictionary<Guid, AgentType>(), SeedProfilePricer.Instance, null, footage)
            .HoldsMillis[0];

    [Fact]
    public void A_sixteen_minute_4k_source_holds_its_real_analyse_cost_and_the_transcription_when_on()
    {
        long today = Hold(OfFile(), null);
        long withAsr = Hold(OfFile(), Footage(960, 3840, 2160));
        long noAsr = Hold(OfFile(VideoTranscriptionMode.Off), Footage(960, 3840, 2160));
        long todayNoAsr = Hold(OfFile(VideoTranscriptionMode.Off), null);

        output.WriteLine($"16-min 4K, transcription on : before {today / 1000.0:0.###} credits, after {withAsr / 1000.0:0.###}");
        output.WriteLine($"16-min 4K, transcription off: before {todayNoAsr / 1000.0:0.###} credits, after {noAsr / 1000.0:0.###}");

        noAsr.Should().BeGreaterThan(todayNoAsr * 2, "960 s at the UHD tier is about 28x the 138.7 s 1080p profile's analyse part");
        withAsr.Should().BeGreaterThan(noAsr, "the audio seconds are priced when the step transcribes");
        withAsr.Should().BeGreaterThan(today);
    }

    [Fact]
    public void The_hold_follows_the_tier_and_the_seconds()
    {
        long uhd = Hold(OfFile(VideoTranscriptionMode.Off), Footage(960, 3840, 2160));
        long hd = Hold(OfFile(VideoTranscriptionMode.Off), Footage(960, 1920, 1080));
        long longer = Hold(OfFile(VideoTranscriptionMode.Off), Footage(1920, 1920, 1080));
        uhd.Should().BeGreaterThan(hd);
        longer.Should().BeGreaterThan(hd);
    }

    [Fact]
    public void A_source_without_audio_adds_no_transcription()
    {
        // Optional vs Off differ by the model-token part of the cold row (not by source footage); no audio adds no ASR on top.
        long tokenPart = Hold(OfFile(), null) - Hold(OfFile(VideoTranscriptionMode.Off), null);
        Hold(OfFile(), Footage(960, 3840, 2160, audio: false)).Should().Be(Hold(OfFile(VideoTranscriptionMode.Off), Footage(960, 3840, 2160)) + tokenPart);
    }

    [Fact]
    public async Task A_file_of_another_organization_is_unknown_and_adds_nothing()
    {
        await using InferenceApiDbContext db = NewDb();
        await SeedAsync(db);
        var resolved = await SourceFootageCredits.ResolveAsync(
            db.ProjectFiles, db.MediaDerivatives, db.WorkflowDefinitions, [OfFile()], Guid.NewGuid(), CancellationToken.None);
        resolved.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_of_another_project_in_the_same_organization_is_unknown_and_adds_nothing()
    {
        await using InferenceApiDbContext db = NewDb();
        await SeedAsync(db);
        Guid otherProject = Guid.NewGuid();
        Guid otherDefinition = Guid.NewGuid();
        db.AddRange(
            new Project { Id = otherProject, Name = "other", OrganizationId = Org },
            new WorkflowDefinition { Id = otherDefinition, ProjectId = otherProject, Name = "other" });
        await db.SaveChangesAsync();
        WorkflowStep step = OfFile();
        step.WorkflowDefinitionId = otherDefinition;

        var resolved = await SourceFootageCredits.ResolveAsync(
            db.ProjectFiles, db.MediaDerivatives, db.WorkflowDefinitions, [step], Org, CancellationToken.None);
        resolved.Should().BeEmpty();
    }

    [Fact]
    public async Task No_organization_or_an_unknown_workflow_reads_no_footage()
    {
        await using InferenceApiDbContext db = NewDb();
        await SeedAsync(db);
        (await SourceFootageCredits.ResolveAsync(
            db.ProjectFiles, db.MediaDerivatives, db.WorkflowDefinitions, [OfFile()], null, CancellationToken.None)).Should().BeEmpty();

        WorkflowStep unsaved = OfFile();
        unsaved.WorkflowDefinitionId = Guid.NewGuid();
        (await SourceFootageCredits.ResolveAsync(
            db.ProjectFiles, db.MediaDerivatives, db.WorkflowDefinitions, [unsaved], Org, CancellationToken.None)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(5, 1280, 720)]
    [InlineData(30, 1920, 1080)]
    [InlineData(100, 1920, 1080)]
    public void A_small_source_never_holds_less_than_today_and_transcription_off_is_unchanged(double seconds, int w, int h)
    {
        WorkflowStep step = OfFile(VideoTranscriptionMode.Off);
        Hold(step, Footage(seconds, w, h)).Should().Be(Hold(step, null), "analyse is already inside the 138.7 s profile; nothing is subtracted");
        Hold(OfFile(), Footage(seconds, w, h)).Should().BeGreaterThanOrEqualTo(Hold(OfFile(), null));
    }

    [Fact]
    public void An_unknown_source_holds_todays_figure()
    {
        WorkflowStep step = Analyze(new VideoSourceRef(VideoSourceKind.PreviousStepOutput));
        Hold(step, null).Should().Be(Hold(step, new Dictionary<int, IReadOnlyList<SourceFootage>>()));
    }

    [Fact]
    public void The_author_trim_window_is_what_is_priced()
    {
        WorkflowStep trimmed = Analyze(new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: FileId, InSec: 0, OutSec: 60), VideoTranscriptionMode.Off);
        Hold(trimmed, Footage(960, 3840, 2160)).Should().BeLessThan(Hold(OfFile(VideoTranscriptionMode.Off), Footage(960, 3840, 2160)));
    }

    [Fact]
    public async Task The_resolver_reads_a_stored_probe_then_a_size_and_a_failure_is_empty()
    {
        await using InferenceApiDbContext db = NewDb();
        await SeedAsync(db);
        var resolved = await SourceFootageCredits.ResolveAsync(db.ProjectFiles, db.MediaDerivatives, db.WorkflowDefinitions, [OfFile()], Org, CancellationToken.None);
        resolved[1].Should().ContainSingle().Which.Should().Be(new SourceFootage(960, 3840, 2160, true));

        db.MediaDerivatives.RemoveRange(db.MediaDerivatives);
        await db.SaveChangesAsync();
        var sizeOnly = await SourceFootageCredits.ResolveAsync(db.ProjectFiles, db.MediaDerivatives, db.WorkflowDefinitions, [OfFile()], Org, CancellationToken.None);
        sizeOnly[1].Single().DurationSec.Should().Be(Math.Ceiling(1_920_000_000d / SourceFootageCredits.AssumedBytesPerSecond));

        var failed = await SourceFootageCredits.ResolveAsync(
            new Mock<IQueryable<ProjectFile>>().Object, db.MediaDerivatives, db.WorkflowDefinitions, [OfFile()], Org, CancellationToken.None);
        failed.Should().BeEmpty("a resolver that cannot read leaves every step at today's hold");
    }

    [Theory]
    [InlineData(VideoTranscriptionMode.Optional)]
    [InlineData(VideoTranscriptionMode.Off)]
    public async Task The_api_estimate_and_the_engine_reservation_agree_and_grow(VideoTranscriptionMode transcription)
    {
        WorkflowStep step = OfFile(transcription);
        WorkflowStep[] steps = [step];
        InferenceApiDbContext api = NewDb();
        await SeedAsync(api);
        DbContextOptions<WorkflowEngineDbContext> engineOptions = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using (WorkflowEngineDbContext engineDb = new(engineOptions))
        {
            await SeedAsync(engineDb);
        }

        WorkflowCostEstimate estimate = await new WorkflowCostEstimator(
            new Scopes(api), Options.Create(new BillingOptions { Enabled = true }),
            Options.Create(new StepCreditEstimateOptions()), NullLogger<WorkflowCostEstimator>.Instance)
            .EstimateAsync(Org, steps, null, CancellationToken.None);
        WorkflowExecution execution = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = Org,
            WorkflowDefinition = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Run", Steps = [.. steps] }
        };
        long engine = await new WorkflowExecutorService(
            scopeFactory: Mock.Of<IServiceScopeFactory>(),
            eventPublisher: Mock.Of<IWorkflowEventPublisher>(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: [],
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry(),
            creditOptions: Options.Create(new StepCreditOptions()),
            creditHoldEstimator: new UsageHistoryCreditHoldEstimator(
                new EngineFactory(engineOptions), Options.Create(new StepCreditEstimateOptions()), NullLogger<UsageHistoryCreditHoldEstimator>.Instance))
            .ResolveStepReserveMillisAsync(execution, step, CancellationToken.None, steps);

        long expected = Hold(step, Footage(960, 3840, 2160));
        engine.Should().Be(expected);
        estimate.RequiredMillis.Should().Be(expected);
        estimate.Steps.Single().HoldMillis.Should().Be(expected);
        estimate.Steps.Single().HighMillis.Should().BeGreaterThanOrEqualTo(Hold(step, null), "the shown band follows the hold");
        expected.Should().BeGreaterThan(Hold(step, null));
    }

    private static WorkflowStep OpenSlot(VideoTranscriptionMode t = VideoTranscriptionMode.Optional, double? inSec = null, double? outSec = null) =>
        Analyze(new VideoSourceRef(VideoSourceKind.ProjectFile, InSec: inSec, OutSec: outSec), t);

    private static WorkflowStep BriefSource() =>
        Analyze(new VideoSourceRef(VideoSourceKind.BriefFiles, Role: RunBriefFileRole.Video));

    private static RunBriefSourceFiles[] BriefVideo(params Guid[] ids) => [new RunBriefSourceFiles(RunBriefFileRole.Video, ids)];

    [Fact]
    public async Task An_open_slot_and_a_BriefFiles_source_resolve_to_the_brief_files_and_unknown_adds_nothing()
    {
        await using InferenceApiDbContext db = NewDb();
        await SeedAsync(db);

        using (SourceFootageBrief.Enter(BriefVideo(FileId)))
        {
            foreach (WorkflowStep step in new[] { OpenSlot(), BriefSource() })
            {
                var resolved = await SourceFootageCredits.ResolveAsync(
                    db.ProjectFiles, db.MediaDerivatives, db.WorkflowDefinitions, [step], Org, CancellationToken.None, SourceFootageBrief.Current);
                resolved[1].Should().ContainSingle().Which.DurationSec.Should().Be(960);
            }

            // the brief names a file that is not in the project / organization: unknown
            using (SourceFootageBrief.Enter(BriefVideo(Guid.NewGuid())))
            {
                (await SourceFootageCredits.ResolveAsync(
                    db.ProjectFiles, db.MediaDerivatives, db.WorkflowDefinitions, [OpenSlot()], Org, CancellationToken.None, SourceFootageBrief.Current))
                    .Should().BeEmpty();
            }

            (await SourceFootageCredits.ResolveAsync(
                db.ProjectFiles, db.MediaDerivatives, db.WorkflowDefinitions, [OpenSlot()], Guid.NewGuid(), CancellationToken.None, SourceFootageBrief.Current))
                .Should().BeEmpty("another organization's brief files reveal nothing");
        }

        // no brief: the open slot stays unknown, today's hold
        (await SourceFootageCredits.ResolveAsync(
            db.ProjectFiles, db.MediaDerivatives, db.WorkflowDefinitions, [OpenSlot()], Org, CancellationToken.None))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task The_trim_window_of_an_open_slot_applies_once_to_the_brief_files()
    {
        await using InferenceApiDbContext db = NewDb();
        await SeedAsync(db);
        using IDisposable brief = SourceFootageBrief.Enter(BriefVideo(FileId));
        WorkflowStep step = OpenSlot(inSec: 100, outSec: 400);
        var resolved = await SourceFootageCredits.ResolveAsync(
            db.ProjectFiles, db.MediaDerivatives, db.WorkflowDefinitions, [step], Org, CancellationToken.None, SourceFootageBrief.Current);
        SourceFootage footage = resolved[1].Single();
        footage.DurationSec.Should().Be(300);
        long once = Hold(step, new Dictionary<int, IReadOnlyList<SourceFootage>> { [1] = [footage] });
        long expected = Hold(OpenSlot(), Footage(300, 3840, 2160));
        once.Should().Be(expected, "the add-on must not trim an already trimmed brief file again");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_api_estimate_and_the_engine_reservation_agree_for_a_brief_sourced_run(bool briefFilesSource)
    {
        WorkflowStep step = briefFilesSource ? BriefSource() : OpenSlot();
        WorkflowStep[] steps = [step];
        InferenceApiDbContext api = NewDb();
        await SeedAsync(api);
        DbContextOptions<WorkflowEngineDbContext> engineOptions = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using (WorkflowEngineDbContext engineDb = new(engineOptions))
        {
            await SeedAsync(engineDb);
        }

        WorkflowCostEstimate estimate;
        using (SourceFootageBrief.Enter(BriefVideo(FileId)))
        {
            estimate = await new WorkflowCostEstimator(
                new Scopes(api), Options.Create(new BillingOptions { Enabled = true }),
                Options.Create(new StepCreditEstimateOptions()), NullLogger<WorkflowCostEstimator>.Instance)
                .EstimateAsync(Org, steps, null, CancellationToken.None);
        }

        WorkflowExecution execution = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = Org,
            WorkflowDefinition = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Run", Steps = [.. steps] }
        };
        long engine;
        using (RunBriefAmbient.Enter(new RunBriefState(new StoredRunBrief(SourceFiles: BriefVideo(FileId)))))
        {
            engine = await new WorkflowExecutorService(
                scopeFactory: Mock.Of<IServiceScopeFactory>(),
                eventPublisher: Mock.Of<IWorkflowEventPublisher>(),
                logger: NullLogger<WorkflowExecutorService>.Instance,
                executors: [],
                rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
                hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
                cancellationRegistry: new ExecutionCancellationRegistry(),
                creditOptions: Options.Create(new StepCreditOptions()),
                creditHoldEstimator: new UsageHistoryCreditHoldEstimator(
                    new EngineFactory(engineOptions), Options.Create(new StepCreditEstimateOptions()), NullLogger<UsageHistoryCreditHoldEstimator>.Instance))
                .ResolveStepReserveMillisAsync(execution, step, CancellationToken.None, steps);
        }

        long expected = Hold(step, Footage(960, 3840, 2160));
        expected.Should().BeGreaterThan(Hold(step, null), "a brief-sourced open slot is no longer the cold hold");
        engine.Should().Be(expected);
        estimate.RequiredMillis.Should().Be(expected);
    }

    [Fact]
    public async Task A_known_file_still_counts_when_another_source_of_the_step_is_unknown_and_api_and_engine_agree()
    {
        var stepOutput = new VideoSourceRef(VideoSourceKind.StepOutput, StepOrder: 0);
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = DefinitionId,
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(new VideoAnalyzeStepConfig(
                Version: 1,
                Source: new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: FileId),
                Sources: [new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: FileId), stepOutput, new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: Guid.NewGuid())]), Json)
        };
        WorkflowStep[] steps = [step];

        InferenceApiDbContext api = NewDb();
        await SeedAsync(api);
        DbContextOptions<WorkflowEngineDbContext> engineOptions = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using (WorkflowEngineDbContext engineDb = new(engineOptions))
        {
            await SeedAsync(engineDb);
        }

        var resolved = await SourceFootageCredits.ResolveAsync(api.ProjectFiles, api.MediaDerivatives, api.WorkflowDefinitions, steps, Org, CancellationToken.None);
        resolved[1].Should().ContainSingle("the foreign / missing id and the step output add 0; the known file still counts");

        WorkflowCostEstimate estimate = await new WorkflowCostEstimator(
            new Scopes(api), Options.Create(new BillingOptions { Enabled = true }),
            Options.Create(new StepCreditEstimateOptions()), NullLogger<WorkflowCostEstimator>.Instance)
            .EstimateAsync(Org, steps, null, CancellationToken.None);
        WorkflowExecution execution = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = Org,
            WorkflowDefinition = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Run", Steps = [.. steps] }
        };
        long engine = await new WorkflowExecutorService(
            scopeFactory: Mock.Of<IServiceScopeFactory>(),
            eventPublisher: Mock.Of<IWorkflowEventPublisher>(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: [],
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry(),
            creditOptions: Options.Create(new StepCreditOptions()),
            creditHoldEstimator: new UsageHistoryCreditHoldEstimator(
                new EngineFactory(engineOptions), Options.Create(new StepCreditEstimateOptions()), NullLogger<UsageHistoryCreditHoldEstimator>.Instance))
            .ResolveStepReserveMillisAsync(execution, step, CancellationToken.None, steps);

        engine.Should().BeGreaterThan(Hold(step, null), "the known 4K file is held for what it is");
        estimate.RequiredMillis.Should().Be(engine);
    }

    private static async Task SeedAsync(DbContext db)
    {
        db.AddRange(
            new Project { Id = ProjectId, Name = "p", OrganizationId = Org },
            new WorkflowDefinition { Id = DefinitionId, ProjectId = ProjectId, Name = "w" },
            new ProjectFile { Id = FileId, ProjectId = ProjectId, StorageKey = Key, SizeBytes = 1_920_000_000, OriginalFileName = "long.mp4" },
            new MediaDerivative
            {
                Id = Guid.NewGuid(),
                ProjectId = ProjectId,
                SourceStorageKey = Key,
                SourceProbeJson = new MediaSourceProbe(960, 30, 1, 3840, 2160, "h264", "aac", "mov", 2, 48000, null, true).ToJson()
            });
        await db.SaveChangesAsync();
    }

    private static InferenceApiDbContext NewDb() => new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class EngineFactory(DbContextOptions<WorkflowEngineDbContext> options) : IDbContextFactory<WorkflowEngineDbContext>
    {
        public WorkflowEngineDbContext CreateDbContext() => new(options);
    }

    private sealed class Scopes(InferenceApiDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(db);

        private sealed class Scope(InferenceApiDbContext db) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new Provider(db);

            public void Dispose()
            {
            }
        }

        private sealed class Provider(InferenceApiDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType) => serviceType == typeof(InferenceApiDbContext) ? db : null;
        }
    }
}
