#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The stream properties a parity comparison is only meaningful over.
/// </summary>
/// <param name="Width">Decoded video width in pixels.</param>
/// <param name="Height">Decoded video height in pixels.</param>
/// <param name="FpsNum">Frame-rate numerator, exactly as ffprobe reports it.</param>
/// <param name="FpsDen">Frame-rate denominator, exactly as ffprobe reports it.</param>
/// <param name="FrameCount">Decoded video frame count, counted from the decoded stream itself.</param>
/// <param name="DurationSec">Container duration ffprobe reports.</param>
/// <param name="AudioSampleRate">Sample rate of the decoded audio stream's WAV extraction.</param>
/// <param name="AudioChannels">Channel count of the decoded audio stream's WAV extraction.</param>
public sealed record GoldenParityStreamProperties(
    int Width,
    int Height,
    int FpsNum,
    int FpsDen,
    int FrameCount,
    double DurationSec,
    int AudioSampleRate,
    int AudioChannels)
{
    /// <summary>The frame period derived from the exact rational rate, or 0 when the rate is unknown.</summary>
    public double FramePeriodSeconds => FpsNum > 0 && FpsDen > 0 ? FpsDen / (double)FpsNum : 0.0;

    /// <summary>
    /// Compares two streams, returning every property that does not match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This runs before any PSNR/audio comparison, and gates it.</b> ffmpeg's <c>psnr</c> filter
    /// compares whatever it is handed: two streams of different length or resolution yield a
    /// plausible-looking average rather than an error, and ffmpeg's own documentation notes the
    /// filter is only defined over identically-sized inputs. A parity number produced across
    /// misaligned streams is worse than no number, because it reads like evidence.
    /// </para>
    /// <para>
    /// Frame count, width, height, rate and the audio geometry must be <b>exactly</b> equal. Only
    /// the container duration carries a tolerance — one frame plus a millisecond — because it is a
    /// muxed container field (audio priming and padding land in it) rather than a property of the
    /// edit.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> DifferencesFrom(GoldenParityStreamProperties other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var differences = new List<string>();

        Compare(nameof(Width), Width, other.Width, differences);
        Compare(nameof(Height), Height, other.Height, differences);
        Compare(nameof(FpsNum), FpsNum, other.FpsNum, differences);
        Compare(nameof(FpsDen), FpsDen, other.FpsDen, differences);
        Compare(nameof(FrameCount), FrameCount, other.FrameCount, differences);
        Compare(nameof(AudioSampleRate), AudioSampleRate, other.AudioSampleRate, differences);
        Compare(nameof(AudioChannels), AudioChannels, other.AudioChannels, differences);

        double durationTolerance = FramePeriodSeconds + 0.001;
        if (Math.Abs(DurationSec - other.DurationSec) > durationTolerance)
        {
            differences.Add(
                $"DurationSec: {DurationSec.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} vs " +
                $"{other.DurationSec.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"(tolerance {durationTolerance.ToString("R", System.Globalization.CultureInfo.InvariantCulture)})");
        }

        return differences;
    }

    private static void Compare(string name, int reference, int candidate, List<string> differences)
    {
        if (reference != candidate)
        {
            differences.Add($"{name}: {reference} vs {candidate}");
        }
    }
}

/// <summary>
/// Reads <see cref="GoldenParityStreamProperties"/> off a produced file: video geometry and rate
/// through the production <see cref="FfprobeOutputParser"/>, frame count by counting decoded frames,
/// and audio geometry from the extracted WAV's own header.
/// </summary>
public sealed class StreamPropertyComparer
{
    private readonly IVideoToolRunner _runner;
    private readonly TimeSpan _timeout;

    public StreamPropertyComparer(IVideoToolRunner runner, TimeSpan timeout)
    {
        _runner = runner;
        _timeout = timeout;
    }

    /// <summary>
    /// Probes a produced file's container/stream facts through the production probe argv and parser.
    /// </summary>
    /// <remarks>
    /// Called before the frame-hash pass, because the raw-RGB frame size — and therefore the decoded
    /// frame count — follows from the probed width and height.
    /// </remarks>
    public async Task<MediaProbeResult> ProbeAsync(string mediaPath, CancellationToken cancellationToken = default)
    {
        string[] probeArgs = FfmpegArgvBuilder.BuildProbeArgs(mediaPath);
        VideoToolResult probe = await _runner.RunFfprobeAsync(probeArgs, _timeout, cancellationToken).ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            throw new InvalidOperationException(
                $"ffprobe failed for '{mediaPath}' (exitCode={probe.ExitCode}, timedOut={probe.TimedOut}). " +
                $"stderr: {probe.StdErr.Trim()}");
        }

        return FfprobeOutputParser.Parse(probe.StdOut);
    }

    /// <summary>
    /// Probes <paramref name="videoPath"/> and returns the properties a parity comparison needs.
    /// </summary>
    /// <param name="videoPath">The produced media file.</param>
    /// <param name="frameCount">Decoded frame count, already counted by the frame-hash pass.</param>
    /// <param name="audioProbePath">
    /// A WAV file previously extracted from <paramref name="videoPath"/> by
    /// <see cref="AudioRmsComparer"/>, whose header supplies the audio geometry. Pass <c>null</c>
    /// when the output has no audio.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<GoldenParityStreamProperties> ReadAsync(
        string videoPath,
        int frameCount,
        string? audioProbePath,
        CancellationToken cancellationToken = default)
    {
        MediaProbeResult media = await ProbeAsync(videoPath, cancellationToken).ConfigureAwait(false);

        int audioSampleRate = 0;
        int audioChannels = 0;
        if (audioProbePath is not null && File.Exists(audioProbePath))
        {
            (audioSampleRate, audioChannels) = WavGeometry.Read(await File.ReadAllBytesAsync(audioProbePath, cancellationToken).ConfigureAwait(false));
        }

        return new GoldenParityStreamProperties(
            Width: media.Width,
            Height: media.Height,
            FpsNum: media.FpsNum,
            FpsDen: media.FpsDen,
            FrameCount: frameCount,
            DurationSec: media.DurationSec,
            AudioSampleRate: audioSampleRate,
            AudioChannels: audioChannels);
    }
}

/// <summary>
/// The audio geometry of a canonical RIFF/WAVE byte array.
/// </summary>
/// <remarks>
/// <b>Why this exists next to <see cref="WavRmsSampler"/>.</b> The RMS windows themselves are the
/// sampler's job and are reused verbatim from it — this is not a second RMS implementation. The
/// sampler deliberately exposes only per-window levels, not the stream geometry, and the production
/// probe result (<see cref="MediaProbeResult"/>) carries no channel count, so the header fields the
/// stream-comparison needs are read here, with the same minimal RIFF chunk walk.
/// </remarks>
internal static class WavGeometry
{
    /// <summary>Returns <c>(sampleRate, channels)</c>, or <c>(0, 0)</c> for an unparseable header.</summary>
    public static (int SampleRate, int Channels) Read(byte[] wavBytes)
    {
        ArgumentNullException.ThrowIfNull(wavBytes);

        if (wavBytes.Length < 12 ||
            wavBytes[0] != 'R' || wavBytes[1] != 'I' || wavBytes[2] != 'F' || wavBytes[3] != 'F' ||
            wavBytes[8] != 'W' || wavBytes[9] != 'A' || wavBytes[10] != 'V' || wavBytes[11] != 'E')
        {
            return (0, 0);
        }

        int position = 12;
        while (position + 8 <= wavBytes.Length)
        {
            string chunkId = new([(char)wavBytes[position], (char)wavBytes[position + 1], (char)wavBytes[position + 2], (char)wavBytes[position + 3]]);
            uint chunkSize = (uint)(wavBytes[position + 4] | (wavBytes[position + 5] << 8) | (wavBytes[position + 6] << 16) | (wavBytes[position + 7] << 24));
            int chunkDataStart = position + 8;

            if (chunkId == "fmt " && chunkDataStart + 16 <= wavBytes.Length)
            {
                int channels = wavBytes[chunkDataStart + 2] | (wavBytes[chunkDataStart + 3] << 8);
                int sampleRate = wavBytes[chunkDataStart + 4] | (wavBytes[chunkDataStart + 5] << 8) |
                                 (wavBytes[chunkDataStart + 6] << 16) | (wavBytes[chunkDataStart + 7] << 24);
                return (sampleRate, channels);
            }

            long advance = chunkSize + (chunkSize % 2 == 1 ? 1 : 0);
            long next = chunkDataStart + advance;
            if (next <= position || next > wavBytes.Length)
            {
                break;
            }

            position = (int)next;
        }

        return (0, 0);
    }
}
