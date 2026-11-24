using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Services.Inference;

/// <summary>
/// Fills <see cref="AudioGenerationResult.DurationSec"/> from the file itself when the vendor stated none — which
/// is every audio endpoint in <see cref="AudioModelCatalog"/> today (research/audio-vendors.md: "No duration field
/// on any audio output"). The bytes are written to a private temporary file, probed with ffprobe through
/// <see cref="IMediaProbe"/>, and the file is removed. An unreadable or non-positive duration FAILS rather than
/// metering zero.
/// </summary>
public sealed class AudioGenerationDurationProbe(IMediaProbe probe)
{
    private readonly IMediaProbe _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    /// <summary>The result with a positive duration: the vendor's, or the probed one.</summary>
    /// <exception cref="AudioGenerationException">The file's duration could not be read (<see cref="AudioGenerationErrorKind.VendorFailed"/>).</exception>
    public async Task<AudioGenerationResult> EnsureDurationAsync(AudioGenerationResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.DurationSec is > 0 and var stated && double.IsFinite(stated)) return result;

        string directory = Path.Combine(Path.GetTempPath(), "reelbolt-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "generated" + SafeExtension(result.Extension));
        try
        {
            await File.WriteAllBytesAsync(path, result.Bytes, ct);
            MediaProbeResult probed;
            try
            {
                probed = await _probe.ProbeAsync(path, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw new AudioGenerationException(AudioGenerationErrorKind.VendorFailed,
                    "The generated audio could not be probed for its duration: " + ex.Message, inner: ex);
            }

            if (!(probed.DurationSec > 0) || !double.IsFinite(probed.DurationSec))
            {
                throw new AudioGenerationException(AudioGenerationErrorKind.VendorFailed,
                    "The generated audio has no readable duration (ffprobe reported " + probed.DurationSec + "), so it " +
                    "cannot be metered.");
            }

            return result with { DurationSec = probed.DurationSec };
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // best effort: the temp directory is private and uniquely named
            }
            catch (UnauthorizedAccessException)
            {
                // as above
            }
        }
    }

    /// <summary>The vendor-derived extension, reduced to a safe file-name suffix.</summary>
    private static string SafeExtension(string? extension) =>
        extension is { Length: > 1 and <= 8 } e && e[0] == '.' && e[1..].All(char.IsAsciiLetterOrDigit) ? e : ".bin";
}
