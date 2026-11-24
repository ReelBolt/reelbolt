using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// T-012: pictures and videos over the film, proven on the REAL ffmpeg with repo-owned fixtures
/// only (docs/design/picture-overlay.md §12). A synthetic 6 s 640x360 blue program with a 440 Hz
/// tone is built in the test; the committed fixtures are an 800x1000 colour-block card
/// (<c>card.png</c>) and a 2 s 320x240 flat-colour clip with a 4 kHz tone (<c>inset.mp4</c>), both
/// produced by <c>TestData/picture-overlay/make-fixtures.sh</c>.
///
/// <para>
/// The test reads pixels back out of the encoded program and measures audio with
/// <c>volumedetect</c>, so it proves the composite rather than the argv. The program tone is 440 Hz
/// and the overlay's is 4 kHz, so a low-pass isolates the film's own audio (the card-mute case) and
/// a high-pass isolates the mixed overlay (the Mixed case). No customer media, no storage bucket,
/// no network.
/// </para>
///
/// <para>Skips, rather than fails, on a host without ffmpeg/ffprobe (see <see cref="FfmpegToolsFactAttribute"/>).</para>
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class VideoCompileMediaOverlayRealFfmpegTests
{
    private const int CanvasWidth = 640;
    private const int CanvasHeight = 360;
    private const double ProgramSeconds = 6.0;

    private static readonly Guid ProjectId = Guid.NewGuid();

    // ==========================================================================================
    // Card + inset on the real program
    // ==========================================================================================

    [FfmpegToolsFact]
    public async Task A_card_and_a_half_opacity_inset_composite_onto_the_real_program()
    {
        (Guid cardId, Guid insetId) = (Guid.NewGuid(), Guid.NewGuid());
        var card = new OverlaySpec(cardId, "card.png", "image/png", Path.Combine(Fixtures, "card.png"));
        var inset = new OverlaySpec(insetId, "inset.mp4", "video/mp4", Path.Combine(Fixtures, "inset.mp4"));

        using MediaOverlayRun run = await MediaOverlayRun.CompileAsync(
            [card, inset],
            config => config with
            {
                MediaOverlays =
                [
                    new VideoMediaOverlay(
                        new InsertContentRef(InsertContentRef.ProjectFileKind, ProjectFileId: cardId),
                        Form: MediaOverlayForm.Card,
                        StartSec: 1,
                        EndSec: 3,
                        CardAudio: MediaOverlayCardAudio.Mute),
                    new VideoMediaOverlay(
                        new InsertContentRef(InsertContentRef.ProjectFileKind, ProjectFileId: insetId),
                        Form: MediaOverlayForm.PictureInPicture,
                        Anchor: MediaOverlayAnchor.BottomRight,
                        Size: MediaOverlaySize.Medium,
                        OpacityPercent: 50,
                        Border: MediaOverlayBorder.Light,
                        StartSec: 2,
                        EndSec: 6,
                        Audio: MediaOverlayAudio.Mixed,
                        Volume: MediaOverlayVolume.Balanced)
                ]
            });

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);

        // ---- The reported node (§6.4): times, form, opacity, card audio, overlay audio. ----
        JsonObject node = run.MediaOverlays;
        node["enabled"]!.GetValue<bool>().Should().BeTrue();
        node["applied"]!.GetValue<bool>().Should().BeTrue();
        node["appliedCount"]!.GetValue<int>().Should().Be(2);
        JsonArray items = node["items"]!.AsArray();

        JsonObject cardItem = items[0]!.AsObject();
        cardItem["form"]!.GetValue<string>().Should().Be("Card");
        cardItem["startSec"]!.GetValue<double>().Should().BeApproximately(1, 0.001);
        cardItem["endSec"]!.GetValue<double>().Should().BeApproximately(3, 0.001);
        cardItem["opacityPercent"]!.GetValue<int>().Should().Be(100);
        cardItem["cardAudio"]!.GetValue<string>().Should().Be("Mute");
        cardItem["endedEarly"]!.GetValue<bool>().Should().BeFalse();

        JsonObject insetItem = items[1]!.AsObject();
        insetItem["form"]!.GetValue<string>().Should().Be("PictureInPicture");
        insetItem["startSec"]!.GetValue<double>().Should().BeApproximately(2, 0.001);
        insetItem["endSec"]!.GetValue<double>().Should().BeApproximately(6, 0.001);
        insetItem["opacityPercent"]!.GetValue<int>().Should().Be(50);
        insetItem["audio"]!.GetValue<string>().Should().Be("Mixed");
        insetItem["audioLevel"]!.GetValue<string>().Should().Be("Balanced");
        insetItem["endedEarly"]!.GetValue<bool>().Should().BeFalse();
        insetItem["border"]!.GetValue<string>().Should().Be("Light");

        // ---- Picture: the card's green inside its span, the blue film outside it. ----
        string produced = run.ProducedFile;
        AssertColour(
            await run.PixelAsync(produced, 1.5, CanvasWidth / 2, CanvasHeight / 2),
            0, 255, 0, tolerance: 30, "the card's centre block fills the frame at 1.5 s");
        AssertColour(
            await run.PixelAsync(produced, 0.5, CanvasWidth / 2, CanvasHeight / 2),
            0, 0, 255, tolerance: 30, "the blue program shows at 0.5 s, before the card");

        // ---- Picture: the inset at 50 % opacity is half its red and half the blue film. ----
        JsonObject box = insetItem["box"]!.AsObject();
        int boxX = box["x"]!.GetValue<int>();
        int boxY = box["y"]!.GetValue<int>();
        int boxW = box["w"]!.GetValue<int>();
        int boxH = box["h"]!.GetValue<int>();
        int sampleX = boxX + boxW / 2;
        int sampleY = boxY + boxH / 2;

        // At 4.5 s the card has ended, so the inset blends with the blue film; the clip's first
        // second (red 0xD02020) is playing at content time 0.5 s through the loop.
        AssertColour(
            await run.PixelAsync(produced, 4.5, sampleX, sampleY),
            (0xD0 + 0) / 2, (0x20 + 0) / 2, (0x20 + 255) / 2,
            tolerance: 45, "the inset is red at 50 % over the blue film");

        // The Light border is white at 50 % over the film, one pixel above the content box. Its
        // green channel (≈127) is the tell: the film's is 0 and the red content's is ≈16.
        (int R, int G, int B) border = await run.PixelAsync(produced, 4.5, sampleX, boxY - 1);
        border.G.Should().BeGreaterThan(60, $"the white border must show above the inset (got {border})");
        border.B.Should().BeGreaterThan(120, $"the border sits over the blue film (got {border})");
        border.R.Should().BeGreaterThan(60, $"the white border must lift the red channel (got {border})");

        // ---- Audio: the muted card silences the program; it is audible outside the card. ----
        double beforeCard = await run.MeanVolumeDbAsync(produced, 0.3, 0.4, LowPass);
        beforeCard.Should().BeGreaterThan(-40, "the program's own 440 Hz tone is audible before the card");
        double insideMutedCard = await run.MeanVolumeDbAsync(produced, 1.5, 0.4, LowPass);
        insideMutedCard.Should().BeLessThan(-60, "CardAudio=Mute silences the program under the card");
        double afterCard = await run.MeanVolumeDbAsync(produced, 3.5, 0.4, LowPass);
        afterCard.Should().BeGreaterThan(-40, "the program audio is restored after the muted card");

        // ---- Audio: the mixed overlay's 4 kHz tone is present in its span at Balanced. ----
        double overlayOutside = await run.MeanVolumeDbAsync(produced, 0.3, 0.4, HighPass);
        overlayOutside.Should().BeLessThan(-45, "no overlay audio before the inset's span");
        double overlayInside = await run.MeanVolumeDbAsync(produced, 4.0, 0.4, HighPass);
        overlayInside.Should().BeGreaterThan(-38, "the mixed overlay audio is present in the span");
        overlayInside.Should().BeLessThan(-18, "the mixed overlay is gained to Balanced (-6 dB), not passed through");
    }

    // ==========================================================================================
    // PlayOnce
    // ==========================================================================================

    [FfmpegToolsFact]
    public async Task A_play_once_inset_ends_early_and_the_film_shows_after_it()
    {
        Guid insetId = Guid.NewGuid();
        var inset = new OverlaySpec(insetId, "inset.mp4", "video/mp4", Path.Combine(Fixtures, "inset.mp4"));

        using MediaOverlayRun run = await MediaOverlayRun.CompileAsync(
            [inset],
            config => config with
            {
                MediaOverlays =
                [
                    new VideoMediaOverlay(
                        new InsertContentRef(InsertContentRef.ProjectFileKind, ProjectFileId: insetId),
                        Form: MediaOverlayForm.PictureInPicture,
                        Anchor: MediaOverlayAnchor.BottomRight,
                        Size: MediaOverlaySize.Medium,
                        StartSec: 1,
                        EndSec: 4,
                        Loop: MediaOverlayLoop.PlayOnce,
                        Audio: MediaOverlayAudio.Muted)
                ]
            },
            clipSeconds: 0.5,
            clipColour: "red");

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);

        JsonObject item = run.MediaOverlays["items"]!.AsArray()[0]!.AsObject();
        item["endedEarly"]!.GetValue<bool>().Should().BeTrue("a 0.5 s PlayOnce clip over 1–4 s ends early");
        item["startSec"]!.GetValue<double>().Should().BeApproximately(1, 0.001);
        item["endSec"]!.GetValue<double>().Should().BeApproximately(4, 0.001);
        item["contentDurationSec"]!.GetValue<double>().Should().BeApproximately(0.5, 0.01);

        JsonObject box = item["box"]!.AsObject();
        int sampleX = box["x"]!.GetValue<int>() + box["w"]!.GetValue<int>() / 2;
        int sampleY = box["y"]!.GetValue<int>() + box["h"]!.GetValue<int>() / 2;

        // While the clip plays the red inset is in its box; after its own end the film shows
        // through (eof_action=pass), all the way to the window's 4 s end.
        AssertColour(
            await run.PixelAsync(run.ProducedFile, 1.2, sampleX, sampleY),
            255, 0, 0, tolerance: 35, "the 0.5 s red clip is visible at 1.2 s");
        AssertColour(
            await run.PixelAsync(run.ProducedFile, 2.5, sampleX, sampleY),
            0, 0, 255, tolerance: 30, "after the clip ends the blue film shows through");
        AssertColour(
            await run.PixelAsync(run.ProducedFile, 3.8, sampleX, sampleY),
            0, 0, 255, tolerance: 30, "the film still shows near the window's end");
    }

    // ==========================================================================================
    // CardAudio Keep
    // ==========================================================================================

    [FfmpegToolsFact]
    public async Task A_card_with_CardAudio_Keep_leaves_the_program_audio_audible()
    {
        Guid cardId = Guid.NewGuid();
        var card = new OverlaySpec(cardId, "card.png", "image/png", Path.Combine(Fixtures, "card.png"));

        using MediaOverlayRun run = await MediaOverlayRun.CompileAsync(
            [card],
            config => config with
            {
                MediaOverlays =
                [
                    new VideoMediaOverlay(
                        new InsertContentRef(InsertContentRef.ProjectFileKind, ProjectFileId: cardId),
                        Form: MediaOverlayForm.Card,
                        StartSec: 1,
                        EndSec: 3,
                        CardAudio: MediaOverlayCardAudio.Keep)
                ]
            });

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);

        JsonObject item = run.MediaOverlays["items"]!.AsArray()[0]!.AsObject();
        item["cardAudio"]!.GetValue<string>().Should().Be("Keep");

        // The film's 440 Hz tone keeps playing under the card, exactly as it does outside it.
        double beforeCard = await run.MeanVolumeDbAsync(run.ProducedFile, 0.3, 0.4, LowPass);
        double insideCard = await run.MeanVolumeDbAsync(run.ProducedFile, 1.5, 0.4, LowPass);
        beforeCard.Should().BeGreaterThan(-40);
        insideCard.Should().BeGreaterThan(-40, "CardAudio=Keep leaves the program audio audible under the card");
        Math.Abs(insideCard - beforeCard).Should().BeLessThan(6, "Keep does not change the program's level");
    }

    // ==========================================================================================
    // Fixtures
    // ==========================================================================================

    private static readonly Lazy<string> FixtureDirectory = new(LocateFixtures);

    private static string Fixtures => FixtureDirectory.Value;

    /// <summary>
    /// The fixtures are committed next to this test's source, not copied to the output directory
    /// (the test project has no wildcard for this folder), so walk up from the test binary and the
    /// working directory to the project that owns <c>TestData/picture-overlay</c>.
    /// </summary>
    private static string LocateFixtures()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? directory = new(start);
            while (directory is not null)
            {
                string candidate = Path.Combine(directory.FullName, "TestData", "picture-overlay");
                if (File.Exists(Path.Combine(candidate, "card.png")) &&
                    File.Exists(Path.Combine(candidate, "inset.mp4")))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "TestData/picture-overlay fixtures were not found; run make-fixtures.sh in that directory.");
    }

    private static void AssertColour(
        (int R, int G, int B) actual, int r, int g, int b, int tolerance, string because)
    {
        Math.Abs(actual.R - r).Should().BeLessThanOrEqualTo(tolerance, $"red channel — {because} (got {actual})");
        Math.Abs(actual.G - g).Should().BeLessThanOrEqualTo(tolerance, $"green channel — {because} (got {actual})");
        Math.Abs(actual.B - b).Should().BeLessThanOrEqualTo(tolerance, $"blue channel — {because} (got {actual})");
    }

    // 440 Hz program tone passes; 4 kHz overlay tone is attenuated.
    private const string LowPass = "lowpass=f=1500,volumedetect";

    // 4 kHz overlay tone passes; 440 Hz program tone is attenuated.
    private const string HighPass = "highpass=f=2000,volumedetect";

    private sealed record OverlaySpec(Guid Id, string Name, string Mime, string FixturePath);

    // ==========================================================================================
    // One real compile, end to end
    // ==========================================================================================

    private sealed class MediaOverlayRun : IDisposable
    {
        private const string AnalysisArtifactPath = "projects/p/agentFiles/video-analysis/e/step-1-analysis.json";

        private readonly string _root;
        private readonly IVideoToolRunner _real;

        private MediaOverlayRun(string root, IVideoToolRunner real, string programFile, RecordingRunner recorder)
        {
            _root = root;
            _real = real;
            ProgramFile = programFile;
            Recorder = recorder;
        }

        public string ProgramFile { get; }

        public RecordingRunner Recorder { get; }

        public StepExecutionResult Result { get; private set; } = null!;

        public JsonObject MediaOverlays => JsonNode.Parse(Result.Output)!.AsObject()["mediaOverlays"]!.AsObject();

        public string ProducedFile => Path.Combine(Recorder.Preserved, "edited.mp4");

        public static async Task<MediaOverlayRun> CompileAsync(
            IReadOnlyList<OverlaySpec> overlays,
            Func<VideoCompileStepConfig, VideoCompileStepConfig> configure,
            double? clipSeconds = null,
            string? clipColour = null)
        {
            VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
            VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();

            Guid executionId = Guid.NewGuid();
            string root = Path.Combine(Path.GetTempPath(), "video-compile-media-overlay-real-ffmpeg", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var options = Options.Create(new VideoEditingOptions
            {
                ScratchPath = Path.Combine(root, "scratch"),
                MaxConcurrentJobs = 1,
                AnalyzeTimeoutSeconds = 120,
                CompileTimeoutSeconds = 300
            });
            var real = new FfmpegVideoToolRunner(options, NullLogger<FfmpegVideoToolRunner>.Instance);
            string preserved = Path.Combine(root, "preserved");
            var recorder = new RecordingRunner(real, preserved);

            string programFile = Path.Combine(root, "program.mp4");
            VideoToolResult generated = await real.RunFfmpegAsync(
                [
                    "-nostdin", "-hide_banner", "-y", "-loglevel", "error",
                    "-f", "lavfi", "-i", $"color=c=blue:s={CanvasWidth}x{CanvasHeight}:r=30:d={ProgramSeconds.ToString(CultureInfo.InvariantCulture)}",
                    "-f", "lavfi", "-i", $"sine=frequency=440:duration={ProgramSeconds.ToString(CultureInfo.InvariantCulture)}",
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "veryfast", "-crf", "18",
                    "-c:a", "aac", "-b:a", "96k", "-ar", "48000", "-ac", "2", "-shortest",
                    programFile
                ],
                TimeSpan.FromMinutes(2), CancellationToken.None);
            generated.Succeeded.Should().BeTrue("the program fixture is built with the host's own ffmpeg: " + generated.StdErr);

            // A shorter, single-colour clip for the PlayOnce case, again built locally.
            Dictionary<string, string> fixtureByStorageKey = new(StringComparer.Ordinal);
            var files = new List<ProjectWorkspaceFile>();
            foreach (OverlaySpec overlay in overlays)
            {
                string fixturePath = overlay.FixturePath;
                if (clipSeconds is { } seconds)
                {
                    fixturePath = Path.Combine(root, "playonce.mp4");
                    VideoToolResult clip = await real.RunFfmpegAsync(
                        [
                            "-nostdin", "-hide_banner", "-y", "-loglevel", "error",
                            "-f", "lavfi", "-i", $"color=c={clipColour ?? "red"}:s=320x240:r=30:d={seconds.ToString(CultureInfo.InvariantCulture)}",
                            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "veryfast", "-crf", "28",
                            fixturePath
                        ],
                        TimeSpan.FromMinutes(1), CancellationToken.None);
                    clip.Succeeded.Should().BeTrue("the PlayOnce clip is built with the host's own ffmpeg: " + clip.StdErr);
                }

                string storageKey = $"projects/{ProjectId}/userFiles/{overlay.Id:D}{Path.GetExtension(overlay.Name)}";
                fixtureByStorageKey[storageKey] = fixturePath;
                files.Add(new ProjectWorkspaceFile(
                    overlay.Id, ProjectId, overlay.Name, null, "userFiles", storageKey, overlay.Mime,
                    new FileInfo(fixturePath).Length, DateTime.UnixEpoch, null));
            }

            VideoAnalysisArtifact artifact = new(
                Version: 1,
                Media: new VideoAnalysisMedia(ProgramSeconds, 30, 1, CanvasWidth, CanvasHeight),
                Shots: [new VideoAnalysisShot("s0", 0.0, ProgramSeconds)],
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
                .Setup(w => w.ListFilesAsync(ProjectId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(files);
            projectFiles
                .Setup(w => w.DownloadStorageKeyToFileAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<Guid, string, string, CancellationToken>((_, key, destination, _) =>
                {
                    Download(key, destination);
                    return Task.CompletedTask;
                });
            projectFiles
                .Setup(w => w.DownloadStorageKeyToFileAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns<Guid, string, string, long, CancellationToken>((_, key, destination, _, _) =>
                {
                    Download(key, destination);
                    return Task.CompletedTask;
                });
            projectFiles
                .Setup(w => w.UploadArtifactAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid pid, string _, string fileName, string _, CancellationToken _, string category) =>
                    "projects/" + pid + "/" + category + "/" + fileName);

            void Download(string key, string destination)
            {
                if (key == AnalysisArtifactPath)
                    File.WriteAllText(destination, artifactJson);
                else if (fixtureByStorageKey.TryGetValue(key, out string? fixture))
                    File.Copy(fixture, destination, overwrite: true);
                else
                    File.Copy(programFile, destination, overwrite: true);
            }

            var probe = new FfprobeMediaProbe(real, options, NullLogger<FfprobeMediaProbe>.Instance);

            VideoCompileStepConfig config = configure(new VideoCompileStepConfig(
                Version: 1,
                Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
                AnalysisStepOrder: 1) with
            {
                Mode = VideoCompileMode.Reencode,
                RegisterProjectFile = false,
                LoudnessTarget = ProgramLoudness.Off,
                PrePaddingMs = 0,
                PostPaddingMs = 0
            });

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
                    new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: "projects/p/outputFiles/e/render.mp4", ArtifactStorageKey: null),
                    new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisArtifactPath),
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

            var run = new MediaOverlayRun(root, real, programFile, recorder);
            try
            {
                run.Result = await executor.ExecuteAsync(context);
                return run;
            }
            catch
            {
                run.Dispose();
                throw;
            }
        }

        public async Task<(int R, int G, int B)> PixelAsync(string file, double atSec, int x, int y)
        {
            byte[] frame = await DecodeFrame(file, atSec);
            int index = (y * CanvasWidth + x) * 3;
            return (frame[index], frame[index + 1], frame[index + 2]);
        }

        public async Task<double> MeanVolumeDbAsync(string file, double atSec, double durationSec, string audioFilter)
        {
            VideoToolResult measured = await _real.RunFfmpegAsync(
                [
                    "-nostdin", "-hide_banner", "-y", "-loglevel", "info",
                    "-ss", atSec.ToString(CultureInfo.InvariantCulture),
                    "-t", durationSec.ToString(CultureInfo.InvariantCulture),
                    "-i", file, "-vn", "-af", audioFilter, "-f", "null", "-"
                ],
                TimeSpan.FromMinutes(1), CancellationToken.None);
            measured.Succeeded.Should().BeTrue(measured.StdErr);
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
                measured.StdErr, @"mean_volume:\s*(-?[0-9.]+|-inf)\s*dB");
            match.Success.Should().BeTrue("volumedetect prints a mean_volume line: " + measured.StdErr);
            return match.Groups[1].Value == "-inf"
                ? double.NegativeInfinity
                : double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        private async Task<byte[]> DecodeFrame(string file, double atSec)
        {
            File.Exists(file).Should().BeTrue(file);
            string raw = Path.Combine(_root, $"frame-{Guid.NewGuid():N}.rgb");
            VideoToolResult decoded = await _real.RunFfmpegAsync(
                [
                    "-nostdin", "-hide_banner", "-y", "-loglevel", "error",
                    "-i", file, "-ss", atSec.ToString(CultureInfo.InvariantCulture),
                    "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", raw
                ],
                TimeSpan.FromMinutes(1), CancellationToken.None);
            decoded.Succeeded.Should().BeTrue(decoded.StdErr);
            byte[] bytes = File.ReadAllBytes(raw);
            bytes.Length.Should().Be(CanvasWidth * CanvasHeight * 3);
            return bytes;
        }

        public void Dispose()
        {
            VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
            VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover scratch directory is not a test failure.
            }
        }
    }

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Records every invocation while delegating to the real runner, and — because the executor
    /// deletes its whole scratch directory when the step ends — carries the encoded program out to
    /// <c>preservedDirectory</c> first.
    /// </summary>
    private sealed class RecordingRunner : IVideoToolRunner
    {
        private readonly IVideoToolRunner _inner;
        private readonly string _preservedDirectory;

        public RecordingRunner(IVideoToolRunner inner, string preservedDirectory)
        {
            _inner = inner;
            _preservedDirectory = preservedDirectory;
        }

        public List<IReadOnlyList<string>> Commands { get; } = new();

        public string Preserved => _preservedDirectory;

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority)
            => RunFfmpegAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(
            IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine, VideoToolPriority priority)
            => RunFfmpegAsync(args, timeout, ct, onStdOutLine);

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority)
            => RunFfprobeAsync(args, timeout, ct);

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
