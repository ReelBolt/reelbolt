using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The VideoAnalyze port: the executor addresses every media file through
/// <see cref="IMediaWorkspace"/>, so its extraction genuinely runs wherever the workspace is.
/// <para>
/// Three properties have to hold at once, and each is pinned here:
/// </para>
/// <list type="number">
/// <item>
/// <b>A session that is not on this host runs the extraction.</b>
/// <see cref="SessionWorkspace"/> answers <see cref="IMediaWorkspace.MaterializeAsync"/> the way a
/// remote session does — the virtual path unchanged — and holds its files in memory, so nothing in
/// the run below can be satisfied by a path on this machine. If any call site still built a scratch
/// path by hand, the run would fail rather than quietly succeed locally.
/// </item>
/// <item>
/// <b>A local run is unchanged.</b> Both arms are driven with the same fakes and the same ids, and
/// the two envelopes must come out byte-identical — which is the bar D9b held VideoCompile to. The
/// only difference the two runs are allowed to have is WHO resolved the path: an absolute scratch
/// path for the local arm, a session-relative name for the session arm.
/// </item>
/// <item>
/// <b>Eligibility is the configuration's, not a hardcoded false.</b> The request the selector sees
/// is asserted directly, because that one boolean is what decides whether a customer's footage is
/// analysed on their machine.
/// </item>
/// </list>
/// </summary>
public class VideoAnalyzeWorkspacePortTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FileId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ExecutionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid StepId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid MusicFileId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private const string SourceKey = "projects/11111111-1111-1111-1111-111111111111/userFiles/take.mp4";
    private const string MusicKey = "projects/11111111-1111-1111-1111-111111111111/userFiles/click.mp3";

    // =======================================================================
    // 1 + 2: both arms, one analysis
    // =======================================================================

    [Fact]
    public async Task Both_arms_produce_the_same_analysis_and_differ_only_in_who_resolves_the_path()
    {
        Arm local = await RunAsync(useSession: false);
        Arm session = await RunAsync(useSession: true);

        local.Result.Status.Should().Be(
            StepStatus.Completed, because: local.Result.ErrorDetails ?? "a local run must still succeed");
        session.Result.Status.Should().Be(
            StepStatus.Completed, because: session.Result.ErrorDetails ?? "a runner-backed run must succeed too");

        // (1) The two arms see one and the same analysis. Every id, time and caption is identical;
        // only the strings naming the media differ, and they differ exactly as the interface says
        // they must (see (2)).
        session.Result.Output.Should().Be(
            local.Result.Output,
            because: "who resolves the path must not change what the analysis says");

        // (2) ...and they are the SAME files, named the same way to the seams. That is the seam's
        // own contract and the reason this port needed no help from the callers: a media service is
        // handed a virtual path and NOTHING else, in both arms, and only the workspace it is handed
        // decides what that name resolves to.
        local.SeamPaths.Should().NotBeEmpty();
        session.SeamPaths.Should().Equal(local.SeamPaths);
        session.SeamPaths.Should().OnlyContain(p => !Path.IsPathRooted(p));
        session.SeamPaths.Should().OnlyContain(p => p.EndsWith("source.mp4", StringComparison.Ordinal));

        // ...and the two workspaces really do differ in where that name points. A local one resolves
        // it to the absolute scratch file this step has always decoded; a session that is not on this
        // host leaves it session-relative, because only the machine holding the bytes can resolve it.
        string scratchRoot = ScratchRoot();
        using VideoScratchSpace scratch = VideoScratchSpace.Create(
            Options.Create(new VideoEditingOptions { ScratchPath = scratchRoot }).Value,
            ExecutionId,
            StepId,
            NullLogger<VideoAnalyzeStepExecutor>.Instance);
        IMediaWorkspace localWorkspace = LocalMediaWorkspace.Create(
            scratch, new Mock<IVideoToolRunner>().Object);
        foreach (string virtualPath in local.SeamPaths)
        {
            string resolved = await localWorkspace.MaterializeAsync(virtualPath, CancellationToken.None);
            Path.IsPathRooted(resolved).Should().BeTrue();
            resolved.Should().StartWith(scratchRoot);
        }

        // The media really was addressed through the session: the source was staged into it and the
        // artifact was published from it, neither of which can happen through a host path.
        session.Session.Staged.Should().Contain("source.mp4");
        session.Session.Published.Should().Contain("analysis.json");
    }

    [Fact]
    public async Task A_step_that_asks_for_object_tracking_asks_the_selector_to_run_locally()
    {
        // Object tracking samples up to 600 MB of grayscale frames across the whole clip off this
        // disk, so it needs the source here. That is the ONE configuration still pinned local, and
        // it is pinned through D6's own RunnerEligible mechanism rather than by a fallback.
        Arm plain = await RunAsync(useSession: true);
        Arm tracking = await RunAsync(useSession: true, config => config with
        {
            TrackObjects = [new ObjectTrackTarget("the phone", AtSec: 1.0)]
        });

        plain.Request.Should().NotBeNull();
        plain.Request!.RunnerEligible.Should().BeTrue(
            because: "a step with nothing local-only about it must actually be eligible to run on a runner");
        plain.Request.JobType.Should().Be(RunnerJobType.VideoAnalyzeExtract);

        tracking.Request.Should().NotBeNull();
        tracking.Request!.RunnerEligible.Should().BeFalse(
            because: "a step asking for object tracking runs where the footage is readable, i.e. here");
    }

    // =======================================================================
    // 3: MusicBeatProbe
    // =======================================================================

    /// <summary>
    /// The probe through a workspace that is not on this host — the case D11 had to SKIP with a logged
    /// reason, because the measurement used to need a local scratch directory. It now runs wherever
    /// the session is.
    /// </summary>
    [Fact]
    public async Task MusicBeatProbe_measures_through_the_workspace_it_is_handed()
    {
        var session = new SessionWorkspace();
        var extractor = new Mock<IAudioExtractor>();
        extractor
            .Setup(a => a.ExtractWavAsync(
                It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<IMediaWorkspace, string, string, CancellationToken>(async (ws, _, outPath, ct) =>
                await ws.WriteBytesAsync(outPath, ClickTrackWav(), ct));

        ProjectWorkspaceFile file = MusicFile();

        MusicBeatAnalysis? measured = await MusicBeatProbe.MeasureAsync(
            session, extractor.Object, file, "beat-0", CancellationToken.None);

        measured.Should().NotBeNull("a real click track must measure through the session");
        measured!.Bpm.Should().BeApproximately(128, 1.0);

        // The two files the probe needs were produced and read inside the session, and nothing was
        // resolved against this host's filesystem to do it.
        session.Staged.Should().Contain("beat-0.mp3");
        session.Files.Should().ContainKey("beat-0.wav");

        // A track the session cannot produce is still a null measurement, never a throw: the probe's
        // contract is unchanged on the new arm too. A decoder that produces nothing is what an
        // unreadable music file amounts to here.
        var empty = new SessionWorkspace();
        MusicBeatAnalysis? missing = await MusicBeatProbe.MeasureAsync(
            empty, new Mock<IAudioExtractor>().Object, MusicFile(), "beat-1", CancellationToken.None);
        missing.Should().BeNull();
    }

    [Fact]
    public async Task MusicBeatProbe_reportsAReadOrDecodeFailureWithACause()
    {
        var extractor = new Mock<IAudioExtractor>();
        extractor
            .Setup(a => a.ExtractWavAsync(
                It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<IMediaWorkspace, string, string, CancellationToken>(async (ws, _, outPath, ct) =>
                await ws.WriteBytesAsync(outPath, [1, 2, 3], ct));

        // Audio whose decode produced an unreadable WAV is a decode failure: null plus a cause (never a silent loss).
        MusicBeatMeasurement none = await MusicBeatProbe.TryMeasureAsync(
            new SessionWorkspace(), extractor.Object, MusicFile(), "beat-2", logger: null, CancellationToken.None);
        none.Analysis.Should().BeNull();
        none.FailureCause.Should().Be("wav_unreadable");

        // A transport failure while reading: null plus a cause, which the warning carries with the stable code.
        var failing = new Mock<IMediaWorkspace>();
        failing.Setup(w => w.StageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("socket closed"));
        MusicBeatMeasurement lost = await MusicBeatProbe.TryMeasureAsync(
            failing.Object, extractor.Object, MusicFile(), "beat-3", logger: null, CancellationToken.None);
        lost.Analysis.Should().BeNull();
        lost.FailureCause.Should().Be("IOException");
        var warning = MusicBeatProbe.UnavailableWarning(lost.FailureCause!);
        warning["code"]!.GetValue<string>().Should().Be("beat_grid_unavailable");
        warning["cause"]!.GetValue<string>().Should().Be("IOException");
    }

    private static Mock<IAudioExtractor> ExtractorWriting(Func<byte[]> wav)
    {
        var extractor = new Mock<IAudioExtractor>();
        extractor
            .Setup(a => a.ExtractWavAsync(
                It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<IMediaWorkspace, string, string, CancellationToken>(async (ws, _, outPath, ct) =>
                await ws.WriteBytesAsync(outPath, wav(), ct));
        return extractor;
    }

    [Fact]
    public async Task A_decodable_beatless_track_is_no_failure_and_no_warning()
    {
        MusicBeatMeasurement m = await MusicBeatProbe.TryMeasureAsync(
            new SessionWorkspace(), ExtractorWriting(() => BeatSyncTests.Wav(new float[16000 * 20])).Object,
            MusicFile(), "beat-4", logger: null, CancellationToken.None);
        m.Analysis.Should().BeNull();
        m.FailureCause.Should().BeNull("decoded fine, no beat found, is not a failure");

        Arm arm = await RunAsync(
            useSession: true,
            configure: c => c with { OfferMusicTracks = true, AnalyzeMusicBeats = true, MusicBeatTrackProjectFileId = MusicFileId },
            audioExtractor: ExtractorWriting(() => BeatSyncTests.Wav(new float[16000 * 20])).Object,
            session: new SessionWorkspace());
        arm.Result.Status.Should().Be(StepStatus.Completed, because: arm.Result.ErrorDetails ?? arm.Result.Output);
        JsonNode envelope = JsonNode.Parse(arm.Result.Output)!;
        (envelope["warnings"] as JsonArray ?? []).Any(w => w!["code"]!.GetValue<string>() == "beat_grid_unavailable")
            .Should().BeFalse();
    }

    [Fact]
    public async Task A_lost_beat_grid_lands_in_the_analyze_step_warnings()
    {
        var extractor = new Mock<IAudioExtractor>();
        extractor
            .Setup(a => a.ExtractWavAsync(
                It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("socket closed"));

        Arm arm = await RunAsync(
            useSession: true,
            configure: c => c with { OfferMusicTracks = true, AnalyzeMusicBeats = true, MusicBeatTrackProjectFileId = MusicFileId },
            audioExtractor: extractor.Object,
            session: new SessionWorkspace());

        arm.Result.Status.Should().Be(StepStatus.Completed, because: arm.Result.ErrorDetails ?? arm.Result.Output);
        JsonArray warnings = JsonNode.Parse(arm.Result.Output)!["warnings"]!.AsArray();
        warnings.Select(w => w!.AsObject()).Should().Contain(w =>
            w["code"]!.GetValue<string>() == "beat_grid_unavailable" && w["cause"]!.GetValue<string>() == "IOException");
    }

    [Fact]
    public async Task A_session_run_still_produces_beat_facts_for_an_offered_track()
    {
        var session = new SessionWorkspace();
        var extractor = new Mock<IAudioExtractor>();
        extractor
            .Setup(a => a.ExtractWavAsync(
                It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<IMediaWorkspace, string, string, CancellationToken>(async (ws, _, outPath, ct) =>
                await ws.WriteBytesAsync(outPath, ClickTrackWav(), ct));

        Arm arm = await RunAsync(
            useSession: true,
            configure: c => c with
            {
                OfferMusicTracks = true,
                AnalyzeMusicBeats = true,
                MusicBeatTrackProjectFileId = MusicFileId
            },
            audioExtractor: extractor.Object,
            session: session);

        arm.Result.Status.Should().Be(StepStatus.Completed, because: arm.Result.ErrorDetails ?? arm.Result.Output);

        JsonNode envelope = JsonNode.Parse(arm.Result.Output)!;
        JsonNode? music = envelope["view"]!["music"];
        music.Should().NotBeNull("the named beat track must reach the editor");

        // view.music.beat is the ViewNode: a tempo word, a BPM and a bar length the editor plans run
        // lengths with. Until this WP, a remote session could not measure any of it — D11 skipped the
        // probe outright — so a non-null beat here is the fix.
        JsonNode beat = music!["beat"]!;
        beat["bpm"]!.GetValue<double>().Should().BeApproximately(128, 1.0);
        beat["tempo"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        beat["barSec"]!.GetValue<double>().Should().BeGreaterThan(0);

        // ...and the measured facts survive the round trip through the persisted artifact too.
        JsonNode musicTracks = envelope["view"]!["musicTracks"]!;
        musicTracks[0]!["beat"]!["bpm"]!.GetValue<double>().Should().BeApproximately(128, 1.0);
    }

    // =======================================================================
    // Large sources: the analysis copy and the deadline
    // =======================================================================

    /// <summary>The owner's clip shape: 4K, 4 minutes, so it is eligible for the analysis copy and is a heavy source.</summary>
    private static MediaProbeResult BigSource() => new(249, 60, 1, 3840, 2160, "hevc", "aac", 48000);

    private static bool IsCopy(IReadOnlyList<string> argv) => argv.Any(a => a.EndsWith(".mkv", StringComparison.Ordinal));

    private static string ErrorCode(StepExecutionResult r) =>
        JsonNode.Parse(r.Output)!["error"]!["code"]!.GetValue<string>();

    private static void AssertRefundedAndNotCharged(StepExecutionResult r)
    {
        string? code = FailureClassifier.CodeFromOutput(r.Output);
        code.Should().Be("SOURCE_TOO_DEMANDING");
        FailureClass cls = FailureClassifier.ForCode(code);
        cls.Should().Be(FailureClass.PlatformInternal);
        CreditGate.IsRefundable(cls).Should().BeTrue("the hold is released and nothing is charged");
    }

    [Fact]
    public async Task A_404_while_the_runner_stages_the_written_analysis_is_a_named_refundable_failure()
    {
        SessionWorkspace session = new() { ArtifactWrite404 = true };

        Arm arm = await RunAsync(useSession: true, session: session);

        arm.Result.Status.Should().Be(StepStatus.Failed);
        string? code = FailureClassifier.CodeFromOutput(arm.Result.Output);
        code.Should().Be("ANALYSIS_ARTIFACT_UNAVAILABLE");
        FailureClass cls = FailureClassifier.ForCode(code);
        cls.Should().Be(FailureClass.PlatformInternal);
        CreditGate.IsRefundable(cls).Should().BeTrue();
        arm.Result.ErrorDetails.Should().Be(AnalysisArtifactStaging.WriteMessage).And.NotContain("projects/");
    }

    [Fact]
    public async Task A_rejected_runner_output_keeps_its_code_class_and_shows_a_plain_sentence()
    {
        SessionWorkspace session = new()
        {
            UploadThrows = new RunnerOutputRejectedException(
                "RUNNER_OUTPUT_INVALID_DOCUMENT", FailureClass.Runner,
                "starts with 7b2276657273696f6e: projects/p/agentFiles/x.json ffprobe")
        };

        Arm arm = await RunAsync(useSession: true, session: session);

        arm.Result.Status.Should().Be(StepStatus.Failed);
        arm.Result.FailureCode.Should().Be("RUNNER_OUTPUT_INVALID_DOCUMENT");
        arm.Result.FailureClass.Should().Be(FailureClass.PlatformInternal);
        arm.Result.ErrorDetails.Should().Be(RunnerOutputRejectedException.CustomerMessage);
        foreach (string text in new[] { arm.Result.ErrorDetails!, arm.Result.Output })
            text.Should().NotContain("ffprobe").And.NotContain("7b2276").And.NotContain("projects/").And.NotContain("Unexpected error");
    }

    [Fact]
    public async Task An_analysis_copy_that_times_out_stops_the_step_with_the_demanding_source_code()
    {
        SessionWorkspace session = new() { FfmpegHandler = argv => IsCopy(argv) ? new VideoToolResult(-1, "", "", true) : new VideoToolResult(0, "", "", false) };

        Arm arm = await RunAsync(useSession: true, session: session, sourceProbe: BigSource());

        arm.Result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(arm.Result).Should().Be("SOURCE_TOO_DEMANDING");
        AssertRefundedAndNotCharged(arm.Result);
        arm.SeamPaths.Where(p => p.Contains("analysis", StringComparison.Ordinal)).Should().BeEmpty("no pass may fall back to the source");
        session.FfmpegCalls.Count(IsCopy).Should().Be(1, "a timed-out copy is not retried");
    }

    [Fact]
    public async Task An_analysis_copy_that_fails_for_another_reason_falls_back_to_the_source()
    {
        SessionWorkspace session = new() { FfmpegHandler = argv => IsCopy(argv) ? new VideoToolResult(1, "", "boom", false) : new VideoToolResult(0, "", "", false) };

        Arm arm = await RunAsync(useSession: true, session: session, sourceProbe: BigSource());

        arm.Result.Status.Should().Be(StepStatus.Completed, arm.Result.ErrorDetails);
        arm.SeamPaths.Should().OnlyContain(p => p.EndsWith("source.mp4", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_shot_pass_that_times_out_on_a_large_source_maps_to_the_code()
    {
        // heavy requires the scope; the exception the real detector throws carries the flag.
        Arm arm = await RunAsync(useSession: true, sourceProbe: BigSource(),
            shotsThrow: new VideoToolTimedOutException("shot detection timed out", heavySource: true));

        arm.Result.Status.Should().Be(StepStatus.Failed);
        AssertRefundedAndNotCharged(arm.Result);
    }

    [Fact]
    public async Task A_grid_pass_that_times_out_on_a_large_source_maps_to_the_code_instead_of_degrading()
    {
        Arm arm = await RunAsync(useSession: true, sourceProbe: BigSource(),
            gridThrow: new VideoToolTimedOutException("grid sampling timed out", heavySource: true));

        arm.Result.Status.Should().Be(StepStatus.Failed);
        AssertRefundedAndNotCharged(arm.Result);
    }

    [Fact]
    public async Task A_non_timeout_grid_failure_still_degrades_the_step_instead_of_failing_it()
    {
        Arm arm = await RunAsync(useSession: true, gridThrow: new InvalidOperationException("grid broke"));

        arm.Result.Status.Should().Be(StepStatus.Completed, arm.Result.ErrorDetails);
    }

    [Fact]
    public async Task A_timeout_on_a_small_source_is_not_a_demanding_source()
    {
        Arm arm = await RunAsync(useSession: true,
            gridThrow: new VideoToolTimedOutException("grid sampling timed out", heavySource: false));

        arm.Result.Status.Should().Be(StepStatus.Completed, "a short source keeps today's degrading behaviour");
    }

    // =======================================================================
    // The two arms
    // =======================================================================

    private sealed record Arm(
        StepExecutionResult Result,
        List<string> SeamPaths,
        MediaWorkspaceRequest? Request,
        SessionWorkspace Session);

    private static async Task<Arm> RunAsync(
        bool useSession,
        Func<VideoAnalyzeStepConfig, VideoAnalyzeStepConfig>? configure = null,
        IAudioExtractor? audioExtractor = null,
        SessionWorkspace? session = null,
        Func<Guid, CancellationToken, Task<IReadOnlyList<ProjectWorkspaceFile>>>? listFiles = null,
        MediaProbeResult? sourceProbe = null,
        Exception? shotsThrow = null,
        Exception? gridThrow = null)
    {
        session ??= new SessionWorkspace();
        var seamPaths = new List<string>();

        var projectFiles = new Mock<IProjectFileWorkspace>();
        projectFiles
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(() => listFiles is null
                ? Task.FromResult<IReadOnlyList<ProjectWorkspaceFile>>([SourceFile(), MusicFile()])
                : listFiles(ProjectId, CancellationToken.None));

        // The bare-artifact upload has no ProjectFile row to dedupe against, so the caller's own key
        // layout is preserved verbatim under the category prefix. Reproduce exactly that, so both arms
        // land on the same key and the two envelopes can be compared character for character.
        projectFiles
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Returns<Guid, string, string, string, CancellationToken, string>(
                (projectId, _, fileName, _, _, category) => Task.FromResult($"projects/{projectId:D}/{category}/{fileName}"));

        var probe = new Mock<IMediaProbe>();
        probe
            .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<IMediaWorkspace, string, CancellationToken>((_, path, _) =>
            {
                seamPaths.Add(path);
                return Task.FromResult(sourceProbe ?? new MediaProbeResult(4, 30, 1, 1920, 1080, "h264", "aac", 48000));
            });

        var silence = new Mock<ISilenceDetector>();
        silence
            .Setup(s => s.DetectAsync(
                It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .Returns<IMediaWorkspace, string, double, double, double?, CancellationToken>((_, path, _, _, _, _) =>
            {
                seamPaths.Add(path);
                return Task.FromResult<IReadOnlyList<(double StartSec, double EndSec)>>([(1.0, 1.5)]);
            });

        var shots = new Mock<IShotDetector>();
        shots
            .Setup(s => s.DetectShotsAsync(
                It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<CancellationToken>()))
            .Returns<IMediaWorkspace, string, double, double, CancellationToken>((_, path, _, _, _) =>
            {
                seamPaths.Add(path);
                if (shotsThrow is not null)
                    throw shotsThrow;
                return Task.FromResult<IReadOnlyList<(double StartSec, double EndSec)>>([(0.0, 2.0), (2.0, 4.0)]);
            });

        var grid = new Mock<IFrameGridSampler>();
        grid
            .Setup(g => g.SampleAsync(
                It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<IMediaWorkspace, string, double, int, int, double, int, CancellationToken>(
                (_, path, _, width, height, _, _, _) =>
                {
                    seamPaths.Add(path);
                    if (gridThrow is not null)
                        throw gridThrow;
                    const int FrameCount = 8;
                    return Task.FromResult(new FrameGridResult(
                        new byte[width * height * 3 * FrameCount], width, height, 2.0, FrameCount));
                });

        var factory = useSession ? new RecordingWorkspaceFactory(session) : null;

        var executor = new VideoAnalyzeStepExecutor(
            probe.Object,
            silence.Object,
            shots.Object,
            audioExtractor ?? new Mock<IAudioExtractor>().Object,
            grid.Object,
            new Mock<IKeyframeExtractor>().Object,
            new Mock<ISharpnessSampler>().Object,
            new Mock<IShotCaptioner>().Object,
            projectFiles.Object,
            new Mock<ITranscriptionClientFactory>().Object,
            new Mock<IInferenceProviderResolver>().Object,
            Options.Create(new VideoEditingOptions
            {
                ScratchPath = ScratchRoot(),
                MaxConcurrentJobs = 1,
                AnalyzeTimeoutSeconds = 30,
                CompileTimeoutSeconds = 60
            }),
            NullLogger<VideoAnalyzeStepExecutor>.Instance,
            videoToolRunner: new Mock<IVideoToolRunner>().Object,
            mediaWorkspaces: factory);

        VideoAnalyzeStepConfig config = configure is null
            ? DefaultConfig()
            : configure(DefaultConfig());

        var step = new WorkflowStep
        {
            Id = StepId,
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(
                config,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } })
        };

        StepExecutionResult result = await executor.ExecuteAsync(new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = ExecutionId, ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "video-analyze-workspace-port",
            CancellationToken = CancellationToken.None
        });

        return new Arm(result, seamPaths, factory?.Request, session);
    }

    /// <summary>
    /// The step configuration both arms run: every stage that produces or consumes a session file is
    /// ON (a download, a probe, silence, shots, the visual grid) and only the provider stages are off,
    /// so the run exercises the port rather than a shortcut through it.
    /// </summary>
    private static VideoAnalyzeStepConfig DefaultConfig() => new(
        Version: 1,
        Source: new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: FileId),
        DetectSilence: true,
        DetectShots: true,
        AnalyzeVisuals: true,
        AnalyzeAudioLevels: false,
        DetectNearDuplicates: true,
        Transcription: VideoTranscriptionMode.Off,
        Vision: VideoVisionMode.Off);

    private static string ScratchRoot() =>
        Path.Combine(Path.GetTempPath(), "video-analyze-port-tests", Guid.NewGuid().ToString("N"));

    private static byte[] ClickTrackWav() =>
        BeatSyncTests.Wav(MusicBeatAnalyzerTests.ClickTrack(128, 0.1, 20));

    private static ProjectWorkspaceFile SourceFile() => new(
        FileId, ProjectId, "take.mp4", null, "userFiles", SourceKey, "video/mp4", 3, DateTime.UnixEpoch, null);

    private static ProjectWorkspaceFile MusicFile() => new(
        MusicFileId, ProjectId, "click.mp3", null, "userFiles", MusicKey, "audio/mpeg", 4096, DateTime.UnixEpoch, null);

    /// <summary>
    /// A workspace that behaves the way a REMOTE session does at every point this executor can
    /// observe: <see cref="MaterializeAsync"/> answers the virtual path UNCHANGED — which is the
    /// interface's own rule for a session whose bytes are on another machine — and every stage, read,
    /// write, stat and publish goes through an in-memory store instead of this host's filesystem.
    /// Nothing here can be satisfied by a path on this machine, which is what makes a green run
    /// evidence that the extraction ran on the session.
    /// </summary>
    private sealed class SessionWorkspace : IMediaWorkspace
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public List<string> Staged { get; } = [];

        public List<string> Published { get; } = [];

        public List<string[]> FfmpegCalls { get; } = [];

        /// <summary>When set, answers every ffmpeg call (a test's way to time out or fail the analysis copy).</summary>
        public Func<IReadOnlyList<string>, VideoToolResult>? FfmpegHandler { get; set; }

        public string RootPath => "session:/";

        public Task StageAsync(string storageKey, string virtualPath, CancellationToken ct)
        {
            Staged.Add(virtualPath);
            // A staged source is decoded by fake seams here, so its bytes only have to exist.
            Files[virtualPath] = [0x00, 0x01, 0x02];
            return Task.CompletedTask;
        }

        public Task<VideoToolResult> RunFfmpegAsync(
            IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct)
            => RunFfmpegAsync(argv, timeout, ct, null);

        public Task<VideoToolResult> RunFfmpegAsync(
            IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine)
        {
            FfmpegCalls.Add(argv.ToArray());
            onStdOutLine?.Invoke("progress=continue");
            if (FfmpegHandler is { } handler)
                return Task.FromResult(handler(argv));
            return Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));
        }

        public Task<VideoToolResult> RunFfprobeAsync(
            IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));

        /// <summary>The remote rule: a virtual path stays a virtual path.</summary>
        public Task<string> MaterializeAsync(string virtualPath, CancellationToken ct) => Task.FromResult(virtualPath);

        /// <summary>When true, writing analysis.json fails as the runner's stage of it does on a missing object.</summary>
        public bool ArtifactWrite404 { get; set; }

        /// <summary>When set, publishing a document throws this (a rejected runner output).</summary>
        public Exception? UploadThrows { get; set; }

        public Task WriteBytesAsync(string virtualPath, byte[] content, CancellationToken ct)
        {
            if (ArtifactWrite404 && virtualPath == "analysis.json")
                throw new ReelBolt.WorkflowEngine.Services.Compute.RunnerSessionOpException(
                    "stage_failed", "stage 'analysis.json' failed on the runner: the object store answered 404", false, false);
            Files[virtualPath] = content;
            return Task.CompletedTask;
        }

        public Task WriteTextAsync(string virtualPath, string content, Encoding encoding, CancellationToken ct)
            => WriteBytesAsync(virtualPath, encoding.GetBytes(content), ct);

        public Task<byte[]> ReadAsync(string virtualPath, CancellationToken ct) =>
            ReadAsync(virtualPath, maxBytes: int.MaxValue, ct);

        public Task<byte[]> ReadAsync(string virtualPath, int maxBytes, CancellationToken ct) =>
            Files.TryGetValue(virtualPath, out byte[]? bytes)
                ? Task.FromResult(bytes)
                : throw new FileNotFoundException(
                    $"Media workspace file '{virtualPath}' does not exist.", virtualPath);

        public Task<MediaWorkspaceStat> StatAsync(string virtualPath, CancellationToken ct) =>
            Task.FromResult(Files.TryGetValue(virtualPath, out byte[]? bytes)
                ? new MediaWorkspaceStat(true, bytes.Length)
                : new MediaWorkspaceStat(false, 0));

        public Task<string> PublishAsync(
            string virtualPath, string finalStorageKey, string contentType, CancellationToken ct)
        {
            Published.Add(virtualPath);
            return Task.FromResult(finalStorageKey);
        }

        public Task<string> UploadArtifactAsync(
            string virtualPath, string finalStorageKey, string contentType, CancellationToken ct)
        {
            if (UploadThrows is not null && virtualPath == "analysis.json")
                throw UploadThrows;
            Published.Add(virtualPath);
            return Task.FromResult(finalStorageKey);
        }
    }

    private sealed class RecordingWorkspaceFactory(SessionWorkspace session) : IMediaWorkspaceFactory
    {
        public MediaWorkspaceRequest? Request { get; private set; }

        public Task<MediaWorkspaceOpenResult> OpenAsync(
            MediaWorkspaceRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(MediaWorkspaceOpenResult.Opened(new MediaWorkspaceLease(
                session,
                scratch: null,
                ComputeDecision.Local(ComputeDecisionReasons.PreferenceRunner),
                job: null,
                request.ExecutionId,
                request.StepOrder,
                NullLogger.Instance)));
        }
    }
}
