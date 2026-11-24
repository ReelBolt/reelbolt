#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-006: video-overlay specifics — loop/end pacing, the program frame rate, and Muted vs Mixed
/// audio (docs/design/picture-overlay.md §5.5). Each encode is an exact first-party argv/filtergraph
/// assertion through <see cref="ReelCompileFixture"/>; no real ffmpeg runs.
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class MediaOverlayVideoLoopTests
{
    private static readonly Guid ProjectId = VideoCompileReelFeatureTests.ProjectId;

    // ==========================================================================================
    // Loop and end
    // ==========================================================================================

    [Fact]
    public async Task Short_loop_content_loops_to_the_window_end_within_its_own_span()
    {
        var fixture = SingleSourceVideoOverlay(durationSec: 2, loop: MediaOverlayLoop.Loop, start: 2, end: 6);

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        List<string> args = fixture.EncodeArgs!;
        FlagsBeforeInput(args, 1).Should().Equal("-stream_loop", "-1", "-t", "4", "-protocol_whitelist", "file", "-format_whitelist", UserMediaInputFormats.Whitelist);
        fixture.FilterGraph.Should().Contain(
            "[1:v]format=rgba,fps=30/1,trim=duration=4,");
        fixture.FilterGraph.Should().Contain(
            "eof_action=pass:enable='between(t,2,6)'");
        Item(fixture)["endedEarly"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Play_once_ends_early_and_reports_the_probed_length()
    {
        var fixture = SingleSourceVideoOverlay(durationSec: 2, loop: MediaOverlayLoop.PlayOnce, start: 2, end: 6);

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        List<string> args = fixture.EncodeArgs!;
        FlagsBeforeInput(args, 1).Should().Equal(UserMediaInputFormats.InputOptions(), "PlayOnce must not loop the input: only the input whitelists precede it");
        fixture.FilterGraph.Should().NotContain("stream_loop");
        fixture.FilterGraph.Should().Contain("[1:v]format=rgba,fps=30/1,trim=duration=4,");
        fixture.FilterGraph.Should().Contain("eof_action=pass:enable='between(t,2,6)'");
        JsonObject item = Item(fixture);
        item["endedEarly"]!.GetValue<bool>().Should().BeTrue();
        item["contentDurationSec"]!.GetValue<double>().Should().Be(2);
    }

    [Fact]
    public async Task Content_longer_than_the_window_is_trimmed_to_the_span()
    {
        var fixture = SingleSourceVideoOverlay(durationSec: 10, loop: MediaOverlayLoop.Loop, start: 2, end: 6);

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        FlagsBeforeInput(fixture.EncodeArgs!, 1).Should().Equal("-stream_loop", "-1", "-t", "4", "-protocol_whitelist", "file", "-format_whitelist", UserMediaInputFormats.Whitelist);
        fixture.FilterGraph.Should().Contain("trim=duration=4,");
        Item(fixture)["endedEarly"]!.GetValue<bool>().Should().BeFalse();
    }

    // ==========================================================================================
    // Frame rate
    // ==========================================================================================

    [Fact]
    public async Task A_24_fps_inset_is_normalized_to_the_30_fps_program_on_both_encoders()
    {
        var single = SingleSourceVideoOverlay(
            durationSec: 2, loop: MediaOverlayLoop.Loop, start: 2, end: 6, contentFps: 24);
        StepExecutionResult singleResult = await single.RunAsync(ReelCompileFixture.DecisionJson("s0"));
        singleResult.Status.Should().Be(StepStatus.Completed, because: singleResult.ErrorDetails ?? singleResult.Output);
        single.FilterGraph.Should().Contain("[1:v]format=rgba,fps=30/1,trim=duration=4,");

        Guid fileId = Guid.NewGuid();
        var segmented = new ReelCompileFixture(ReelCompileFixture.TwoSourceArtifact());
        segmented.ProjectFiles.Add(ProjectFile(fileId, "inset.mp4", "video/mp4"));
        segmented.ProbeHook = VideoProbe(durationSec: 2, fpsNum: 24, audioCodec: "aac");
        segmented.Config = c => c with
        {
            PrePaddingMs = 0,
            PostPaddingMs = 0,
            MediaOverlays =
            [
                ConfiguredOverlay(fileId, MediaOverlayForm.PictureInPicture, start: 1, end: 3)
            ]
        };

        StepExecutionResult result = await segmented.RunAsync(ReelCompileFixture.DecisionJson("s0", "s1"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        segmented.FilterGraph.Should().Contain("[2:v]format=rgba,fps=30/1,trim=duration=2,");
    }

    // ==========================================================================================
    // Muted / Mixed audio
    // ==========================================================================================

    [Fact]
    public async Task A_silent_mixed_source_degrades_to_muted_and_is_reported_not_failed()
    {
        var fixture = SingleSourceVideoOverlay(
            durationSec: 2, loop: MediaOverlayLoop.Loop, start: 2, end: 6,
            audio: MediaOverlayAudio.Mixed, volume: MediaOverlayVolume.Quiet, audioCodec: null);

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonObject node = fixture.Output!["mediaOverlays"]!.AsObject();
        node["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).Should().Contain("audio_missing");
        Item(fixture)["audio"]!.GetValue<string>().Should().Be("Muted");
        fixture.FilterGraph.Should().NotContain("[moa0]");
        fixture.FilterGraph.Should().NotContain("volume=-12dB");
    }

    [Fact]
    public void Mixed_levels_map_to_their_bounded_dB_and_clamp()
    {
        MediaOverlayFilterBuilder.VolumeDb(MediaOverlayVolume.Quiet).Should().Be(-12);
        MediaOverlayFilterBuilder.VolumeDb(MediaOverlayVolume.Balanced).Should().Be(-6);
        MediaOverlayFilterBuilder.VolumeDb(MediaOverlayVolume.Prominent).Should().Be(0);

        MediaOverlayFilterBuilder.ClampVolumeDb(-40).Should().Be(-18);
        MediaOverlayFilterBuilder.ClampVolumeDb(6).Should().Be(0);
        MediaOverlayFilterBuilder.ClampVolumeDb(-9).Should().Be(-9);
    }

    [Fact]
    public async Task Mixed_audio_is_trimmed_resampled_gained_and_delayed_to_the_window_start()
    {
        var fixture = SingleSourceVideoOverlay(
            durationSec: 5, loop: MediaOverlayLoop.Loop, start: 3.5, end: 7.5,
            audio: MediaOverlayAudio.Mixed, volume: MediaOverlayVolume.Quiet);

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.FilterGraph.Should().Contain(
            "[1:a]atrim=start=0:end=4,asetpts=PTS-STARTPTS," +
            "aformat=sample_rates=48000:channel_layouts=stereo,volume=-12dB,adelay=3500|3500[moa0]");
        fixture.FilterGraph.Should().Contain(
            "[aout][moa0]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[aovl]");
        // Not dialogue: it is summed after the program mix, with no ducking stage of its own.
        fixture.FilterGraph.Should().NotContain("sidechaincompress");
        JsonObject item = Item(fixture);
        item["audio"]!.GetValue<string>().Should().Be("Mixed");
        item["audioLevel"]!.GetValue<string>().Should().Be("Quiet");
    }

    [Fact]
    public async Task A_cards_mixed_audio_is_summed_after_the_card_mute()
    {
        Guid fileId = Guid.NewGuid();
        var fixture = new ReelCompileFixture(ReelCompileFixture.SingleSourceArtifact(10.0));
        fixture.ProjectFiles.Add(ProjectFile(fileId, "card.mp4", "video/mp4"));
        fixture.ProbeHook = VideoProbe(durationSec: 5, audioCodec: "aac");
        fixture.Config = c => c with
        {
            PrePaddingMs = 0,
            PostPaddingMs = 0,
            MediaOverlays =
            [
                ConfiguredOverlay(
                    fileId, MediaOverlayForm.Card, start: 2, end: 6,
                    audio: MediaOverlayAudio.Mixed, volume: MediaOverlayVolume.Balanced,
                    cardAudio: MediaOverlayCardAudio.Mute)
            ]
        };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        const string expression =
            "if(lt(t,2),1,if(lt(t,2.05),(2.05-t)/0.05,if(lte(t,5.95),0,if(lt(t,6),(t-5.95)/0.05,1))))";
        fixture.FilterGraph.Should().Contain($"[aout]volume=eval=frame:volume='{expression}'[amut]");
        fixture.FilterGraph.Should().Contain(
            "aformat=sample_rates=48000:channel_layouts=stereo,volume=-6dB,adelay=2000|2000[moa0]");
        fixture.FilterGraph.Should().Contain(
            "[amut][moa0]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[aovl]");
        Item(fixture)["cardAudio"]!.GetValue<string>().Should().Be("Mute");
    }

    // ==========================================================================================
    // Helpers
    // ==========================================================================================

    private static ReelCompileFixture SingleSourceVideoOverlay(
        double durationSec,
        MediaOverlayLoop loop,
        double start,
        double end,
        MediaOverlayAudio audio = MediaOverlayAudio.Muted,
        MediaOverlayVolume volume = MediaOverlayVolume.Balanced,
        string? audioCodec = "aac",
        int contentFps = 30)
    {
        Guid fileId = Guid.NewGuid();
        var fixture = new ReelCompileFixture(ReelCompileFixture.SingleSourceArtifact(10.0));
        fixture.ProjectFiles.Add(ProjectFile(fileId, "clip.mp4", "video/mp4"));
        fixture.ProbeHook = VideoProbe(durationSec, contentFps, audioCodec);
        fixture.Config = c => c with
        {
            PrePaddingMs = 0,
            PostPaddingMs = 0,
            MediaOverlays =
            [
                ConfiguredOverlay(
                    fileId, MediaOverlayForm.PictureInPicture, start: start, end: end,
                    audio: audio, volume: volume, loop: loop)
            ]
        };
        return fixture;
    }

    private static ProjectWorkspaceFile ProjectFile(Guid id, string name, string mime) => new(
        id, ProjectId, name, null, "userFiles",
        $"projects/{ProjectId}/userFiles/{id:D}{Path.GetExtension(name)}", mime, 1024, DateTime.UnixEpoch, null);

    private static VideoMediaOverlay ConfiguredOverlay(
        Guid fileId,
        MediaOverlayForm form,
        double start,
        double end,
        MediaOverlayAudio audio = MediaOverlayAudio.Muted,
        MediaOverlayVolume volume = MediaOverlayVolume.Balanced,
        MediaOverlayCardAudio cardAudio = MediaOverlayCardAudio.Keep,
        MediaOverlayLoop loop = MediaOverlayLoop.Loop) => new(
        new InsertContentRef(InsertContentRef.ProjectFileKind, ProjectFileId: fileId),
        Form: form,
        Anchor: MediaOverlayAnchor.BottomRight,
        Size: MediaOverlaySize.Medium,
        OpacityPercent: 100,
        StartSec: start,
        EndSec: end,
        Loop: loop,
        Audio: audio,
        Volume: volume,
        CardAudio: cardAudio);

    private static Func<string, MediaProbeResult?> VideoProbe(
        double durationSec, int fpsNum = 30, string? audioCodec = "aac") =>
        path => Path.GetFileName(path).StartsWith("media-overlay-", StringComparison.Ordinal)
            ? new MediaProbeResult(
                durationSec, fpsNum, 1, 320, 240, "h264", audioCodec,
                audioCodec is null ? null : 48000)
            : null;

    private static JsonObject Item(ReelCompileFixture fixture) =>
        fixture.Output!["mediaOverlays"]!["items"]!.AsArray()[0]!.AsObject();

    private static IEnumerable<string> FlagsBeforeInput(IReadOnlyList<string> args, int inputOrdinal)
    {
        int seen = -1;
        int previousInputEnd = 0;
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] != "-i")
                continue;
            seen++;
            if (seen == inputOrdinal)
                return args.Skip(previousInputEnd).Take(i - previousInputEnd);
            previousInputEnd = i + 2; // "-i" plus its path
        }

        return [];
    }
}
