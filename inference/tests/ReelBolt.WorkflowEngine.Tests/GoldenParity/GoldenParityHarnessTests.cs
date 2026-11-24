#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The golden-parity harness itself: the fixture it seeds, the three calibration pairs it measures
/// and the gate it draws from them.
/// </summary>
/// <remarks>
/// The harness runs once, in <see cref="GoldenParityTestEnvironment"/>, and every test here asserts
/// a different property of the single verdict it produced. Sharing one run is not an optimization
/// shortcut: P1/P2/P3 are a <i>single</i> calibration experiment, and re-running it per assertion
/// would let the three pairs disagree about the fixture they were measured on.
/// </remarks>
[Collection(GoldenParityCollection.Name)]
public class GoldenParityHarnessTests
{
    private readonly GoldenParityTestEnvironment _environment;

    public GoldenParityHarnessTests(GoldenParityTestEnvironment environment) => _environment = environment;

    private GoldenParityVerdict Verdict => _environment.Verdict;

    // ---------------------------------------------------------------------------------------
    // The fixture is seeded through the real seeder, and is not vacuous.
    // ---------------------------------------------------------------------------------------

    [FfmpegToolsFact]
    public void Seeded_document_comes_from_the_committed_manifest_and_is_not_vacuous()
    {
        GoldenParityDocumentSummary document = Verdict.Document;

        // The seeder documents a long list of skip cases. If the fixture manifest tripped one of
        // them the harness would still render, still measure, and mean nothing.
        document.VideoClipCount.Should().Be(2, "the fixture manifest carries two cuts on one video track");
        document.AudioClipCount.Should().Be(1, "the fixture manifest carries one audio bed");
        document.TrackCount.Should().Be(2, "one video track and one audio track survive the seed");
        document.TotalItemCount.Should().Be(3);
        document.RecordOutFrames.Should().Be(60, "a two-second edit at 30 fps is 60 record frames");

        document.Width.Should().Be(320);
        document.Height.Should().Be(180);
        document.RateNum.Should().Be(30);
        document.RateDen.Should().Be(1);

        // The timeline's own name is the manifest's storage key — the only name the manifest carries.
        document.TimelineName.Should().Be("golden-parity/compile-output.mp4");
    }

    [FfmpegToolsFact]
    public void Fixture_media_is_generated_at_test_time_and_hashed_not_committed()
    {
        Verdict.Fixture.SourceSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        Verdict.Fixture.BedSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        Verdict.Fixture.SourceSha256.Should().NotBe(Verdict.Fixture.BedSha256);

        File.Exists(Verdict.Fixture.SourcePath).Should().BeTrue();
        File.Exists(Verdict.Fixture.BedPath).Should().BeTrue();

        // Nothing binary is committed: the fixture paths are inside the run's own scratch directory.
        Verdict.Fixture.SourcePath.Should().StartWith(_environment.WorkingDirectory);
        Verdict.Fixture.BedPath.Should().StartWith(_environment.WorkingDirectory);
    }

    // ---------------------------------------------------------------------------------------
    // P1 — the same renderer twice. This is what decides whether frame hashes can gate at all.
    // ---------------------------------------------------------------------------------------

    [FfmpegToolsFact]
    public void P1_same_renderer_twice_has_byte_identical_argv_and_bit_exact_frames()
    {
        GoldenParityPairResult p1 = Verdict.Pair("P1");

        p1.ArgvIdentical.Should().BeTrue(
            "P1 is only meaningful if both invocations are literally the same render");
        p1.FrameHashes.Identical.Should().BeTrue(
            "libx264 at a fixed preset/crf is deterministic here, so identical argv must reproduce identical frames");
        p1.FrameHashes.MismatchCount.Should().Be(0);
        p1.StreamPropertiesMatch.Should().BeTrue();
        p1.GateAccepted.Should().BeTrue(string.Join("; ", p1.GateFailures));
        p1.MetExpectation.Should().BeTrue(string.Join("; ", p1.ExpectationFailures));

        // Identical frames means PSNR is not "very high", it is undefined — preserved as infinity so
        // "bit-identical" and "very close" stay distinguishable findings.
        double.IsPositiveInfinity(p1.Psnr.AverageDb).Should().BeTrue();
        p1.AudioRms.OverallLevelDiffDb.Should().Be(0.0);

        // And the stronger statement the report quotes: the files themselves are byte-identical.
        File.ReadAllBytes(p1.ReferenceOutputPath)
            .Should().Equal(File.ReadAllBytes(p1.CandidateOutputPath));
    }

    // ---------------------------------------------------------------------------------------
    // P2 — the pass band.
    // ---------------------------------------------------------------------------------------

    [FfmpegToolsFact]
    public void P2_equivalent_edit_through_two_encode_paths_passes_the_parity_gate()
    {
        GoldenParityPairResult p2 = Verdict.Pair("P2");

        p2.ArgvIdentical.Should().BeFalse("the whole point of P2 is that the two sides are different encodes");
        p2.StreamPropertiesMatch.Should().BeTrue(
            "both paths must describe the same programme before PSNR means anything: " +
            string.Join("; ", p2.StreamDifferences));
        p2.Psnr.AverageDb.Should().BeGreaterThanOrEqualTo(Verdict.Thresholds.MinPsnrDb);
        p2.AudioRms.OverallLevelDiffDb.Should().BeLessThanOrEqualTo(Verdict.Thresholds.MaxAudioRmsDiffDb);
        p2.PsnrGateOk.Should().BeTrue();
        p2.AudioGateOk.Should().BeTrue();
        p2.GateAccepted.Should().BeTrue(string.Join("; ", p2.GateFailures));
        p2.MetExpectation.Should().BeTrue(string.Join("; ", p2.ExpectationFailures));

        // The finding that sets the shape of the gate: a real re-encode at this PSNR still differs on
        // most individual frames, so exact frame-hash parity is NOT a cross-path gate.
        p2.FrameHashes.Identical.Should().BeFalse();
        p2.FrameHashGateOk.Should().BeNull("frame-hash bit-exactness is not applied across encode paths");
    }

    // ---------------------------------------------------------------------------------------
    // P3 — the fail band, without which the threshold is unfalsifiable.
    // ---------------------------------------------------------------------------------------

    [FfmpegToolsFact]
    public void P3_one_frame_boundary_shift_fails_the_same_gate_P2_passes()
    {
        GoldenParityPairResult p2 = Verdict.Pair("P2");
        GoldenParityPairResult p3 = Verdict.Pair("P3");

        // Anti-vacuity: the negative control has to be a genuinely different edit. A pair that
        // renders the same frames would "fail" nothing and prove nothing.
        p3.FrameHashes.MismatchCount.Should().BeGreaterThan(0);
        p3.FrameHashes.Identical.Should().BeFalse();

        // The shift moves the second cut's source in-point, not its record range, so the two edits
        // are the same length and this is a threshold test rather than a stream-property test.
        p3.StreamPropertiesMatch.Should().BeTrue(string.Join("; ", p3.StreamDifferences));
        p3.ReferenceStreams.FrameCount.Should().Be(p3.CandidateStreams.FrameCount);
        p3.ReferenceStreams.FrameCount.Should().Be(60);

        p3.Psnr.AverageDb.Should().BeLessThan(p2.Psnr.AverageDb);
        p3.PsnrGateOk.Should().BeFalse("a one-frame edit must not pass the parity threshold");
        p3.Psnr.AverageDb.Should().BeLessThan(Verdict.Thresholds.MinPsnrDb);

        // P3 is the negative control: the gate rejecting it is the outcome the calibration REQUIRES,
        // so GateAccepted is false while MetExpectation is true. Conflating those two would make the
        // harness report its own success as a failure.
        p3.Expectation.Should().Be(GoldenParityPairExpectation.MustFailGate);
        p3.GateAccepted.Should().BeFalse();
        p3.MetExpectation.Should().BeTrue(string.Join("; ", p3.ExpectationFailures));

        // The audio leg is a level gate: a one-frame picture shift leaves a stationary audio bed
        // measurably unchanged, which is exactly why the harness carries a separate audio probe.
        p3.AudioRms.OverallLevelDiffDb.Should().BeLessThanOrEqualTo(Verdict.Thresholds.MaxAudioRmsDiffDb);
    }

    // ---------------------------------------------------------------------------------------
    // The thresholds themselves.
    // ---------------------------------------------------------------------------------------

    [FfmpegToolsFact]
    public void Psnr_threshold_sits_strictly_between_P2_and_P3_with_margin_on_both_sides()
    {
        double passBand = Verdict.Pair("P2").Psnr.AverageDb;
        double failBand = Verdict.Pair("P3").Psnr.AverageDb;
        double threshold = Verdict.Thresholds.MinPsnrDb;

        failBand.Should().BeLessThan(passBand);
        threshold.Should().BeGreaterThan(failBand, "a threshold below the fail band would accept a real edit");
        threshold.Should().BeLessThan(passBand, "a threshold above the pass band would reject a legitimate renderer");

        // Both margins are wide enough to survive a different host's encoder build, which is the
        // only thing that can move these numbers on a machine with the same fixture.
        (passBand - threshold).Should().BeGreaterThan(5.0);
        (threshold - failBand).Should().BeGreaterThan(5.0);
    }

    /// <summary>
    /// How far two legitimate renderers' audio may sit apart before it is a mix difference rather than
    /// a quantisation one: the production mix's 16-bit PCM intermediate measured 0.003 dB whole-file
    /// against the spike's float chain. 0.05 dB is ~15x that measurement and 1/20 of the gate.
    /// </summary>
    internal const double MixIntermediateToleranceDb = 0.05;

    [FfmpegToolsFact]
    public void Audio_threshold_is_the_only_leg_that_a_level_divergence_falsifies()
    {
        GoldenParityAudioSensitivityProbeResult probe = Verdict.AudioSensitivityProbe;

        // P3 (the same renderer, a shifted VIDEO cut) leaves the audio bit-identical, and P2's two
        // renderers differ only by a 16-bit PCM intermediate before AAC (measured 0.003 dB — see
        // GoldenParityThresholds), so the audio threshold would be unfalsified by the three
        // prescribed pairs alone. The probe supplies the missing falsification.
        Verdict.Pair("P2").AudioRms.OverallLevelDiffDb.Should().BeLessThan(MixIntermediateToleranceDb);
        Verdict.Pair("P3").AudioRms.OverallLevelDiffDb.Should().Be(0.0);

        probe.OverallLevelDiffDb.Should().BeGreaterThan(Verdict.Thresholds.MaxAudioRmsDiffDb);
        probe.GateRejects.Should().BeTrue(
            "the audio leg must reject a real mix divergence, or it is not a gate");
        probe.OverallLevelDiffDb.Should().BeApproximately(6.0, 0.5, "the probe renders the bed 6 dB lower");
    }

    [FfmpegToolsFact]
    public void Thresholds_carry_their_measured_provenance_and_the_gate_holds_together()
    {
        GoldenParityThresholds thresholds = Verdict.Thresholds;

        thresholds.MinPsnrDb.Should().Be(36.0);
        thresholds.MaxAudioRmsDiffDb.Should().Be(1.0);
        thresholds.MaxBitExactFrameHashMismatches.Should().Be(0);

        Verdict.Failures.Should().BeEmpty(string.Join("; ", Verdict.Failures));
        Verdict.Passed.Should().BeTrue();
        Verdict.Pairs.Select(p => p.Id).Should().Equal(GoldenParityVerdict.PairIds);

        // Both directions of the calibration are asserted: the equivalent pair is accepted, the
        // negative control is rejected. A verdict that only checked the first would be unfalsifiable.
        Verdict.Pair("P1").GateAccepted.Should().BeTrue();
        Verdict.Pair("P2").GateAccepted.Should().BeTrue();
        Verdict.Pair("P3").GateAccepted.Should().BeFalse();
        Verdict.Pairs.Should().OnlyContain(p => p.MetExpectation);
    }

    // ---------------------------------------------------------------------------------------
    // The artifact.
    // ---------------------------------------------------------------------------------------

    [FfmpegToolsFact]
    public void Harness_writes_a_machine_readable_measurements_artifact()
    {
        File.Exists(_environment.MeasurementsPath).Should().BeTrue(
            $"the harness must write {GoldenParityHarness.MeasurementsFileName} on every run, so the report quotes " +
            "measured values rather than claims");

        using JsonDocument artifact = JsonDocument.Parse(File.ReadAllText(_environment.MeasurementsPath));
        JsonElement root = artifact.RootElement;

        root.GetProperty("schemaVersion").GetInt32().Should().Be(GoldenParityHarness.MeasurementsSchemaVersion);
        root.GetProperty("passed").GetBoolean().Should().BeTrue();
        root.GetProperty("ffmpegVersion").GetString().Should().Contain("ffmpeg");

        JsonElement pairs = root.GetProperty("pairs");
        pairs.GetArrayLength().Should().Be(3);

        // Every gated number the report quotes is present in the artifact, for every pair.
        foreach (JsonElement pair in pairs.EnumerateArray())
        {
            pair.GetProperty("id").GetString().Should().NotBeNullOrWhiteSpace();
            ReadDb(pair.GetProperty("psnr").GetProperty("averageDb")).Should().NotBe(double.NaN);
            pair.GetProperty("audioRms").GetProperty("overallLevelDiffDb").GetDouble().Should().BeGreaterThanOrEqualTo(0.0);
            pair.GetProperty("frameHashes").GetProperty("mismatchCount").GetInt32().Should().BeGreaterThanOrEqualTo(0);
            pair.GetProperty("referenceStreams").GetProperty("frameCount").GetInt32().Should().BeGreaterThan(0);
            pair.GetProperty("candidateStreams").GetProperty("frameCount").GetInt32().Should().BeGreaterThan(0);
            pair.GetProperty("expectation").GetString().Should().BeOneOf("MustPassGate", "MustFailGate");
        }

        root.GetProperty("audioSensitivityProbe").GetProperty("gateRejects").GetBoolean().Should().BeTrue();
        root.GetProperty("elapsedMs").GetDouble().Should().BeGreaterThan(0.0);
    }

    /// <summary>
    /// Reads a dB figure that may be written as a JSON number or as the named literal
    /// <c>"Infinity"</c> (P1's bit-identical frames produce an infinite PSNR).
    /// </summary>
    private static double ReadDb(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? double.Parse(element.GetString()!, CultureInfo.InvariantCulture)
            : element.GetDouble();

    // ---------------------------------------------------------------------------------------
    // A missing ffmpeg must never look like a pass.
    // ---------------------------------------------------------------------------------------

    [FfmpegToolsFact]
    public async Task Harness_fails_loudly_when_ffmpeg_cannot_be_executed()
    {
        string missingFfmpeg = Path.Combine(Path.GetTempPath(), "golden-parity-absent-" + Guid.NewGuid().ToString("N"), "ffmpeg");
        var brokenRunner = GoldenParityTooling.CreateRunner(missingFfmpeg, missingFfmpeg);

        string scratch = Path.Combine(Path.GetTempPath(), "golden-parity-absent-run-" + Guid.NewGuid().ToString("N"));
        string measurements = Path.Combine(scratch, GoldenParityHarness.MeasurementsFileName);

        GoldenParityRequest request = _environment.BuildRequest(brokenRunner, scratch, measurements);

        var act = async () => await GoldenParityHarness.RunAsync(request);

        (await act.Should().ThrowAsync<GoldenParityToolingUnavailableException>())
            .WithMessage("*could not be started*");

        // And crucially: no artifact, so there is nothing a later reader could mistake for evidence.
        File.Exists(measurements).Should().BeFalse();

        if (Directory.Exists(scratch))
        {
            Directory.Delete(scratch, recursive: true);
        }
    }
}
