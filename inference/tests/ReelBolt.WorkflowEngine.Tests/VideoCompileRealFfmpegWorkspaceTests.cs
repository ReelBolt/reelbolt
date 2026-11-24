using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// One REAL ffmpeg compile, end to end, through the D9b seam: a synthetic source clip is encoded
/// with the host's own ffmpeg, "downloaded" into the step workspace by the object-store stub, cut by
/// <see cref="VideoCompileStepExecutor"/> through the real <see cref="FfmpegVideoToolRunner"/>, and
/// probed back with the real <see cref="FfprobeMediaProbe"/>.
///
/// <para>
/// <see cref="VideoCompileMediaWorkspaceSeamTests"/> proves the COMMANDS are byte-identical to the
/// pre-D9b ones. This proves the compile still RUNS: the file is playable, H.264/yuv420p, 30 fps, of
/// the expected length, and the whole run — the staged source, the encode's output, its filter
/// script — happens inside the workspace's own session directory.
/// </para>
///
/// <para>
/// With <c>REELBOLT_SEAM_DUMP</c> set to a file path it also writes the produced file's SHA-256 and
/// the full argv (session root rewritten to <c>{session}</c>) — how the D9b report's before/after
/// comparison was taken: the same fixture, the pre-D9b tree and the post-D9b tree. Off by default.
/// </para>
///
/// <para>No-ops on a host without ffmpeg/ffprobe on PATH.</para>
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class VideoCompileRealFfmpegWorkspaceTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private const string AnalysisKey = "projects/p/agentFiles/video-analysis/e/step-1-analysis.json";
    private const string SourceVideoKey = "projects/p/outputFiles/e/render.mp4";

    [Fact]
    public async Task A_real_ffmpeg_compile_produces_the_expected_program_entirely_inside_the_workspace()
    {
        if (!ToolOnPath("ffmpeg") || !ToolOnPath("ffprobe"))
            return; // a host without the toolchain: the argv/parity tests still cover the port

        VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
        VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();

        Guid executionId = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "video-compile-real-ffmpeg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string sourceFile = Path.Combine(root, "source.mp4");

        var options = Options.Create(new VideoEditingOptions
        {
            ScratchPath = Path.Combine(root, "scratch"),
            MaxConcurrentJobs = 1,
            AnalyzeTimeoutSeconds = 120,
            CompileTimeoutSeconds = 300
        });

        var real = new FfmpegVideoToolRunner(options, NullLogger<FfmpegVideoToolRunner>.Instance);

        try
        {
            VideoToolResult generated = await real.RunFfmpegAsync(
                [
                    "-nostdin", "-hide_banner", "-y", "-loglevel", "error",
                    "-f", "lavfi", "-i", "testsrc=size=320x240:rate=30:duration=6",
                    "-f", "lavfi", "-i", "sine=frequency=440:duration=6",
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "veryfast", "-crf", "20",
                    "-c:a", "aac", "-shortest",
                    sourceFile
                ],
                TimeSpan.FromMinutes(2), CancellationToken.None);
            generated.Succeeded.Should().BeTrue("the fixture is built with the host's own ffmpeg: " + generated.StdErr);

            VideoAnalysisArtifact artifact = new(
                Version: 1,
                Media: new VideoAnalysisMedia(6.0, 30, 1, 320, 240),
                Shots: [new VideoAnalysisShot("s0", 0.0, 6.0)],
                SilenceSpans: [],
                Segments: [],
                Words: [],
                OfferedIds: ["s0"],
                Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
                Placements: null,
                OfferedPlacementIds: null);
            string artifactJson = JsonSerializer.Serialize(artifact, JsonOptions());
            string decisionJson = JsonSerializer.Serialize(
                new { keep = new[] { new { fromId = "s0", toId = "s0", reason = "keep" } }, editRationale = "test", suggestedTitle = "Test" },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var projectFiles = new Mock<IProjectFileWorkspace>();
            projectFiles
                .Setup(w => w.DownloadStorageKeyToFileAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<Guid, string, string, CancellationToken>((_, key, destination, _) =>
                {
                    if (key == AnalysisKey)
                        File.WriteAllText(destination, artifactJson);
                    else
                        File.Copy(sourceFile, destination, overwrite: true);
                    return Task.CompletedTask;
                });
            projectFiles
                .Setup(w => w.UploadArtifactAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid pid, string _, string fileName, string _, CancellationToken _, string category) =>
                    "projects/" + pid + "/" + category + "/" + fileName);

            // The executor disposes its scratch directory in a finally, by design — so the
            // recording runner keeps a copy of whatever a successful ffmpeg call actually wrote,
            // which is the only way to inspect the produced program from outside the step.
            string preserved = Path.Combine(root, "preserved");
            var recorder = new RecordingRunner(real, preserved);
            var probe = new FfprobeMediaProbe(real, options, NullLogger<FfprobeMediaProbe>.Instance);

            VideoCompileStepConfig config = new VideoCompileStepConfig(
                Version: 1,
                Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
                AnalysisStepOrder: 1) with
            {
                Mode = VideoCompileMode.Reencode,
                RegisterProjectFile = false,
                LoudnessTarget = ProgramLoudness.Off
            };

            var step = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                StepOrder = 3,
                StepType = StepType.VideoCompile,
                VideoCompileConfigJson = JsonSerializer.Serialize(config, JsonOptions())
            };
            var analyzeStep = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                StepOrder = 1,
                StepType = StepType.VideoAnalyze,
                VideoAnalyzeConfigJson = JsonSerializer.Serialize(
                    new VideoAnalyzeStepConfig(Version: 1, Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput)),
                    JsonOptions())
            };

            var context = new StepExecutionContext
            {
                Execution = new WorkflowExecution { Id = executionId, ProjectId = ProjectId },
                Step = step,
                AllSteps = [analyzeStep, step],
                AccumulatedOutput = decisionJson,
                StepOutputHistory =
                [
                    new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null),
                    new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
                    new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null)
                ],
                CurrentStepIndex = 2,
                IterationCount = 0,
                CorrelationId = "test",
                CancellationToken = CancellationToken.None
            };

            var executor = new VideoCompileStepExecutor(
                recorder, probe, projectFiles.Object, new Mock<IServiceScopeFactory>().Object,
                options, NullLogger<VideoCompileStepExecutor>.Instance);

            string sessionRoot = Path.GetFullPath(Path.Combine(
                Path.GetFullPath(options.Value.ScratchPath), executionId.ToString("D"), step.Id.ToString("D")));
            string expectedOutput = Path.Combine(sessionRoot, "edited.mp4");

            StepExecutionResult result = await executor.ExecuteAsync(context);
            result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

            // The real encode ran over the staged SOURCE inside the session directory the workspace
            // owns, and wrote the program to exactly the path the workspace materialized.
            List<string> encode = recorder.Commands.Last(a => a.Contains(expectedOutput)).ToList();
            int inputIndex = encode.IndexOf("-i");
            inputIndex.Should().BeGreaterThan(-1);
            encode[inputIndex + 1].Should().StartWith(sessionRoot + Path.DirectorySeparatorChar);
            encode[inputIndex + 1].Should().Be(Path.Combine(sessionRoot, "source-0.mp4"));
            encode[^1].Should().Be(expectedOutput);

            // ...and the file really was produced there (carried out of the scratch directory
            // before the step's own cleanup deleted it).
            string produced = Path.Combine(preserved, "edited.mp4");
            File.Exists(produced).Should().BeTrue();

            // ffprobe-equal to what the pre-D9b pipeline produced: the same encoder settings and the
            // same canvas, read back off the FILE rather than asserted from the argv.
            MediaProbeResult output = await probe.ProbeAsync(produced, CancellationToken.None);
            output.VideoCodec.Should().Be("h264");
            output.AudioCodec.Should().Be("aac");
            output.PixFmt.Should().Be("yuv420p");
            output.Width.Should().Be(320);
            output.Height.Should().Be(240);
            output.FpsNum.Should().Be(30);
            output.FpsDen.Should().Be(1);
            output.DurationSec.Should().BeApproximately(6.0, 0.25);

            // Opt-in provenance for the report's before/after comparison.
            string? dumpPath = Environment.GetEnvironmentVariable("REELBOLT_SEAM_DUMP");
            if (!string.IsNullOrWhiteSpace(dumpPath))
            {
                string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(produced))).ToLowerInvariant();
                File.WriteAllText(dumpPath, JsonSerializer.Serialize(new
                {
                    sha256 = hash,
                    bytes = new FileInfo(produced).Length,
                    commands = recorder.Commands
                        .Select(a => a.Select(x => x.Replace(sessionRoot, "{session}", StringComparison.Ordinal)).ToList())
                        .ToList()
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally
        {
            VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
            VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();
            TryDelete(root);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Regression (#340): a motion-graphics overlay whose rendered VP9/WebM asset reports its alpha
    // only as tags.ALPHA_MODE="1" (what a plain stream-copy remux writes) must be composited into
    // the REAL compiled program — not dropped with asset_missing_alpha_channel — and be visible in
    // the produced file's pixels. A genuinely opaque asset is still dropped.
    // ---------------------------------------------------------------------------------------------

    private const int OverlayCanvasW = 320;
    private const int OverlayCanvasH = 240;

    [FfmpegToolsFact]
    public async Task A_remuxed_webm_overlay_tagged_ALPHA_MODE_is_composited_into_the_real_compiled_program()
    {
        using OverlayCompileRun run = await CompileWithOverlayAsync(transparentRemuxedAsset: true);

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);

        JsonElement graphics = run.Edl.GetProperty("graphics");
        graphics.GetProperty("droppedOverlays").EnumerateArray()
            .Select(d => d.GetProperty("reason").GetString()).Should().BeEmpty(
                "the ALPHA_MODE-tagged asset carries real alpha and must not be dropped");
        graphics.GetProperty("applied").GetBoolean().Should().BeTrue();
        graphics.GetProperty("appliedOverlayCount").GetInt32().Should().Be(1);

        // Where the encode actually drew the asset, read off the real argv (never re-derived).
        string filter = run.EncodeFilterComplex();
        System.Text.RegularExpressions.Match scale = System.Text.RegularExpressions.Regex.Match(filter, @"format=rgba,scale=(\d+):(\d+)");
        System.Text.RegularExpressions.Match place = System.Text.RegularExpressions.Regex.Match(filter, @"overlay=x=(\d+):y=(\d+)");
        scale.Success.Should().BeTrue(filter);
        place.Success.Should().BeTrue(filter);
        int boxW = int.Parse(scale.Groups[1].Value, CultureInfo.InvariantCulture);
        int boxH = int.Parse(scale.Groups[2].Value, CultureInfo.InvariantCulture);
        int boxX = int.Parse(place.Groups[1].Value, CultureInfo.InvariantCulture);
        int boxY = int.Parse(place.Groups[2].Value, CultureInfo.InvariantCulture);
        boxW.Should().BeGreaterThan(80);
        boxH.Should().BeGreaterThan(60);

        byte[] frame = await run.DecodeFrameRgbAsync(Path.Combine(run.Preserved, "edited.mp4"), 1.5);
        byte[] source = await run.DecodeFrameRgbAsync(run.SourceFile, 1.5);

        // The asset's opaque red body sits in the middle of its box: there the output is red,
        // clearly not the blue source.
        (int R, int G, int B) src = Pixel(source, boxX + boxW / 2, boxY + boxH / 2);
        src.B.Should().BeGreaterThan(150, "the fixture source is solid blue");
        src.R.Should().BeLessThan(80);
        (int R, int G, int B) inside = Pixel(frame, boxX + boxW / 2, boxY + boxH / 2);
        inside.R.Should().BeGreaterThan(180, $"the overlay's opaque red body must show in the output (got {inside})");
        inside.B.Should().BeLessThan(80, $"the overlay's opaque red body must cover the blue source (got {inside})");

        // The asset's transparent margin, still inside its box: the source shows through, so the
        // overlay was composited WITH its alpha, not as an opaque rectangle.
        (int R, int G, int B) margin = Pixel(frame, boxX + 2 + boxW / 16, boxY + 2 + boxH / 16);
        margin.B.Should().BeGreaterThan(150, $"the asset's transparent margin must let the blue source through (got {margin})");
        margin.R.Should().BeLessThan(80, $"the asset's transparent margin must not be painted (got {margin})");
    }

    [FfmpegToolsFact]
    public async Task An_opaque_webm_overlay_is_still_dropped_with_asset_missing_alpha_channel_in_a_real_compile()
    {
        using OverlayCompileRun run = await CompileWithOverlayAsync(transparentRemuxedAsset: false);

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);

        JsonElement graphics = run.Edl.GetProperty("graphics");
        graphics.GetProperty("applied").GetBoolean().Should().BeFalse();
        graphics.GetProperty("appliedOverlayCount").GetInt32().Should().Be(0);
        JsonElement[] dropped = graphics.GetProperty("droppedOverlays").EnumerateArray().ToArray();
        dropped.Should().HaveCount(1);
        dropped[0].GetProperty("placementId").GetString().Should().Be("p0");
        dropped[0].GetProperty("reason").GetString().Should().Be("asset_missing_alpha_channel");
    }

    private static (int R, int G, int B) Pixel(byte[] rgb, int x, int y)
    {
        int i = (y * OverlayCanvasW + x) * 3;
        return (rgb[i], rgb[i + 1], rgb[i + 2]);
    }

    private static async Task<OverlayCompileRun> CompileWithOverlayAsync(bool transparentRemuxedAsset)
    {
        VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
        VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();

        Guid executionId = Guid.NewGuid();
        string root = WebmAlphaFixtures.NewTempRoot();
        IOptions<VideoEditingOptions> options = WebmAlphaFixtures.Options(root);
        var real = new FfmpegVideoToolRunner(options, NullLogger<FfmpegVideoToolRunner>.Instance);
        var run = new OverlayCompileRun(root, real);
        try
        {
            // Solid blue source, so a red overlay pixel is unambiguous.
            VideoToolResult generated = await real.RunFfmpegAsync(
                [
                    "-nostdin", "-hide_banner", "-y", "-loglevel", "error",
                    "-f", "lavfi", "-i", $"color=c=blue:s={OverlayCanvasW}x{OverlayCanvasH}:r=30:d=4",
                    "-f", "lavfi", "-i", "sine=frequency=440:duration=4",
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "veryfast", "-crf", "18",
                    "-c:a", "aac", "-shortest",
                    run.SourceFile
                ],
                TimeSpan.FromMinutes(2), CancellationToken.None);
            generated.Succeeded.Should().BeTrue("the fixture is built with the host's own ffmpeg: " + generated.StdErr);

            // 960x720 asset, designed for a 1920-wide canvas, so it lands as a 160x120 box on this
            // 320x240 one: transparent, with an opaque red body over its middle half.
            string assetFile = Path.Combine(root, "overlay.webm");
            if (transparentRemuxedAsset)
            {
                string encoded = Path.Combine(root, "overlay-encoded.webm");
                await WebmAlphaFixtures.EncodeTransparentWebmAsync(real, encoded, width: 960, height: 720, durationSec: 3);
                await WebmAlphaFixtures.RemuxStreamCopyAsync(real, encoded, assetFile);
            }
            else
            {
                await WebmAlphaFixtures.EncodeOpaqueWebmAsync(real, assetFile, width: 960, height: 720, durationSec: 3);
            }

            var placement = new VideoAnalysisPlacement(
                "p0", "s0", "CenterBand", new VideoAnalysisRect(0.25, 0.25, 0.5, 0.5),
                "ShotMiddle", 0.5, 3.5, 0.9, "Light");
            VideoAnalysisArtifact artifact = new(
                Version: 1,
                Media: new VideoAnalysisMedia(4.0, 30, 1, OverlayCanvasW, OverlayCanvasH),
                Shots: [new VideoAnalysisShot("s0", 0.0, 4.0)],
                SilenceSpans: [],
                Segments: [],
                Words: [],
                OfferedIds: ["s0"],
                Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
                Placements: [placement],
                OfferedPlacementIds: ["p0"]);
            string artifactJson = JsonSerializer.Serialize(artifact, JsonOptions());
            string decisionJson = JsonSerializer.Serialize(
                new { keep = new[] { new { fromId = "s0", toId = "s0", reason = "keep" } }, editRationale = "test", suggestedTitle = "Test" },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            // The rendered asset lives under THIS execution's own outputFiles prefix, which is the
            // only place the executor accepts it from.
            string overlayStorageKey = $"projects/{ProjectId}/outputFiles/{executionId:D}/overlay.webm";
            string graphicsPlanJson = JsonSerializer.Serialize(new
            {
                overlays = new[]
                {
                    new { placementId = "p0", kind = "CenterBand", text = "", subtext = "", duration = "Medium", emphasis = "Normal", renderedAssetStorageKey = overlayStorageKey, reason = "designed graphic" }
                },
                planRationale = "test"
            });

            var projectFiles = new Mock<IProjectFileWorkspace>();
            projectFiles
                .Setup(w => w.DownloadStorageKeyToFileAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<Guid, string, string, CancellationToken>((_, key, destination, _) =>
                {
                    if (key == AnalysisKey)
                        File.WriteAllText(destination, artifactJson);
                    else if (key == overlayStorageKey)
                        File.Copy(assetFile, destination, overwrite: true);
                    else
                        File.Copy(run.SourceFile, destination, overwrite: true);
                    return Task.CompletedTask;
                });
            projectFiles
                .Setup(w => w.UploadArtifactAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .Callback<Guid, string, string, string, CancellationToken, string>((_, path, _, _, _, _) =>
                {
                    if (path.EndsWith("edl.json", StringComparison.OrdinalIgnoreCase))
                        run.Edl = JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
                })
                .ReturnsAsync((Guid pid, string _, string fileName, string _, CancellationToken _, string category) =>
                    "projects/" + pid + "/" + category + "/" + fileName);

            var probe = new FfprobeMediaProbe(real, options, NullLogger<FfprobeMediaProbe>.Instance);

            VideoCompileStepConfig config = new VideoCompileStepConfig(
                Version: 1,
                Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
                AnalysisStepOrder: 1,
                EnableGraphics: true,
                GraphicsPlan: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 3)) with
            {
                Mode = VideoCompileMode.Reencode,
                RegisterProjectFile = false,
                LoudnessTarget = ProgramLoudness.Off
            };

            var step = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                StepOrder = 4,
                StepType = StepType.VideoCompile,
                VideoCompileConfigJson = JsonSerializer.Serialize(config, JsonOptions())
            };
            var analyzeStep = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                StepOrder = 1,
                StepType = StepType.VideoAnalyze,
                VideoAnalyzeConfigJson = JsonSerializer.Serialize(
                    new VideoAnalyzeStepConfig(Version: 1, Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput)),
                    JsonOptions())
            };

            var context = new StepExecutionContext
            {
                Execution = new WorkflowExecution { Id = executionId, ProjectId = ProjectId },
                Step = step,
                AllSteps = [analyzeStep, step],
                AccumulatedOutput = graphicsPlanJson,
                StepOutputHistory =
                [
                    new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null),
                    new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
                    new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null),
                    new StepOutputHistoryEntry(3, "MotionGraphicsPlanner", graphicsPlanJson, OutputStorageKey: null, ArtifactStorageKey: null)
                ],
                CurrentStepIndex = 3,
                IterationCount = 0,
                CorrelationId = "test",
                CancellationToken = CancellationToken.None
            };

            var executor = new VideoCompileStepExecutor(
                run.Recorder, probe, projectFiles.Object, new Mock<IServiceScopeFactory>().Object,
                options, NullLogger<VideoCompileStepExecutor>.Instance);

            run.Result = await executor.ExecuteAsync(context);
            return run;
        }
        catch
        {
            run.Dispose();
            throw;
        }
    }

    /// <summary>One real overlay compile's inputs and outputs, and the temp dir it owns.</summary>
    private sealed class OverlayCompileRun : IDisposable
    {
        private readonly string _root;
        private readonly IVideoToolRunner _real;

        public OverlayCompileRun(string root, IVideoToolRunner real)
        {
            _root = root;
            _real = real;
            Preserved = Path.Combine(root, "preserved");
            SourceFile = Path.Combine(root, "source.mp4");
            Recorder = new RecordingRunner(real, Preserved);
        }

        public RecordingRunner Recorder { get; }
        public string Preserved { get; }
        public string SourceFile { get; }
        public StepExecutionResult Result { get; set; } = null!;
        public JsonElement Edl { get; set; }

        /// <summary>The inline filter graph of the program encode (the call that wrote edited.mp4).</summary>
        public string EncodeFilterComplex()
        {
            List<string> encode = Recorder.Commands
                .Last(a => a.Count > 0 && a[^1].EndsWith("edited.mp4", StringComparison.Ordinal))
                .ToList();
            int i = encode.IndexOf("-filter_complex");
            i.Should().BeGreaterThan(-1, "a one-span compile passes its filter graph inline: " + string.Join(' ', encode));
            return encode[i + 1];
        }

        public async Task<byte[]> DecodeFrameRgbAsync(string file, double atSec)
        {
            File.Exists(file).Should().BeTrue(file);
            string raw = Path.Combine(_root, $"frame-{Guid.NewGuid():N}.rgb");
            VideoToolResult decoded = await _real.RunFfmpegAsync(
                [
                    "-nostdin", "-hide_banner", "-y", "-loglevel", "error",
                    "-ss", atSec.ToString(CultureInfo.InvariantCulture), "-i", file,
                    "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", raw
                ],
                TimeSpan.FromMinutes(1), CancellationToken.None);
            decoded.Succeeded.Should().BeTrue(decoded.StdErr);
            byte[] bytes = File.ReadAllBytes(raw);
            bytes.Length.Should().Be(OverlayCanvasW * OverlayCanvasH * 3);
            return bytes;
        }

        public void Dispose()
        {
            VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
            VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();
            TryDelete(_root);
        }
    }

    private static bool ToolOnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, tool)));

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover scratch directory is not a test failure.
        }
    }

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Records every invocation while delegating to the real runner, and — because the executor
    /// deletes its whole scratch directory when the step ends — carries any file a successful
    /// ffmpeg call actually produced out to <c>preservedDirectory</c> first.
    /// </summary>
    internal sealed class RecordingRunner : IVideoToolRunner
    {
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority)
            => RunFfmpegAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(
            IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine, VideoToolPriority priority)
            => RunFfmpegAsync(args, timeout, ct, onStdOutLine);

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority)
            => RunFfprobeAsync(args, timeout, ct);

        private readonly IVideoToolRunner _inner;
        private readonly string _preservedDirectory;

        public RecordingRunner(IVideoToolRunner inner, string preservedDirectory)
        {
            _inner = inner;
            _preservedDirectory = preservedDirectory;
        }

        public List<IReadOnlyList<string>> Commands { get; } = new();

        public async Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
        {
            Commands.Add(args.ToList());
            VideoToolResult result = await _inner.RunFfmpegAsync(args, timeout, ct);
            Preserve(args, result);
            return result;
        }

        public async Task<VideoToolResult> RunFfmpegAsync(
            IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine)
        {
            Commands.Add(args.ToList());
            VideoToolResult result = await _inner.RunFfmpegAsync(args, timeout, ct, onStdOutLine);
            Preserve(args, result);
            return result;
        }

        private void Preserve(IReadOnlyList<string> args, VideoToolResult result)
        {
            if (!result.Succeeded || args.Count == 0)
                return;

            string produced = args[^1];
            if (!produced.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || !File.Exists(produced))
                return;

            Directory.CreateDirectory(_preservedDirectory);
            File.Copy(produced, Path.Combine(_preservedDirectory, Path.GetFileName(produced)), overwrite: true);
        }

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
        {
            Commands.Add(args.ToList());
            return _inner.RunFfprobeAsync(args, timeout, ct);
        }

        public Task<VideoToolResult> RunFfmpegAsync(
            IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
            workspace.RunFfmpegAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(
            IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct,
            Action<string> onStdOutLine) =>
            workspace.RunFfmpegAsync(args, timeout, ct, onStdOutLine);
    }
}
