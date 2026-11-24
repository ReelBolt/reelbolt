using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Regression tests for the raw plate on a later clip's first frame. Generated clips (MiniMax)
/// carry 158 video frames at 24 fps (6.583 s) in a 6.592 s container — the audio is longer. A span
/// kept to the clip's end snaps to the CONTAINER duration, 159 frames, so the output timeline put
/// the next clip at 6.625 s; the segment delivered 158 frames, <c>concat</c> started the next clip
/// at the audio's 6.592 s, and that first frame fell before every insert's and plate guard's
/// <c>enable</c> window — fully green on the customer's two-clip composites (16% / 21% of the
/// frame), the rest of the clip one frame out of registration. Each segment is now held to its
/// timeline length (<see cref="VideoCompileStepExecutor.SegmentLengthSuffix"/>).
/// </summary>
public class SegmentLengthBoundaryTests
{
    private static VideoCompileStepExecutor.ResolvedSpan WholeClip(double containerSec, int fps) =>
        new(0, containerSec, 0,
            VideoCompileStepExecutor.FrameToSec(VideoCompileStepExecutor.ToEndFrame(containerSec, fps, 1), fps, 1),
            0, VideoCompileStepExecutor.ToEndFrame(containerSec, fps, 1));

    [Fact]
    public void A_span_is_padded_and_trimmed_to_its_own_snapped_length()
    {
        VideoCompileStepExecutor.ResolvedSpan span = WholeClip(6.592, 24);
        span.EndFrame.Should().Be(159, "the container duration snaps up to 159 frames");

        VideoCompileStepExecutor.SegmentLengthSuffix(span)
            .Should().Be(",tpad=stop_mode=clone:stop_duration=6.625,trim=end=6.625");
    }

    [Fact]
    public void A_span_without_a_usable_frame_rate_is_left_alone()
    {
        VideoCompileStepExecutor.SegmentLengthSuffix(new VideoCompileStepExecutor.ResolvedSpan(0, 5, 0, 5, 0, 0))
            .Should().BeEmpty();
    }

    /// <summary>The ffmpeg behaviour itself, on a clip shaped like the customer's.</summary>
    [RealFfmpegFact]
    public void The_next_clip_starts_exactly_where_the_timeline_puts_it()
    {
        string work = Path.Combine(Path.GetTempPath(), "segment-length-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string clip = Path.Combine(work, "clip.mp4");
            Run($"-v error -f lavfi -i testsrc2=s=160x90:r=24 -f lavfi -i sine=r=48000 -frames:v 158 -t 6.592 " +
                $"-c:v libx264 -preset ultrafast -c:a aac -shortest -y \"{clip}\"");
            Run($"-v error -i \"{clip}\" -map 0:a -t 6.592 -c:a aac -y \"{clip}.a.mp4\"");
            // Container duration longer than the video, exactly like the generated clips.
            Run($"-v error -i \"{clip}\" -i \"{clip}.a.mp4\" -map 0:v -map 1:a -c copy -y \"{clip}.m.mp4\"");
            File.Move(clip + ".m.mp4", clip, overwrite: true);

            VideoCompileStepExecutor.ResolvedSpan span = WholeClip(6.592, 24);
            List<string> fixedHashes = Concat(clip, span, VideoCompileStepExecutor.SegmentLengthSuffix(span), work);
            List<string> sourceHashes = Hashes($"-i \"{clip}\" -map 0:v", work);

            sourceHashes.Should().HaveCount(158);
            fixedHashes.Should().HaveCount(318, "two segments of the timeline's 159 frames each");
            fixedHashes[158].Should().Be(sourceHashes[157], "the shortfall is filled with the clip's last frame");
            fixedHashes[159].Should().Be(sourceHashes[0], "the next clip begins on the frame the timeline gives it");

            // Without the suffix the next clip lands one frame early — the bug.
            List<string> brokenHashes = Concat(clip, span, string.Empty, work);
            brokenHashes[158].Should().Be(sourceHashes[0]);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static List<string> Concat(string clip, VideoCompileStepExecutor.ResolvedSpan span, string suffix, string work)
    {
        string ee = ReelBolt.WorkflowEngine.Services.Video.FfmpegArgvFormat.Number(span.SnappedEnd);
        string seg(int i) =>
            $"[{i}:v]trim=start=0:end={ee},setpts=PTS-STARTPTS,fps=24/1{suffix}[v{i}];" +
            $"[{i}:a]atrim=start=0:end={ee},asetpts=PTS-STARTPTS[a{i}]";
        string graph = $"{seg(0)};{seg(1)};[v0][a0][v1][a1]concat=n=2:v=1:a=1[v][a]";
        return Hashes($"-i \"{clip}\" -i \"{clip}\" -filter_complex \"{graph}\" -map [v] -map [a]", work);
    }

    /// <summary>The per-frame video hashes of an ffmpeg invocation's first output stream.</summary>
    private static List<string> Hashes(string inputs, string work)
    {
        string md5 = Path.Combine(work, Guid.NewGuid().ToString("N") + ".md5");
        Run($"-v error {inputs} -f framemd5 -y \"{md5}\"");
        return File.ReadLines(md5)
            .Where(l => l.StartsWith("0,", StringComparison.Ordinal))
            .Select(l => l.Split(',')[^1].Trim())
            .ToList();
    }

    private static void Run(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("ffmpeg", args) { RedirectStandardError = true })!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, err);
    }

    [Fact]
    public void A_track_ending_on_its_clips_last_tracked_frame_is_held_over_the_repeated_frame()
    {
        // Two clips of 159 timeline frames at 24 fps (6.625 s each). The laptop's plate track ends
        // on its last tracked frame, 6.583 s — one frame short of the segment, the repeated frame
        // that showed raw green. A track ending mid-clip is left alone.
        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>
        {
            new(0, 6.625, 0, 6.625, 0, 159, SourceIndex: 0),
            new(0, 6.625, 0, 6.625, 0, 159, SourceIndex: 1)
        };
        OutputTimeline timeline = OutputTimeline.Build(spans, VideoCompileStepExecutor.BuildHardCutSeamsForFallback(spans.Count));
        double hold = 2.0 / 24;

        timeline.ExtendToSegmentEnd((0, 6.5833), hold).Should().Be((0, 6.625));
        timeline.ExtendToSegmentEnd((6.625, 13.208), hold).Should().Be((6.625, 13.25));
        timeline.ExtendToSegmentEnd((0, 5.0), hold).Should().Be((0, 5.0), "a track that stops mid-clip is not a clip-end track");
    }
}
