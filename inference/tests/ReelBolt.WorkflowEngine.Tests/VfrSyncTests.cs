using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Variable-frame-rate sources keep picture and sound in sync (vfr-sync-1): the single-source cut
/// normalizes to the probed constant rate before the select. The filter-string tests are exact; the
/// real-ffmpeg test measures picture length, sound length and a marker per span on synthetic VFR.
/// </summary>
public class VfrSyncTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static VideoCompileStepExecutor.ResolvedSpan Span(double s, double e) => new(s, e, s, e, 0, 0);

    [Theory]
    [InlineData(30000, 1001, "30000/1001")]
    [InlineData(60, 1, "60/1")]
    [InlineData(120, 1, "120/1")]
    [InlineData(240, 1, "240/1")]
    [InlineData(241, 1, null)]
    [InlineData(1000, 1, null)]
    [InlineData(0, 0, null)]
    [InlineData(30, 0, null)]
    public void Normalize_rate_keeps_real_high_rate_footage_and_refuses_absurd_or_unknown_rates(int num, int den, string? expected) =>
        VideoCompileStepExecutor.VfrNormalizeRate(num, den).Should().Be(expected);

    [Fact]
    public void Select_cut_normalizes_before_the_cut_and_bounds_the_hold()
    {
        VideoCompileStepExecutor.BuildSelectCutFilter([Span(1, 3), Span(20, 23)], "30000/1001")
            .Should().Be("fps=30000/1001:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=23.066733," +
                         "select='gte(t,1)*lt(t,3)+gte(t,20)*lt(t,23)',setpts=N/FRAME_RATE/TB");
    }

    [Fact]
    public void Select_cut_without_a_usable_rate_is_todays_filter_and_tone_map_still_follows()
    {
        VideoCompileStepExecutor.BuildSelectCutFilter([Span(1, 3)], null)
            .Should().Be("select='gte(t,1)*lt(t,3)',setpts=N/FRAME_RATE/TB");
        string hdr = VideoCompileStepExecutor.BuildSelectCutFilter([Span(1, 3)], "60/1") + "," + HdrToneMap.Filter("HLG");
        hdr.Should().StartWith("fps=60/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=3.033333,select=").And.Contain("setpts=N/FRAME_RATE/TB,");
    }

    [Fact]
    public void Insert_dip_cut_normalizes_when_the_rate_is_usable_and_a_duration_is_known()
    {
        VideoCompileStepExecutor.BuildInsertDipFilter([(2.8, 3.6)], InsertContentDipMode.Cut, 30, 1, 10)
            .Should().Be("fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=10.066667," +
                         "select='not(between(t,2.8,3.6))',setpts=N/FRAME_RATE/TB");
    }

    [Fact]
    public void Insert_dip_cut_normalizes_only_with_a_usable_rate_and_a_duration()
    {
        VideoCompileStepExecutor.BuildInsertDipFilter([(2.8, 3.6)], InsertContentDipMode.Cut, 30, 1, 0)
            .Should().Be("select='not(between(t,2.8,3.6))',setpts=N/FRAME_RATE/TB");
        VideoCompileStepExecutor.BuildInsertDipFilter([(2.8, 3.6)], InsertContentDipMode.Cut, 1000, 1, 10)
            .Should().Be("select='not(between(t,2.8,3.6))',setpts=N/FRAME_RATE/TB");
    }

    [Fact]
    public void Segmented_reframe_branch_carries_the_normalize_head_before_the_trim()
    {
        string head = VideoCompileStepExecutor.VfrNormalizeHead("30/1", 4);
        IReadOnlyList<string>? parts = ReframeFilterBuilder.SpanVideoChain(new ReframeSpanChain(
            0, 1, "1", "4", "crop=10:10", "", false, 0, false, 160, 120, 30, 1, "", "", null, head));
        parts![0].Should().StartWith("[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=4.066667,trim=start=1:end=4,");
    }

    /// <summary>
    /// The fix must not touch a constant-rate source: the same spans, encoded with the plain
    /// select filter and with the normalize head, are the SAME BYTES. Measured on ffmpeg 8.1:
    /// x264 and x265 sources, 29.97 and 60 fps, 2 and 8 spans, all eight identical. This holds
    /// because <c>fps=R</c> at the source's own rate keeps every frame, and the tail hold is cut
    /// two frames past the last span, so it never decides which frames the select keeps.
    /// </summary>
    [Fact]
    public void Constant_rate_output_is_byte_identical_with_and_without_the_normalize_head()
    {
        if (FfmpegToolsOnPath.MissingBinary() is not null)
            return; // no ffmpeg on this machine: nothing to measure.
        string encoders = RunStdout("-encoders");
        if (!encoders.Contains("libx264", StringComparison.Ordinal))
            return; // the compile encodes libx264; without it there is no output to compare.
        bool hasX265 = encoders.Contains("libx265", StringComparison.Ordinal);

        string dir = Path.Combine(Path.GetTempPath(), "vfr-cfr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var report = new List<string>();
            var spanSets = new (string Name, (double S, double E)[] Spans)[]
            {
                ("2", [(1, 3), (5, 8)]),
                ("8", [(0, 1), (2, 3), (4, 5), (6, 7), (8, 9), (10, 11), (12, 13), (14, 15)]),
            };
            foreach (string codec in hasX265 ? new[] { "x264", "x265" } : new[] { "x264" })
            {
                foreach ((int num, int den) in new[] { (30000, 1001), (60, 1) })
                {
                    string rate = $"{num}/{den}";
                    string source = Path.Combine(dir, $"{codec}-{num}-{den}.mp4");
                    string encodeSource = codec == "x265"
                        ? "-c:v libx265 -preset ultrafast -x265-params log-level=error"
                        : "-c:v libx264 -preset ultrafast -bf 3";
                    Run($"-f lavfi -i \"testsrc2=s=160x120:r={rate}:d=20\" {encodeSource} \"{source}\"");

                    foreach ((string name, (double S, double E)[] plan) in spanSets)
                    {
                        List<VideoCompileStepExecutor.ResolvedSpan> spans = plan.Select(p => Span(p.S, p.E)).ToList();
                        string plain = $"select='{VideoCompileStepExecutor.BetweenTermsOf(spans)}',setpts=N/FRAME_RATE/TB";
                        string normalized = VideoCompileStepExecutor.BuildSelectCutFilter(spans, rate);
                        string[] digests = new string[2];
                        foreach ((string label, string filter) in new[] { ("plain", plain), ("normalized", normalized) })
                        {
                            string output = Path.Combine(dir, $"{label}.mp4");
                            Run($"-i \"{source}\" -filter_complex \"[0:v]{filter}[v]\" -map \"[v]\" " +
                                $"-c:v libx264 -crf 20 -preset veryfast -pix_fmt yuv420p -movflags +faststart \"{output}\"");
                            digests[label == "plain" ? 0 : 1] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output)));
                        }

                        report.Add($"{codec} {rate} {name} spans: {digests[0][..12]} / {digests[1][..12]}");
                        digests[1].Should().Be(digests[0],
                            $"a constant-rate source must encode byte-identically ({codec}, {rate}, {name} spans):\n" +
                            string.Join("\n", report));
                    }
                }
            }

            output.WriteLine(string.Join("\n", report));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- real ffmpeg ----

    private static readonly (double S, double E)[] Plan = [(1, 3), (5, 8), (12, 13.5), (20, 23)];
    private const double PlanSec = 9.5;
    // Source markers: a 0.2 s white flash on the picture and a 1 kHz burst on the sound, 0.5 s into every span.
    private static string Markers() => string.Join("+", Plan.Select(p => $"between(t,{F(p.S + 0.5)},{F(p.S + 0.7)})"));

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    [FfmpegCutsAudioWithAselectFact]
    public void Compiled_picture_and_sound_match_the_plan_and_every_marker_lines_up_on_vfr_sources()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vfr-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string m = Markers();
            // The VFR variants never drop a marker frame, so the flash is always in the picture (the no-frame span is the exception).
            var variants = new (string Name, string Select, bool GapSpan)[]
            {
                ("cfr", "1", false),
                ("vfr3", $"not(eq(mod(n,33),5))+{m}", false),
                ("vfr14", $"not(eq(mod(n,7),3))+{m}", false),
                ("screen", $"eq(mod(n,15),0)+{m}", false),
                ("noframespan", "not(between(t,11.5,14))", true),
            };
            string audioSrc = Path.Combine(dir, "a.wav");
            Run($"-f lavfi -i \"aevalsrc='sin(2*PI*1000*t)*({m})':s=48000:d=30\" \"{audioSrc}\"");
            var spans = Plan.Select(p => Span(p.S, p.E)).ToList();
            string terms = VideoCompileStepExecutor.BetweenTermsOf(spans);
            var report = new List<string>();
            bool oldFilterWasWrong = false;
            foreach ((string name, string sel, bool gap) in variants)
            {
                string src = Path.Combine(dir, name + ".mp4");
                string srcVf = sel == "1" ? "null" : $"select='{sel}'";
                string drawn = "drawbox=x=0:y=0:w=iw:h=ih:t=fill:c=white:enable='" + m + "'";
                Run($"-f lavfi -i \"color=c=black:s=160x120:r=30:d=30\" -i \"{audioSrc}\" -vf \"{drawn},{srcVf}\" -fps_mode passthrough " +
                    $"-c:v libx264 -preset ultrafast -c:a aac \"{src}\"");

                foreach (bool fixedFilter in new[] { false, true })
                {
                    string vf = VideoCompileStepExecutor.BuildSelectCutFilter(spans, fixedFilter ? "30/1" : null);
                    string output = Path.Combine(dir, $"{name}-{(fixedFilter ? "new" : "old")}.mp4");
                    Run($"-i \"{src}\" -filter_complex \"[0:v]{vf}[v];[0:a]aselect='{terms}',asetpts=N/SR/TB[a]\" -map [v] -map [a] " +
                        $"-c:v libx264 -preset ultrafast -c:a aac \"{output}\"", allowFailure: !fixedFilter);
                    if (!File.Exists(output))
                    {
                        report.Add($"{name} {(fixedFilter ? "new" : "old")}: no output");
                        if (!fixedFilter) { oldFilterWasWrong = true; continue; }
                    }

                    double v = StreamSec(output, "v"), a = StreamSec(output, "a");
                    report.Add($"{name} {(fixedFilter ? "new" : "old")}: picture {v:0.00} s, sound {a:0.00} s, plan {PlanSec} s");
                    if (!fixedFilter)
                    {
                        oldFilterWasWrong |= Math.Abs(v - PlanSec) > 0.3;
                        continue;
                    }

                    Math.Abs(v - PlanSec).Should().BeLessThan(0.15, string.Join("\n", report));
                    Math.Abs(a - PlanSec).Should().BeLessThan(0.15, string.Join("\n", report));
                    double[] flashes = FlashOnsets(output);
                    double[] beeps = BeepOnsets(output);
                    double[] expected = [0.5, 2.5, 5.5, 7.0];
                    for (int i = 0; i < expected.Length; i++)
                    {
                        if (gap && i == 2)
                            continue; // the span with no frame at all: the picture holds the frame before it, no flash to find
                        beeps.Should().Contain(b => Math.Abs(b - expected[i]) < 0.12, $"{name}: sound marker {i} at {expected[i]}; found {string.Join(",", beeps)}");
                        flashes.Should().Contain(f => Math.Abs(f - expected[i]) < 0.12, $"{name}: picture marker {i} at {expected[i]}; found {string.Join(",", flashes)}");
                    }
                }
            }

            output.WriteLine(string.Join("\n", report));
            oldFilterWasWrong.Should().BeTrue("the old filter must fail this check on VFR input:\n" + string.Join("\n", report));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static double StreamSec(string file, string stream)
    {
        string o = Run($"-i \"{file}\" -map 0:{stream} -c copy -f null -", exe: "ffmpeg", capture: true);
        Match t = Regex.Matches(o, @"time=(\d+):(\d+):([\d.]+)").Last();
        return int.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 + int.Parse(t.Groups[2].Value, CultureInfo.InvariantCulture) * 60
               + double.Parse(t.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    private static double[] FlashOnsets(string file)
    {
        string o = Run($"-i \"{file}\" -vf signalstats,metadata=print:key=lavfi.signalstats.YAVG -an -f null -", capture: true);
        var result = new List<double>();
        double? pts = null;
        bool prevBright = false;
        foreach (string line in o.Split('\n'))
        {
            Match pt = Regex.Match(line, @"pts_time:([\d.]+)");
            if (pt.Success) pts = double.Parse(pt.Groups[1].Value, CultureInfo.InvariantCulture);
            Match y = Regex.Match(line, @"YAVG=([\d.]+)");
            if (!y.Success || pts is null) continue;
            bool bright = double.Parse(y.Groups[1].Value, CultureInfo.InvariantCulture) > 128;
            if (bright && !prevBright) result.Add(pts.Value);
            prevBright = bright;
        }

        return [.. result];
    }

    private static double[] BeepOnsets(string file)
    {
        string o = Run($"-i \"{file}\" -af silencedetect=n=-30dB:d=0.15 -vn -f null -", capture: true);
        return Regex.Matches(o, @"silence_end: ([\d.]+)").Select(x => double.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
    }

    /// <summary>Runs ffmpeg and returns STDOUT (the <c>-encoders</c> listing goes there, not stderr).</summary>
    private static string RunStdout(string args)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-hide_banner");
        foreach (string a in SplitArgs(args)) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return stdout;
    }

    internal static string Run(string args, bool allowFailure = false, bool capture = false, string exe = "ffmpeg")
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-y");
        foreach (string a in SplitArgs(args)) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && !allowFailure)
            throw new InvalidOperationException($"{exe} {args} failed ({p.ExitCode}): {err}");
        return err;
    }

    private static IEnumerable<string> SplitArgs(string s) =>
        Regex.Matches(s, "\"([^\"]*)\"|(\\S+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
}

/// <summary>
/// A fact that needs a real ffmpeg whose <c>aselect</c> actually drops audio samples. Some builds (the
/// host's 8.0.1) pass the whole sound through, which makes any audio-length assertion meaningless, so
/// the test is SKIPPED with that reason there — the engine image (8.1.3) runs it.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class FfmpegCutsAudioWithAselectFactAttribute : FactAttribute
{
    public FfmpegCutsAudioWithAselectFactAttribute()
    {
        string? missing = FfmpegToolsOnPath.MissingBinary();
        if (missing is not null)
        {
            Skip = $"'{missing}' is not on PATH; this test runs the real binary.";
            return;
        }

        try
        {
            string err = VfrSyncTests.Run("-f lavfi -i sine=d=10 -af aselect='lt(t,3)' -f null -", capture: true);
            Match t = Regex.Matches(err, @"time=(\d+):(\d+):([\d.]+)").Last();
            double sec = int.Parse(t.Groups[2].Value, CultureInfo.InvariantCulture) * 60 + double.Parse(t.Groups[3].Value, CultureInfo.InvariantCulture);
            if (sec > 3.5)
                Skip = $"this ffmpeg does not cut audio with aselect (10 s source, aselect lt(t,3) gave {sec:0.0} s); audio length cannot be asserted here.";
        }
        catch (Exception ex)
        {
            Skip = "could not probe ffmpeg aselect: " + ex.Message;
        }
    }
}
