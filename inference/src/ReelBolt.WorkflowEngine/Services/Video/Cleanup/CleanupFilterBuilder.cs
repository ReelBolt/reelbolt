using System.Globalization;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video.Cleanup;

/// <summary>What the audio itself told us. Only ever filled by first-party measurement.</summary>
public sealed record CleanupMeasurements(double? MainsFrequencyHz, double? NoiseFloorDb)
{
    public static CleanupMeasurements None { get; } = new(null, null);
}

/// <summary>The builder's answer: an ordered filter list, or "nothing to do" with a reason.</summary>
public sealed record CleanupFilterResult(IReadOnlyList<string> Filters, string? Reason)
{
    public bool HasFilter => Filters.Count > 0;

    public string FilterString => string.Join(",", Filters);

    public static CleanupFilterResult NoFilter(string reason) => new([], reason);
}

/// <summary>
/// Pure, I/O-free mapping from a closed <see cref="LocalCleanupTarget"/> to an ffmpeg <c>-af</c>
/// chain. Every number is a constant in this file or a first-party measurement; nothing comes from
/// a request or a model.
/// </summary>
public static class CleanupFilterBuilder
{
    public const string HumNotDetected = "hum_not_detected";

    // Hum: notch the mains fundamental and harmonics up to the 5th. Q 30 keeps each notch about
    // 1.7 Hz wide at 50 Hz, so speech fundamentals (>= 85 Hz) are hardly touched.
    public const int HumHarmonics = 5;
    public const int HumNotchQ = 30;

    // Wind: 2-pole high-pass at 90 Hz plus a gentle low-shelf cut below 200 Hz.
    public const int WindHighPassHz = 90;
    public const int WindShelfHz = 200;
    public const int WindShelfGainDb = -4;

    // Hiss: a single afftdn chain. afftdn is deterministic, fast and has no look-ahead state that
    // varies with block size; anlmdn is ~10x slower and its result depends on search params, so it
    // is left out to keep one predictable chain. nr is the reduction in dB (table), nf the noise
    // floor (measured when available, clamped to the table range, else the default).
    public const int HissReductionDb = 12;
    public const int HissDefaultNoiseFloorDb = -50;
    public const int HissMinNoiseFloorDb = -80;
    public const int HissMaxNoiseFloorDb = -20;

    // Echo: a gentle downward expander that lowers the room tail between words, plus a mild
    // high-pass. This REDUCES room tail; it cannot dereverberate.
    public const int EchoHighPassHz = 100;

    // VoiceBasic: RNNoise then a conservative high-pass.
    public const int VoiceHighPassHz = 80;

    /// <summary>
    /// <paramref name="rnnoiseModelPath"/> is only read for <see cref="LocalCleanupTarget.VoiceBasic"/>
    /// and comes from <see cref="LocalCleanupOptions"/>; null selects the model-free fallback chain.
    /// </summary>
    public static CleanupFilterResult Build(
        LocalCleanupTarget target, CleanupMeasurements measurements, string? rnnoiseModelPath = null)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        return target switch
        {
            LocalCleanupTarget.Hum => BuildHum(measurements),
            LocalCleanupTarget.Wind => new CleanupFilterResult(
                [
                    $"highpass=f={WindHighPassHz}:poles=2",
                    $"lowshelf=f={WindShelfHz}:g={WindShelfGainDb}"
                ],
                null),
            LocalCleanupTarget.Hiss => BuildHiss(measurements),
            LocalCleanupTarget.Echo => new CleanupFilterResult(
                [
                    $"highpass=f={EchoHighPassHz}:poles=2",
                    "agate=threshold=0.03:ratio=2:attack=5:release=200:range=0.25"
                ],
                null),
            LocalCleanupTarget.VoiceBasic => BuildVoice(rnnoiseModelPath),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown local cleanup target.")
        };
    }

    private static CleanupFilterResult BuildHum(CleanupMeasurements m)
    {
        if (m.MainsFrequencyHz is not (50 or 60))
        {
            return CleanupFilterResult.NoFilter(HumNotDetected);
        }

        int mains = (int)m.MainsFrequencyHz.Value;
        var filters = new List<string>(HumHarmonics);
        for (int k = 1; k <= HumHarmonics; k++)
        {
            filters.Add($"bandreject=f={mains * k}:width_type=q:w={HumNotchQ}");
        }

        return new CleanupFilterResult(filters, null);
    }

    private static CleanupFilterResult BuildHiss(CleanupMeasurements m)
    {
        int nf = m.NoiseFloorDb is { } measured && double.IsFinite(measured)
            ? (int)Math.Clamp(Math.Round(measured), HissMinNoiseFloorDb, HissMaxNoiseFloorDb)
            : HissDefaultNoiseFloorDb;
        return new CleanupFilterResult(
            [$"afftdn=nr={HissReductionDb}:nf={nf.ToString(CultureInfo.InvariantCulture)}:nt=w"], null);
    }

    private static CleanupFilterResult BuildVoice(string? modelPath)
    {
        string highPass = $"highpass=f={VoiceHighPassHz}:poles=2";
        if (modelPath is null)
        {
            // Model unavailable: degrade to a classic chain (never silent: the caller reports it).
            return new CleanupFilterResult(
                [$"afftdn=nr={HissReductionDb}:nf={HissDefaultNoiseFloorDb}:nt=w", "anlmdn=s=0.0001:p=0.002:r=0.006", highPass],
                null);
        }

        return new CleanupFilterResult([$"arnndn=m={EscapeFilterPath(modelPath)}", highPass], null);
    }

    /// <summary>
    /// The model path is operator configuration, but it still lands inside a filter string, so it
    /// is restricted to a conservative character set rather than escaped.
    /// </summary>
    internal static string EscapeFilterPath(string path)
    {
        foreach (char c in path)
        {
            bool safe = char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-';
            if (!safe)
            {
                throw new ArgumentException(
                    "The RNNoise model path may only contain letters, digits, '/', '.', '_' and '-'.", nameof(path));
            }
        }

        return path;
    }
}
