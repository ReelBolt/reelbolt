using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Segmentation;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Tracking;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A Display Matrix 90/270 source is transcoded upright before the vendor upload (Owner decision, #772),
/// so segmentation masks are in display space, the space every engine decode uses.
/// </summary>
public sealed class SegmentationUprightTests
{
    private const string UprightProbe =
        """{"streams":[{"codec_type":"video","codec_name":"h264","width":180,"height":320,"r_frame_rate":"24/1"}],"format":{"duration":"0.25","size":"4096"}}""";

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "ffprobe-rotation", name + ".json"));

    [Fact]
    public async Task A_rotated_source_presigns_the_upright_vendor_copy_and_keys_masks_on_it()
    {
        var provider = new SegmentationTrackingTests.CountingClient { Width = 180, Height = 320 };
        var harness = new SegmentationTrackingTests.Harness(provider);
        harness.Workspace.ProbeOutputs.Enqueue(Fixture("dm-rot-90"));
        harness.Workspace.ProbeOutputs.Enqueue(UprightProbe);

        await harness.RunAsync(SegmentationTrackingTests.SegmentationConfig(), width: 180, height: 320);

        // QA (PR #930): a SMALL rotated clip is size-bounded too; the old CRF-12 mezzanine measured 4.5x its source.
        harness.Workspace.FfmpegCalls.Should().ContainSingle(c => c.Contains("-map_metadata"))
            .Which.Should().Contain("-maxrate");
        harness.Store.VendorCopies.Should().HaveCount(1);
        harness.Presign.Keys.Should().ContainSingle().Which.Should()
            .Be(SegmentationMaskStore.VendorCopyStorageKeyFor(SegmentationTrackingTests.Project, harness.Store.VendorCopies.Single()));
        harness.Store.KeysSeen.Should().NotBeEmpty().And.OnlyContain(k => k.SourceContentHash.StartsWith(SegmentationTrackingService.UprightHashPrefix));
    }

    [Fact]
    public async Task A_failed_upright_transcode_tells_the_customer_nothing_internal()
    {
        var harness = new SegmentationTrackingTests.Harness(new SegmentationTrackingTests.CountingClient());
        harness.Workspace.ProbeOutputs.Enqueue(Fixture("dm-rot-90"));
        harness.Workspace.FfmpegResult = new VideoToolResult(1, string.Empty, "STDERR_MARKER /srv/scratch/secret/in.mp4", false);

        SegmentationSourceOutcome outcome = await harness.RunAsync(SegmentationTrackingTests.SegmentationConfig(), width: 180, height: 320);

        outcome.FailureCode.Should().Be(SegmentationFailureCodes.EgressUnavailable);
        outcome.FailureMessage.Should().NotContain("STDERR_MARKER").And.NotContain("/srv/scratch")
            .And.NotContain("clip.mp4").And.NotContain("exited").And.Contain("orientation could not be read").And.NotContain("rotated");
        outcome.FailureLogMessage.Should().Contain("STDERR_MARKER").And.Contain("clip.mp4");
        harness.Presign.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("dm-rot-180")]
    [InlineData("dm-rot-hflip")]
    [InlineData("dm-rot-vflip")]
    [InlineData("dm-rot-90.7")] // ffmpeg rotates by 91 degrees (rotate filter, size kept)
    public async Task A_non_transposing_flip_or_half_turn_is_still_uprighted(string fixture)
    {
        var harness = new SegmentationTrackingTests.Harness(new SegmentationTrackingTests.CountingClient());
        harness.Workspace.ProbeOutputs.Enqueue(Fixture(fixture));
        harness.Workspace.ProbeOutputs.Enqueue(
            """{"streams":[{"codec_type":"video","width":12,"height":6}],"format":{"duration":"0.25","size":"4096"}}""");

        await harness.RunAsync(SegmentationTrackingTests.SegmentationConfig());

        harness.Workspace.FfmpegCalls.Should().ContainSingle(c => c.Contains("-map_metadata"));
        harness.Presign.Keys.Should().ContainSingle().Which.Should().Contain("/vendor1/");
    }

    [Fact]
    public async Task A_failed_reprobe_refuses_and_uploads_nothing()
    {
        var harness = new SegmentationTrackingTests.Harness(new SegmentationTrackingTests.CountingClient());
        harness.Workspace.ProbeOutputs.Enqueue("");

        SegmentationSourceOutcome outcome = await harness.RunAsync(SegmentationTrackingTests.SegmentationConfig());

        outcome.FailureCode.Should().Be(SegmentationFailureCodes.EgressUnavailable);
        outcome.FailureMessage.Should().NotContain("clip.mp4");
        harness.Presign.Calls.Should().Be(0);
        harness.Workspace.FfmpegCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_cached_mezzanine_is_not_transcoded_again()
    {
        var harness = new SegmentationTrackingTests.Harness(new SegmentationTrackingTests.CountingClient());
        harness.Workspace.ProbeOutputs.Enqueue(Fixture("dm-rot-90"));
        harness.Workspace.ProbeOutputs.Enqueue(UprightProbe);
        await harness.RunAsync(SegmentationTrackingTests.SegmentationConfig(), width: 180, height: 320);
        int firstRunCalls = harness.Workspace.FfmpegCalls.Count(c => c.Contains("-map_metadata"));

        harness.Workspace.ProbeOutputs.Enqueue(Fixture("dm-rot-90"));
        await harness.RunAsync(SegmentationTrackingTests.SegmentationConfig(), width: 180, height: 320);

        firstRunCalls.Should().Be(1);
        harness.Workspace.FfmpegCalls.Count(c => c.Contains("-map_metadata")).Should().Be(1);
    }

    [Theory]
    [InlineData("dm-rot-0")]
    [InlineData("tag-mkv")]
    public async Task An_unrotated_source_takes_the_unchanged_path(string fixture)
    {
        var harness = new SegmentationTrackingTests.Harness(new SegmentationTrackingTests.CountingClient());
        harness.Workspace.ProbeOutputs.Enqueue(Fixture(fixture));

        await harness.RunAsync(SegmentationTrackingTests.SegmentationConfig());

        harness.Workspace.FfmpegCalls.Should().NotContain(c => c.Contains("-map_metadata"));
        harness.Store.VendorCopies.Should().BeEmpty();
        harness.Presign.Keys.Should().ContainSingle().Which.Should().Be("clip.mp4");
        harness.Store.KeysSeen.Should().NotBeEmpty().And.OnlyContain(k => !k.SourceContentHash.StartsWith(SegmentationTrackingService.UprightHashPrefix));
    }

    [Fact]
    public async Task Real_ffmpeg_bakes_a_display_matrix_90_clip_upright_with_no_matrix()
    {
        if (!HasTool("ffmpeg") || !HasTool("ffprobe")) return;
        string dir = Path.Combine(Path.GetTempPath(), "upright-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string plain = Path.Combine(dir, "plain.mp4"), rotated = Path.Combine(dir, "rot.mp4"), upright = Path.Combine(dir, "up.mp4");
            Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=s=320x180:d=2:r=10", "-c:v", "libx264", "-pix_fmt", "yuv420p", plain);
            Run("ffmpeg", "-v", "error", "-y", "-display_rotation", "90", "-i", plain, "-c", "copy", rotated);
            Run("ffmpeg", [.. SegmentationMaskArgvBuilder.BuildVendorCopyArgs(rotated, upright, 2000)]);

            string srcJson = Run("ffprobe", [.. FfmpegArgvBuilder.BuildProbeArgs(rotated)]);
            string outJson = Run("ffprobe", [.. FfmpegArgvBuilder.BuildProbeArgs(upright)]);
            FfprobeOutputParser.TransposesOnDecode(srcJson).Should().BeTrue();
            FfprobeOutputParser.TransposesOnDecode(outJson).Should().BeFalse();
            outJson.Should().NotContain("Display Matrix");
            MediaProbeResult src = FfprobeOutputParser.Parse(srcJson), up = FfprobeOutputParser.Parse(outJson);
            (up.Width, up.Height).Should().Be((src.Width, src.Height), "the mezzanine's coded size is the source's display size");
            (up.Width, up.Height).Should().Be((180, 320));
            Frames(upright).Should().Be(Frames(rotated));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static int Frames(string path) =>
        int.Parse(Run("ffprobe", "-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", path).Trim().TrimEnd(','));

    private static bool HasTool(string name)
    {
        try { Run(name, "-version"); return true; } catch { return false; }
    }

    private static string Run(string tool, params string[] args)
    {
        ProcessStartInfo psi = new(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{tool} exited {p.ExitCode}");
        return stdout;
    }
}
