using System;
using System.Diagnostics;
using System.IO;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Cutaway-clip-start-1, with the real ffmpeg: the cutaway's first shown frame is the clip's FIRST frame. The clip is one second of green
/// then one of blue; a window opens at 1.0 s over a red base. Before the fix the overlay showed the clip at its own t=0, so the window
/// opened on the clip's frame at 1.0 s (blue) and, for a late window, on nothing at all.
/// </summary>
public class CutawayClipStartRealFfmpegTests
{
    private static string Run(params string[] args)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in new[] { "-hide_banner", "-loglevel", "error", "-y" }) psi.ArgumentList.Add(a);
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, err);
        return err;
    }

    private static string Pixel(string film, int frame)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-v", "error", "-i", film, "-vf", $"select=eq(n\\,{frame}),crop=2:2:32:32", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-" })
            psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        byte[] px = ms.ToArray();
        px.Length.Should().Be(12);
        // dominant channel: R (red base), G (green clip frame 0), B (blue clip, second half)
        return px[0] > 150 && px[1] < 100 && px[2] < 100 ? "red" : px[1] > 100 && px[0] < 100 && px[2] < 100 ? "green" : px[2] > 150 && px[0] < 100 && px[1] < 100 ? "blue" : $"?{px[0]},{px[1]},{px[2]}";
    }

    [Fact]
    public void The_compile_revision_changes_only_with_generated_clips_enabled()
    {
        string plain = ReelBolt.WorkflowEngine.Execution.Caching.DeterministicStepRevisions.For(ReelBolt.Shared.Data.Models.StepType.VideoCompile, "{\"version\":1}");
        string clips = ReelBolt.WorkflowEngine.Execution.Caching.DeterministicStepRevisions.For(ReelBolt.Shared.Data.Models.StepType.VideoCompile, "{\"version\":1,\"enableGeneratedClips\":true}");
        plain.Should().NotContain("cutaway-clip-start-1");
        clips.Should().Contain("+cutaway-clip-start-1").And.StartWith(plain);
    }

    [FfmpegToolsFact]
    public void The_first_frame_at_the_window_start_is_the_clips_first_frame()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cutaway-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string clip = Path.Combine(dir, "clip.mp4"), film = Path.Combine(dir, "film.mp4");
            Run("-f", "lavfi", "-i", "color=c=0x00ff00:s=64x64:r=30:d=1", "-f", "lavfi", "-i", "color=c=0x0000ff:s=64x64:r=30:d=1",
                "-filter_complex", "[0:v][1:v]concat=n=2:v=1[v]", "-map", "[v]", "-pix_fmt", "yuv420p", clip);

            // A window from 1.0 s to 2.5 s (frames 30..74), whole frames, as the executor writes them.
            var cutaway = new VideoCompileStepExecutor.ResolvedGeneratedClip("v1t1", "cutaway", clip, 2.0, false, "s1", 1.0, 2.5, FrameSnapped: true);
            (string branch, string overlay) = VideoCompileStepExecutor.BuildCutawayStage("[0:v]", "[gcut0]", "[out]", 1, cutaway, 64, 64, 30, 1);
            overlay.Should().Contain("between(t,0.98333").And.Contain(",2.48333");
            branch.Should().EndWith("settb=AVTB,setpts=PTS-STARTPTS+991667[gcut0]", "the clip is stamped on the window frame, a quarter frame early (QA-DYN-43)");
            Run("-f", "lavfi", "-i", "color=c=0xff0000:s=64x64:r=30:d=4", "-i", clip,
                "-filter_complex", branch + ";" + overlay, "-map", "[out]", "-pix_fmt", "yuv420p", "-c:v", "libx264", "-crf", "5", film);

            Pixel(film, 29).Should().Be("red", "the frame before the window is the base");
            Pixel(film, 30).Should().Be("green", "the window opens on the clip's first frame, not on the clip's frame at 1.0 s");
            Pixel(film, 59).Should().Be("green", "the first second of the clip plays during the first second of the window");
            Pixel(film, 61).Should().Be("blue", "then the clip's second half");
            Pixel(film, 74).Should().Be("blue", "the window's last frame (2.5 s) is still the clip");
            Pixel(film, 75).Should().Be("red", "the window closes exactly on its end frame");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
