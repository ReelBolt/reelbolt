using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>R08.4: the measuring pass reads a generated -23 LUFS tone within 0.5 LU (real ffmpeg; skipped without one).</summary>
public class LoudnessToneFixtureTests
{
    [FfmpegFact]
    public async Task A_generated_minus_23_lufs_tone_measures_within_half_a_lu()
    {
        string dir = Path.Combine(Path.GetTempPath(), "loud-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            string tone = Path.Combine(dir, "tone.wav");
            // 1 kHz sine (ffmpeg default peak 0.125, x -2.0 dB; the K-weighting adds about +0.7 at 1 kHz) -> about -23 LUFS (10*log10(A^2/2) - 0.691).
            await Run(["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "sine=frequency=1000:duration=8:sample_rate=48000", "-af", "volume=-2.0dB", tone]);
            string stderr = await Run(LoudnessNormalizer.MeasureArgs(tone, (-23, -2, 15)));

            LoudnessMeasurement? m = LoudnessNormalizer.ParseMeasurement(stderr);

            m.Should().NotBeNull();
            m!.IntegratedLufs.Should().BeApproximately(-23, 0.5);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static async Task<string> Run(System.Collections.Generic.IReadOnlyList<string> args)
    {
        ProcessStartInfo psi = new("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return await err;
    }
}
