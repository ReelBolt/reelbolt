using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video.Cleanup;

public sealed record LocalCleanupResult(
    bool Applied,
    string? Reason,
    double DurationSec,
    int SampleRate,
    int Channels,
    CleanupMeasurements Measurements);

public interface ILocalAudioCleaner
{
    Task<LocalCleanupResult> CleanAsync(
        string sourcePath, string outputPath, LocalCleanupTarget target, CancellationToken ct);
}

/// <summary>
/// Free, offline audio cleanup: everything runs through the local ffmpeg via
/// <see cref="IVideoToolRunner"/>; customer audio goes nowhere else.
/// </summary>
public sealed class LocalAudioCleaner : ILocalAudioCleaner
{
    /// <summary>Output duration must match the source within this many seconds.</summary>
    public const double MaxDurationDeltaSec = 0.050;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromMinutes(15);

    private readonly IVideoToolRunner _runner;
    private readonly LocalCleanupOptions _options;
    private readonly MainsFrequencyProbe _mainsProbe;
    private bool _modelVerified;

    public LocalAudioCleaner(IVideoToolRunner runner, IOptions<LocalCleanupOptions> options)
    {
        _runner = runner;
        _options = options.Value;
        _mainsProbe = new MainsFrequencyProbe(runner);
    }

    public async Task<LocalCleanupResult> CleanAsync(
        string sourcePath, string outputPath, LocalCleanupTarget target, CancellationToken ct)
    {
        (double duration, int sampleRate, int channels) = await ProbeAsync(sourcePath, ct).ConfigureAwait(false);

        var measurements = CleanupMeasurements.None;
        if (target == LocalCleanupTarget.Hum)
        {
            string pcm = outputPath + ".mains.pcm";
            MainsFrequencyResult mains = await _mainsProbe.ProbeAsync(sourcePath, pcm, ct).ConfigureAwait(false);
            measurements = new CleanupMeasurements(mains.Hz, null);
        }

        string? modelPath = null;
        if (target == LocalCleanupTarget.VoiceBasic)
        {
            modelPath = _options.RnnoiseModelPath;
            await VerifyModelAsync(modelPath, ct).ConfigureAwait(false);
        }

        CleanupFilterResult filter = CleanupFilterBuilder.Build(target, measurements, modelPath);
        if (!filter.HasFilter)
        {
            return new LocalCleanupResult(false, filter.Reason, duration, sampleRate, channels, measurements);
        }

        string[] args = BuildArgs(sourcePath, outputPath, filter, duration, sampleRate, channels);
        VideoToolResult run = await _runner.RunFfmpegAsync(args, FfmpegTimeout, ct).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            throw new CleanupException(CleanupErrorCodes.FfmpegFailed, "ffmpeg audio cleanup failed.");
        }

        (double outDuration, _, _) = await ProbeAsync(outputPath, ct, trustedWav: true).ConfigureAwait(false);
        if (Math.Abs(outDuration - duration) > MaxDurationDeltaSec)
        {
            throw new CleanupException(
                CleanupErrorCodes.DurationMismatch,
                $"Cleaned audio duration {outDuration:F3}s differs from source {duration:F3}s by more than 50 ms.");
        }

        return new LocalCleanupResult(true, null, outDuration, sampleRate, channels, measurements);
    }

    /// <summary>Argv builder, exposed for byte-pinned tests. No second <c>-i</c>, video dropped.</summary>
    public static string[] BuildArgs(
        string sourcePath,
        string outputPath,
        CleanupFilterResult filter,
        double durationSec,
        int sampleRate,
        int channels)
    {
        string d = durationSec.ToString("F6", CultureInfo.InvariantCulture);
        // apad + atrim pin the output length to the source length (filters like arnndn can
        // shift or shorten the tail by a few frames).
        string af = $"{filter.FilterString},apad=whole_dur={d},atrim=end={d}";
        return
        [
            "-hide_banner", "-nostdin", "-y", "-v", "error",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-i", sourcePath,
            "-vn",
            "-af", af,
            "-ar", sampleRate.ToString(CultureInfo.InvariantCulture),
            "-ac", channels.ToString(CultureInfo.InvariantCulture),
            "-c:a", "pcm_s16le",
            "-f", "wav",
            outputPath
        ];
    }

    private async Task VerifyModelAsync(string path, CancellationToken ct)
    {
        if (_modelVerified)
        {
            return;
        }

        if (!File.Exists(path))
        {
            throw new CleanupException(CleanupErrorCodes.ModelMissing, "The RNNoise model file is missing.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (hash != LocalCleanupOptions.RnnoiseModelSha256)
        {
            throw new CleanupException(
                CleanupErrorCodes.ModelChecksumMismatch, "The RNNoise model file does not match its pinned SHA-256.");
        }

        _modelVerified = true;
    }

    private async Task<(double Duration, int SampleRate, int Channels)> ProbeAsync(
        string path, CancellationToken ct, bool trustedWav = false)
    {
        string[] args =
        [
            "-v", "error", "-select_streams", "a:0",
            "-show_entries", "stream=sample_rate,channels:format=duration",
            "-of", "json",
            "-protocol_whitelist", "file",
            "-format_whitelist", trustedWav ? "wav" : MediaDerivativeInputFormats.Whitelist,
            path
        ];
        VideoToolResult r = await _runner.RunFfprobeAsync(args, ProbeTimeout, ct).ConfigureAwait(false);
        if (!r.Succeeded)
        {
            throw new CleanupException(CleanupErrorCodes.ProbeFailed, "ffprobe failed on the audio file.");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(r.StdOut);
            JsonElement root = doc.RootElement;
            JsonElement stream = root.GetProperty("streams")[0];
            int sr = int.Parse(stream.GetProperty("sample_rate").GetString()!, CultureInfo.InvariantCulture);
            int ch = stream.GetProperty("channels").GetInt32();
            double dur = double.Parse(
                root.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
            return (dur, sr, ch);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new CleanupException(CleanupErrorCodes.ProbeFailed, "ffprobe output had no usable audio stream.");
        }
    }
}
