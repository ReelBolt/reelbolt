#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Everything one harness run needs. The seams are injected so that P4-Q1 can swap the throwaway
/// spike adapters for adapters over the real renderers without touching the harness.
/// </summary>
/// <param name="WorkingDirectory">Scratch directory for fixture media, renders and raw dumps. Created if missing.</param>
/// <param name="ManifestPath">The committed v1 <see cref="TimelineManifest"/> JSON the seeder consumes.</param>
/// <param name="MeasurementsPath">Where <c>golden-parity-measurements.json</c> is written.</param>
/// <param name="Runner">The single ffmpeg/ffprobe seam. Every invocation in this run goes through it.</param>
/// <param name="TimelineRenderPath">
/// The candidate path — the <c>TimelineRender</c>-shaped renderer being checked.
/// </param>
/// <param name="VideoCompilePath">
/// The reference path — the <c>VideoCompile</c>-shaped renderer the candidate is checked against.
/// </param>
/// <param name="AudioDivergenceProbe">
/// A render of the same document with a deliberately different audio level, used to show the audio
/// threshold can reject a real mix divergence. See <see cref="GoldenParityThresholds.MaxAudioRmsDiffDb"/>.
/// </param>
/// <param name="ToolTimeout">Per-invocation wall-clock ceiling. Defaults to two minutes.</param>
public sealed record GoldenParityRequest(
    string WorkingDirectory,
    string ManifestPath,
    string MeasurementsPath,
    IVideoToolRunner Runner,
    ITimelineRenderSeam TimelineRenderPath,
    ITimelineRenderSeam VideoCompilePath,
    ITimelineRenderSeam AudioDivergenceProbe,
    TimeSpan ToolTimeout = default)
{
    /// <summary>The timeout actually applied, resolving the record's <c>default</c> sentinel.</summary>
    public TimeSpan EffectiveToolTimeout =>
        ToolTimeout == default ? TimeSpan.FromMinutes(2) : ToolTimeout;
}

/// <summary>
/// The golden-parity harness (plan task P0-M2 / decisions.md point 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>What it does.</b> Generates a deterministic fixture, seeds a v2 <see cref="EditTimeline"/> from
/// a committed v1 compile-result manifest through the real <see cref="EditTimelineSeeder"/>, renders
/// that document through an <see cref="ITimelineRenderSeam"/> and through a second, differently
/// shaped renderer, and compares the two outputs by stream properties, frame hash, PSNR and audio
/// RMS — writing every measured value to <c>golden-parity-measurements.json</c> so the numbers are
/// auditable rather than asserted.
/// </para>
/// <para>
/// <b>What it does NOT do — read this before trusting a pass.</b> The production renderer leg is
/// <b>not wired</b>. <c>StepType.TimelineRender</c> and <c>TimelineRenderStepExecutor</c> do not
/// exist until plan task P4-M1, so the harness renders through
/// <see cref="SpikeTimelineRenderSeam"/> — throwaway spike scaffolding. A green verdict here means
/// <i>the tooling works and the tolerances are calibrated and falsifiable</i>. It does <b>not</b>
/// mean the editor can render a timeline. The real gate lands in P4-Q1, and
/// <c>TimelineRenderSeamMarkerTests</c> keeps the gap visible as a skip in every test run.
/// </para>
/// <para>
/// <b>The three measured pairs</b> (<see cref="GoldenParityVerdict.PairIds"/>):
/// <c>P1</c> the same renderer twice with a byte-identical invocation recipe (its own output path
/// aside — see <see cref="InvocationsEqual"/>) — proves determinism and decides whether frame-hash
/// parity is usable at all; <c>P2</c> the same edit through the two encode paths — sets the pass
/// band; <c>P3</c> the same edit with one cut's source in-point shifted by a single frame — sets the
/// fail band, without which the threshold would be unfalsifiable.
/// </para>
/// </remarks>
public static class GoldenParityHarness
{
    /// <summary>Name of the machine-readable measurements artifact.</summary>
    public const string MeasurementsFileName = "golden-parity-measurements.json";

    /// <summary>Schema version of the measurements artifact.</summary>
    public const int MeasurementsSchemaVersion = 1;

    /// <summary>
    /// Where the generated fixture media lands inside a harness working directory. Public so a
    /// caller wiring its own seam can resolve fixture assets to the same place the harness wrote
    /// them.
    /// </summary>
    public static string FixtureDirectoryFor(string workingDirectory) =>
        Path.Combine(workingDirectory, "fixture");

    /// <summary>
    /// Runs the whole harness and returns its verdict, writing the measurements artifact on the way.
    /// </summary>
    /// <exception cref="GoldenParityToolingUnavailableException">
    /// ffmpeg cannot be executed. Deliberately an exception: a missing binary must fail loudly rather
    /// than produce a green verdict from no measurement at all.
    /// </exception>
    public static async Task<GoldenParityVerdict> RunAsync(
        GoldenParityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        TimeSpan timeout = request.EffectiveToolTimeout;
        Directory.CreateDirectory(request.WorkingDirectory);

        // 0. Prove the tooling executes BEFORE any measurement, so absence can never be mistaken
        //    for a pass.
        string ffmpegVersion = await GoldenParityTooling
            .RequireFfmpegVersionAsync(request.Runner, timeout, cancellationToken)
            .ConfigureAwait(false);

        // 1. Fixture: generated at test time, never committed.
        string fixtureDirectory = FixtureDirectoryFor(request.WorkingDirectory);
        var media = await GoldenParityFixtureSources
            .CreateAsync(request.Runner, fixtureDirectory, timeout, cancellationToken)
            .ConfigureAwait(false);

        // 2. Seed leg: the committed v1 manifest through the real EditTimelineSeeder.
        TimelineManifest manifest = GoldenParityManifest.Load(request.ManifestPath);
        EditTimeline document = EditTimelineSeeder.Seed(manifest, FixtureTimelineId, GoldenParityAssets.Resolve);
        GoldenParityDocumentSummary documentSummary = Summarize(document);
        RequireNonEmpty(documentSummary, "the seeded document");

        // 3. The negative control's document: identical except for one cut's source in-point.
        EditTimeline shiftedDocument = EditTimelineSeeder.Seed(
            GoldenParityManifest.ShiftSecondVideoCutInPoint(manifest, frames: 1),
            FixtureTimelineId,
            GoldenParityAssets.Resolve);
        GoldenParityDocumentSummary shiftedSummary = Summarize(shiftedDocument);
        RequireNonEmpty(shiftedSummary, "the shifted document");

        // 4. Renders. P1 reuses its first output as the reference for P2 and P3, so every pair is
        //    measured against the same unshifted, TimelineRender-shaped render.
        string renderDirectory = Path.Combine(request.WorkingDirectory, "renders");
        Directory.CreateDirectory(renderDirectory);

        TimelineRenderProduct p1Reference = await request.TimelineRenderPath
            .RenderAsync(document, Path.Combine(renderDirectory, "p1-timeline-render-a.mp4"), cancellationToken)
            .ConfigureAwait(false);
        TimelineRenderProduct p1Repeat = await request.TimelineRenderPath
            .RenderAsync(document, Path.Combine(renderDirectory, "p1-timeline-render-b.mp4"), cancellationToken)
            .ConfigureAwait(false);
        TimelineRenderProduct p2Reference = await request.VideoCompilePath
            .RenderAsync(document, Path.Combine(renderDirectory, "p2-video-compile.mp4"), cancellationToken)
            .ConfigureAwait(false);
        TimelineRenderProduct p3Shifted = await request.TimelineRenderPath
            .RenderAsync(shiftedDocument, Path.Combine(renderDirectory, "p3-shifted.mp4"), cancellationToken)
            .ConfigureAwait(false);
        TimelineRenderProduct probe = await request.AudioDivergenceProbe
            .RenderAsync(document, Path.Combine(renderDirectory, "audio-probe.mp4"), cancellationToken)
            .ConfigureAwait(false);

        var frameHashes = new FrameHashComparer(request.Runner, timeout);
        var psnr = new PsnrComparer(request.Runner, timeout);
        var audio = new AudioRmsComparer(request.Runner, timeout);
        var streams = new StreamPropertyComparer(request.Runner, timeout);

        GoldenParityThresholds thresholds = GoldenParityThresholds.Calibrated;

        PairPlan[] plans =
        [
            new PairPlan(
                Id: "P1",
                Description: "The same renderer invoked twice with a byte-identical invocation recipe, its own output path aside (determinism / bit-exactness).",
                Reference: p1Reference,
                Candidate: p1Repeat,
                RequiresBitExactFrames: true,
                RequiresIdenticalArgv: true,
                Expectation: GoldenParityPairExpectation.MustPassGate),
            new PairPlan(
                Id: "P2",
                Description: "The same edit through two encode paths: one filter_complex pass vs per-cut segments joined by the concat demuxer (the pass band).",
                Reference: p1Reference,
                Candidate: p2Reference,
                RequiresBitExactFrames: false,
                RequiresIdenticalArgv: false,
                Expectation: GoldenParityPairExpectation.MustPassGate),
            new PairPlan(
                Id: "P3",
                Description: "Negative control: the same edit with the second cut's source in-point shifted by exactly one frame (the fail band).",
                Reference: p1Reference,
                Candidate: p3Shifted,
                RequiresBitExactFrames: false,
                RequiresIdenticalArgv: false,
                Expectation: GoldenParityPairExpectation.MustFailGate)
        ];

        var pairResults = new List<GoldenParityPairResult>(plans.Length);
        foreach (PairPlan plan in plans)
        {
            pairResults.Add(await MeasureAsync(
                plan,
                thresholds,
                Path.Combine(request.WorkingDirectory, "pairs", plan.Id),
                frameHashes,
                psnr,
                audio,
                streams,
                cancellationToken).ConfigureAwait(false));
        }

        // 5. Supplementary audio-leg sensitivity probe — see the record's own remarks.
        var probeReference = await audio.ExtractAsync(
            p1Reference.OutputPath,
            Path.Combine(request.WorkingDirectory, "pairs", "audio-probe-reference.wav"),
            cancellationToken).ConfigureAwait(false);
        var probeCandidate = await audio.ExtractAsync(
            probe.OutputPath,
            Path.Combine(request.WorkingDirectory, "pairs", "audio-probe-candidate.wav"),
            cancellationToken).ConfigureAwait(false);
        GoldenParityAudioRmsMeasurement probeMeasurement = AudioRmsComparer.Difference(probeReference, probeCandidate);

        var probeResult = new GoldenParityAudioSensitivityProbeResult(
            Description: "The same document rendered with the music bed 6 dB lower: the audio leg's own falsification test.",
            ReferenceLevelDbfs: probeMeasurement.ReferenceLevelDbfs,
            CandidateLevelDbfs: probeMeasurement.CandidateLevelDbfs,
            OverallLevelDiffDb: probeMeasurement.OverallLevelDiffDb,
            MaxWindowDiffDb: probeMeasurement.MaxWindowDiffDb,
            GateRejects: probeMeasurement.OverallLevelDiffDb > thresholds.MaxAudioRmsDiffDb);

        // Only expectation failures roll up: P2 and P3 are required to disagree about the gate.
        var failures = new List<string>(pairResults.SelectMany(p => p.ExpectationFailures));
        if (!probeResult.GateRejects)
        {
            failures.Add(
                $"Audio sensitivity probe: a 6 dB bed-level divergence measured only " +
                $"{Format(probeResult.OverallLevelDiffDb)} dB, which the audio threshold " +
                $"({Format(thresholds.MaxAudioRmsDiffDb)} dB) does not reject — the audio leg is not falsifiable " +
                "at this threshold.");
        }

        stopwatch.Stop();

        var verdict = new GoldenParityVerdict(
            SchemaVersion: MeasurementsSchemaVersion,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            FfmpegVersion: ffmpegVersion,
            ToolTimeoutSeconds: timeout.TotalSeconds,
            Fixture: new GoldenParityFixtureDescription(
                SourcePath: media.SourcePath,
                SourceSha256: media.SourceSha256,
                SourceWidth: GoldenParityFixtureSources.Width,
                SourceHeight: GoldenParityFixtureSources.Height,
                SourceDurationSec: GoldenParityFixtureSources.SourceDurationSec,
                BedPath: media.BedPath,
                BedSha256: media.BedSha256),
            Document: documentSummary,
            ShiftedDocument: shiftedSummary,
            Pairs: pairResults,
            AudioSensitivityProbe: probeResult,
            Thresholds: thresholds,
            Passed: failures.Count == 0,
            Failures: failures,
            ElapsedMs: stopwatch.Elapsed.TotalMilliseconds,
            MeasurementsPath: request.MeasurementsPath);

        await WriteMeasurementsAsync(verdict, cancellationToken).ConfigureAwait(false);

        return verdict;
    }

    /// <summary>
    /// Measures one calibration pair end to end and applies every gate that applies to it.
    /// </summary>
    private static async Task<GoldenParityPairResult> MeasureAsync(
        PairPlan plan,
        GoldenParityThresholds thresholds,
        string pairDirectory,
        FrameHashComparer frameHashes,
        PsnrComparer psnr,
        AudioRmsComparer audio,
        StreamPropertyComparer streams,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(pairDirectory);

        // Geometry is needed before frames can be hashed (the frame size follows from it), so the
        // probe comes first and the rest of the properties are filled in afterwards.
        MediaProbeResult referenceProbe = await streams.ProbeAsync(plan.Reference.OutputPath, cancellationToken).ConfigureAwait(false);
        MediaProbeResult candidateProbe = await streams.ProbeAsync(plan.Candidate.OutputPath, cancellationToken).ConfigureAwait(false);

        GoldenParityFrameHashes referenceHashes = await frameHashes.ComputeAsync(
            plan.Reference.OutputPath,
            referenceProbe.Width,
            referenceProbe.Height,
            pairDirectory,
            cancellationToken).ConfigureAwait(false);
        GoldenParityFrameHashes candidateHashes = await frameHashes.ComputeAsync(
            plan.Candidate.OutputPath,
            candidateProbe.Width,
            candidateProbe.Height,
            pairDirectory,
            cancellationToken).ConfigureAwait(false);

        GoldenParityAudioExtraction referenceAudio = await audio.ExtractAsync(
            plan.Reference.OutputPath,
            Path.Combine(pairDirectory, "reference.audio.wav"),
            cancellationToken).ConfigureAwait(false);
        GoldenParityAudioExtraction candidateAudio = await audio.ExtractAsync(
            plan.Candidate.OutputPath,
            Path.Combine(pairDirectory, "candidate.audio.wav"),
            cancellationToken).ConfigureAwait(false);

        GoldenParityStreamProperties referenceStreams = await streams.ReadAsync(
            plan.Reference.OutputPath, referenceHashes.FrameCount, referenceAudio.WavPath, cancellationToken).ConfigureAwait(false);
        GoldenParityStreamProperties candidateStreams = await streams.ReadAsync(
            plan.Candidate.OutputPath, candidateHashes.FrameCount, candidateAudio.WavPath, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> streamDifferences = referenceStreams.DifferencesFrom(candidateStreams);
        GoldenParityFrameHashComparison frameComparison = FrameHashComparer.Compare(referenceHashes, candidateHashes);
        GoldenParityAudioRmsMeasurement audioMeasurement = AudioRmsComparer.Difference(referenceAudio, candidateAudio);
        GoldenParityPsnrMeasurement psnrMeasurement = await psnr
            .CompareAsync(plan.Reference.OutputPath, plan.Candidate.OutputPath, cancellationToken)
            .ConfigureAwait(false);

        bool argvIdentical = InvocationsEqual(
            plan.Reference.Invocations,
            plan.Candidate.Invocations,
            plan.Reference.OutputPath,
            plan.Candidate.OutputPath);

        var failures = new List<string>();

        if (plan.RequiresIdenticalArgv && !argvIdentical)
        {
            failures.Add($"{plan.Id}: the two sides' ffmpeg invocations are not byte-identical, so this is not the same render and the pair proves nothing.");
        }

        bool streamsOk = streamDifferences.Count == 0;
        if (!streamsOk)
        {
            failures.Add($"{plan.Id}: stream properties differ, so no PSNR/audio number for this pair is meaningful ({string.Join("; ", streamDifferences)}).");
        }

        bool psnrOk = psnrMeasurement.AverageDb >= thresholds.MinPsnrDb;
        if (!psnrOk)
        {
            failures.Add($"{plan.Id}: PSNR average {Format(psnrMeasurement.AverageDb)} dB is below the {Format(thresholds.MinPsnrDb)} dB floor.");
        }

        bool audioOk = audioMeasurement.OverallLevelDiffDb <= thresholds.MaxAudioRmsDiffDb;
        if (!audioOk)
        {
            failures.Add($"{plan.Id}: audio RMS level difference {Format(audioMeasurement.OverallLevelDiffDb)} dB exceeds the {Format(thresholds.MaxAudioRmsDiffDb)} dB ceiling.");
        }

        bool? frameHashOk = null;
        if (plan.RequiresBitExactFrames)
        {
            frameHashOk = frameComparison.MismatchCount <= thresholds.MaxBitExactFrameHashMismatches;
            if (frameHashOk != true)
            {
                failures.Add($"{plan.Id}: {frameComparison.MismatchCount} of {frameComparison.ComparedFrames} frames differ, but this pair requires bit-exact frames.");
            }
        }

        // Anti-vacuity guards. A pair whose two sides are frame-identical carries no divergence, so
        // it can neither calibrate a pass band nor falsify one — and a "determinism" pair that
        // differs is itself the finding, not noise to be ignored.
        if (!plan.RequiresBitExactFrames && frameComparison.MismatchCount == 0)
        {
            failures.Add($"{plan.Id}: the candidate is frame-identical to the reference, so this pair carries no divergence at all — it can neither calibrate a pass band nor falsify one, and the fixture edit had no effect.");
        }

        if (plan.RequiresBitExactFrames && frameComparison.MismatchCount != 0)
        {
            failures.Add($"{plan.Id}: the same renderer invoked twice did not reproduce the same frames ({frameComparison.MismatchCount} of {frameComparison.ComparedFrames} differ).");
        }

        // Whether the gate accepted the pair is one thing; whether the pair did its job is another.
        // P3 exists to be REJECTED, so a P3 the gate accepts is a calibration failure, not a pass.
        bool gateAccepted = failures.Count == 0;
        bool metExpectation = plan.Expectation switch
        {
            GoldenParityPairExpectation.MustPassGate => gateAccepted,
            GoldenParityPairExpectation.MustFailGate => !gateAccepted,
            _ => throw new NotSupportedException($"Unknown pair expectation '{plan.Expectation}'.")
        };

        var expectationFailures = new List<string>();
        if (!metExpectation)
        {
            expectationFailures.Add(plan.Expectation == GoldenParityPairExpectation.MustFailGate
                ? $"{plan.Id}: this pair is the NEGATIVE CONTROL and the gate accepted it, so the thresholds are not " +
                  $"falsifiable. Measured: PSNR {Format(psnrMeasurement.AverageDb)} dB (floor {Format(thresholds.MinPsnrDb)}), " +
                  $"audio level diff {Format(audioMeasurement.OverallLevelDiffDb)} dB (ceiling {Format(thresholds.MaxAudioRmsDiffDb)}), " +
                  $"{frameComparison.MismatchCount} of {frameComparison.ComparedFrames} frames differing."
                : $"{plan.Id}: " + string.Join("; ", failures));
        }

        return new GoldenParityPairResult(
            Id: plan.Id,
            Description: plan.Description,
            ReferenceOutputPath: plan.Reference.OutputPath,
            CandidateOutputPath: plan.Candidate.OutputPath,
            ReferenceInvocation: plan.Reference.Invocations,
            CandidateInvocation: plan.Candidate.Invocations,
            ArgvIdentical: argvIdentical,
            RequiresBitExactFrames: plan.RequiresBitExactFrames,
            StreamPropertiesMatch: streamsOk,
            StreamDifferences: streamDifferences,
            ReferenceStreams: referenceStreams,
            CandidateStreams: candidateStreams,
            FrameHashes: frameComparison,
            Psnr: psnrMeasurement,
            AudioRms: audioMeasurement,
            FrameHashGateOk: frameHashOk,
            PsnrGateOk: psnrOk,
            AudioGateOk: audioOk,
            GateAccepted: gateAccepted,
            Expectation: plan.Expectation,
            MetExpectation: metExpectation,
            GateFailures: failures,
            ExpectationFailures: expectationFailures);
    }

    /// <summary>
    /// Writes the measurements artifact. The report quotes this file, so it is written on every run
    /// — including a failing one — and a failure to write it fails the run rather than being
    /// swallowed.
    /// </summary>
    private static async Task WriteMeasurementsAsync(GoldenParityVerdict verdict, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(verdict.MeasurementsPath) ?? ".");

        string json = JsonSerializer.Serialize(
            verdict,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

                // Enums as their names: an artifact whose "expectation" reads 0/1 cannot be audited
                // by the person the artifact exists for.
                Converters = { new JsonStringEnumConverter() },

                // P1's PSNR is +Infinity (bit-identical frames), and System.Text.Json refuses to
                // write a non-finite double by default — the artifact would throw instead of being
                // written, which is the one outcome this artifact must never have.
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
            });

        await File.WriteAllTextAsync(verdict.MeasurementsPath, json + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Summarizes the shape of a seeded document. The harness requires these counts to be non-zero:
    /// a manifest the seeder silently reduces to nothing would leave every ffmpeg call succeeding
    /// and every number in the artifact meaningless.
    /// </summary>
    private static GoldenParityDocumentSummary Summarize(EditTimeline document)
    {
        List<Track> videoTracks = document.Tracks.Where(t => t.Kind == TrackKind.Video).ToList();
        List<Track> audioTracks = document.Tracks.Where(t => t.Kind == TrackKind.Audio).ToList();

        List<MediaClip> videoClips = videoTracks.SelectMany(t => t.Items.OfType<MediaClip>()).ToList();
        List<MediaClip> audioClips = audioTracks.SelectMany(t => t.Items.OfType<MediaClip>()).ToList();

        return new GoldenParityDocumentSummary(
            TrackCount: document.Tracks.Count,
            VideoClipCount: videoClips.Count,
            AudioClipCount: audioClips.Count,
            TotalItemCount: document.Tracks.Sum(t => t.Items.Count),
            TimelineName: document.Name,
            Width: document.Settings.Width,
            Height: document.Settings.Height,
            RateNum: document.Settings.Rate.Num,
            RateDen: document.Settings.Rate.Den,
            RecordOutFrames: videoClips.Count == 0 ? 0 : videoClips.Max(c => c.RecordOut));
    }

    /// <summary>
    /// Fails the run when the seeder produced a document with nothing renderable in it.
    /// </summary>
    private static void RequireNonEmpty(GoldenParityDocumentSummary summary, string what)
    {
        if (summary.VideoClipCount < 2 || summary.AudioClipCount < 1)
        {
            throw new InvalidOperationException(
                $"The golden-parity fixture is vacuous: {what} has {summary.VideoClipCount} video clip(s) and " +
                $"{summary.AudioClipCount} audio clip(s) across {summary.TrackCount} track(s), but the harness needs " +
                "at least two video cuts and one audio bed. A manifest the seeder reduces to nothing would make " +
                "every measured number meaningless while every ffmpeg call still succeeded.");
        }
    }

    /// <summary>
    /// Structural equality of two invocation lists, argument by argument, with each side's own
    /// output path normalized away.
    /// </summary>
    /// <remarks>
    /// The output path is the one argument that <i>must</i> differ between two renders written to
    /// different files, so comparing it would make every "same render" claim false on a technicality
    /// while saying nothing about the render. Normalizing it is what lets P1 assert the stronger,
    /// meaningful property: the two invocations are the same recipe.
    /// </remarks>
    private static bool InvocationsEqual(
        IReadOnlyList<IReadOnlyList<string>> left,
        IReadOnlyList<IReadOnlyList<string>> right,
        string leftOutputPath,
        string rightOutputPath)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (!Normalize(left[i], leftOutputPath).SequenceEqual(
                    Normalize(right[i], rightOutputPath),
                    StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One invocation with its own output path replaced by a stable placeholder.</summary>
    private static IEnumerable<string> Normalize(IReadOnlyList<string> args, string outputPath)
    {
        foreach (string arg in args)
        {
            yield return string.Equals(arg, outputPath, StringComparison.Ordinal) ? OutputPathArgument : arg;
        }
    }

    /// <summary>The placeholder an invocation's own output path is normalized to.</summary>
    private const string OutputPathArgument = "<output>";

    private static string Format(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A stable id for the seeded document, so a re-seed is byte-identical.</summary>
    private static Guid FixtureTimelineId { get; } = new("1b2c3d4e-5f60-4711-8a92-3c4d5e6f7001");

    /// <summary>One calibration pair: what to render, which gates apply, and what it must prove.</summary>
    private sealed record PairPlan(
        string Id,
        string Description,
        TimelineRenderProduct Reference,
        TimelineRenderProduct Candidate,
        bool RequiresBitExactFrames,
        bool RequiresIdenticalArgv,
        GoldenParityPairExpectation Expectation);
}
