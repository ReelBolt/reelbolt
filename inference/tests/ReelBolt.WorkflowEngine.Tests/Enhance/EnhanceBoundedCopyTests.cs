using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Enhance;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Enhance;

/// <summary>The bounded stream copy a hosted enhancement uploads instead of the original (T-028).</summary>
public class EnhanceBoundedCopyTests
{
    [Fact]
    public void The_argv_pins_both_whitelists_before_the_one_input_the_stream_copy_and_both_bounds()
    {
        string[] mp4 = EnhanceBoundedCopy.BuildArgs("/s/in.mkv", "/s/out.mp4", 12.2, 366, mp4: true);

        int input = Array.IndexOf(mp4, "-i");
        input.Should().Be(Array.LastIndexOf(mp4, "-i"), "exactly one input");
        string[] before = mp4[..input];
        before[Array.IndexOf(before, "-protocol_whitelist") + 1].Should().Be("file");
        before[Array.IndexOf(before, "-format_whitelist") + 1].Should().Be(MediaDerivativeInputFormats.Whitelist);
        mp4.Should().ContainInOrder("-map", "0:v:0", "-map", "0:a?", "-sn", "-dn", "-c", "copy");
        mp4[Array.IndexOf(mp4, "-t") + 1].Should().Be("12.200000");
        mp4[Array.IndexOf(mp4, "-frames:v") + 1].Should().Be("367");
        mp4.Should().ContainInOrder("-movflags", "+faststart", "-f", "mp4", "/s/out.mp4");

        string[] mkv = EnhanceBoundedCopy.BuildArgs("/s/in.webm", "/s/out.mkv", 5, null, mp4: false);
        mkv.Should().NotContain("-frames:v", "with no known frame count only the time bound applies");
        mkv.Should().NotContain("-movflags");
        mkv.Should().ContainInOrder("-f", "matroska", "/s/out.mkv");
    }

    [Theory]
    [InlineData("h264", "aac", true)]
    [InlineData("hevc", null, true)]
    [InlineData("vp8", "opus", false)]
    [InlineData("h264", "opus", false)]
    [InlineData("theora", "vorbis", false)]
    public void Codecs_that_can_be_stream_copied_into_mp4_stay_mp4_and_the_rest_keep_matroska(string video, string? audio, bool mp4)
    {
        MediaProbeResult probe = new(10, 30, 1, 1920, 1080, video, audio, 48000);
        EnhanceBoundedCopy.FitsMp4(probe).Should().Be(mp4);
        EnhanceBoundedCopy.ExtensionFor(probe).Should().Be(mp4 ? ".mp4" : ".mkv");
        EnhanceBoundedCopy.ContentTypeFor(EnhanceBoundedCopy.ExtensionFor(probe)).Should().Be(mp4 ? "video/mp4" : "video/x-matroska");
    }

    [Fact]
    public void A_copy_longer_than_the_checked_length_by_more_than_a_frame_is_refused()
    {
        EnhanceBoundedCopy.ExceedsChecked(10.0, 10.0, 30).Should().BeFalse();
        EnhanceBoundedCopy.ExceedsChecked(10.03, 10.0, 30).Should().BeFalse("under one frame");
        EnhanceBoundedCopy.ExceedsChecked(10.2, 10.0, 30).Should().BeTrue();
        EnhanceBoundedCopy.ExceedsChecked(10.1, 10.0, 0).Should().BeTrue("an unknown rate uses a 40 ms floor");
    }

    /// <summary>
    /// METHOD: a 3 s Matroska is made with lavfi, then the EBML Duration element (ID 0x4489) in its Segment Info is patched to 1000 ms, so the
    /// container claims 1 s while the file holds 3 s (and Matroska carries no per-stream duration). The bounded copy is cut at the length
    /// that was CHECKED (the container's claim, 1 s), so what would be uploaded is 1 s, not the true 3 s the vendor would bill.
    /// </summary>
    [FfmpegToolsFact]
    public void A_patched_matroska_duration_cannot_make_the_upload_longer_than_the_checked_length()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rb-bounded-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string real = Path.Combine(dir, "real.mkv");
            Run("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=24:duration=3",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", real]);
            Measure(real).Frames.Should().BeGreaterThan(60, "the file really holds about 72 frames");

            string patched = Path.Combine(dir, "patched.mkv");
            byte[] bytes = File.ReadAllBytes(real);
            PatchDuration(bytes, 1000.0);
            File.WriteAllBytes(patched, bytes);
            (double claimed, _) = FormatAndStream(patched);
            claimed.Should().BeApproximately(1.0, 0.05, "the container now claims one second");

            string copy = Path.Combine(dir, "copy.mp4");
            Run("ffmpeg", EnhanceBoundedCopy.BuildArgs(patched, copy, claimed, frames(claimed, 24), mp4: true));

            (double copySeconds, long copyFrames) = Measure(copy);
            copySeconds.Should().BeLessThanOrEqualTo(claimed + 0.05, "the upload is bounded by the checked length");
            copyFrames.Should().BeLessThanOrEqualTo(frames(claimed, 24)!.Value + 1);
            EnhanceBoundedCopy.ExceedsChecked(copySeconds, claimed, 24).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        static long? frames(double seconds, int fps) => (long)Math.Ceiling(seconds * fps);
    }

    private static void PatchDuration(byte[] bytes, double milliseconds)
    {
        for (int i = 0; i < bytes.Length - 10; i++)
        {
            if (bytes[i] != 0x44 || bytes[i + 1] != 0x89)
                continue;

            if (bytes[i + 2] == 0x88)
            {
                BitConverter.GetBytes(milliseconds).Reverse().ToArray().CopyTo(bytes, i + 3);
                return;
            }

            if (bytes[i + 2] == 0x84)
            {
                BitConverter.GetBytes((float)milliseconds).Reverse().ToArray().CopyTo(bytes, i + 3);
                return;
            }
        }

        throw new InvalidOperationException("no Duration element found in the Matroska file");
    }

    private static (double Seconds, long Frames) Measure(string path)
    {
        string json = Capture("ffprobe", ["-v", "error", "-select_streams", "v:0", "-count_packets", "-show_entries",
            "stream=nb_read_packets:format=duration", "-of", "default=nw=1", path]);
        double seconds = 0;
        long frames = 0;
        foreach (string line in json.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("duration=", StringComparison.Ordinal) && double.TryParse(line[9..], NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                seconds = d;
            if (line.StartsWith("nb_read_packets=", StringComparison.Ordinal))
                frames = long.Parse(line[16..], CultureInfo.InvariantCulture);
        }

        return (seconds, frames);
    }

    private static (double Format, string Stream) FormatAndStream(string path)
    {
        string text = Capture("ffprobe", ["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path]);
        return (double.Parse(text.Trim(), CultureInfo.InvariantCulture), string.Empty);
    }

    private static void Run(string tool, IEnumerable<string> args) => Capture(tool, args);

    private static string Capture(string tool, IEnumerable<string> args)
    {
        ProcessStartInfo info = new(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args)
            info.ArgumentList.Add(a);
        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, error);
        return output;
    }
}
