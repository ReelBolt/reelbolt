#nullable enable
using System;
using System.Collections.Generic;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The tolerance band the golden-parity gate accepts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every number here is measured, and the measurement is quoted next to it.</b> The calibration
/// pairs are rendered by <see cref="GoldenParityHarness"/> on the same host that runs the
/// assertion, and the values in these comments are the numbers that run reported — they are
/// recorded rather than asserted, and the report quotes the artifact rather than this file.
/// </para>
/// <para>
/// <b>What a pass has to mean.</b> The gate exists to answer one question in P4-Q1: <i>does
/// <c>StepType.TimelineRender</c> reproduce the <c>VideoCompile</c> output for the same edit?</i>
/// The bar is therefore "visually indistinguishable from the compile output", not "bit-identical":
/// the two paths genuinely are different encoders, and demanding bit-identity would fail every
/// legitimate renderer. The floor is placed at the midpoint of the two measured bands (the
/// equivalent-edit pair and the one-frame negative control), rounded to a whole dB, so it rejects
/// a real edit while tolerating a re-encode with the same margin on either side.
/// </para>
/// </remarks>
/// <param name="MinPsnrDb">
/// Minimum acceptable <c>average</c> PSNR, in dB, between the two outputs' decoded video.
/// <para>
/// <b>Measured provenance (ffmpeg 6.1.1-3ubuntu5, this host, P4-Q1):</b> the equivalent-edit
/// pair (P2, the production <c>TimelineRenderer</c> — CRF 10 all-intra region mezzanines, concat,
/// one CRF 18 delivery encode — against the spike's single-generation segmented-concat path)
/// measured <b>43.97 dB</b>; the negative control (P3, one frame of boundary shift through the
/// same renderer) measured <b>28.09 dB</b>. 36 dB is their midpoint: <b>7.97 dB below P2</b> (the
/// reject budget before a legitimate renderer fails) and <b>7.91 dB above P3</b> (the detection
/// budget before a real edit slips through).
/// </para>
/// <para>
/// <b>History.</b> The spike-era floor was 40 dB, calibrated when both P2 legs were spike paths
/// (47.49 dB vs 28.04 dB, ffmpeg 8.0.1). The real renderer's second encode generation costs about
/// 3.5 dB against the one-generation spike; a lossless (<c>-qp 0</c>) mezzanine was measured at
/// <b>45.01 dB</b>, i.e. only ~1 dB of that is the mezzanine and the rest is the two paths'
/// different filter chains. The mezzanine stays at CRF 10 because the region cache stores those
/// files in the object store and lossless intra 1080p is an order of magnitude larger, so the floor
/// was re-centred on the measured bands instead of the renderer being bent toward the spike.
/// </para>
/// </param>
/// <param name="MaxAudioRmsDiffDb">
/// Maximum acceptable whole-file audio RMS level difference, in dB.
/// <para>
/// <b>Measured provenance:</b> P2 measured <b>0.003 dB</b> whole-file (0.022 dB in the worst
/// 250 ms window): the production renderer mixes to a 16-bit PCM intermediate and AAC-encodes it
/// in the delivery pass, the spike encodes AAC straight from its float filter chain, and that is
/// the size of the difference. P1 and P3, the same renderer twice, measure <b>0.000 dB</b>. A 6 dB
/// level divergence (the audio-sensitivity probe below) measured <b>6.005 dB</b>. 1 dB is
/// <b>~1.0 dB above the pass band</b> and <b>~5.0 dB below the probe</b>: it leaves room for two
/// independent AAC encodes of the same PCM to diverge on a different host, while staying far under
/// the smallest mix error this leg exists to catch (a missing track or a wrong bed gain is several
/// dB, not a fraction of one).
/// </para>
/// </param>
/// <param name="MaxBitExactFrameHashMismatches">
/// Maximum number of frames allowed to differ when a pair declares itself bit-exact.
/// <para>
/// <b>Measured provenance:</b> P1 (the same renderer invoked twice with a byte-identical invocation
/// recipe) produced <b>byte-identical output files</b>, so this is exactly 0. P2 measured <b>60 of
/// 60</b> mismatching frames at 43.97 dB (every frame goes through a different encoder path), and
/// the one-frame negative control measured <b>39 of 60</b> — which is why frame-hash parity is
/// <i>not</i> the cross-path gate: encoding noise alone mismatches more frames than a genuine
/// one-frame edit does. Frame hashes gate bit-exactness
/// (P1); PSNR gates parity (P2/P3).
/// </para>
/// </param>
public sealed record GoldenParityThresholds(
    double MinPsnrDb,
    double MaxAudioRmsDiffDb,
    int MaxBitExactFrameHashMismatches)
{
    /// <summary>
    /// The calibrated tolerance band. See the individual parameters for the measurement each value
    /// came from; the harness writes those measurements to <c>golden-parity-measurements.json</c> on
    /// every run so they can be re-checked rather than trusted.
    /// </summary>
    public static GoldenParityThresholds Calibrated { get; } = new(
        MinPsnrDb: 36.0,
        MaxAudioRmsDiffDb: 1.0,
        MaxBitExactFrameHashMismatches: 0);
}

/// <summary>What the harness generated for this run, and what it hashed to.</summary>
public sealed record GoldenParityFixtureDescription(
    string SourcePath,
    string SourceSha256,
    int SourceWidth,
    int SourceHeight,
    double SourceDurationSec,
    string BedPath,
    string BedSha256);

/// <summary>
/// The shape of the document <see cref="ReelBolt.Shared.Editing.EditTimelineSeeder"/> produced.
/// </summary>
/// <remarks>
/// Recorded, and asserted non-empty by the harness, because a manifest the seeder silently reduces
/// to nothing would make every number in the artifact vacuous while every ffmpeg call still
/// succeeded.
/// </remarks>
/// <param name="TrackCount">Tracks the seeder emitted.</param>
/// <param name="VideoClipCount">Media clips on the video track.</param>
/// <param name="AudioClipCount">Media clips on audio tracks.</param>
/// <param name="TotalItemCount">Every item across every track.</param>
/// <param name="TimelineName">The seeded timeline's name.</param>
/// <param name="Width">Seeded document width.</param>
/// <param name="Height">Seeded document height.</param>
/// <param name="RateNum">Seeded frame-rate numerator.</param>
/// <param name="RateDen">Seeded frame-rate denominator.</param>
/// <param name="RecordOutFrames">End of the last video clip, in record frames.</param>
public sealed record GoldenParityDocumentSummary(
    int TrackCount,
    int VideoClipCount,
    int AudioClipCount,
    int TotalItemCount,
    string TimelineName,
    int Width,
    int Height,
    int RateNum,
    int RateDen,
    long RecordOutFrames);

/// <summary>
/// What a calibration pair is required to prove.
/// </summary>
/// <remarks>
/// The three pairs are not three independent assertions that must all pass. P1 and P2 are required
/// to be <i>accepted</i> by the gate; P3 exists to be <i>rejected</i> by it, and a P3 the gate
/// accepts is a failure of the calibration, not a success of the renderer. Encoding that as data
/// rather than as prose in a report is what keeps the harness honest when the thresholds are
/// revisited.
/// </remarks>
public enum GoldenParityPairExpectation
{
    /// <summary>The gate must accept this pair.</summary>
    MustPassGate,

    /// <summary>The gate must reject this pair — it is the negative control.</summary>
    MustFailGate
}

/// <summary>One measured calibration pair and its gate outcome.</summary>
/// <param name="Id">Short pair id (<c>P1</c>, <c>P2</c>, <c>P3</c>).</param>
/// <param name="Description">What the pair is.</param>
/// <param name="ReferenceOutputPath">Where the reference side rendered.</param>
/// <param name="CandidateOutputPath">Where the candidate side rendered.</param>
/// <param name="ReferenceInvocation">Every argv the reference side used.</param>
/// <param name="CandidateInvocation">Every argv the candidate side used.</param>
/// <param name="ArgvIdentical">
/// Whether the two sides' invocation lists are identical once each side's own output path is
/// normalized away.
/// </param>
/// <param name="RequiresBitExactFrames">Whether this pair is gated on exact frame-hash parity.</param>
/// <param name="StreamPropertiesMatch">Whether every gated stream property matched.</param>
/// <param name="StreamDifferences">The properties that did not match, verbatim.</param>
/// <param name="ReferenceStreams">Measured stream properties of the reference output.</param>
/// <param name="CandidateStreams">Measured stream properties of the candidate output.</param>
/// <param name="FrameHashes">Frame-hash comparison.</param>
/// <param name="Psnr">PSNR measurement.</param>
/// <param name="AudioRms">Audio RMS measurement.</param>
/// <param name="FrameHashGateOk">
/// Bit-exactness gate outcome, or <c>null</c> when the pair does not gate on it (see
/// <see cref="RequiresBitExactFrames"/>).
/// </param>
/// <param name="PsnrGateOk">PSNR threshold outcome.</param>
/// <param name="AudioGateOk">Audio level threshold outcome.</param>
/// <param name="GateAccepted">Whether every gate that applies to this pair accepted it.</param>
/// <param name="Expectation">What this pair was required to prove.</param>
/// <param name="MetExpectation">
/// Whether the pair did what it was required to do. For P1/P2 that is <see cref="GateAccepted"/>; for
/// the P3 negative control it is the <i>negation</i> of it.
/// </param>
/// <param name="GateFailures">Why the gate rejected the pair, empty when it accepted it.</param>
/// <param name="ExpectationFailures">
/// Why the pair failed to do what it was required to do, empty when it did. Only these roll up into
/// the harness's own verdict: P3 being rejected by the gate is the intended outcome, not a failure.
/// </param>
public sealed record GoldenParityPairResult(
    string Id,
    string Description,
    string ReferenceOutputPath,
    string CandidateOutputPath,
    IReadOnlyList<IReadOnlyList<string>> ReferenceInvocation,
    IReadOnlyList<IReadOnlyList<string>> CandidateInvocation,
    bool ArgvIdentical,
    bool RequiresBitExactFrames,
    bool StreamPropertiesMatch,
    IReadOnlyList<string> StreamDifferences,
    GoldenParityStreamProperties ReferenceStreams,
    GoldenParityStreamProperties CandidateStreams,
    GoldenParityFrameHashComparison FrameHashes,
    GoldenParityPsnrMeasurement Psnr,
    GoldenParityAudioRmsMeasurement AudioRms,
    bool? FrameHashGateOk,
    bool PsnrGateOk,
    bool AudioGateOk,
    bool GateAccepted,
    GoldenParityPairExpectation Expectation,
    bool MetExpectation,
    IReadOnlyList<string> GateFailures,
    IReadOnlyList<string> ExpectationFailures);

/// <summary>
/// The supplementary audio-leg sensitivity probe.
/// </summary>
/// <remarks>
/// Not one of the three prescribed calibration pairs. It exists because P2 and P3 both leave the
/// audio leg at exactly 0.0 dB — a one-frame video shift does not move a stationary audio bed — so
/// without it the audio threshold would be unfalsified. It renders the same document with the bed
/// 6 dB lower and shows the threshold rejects it.
/// </remarks>
/// <param name="Description">What the probe does.</param>
/// <param name="ReferenceLevelDbfs">Reference output level.</param>
/// <param name="CandidateLevelDbfs">Probe output level.</param>
/// <param name="OverallLevelDiffDb">Measured level difference.</param>
/// <param name="MaxWindowDiffDb">Largest per-window difference.</param>
/// <param name="GateRejects">Whether the audio threshold rejects the probe (it must).</param>
public sealed record GoldenParityAudioSensitivityProbeResult(
    string Description,
    double ReferenceLevelDbfs,
    double CandidateLevelDbfs,
    double OverallLevelDiffDb,
    double MaxWindowDiffDb,
    bool GateRejects);

/// <summary>Everything one harness run measured, and the verdict it drew from it.</summary>
/// <param name="SchemaVersion">Artifact schema version.</param>
/// <param name="GeneratedAtUtc">When the run started.</param>
/// <param name="FfmpegVersion">The first line of the ffmpeg version banner actually used.</param>
/// <param name="ToolTimeoutSeconds">Per-invocation wall-clock ceiling in force.</param>
/// <param name="Fixture">The generated fixture media and its content hashes.</param>
/// <param name="Document">The shape of the seeded document.</param>
/// <param name="ShiftedDocument">The shape of the negative control's document.</param>
/// <param name="Pairs">The three measured calibration pairs.</param>
/// <param name="AudioSensitivityProbe">The supplementary audio-leg falsification probe.</param>
/// <param name="Thresholds">The tolerance band the gate applied.</param>
/// <param name="Passed">
/// Whether the harness did what it is required to do — P1 and P2 accepted, P3 rejected, the probe
/// rejected. It is <b>not</b> a claim that the editor can render a timeline; see
/// <see cref="GoldenParityHarness"/>.
/// </param>
/// <param name="Failures">Harness-level failures; empty when every expectation was met.</param>
/// <param name="ElapsedMs">Wall clock the harness itself took, in milliseconds.</param>
/// <param name="MeasurementsPath">Where this artifact was written.</param>
public sealed record GoldenParityVerdict(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string FfmpegVersion,
    double ToolTimeoutSeconds,
    GoldenParityFixtureDescription Fixture,
    GoldenParityDocumentSummary Document,
    GoldenParityDocumentSummary ShiftedDocument,
    IReadOnlyList<GoldenParityPairResult> Pairs,
    GoldenParityAudioSensitivityProbeResult AudioSensitivityProbe,
    GoldenParityThresholds Thresholds,
    bool Passed,
    IReadOnlyList<string> Failures,
    double ElapsedMs,
    string MeasurementsPath)
{
    /// <summary>The pair ids the harness always produces, in order.</summary>
    public static IReadOnlyList<string> PairIds { get; } = ["P1", "P2", "P3"];

    /// <summary>Finds a measured pair by id.</summary>
    /// <exception cref="InvalidOperationException">No pair carries that id.</exception>
    public GoldenParityPairResult Pair(string id)
    {
        foreach (GoldenParityPairResult pair in Pairs)
        {
            if (string.Equals(pair.Id, id, StringComparison.Ordinal))
            {
                return pair;
            }
        }

        throw new InvalidOperationException($"No measured pair with id '{id}'.");
    }
}
