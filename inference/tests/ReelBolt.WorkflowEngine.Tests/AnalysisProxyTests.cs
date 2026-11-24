#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class AnalysisProxyTests
{
    private static MediaProbeResult Probe(double sec, int w, int h) => new(sec, 30, 1, w, h, "h264", "aac", 48000);

    [Theory]
    [InlineData(120, 3840, 2160, 120, true)]
    [InlineData(119.9, 3840, 2160, 120, false)]
    [InlineData(600, 1920, 1080, 120, false)]
    [InlineData(600, 2160, 3840, 120, true)]
    [InlineData(600, 3840, 2160, 0, false)]
    public void Only_long_sources_with_a_short_side_above_1080_get_a_copy(double sec, int w, int h, int minSec, bool expected) =>
        AnalysisProxy.Eligible(Probe(sec, w, h), minSec).Should().Be(expected);

    [Fact]
    public void The_copy_argv_is_exact_silent_keeps_every_frame_and_tone_maps_after_scaling()
    {
        string scale = "scale=w='if(gt(iw,ih),-2,540)':h='if(gt(iw,ih),540,-2)':flags=bilinear";
        string[] sdr = AnalysisProxy.Build("in.mp4", "out.mkv", 4, null);
        string.Join(' ', sdr).Should().Be(
            "-y -nostdin -hide_banner -loglevel error -protocol_whitelist file -format_whitelist " + UserMediaInputFormats.Whitelist + " -threads 4 -i in.mp4 -an -sn -dn " +
            $"-vf {scale} -fps_mode passthrough -c:v libx264 -preset ultrafast -crf 26 -g 30 -pix_fmt yuv420p -threads 4 out.mkv");
        string[] hdr = AnalysisProxy.Build("in.mp4", "out.mkv", 4, "hlg");
        hdr[hdr.ToList().IndexOf("-vf") + 1].Should().Be(scale + "," + HdrToneMap.AnalysisFilter("hlg"));
        HdrToneMap.AnalysisFilter("hlg").Should().Be("zscale=tin=arib-std-b67:t=bt709:min=bt2020nc:m=bt709:pin=bt2020:p=bt709:r=tv,format=yuv420p");
        HdrToneMap.AnalysisFilter("pq").Should().Contain("tin=smpte2084");
        HdrToneMap.AnalysisFilter("pq").Should().NotContain("tonemap").And.NotContain("gbrpf32le");
        // Thinned to 30 fps first only when the source is faster; at or below 30 nothing changes.
        string[] fast = AnalysisProxy.Build("in.mp4", "out.mkv", 4, null, 59.94);
        fast[fast.ToList().IndexOf("-vf") + 1].Should().Be("fps=30," + scale);
        AnalysisProxy.Build("in.mp4", "out.mkv", 4, null, 30).Should().Equal(sdr);
        AnalysisProxy.Build("in.mp4", "out.mkv", 4, null, 29.97).Should().Equal(sdr);

        VideoEditingOptions o = new();
        o.ResolveAnalysisProxyThreads().Should().Be(Environment.ProcessorCount);
        o.AnalysisProxyThreads = 2;
        o.ResolveAnalysisProxyThreads().Should().Be(Math.Min(2, Environment.ProcessorCount));
        o.AnalysisProxyThreads = 100_000;
        o.ResolveAnalysisProxyThreads().Should().Be(Environment.ProcessorCount, "bounded to the core count");
    }

    [Fact]
    public void Cut_times_found_on_the_copy_agree_with_the_source_within_one_frame()
    {
        if (!Have("ffmpeg")) return;
        string dir = Path.Combine(Path.GetTempPath(), "apx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string src = Path.Combine(dir, "src.mp4"), copy = Path.Combine(dir, "copy.mkv");
            Run(["-y", "-f", "lavfi", "-i", "color=c=red:s=1280x720:r=30:d=2", "-f", "lavfi", "-i", "color=c=blue:s=1280x720:r=30:d=2",
                 "-f", "lavfi", "-i", "color=c=green:s=1280x720:r=30:d=2", "-filter_complex", "[0][1][2]concat=n=3:v=1:a=0",
                 "-c:v", "libx264", "-pix_fmt", "yuv420p", src]);
            Run(AnalysisProxy.Build(src, copy, 2, null));
            double[] a = Cuts(src), b = Cuts(copy);
            a.Should().HaveCount(2);
            b.Should().HaveCount(a.Length);
            for (int i = 0; i < a.Length; i++) Math.Abs(a[i] - b[i]).Should().BeLessThan(1.0 / 30);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void A_cut_found_on_the_30_fps_copy_of_a_60_fps_clip_lands_within_two_source_frames()
    {
        if (!Have("ffmpeg")) return;
        string dir = Path.Combine(Path.GetTempPath(), "apx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string src = Path.Combine(dir, "src.mp4"), copy = Path.Combine(dir, "copy.mkv");
            Run(["-y", "-f", "lavfi", "-i", "color=c=red:s=1280x720:r=60:d=1", "-f", "lavfi", "-i", "color=c=blue:s=1280x720:r=60:d=1",
                 "-f", "lavfi", "-i", "color=c=green:s=1280x720:r=60:d=1", "-filter_complex", "[0][1][2]concat=n=3:v=1:a=0",
                 "-c:v", "libx264", "-pix_fmt", "yuv420p", src]);
            Run(AnalysisProxy.Build(src, copy, 2, null, 60));
            AssertCutsAgree(Cuts(src), Cuts(copy), expectedCount: 2);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [HdrFact]
    public void Cuts_on_the_30_fps_copy_of_a_60_fps_HDR_clip_agree_with_the_source_through_the_cheap_conversion()
    {
        string dir = Path.Combine(Path.GetTempPath(), "apx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string src = Path.Combine(dir, "hdr60.mkv"), copy = Path.Combine(dir, "copy.mkv");
            // Real hard cuts (white, black, white), 60 fps, 10-bit, tagged HLG / BT.2020 like phone HDR footage.
            Run(["-y", "-f", "lavfi", "-i", "color=c=white:s=1280x720:r=60:d=1", "-f", "lavfi", "-i", "color=c=black:s=1280x720:r=60:d=1",
                 "-f", "lavfi", "-i", "color=c=white:s=1280x720:r=60:d=1", "-filter_complex", "[0][1][2]concat=n=3:v=1:a=0,format=yuv420p10le",
                 "-c:v", "libx265", "-preset", "ultrafast",
                 "-x265-params", "colorprim=bt2020:transfer=arib-std-b67:colormatrix=bt2020nc:log-level=none", src]);
            ProbeTags(src).Should().Contain("arib-std-b67", "the clip must really be HLG-tagged for this to test the HDR path");

            Run(AnalysisProxy.Build(src, copy, 2, "hlg", 60));

            AssertCutsAgree(Cuts(src), Cuts(copy), expectedCount: 2);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private static string ProbeTags(string path)
    {
        ProcessStartInfo psi = new("ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-v", "error", "-show_entries", "stream=color_transfer", "-of", "csv=p=0", path }) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o;
    }

    private static void AssertCutsAgree(double[] source, double[] copy, int? expectedCount)
    {
        if (expectedCount is { } n) source.Should().HaveCount(n);
        copy.Should().HaveCount(source.Length);
        for (int i = 0; i < source.Length; i++) Math.Abs(source[i] - copy[i]).Should().BeLessThanOrEqualTo(2.0 / 60 + 1e-6);
    }

    private static bool Have(string exe)
    {
        try { using Process? p = Process.Start(new ProcessStartInfo(exe, "-version") { RedirectStandardOutput = true, RedirectStandardError = true }); p?.WaitForExit(); return p?.ExitCode == 0; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static string Run(IEnumerable<string> args)
    {
        ProcessStartInfo psi = new("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in new[] { "-nostdin", "-hide_banner", "-loglevel", "info" }.Concat(args)) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return err;
    }

    private static double[] Cuts(string path) =>
        System.Text.RegularExpressions.Regex.Matches(
                Run(["-i", path, "-an", "-vf", "select='gt(scene,0.3)',showinfo", "-f", "null", "-"]), @"pts_time:([0-9.]+)")
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
}
