using System;
using System.Diagnostics;
using System.IO;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A matted insert must not lengthen the film. Its synthesized masks run a margin past the
/// insert's window, and spill suppression used to merge them with <c>maskedmerge</c>, whose
/// framesync repeats every input's last frame until the longest one ends: the render ran a second
/// past its source on the held final frame, after the insert's window had closed, so every video
/// whose insert reached its end finished on a raw green screen. Only a real ffmpeg run can show it.
/// </summary>
public class ScreenInsertProgrammeLengthTests
{
    [FfmpegFact]
    public void A_matted_insert_reaching_the_last_frame_neither_extends_the_film_nor_leaves_it_green()
    {
        const int W = 320, H = 180, Frames = 24;
        string dir = Path.Combine(Path.GetTempPath(), "insert-length-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string script = Path.Combine(dir, "graph.txt"), output = Path.Combine(dir, "out.rgb");
            var insert = new ResolvedScreenInsert(
                "r0", "content", 0, Frames / 24.0,
                [new InsertQuadFrame(0, 100, 40, 220, 40, 100, 140, 220, 140)],
                24, 1,
                Matte: new VideoInsertChromaKey(0, 200, 0, 0.39, 0, 0.95, PlateDominance: 200),
                TransferReflections: true);
            File.WriteAllText(script, ScreenInsertFilterBuilder.BuildFilterChain("[0:v]", [insert], W, H, _ => 1, "[vout]"));

            var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string a in new[]
                     {
                         "-v", "error", "-y",
                         // Base: a grey frame with a green plate where the insert goes, exactly one second long.
                         "-f", "lavfi", "-i", $"color=c=gray:size={W}x{H}:rate=24:duration=1,drawbox=x=100:y=40:w=121:h=101:color=0x00C800:t=fill",
                         "-f", "lavfi", "-i", $"color=c=red:size=64x64:rate=24:duration=1",
                         "-filter_complex_script", script, "-map", "[vout]", "-f", "rawvideo", "-pix_fmt", "rgb24", output
                     })
                psi.ArgumentList.Add(a);
            using Process p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            p.ExitCode.Should().Be(0, err);

            byte[] rgb = File.ReadAllBytes(output);
            (rgb.Length / (W * H * 3)).Should().Be(Frames, "the film is as long as its base picture, not as long as the insert's padded masks");

            int last = (Frames - 1) * W * H * 3 + (90 * W + 160) * 3;
            rgb[last + 1].Should().BeLessThan(rgb[last], "the plate centre on the final frame shows the (red) insert, not green");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
