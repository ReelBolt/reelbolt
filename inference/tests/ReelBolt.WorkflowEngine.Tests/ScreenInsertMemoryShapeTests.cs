using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Regression tests for the out-of-memory compile (docs/video-editing.md "Memory: every branch
/// starts at zero"). A matted insert's alpha is keyed off the base picture, so an insert branch
/// that only begins at the insert's start time made every framesync filter wait for a frame the
/// base had to be decoded up to that time to produce — and the base's <c>split</c> queued the
/// whole programme before it, full resolution, once per branch. A four-clip composite passed
/// 20 GB and was SIGKILLed; a two-clip one peaked at 14.7 GB. Every branch of a matted insert is
/// now padded from t=0, and these tests pin that shape.
/// </summary>
public class ScreenInsertMemoryShapeTests
{
    private static readonly VideoInsertChromaKey Key = new(0, 232, 0, 0.30, 0.10, 0.9);

    private static ResolvedScreenInsert Insert(
        string id, double start, double end, VideoInsertChromaKey? matte, bool reflect = false, InsertMeshPlan? mesh = null) =>
        new(
            id, "/scratch/insert.mp4", start, end,
            [
                new InsertQuadFrame(0, 576, 216, 1152, 238, 595, 756, 1171, 778),
                new InsertQuadFrame(60, 672, 216, 1248, 238, 691, 756, 1267, 778)
            ],
            24, 1, Mesh: mesh, Matte: matte, TransferReflections: reflect);

    private static InsertMeshPlan Mesh() => new(1, 2,
    [
        new InsertMeshFrame(0,
        [
            [(500.0, 200.0), (800.0, 210.0), (1100.0, 200.0)],
            [(500.0, 700.0), (800.0, 690.0), (1100.0, 700.0)]
        ])
    ]);

    [Fact]
    public void No_branch_of_a_matted_insert_is_time_shifted_every_one_is_padded_from_zero()
    {
        // Late starts, several inserts, both renderers, with and without reflections: the shapes
        // the four-clip compile had.
        List<ResolvedScreenInsert> inserts =
        [
            Insert("r0", 6.625, 13.2, Key, reflect: true),
            Insert("r1", 1.083333, 6.583333, Key),
            Insert("r2", 19.75, 26.3, Key, reflect: true, mesh: Mesh()),
            Insert("r3", 13.25, 19.75, Key)
        ];

        string chain = ScreenInsertFilterBuilder.BuildFilterChain("[vcat]", inserts, 1920, 1080, i => 4 + i);

        chain.Should().NotContain("PTS-STARTPTS+",
            "a branch whose first frame is at the insert's start makes framesync hold the base back to that point");

        // Every content/mask branch (an input or a synthesized plate) is padded by the insert's
        // own start in whole frames, AFTER its warp so perspective's `in` still counts from the
        // insert's first frame.
        foreach ((ResolvedScreenInsert insert, int i) in inserts.Select((x, i) => (x, i)))
        {
            long frames = ScreenInsertFilterBuilder.PadFrames(insert);
            string pad = $"tpad=start={frames}:start_mode=add:color=black";
            List<string> branches = chain.Split(';')
                .Where(s => s.StartsWith($"[{4 + i}:v]", StringComparison.Ordinal)
                            || (s.StartsWith("color=c=white", StringComparison.Ordinal) && s.Contains($"[sim{i}]"))
                            || Regex.IsMatch(s, $@"\[(mc|mm){i}c\d+\]$"))
                .ToList();
            branches.Should().NotBeEmpty();
            foreach (string branch in branches.Where(b => b.Contains("perspective=", StringComparison.Ordinal)))
            {
                branch.Should().Contain(pad);
                branch.LastIndexOf("perspective=", StringComparison.Ordinal)
                    .Should().BeLessThan(branch.IndexOf("tpad=", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void The_pad_is_the_old_shift_in_whole_frames()
    {
        ScreenInsertFilterBuilder.PadFrames(Insert("r", 6.625, 9, Key)).Should().Be(159);
        ScreenInsertFilterBuilder.PadFrames(Insert("r", 1.0833333333333333, 9, Key)).Should().Be(26);
        ScreenInsertFilterBuilder.PadFrames(Insert("r", 0, 9, Key)).Should().Be(0);

        // Starting at zero needs no pad at all.
        ScreenInsertFilterBuilder.DelayToOutputStart(Insert("r", 0, 9, Key)).Should().Be("setpts=PTS-STARTPTS");
    }

    [Fact]
    public void An_unmatted_insert_keeps_its_timestamp_shift_nothing_it_waits_on_comes_from_the_base()
    {
        string chain = ScreenInsertFilterBuilder.BuildFilterChain(
            "[vcat]", [Insert("r0", 6.625, 13.2, matte: null)], 1920, 1080, _ => 4);

        chain.Should().Contain("setpts=PTS-STARTPTS+6.625/TB[sic0]").And.NotContain("tpad=start=");
    }

    /// <summary>
    /// The measurement itself, on a small synthetic programme: a matted insert starting 20 s in.
    /// The old shape queued ~480 frames on each of the base's split branches; the padded shape
    /// holds a handful. Real ffmpeg; skipped where it is not installed.
    /// </summary>
    [RealFfmpegFact]
    public void A_late_matted_insert_composites_in_bounded_memory()
    {
        string work = Path.Combine(Path.GetTempPath(), "insert-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            const int w = 640, h = 360;
            var insert = new ResolvedScreenInsert(
                "r0", Path.Combine(work, "content.mp4"), 20.0, 22.0,
                [new InsertQuadFrame(0, 200, 100, 440, 100, 200, 260, 440, 260)],
                24, 1, Matte: Key, TransferReflections: true);
            Run($"-v error -f lavfi -i testsrc2=s=320x568:r=24:d=3 -c:v libx264 -crf 30 -y \"{insert.LocalAssetPath}\"");

            // A grey base with a green plate in the middle, 22 s long.
            string graph =
                $"color=c=gray:s={w}x{h}:r=24:d=22[bg];color=c=0x00E800:s=240x160:r=24:d=22[pl];" +
                "[bg][pl]overlay=200:100[vcut];" +
                ScreenInsertFilterBuilder.BuildFilterChain("[vcut]", [insert], w, h, _ => 0);
            string script = Path.Combine(work, "graph.txt");
            File.WriteAllText(script, graph);

            long peakKb = RunMeasured(
                $"-v error -i \"{insert.LocalAssetPath}\" -filter_complex_script \"{script}\" -map [vout] -c:v libx264 -preset ultrafast -f null -");

            // Measured: ~190 MB padded from zero, ~900 MB with the old time-shifted branches (480
            // frames of a 640x360 base queued on every split branch until the insert's start).
            peakKb.Should().BeLessThan(450 * 1024, "the base must not be queued up to the insert's start");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static void Run(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("ffmpeg", args) { RedirectStandardError = true })!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, err);
    }

    /// <summary>Runs ffmpeg and returns its peak resident set (VmHWM), in KiB, read just before it exits.</summary>
    private static long RunMeasured(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("ffmpeg", args) { RedirectStandardError = true })!;
        long peak = 0;
        var stderr = p.StandardError.ReadToEndAsync();
        while (!p.HasExited)
        {
            peak = Math.Max(peak, ReadHighWaterMark(p.Id));
            Thread.Sleep(50);
        }

        p.WaitForExit();
        p.ExitCode.Should().Be(0, stderr.Result);
        return peak;
    }

    private static long ReadHighWaterMark(int pid)
    {
        try
        {
            foreach (string line in File.ReadLines($"/proc/{pid}/status"))
            {
                if (line.StartsWith("VmHWM:", StringComparison.Ordinal))
                    return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return 0;
    }
}
