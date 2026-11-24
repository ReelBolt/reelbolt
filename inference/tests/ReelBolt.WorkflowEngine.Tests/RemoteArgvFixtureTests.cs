using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Security T-326/T-029: the argv the engine builds for a remote (runner) session in the shapes that draw text
/// (caption ass, caption drawtext, overlay chain, thumbnail, placement still, disclosure label), checked in at
/// <c>sandbox/pkg/agent/media/testdata/engine-remote-argv.json</c> so the runner's Go validator is tested against
/// the engine's REAL argv. Built with exactly the remote inputs: session-relative virtual paths (what
/// <see cref="RemoteMediaWorkspace.MaterializeAsync"/> returns) and the font the engine passes remotely (none).
/// Regenerate with <c>UPDATE_ENGINE_REMOTE_ARGV=1</c>; the drift test fails while the fixture is stale.
/// </summary>
public class RemoteArgvFixtureTests
{
    internal static string FixturePath()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "inference", "ReelBolt.sln")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("the test runs inside the repository");
        return Path.Combine(dir!, "sandbox", "pkg", "agent", "media", "testdata", "engine-remote-argv.json");
    }

    /// <summary>
    /// The compile's filter graph reaches ffmpeg inline as one <c>-filter_complex</c> argument, or (a long graph with
    /// overlays, music, inserts, sfx or censor) as the session file <c>filter_complex.txt</c> named by
    /// <c>-filter_complex_script</c>. Only the inline shape is recorded: the Go fixture test
    /// (TestValidateArgvAcceptsTheEnginesRemoteArgvFixture) rewrites every inline filter into the script shape itself,
    /// writing the file into the session. The argv around the filter is the compile's input and output skeleton.
    /// </summary>
    private static string[] CompileArgv(string filter, bool script = false) =>
    [
        "-nostdin", "-hide_banner", "-y", "-loglevel", "error",
        .. UserMediaInputFormats.InputOptions(), "-i", "source-0.mp4",
        .. script ? new[] { "-filter_complex_script", "filter_complex.txt" } : new[] { "-filter_complex", filter },
        "-map", "[vout]", "-map", "0:a?", "-c:v", "libx264", "-c:a", "aac", "program.mp4"
    ];

    internal sealed record Shape(string Name, string[] Argv, IReadOnlyDictionary<string, string>? Files = null);

    internal static List<Shape> Shapes()
    {
        string? font = null; // DrawtextFilterBuilder.EngineFontFor(remote, ...) is null for every remote session.
        RemoteSessionFontExecutorTests.RecordingWorkspace remote = new(remote: true);
        List<ResolvedCaptionCue> cues = [new("vo0", 0, "hello", 0, 1), new("vo0", 1, "world", 1, 2)];
        ResolvedOverlay overlay = new(
            "p0", "LowerThird", "Title", "Sub", DurationMs: 1500, Emphasis: "Normal", OutputStartSec: 1, OutputEndSec: 3,
            Rect: new VideoAnalysisRect(0.1, 0.8, 0.6, 0.15), TextColor: "Light");

        // The compile's own text-overlay stage, built by the executor against a remote session.
        string overlayChain = RemoteSessionFontExecutorTests.Compile().BuildTextOverlayStage(
            remote, "[vcut]", [overlay], 1920, 1080, RemoteSessionFontExecutorTests.OverlayConfig, slot => $"overlay-{slot}.txt", "[vout]");

        // The generated-clip normalizer's real argv (with the visible disclosure label) against a remote session.
        RemoteSessionFontExecutorTests.RecordingWorkspace normalizerSession = new(remote: true);
        new GeneratedClipNormalizer(new Moq.Mock<IVideoToolRunner>().Object,
                Microsoft.Extensions.Options.Options.Create(new VideoEditingOptions()),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GeneratedClipNormalizer>.Instance)
            .NormalizeAsync(normalizerSession, "clip-raw.mp4", "clip-normalized.mp4", System.Threading.CancellationToken.None,
                disclosure: new GeneratedClipDisclosure(VisibleLabel: true))
            .GetAwaiter().GetResult();

        return
        [
            new("caption-ass", CompileArgv(CaptionFilterBuilder.BuildAssFilter("[vcap]", AssSubtitleBuilder.ScriptFileName, fontsDir: null))),
            new("caption-drawtext", CompileArgv(CaptionFilterBuilder.BuildFilterChain(
                "[vcap]", cues, 1920, 1080, CaptionStyle.Karaoke, fontSizePct: 4, fontColor: "white", boxColor: "black@0.6",
                highlightColor: "yellow", fontFilePath: font, textFilePathForSlot: CaptionFilterBuilder.SlotFileName))),
            new("overlay-drawtext", CompileArgv(overlayChain)),
            new("thumbnail", FfmpegArgvBuilder.BuildThumbnailArgs(
                "source.mp4", "thumbnail-extracted.png", 1.5, 1280, 720, cover: true, quality: 4,
                new ThumbnailOverlay("thumbnail-overlay.txt", font, 65, "white", "black@0.45", 0.78))),
            new("placement-still", PlacementStillArgvBuilder.Build(
                "placement-stills/source-0.mp4", "placement-stills/still-01.jpg", 3, 1080, 1920, cover: true,
                [new PlacementStillBand("p0", new VideoAnalysisRect(0, 0.7, 1, 0.2))], font)),
            new("disclosure-label", normalizerSession.FfmpegCalls.Single()),
        ];
    }

    private static string Render()
    {
        var doc = new
        {
            comment = "Generated by inference/tests/ReelBolt.WorkflowEngine.Tests/RemoteArgvFixtureTests.cs; " +
                      "regenerate with UPDATE_ENGINE_REMOTE_ARGV=1. The engine's argv for a remote (runner) session; " +
                      "'files' holds the session files an argv names (the filter script).",
            shapes = Shapes().ConvertAll(s => s.Files is null
                ? (object)new { shape = s.Name, argv = s.Argv }
                : new { shape = s.Name, argv = s.Argv, files = s.Files }),
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n";
    }

    [Fact]
    public void The_checked_in_remote_argv_fixture_matches_what_the_engine_builds()
    {
        string path = FixturePath();
        string expected = Render();
        if (Environment.GetEnvironmentVariable("UPDATE_ENGINE_REMOTE_ARGV") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, expected);
        }

        File.Exists(path).Should().BeTrue("regenerate with UPDATE_ENGINE_REMOTE_ARGV=1");
        File.ReadAllText(path).ReplaceLineEndings("\n").Should().Be(expected,
            "the engine's remote argv changed: regenerate the fixture with UPDATE_ENGINE_REMOTE_ARGV=1 and re-run the Go validator tests");
    }

    [Fact]
    public void No_remote_shape_carries_a_font_or_an_absolute_path()
    {
        foreach (Shape s in Shapes())
        {
            string shape = s.Name;
            foreach (string arg in s.Argv.Concat(s.Files?.Values ?? []))
            {
                arg.Should().NotStartWith("/", shape);
                arg.Should().NotContain("fontfile=", shape).And.NotContain("fontsdir=", shape).And.NotContain("'/", shape);
            }
        }
    }
}
