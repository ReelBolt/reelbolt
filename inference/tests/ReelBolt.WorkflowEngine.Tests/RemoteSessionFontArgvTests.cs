using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Security T-326: a remote (runner) session cannot read the engine host's fonts, and the runner's argv validator
/// accepts font paths only under <c>/usr/share/fonts/</c>. The engine passes no font path to a remote session
/// (drawtext falls back to fontconfig, libass to its default) and keeps the local argv byte-identical.
/// </summary>
public class RemoteSessionFontArgvTests
{
    private const string EngineFont = "/usr/share/fonts/dejavu/DejaVuSans.ttf";

    private static RemoteMediaWorkspace Remote() =>
        (RemoteMediaWorkspace)RuntimeHelpers.GetUninitializedObject(typeof(RemoteMediaWorkspace));

    private static IMediaWorkspace Local() =>
        LocalMediaWorkspace.ForLocalFile("/scratch/source.mp4", new Mock<IVideoToolRunner>().Object, NullLogger.Instance);

    /// <summary>The session paths these tests hand the builders are relative, so any absolute path left is the engine's.</summary>
    private static void AssertNoEngineFontPath(string filter)
    {
        filter.Should().NotContain("fontfile=").And.NotContain("fontsdir=").And.NotContain("/usr/share/fonts");
        filter.Should().NotContain("'/", "a remote session's argv carries no absolute path");
    }

    [Fact]
    public void A_remote_session_gets_no_engine_font_and_a_local_one_keeps_it()
    {
        DrawtextFilterBuilder.EngineFontFor(Remote(), EngineFont).Should().BeNull();
        DrawtextFilterBuilder.EngineFontFor(Local(), EngineFont).Should().Be(EngineFont);
    }

    [Fact]
    public void The_font_option_is_the_old_bytes_when_a_font_is_given_and_nothing_when_not()
    {
        DrawtextFilterBuilder.FontFileOption(EngineFont).Should().Be($"fontfile='{EngineFont}':");
        DrawtextFilterBuilder.FontFileOption(null).Should().BeEmpty();
    }

    [Fact]
    public void Remote_caption_ass_filter_has_no_fontsdir()
    {
        AssertNoEngineFontPath(CaptionFilterBuilder.BuildAssFilter("[vcap]", "session/captions.ass", null));
    }

    [Fact]
    public void Remote_caption_drawtext_chain_has_no_font_path()
    {
        List<ResolvedCaptionCue> cues = [new("vo0", 0, "hello", 0, 1), new("vo0", 1, "world", 1, 2)];
        foreach (CaptionStyle style in new[] { CaptionStyle.Subtitle, CaptionStyle.Karaoke, CaptionStyle.Punch })
        {
            string chain = CaptionFilterBuilder.BuildFilterChain(
                "[vcap]", cues, 1920, 1080, style, fontSizePct: 4, fontColor: "white", boxColor: "black@0.6",
                highlightColor: "yellow", fontFilePath: null, textFilePathForSlot: slot => $"session/cap-{slot}.txt");

            chain.Should().Contain("drawtext=textfile='session/cap-0.txt':expansion=none");
            AssertNoEngineFontPath(chain);
        }
    }

    [Fact]
    public void Remote_overlay_drawtext_chain_has_no_font_path()
    {
        ResolvedOverlay overlay = new(
            "p0", "LowerThird", "Title", "Sub", DurationMs: 1500, Emphasis: "Normal", OutputStartSec: 1, OutputEndSec: 3,
            Rect: new VideoAnalysisRect(0.1, 0.8, 0.6, 0.15), TextColor: "Light");

        string chain = DrawtextFilterBuilder.BuildFilterChain(
            "[vcut]", [overlay], 1920, 1080, fontSizePct: 5, fadeMs: 300, fontColor: "white", boxColor: "black@0.45",
            fontFilePath: null, textFilePathForIndex: slot => $"session/ov-{slot}.txt");

        chain.Should().Contain("drawtext=textfile='session/ov-");
        AssertNoEngineFontPath(chain);
    }

    [Fact]
    public void Remote_thumbnail_overlay_has_no_font_path()
    {
        ThumbnailOverlay overlay = new("session/thumbnail-overlay.txt", null, 65, "white", "black@0.45", 0.78);

        string[] args = FfmpegArgvBuilder.BuildThumbnailArgs("session/in.mp4", "session/out.jpg", 0, 1280, 720, cover: true, quality: 4, overlay);

        string filter = string.Join(' ', args);
        filter.Should().Contain("drawtext=textfile='session/thumbnail-overlay.txt'");
        AssertNoEngineFontPath(filter);
    }

    [Fact]
    public void Remote_placement_still_has_no_font_path()
    {
        string[] args = PlacementStillArgvBuilder.Build(
            "session/in.mp4", "session/out.jpg", 3, 1080, 1920, cover: true,
            [new PlacementStillBand("p0", new VideoAnalysisRect(0, 0.7, 1, 0.2))], null);

        string vf = args[System.Array.IndexOf(args, "-vf") + 1];
        vf.Should().Contain("drawtext=text='p0'");
        AssertNoEngineFontPath(vf);
    }

    [Fact]
    public void Remote_generated_clip_disclosure_label_has_no_font_path()
    {
        string filter = GeneratedClipNormalizer.BuildDisclosureLabelFilter(null);

        filter.Should().StartWith("drawtext=text=");
        AssertNoEngineFontPath(filter);
    }

    [Fact]
    public void Local_filters_keep_the_engine_font_byte_for_byte()
    {
        GeneratedClipNormalizer.BuildDisclosureLabelFilter(EngineFont).Should().StartWith($"drawtext=fontfile='{EngineFont}':text=");
        CaptionFilterBuilder.BuildAssFilter("[vcap]", "/s/c.ass", "/fonts/captions").Should().Contain(":fontsdir='/fonts/captions'");
        string[] args = PlacementStillArgvBuilder.Build(
            "/in.mp4", "/out.jpg", 3, 1080, 1920, cover: true, [new PlacementStillBand("p0", new VideoAnalysisRect(0, 0.7, 1, 0.2))], EngineFont);
        args.Single(a => a.Contains("drawtext", System.StringComparison.Ordinal)).Should().Contain($"drawtext=fontfile='{EngineFont}':text='p0'");
    }
}
