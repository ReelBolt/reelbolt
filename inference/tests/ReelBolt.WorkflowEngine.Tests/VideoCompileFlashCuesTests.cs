#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>flashCues and windowed Flash/Glow region effects: pure tables, golden graphs, off identity, real 3 s renders.</summary>
public partial class VideoCompileNarrationAndFormatTests
{
    [Fact]
    public void Flash_word_tables_are_first_party_and_ordered()
    {
        FlashCueExpander.ColourHex(FlashColour.White).Should().Be("0xFFFFFF");
        FlashCueExpander.ColourHex(FlashColour.Warm).Should().Be("0xFFD699");
        FlashCueExpander.ColourHex(FlashColour.Cool).Should().Be("0xAACDFF");
        FlashCueExpander.PeakAlpha(FlashStrength.Subtle).Should().BeLessThan(FlashCueExpander.PeakAlpha(FlashStrength.Normal));
        FlashCueExpander.PeakAlpha(FlashStrength.Normal).Should().BeLessThan(FlashCueExpander.PeakAlpha(FlashStrength.Strong));
        FlashCueExpander.LengthSeconds(FlashLength.Short).Should().BeLessThan(FlashCueExpander.LengthSeconds(FlashLength.Long));
    }

    [Fact]
    public void Flash_cues_config_checks_have_specific_codes()
    {
        VideoCompileStepConfig Cfg(IReadOnlyList<FlashCueRequest> cues, VideoCompileMode mode = VideoCompileMode.Reencode) =>
            new(1, new ExtractInputRef(ExtractInputSource.Previous), 1, Mode: mode, FlashCues: cues);

        FlashCueRequest.Check(Cfg([])).Should().BeNull();
        FlashCueRequest.Check(Cfg([new FlashCueRequest("e0")])).Should().BeNull();
        FlashCueRequest.Check(Cfg(Enumerable.Range(0, 17).Select(i => new FlashCueRequest("e" + i)).ToList()))!.ErrorCode.Should().Be("FLASH_CUES_TOO_MANY");
        FlashCueRequest.Check(Cfg([new FlashCueRequest("t3")]))!.ErrorCode.Should().Be("FLASH_CUES_EVENT_ID_INVALID");
        FlashCueRequest.Check(Cfg([new FlashCueRequest("e0", (FlashColour)99)]))!.ErrorCode.Should().Be("FLASH_CUES_WORD_UNKNOWN");
        FlashCueRequest.Check(Cfg([new FlashCueRequest("e0")], VideoCompileMode.StreamCopy))!.ErrorCode.Should().Be("FLASH_CUES_REQUIRE_REENCODE");
        VideoReframe.CheckConfig(Cfg([new FlashCueRequest("e0")], VideoCompileMode.StreamCopy)).ErrorCode.Should().Be("FLASH_CUES_REQUIRE_REENCODE");
    }

    [Fact]
    public void Region_effect_window_event_ids_are_checked_on_save()
    {
        RegionEffectRules.Validate([new VideoRegionEffect("g0", RegionEffect.Flash, FromEventId: "e1", ToEventId: "e2")], VideoCompileMode.Reencode)
            .Should().BeEmpty();
        RegionEffectRules.Validate([new VideoRegionEffect("g0", RegionEffect.Glow, FromEventId: "1.5")], VideoCompileMode.Reencode)
            .Should().ContainSingle().Which.Code.Should().Be("EFFECT_WINDOW_EVENT_INVALID");
    }

    [Fact]
    public void Flash_and_glow_effect_graphs_are_pinned()
    {
        RegionEffectFilterBuilder.BuildEffectChain("[a]", "[m]", "[o]", 0, RegionEffect.Flash, 1280, 720, 1.0, "30/1", 3.0).Should().Be(
            "[a]split=2[ree0b][ree0h1];[ree0h1]eq=brightness=0.45:contrast=1.15,format=yuva420p[ree0h2];" +
            "[ree0h2][m]alphamerge[ree0h3];[ree0b][ree0h3]overlay=x=0:y=0:format=yuv420[o]");
        RegionEffectFilterBuilder.BuildEffectChain("[a]", "[m]", "[o]", 0, RegionEffect.Glow, 1280, 720, 1.0, "30/1", 3.0).Should().Be(
            "[a]split=2[ree0b][ree0g1];[ree0g1]gblur=sigma=14.4,eq=brightness=0.12:saturation=1.25,format=yuva420p[ree0g2];" +
            "[ree0g2][m]alphamerge[ree0g3];[ree0b][ree0g3]overlay=x=0:y=0:format=yuv420[o]");
    }

    [Fact]
    public void Region_effect_window_wraps_the_chain_in_a_gated_overlay_and_no_window_is_unchanged()
    {
        string plain = RegionEffectFilterBuilder.BuildEffectChain("[a]", "[m]", "[o]", 0, RegionEffect.Flash, 64, 64, 1.0, "30/1", 3.0);
        new RegionEffectWindow(1.5, null).Wrap("[a]", "[o]", 0, (i, o) => RegionEffectFilterBuilder.BuildEffectChain(i, "[m]", o, 0, RegionEffect.Flash, 64, 64, 1.0, "30/1", 3.0))
            .Should().Be(
                "[a]split=2[rew0b][rew0i];" +
                "[rew0i]split=2[ree0b][ree0h1];[ree0h1]eq=brightness=0.45:contrast=1.15,format=yuva420p[ree0h2];[ree0h2][m]alphamerge[ree0h3];[ree0b][ree0h3]overlay=x=0:y=0:format=yuv420[rew0o]" +
                ";[rew0b][rew0o]overlay=x=0:y=0:format=yuv420:enable='gte(t,1.499)'[o]");
        plain.Should().NotContain("rew0");
        new RegionEffectWindow(1.0, 2.0).EnableExpression().Should().Be("gte(t,0.999)*lt(t,1.999)");
        new RegionEffectWindow(null, 2.0).EnableExpression().Should().Be("lt(t,1.999)");
        new RegionEffectWindow(null, null, Never: true).EnableExpression().Should().Be("0");
    }

    [Fact]
    public async Task Flash_cues_off_leaves_the_graph_unchanged_and_on_adds_pinned_drawboxes_and_a_report()
    {
        VideoAnalysisArtifact artifact = WithEvents(SingleSourceArtifact(10.0), ("e0", 4.0), ("e1", 12.0));

        var off = new CompileFixture(artifact) { Config = c => c with { PrePaddingMs = 0, PostPaddingMs = 0, FlashCues = null } };
        (await off.RunAsync(BuildDecisionJson("s0"))).Status.Should().Be(StepStatus.Completed);
        var plain = new CompileFixture(SingleSourceArtifact(10.0)) { Config = c => c with { PrePaddingMs = 0, PostPaddingMs = 0 } };
        (await plain.RunAsync(BuildDecisionJson("s0"))).Status.Should().Be(StepStatus.Completed);
        off.FilterGraph.Should().Be(plain.FilterGraph).And.NotContain("drawbox");
        off.Output!.ContainsKey("flashCues").Should().BeFalse();

        var on = new CompileFixture(artifact)
        {
            Config = c => c with
            {
                PrePaddingMs = 0,
                PostPaddingMs = 0,
                FlashCues = [new FlashCueRequest("e0"), new FlashCueRequest("e1"), new FlashCueRequest("e7")]
            }
        };
        StepExecutionResult result = await on.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        on.FilterGraph.Should().Contain(
            "drawbox=x=0:y=0:w=iw:h=ih:color=0xFFFFFF@0.55:t=fill:enable='gte(t,3.999)*lt(t,4.039)'," +
            "drawbox=x=0:y=0:w=iw:h=ih:color=0xFFFFFF@0.275:t=fill:enable='gte(t,4.039)*lt(t,4.099)'");
        on.FilterGraph!.Split("drawbox").Length.Should().Be(3, "one cue is two gated boxes");
        JsonObject node = on.Output!["flashCues"]!.AsObject();
        node["flashesApplied"]!.AsArray().Should().HaveCount(1);
        node["dropped"]!.AsArray().Select(d => d!["reason"]!.GetValue<string>()).Should().Equal("anchor_cut_away", "unknown_anchor_id");
        on.Edl!.ContainsKey("flashCues").Should().BeTrue();
    }

    [Fact]
    public async Task Flash_cues_on_a_stream_copy_are_a_config_error()
    {
        var fixture = new CompileFixture(WithEvents(SingleSourceArtifact(10.0), ("e0", 4.0)))
        {
            Config = c => c with { Mode = VideoCompileMode.StreamCopy, AllowKeyframeSnapping = true, FlashCues = [new FlashCueRequest("e0")] }
        };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("FLASH_CUES_REQUIRE_REENCODE");
    }

    [RealFfmpegFact]
    public async Task Real_ffmpeg_flash_cue_brightens_the_event_frame_and_only_a_few_frames()
    {
        const int width = 128, height = 72, fps = 30;
        string work = Path.Combine(Path.GetTempPath(), "reelbolt-flash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string clip = Path.Combine(work, "clip.mp4");
            RunArgv(work, "ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi",
                "-i", $"color=c=0x606060:s={width}x{height}:r={fps}:d=3,format=yuv420p",
                "-an", "-c:v", "libx264", "-crf", "0", "-pix_fmt", "yuv420p", clip);
            var options = Options.Create(new VideoEditingOptions
            {
                ScratchPath = Path.Combine(work, "scratch"),
                MaxConcurrentJobs = 1,
                AnalyzeTimeoutSeconds = 60,
                CompileTimeoutSeconds = 120
            });
            var runner = new FfmpegVideoToolRunner(options, NullLogger<FfmpegVideoToolRunner>.Instance);
            var fixture = new CompileFixture(WithEvents(SingleSourceArtifact(3.0, width, height), ("e0", 1.5)), sourcesHaveAudio: false)
            {
                RealToolRunner = runner,
                RealProbe = new FfprobeMediaProbe(runner, options, NullLogger<FfprobeMediaProbe>.Instance),
                SourceFile = clip,
                OutputCopy = Path.Combine(work, "out.mp4")
            };
            fixture.Config = c => c with
            {
                PrePaddingMs = 0,
                PostPaddingMs = 0,
                Crf = 0,
                FlashCues = [new FlashCueRequest("e0", FlashColour.White, FlashStrength.Strong, FlashLength.Medium)]
            };

            StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

            result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
            string raw = Path.Combine(work, "frames.yuv");
            RunArgv(work, "ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", fixture.OutputCopy!, "-pix_fmt", "yuv420p", "-f", "rawvideo", raw);
            byte[] bytes = File.ReadAllBytes(raw);
            int frameBytes = width * height * 3 / 2;
            (bytes.Length / frameBytes).Should().Be(3 * fps, "a flash keeps every frame");
            int Luma(int k) => bytes[k * frameBytes + width * height / 2];

            int baseline = Luma(10);
            int first = Enumerable.Range(0, 3 * fps).First(k => Luma(k) > baseline + 20);
            first.Should().BeInRange(45 - 1, 45 + 1, "the flash lands on the event frame");
            int bright = Enumerable.Range(0, 3 * fps).Count(k => Luma(k) > baseline + 6);
            bright.Should().BeInRange(4, 8, "a 0.2 s flash is about six frames");
            Luma(fps * 3 - 1).Should().BeInRange(baseline - 2, baseline + 2);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    [RealFfmpegFact]
    public void Real_ffmpeg_windowed_flash_effect_only_changes_frames_inside_the_window()
    {
        const int width = 64, height = 64, fps = 30;
        string work = Path.Combine(Path.GetTempPath(), "reelbolt-fxwindow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string raw = Path.Combine(work, "frames.yuv");
            string graph = new RegionEffectWindow(1.0, 2.0).Wrap("[0:v]", "[out]", 0,
                (i, o) => RegionEffectFilterBuilder.BuildEffectChain(i, "[1:v]", o, 0, RegionEffect.Flash, width, height, 1.0, $"{fps}/1", 3.0));
            RunArgv(work, "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", $"color=c=0x606060:s={width}x{height}:r={fps}:d=3,format=yuv420p",
                "-f", "lavfi", "-i", $"color=c=white:s={width}x{height}:r={fps}:d=3,format=gray",
                "-filter_complex", graph, "-map", "[out]", "-pix_fmt", "yuv420p", "-f", "rawvideo", raw);
            byte[] bytes = File.ReadAllBytes(raw);
            int frameBytes = width * height * 3 / 2;
            (bytes.Length / frameBytes).Should().Be(3 * fps);
            int Luma(int k) => bytes[k * frameBytes];
            Luma(10).Should().Be(Luma(80));
            Luma(29).Should().Be(Luma(10), "before the window");
            Luma(30).Should().BeGreaterThan(Luma(10) + 20, "the window opens on its first frame");
            Luma(59).Should().BeGreaterThan(Luma(10) + 20);
            Luma(60).Should().Be(Luma(10), "the window has closed");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }
}
