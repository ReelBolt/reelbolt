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
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Caching;
using ReelBolt.WorkflowEngine.Services.Video.Events;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The AnalyzeEvents path of <see cref="VideoAnalyzeStepExecutor"/> through a stub <see cref="IVideoEventDetector"/>.</summary>
public class VideoAnalyzeEventsTests
{
    internal static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly string SourceKey = $"projects/{ProjectId}/outputFiles/e/render.mp4";

    /// <summary>A stored file of the test project with another extension (a photo, say).</summary>
    internal static string SourceKeyWithExtension(string extension) => $"projects/{ProjectId}/userFiles/upload{extension}";

    internal sealed class StubDetector(Func<VideoEventOptions?, VideoEventSet> detect) : IVideoEventDetector
    {
        public List<VideoEventOptions?> Seen { get; } = [];

        /// <summary>The pixels file of each call, in order.</summary>
        public List<string> PixelPaths { get; } = [];

        public Task<VideoEventSet> DetectAsync(
            IMediaWorkspace workspace, string pixelsVirtualPath, MediaProbeResult probe, VideoEventOptions? options = null,
            string? audioVirtualPath = null, int pixelsFpsNum = 0, int pixelsFpsDen = 0, CancellationToken ct = default)
        {
            Seen.Add(options);
            PixelPaths.Add(pixelsVirtualPath);
            return Task.FromResult(detect(options));
        }
    }

    /// <summary>How often each stubbed pass ran and which file it read, across the runs that share one instance.</summary>
    internal sealed class PassCalls
    {
        public int Silence { get; set; }
        public int Shots { get; set; }
        public int Grid { get; set; }
        public List<string> ShotPaths { get; } = [];
        public List<string> GridPaths { get; } = [];
    }

    /// <summary>A frame grid sampler that answers real bytes: one frame a sampled second, the same bytes for the same arguments.</summary>
    internal sealed class StubGridSampler(PassCalls calls) : IFrameGridSampler
    {
        public Task<FrameGridResult> SampleAsync(
            IMediaWorkspace workspace, string inputVirtualPath, double sampleFps, int gridWidth, int gridHeight,
            double totalDurationSec, int maxSampleFrames, CancellationToken ct)
        {
            calls.Grid++;
            calls.GridPaths.Add(inputVirtualPath);
            int frames = (int)Math.Clamp(Math.Ceiling(totalDurationSec * sampleFps), 1, Math.Max(1, maxSampleFrames));
            byte[] pixels = new byte[frames * gridWidth * gridHeight * 3];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = (byte)((i * 31 + i / (gridWidth * gridHeight * 3) * 7) % 251);
            return Task.FromResult(new FrameGridResult(pixels, gridWidth, gridHeight, sampleFps, frames));
        }

        public Task<FrameGridResult> SampleAsync(
            IMediaWorkspace workspace, string inputVirtualPath, double sampleFps, int gridWidth, int gridHeight,
            double totalDurationSec, int maxSampleFrames, GridChroma chroma, CancellationToken ct) =>
            SampleAsync(workspace, inputVirtualPath, sampleFps, gridWidth, gridHeight, totalDurationSec, maxSampleFrames, ct);

        public Task<FrameGridResult> SampleGrayAsync(
            IMediaWorkspace workspace, string inputVirtualPath, double sampleFps, int gridWidth, int gridHeight,
            double totalDurationSec, int maxSampleFrames, CancellationToken ct) => throw new NotSupportedException();

        public Task<FrameGridResult> SampleAsync(
            string localFilePath, VideoScratchSpace scratch, double sampleFps, int gridWidth, int gridHeight,
            double totalDurationSec, int maxSampleFrames, CancellationToken ct) => throw new NotSupportedException();

        public Task<FrameGridResult> SampleGrayAsync(
            string localFilePath, VideoScratchSpace scratch, double sampleFps, int gridWidth, int gridHeight,
            double totalDurationSec, int maxSampleFrames, CancellationToken ct) => throw new NotSupportedException();
    }

    internal static VideoEvent Ev(VideoEventType type, int frame, double strength) =>
        new(type, frame, null, null, strength, VideoEventOrigin.Pixels, 0.9);

    internal static VideoEventSet Set(int suppressed, params VideoEvent[] events) => new(30, 900, 1920, 1080, events, suppressed);

    internal sealed record Run(StepExecutionResult Result, JsonObject Root, JsonObject? Artifact)
    {
        public JsonObject Meta => Root["meta"]!.AsObject();
        public JsonObject View => Root["view"]!.AsObject();
        public IEnumerable<string> WarningCodes => (Root["warnings"] as JsonArray ?? []).Select(w => w!["code"]!.GetValue<string>());
    }

    internal static async Task<Run> RunAsync(
        IVideoEventDetector? detector, Func<VideoAnalyzeStepConfig, VideoAnalyzeStepConfig> configure, string? declaredSidecar = null,
        IEventDescriber? describer = null, bool visionProvider = false,
        // The per-source pass cache runs (appended, all defaulted): the cache, the pass counters (which also swap in a
        // grid sampler with real bytes), the probe every file answers, the source's storage key, and a stand-in for
        // ffmpeg (the analysis copy, a trim, a still) that is handed each argv.
        ISourcePassCache? passCache = null, PassCalls? calls = null, MediaProbeResult? probeResult = null,
        string? sourceKey = null, Func<IReadOnlyList<string>, VideoToolResult>? ffmpeg = null,
        // Appended: collects the file name (the part of the key after agentFiles/) of every artifact the step uploads.
        List<string>? uploads = null,
        // Appended: the keyframe extractor (the default one makes nothing and never fails).
        IKeyframeExtractor? keyframes = null)
    {
        sourceKey ??= SourceKey;
        var workspace = new Mock<IProjectFileWorkspace>();
        var probe = new Mock<IMediaProbe>();
        var silence = new Mock<ISilenceDetector>();
        var shots = new Mock<IShotDetector>();
        string? artifactJson = null;
        string? sidecarKey = RenderEventManifest.SidecarKeyFor(sourceKey);

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, key, dest, _) =>
            {
                if (key == sidecarKey && declaredSidecar is not null)
                    File.WriteAllText(dest, declaredSidecar);
                else if (key == sidecarKey)
                    throw new FileNotFoundException(key);
                else
                    File.WriteAllBytes(dest, [0, 1, 2]);
                return Task.CompletedTask;
            });
        workspace
            .Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Returns<Guid, string, string, string, CancellationToken, string>((_, path, fileName, _, _, _) =>
            {
                if (uploads is not null)
                {
                    lock (uploads)
                        uploads.Add(fileName);
                }

                // The analysis document is the JSON upload; a trimmed window or a still's clip is a video.
                if (path.EndsWith(".json", StringComparison.Ordinal))
                    artifactJson = File.ReadAllText(path);
                return Task.FromResult("projects/p/agentFiles/video-analysis/e/step-1-analysis.json");
            });
        probe.Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(probeResult ?? new MediaProbeResult(30, 30, 1, 1920, 1080, "h264", "aac", 48000));
        shots.Setup(s => s.DetectShotsAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .Returns<IMediaWorkspace, string, double, double, CancellationToken>((_, path, _, _, _) =>
            {
                if (calls is not null)
                {
                    calls.Shots++;
                    calls.ShotPaths.Add(path);
                }

                return Task.FromResult<IReadOnlyList<(double StartSec, double EndSec)>>(new[] { (0.0, 10.0), (10.0, 30.0) });
            });
        silence.Setup(s => s.DetectAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .Returns<IMediaWorkspace, string, double, double, double?, CancellationToken>((_, _, _, _, _, _) =>
            {
                if (calls is not null)
                    calls.Silence++;
                return Task.FromResult<IReadOnlyList<(double StartSec, double EndSec)>>(
                    calls is null ? Array.Empty<(double, double)>() : new[] { (4.0, 5.5), (20.25, 21.0) });
            });
        Mock<IVideoToolRunner>? toolRunner = null;
        if (ffmpeg is not null)
        {
            toolRunner = new Mock<IVideoToolRunner>();
            toolRunner.Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Returns<IReadOnlyList<string>, TimeSpan, CancellationToken>((args, _, _) => Task.FromResult(ffmpeg(args)));
            toolRunner.Setup(r => r.RunFfmpegAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Returns<IMediaWorkspace, IReadOnlyList<string>, TimeSpan, CancellationToken>((_, args, _, _) => Task.FromResult(ffmpeg(args)));
        }
        var audio = new Mock<IAudioExtractor>();
        audio.Setup(a => a.ExtractWavAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<IMediaWorkspace, string, string, CancellationToken>((ws, _, outPath, ct) => ws.WriteBytesAsync(outPath, new byte[100], ct));

        var options = Options.Create(new VideoEditingOptions
        {
            ScratchPath = Path.Combine(Path.GetTempPath(), "video-analyze-events-tests", Guid.NewGuid().ToString("N")),
            MaxConcurrentJobs = 1,
            AnalyzeTimeoutSeconds = 30,
            CompileTimeoutSeconds = 30
        });
        var resolver = new Mock<IInferenceProviderResolver>();
        if (visionProvider)
            resolver.Setup(r => r.ResolveVisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedInferenceProvider(Guid.NewGuid(), "vision", InferenceProviderKind.OpenAICompatible, "http://x", "vision-model", "", 30));
        var executor = new VideoAnalyzeStepExecutor(
            probe.Object, silence.Object, shots.Object, audio.Object,
            calls is null ? new Mock<IFrameGridSampler>().Object : new StubGridSampler(calls),
            keyframes ?? new Mock<IKeyframeExtractor>().Object, new Mock<ISharpnessSampler>().Object, new Mock<IShotCaptioner>().Object,
            workspace.Object, new Mock<ITranscriptionClientFactory>().Object, resolver.Object,
            options, NullLogger<VideoAnalyzeStepExecutor>.Instance, videoToolRunner: toolRunner?.Object,
            eventDetector: detector, eventDescriber: describer, sourcePassCache: passCache);

        VideoAnalyzeStepConfig config = configure(new VideoAnalyzeStepConfig(Version: 1, Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput))
            with
        { Transcription = VideoTranscriptionMode.Off, DetectSilence = false, AnalyzeAudioLevels = false });
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } })
        };
        var context = new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: sourceKey, ArtifactStorageKey: null)],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };

        StepExecutionResult result = await executor.ExecuteAsync(context);
        return new Run(result, result.Output is { Length: > 0 } && result.Status == StepStatus.Completed ? JsonNode.Parse(result.Output)!.AsObject() : [],
            artifactJson is null ? null : JsonNode.Parse(artifactJson)!.AsObject());
    }

    /// <summary>The artifact with its run-time stamps blanked, so two runs compare on shape and content.</summary>
    internal static string Stable(JsonObject artifact) =>
        System.Text.RegularExpressions.Regex.Replace(artifact.ToJsonString(), "\\d{4}-\\d\\d-\\d\\dT[\\d:.]+Z", "T");

    [Fact]
    public async Task Off_leaves_every_events_key_out_and_matches_a_run_with_no_detector_byte_for_byte()
    {
        var throwing = new StubDetector(_ => throw new InvalidOperationException("must not be called when AnalyzeEvents is off"));

        Run without = await RunAsync(null, c => c);
        Run with = await RunAsync(throwing, c => c with { AnalyzeEvents = false });

        without.Result.Status.Should().Be(StepStatus.Completed, because: without.Result.ErrorDetails ?? without.Result.Output);
        with.Result.Output.Should().Be(without.Result.Output);
        Stable(with.Artifact!).Should().Be(Stable(without.Artifact!));
        throwing.Seen.Should().BeEmpty();
        with.Result.Output.Should().NotContain("\"events\"").And.NotContain("\"renderEvents\"").And.NotContain("events_unavailable");
        with.Artifact["events"].Should().BeNull();
        with.Artifact["offeredEventIds"].Should().BeNull();
    }

    [Fact]
    public async Task On_offers_words_only_in_the_view_and_a_digest_in_meta_and_merges_with_declared_events_by_frame()
    {
        const string sidecar = "{\"version\":1,\"fps\":30,\"events\":[{\"name\":\"stamp\",\"frame\":45,\"label\":\"Stamp\"}]}";
        var detector = new StubDetector(_ => Set(2, Ev(VideoEventType.Hit, 90, 0.9), Ev(VideoEventType.Appear, 15, 0.5)));

        Run run = await RunAsync(detector, c => c with { AnalyzeEvents = true, OfferSfxClips = true, SfxWorthyEventsOnly = false }, sidecar);

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);
        JsonArray viewEvents = run.View["events"]!.AsArray();
        viewEvents.Should().HaveCount(2);
        foreach (JsonNode? e in viewEvents)
            e!.AsObject().Select(p => p.Key).Should().BeSubsetOf(["id", "type", "strength", "region", "what"]);
        viewEvents.ToJsonString().Should().NotContain("frame").And.NotContain("Sec");

        JsonObject events = run.Meta["events"]!.AsObject();
        events["count"]!.GetValue<int>().Should().Be(2);
        events["byType"]!.AsObject().Select(p => p.Key).Should().BeEquivalentTo(["appear", "hit"]);
        events["digest"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        events["suppressed"]!.GetValue<int>().Should().Be(2);

        JsonArray merged = run.Artifact!["events"]!.AsArray();
        merged.Select(e => e!["id"]!.GetValue<string>()).Should().Equal("e0", "e1", "e2");
        merged.Select(e => e!["frame"]!.GetValue<int>()).Should().Equal(15, 45, 90);
        merged.Select(e => e!["name"]!.GetValue<string>()).Should().Equal("appear", "stamp", "hit");
        run.View["renderEvents"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public async Task Outline_off_adds_no_outline_key_and_on_adds_the_outline_the_section_lists_and_a_joined_digest()
    {
        var detector = new StubDetector(_ => Set(2, Ev(VideoEventType.Hit, 90, 0.9), Ev(VideoEventType.Appear, 15, 0.5), Ev(VideoEventType.TextIn, 400, 0.6)));

        Run plain = await RunAsync(detector, c => c with { AnalyzeEvents = true });
        Run outlined = await RunAsync(detector, c => c with { AnalyzeEvents = true, Outline = true });

        plain.Result.Output.Should().NotContain("\"outline\"").And.NotContain("sectionEvents");
        outlined.Result.Status.Should().Be(StepStatus.Completed, because: outlined.Result.ErrorDetails ?? outlined.Result.Output);
        outlined.View["outline"]!["sections"]!.AsArray().Count.Should().BeInRange(3, 12);
        outlined.View["sectionEvents"]!.AsObject().Select(p => p.Key).Should().Contain("c0");
        JsonObject meta = outlined.Meta["outline"]!.AsObject();
        meta["sections"]!.GetValue<int>().Should().BeInRange(3, 12);
        meta["tokensEstimate"]!.GetValue<int>().Should().BeGreaterThan(0);
        meta["digest"]!.GetValue<string>().Should().HaveLength(64);
        outlined.Meta["events"]!["digest"]!.GetValue<string>().Should().NotBe(plain.Meta["events"]!["digest"]!.GetValue<string>(), "the outline joins the upstream digest");
        outlined.View["outline"]!.ToJsonString().Should().NotContain("frame").And.NotContain("Sec");
    }

    [Fact]
    public async Task Outline_without_AnalyzeEvents_does_nothing()
    {
        Run run = await RunAsync(null, c => c with { Outline = true });
        run.Result.Output.Should().NotContain("\"outline\"");
    }

    [Fact]
    public async Task A_detector_that_throws_degrades_to_events_unavailable_and_the_step_still_succeeds()
    {
        Run run = await RunAsync(new StubDetector(_ => throw new InvalidOperationException("boom")), c => c with { AnalyzeEvents = true });

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);
        run.WarningCodes.Should().Contain("events_unavailable");
        run.View.ContainsKey("events").Should().BeFalse();
    }

    [Fact]
    public async Task Events_the_view_cannot_hold_are_left_out_with_an_events_suppressed_warning_and_a_meta_count()
    {
        VideoEvent[] many = [.. Enumerable.Range(0, 40).Select(i => Ev(VideoEventType.Hit, i * 10, 0.5 + i / 100.0))];

        Run run = await RunAsync(new StubDetector(_ => Set(0, many)), c => c with { AnalyzeEvents = true, MaxOutputChars = 256 });

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);
        run.WarningCodes.Should().Contain("events_suppressed");
        run.Meta["events"]!["suppressed"]!.GetValue<int>().Should().BeGreaterThan(0);
        (run.View["events"] as JsonArray)?.Count.Should().BeLessThan(40);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_event_the_read_tools_return_is_an_offered_id_even_when_the_flat_list_was_cut(bool outline)
    {
        VideoEvent[] many = [.. Enumerable.Range(0, 300).Select(i => Ev(VideoEventType.Hit, i * 3, 0.4 + i / 1000.0))];

        Run run = await RunAsync(new StubDetector(_ => Set(0, many)), c => c with { AnalyzeEvents = true, Outline = outline, MaxOutputChars = 256 });

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);
        run.WarningCodes.Should().Contain("events_suppressed");
        string[] shownIds = [.. (run.View["events"] as JsonArray ?? []).Select(e => e!["id"]!.GetValue<string>())];
        VideoAnalysisArtifact artifact = VideoEventReader.Parse(run.Artifact!.ToJsonString())!;
        // What ListVideoEvents answers when asked for the most it gives: the compile must take each of these ids.
        JsonObject listed = JsonNode.Parse(VideoEventReader.List(artifact, null, null, null, VideoEventReader.MaxLimit))!.AsObject();
        listed["total"]!.GetValue<int>().Should().Be(300);
        string[] listedIds = [.. listed["events"]!.AsArray().Select(e => e!["id"]!.GetValue<string>())];
        listedIds.Except(shownIds).Should().NotBeEmpty("the read tool returns moments the cut flat list does not show");
        artifact.OfferedEventIds.Should().Contain(listedIds);
        artifact.OfferedEventIds.Should().BeEquivalentTo(VideoEventOutline.DetectedEvents(artifact).Select(e => e.Id));
        // The outline's own section lists show ids too: each is offered.
        if (run.View["sectionEvents"] is JsonObject sections)
            artifact.OfferedEventIds.Should().Contain(sections.SelectMany(s => s.Value!.AsArray().Select(e => e!["id"]!.GetValue<string>())));
    }

    [Fact]
    public async Task A_detection_that_lost_a_part_says_so_in_meta_and_is_never_step_cached_and_a_whole_one_is_unchanged()
    {
        VideoEventSet whole = Set(0, Ev(VideoEventType.Hit, 90, 0.9), Ev(VideoEventType.Appear, 15, 0.5));

        Run clean = await RunAsync(new StubDetector(_ => whole), c => c with { AnalyzeEvents = true });
        Run degraded = await RunAsync(new StubDetector(_ => whole with { Degraded = true }), c => c with { AnalyzeEvents = true });
        Run nothingFound = await RunAsync(new StubDetector(_ => Set(0) with { Degraded = true }), c => c with { AnalyzeEvents = true });

        clean.Meta["events"]!.AsObject().ContainsKey("degraded").Should().BeFalse("only a degraded set is marked, so every other output is unchanged");
        ReelBolt.WorkflowEngine.Execution.Caching.StepResultCache.ReportsDegradation(clean.Result.Output).Should().BeFalse();

        degraded.Result.Status.Should().Be(StepStatus.Completed, because: degraded.Result.ErrorDetails ?? degraded.Result.Output);
        degraded.Meta["events"]!["degraded"]!.GetValue<bool>().Should().BeTrue();
        ReelBolt.WorkflowEngine.Execution.Caching.StepResultCache.ReportsDegradation(degraded.Result.Output).Should().BeTrue("another run may find more");
        degraded.View["events"]!.ToJsonString().Should().Be(clean.View["events"]!.ToJsonString(), "what was found is still offered");

        // Even when the part that was read found nothing: the missing part might have.
        nothingFound.Result.Status.Should().Be(StepStatus.Completed);
        ReelBolt.WorkflowEngine.Execution.Caching.StepResultCache.ReportsDegradation(nothingFound.Result.Output).Should().BeTrue();
    }

    [Fact]
    public async Task A_heavy_source_timeout_stops_the_step_with_source_too_demanding_instead_of_degrading()
    {
        var detector = new StubDetector(_ => throw new VideoToolTimedOutException("timed out", heavySource: true));

        Run run = await RunAsync(detector, c => c with { AnalyzeEvents = true });

        run.Result.Status.Should().Be(StepStatus.Failed);
        run.Result.ErrorDetails.Should().Be(FailureMessages.UserMessage(VideoSourceLimits.TooDemandingCode));
    }

    [Fact]
    public async Task A_light_source_timeout_degrades_like_any_other_detector_failure()
    {
        Run run = await RunAsync(new StubDetector(_ => throw new VideoToolTimedOutException("timed out", heavySource: false)), c => c with { AnalyzeEvents = true });

        run.Result.Status.Should().Be(StepStatus.Completed);
        run.WarningCodes.Should().Contain("events_unavailable");
    }

    [Theory]
    [InlineData(null, 400)]
    [InlineData(1, 10)]
    [InlineData(50, 50)]
    [InlineData(99_999, 2000)]
    public async Task MaxEvents_defaults_to_400_and_is_clamped_to_10_through_2000_like_the_other_bounded_knobs(int? configured, int expected)
    {
        var detector = new StubDetector(_ => Set(0));

        await RunAsync(detector, c => c with { AnalyzeEvents = true, MaxEvents = configured });

        detector.Seen.Should().ContainSingle().Which!.MaxEvents.Should().Be(expected);
    }
}
