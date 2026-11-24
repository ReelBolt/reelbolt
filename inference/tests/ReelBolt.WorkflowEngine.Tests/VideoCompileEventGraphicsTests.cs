#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>Graphics on an event: an overlay naming an e{n} id starts on that moment; the p{n} path is unchanged.</summary>
public class VideoCompileEventGraphicsTests
{
    private const string AnalysisKey = "projects/p/agentFiles/video-analysis/e/step-1-analysis.json";
    private const string SourceVideoKey = "projects/p/outputFiles/e/render.mp4";

    private static JsonSerializerOptions Json() => new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static VideoAnalysisArtifact Artifact(double seconds, int width, int height, params (string Id, double Sec, VideoEventRegion? Region)[] events) => new VideoAnalysisArtifact(
        Version: 1,
        Media: new VideoAnalysisMedia(seconds, 30, 1, width, height),
        Shots: [new VideoAnalysisShot("s0", 0.0, seconds)],
        SilenceSpans: [],
        Segments: [],
        Words: [],
        OfferedIds: ["s0"],
        Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
        Placements: [new VideoAnalysisPlacement("p0", "s0", "CenterBand", new VideoAnalysisRect(0.0, 1.0 / 3, 1.0, 1.0 / 3), "Event", 1.5, seconds, 1.0, "Light", 0)],
        OfferedPlacementIds: ["p0"]) with
    {
        Events = events.Select(e => new VideoAnalysisRenderEvent(e.Id, "event", "event", (int)Math.Round(e.Sec * 30), e.Sec, 0, null, "Flash", "Pixels", 0.9, e.Region)).ToList(),
        OfferedEventIds = events.Select(e => e.Id).ToList()
    };

    private static string Plan(params object[] overlays) =>
        JsonSerializer.Serialize(new { overlays, planRationale = "test" }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static object Overlay(string placementId, string eventId, string kind, string text = "Hello") =>
        new { placementId, eventId, kind, text, subtext = "", duration = "Medium", emphasis = "Normal", reason = "test" };

    private sealed record Run(StepExecutionResult Result, JsonElement Edl, string? Graph, string? OutputCopy);

    private static async Task<Run> RunAsync(
        VideoAnalysisArtifact artifact, string graphicsPlanJson, Func<VideoCompileStepConfig, VideoCompileStepConfig>? config = null,
        string? realSource = null, IVideoToolRunner? realRunner = null, IMediaProbe? realProbe = null, string? outputCopy = null)
    {
        VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
        VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();

        string artifactJson = JsonSerializer.Serialize(artifact, Json());
        string? graph = null;
        JsonElement edl = default;
        var files = new Mock<IProjectFileWorkspace>();
        files.Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, key, dest, _) =>
            {
                if (key == AnalysisKey)
                    File.WriteAllText(dest, artifactJson);
                else if (realSource is not null)
                    File.Copy(realSource, dest, overwrite: true);
                else
                    File.WriteAllBytes(dest, [0x00, 0x01]);
                return Task.CompletedTask;
            });
        files.Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Callback<Guid, string, string, string, CancellationToken, string>((_, path, _, _, _, _) =>
            {
                if (path.EndsWith("edl.json", StringComparison.OrdinalIgnoreCase))
                    edl = JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
            })
            .ReturnsAsync("projects/p/agentFiles/video-analysis/e/step-4-edl.json");
        files.Setup(w => w.UploadBinaryFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid projectId, string localPath, string fileName, string mime, SummaryStatus _, FileIndexingStatus _, CancellationToken _, string category, string? _) =>
            {
                if (outputCopy is not null)
                    File.Copy(localPath, outputCopy, overwrite: true);
                return new ProjectWorkspaceFile(Guid.NewGuid(), projectId, fileName, null, category,
                    $"projects/{projectId}/{category}/{Guid.NewGuid():D}.mp4", mime, 1024, DateTime.UnixEpoch, null);
            });
        files.SetupRegisteredRenderPublish(onPublishedLocalPath: p => { if (outputCopy is not null) File.Copy(p, outputCopy, overwrite: true); });

        IVideoToolRunner runner;
        IMediaProbe probe;
        if (realRunner is not null)
        {
            runner = realRunner;
            probe = realProbe!;
        }
        else
        {
            void Capture(IReadOnlyList<string> args)
            {
                if (!args.Contains("-map"))
                    return;
                List<string> list = args.ToList();
                int script = list.IndexOf("-filter_complex_script");
                graph = script >= 0 ? File.ReadAllText(list[script + 1]) : list[list.IndexOf("-filter_complex") + 1];
                // The scratch directory and the font file are per machine and per run.
                graph = System.Text.RegularExpressions.Regex.Replace(graph, "textfile='[^']*?/ov-", "textfile='<scratch>/ov-");
                graph = System.Text.RegularExpressions.Regex.Replace(graph, "fontfile='[^']*'", "fontfile='<font>'");
            }

            var tools = new Mock<IVideoToolRunner>();
            tools.Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VideoToolResult(0, "... drawtext perspective alphamerge xfade acrossfade colorbalance colorlevels hue eq ...", string.Empty, false));
            tools.Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
                .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken, Action<string>>((args, _, _, _) => Capture(args))
                .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
            tools.Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken>((args, _, _) => Capture(args))
                .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
            runner = tools.Object;
            var mediaProbe = new Mock<IMediaProbe>();
            mediaProbe.Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaProbeResult(artifact.Media.DurationSec, 30, 1, artifact.Media.Width, artifact.Media.Height, "h264", null, null));
            probe = mediaProbe.Object;
        }

        VideoCompileStepConfig cfg = new VideoCompileStepConfig(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
            AnalysisStepOrder: 1,
            EnableGraphics: true,
            GraphicsPlan: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 3)) with
        {
            Mode = VideoCompileMode.Reencode,
            PrePaddingMs = 0,
            PostPaddingMs = 0,
            LoudnessTarget = ProgramLoudness.Off
        };
        if (config is not null)
            cfg = config(cfg);

        var step = new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 4, StepType = StepType.VideoCompile, VideoCompileConfigJson = JsonSerializer.Serialize(cfg, Json()) };
        var analyzeStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(new VideoAnalyzeStepConfig(Version: 1, Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput)), Json())
        };
        string decision = JsonSerializer.Serialize(
            new { keep = new[] { new { fromId = "s0", toId = "s0", reason = "keep" } }, editRationale = "test", suggestedTitle = "Test" },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var executor = new VideoCompileStepExecutor(
            runner, probe, files.Object, new Mock<IServiceScopeFactory>().Object,
            Options.Create(new VideoEditingOptions
            {
                ScratchPath = Path.Combine(Path.GetTempPath(), "video-compile-event-graphics-tests", Guid.NewGuid().ToString("N")),
                MaxConcurrentJobs = 1,
                AnalyzeTimeoutSeconds = 60,
                CompileTimeoutSeconds = 120
            }),
            NullLogger<VideoCompileStepExecutor>.Instance);

        Guid executionId = Guid.NewGuid();
        StepExecutionResult result = await executor.ExecuteAsync(new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = executionId, ProjectId = Guid.NewGuid() },
            Step = step,
            AllSteps = [analyzeStep, step],
            AccumulatedOutput = graphicsPlanJson,
            StepOutputHistory =
            [
                new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null),
                new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
                new StepOutputHistoryEntry(2, "StoryEditor", decision, OutputStorageKey: null, ArtifactStorageKey: null),
                new StepOutputHistoryEntry(3, "MotionGraphicsPlanner", graphicsPlanJson, OutputStorageKey: null, ArtifactStorageKey: null)
            ],
            CurrentStepIndex = 3,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        });
        return new Run(result, edl, graph, outputCopy);
    }

    // ---- layout words ----

    [Theory]
    [InlineData("LowerThird", 0.5, 0.1, "LowerThird", 0.0, 2.0 / 3, 1.0)]
    [InlineData("Title", 0.5, 0.1, "CenterBand", 0.0, 1.0 / 3, 1.0)]
    [InlineData("Callout", 0.5, 0.1, "UpperThird", 0.0, 0.0, 1.0)]
    [InlineData("Callout", 0.1, 0.9, "LowerThird", 0.0, 2.0 / 3, 1.0)]
    [InlineData("Tag", 0.8, 0.2, "UpperThird", 2.0 / 3, 0.0, 1.0 / 3)]
    [InlineData("Tag", 0.2, 0.5, "CenterBand", 0.0, 1.0 / 3, 1.0 / 3)]
    [InlineData("", 0.5, 0.9, "LowerThird", 0.0, 2.0 / 3, 1.0)]
    [InlineData("Banner", 0.5, 0.5, "CenterBand", 0.0, 1.0 / 3, 1.0)]
    public void Each_kind_word_has_its_own_layout(string kind, double cx, double cy, string band, double x, double y, double w)
    {
        (string region, VideoAnalysisRect rect) = EventOverlayLayout.For(kind, new VideoEventRegion(cx - 0.05, cy - 0.05, 0.1, 0.1));

        region.Should().Be(band);
        rect.X.Should().BeApproximately(x, 1e-9);
        rect.Y.Should().BeApproximately(y, 1e-9);
        rect.W.Should().BeApproximately(w, 1e-9);
        rect.H.Should().BeApproximately(1.0 / 3, 1e-9);
    }

    [Fact]
    public void An_event_without_a_region_lays_a_callout_out_in_the_centre_band()
    {
        EventOverlayLayout.For("Callout", null).Region.Should().Be("CenterBand");
    }

    [Fact]
    public void The_event_id_round_trips_and_defaults_to_empty()
    {
        new MotionGraphicsOverlay().EventId.Should().BeEmpty();
        MotionGraphicsOverlay back = JsonSerializer.Deserialize<MotionGraphicsOverlay>("{\"eventId\":\"e3\",\"kind\":\"Tag\"}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        back.EventId.Should().Be("e3");
        back.PlacementId.Should().BeEmpty();
    }

    // ---- compile: golden on / identity off ----

    [Fact]
    public async Task An_event_overlay_draws_exactly_what_the_same_overlay_on_a_synthetic_placement_would()
    {
        // e0 at 4.0 s, no region: a Callout sits in the centre band. The equivalent hand-made placement starts at 4.0 s.
        VideoAnalysisArtifact withEvent = Artifact(10.0, 1920, 1080, ("e0", 4.0, null));
        Run onEvent = await RunAsync(withEvent, Plan(Overlay("", "e0", "Callout")));
        onEvent.Result.Status.Should().Be(StepStatus.Completed, because: onEvent.Result.ErrorDetails ?? onEvent.Result.Output);
        onEvent.Edl.GetProperty("graphics").GetProperty("appliedOverlayCount").GetInt32().Should().Be(1);

        VideoAnalysisArtifact withPlacement = withEvent with
        {
            Placements = [new VideoAnalysisPlacement("p0", "s0", "CenterBand", new VideoAnalysisRect(0.0, 1.0 / 3, 1.0, 1.0 / 3), "Event", 4.0, 14.0, 1.0, "Light", 0)],
            Events = null,
            OfferedEventIds = null
        };
        Run onPlacement = await RunAsync(withPlacement, Plan(Overlay("p0", "", "Callout")));

        onEvent.Graph.Should().NotBeNullOrEmpty().And.Contain("drawtext");
        onEvent.Graph.Should().Be(onPlacement.Graph, "the event only chooses where and when; the drawing is the placement path");
        // Golden: the overlay starts on the event (4 s) and stays up for the Medium duration (3 s), in the centre band.
        onEvent.Graph.Should().Be(
            "[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=10.066667,select='gte(t,0)*lt(t,10)',setpts=N/FRAME_RATE/TB[vcut];" +
            "[vcut]drawbox=x=173:y=453:w=1574:h=173:color=black@0.45:t=fill:enable='between(t,4,7)'[gfx0b];" +
            "[gfx0b]drawtext=fontfile='<font>':textfile='<scratch>/ov-0.txt':expansion=none:fontsize=54:fontcolor=white:" +
            "x=173+(1574-text_w)/2:y=453+(173-text_h)/2:" +
            "alpha='if(lt(t,4),0,if(lt(t,4+0.3),(t-4)/0.3,if(lt(t,7-0.3),1,if(lt(t,7),(7-t)/0.3,0))))':enable='between(t,4,7)'[vout]");
        onEvent.Edl.GetProperty("graphics").GetProperty("appliedOverlays")[0].GetProperty("placementId").GetString().Should().Be("e0");
    }

    [Fact]
    public async Task A_plan_without_event_ids_compiles_to_the_same_graph_as_before()
    {
        VideoAnalysisArtifact artifact = Artifact(10.0, 1920, 1080, ("e0", 4.0, null));
        string legacy = JsonSerializer.Serialize(
            new { overlays = new[] { new { placementId = "p0", kind = "Title", text = "Hello", subtext = "", duration = "Medium", emphasis = "Normal", reason = "test" } }, planRationale = "test" },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Run old = await RunAsync(artifact, legacy);
        Run now = await RunAsync(artifact, Plan(Overlay("p0", "", "Title")));

        old.Result.Status.Should().Be(StepStatus.Completed);
        old.Graph.Should().NotBeNullOrEmpty().And.Be(now.Graph);
        old.Edl.GetProperty("graphics").GetRawText().Should().Be(now.Edl.GetProperty("graphics").GetRawText());
    }

    [Fact]
    public async Task Overlays_that_cannot_apply_drop_with_a_reason_and_the_rest_still_apply()
    {
        // e1 is at 12 s: the artifact's 10 s edit does not contain it. e9 was never offered.
        VideoAnalysisArtifact artifact = Artifact(10.0, 1920, 1080, ("e0", 4.0, null), ("e1", 12.0, null));
        Run run = await RunAsync(artifact, Plan(
            Overlay("", "e0", "Title", "Kept"),
            Overlay("", "e1", "Title", "Late"),
            Overlay("", "e9", "Title", "Unknown"),
            Overlay("p0", "e0", "Title", "Both"),
            Overlay("", "", "Title", "Neither")));

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);
        JsonElement graphics = run.Edl.GetProperty("graphics");
        graphics.GetProperty("appliedOverlayCount").GetInt32().Should().Be(1);
        graphics.GetProperty("droppedOverlays").EnumerateArray()
            .Select(d => (d.GetProperty("placementId").GetString(), d.GetProperty("reason").GetString()))
            .Should().Equal(("e1", "anchor_cut_away"), ("e9", "unknown_anchor_id"), ("p0", "placement_and_event_both_set"), ("", "unknown_placement_id"));
    }

    [Fact]
    public async Task Two_overlays_on_one_event_keep_their_own_layouts()
    {
        VideoAnalysisArtifact artifact = Artifact(10.0, 1920, 1080, ("e0", 4.0, null));
        Run run = await RunAsync(artifact, Plan(Overlay("", "e0", "LowerThird", "Name"), Overlay("", "e0", "Title", "Heading")));

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);
        run.Edl.GetProperty("graphics").GetProperty("appliedOverlays").EnumerateArray()
            .Select(o => o.GetProperty("placementId").GetString()).Should().Equal("e0", "e0#2");
    }

    [Fact]
    public async Task An_event_overlay_on_a_stream_copy_is_the_same_config_error_as_any_overlay()
    {
        Run run = await RunAsync(
            Artifact(10.0, 1920, 1080, ("e0", 4.0, null)), Plan(Overlay("", "e0", "Title")),
            c => c with { Mode = VideoCompileMode.StreamCopy, AllowKeyframeSnapping = true });

        run.Result.Status.Should().Be(StepStatus.Failed);
        JsonDocument.Parse(run.Result.Output).RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("GRAPHICS_REQUIRE_REENCODE");
    }

    // ---- real ffmpeg end to end ----

    [RealFfmpegFact]
    public async Task Real_ffmpeg_overlay_on_an_event_appears_when_the_event_happens()
    {
        // A 4 s solid black 320x240 clip, an event at 0.5 s, a Title: nothing before it, text in the centre band after, gone after 3 s.
        const int width = 320, height = 240;
        VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
        string work = Path.Combine(Path.GetTempPath(), "reelbolt-eventgfx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string clip = Path.Combine(work, "clip.mp4");
            Ffmpeg(work, "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"color=black:s={width}x{height}:r=30:d=4",
                "-an", "-c:v", "libx264", "-crf", "0", "-pix_fmt", "yuv420p", clip);
            var options = Options.Create(new VideoEditingOptions { ScratchPath = Path.Combine(work, "scratch"), MaxConcurrentJobs = 1, AnalyzeTimeoutSeconds = 60, CompileTimeoutSeconds = 120 });
            var runner = new FfmpegVideoToolRunner(options, NullLogger<FfmpegVideoToolRunner>.Instance);
            string outFile = Path.Combine(work, "out.mp4");

            Run run = await RunAsync(
                Artifact(4.0, width, height, ("e0", 0.5, null)), Plan(Overlay("", "e0", "Title", "EVENT")),
                c => c with { Crf = 0, RegisterProjectFile = true },
                realSource: clip, realRunner: runner, realProbe: new FfprobeMediaProbe(runner, options, NullLogger<FfprobeMediaProbe>.Instance), outputCopy: outFile);

            run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);
            JsonElement graphics = run.Edl.GetProperty("graphics");
            if (graphics.GetProperty("unavailable").GetBoolean())
                return; // this ffmpeg has no drawtext: the compile degrades to no graphics, as designed.

            graphics.GetProperty("appliedOverlayCount").GetInt32().Should().Be(1);
            int Brightest(double atSec)
            {
                string raw = Path.Combine(work, $"f-{atSec:0.0}.gray");
                Ffmpeg(work, "-hide_banner", "-loglevel", "error", "-y", "-ss", atSec.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                    "-i", outFile, "-frames:v", "1", "-pix_fmt", "gray", "-f", "rawvideo", raw);
                byte[] bytes = File.ReadAllBytes(raw);
                bytes.Length.Should().Be(width * height);
                return bytes.Max(b => (int)b);
            }

            Brightest(0.2).Should().BeLessThan(20, "before the event the picture is the untouched black clip");
            Brightest(1.5).Should().BeGreaterThan(100, "after the event the title is drawn");
            Brightest(3.9).Should().BeLessThan(20, "the title leaves after its Medium duration");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void Ffmpeg(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("ffmpeg") { WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, err);
    }
}
