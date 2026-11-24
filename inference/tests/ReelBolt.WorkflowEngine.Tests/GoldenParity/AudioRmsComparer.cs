#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>One output's audio, extracted to 16-bit PCM WAV and windowed into RMS levels.</summary>
/// <param name="WavPath">Where the extraction was written.</param>
/// <param name="SampleRate">Sample rate read from the extracted WAV's own header.</param>
/// <param name="Channels">Channel count read from the extracted WAV's own header.</param>
/// <param name="SampleFrames">Sample frames in the extracted data chunk.</param>
/// <param name="Windows">Per-window RMS/peak levels from <see cref="WavRmsSampler"/>.</param>
/// <param name="Argv">The ffmpeg argv the extraction used.</param>
public sealed record GoldenParityAudioExtraction(
    string WavPath,
    int SampleRate,
    int Channels,
    int SampleFrames,
    IReadOnlyList<WavRmsSampler.RmsWindow> Windows,
    IReadOnlyList<string> Argv)
{
    /// <summary>
    /// The whole-file RMS level in dBFS, as the energy average of the window levels.
    /// </summary>
    /// <remarks>
    /// An energy average rather than a mean of decibels: the mean of dB values is not a level, and
    /// averaging it would let a silent half and a loud half average to something inaudible.
    /// </remarks>
    /// <exception cref="InvalidOperationException">There are no windows.</exception>
    public double OverallLevelDbfs =>
        Windows.Count == 0
            ? throw new InvalidOperationException(
                "An extraction with no RMS windows has no level. ExtractAsync rejects an unwindowable WAV " +
                "precisely so this can never be silently treated as silence.")
            : 10.0 * Math.Log10(Windows.Average(w => Math.Pow(10.0, w.RmsDbfs / 10.0)));
}

/// <summary>The audio comparison between two outputs.</summary>
/// <param name="ReferenceLevelDbfs">Whole-file RMS level of the reference output.</param>
/// <param name="CandidateLevelDbfs">Whole-file RMS level of the candidate output.</param>
/// <param name="OverallLevelDiffDb">Absolute whole-file level difference — the gated quantity.</param>
/// <param name="MaxWindowDiffDb">Largest per-window level difference (same window index on both sides).</param>
/// <param name="ComparedWindows">Full windows actually compared (over the shorter of the two; a trailing partial window is not compared).</param>
/// <param name="ReferenceSampleFrames">Sample frames in the reference extraction.</param>
/// <param name="CandidateSampleFrames">Sample frames in the candidate extraction.</param>
/// <param name="ReferenceSampleRate">Sample rate of the reference extraction.</param>
/// <param name="CandidateSampleRate">Sample rate of the candidate extraction.</param>
/// <param name="Argv">Every ffmpeg argv this comparison used, in order.</param>
public sealed record GoldenParityAudioRmsMeasurement(
    double ReferenceLevelDbfs,
    double CandidateLevelDbfs,
    double OverallLevelDiffDb,
    double MaxWindowDiffDb,
    int ComparedWindows,
    int ReferenceSampleFrames,
    int CandidateSampleFrames,
    int ReferenceSampleRate,
    int CandidateSampleRate,
    IReadOnlyList<IReadOnlyList<string>> Argv);

/// <summary>
/// Compares the audio of two outputs by windowing each into RMS levels and differencing them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built on <see cref="WavRmsSampler"/>, deliberately.</b> Windowed RMS/peak over 16-bit PCM WAV
/// is already implemented and tested in production; this comparer only extracts, calls the sampler,
/// and differences the results. A second RMS implementation here would give the gate a different
/// definition of loudness than the pipeline it is checking.
/// </para>
/// <para>
/// <b>This is a level gate, and only a level gate.</b> RMS is invariant to a time shift of
/// stationary content, so it catches a wrong or missing level, a dropped track or a mis-set bed
/// gain — the mix errors this leg exists for — and it does <b>not</b> catch a one-frame timing
/// shift. The harness measures this explicitly: its negative control (a one-frame edit shift) leaves
/// the audio leg at exactly 0.0 dB while the video leg collapses. Timing sensitivity would need a
/// correlation or alignment metric; see the harness's reported risks.
/// </para>
/// </remarks>
public sealed class AudioRmsComparer
{
    /// <summary>Window length for the RMS comparison, in milliseconds.</summary>
    /// <remarks>
    /// 250 ms is <see cref="WavRmsSampler"/>'s own default and the granularity the pipeline's
    /// waveform/peaks work already uses, so the gate and the product agree on what a "window" is.
    /// </remarks>
    public const int WindowMs = 250;

    private readonly IVideoToolRunner _runner;
    private readonly TimeSpan _timeout;

    public AudioRmsComparer(IVideoToolRunner runner, TimeSpan timeout)
    {
        _runner = runner;
        _timeout = timeout;
    }

    /// <summary>
    /// Extracts <paramref name="videoPath"/>'s audio to a WAV and windows it into RMS levels.
    /// </summary>
    /// <remarks>
    /// The extraction keeps the source's own rate and channel count (no <c>-ar</c>/<c>-ac</c>
    /// coercion), so what follows measures the stream that was actually produced rather than a
    /// normalized copy of it.
    /// </remarks>
    public async Task<GoldenParityAudioExtraction> ExtractAsync(
        string videoPath,
        string wavPath,
        CancellationToken cancellationToken = default)
    {
        string[] args =
        [
            "-y", "-v", "error", "-nostdin",
            "-i", videoPath,
            "-vn",
            "-c:a", "pcm_s16le",
            "-f", "wav",
            wavPath
        ];

        VideoToolResult result = await _runner.RunFfmpegAsync(args, _timeout, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Audio extraction failed for '{videoPath}' (exitCode={result.ExitCode}, timedOut={result.TimedOut}). " +
                $"stderr: {result.StdErr.Trim()}");
        }

        byte[] wavBytes = await File.ReadAllBytesAsync(wavPath, cancellationToken).ConfigureAwait(false);
        (int sampleRate, int channels) = WavGeometry.Read(wavBytes);
        IReadOnlyList<WavRmsSampler.RmsWindow> windows = WavRmsSampler.Sample(wavBytes, WindowMs);

        if (windows.Count == 0)
        {
            throw new InvalidOperationException(
                $"'{videoPath}' produced a WAV that {nameof(WavRmsSampler)} could not window " +
                $"(sampleRate={sampleRate}, channels={channels}, bytes={wavBytes.Length}). The audio leg cannot be " +
                "measured, and an unmeasurable audio leg must not read as a pass.");
        }

        // EndSec is frameEnd / sampleRate, so the last window's end is the exact sample-frame count.
        int sampleFrames = (int)Math.Round(windows[^1].EndSec * sampleRate);

        return new GoldenParityAudioExtraction(
            WavPath: wavPath,
            SampleRate: sampleRate,
            Channels: channels,
            SampleFrames: sampleFrames,
            Windows: windows,
            Argv: args);
    }

    /// <summary>
    /// Extracts and compares both outputs' audio. The WAVs are written into
    /// <paramref name="workingDirectory"/>.
    /// </summary>
    public async Task<GoldenParityAudioRmsMeasurement> CompareAsync(
        string referencePath,
        string candidatePath,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(workingDirectory);
        string referenceWav = Path.Combine(workingDirectory, Path.GetFileNameWithoutExtension(referencePath) + ".audio.wav");
        string candidateWav = Path.Combine(workingDirectory, Path.GetFileNameWithoutExtension(candidatePath) + ".audio.wav");

        GoldenParityAudioExtraction reference = await ExtractAsync(referencePath, referenceWav, cancellationToken).ConfigureAwait(false);
        GoldenParityAudioExtraction candidate = await ExtractAsync(candidatePath, candidateWav, cancellationToken).ConfigureAwait(false);

        return Difference(reference, candidate);
    }

    private static bool IsFullWindow(WavRmsSampler.RmsWindow window) =>
        window.EndSec - window.StartSec >= WindowMs / 1000.0 - 1e-6;

    /// <summary>
    /// Differences two extractions. Shared with the focused comparer tests, which supply extractions
    /// directly rather than re-rendering to obtain them.
    /// </summary>
    public static GoldenParityAudioRmsMeasurement Difference(
        GoldenParityAudioExtraction reference,
        GoldenParityAudioExtraction candidate)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);

        // Only FULL windows are compared. The remainder after the last whole window is a few
        // milliseconds of the encoder's tail padding (256 samples on the P2 renders); an RMS over that
        // little near-silence swings by ~10 dB between AAC builds and says nothing about the mix.
        int compared = 0;
        double maxWindowDiff = 0.0;
        for (int i = 0; i < Math.Min(reference.Windows.Count, candidate.Windows.Count); i++)
        {
            if (!IsFullWindow(reference.Windows[i]) || !IsFullWindow(candidate.Windows[i]))
                continue;

            compared++;
            maxWindowDiff = Math.Max(
                maxWindowDiff,
                Math.Abs(reference.Windows[i].RmsDbfs - candidate.Windows[i].RmsDbfs));
        }

        double referenceLevel = reference.OverallLevelDbfs;
        double candidateLevel = candidate.OverallLevelDbfs;

        return new GoldenParityAudioRmsMeasurement(
            ReferenceLevelDbfs: referenceLevel,
            CandidateLevelDbfs: candidateLevel,
            OverallLevelDiffDb: Math.Abs(referenceLevel - candidateLevel),
            MaxWindowDiffDb: maxWindowDiff,
            ComparedWindows: compared,
            ReferenceSampleFrames: reference.SampleFrames,
            CandidateSampleFrames: candidate.SampleFrames,
            ReferenceSampleRate: reference.SampleRate,
            CandidateSampleRate: candidate.SampleRate,
            Argv: [reference.Argv, candidate.Argv]);
    }
}
