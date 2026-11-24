#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using AwesomeAssertions;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The fixture and instrument checks for prompted region effects (T-002): the synthetic dashboard's
/// mask really is its own ground truth and generates deterministically, the bar harness can both pass
/// a correct effect and fail a mask-ignorant one, and the real <c>fal-ai/sam-3-1/video-rle</c> replay
/// decodes to pinned digests so a later decoder change cannot drift silently.
///
/// <para>
/// Everything here is offline: the synthetic family is painted in-process and encoded by the local
/// ffmpeg, and the real family is the committed response JSON. Nothing calls a vendor.
/// </para>
/// </summary>
public class PromptedRegionFixturesTests
{
    private readonly ITestOutputHelper _output;

    public PromptedRegionFixturesTests(ITestOutputHelper output) => _output = output;

    [FfmpegFact]
    public void The_dashboard_mask_reproduces_its_own_sidecar_frame_by_frame()
    {
        PromptedRegionFixture fixture = PromptedRegionTestFixtures.Dashboard;
        PromptedRegionSidecar sidecar = fixture.Sidecar;

        (sidecar.Width, sidecar.Height).Should().Be((960, 540));
        (sidecar.FrameCount, sidecar.Fps).Should().Be((144, 24));
        sidecar.DurationSec.Should().BeApproximately(6.0, 1e-9);
        sidecar.BoxConvention.Should().Contain("corner-anchored", "the convention is stated, never inferred by a reader");

        // The occlusion case the brief asks for: the reading hand hides the gauge completely for
        // exactly half a second, and nowhere else.
        sidecar.OccludedFrames.Should().Equal(Enumerable.Range(96, 12));
        sidecar.Frames.Where(f => f.AreaPx == 0).Select(f => f.Index).Should().Equal(Enumerable.Range(96, 12));

        // ...and the scene really moves, scales and rotates: a fixture whose object never changes is
        // no test of anything.
        List<PromptedRegionFrameTruth> visible = sidecar.Frames.Where(f => f.AreaPx > 0).ToList();
        visible.Select(f => Math.Round(f.CentroidX)).Distinct().Count().Should().BeGreaterThan(60);
        visible.Select(f => Math.Round(f.CentroidY)).Distinct().Count().Should().BeGreaterThan(10);
        (visible.Min(f => f.AreaPx) / (double)visible.Max(f => f.AreaPx)).Should().BeLessThan(0.75,
            "the gauge scales over the clip");
        visible.Max(f => f.CentroidX).Should().BeGreaterThan(visible.Min(f => f.CentroidX) + 100,
            "the gauge travels across the frame");

        // The mask video is the ground truth, not a second guess: it must agree with the sidecar.
        Action verify = () => PromptedRegionBarHarness.VerifyMaskMatchesSidecar(fixture.MaskPath, sidecar);
        verify.Should().NotThrow();

        File.Exists(fixture.ClipPath).Should().BeTrue();
        var (width, height, fpsNum, fpsDen, duration) = PromptedRegionBarHarness.Probe(fixture.ClipPath);
        (width, height, fpsNum, fpsDen).Should().Be((960, 540, 24, 1));
        duration.Should().BeApproximately(6.0, 0.1);
    }

    [FfmpegFact]
    public void Two_generations_produce_byte_identical_masks_and_identical_decoded_clip_frames()
    {
        PromptedRegionFixture first = PromptedRegionTestFixtures.Dashboard;
        PromptedRegionFixture second = PromptedRegionFixtures.GenerateDashboard(
            PromptedRegionTestFixtures.NewTemporaryDirectory("determinism"));

        // The mask FILE cannot be byte-identical: Matroska writes a random Segment UID per mux, and
        // ffmpeg's bitexact flags do not remove it (measured). The mask FRAMES are lossless FFV1, so
        // identical frames prove the same painted mask — which is the property that matters.
        DecodedVideoHash(second.MaskPath, "gray").Should().Be(DecodedVideoHash(first.MaskPath, "gray"),
            "the mask is painted from the same formula, so two runs must decode to the same frames");

        // The clip's bytes may carry container metadata (dates, muxing tags); its DECODED frames may not.
        DecodedVideoHash(second.ClipPath, "rgb24").Should().Be(DecodedVideoHash(first.ClipPath, "rgb24"),
            "the clip must decode to the same pixels on every run");

        second.Sidecar.ToJson().Should().Be(first.Sidecar.ToJson(), "the sidecar is part of the fixture");
    }

    [FfmpegFact]
    public void The_wide_fixture_is_the_same_scene_at_16_by_9_for_the_reframe_check()
    {
        PromptedRegionFixture wide = PromptedRegionFixtures.GenerateDashboardWide(
            PromptedRegionTestFixtures.NewTemporaryDirectory("wide"));
        PromptedRegionFixture narrow = PromptedRegionTestFixtures.Dashboard;

        (wide.Sidecar.Width, wide.Sidecar.Height).Should().Be((1280, 720));
        wide.Sidecar.FrameCount.Should().Be(narrow.Sidecar.FrameCount);
        PromptedRegionBarHarness.VerifyMaskMatchesSidecar(wide.MaskPath, wide.Sidecar);

        // The scene is defined in normalised coordinates, so at the same frame the object covers the
        // same fraction of the frame and sits in the same place: a reframe test can compare the two.
        foreach (int index in new[] { 0, 40, 72, 120, 143 })
        {
            PromptedRegionFrameTruth a = narrow.Sidecar.Frames[index], b = wide.Sidecar.Frames[index];
            (b.AreaFraction / a.AreaFraction).Should().BeApproximately(1.0, 0.01, $"frame {index}: same scene, same coverage");
            (b.CentroidX / wide.Sidecar.Width).Should().BeApproximately(a.CentroidX / narrow.Sidecar.Width, 0.002,
                $"frame {index}: same normalised position");
            (b.CentroidY / wide.Sidecar.Height).Should().BeApproximately(a.CentroidY / narrow.Sidecar.Height, 0.002,
                $"frame {index}: same normalised position");
        }

        wide.Sidecar.OccludedFrames.Should().Equal(Enumerable.Range(96, 12), "the occlusion is part of the scene");
    }

    [PromptedRegionHarnessFact]
    public void An_effect_applied_through_the_mask_passes_every_bar_criterion()
    {
        (PromptedRegionBarReport report, string directory) = PromptedRegionTestFixtures.MeasureBlurControl(everywhere: false);
        PromptedRegionFixture fixture = PromptedRegionTestFixtures.Dashboard;
        _output.WriteLine(Path.Combine(directory, "report.json"));

        report.Failures.Should().BeEmpty(
            "a blur applied exactly through the ground-truth mask is the control the bar must accept: " +
            report.ToJson());
        report.Pass.Should().BeTrue();
        report.FrameCount.Should().Be(fixture.Sidecar.FrameCount);
        report.Aggregate.OccludedFrames.Should().Be(12);
        report.Aggregate.OccludedChangedPixelsMax.Should().Be(0, "the gauge is hidden there, so nothing may change");
        report.Aggregate.InsideMean.Should().BeGreaterThan(8, "a blur must actually change the object");
        report.Aggregate.MaskedFrames.Should().Be(132);
    }

    [PromptedRegionHarnessFact]
    public void An_effect_applied_everywhere_fails_the_isolation_bar_so_the_instrument_can_fail()
    {
        (PromptedRegionBarReport report, string directory) = PromptedRegionTestFixtures.MeasureBlurControl(everywhere: true);
        _output.WriteLine(Path.Combine(directory, "report.json"));

        report.Pass.Should().BeFalse("an effect that ignores the mask is exactly what the bar exists to reject");
        report.Failures.Should().Contain("isolation.outsideMean");
        report.Failures.Should().Contain("isolation.changedSetIoUP05");
        report.Failures.Should().Contain("isolation.changedSetIoUMin");
        report.Aggregate.OutsideMean.Should().BeGreaterThan(PromptedRegionBar.Thresholds.OutsideMeanMax * 5,
            "a global blur moves pixels far outside the object, not just past the tolerance");
        report.Aggregate.ChangedSetIoUP05.Should().BeLessThan(0.5);
        report.Aggregate.OccludedChangedPixelsMax.Should().BeGreaterThan(0, "a global effect changes the hidden frames too");
    }

    [PromptedRegionHarnessFact]
    public void The_contact_sheet_has_one_measured_row_per_sample_and_four_labelled_tiles()
    {
        (PromptedRegionBarReport report, string directory) = PromptedRegionTestFixtures.MeasureBlurControl(everywhere: false);
        string sheet = Path.Combine(directory, "prompted-region-sheet.png");

        PromptedRegionBarHarness.WriteContactSheet(
            report, report.OnPath, report.OffPath, report.MaskPath, sheet, samples: 3);

        File.Exists(sheet).Should().BeTrue();
        new FileInfo(sheet).Length.Should().BeGreaterThan(10_000, "a labelled sheet of real frames, not a blank image");
        var (width, height, _, _, _) = PromptedRegionBarHarness.Probe(sheet);
        width.Should().Be((4 * report.Width) + 6);
        height.Should().Be((3 * report.Height) + 4);
    }

    [Fact]
    public void The_real_sam_3_1_replay_decodes_to_the_pinned_digests()
    {
        IReadOnlyList<PromptedRegionReplayFixture> fixtures = PromptedRegionReplay.ReadDigests();
        fixtures.Select(f => f.Name).Should().Equal("R1-tabby", "R1-turn-a", "R10-tabby");

        foreach (PromptedRegionReplayFixture fixture in fixtures)
        {
            File.Exists(fixture.ResponsePath).Should().BeTrue($"{fixture.ResponsePath} is part of the replay pack");
            File.Exists(fixture.ClipPath).Should().BeTrue($"{fixture.ClipPath} is the clip the response was made from");
            (fixture.Width, fixture.Height).Should().Be((768, 1344), "the grid is the source frame's own size");

            PromptedRegionMaskDecode decode = PromptedRegionRleReference.Decode(fixture);

            decode.FrameCount.Should().Be(fixture.FrameCount, $"{fixture.Name}: every frame has a mask");
            decode.EmptyFrames.Should().Be(fixture.EmptyFrames, $"{fixture.Name}: the fully hidden frames");
            decode.DecodedMaskSha256.Should().Be(fixture.DecodedMaskSha256,
                $"{fixture.Name}: the decode digest is pinned; a decoder change must not move a pixel silently");

            // A decode regression check, not a model-quality claim: the vendor's own box, read with the
            // centre anchoring PROVENANCE.md measures, bounds its own mask.
            decode.MinInsideBoxFraction.Should().BeGreaterThan(0.98, $"{fixture.Name}: mask pixels sit inside the model's box");
        }
    }

    [Fact]
    public void The_replay_pack_shows_both_a_clean_track_and_a_fully_hidden_stretch()
    {
        Dictionary<string, PromptedRegionMaskDecode> decodes = PromptedRegionReplay.ReadDigests()
            .ToDictionary(f => f.Name, PromptedRegionRleReference.Decode);

        decodes["R1-tabby"].EmptyFrames.Should().Be(0, "the tabby clip tracks the cat through the whole 5 s");
        decodes["R1-turn-a"].EmptyFrames.Should().Be(14, "the turn clip is the occlusion case the brief asks for");
        decodes["R10-tabby"].EmptyFrames.Should().Be(0);
        decodes["R1-tabby"].DecodedMaskSha256.Should().NotBe(decodes["R10-tabby"].DecodedMaskSha256,
            "a text prompt and a point prompt are two runs, not one file twice");
    }

    [Fact]
    public void The_replay_pack_says_what_it_left_out_and_why()
    {
        string pack = PromptedRegionReplay.PackDirectory;
        File.Exists(Path.Combine(pack, "PROVENANCE.md")).Should().BeTrue();
        File.Exists(Path.Combine(pack, "decode-digests.json")).Should().BeTrue();

        string provenance = File.ReadAllText(Path.Combine(pack, "PROVENANCE.md"));
        provenance.Should().Contain("fal-ai/sam-3-1/video-rle").And.Contain("2026-10-18").And.Contain("$0.01 per 16 frames");
        provenance.Should().Contain("Deliberately not copied", "the pack is under T-002's size budget on purpose");
        provenance.Should().Contain("black").And.Contain("turn-b");

        // Only the documented fixtures and their two clips: no stray vendor file may ride along.
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "PROVENANCE.md", "decode-digests.json", "R1-tabby/response.json", "R1-turn-a/response.json",
            "R10-tabby/response.json", "clips/cat-tabby.mp4", "clips/cat-turn-a.mp4",
            // T-020: the answer that settles the renumbering question, replayed by
            // SegmentationTrackingTests. Its provenance file records the request ids and the cost.
            "T020-reacquire/response.json", "T020-reacquire/PROVENANCE.md"
        };
        string[] actual = Directory.GetFiles(pack, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(pack, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        actual.Should().Equal(expected.OrderBy(path => path, StringComparer.Ordinal));
        new DirectoryInfo(pack).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            .Should().BeLessThan(16 * 1024 * 1024, "T-002 caps the replay pack at about 15 MB");
    }

    [Fact]
    public void The_reference_decoder_refuses_a_run_that_cannot_be_a_mask()
    {
        var destination = new byte[4 * 3];

        Action odd = () => PromptedRegionRleReference.DecodeInto("0 1 2", destination, 4, 3);
        odd.Should().Throw<InvalidDataException>().WithMessage("*not 'start length' pairs*");

        Action pastTheEnd = () => PromptedRegionRleReference.DecodeInto("0 10 9 5", destination, 4, 3);
        pastTheEnd.Should().Throw<InvalidDataException>().WithMessage("*leaves the 4x3 frame*");

        Action overlapping = () => PromptedRegionRleReference.DecodeInto("0 4 2 2", destination, 4, 3);
        overlapping.Should().Throw<InvalidDataException>().WithMessage("*overlaps the previous run*");

        Action negative = () => PromptedRegionRleReference.DecodeInto("-1 2", destination, 4, 3);
        negative.Should().Throw<InvalidDataException>();

        Action zeroLength = () => PromptedRegionRleReference.DecodeInto("0 0", destination, 4, 3);
        zeroLength.Should().Throw<InvalidDataException>().WithMessage("*length 0*");

        Action notANumber = () => PromptedRegionRleReference.DecodeInto("0 x", destination, 4, 3);
        notANumber.Should().Throw<InvalidDataException>().WithMessage("*not an integer*");

        // An empty frame is not an error: the vendor returns one for every frame it does not see the object in.
        PromptedRegionRleReference.DecodeInto("", destination, 4, 3);
        destination.Should().OnlyContain(value => value == 0);

        PromptedRegionRleReference.DecodeInto("1 3 7 5", destination, 4, 3);
        destination.Should().Equal(new byte[] { 0, 255, 255, 255, 0, 0, 0, 255, 255, 255, 255, 255 });
    }

    [Fact]
    public void The_bar_thresholds_are_the_ones_plan_section_7_states()
    {
        PromptedRegionBar.Source.Should().Contain("plan.md §7");
        PromptedRegionBar.Thresholds.OutsideMeanMax.Should().Be(1.0, "\"mean |Δ| outside the ground-truth mask ... ≤ 1/255\"");
        PromptedRegionBar.Thresholds.OutsideP99Max.Should().Be(2.0, "\"p99 ≤ 2/255\"");
        PromptedRegionBar.Thresholds.InsideMeanMin.Should().Be(8.0, "\"inside the mask mean |Δ| ≥ 8/255 for Blur/Pixelate/Outline\"");
        PromptedRegionBar.Thresholds.ChangedSetIoUMin.Should().Be(0.99, "\"Changed-pixel set vs ground truth: IoU ≥ 0.99 per frame (p05)\"");
        PromptedRegionBar.Thresholds.CentroidErrorPxMax.Should().Be(2.0, "\"effect centroid within 2 px of the ground-truth object centroid\"");
        PromptedRegionBar.Thresholds.AreaErrorP95Max.Should().Be(0.01, "\"p95 relative error ≤ 1%\"");
        PromptedRegionBar.Thresholds.OccludedFrameChangedPixelsMax.Should().Be(0, "\"the renders are identical (0 changed pixels)\"");
        PromptedRegionBar.Thresholds.MaskDilationPx.Should().Be(1, "\"outside the ground-truth mask (1 px dilated)\"");
    }

    private static string DecodedVideoHash(string path, string pixelFormat)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-v", "error", "-i", path, "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", pixelFormat, "-" })
            psi.ArgumentList.Add(a);
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var sha = SHA256.Create();
        using (var sink = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            process.StandardOutput.BaseStream.CopyTo(sink);
        string error = stderr.GetAwaiter().GetResult();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, error);
        return Convert.ToHexStringLower(sha.Hash!);
    }
}

/// <summary>Shared generated fixtures and control renders, so one test class generates once.</summary>
internal static class PromptedRegionTestFixtures
{
    private const double BlurSigma = 8;

    private static readonly Lazy<PromptedRegionFixture> DashboardFixture = new(
        () => PromptedRegionFixtures.GenerateDashboard(NewTemporaryDirectory("dashboard")));

    private static readonly Lazy<(PromptedRegionBarReport Report, string Directory)> MaskedPair = new(
        () => RenderControl(everywhere: false));

    private static readonly Lazy<(PromptedRegionBarReport Report, string Directory)> GlobalPair = new(
        () => RenderControl(everywhere: true));

    public static PromptedRegionFixture Dashboard => DashboardFixture.Value;

    public static string NewTemporaryDirectory(string label) => Path.Combine(
        Path.GetTempPath(),
        string.Create(CultureInfo.InvariantCulture, $"prompted-region-{label}-{Guid.NewGuid():N}"));

    /// <summary>Renders the effect-on/effect-off pair for one control and measures it.</summary>
    public static (PromptedRegionBarReport Report, string Directory) MeasureBlurControl(bool everywhere) =>
        everywhere ? GlobalPair.Value : MaskedPair.Value;

    private static (PromptedRegionBarReport Report, string Directory) RenderControl(bool everywhere)
    {
        PromptedRegionFixture fixture = Dashboard;
        string directory = NewTemporaryDirectory(everywhere ? "control-global" : "control-masked");
        Directory.CreateDirectory(directory);
        PromptedRegionFixtureOptions shape = PromptedRegionFixtures.Dashboard;

        string off = PromptedRegionEffectRenders.RenderLossless(
            fixture.ClipPath, fixture.MaskPath, Path.Combine(directory, "effect-off.mkv"), "[0:v]format=gbrp[o]",
            shape.FrameCount);
        string on = PromptedRegionEffectRenders.RenderLossless(
            fixture.ClipPath, fixture.MaskPath, Path.Combine(directory, "effect-on.mkv"),
            PromptedRegionEffectRenders.BlurGraph(BlurSigma, everywhere, fpsNum: 24, fpsDen: 1),
            shape.FrameCount);

        PromptedRegionBarReport report = PromptedRegionBarHarness.Measure(on, off, fixture.MaskPath, fixture.SidecarPath);
        // The report is the artifact a human or T-009 reads; keep it beside the renders it describes.
        File.WriteAllText(Path.Combine(directory, "report.json"), report.ToJson());
        return (report, directory);
    }
}
