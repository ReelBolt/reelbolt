#nullable enable
using ReelBolt.WorkflowEngine.Services.Billing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// TimelineRender honours the plan's output cap (saas-billing-overhaul §A "TimelineRender never
/// applies the plan cap"): a shape preset above the cap is CLAMPED to the plan's short side —
/// aspect-preserving, before the preset is resolved, so region hashes and the encode agree — and the
/// clamp is recorded in the step output. Unlimited plans, a plan at or above the request, and a
/// failed entitlement lookup render exactly what was asked.
/// </summary>
public sealed class TimelineRenderPlanCapTests : IDisposable
{
    private static readonly Guid ProjectId = Guid.Parse("c4c4c4c4-0000-4000-8000-0000000000c4");
    private static readonly Guid OrgId = Guid.Parse("0a0a0a0a-0000-4000-8000-00000000000a");
    private static readonly Guid FileId = Guid.Parse("f1f1f1f1-0000-4000-8000-0000000000f1");

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "tr-plancap-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch)) Directory.Delete(_scratch, recursive: true);
    }

    // ── TimelineRenderPresets.CapToShortSide ─────────────────────────────────

    private static readonly EditTimelineSettings Hd = new(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

    [Fact]
    public void Clamping_a_preset_without_encode_settings_keeps_the_presets_own_crf_and_speed()
    {
        TimelineRenderStepConfig master = new(Guid.NewGuid(), Preset: TimelineRenderPreset.Master);
        EditTimelineSettings uhd = Hd with { Width = 3840, Height = 2160 };

        TimelineRenderStepConfig capped = TimelineRenderPresets.CapToShortSide(master, uhd, 720, out bool applied);

        applied.Should().BeTrue();
        ResolvedRenderTarget before = TimelineRenderPresets.Resolve(master, uhd);
        ResolvedRenderTarget after = TimelineRenderPresets.Resolve(capped, uhd);
        (after.Width, after.Height).Should().Be((1280, 720));
        after.Should().BeEquivalentTo(before, o => o.Excluding(t => t.Width).Excluding(t => t.Height));
    }

    [Fact]
    public void Clamping_keeps_an_explicit_encodes_other_settings()
    {
        TimelineRenderStepConfig config = new(Guid.NewGuid(), Preset: TimelineRenderPreset.Uhd4k16x9,
            Encode: new TimelineRenderEncodeSettings(Codec: TimelineRenderVideoCodec.H265, Crf: 23, Speed: TimelineRenderEncoderSpeed.Fast, AudioBitrateKbps: 256));

        TimelineRenderStepConfig capped = TimelineRenderPresets.CapToShortSide(config, Hd, 1080, out bool applied);

        applied.Should().BeTrue();
        capped.Encode.Should().Be(config.Encode! with { Width = 1920, Height = 1080 });
    }

    [Theory]
    [InlineData(TimelineRenderPreset.Uhd4k16x9, 2160)]
    [InlineData(TimelineRenderPreset.Hd1080p16x9, 1080)]
    [InlineData(TimelineRenderPreset.AudioOnly, 720)]
    [InlineData(TimelineRenderPreset.Uhd4k16x9, OrganizationEntitlementSet.UnlimitedInt)]
    public void A_config_that_fits_the_cap_is_returned_unchanged(TimelineRenderPreset preset, int cap)
    {
        TimelineRenderStepConfig config = new(Guid.NewGuid(), Preset: preset);

        TimelineRenderPresets.CapToShortSide(config, Hd, cap, out bool applied).Should().BeSameAs(config);
        applied.Should().BeFalse();
    }

    // ── TimelineRenderStepExecutor ───────────────────────────────────────────

    [Fact]
    public async Task Trial_Hd1080p_is_clamped_to_1280x720_and_the_clamp_is_recorded()
    {
        (StepExecutionResult result, RecordingRunner runner) = await RunAsync(TimelineRenderPreset.Hd1080p16x9, Plan(720));

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        using JsonDocument output = JsonDocument.Parse(result.Output);
        output.RootElement.GetProperty("width").GetInt32().Should().Be(1280);
        output.RootElement.GetProperty("height").GetInt32().Should().Be(720);
        output.RootElement.GetProperty("preset").GetString().Should().Be("Hd1080p16x9");
        JsonElement planCap = output.RootElement.GetProperty("planCap");
        planCap.GetProperty("maxOutputHeight").GetInt32().Should().Be(720);
        planCap.GetProperty("applied").GetBoolean().Should().BeTrue();

        string allArgs = string.Join('\n', runner.Invocations.SelectMany(a => a));
        allArgs.Should().Contain("1280").And.NotContain("1920", "no region or encode may be built at the unclamped size");
    }

    [Fact]
    public async Task A_timeline_seeded_from_a_run_renders_its_clip_provenance_resolves_in_the_engine()
    {
        // The API's PATCH validator resolves provenance step results; the engine's re-validation
        // must too, or every timeline seeded from a run fails with DOCUMENT_INVALID at render time.
        (StepExecutionResult result, _) = await RunAsync(
            TimelineRenderPreset.Hd1080p16x9, Serving(OrganizationEntitlementSet.Unlimited), withProvenance: true);

        result.Status.Should().Be(StepStatus.Completed, result.Output + " " + result.ErrorDetails);
    }

    [Fact]
    public async Task Pro_Uhd4k_renders_3840x2160_unchanged()
    {
        (StepExecutionResult result, RecordingRunner runner) = await RunAsync(TimelineRenderPreset.Uhd4k16x9, Plan(2160));

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        using JsonDocument output = JsonDocument.Parse(result.Output);
        output.RootElement.GetProperty("width").GetInt32().Should().Be(3840);
        output.RootElement.GetProperty("height").GetInt32().Should().Be(2160);
        JsonElement planCap = output.RootElement.GetProperty("planCap");
        planCap.GetProperty("maxOutputHeight").GetInt32().Should().Be(2160);
        planCap.GetProperty("applied").GetBoolean().Should().BeFalse();
        string.Join('\n', runner.Invocations.SelectMany(a => a)).Should().Contain("3840");
    }

    [Fact]
    public async Task An_unlimited_plan_renders_as_asked_and_records_no_plan_cap()
    {
        (StepExecutionResult result, _) = await RunAsync(TimelineRenderPreset.Uhd4k16x9, OrganizationEntitlementSet.Unlimited);

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        using JsonDocument output = JsonDocument.Parse(result.Output);
        output.RootElement.GetProperty("width").GetInt32().Should().Be(3840);
        output.RootElement.TryGetProperty("planCap", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_entitlement_lookup_fails_open_and_renders_as_asked()
    {
        Mock<IOrganizationEntitlements> throwing = new();
        throwing.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("billing down"));

        (StepExecutionResult result, _) = await RunAsync(TimelineRenderPreset.Hd1080p16x9, entitlements: throwing.Object);

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        using JsonDocument output = JsonDocument.Parse(result.Output);
        output.RootElement.GetProperty("width").GetInt32().Should().Be(1920);
        output.RootElement.TryGetProperty("planCap", out _).Should().BeFalse();
    }

    // ── inside an ordinary workflow run: the run's Auto rule (SEC-RR-02) ─────

    private static readonly EditTimelineSettings Uhd = Hd with { Width = 3840, Height = 2160 };

    [Fact]
    public async Task Pro_editor_export_Uhd4k_renders_3840x2160()
    {
        (StepExecutionResult result, _) = await RunAsync(TimelineRenderPreset.Uhd4k16x9, Serving(Plan(2160)), WorkflowDefinitionKind.TimelineRender);

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        using JsonDocument output = JsonDocument.Parse(result.Output);
        output.RootElement.GetProperty("width").GetInt32().Should().Be(3840);
        output.RootElement.GetProperty("height").GetInt32().Should().Be(2160);
    }

    [Fact]
    public async Task Pro_ordinary_workflow_on_Auto_renders_a_Master_4k_timeline_at_1080()
    {
        (StepExecutionResult result, RecordingRunner runner) = await RunAsync(
            TimelineRenderPreset.Master, Serving(Plan(2160)), WorkflowDefinitionKind.Standard, outputResolution: null, settings: Uhd);

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        using JsonDocument output = JsonDocument.Parse(result.Output);
        output.RootElement.GetProperty("width").GetInt32().Should().Be(1920);
        output.RootElement.GetProperty("height").GetInt32().Should().Be(1080);
        JsonElement planCap = output.RootElement.GetProperty("planCap");
        planCap.GetProperty("maxOutputHeight").GetInt32().Should().Be(1080);
        planCap.GetProperty("applied").GetBoolean().Should().BeTrue();
        string.Join('\n', runner.Invocations.SelectMany(a => a)).Should().NotContain("3840");
    }

    [Fact]
    public async Task Pro_ordinary_workflow_that_chose_2160p_renders_a_Master_4k_timeline_at_2160()
    {
        (StepExecutionResult result, _) = await RunAsync(
            TimelineRenderPreset.Master, Serving(Plan(2160)), WorkflowDefinitionKind.Standard, outputResolution: 2160, settings: Uhd);

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        using JsonDocument output = JsonDocument.Parse(result.Output);
        output.RootElement.GetProperty("width").GetInt32().Should().Be(3840);
        output.RootElement.GetProperty("height").GetInt32().Should().Be(2160);
    }

    [Fact]
    public async Task Pro_ordinary_workflow_on_Auto_clamps_an_explicit_Uhd4k_preset_to_1080()
    {
        (StepExecutionResult result, _) = await RunAsync(
            TimelineRenderPreset.Uhd4k16x9, Serving(Plan(2160)), WorkflowDefinitionKind.Standard);

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        using JsonDocument output = JsonDocument.Parse(result.Output);
        output.RootElement.GetProperty("height").GetInt32().Should().Be(1080);
    }

    [Fact]
    public async Task A_clip_whose_project_file_was_deleted_fails_ASSET_UNRESOLVED_with_the_plain_message()
    {
        (StepExecutionResult result, _) = await RunAsync(
            TimelineRenderPreset.Hd1080p16x9, Serving(Plan(2160)), deleteSourceFile: true);

        result.Status.Should().Be(StepStatus.Failed);
        string code = FailureMessages.EnsureResultCode(result.FailureCode, result.Output, result.ErrorDetails);
        code.Should().Be("ASSET_UNRESOLVED");
        FailureMessages.UserMessage(code, result.ErrorDetails).Should().Contain("no longer in the project");
    }

    [Fact]
    public async Task A_clip_naming_another_projects_file_answers_exactly_like_a_deleted_one()
    {
        (StepExecutionResult foreign, _) = await RunAsync(
            TimelineRenderPreset.Hd1080p16x9, Serving(Plan(2160)), sourceFileProjectId: Guid.NewGuid());
        (StepExecutionResult deleted, _) = await RunAsync(
            TimelineRenderPreset.Hd1080p16x9, Serving(Plan(2160)), deleteSourceFile: true);

        foreign.Status.Should().Be(StepStatus.Failed);
        foreign.ErrorDetails.Should().StartWith("ASSET_UNRESOLVED: ");
        foreign.ErrorDetails.Should().Be(deleted.ErrorDetails, "the answer must not confirm that the file exists elsewhere");
    }

    [Fact]
    public async Task A_deleted_file_in_a_document_that_is_also_structurally_wrong_stays_DOCUMENT_INVALID()
    {
        (StepExecutionResult result, _) = await RunAsync(
            TimelineRenderPreset.Hd1080p16x9, Serving(Plan(2160)), deleteSourceFile: true, clipLengthFrames: -1);

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().Contain("not found in this project", "the deleted file is reported too");
        FailureMessages.EnsureResultCode(result.FailureCode, result.Output, result.ErrorDetails).Should().Be("DOCUMENT_INVALID");
    }

    // ── fixture ──────────────────────────────────────────────────────────────

    private static OrganizationEntitlementSet Plan(int maxOutputHeight) => new() { Tier = "test", MaxOutputHeight = maxOutputHeight };

    private static IOrganizationEntitlements Serving(OrganizationEntitlementSet set)
    {
        Mock<IOrganizationEntitlements> entitlements = new();
        entitlements.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<OrganizationEntitlementSet>(set));
        return entitlements.Object;
    }

    private Task<(StepExecutionResult, RecordingRunner)> RunAsync(TimelineRenderPreset preset, OrganizationEntitlementSet set) =>
        RunAsync(preset, Serving(set));

    /// <summary>
    /// Runs one TimelineRender step. By default it belongs to the editor's hidden render workflow
    /// (<see cref="WorkflowDefinitionKind.TimelineRender"/>, what <c>POST …/edit-timelines/{id}/render</c>
    /// creates); <paramref name="kind"/> <see cref="WorkflowDefinitionKind.Standard"/> makes it a step
    /// of an ordinary workflow run that chose <paramref name="outputResolution"/>.
    /// </summary>
    private async Task<(StepExecutionResult, RecordingRunner)> RunAsync(
        TimelineRenderPreset preset,
        IOrganizationEntitlements entitlements,
        WorkflowDefinitionKind kind = WorkflowDefinitionKind.TimelineRender,
        int? outputResolution = null,
        EditTimelineSettings? settings = null,
        bool withProvenance = false,
        bool deleteSourceFile = false,
        Guid? sourceFileProjectId = null,
        long clipLengthFrames = 60)
    {
        WorkflowEngineDbContext db = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        AssetRef asset = new(AssetRefKind.ProjectFile, FileId, null);
        Guid timelineId = Guid.NewGuid();
        // A clip seeded from a run carries the deciding step's result as provenance (not as its asset).
        Guid decidingStepResultId = Guid.NewGuid();
        Provenance? provenance = withProvenance ? new Provenance(ProvenanceOrigin.Generated, decidingStepResultId) : null;
        MediaClip clip = new(Guid.NewGuid(), "clip", 0, clipLengthFrames, asset, 0, 1.0, true, provenance, null);
        Track video = new(Guid.NewGuid(), TrackKind.Video, "V1", null, 64, false, true, false, false, null, [clip]);
        EditTimeline document = new(timelineId, ProjectId, "doc", settings ?? Hd, [video], [], null, null);
        Guid workflowId = Guid.NewGuid();
        db.WorkflowDefinitions.Add(new WorkflowDefinition
        {
            Id = workflowId,
            ProjectId = ProjectId,
            Name = "w",
            Kind = kind,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        if (withProvenance)
        {
            Guid seedRunId = Guid.NewGuid();
            db.WorkflowExecutions.Add(new WorkflowExecution { Id = seedRunId, WorkflowDefinitionId = workflowId, ProjectId = ProjectId, OrganizationId = OrgId });
            db.WorkflowStepResults.Add(new WorkflowStepResult { Id = decidingStepResultId, WorkflowExecutionId = seedRunId, WorkflowStepId = Guid.NewGuid() });
        }

        if (!deleteSourceFile) db.ProjectFiles.Add(new ProjectFile
        {
            Id = FileId,
            ProjectId = sourceFileProjectId ?? ProjectId,
            OriginalFileName = "a.mp4",
            StorageKey = $"projects/{ProjectId:D}/files/a.mp4",
            MimeType = "video/mp4",
            SizeBytes = 100
        });
        db.EditTimelines.Add(new EditTimelineEntity
        {
            Id = timelineId,
            ProjectId = ProjectId,
            Name = "doc",
            DocumentJson = JsonSerializer.Serialize(document, EditTimelineJson.Options),
            Revision = 1
        });
        await db.SaveChangesAsync();

        Mock<IServiceProvider> provider = new();
        provider.Setup(p => p.GetService(typeof(WorkflowEngineDbContext))).Returns(db);
        provider.Setup(p => p.GetService(typeof(IOrganizationEntitlements))).Returns(entitlements);
        Mock<IServiceScope> scope = new();
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        Mock<IServiceScopeFactory> scopeFactory = new();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        Mock<IMediaProbe> probe = new();
        probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(10, 30, 1, 1920, 1080, "h264", "aac", 48000));

        Mock<IProjectFileWorkspace> workspace = new();
        workspace.Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync((Guid _, string _, string name, string _, CancellationToken _, string _) => $"projects/{ProjectId:D}/artifacts/{name}");

        RecordingRunner runner = new();
        TimelineRenderStepExecutor executor = new(
            runner,
            probe.Object,
            workspace.Object,
            Options.Create(new VideoEditingOptions { ScratchPath = _scratch }),
            scopeFactory.Object,
            NullLogger<TimelineRenderStepExecutor>.Instance);

        TimelineRenderStepConfig config = new(timelineId, Preset: preset, RegisterProjectFile: false);
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.TimelineRender,
            TimelineRenderConfigJson = JsonSerializer.Serialize(config, TimelineRenderStepExecutor.ConfigJsonOptions)
        };
        StepExecutionContext context = new()
        {
            Execution = new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflowId,
                ProjectId = ProjectId,
                OrganizationId = OrgId,
                OutputResolution = outputResolution
            },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };

        StepExecutionResult result = await executor.ExecuteAsync(context);
        return (result, runner);
    }

    /// <summary>Succeeds every call, records every argv, and "writes" the output file ffmpeg would have.</summary>
    private sealed class RecordingRunner : IVideoToolRunner
    {
        public List<IReadOnlyList<string>> Invocations { get; } = [];

        private Task<VideoToolResult> Record(IReadOnlyList<string> args)
        {
            Invocations.Add(args.ToList());
            string output = args[^1];
            if (output != "-" && output.Contains(Path.DirectorySeparatorChar))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllBytes(output, [0]);
            }
            return Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));
        }

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) => Record(args);
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine) => Record(args);
        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) => Record(args);
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority) => Record(args);
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine, VideoToolPriority priority) => Record(args);
        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority) => Record(args);
        public Task<VideoToolResult> RunFfmpegAsync(IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) => Record(args);
        public Task<VideoToolResult> RunFfmpegAsync(IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine) => Record(args);
    }
}
