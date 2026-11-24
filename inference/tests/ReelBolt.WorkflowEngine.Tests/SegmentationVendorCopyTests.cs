using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Segmentation;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Tracking;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// magic-blur-fetch-fix: the hosted segmentation model downloads the source by URL and refused a 563 MB
/// original (fal 422 <c>file_download_error</c>, prod 2026-11-02). A source over
/// <see cref="SegmentationLimits.VendorSourceMaxBytes"/> now goes as a size-bounded copy of the same frames
/// at the same size; a source at or under it keeps the unchanged path (original key, no transcode).
/// </summary>
public sealed class SegmentationVendorCopyTests
{
    private const string NotRotatedProbe = """{"streams":[{"codec_type":"video","width":12,"height":6}]}""";

    private static string CopyProbe(long bytes, int width = 12, int height = 6) =>
        "{\"streams\":[{\"codec_type\":\"video\",\"width\":" + width + ",\"height\":" + height +
        "}],\"format\":{\"size\":\"" + bytes + "\"}}";

    private const long OverBudget = SegmentationLimits.VendorSourceMaxBytes + 1;

    [Fact]
    public void The_vendor_copy_argv_keeps_every_frame_and_size_and_caps_the_bitrate()
    {
        SegmentationMaskArgvBuilder.BuildVendorCopyArgs("/in/a.mp4", "/out/v.mp4", 11333).Should().Equal(
            "-hide_banner", "-nostdin", "-y",
            "-protocol_whitelist", "file", "-format_whitelist", UserMediaInputFormats.Whitelist, "-i", "/in/a.mp4",
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-map_metadata", "-1", "-map_chapters", "-1",
            "-fps_mode", "passthrough",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
            "-maxrate", "11333k", "-bufsize", "11333k",
            "-pix_fmt", "yuv420p", "-movflags", "+faststart", "/out/v.mp4");
        string[] argv = [.. SegmentationMaskArgvBuilder.BuildVendorCopyArgs("/in/a.mp4", "/out/v.mp4", 500)];
        argv.Should().NotContain("-vf").And.NotContain("-s").And.NotContain("-r").And.NotContain("-noautorotate")
            .And.NotContain("-t").And.NotContain("-ss");
    }

    [Theory]
    [InlineData(60, 11333)]
    [InlineData(600, 1133)]
    [InlineData(0.25, 50000)]
    [InlineData(double.NaN, 50000)]
    [InlineData(100000, 300)]
    public void The_bitrate_ceiling_fits_the_budget_and_is_clamped(double seconds, int expectedKbps)
    {
        SegmentationTrackingService.VendorCopyMaxrateKbps(seconds).Should().Be(expectedKbps);
    }

    [Fact]
    public void Every_clip_the_cap_allows_fits_the_budget_at_its_ceiling()
    {
        for (int seconds = SegmentationLimits.MinSeconds; seconds <= SegmentationLimits.MaxSeconds; seconds++)
        {
            int kbps = SegmentationTrackingService.VendorCopyMaxrateKbps(seconds);
            // maxrate x duration plus one second of VBV buffer stays under the vendor budget.
            double bytes = kbps * 1000.0 / 8 * (seconds + 1);
            bytes.Should().BeLessThan(SegmentationLimits.VendorSourceMaxBytes, $"a {seconds}s clip at {kbps} kbit/s");
        }
    }

    [Theory]
    [InlineData(0.25, 900)]
    [InlineData(60, 900)]
    [InlineData(300, 1800)]
    [InlineData(600, 3600)]
    [InlineData(100000, 3600)]
    [InlineData(double.NaN, 900)]
    public void The_encode_deadline_scales_with_the_clip_inside_900_to_3600_s(double seconds, int expected)
    {
        SegmentationTrackingService.VendorCopyTimeout(seconds).Should().Be(TimeSpan.FromSeconds(expected));
    }

    [Fact]
    public async Task A_failed_cache_lookup_on_an_unrotated_clip_does_not_blame_its_orientation()
    {
        var harness = new SegmentationTrackingTests.Harness(new SegmentationTrackingTests.CountingClient());
        harness.Store.ThrowOnVendorCopyLookup = true;

        SegmentationSourceOutcome outcome = await harness.RunAsync(
            SegmentationTrackingTests.SegmentationConfig(), durationSec: 60, sourceBytes: OverBudget);

        outcome.FailureCode.Should().Be(SegmentationFailureCodes.EgressUnavailable);
        outcome.FailureMessage.Should().Contain("could not be prepared").And.NotContain("orientation");
        harness.Presign.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("""{"format":{"size":"98765"}}""", 98765L)]
    [InlineData("""{"format":{"size":98765}}""", 98765L)]
    [InlineData("""{"format":{}}""", null)]
    [InlineData("""{"format":{"size":"-1"}}""", null)]
    [InlineData("""{"format":{"size":"12abc"}}""", null)]
    [InlineData("not json", null)]
    [InlineData("", null)]
    public void The_copy_size_is_read_from_the_probe_or_unknown(string json, long? expected)
    {
        SegmentationTrackingService.ProbedFormatSizeBytes(json).Should().Be(expected);
    }

    [Fact]
    public async Task An_over_size_source_presigns_the_bounded_copy_not_the_original()
    {
        var harness = new SegmentationTrackingTests.Harness(new SegmentationTrackingTests.CountingClient());
        harness.Workspace.ProbeOutputs.Enqueue(NotRotatedProbe);
        harness.Workspace.ProbeOutputs.Enqueue(CopyProbe(40_000_000));

        SegmentationSourceOutcome outcome = await harness.RunAsync(
            SegmentationTrackingTests.SegmentationConfig(), durationSec: 60, sourceBytes: OverBudget);

        outcome.FailureCode.Should().BeNull();
        harness.Workspace.FfmpegCalls.Should().ContainSingle(c => c.Contains("-maxrate"))
            .Which.Should().ContainInOrder("-maxrate", "11333k");
        harness.Store.VendorCopies.Should().ContainSingle();
        // Same frames, same size, not rotated: masks stay keyed on the ORIGINAL's hash (cache still valid).
        harness.Store.KeysSeen.Should().NotBeEmpty().And.OnlyContain(
            k => k.SourceContentHash == harness.Store.VendorCopies.Single());
        harness.Presign.Keys.Should().ContainSingle().Which.Should()
            .Be(SegmentationMaskStore.VendorCopyStorageKeyFor(SegmentationTrackingTests.Project, harness.Store.VendorCopies.Single()))
            .And.Contain("/agentFiles/segmentation/vendor1/");
        harness.Client.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_source_at_the_budget_takes_the_unchanged_path()
    {
        var harness = new SegmentationTrackingTests.Harness(new SegmentationTrackingTests.CountingClient());

        await harness.RunAsync(
            SegmentationTrackingTests.SegmentationConfig(), sourceBytes: SegmentationLimits.VendorSourceMaxBytes);

        harness.Workspace.FfmpegCalls.Should().NotContain(c => c.Contains("-maxrate") || c.Contains("-map_metadata"));
        harness.Store.VendorCopies.Should().BeEmpty();
        harness.Presign.Keys.Should().ContainSingle().Which.Should().Be("clip.mp4");
    }

    [Fact]
    public async Task A_cached_vendor_copy_is_not_encoded_again()
    {
        var harness = new SegmentationTrackingTests.Harness(new SegmentationTrackingTests.CountingClient());
        harness.Workspace.ProbeOutputs.Enqueue(NotRotatedProbe);
        harness.Workspace.ProbeOutputs.Enqueue(CopyProbe(40_000_000));
        await harness.RunAsync(SegmentationTrackingTests.SegmentationConfig(), durationSec: 60, sourceBytes: OverBudget);

        harness.Workspace.ProbeOutputs.Enqueue(NotRotatedProbe);
        await harness.RunAsync(SegmentationTrackingTests.SegmentationConfig(), durationSec: 60, sourceBytes: OverBudget);

        harness.Workspace.FfmpegCalls.Count(c => c.Contains("-maxrate")).Should().Be(1);
        harness.Presign.Keys.Should().HaveCount(2).And.OnlyContain(k => k.Contains("/vendor1/"));
    }

    [Fact]
    public async Task A_copy_still_over_the_budget_is_never_stored_or_sent()
    {
        var harness = new SegmentationTrackingTests.Harness(new SegmentationTrackingTests.CountingClient());
        harness.Workspace.ProbeOutputs.Enqueue(NotRotatedProbe);
        harness.Workspace.ProbeOutputs.Enqueue(CopyProbe(OverBudget));

        SegmentationSourceOutcome outcome = await harness.RunAsync(
            SegmentationTrackingTests.SegmentationConfig(), durationSec: 60, sourceBytes: OverBudget * 3);

        outcome.FailureCode.Should().Be(SegmentationFailureCodes.EgressUnavailable);
        outcome.FailureClass.Should().Be(ReelBolt.Shared.Data.Models.FailureClass.PlatformInternal, "a refused egress is refunded");
        outcome.FailureMessage.Should().Contain("too large").And.Contain("not been charged").And.NotContain("clip.mp4");
        outcome.FailureLogMessage.Should().Contain("clip.mp4");
        harness.Store.VendorCopies.Should().BeEmpty();
        harness.Presign.Calls.Should().Be(0);
        harness.Client.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("""{"streams":[{"codec_type":"video","width":6,"height":3}],"format":{"size":"1000"}}""")]
    [InlineData("""{"streams":[{"codec_type":"video","width":12,"height":6}],"format":{}}""")]
    public async Task A_copy_with_other_geometry_or_no_size_fails_closed(string copyProbe)
    {
        var harness = new SegmentationTrackingTests.Harness(new SegmentationTrackingTests.CountingClient());
        harness.Workspace.ProbeOutputs.Enqueue(NotRotatedProbe);
        harness.Workspace.ProbeOutputs.Enqueue(copyProbe);

        SegmentationSourceOutcome outcome = await harness.RunAsync(
            SegmentationTrackingTests.SegmentationConfig(), durationSec: 60, sourceBytes: OverBudget);

        outcome.FailureCode.Should().Be(SegmentationFailureCodes.EgressUnavailable);
        outcome.FailureMessage.Should().Contain("could not be prepared").And.NotContain("orientation");
        harness.Store.VendorCopies.Should().BeEmpty();
        harness.Presign.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_failed_encode_tells_the_customer_nothing_internal()
    {
        var harness = new SegmentationTrackingTests.Harness(new SegmentationTrackingTests.CountingClient());
        harness.Workspace.ProbeOutputs.Enqueue(NotRotatedProbe);
        harness.Workspace.FfmpegResult = new VideoToolResult(1, string.Empty, "STDERR_MARKER /srv/scratch/in.mp4", false);

        SegmentationSourceOutcome outcome = await harness.RunAsync(
            SegmentationTrackingTests.SegmentationConfig(), durationSec: 60, sourceBytes: OverBudget);

        outcome.FailureCode.Should().Be(SegmentationFailureCodes.EgressUnavailable);
        outcome.FailureMessage.Should().NotContain("STDERR_MARKER").And.NotContain("/srv/scratch").And.NotContain("clip.mp4");
        outcome.FailureLogMessage.Should().Contain("STDERR_MARKER").And.Contain("bounded vendor");
        harness.Presign.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_rotated_over_size_source_sends_one_upright_bounded_copy()
    {
        var harness = new SegmentationTrackingTests.Harness(new SegmentationTrackingTests.CountingClient { Width = 180, Height = 320 });
        harness.Workspace.ProbeOutputs.Enqueue(System.IO.File.ReadAllText(System.IO.Path.Combine(
            AppContext.BaseDirectory, "TestData", "ffprobe-rotation", "dm-rot-90.json")));
        harness.Workspace.ProbeOutputs.Enqueue(CopyProbe(40_000_000, width: 180, height: 320));

        SegmentationSourceOutcome outcome = await harness.RunAsync(
            SegmentationTrackingTests.SegmentationConfig(), durationSec: 60, width: 180, height: 320, sourceBytes: OverBudget);

        outcome.FailureCode.Should().BeNull();
        // One encode: the bounded copy is upright too (autorotate on), so no separate mezzanine.
        harness.Workspace.FfmpegCalls.Should().ContainSingle(c => c.Contains("-map_metadata"))
            .Which.Should().Contain("-maxrate");
        harness.Presign.Keys.Should().ContainSingle().Which.Should().Contain("/vendor1/");
        harness.Store.KeysSeen.Should().NotBeEmpty().And.OnlyContain(
            k => k.SourceContentHash == SegmentationTrackingService.UprightHashPrefix + harness.Store.VendorCopies.Single());
    }

    [Fact]
    public void Real_ffmpeg_shrinks_a_heavy_clip_keeping_every_frame_and_its_size()
    {
        if (!HasTool("ffmpeg") || !HasTool("ffprobe")) return;
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vendorcopy-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string heavy = System.IO.Path.Combine(dir, "heavy.mp4"), copy = System.IO.Path.Combine(dir, "copy.mp4");
            // Lossless noise: the worst case for a bitrate ceiling, and a source far bigger than its copy.
            Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:d=3:r=30",
                "-vf", "noise=alls=60:allf=t", "-c:v", "libx264", "-qp", "0", "-pix_fmt", "yuv420p", heavy);
            Run("ffmpeg", [.. SegmentationMaskArgvBuilder.BuildVendorCopyArgs(heavy, copy, 400)]);

            string srcJson = Run("ffprobe", [.. FfmpegArgvBuilder.BuildProbeArgs(heavy)]);
            string outJson = Run("ffprobe", [.. FfmpegArgvBuilder.BuildProbeArgs(copy)]);
            MediaProbeResult src = FfprobeOutputParser.Parse(srcJson), out_ = FfprobeOutputParser.Parse(outJson);
            (out_.Width, out_.Height).Should().Be((src.Width, src.Height));
            Frames(copy).Should().Be(Frames(heavy));
            long copyBytes = new System.IO.FileInfo(copy).Length;
            SegmentationTrackingService.ProbedFormatSizeBytes(outJson).Should().Be(copyBytes);
            copyBytes.Should().BeLessThan(new System.IO.FileInfo(heavy).Length / 4);
            // 400 kbit/s for 3 s, plus one second of buffer and the container: well under 400 KB.
            copyBytes.Should().BeLessThan(400_000);
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    private static int Frames(string path) =>
        int.Parse(Run("ffprobe", "-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", path).Trim().TrimEnd(','));

    private static bool HasTool(string name)
    {
        try { Run(name, "-version"); return true; } catch { return false; }
    }

    private static string Run(string tool, params string[] args)
    {
        System.Diagnostics.ProcessStartInfo psi = new(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{tool} exited {p.ExitCode}");
        return stdout;
    }
}
