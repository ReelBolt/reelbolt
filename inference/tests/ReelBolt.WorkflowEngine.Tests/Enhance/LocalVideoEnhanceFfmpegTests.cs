using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Enhance;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Enhance;

/// <summary>
/// End-to-end runs of the local engines against tiny lavfi fixtures. Every test is at most 2 s and
/// 320x180 (except the one upscale) so the suite stays fast; each skips when ffmpeg is absent.
/// </summary>
public class LocalVideoEnhanceFfmpegTests
{
    // ---- denoise: measured PSNR gain over the noisy source ----

    [FfmpegToolsFact]
    public async Task Denoise_light_and_strong_raise_psnr_against_the_clean_reference()
    {
        string dir = NewDir();
        try
        {
            // Lossless x264 MP4 fixtures keep them CFR (an ffv1 Matroska from lavfi has jittery
            // timestamps that make a CFR re-encode duplicate/drop the odd frame and poison PSNR).
            string clean = Path.Combine(dir, "clean.mp4");
            string noisy = Path.Combine(dir, "noisy.mp4");
            Run("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=24:duration=2",
                "-c:v", "libx264", "-qp", "0", "-pix_fmt", "yuv420p", clean]);
            Run("ffmpeg", ["-v", "error", "-y", "-f", "lavfi",
                "-i", "testsrc2=size=160x90:rate=24:duration=2,noise=alls=20:allf=t+u",
                "-c:v", "libx264", "-qp", "0", "-pix_fmt", "yuv420p", noisy]);

            double baseline = Psnr(noisy, clean);
            foreach (DenoiseStrength strength in new[] { DenoiseStrength.Light, DenoiseStrength.Strong })
            {
                string output = Path.Combine(dir, $"denoise-{strength}.mp4");
                LocalEnhanceResult result = await NewEnhancer().EnhanceAsync(
                    noisy, output,
                    new LocalEnhanceRequest { Op = VideoEnhanceOp.Denoise, DenoiseStrength = strength },
                    null, CancellationToken.None);

                result.OutWidth.Should().Be(160);
                result.OutHeight.Should().Be(90);
                result.DurationSec.Should().BeApproximately(2.0, 0.05);
                double psnr = Psnr(output, clean);
                (psnr - baseline).Should().BeGreaterThanOrEqualTo(
                    2.0, $"{strength} denoise must raise PSNR from {baseline:F2} dB by at least 2 dB (got {psnr:F2} dB).");

                // Edges survive: of the clean reference's strong Sobel edges (|gx|+|gy| > 300 on a
                // 160x90 gray frame), at least 90% are still strong after denoising. Measured
                // recall is about 0.97 for both strengths; a blurring denoiser falls well below.
                HashSet<int> cleanEdges = StrongEdges(clean);
                HashSet<int> keptEdges = StrongEdges(output);
                double recall = cleanEdges.Count(keptEdges.Contains) / (double)cleanEdges.Count;
                recall.Should().BeGreaterThanOrEqualTo(
                    0.90, $"{strength} denoise must keep the clean edges (recall {recall:F3}).");
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- motion blur: the trail follows the shutter angle ----

    [FfmpegToolsFact]
    public async Task Motion_blur_trail_matches_the_shutter_angle()
    {
        string dir = NewDir();
        try
        {
            // A white 8 px bar on black moves 8 px per 12 fps source frame.
            string source = Path.Combine(dir, "bar.mkv");
            Run("ffmpeg", ["-v", "error", "-y", "-f", "lavfi",
                "-i", "color=black:s=160x90:r=12:d=1", "-f", "lavfi", "-i", "color=white:s=8x30:r=12:d=1",
                "-filter_complex", "[0][1]overlay=x='10+mod(t*96,120)':y=30", "-c:v", "ffv1", source]);

            int span180 = await BarSpanAsync(dir, source, 180);
            int span360 = await BarSpanAsync(dir, source, 360);

            // At 180 degrees tmix averages 4 of the 8x sub-frames, so the bar is ~N/2 = 4 px longer;
            // at 360 degrees all 8, so ~N = 8 px. Tolerance is +/-2 px on the extra length.
            (span180 - 8).Should().BeInRange(2, 6, "180 degrees should leave a trail of about 4 px");
            (span360 - 8).Should().BeInRange(6, 10, "360 degrees should leave a trail of about 8 px");
            span360.Should().BeGreaterThan(span180);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- repair: pad and trim leave every source audio stream untouched ----

    [FfmpegToolsFact]
    public void Pad_and_trim_repairs_keep_both_audio_streams_packet_identical()
    {
        string dir = NewDir();
        try
        {
            string input = Path.Combine(dir, "two-audio.mp4");
            Run("ffmpeg", ["-v", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=24:duration=1.5",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-f", "lavfi", "-i", "sine=frequency=880:duration=2",
                "-map", "0:v", "-map", "1:a", "-map", "2:a",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", input]);
            AudioStreams(input).Should().Be(2);

            string padded = Path.Combine(dir, "padded.mp4");
            Run("ffmpeg", EnhanceDuration.BuildPadArgs(input, padded, 2.0, 1.5));
            string trimmed = Path.Combine(dir, "trimmed.mp4");
            Run("ffmpeg", EnhanceDuration.BuildTrimArgs(input, trimmed, 1.0));

            foreach (string repaired in new[] { padded, trimmed })
            {
                AudioStreams(repaired).Should().Be(2);
                StreamMd5(repaired, 0, trimmed == repaired ? 1.0 : 2.0)
                    .Should().Be(StreamMd5(input, 0, trimmed == repaired ? 1.0 : 2.0));
                StreamMd5(repaired, 1, trimmed == repaired ? 1.0 : 2.0)
                    .Should().Be(StreamMd5(input, 1, trimmed == repaired ? 1.0 : 2.0));
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- interpolate: N x frames, same duration ----

    [FfmpegToolsFact]
    public async Task Interpolate_doubles_frames_and_keeps_the_duration()
    {
        string dir = NewDir();
        try
        {
            string source = Path.Combine(dir, "in.mp4");
            Run("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=24:duration=2",
                "-c:v", "libx264", "-crf", "23", "-pix_fmt", "yuv420p", source]);

            string output = Path.Combine(dir, "out.mp4");
            LocalEnhanceResult result = await NewEnhancer().EnhanceAsync(
                source, output,
                new LocalEnhanceRequest { Op = VideoEnhanceOp.Interpolate, FrameMultiplier = 2 },
                null, CancellationToken.None);

            result.OutFps.Should().Be(48);
            result.OutWidth.Should().Be(160);
            result.OutHeight.Should().Be(90);
            result.DurationSec.Should().BeApproximately(2.0, 0.05);
            FrameCount(output).Should().BeInRange(95, 97);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- upscale: size as asked, fps and duration unchanged ----

    [FfmpegToolsFact]
    public async Task Upscale_doubles_the_size_and_keeps_fps_and_duration()
    {
        string dir = NewDir();
        try
        {
            string source = Path.Combine(dir, "in.mp4");
            Run("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24:duration=1",
                "-c:v", "libx264", "-crf", "23", "-pix_fmt", "yuv420p", source]);

            string output = Path.Combine(dir, "out.mp4");
            LocalEnhanceResult result = await NewEnhancer().EnhanceAsync(
                source, output,
                new LocalEnhanceRequest { Op = VideoEnhanceOp.Upscale, UpscaleFactor = 2 },
                null, CancellationToken.None);

            result.OutWidth.Should().Be(640);
            result.OutHeight.Should().Be(360);
            result.OutFps.Should().Be(24);
            result.DurationSec.Should().BeApproximately(1.0, 0.05);
            ProbeValue(output, "width").Should().Be("640");
            ProbeValue(output, "height").Should().Be("360");
            ProbeValue(output, "r_frame_rate").Should().Be("24/1");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- audio: copied packets and no invented audio ----

    [FfmpegToolsFact]
    public async Task Source_audio_is_packet_identical_and_silent_source_stays_silent()
    {
        string dir = NewDir();
        try
        {
            string withAudio = Path.Combine(dir, "audio.mp4");
            Run("ffmpeg", [
                "-v", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=24:duration=1",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=1:sample_rate=48000",
                "-c:v", "libx264", "-crf", "23", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-shortest", withAudio]);

            string output = Path.Combine(dir, "out.mp4");
            LocalEnhanceResult result = await NewEnhancer().EnhanceAsync(
                withAudio, output,
                new LocalEnhanceRequest { Op = VideoEnhanceOp.Denoise, DenoiseStrength = DenoiseStrength.Light },
                null, CancellationToken.None);

            result.Warnings.Should().BeEmpty();
            PacketMd5(output).Should().Be(PacketMd5(withAudio));

            string silent = Path.Combine(dir, "silent.mp4");
            Run("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=24:duration=1",
                "-c:v", "libx264", "-crf", "23", "-pix_fmt", "yuv420p", silent]);
            string silentOut = Path.Combine(dir, "silent-out.mp4");
            await NewEnhancer().EnhanceAsync(
                silent, silentOut,
                new LocalEnhanceRequest { Op = VideoEnhanceOp.Denoise, DenoiseStrength = DenoiseStrength.Light },
                null, CancellationToken.None);

            AudioStreams(silentOut).Should().Be(0);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- remux: picture-only file + original audio ----

    [FfmpegToolsFact]
    public async Task Remux_puts_the_source_audio_back_on_a_picture_only_file()
    {
        string dir = NewDir();
        try
        {
            string source = Path.Combine(dir, "source.mp4");
            Run("ffmpeg", [
                "-v", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=24:duration=1",
                "-f", "lavfi", "-i", "sine=frequency=330:duration=1:sample_rate=48000",
                "-c:v", "libx264", "-crf", "23", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-shortest", source]);

            string picture = Path.Combine(dir, "picture.mp4");
            Run("ffmpeg", ["-v", "error", "-y", "-i", source, "-an", "-c:v", "copy", picture]);
            AudioStreams(picture).Should().Be(0);

            string output = Path.Combine(dir, "remuxed.mp4");
            LocalEnhanceResult result = await NewRemux().RemuxAsync(
                source, picture, output, new RemuxTarget(160, 90, 24, 1), CancellationToken.None);

            result.Method.Should().Be("remux+copy");
            result.DurationSec.Should().BeApproximately(1.0, 0.05);
            PacketMd5(output).Should().Be(PacketMd5(source));
            AudioStreams(output).Should().Be(1);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- helpers ----

    private static LocalVideoEnhancer NewEnhancer()
    {
        var opts = Options.Create(new VideoEditingOptions());
        var runner = new FfmpegVideoToolRunner(opts, NullLogger<FfmpegVideoToolRunner>.Instance);
        var probe = new FfprobeMediaProbe(runner, opts, NullLogger<FfprobeMediaProbe>.Instance);
        return new LocalVideoEnhancer(runner, probe, Options.Create(new LocalEnhanceOptions()));
    }

    private static EnhancedAudioRemux NewRemux()
    {
        var opts = Options.Create(new VideoEditingOptions());
        var runner = new FfmpegVideoToolRunner(opts, NullLogger<FfmpegVideoToolRunner>.Instance);
        var probe = new FfprobeMediaProbe(runner, opts, NullLogger<FfprobeMediaProbe>.Instance);
        return new EnhancedAudioRemux(runner, probe, Options.Create(new LocalEnhanceOptions()));
    }

    private static async Task<int> BarSpanAsync(string dir, string source, int angle)
    {
        string output = Path.Combine(dir, $"blur-{angle}.mp4");
        await NewEnhancer().EnhanceAsync(
            source, output,
            new LocalEnhanceRequest { Op = VideoEnhanceOp.MotionBlur, ShutterAngle = angle },
            null, CancellationToken.None);
        byte[] gray = RunCapture("ffmpeg", ["-v", "error", "-ss", "0.5", "-i", output,
            "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "-"]);
        int min = int.MaxValue, max = int.MinValue;
        for (int y = 0; y < 90; y++)
        {
            for (int x = 0; x < 160; x++)
            {
                if (gray[y * 160 + x] > 30)
                {
                    min = Math.Min(min, x);
                    max = Math.Max(max, x);
                }
            }
        }

        return max - min + 1;
    }

    private static HashSet<int> StrongEdges(string path)
    {
        const int w = 160, h = 90, threshold = 300;
        byte[] g = RunCapture("ffmpeg", ["-v", "error", "-ss", "0.5", "-i", path,
            "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "-"]);
        var edges = new HashSet<int>();
        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                int gx = g[((y - 1) * w) + x + 1] + (2 * g[(y * w) + x + 1]) + g[((y + 1) * w) + x + 1]
                    - g[((y - 1) * w) + x - 1] - (2 * g[(y * w) + x - 1]) - g[((y + 1) * w) + x - 1];
                int gy = g[((y + 1) * w) + x - 1] + (2 * g[((y + 1) * w) + x]) + g[((y + 1) * w) + x + 1]
                    - g[((y - 1) * w) + x - 1] - (2 * g[((y - 1) * w) + x]) - g[((y - 1) * w) + x + 1];
                if (Math.Abs(gx) + Math.Abs(gy) > threshold)
                {
                    edges.Add((y * w) + x);
                }
            }
        }

        return edges;
    }

    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rb-enhance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Run(string exe, string[] args)
    {
        (int code, _, string err) = Start(exe, args, capture: false);
        code.Should().Be(0, err);
    }

    private static byte[] RunCapture(string exe, string[] args)
    {
        (int code, byte[] stdout, string err) = StartBytes(exe, args);
        code.Should().Be(0, err);
        return stdout;
    }

    private static (int ExitCode, string StdOut, string StdErr) Start(string exe, string[] args, bool capture)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        string output = capture ? p.StandardOutput.ReadToEnd() : string.Empty;
        if (!capture)
        {
            p.StandardOutput.ReadToEnd();
        }

        p.WaitForExit();
        return (p.ExitCode, output, err);
    }

    private static (int ExitCode, byte[] StdOut, string StdErr) StartBytes(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        using var buffer = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(buffer);
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, buffer.ToArray(), err);
    }

    private static string ProbeCsv(string path, string entries)
    {
        (int code, string stdout, string err) = Start("ffprobe", [
            "-v", "error", "-select_streams", "v:0", "-count_frames",
            "-show_entries", entries, "-of", "default=nw=1:nk=1", path], capture: true);
        code.Should().Be(0, err);
        return stdout.Trim();
    }

    private static string ProbeValue(string path, string field) => ProbeCsv(path, $"stream={field}");

    private static int FrameCount(string path) =>
        int.Parse(ProbeCsv(path, "stream=nb_read_frames"), CultureInfo.InvariantCulture);

    private static int AudioStreams(string path)
    {
        (int code, string stdout, string err) = Start("ffprobe", [
            "-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", path], capture: true);
        code.Should().Be(0, err);
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(line => line.Trim() == "audio");
    }

    /// <summary>MD5 of one audio stream's copied packets, cut at <paramref name="seconds"/> by -t.</summary>
    private static string StreamMd5(string path, int audioIndex, double seconds)
    {
        (int code, string output, string err) = Start("ffmpeg", [
            "-v", "error", "-i", path, "-map", $"0:a:{audioIndex}", "-t",
            seconds.ToString("F3", CultureInfo.InvariantCulture), "-c", "copy", "-f", "md5", "-"], capture: true);
        code.Should().Be(0, err);
        return output.Trim();
    }

    private static string PacketMd5(string path)
    {
        (int code, string stdout, string err) = Start("ffmpeg", [
            "-v", "error", "-i", path, "-map", "0:a", "-c", "copy", "-f", "md5", "-"], capture: true);
        code.Should().Be(0, err);
        return stdout.Trim();
    }

    private static double Psnr(string candidate, string reference)
    {
        (int code, _, string err) = Start("ffmpeg", [
            "-v", "info", "-i", candidate, "-i", reference,
            "-lavfi", "[0:v][1:v]psnr", "-f", "null", "-"], capture: false);
        code.Should().Be(0, err);
        Match match = Regex.Match(err, @"average:([0-9.]+)");
        match.Success.Should().BeTrue($"ffmpeg psnr output should carry an average ({err})");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}
