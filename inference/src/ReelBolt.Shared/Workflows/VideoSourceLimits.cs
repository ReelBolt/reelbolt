using Microsoft.Extensions.Configuration;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// What the platform can edit from one source video, read by the run gate (Inference API) and the
/// engine alike, so a run is refused before it spends anything and the engine agrees with the gate.
/// Configuration: <c>VideoSources:MaxBytes</c> and <c>VideoSources:MaxSeconds</c> (both services).
/// </summary>
/// <remarks>
/// A step's stored <c>MaxInputBytes</c>/<c>MaxDurationSeconds</c> can only lower these. The old form and
/// full serializations wrote the old defaults (2 GB, 30 minutes) into saved workflows and run snapshots,
/// so exactly those two values read as "not set": nobody chose them, and honouring them would keep
/// refusing ordinary phone footage on every workflow saved before this change.
/// </remarks>
public sealed record VideoSourceLimits(long MaxBytes, int MaxSeconds)
{
    public const string SectionName = "VideoSources";

    /// <summary>The value older configs carry for <c>MaxInputBytes</c> without anyone having chosen it.</summary>
    public const long LegacyDefaultMaxInputBytes = 2_000_000_000;

    /// <summary>The value older configs carry for <c>MaxDurationSeconds</c> without anyone having chosen it.</summary>
    public const int LegacyDefaultMaxDurationSeconds = 1800;

    public const long DefaultMaxBytes = 30L * 1024 * 1024 * 1024;
    public const int DefaultMaxSeconds = 3600;

    public const string TooLargeCode = "SOURCE_TOO_LARGE";
    public const string TooLongCode = "SOURCE_TOO_LONG";
    public const string TooDemandingCode = "SOURCE_TOO_DEMANDING";

    public static VideoSourceLimits Default { get; } = new(DefaultMaxBytes, DefaultMaxSeconds);

    public static VideoSourceLimits From(IConfiguration configuration)
    {
        long bytes = configuration.GetValue<long?>($"{SectionName}:MaxBytes") ?? DefaultMaxBytes;
        int seconds = configuration.GetValue<int?>($"{SectionName}:MaxSeconds") ?? DefaultMaxSeconds;
        return new VideoSourceLimits(bytes > 0 ? bytes : DefaultMaxBytes, seconds > 0 ? seconds : DefaultMaxSeconds);
    }

    /// <summary>The byte limit for a step: its own value only when an author chose a lower one.</summary>
    public long EffectiveBytes(long stepMaxInputBytes) =>
        stepMaxInputBytes <= 0 || stepMaxInputBytes == LegacyDefaultMaxInputBytes
            ? MaxBytes
            : Math.Min(stepMaxInputBytes, MaxBytes);

    /// <summary>The length limit for a step: its own value only when an author chose a lower one.</summary>
    public int EffectiveSeconds(int stepMaxDurationSeconds) =>
        stepMaxDurationSeconds <= 0 || stepMaxDurationSeconds == LegacyDefaultMaxDurationSeconds
            ? MaxSeconds
            : Math.Min(stepMaxDurationSeconds, MaxSeconds);

    /// <summary>Whole gigabytes for a sentence, never a byte count.</summary>
    public static string FormatGigabytes(long bytes) =>
        Math.Max(1, (long)Math.Ceiling(bytes / (1024d * 1024 * 1024))).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Whole minutes for a sentence, never a setting name.</summary>
    public static string FormatMinutes(int seconds) =>
        Math.Max(1, (int)Math.Ceiling(seconds / 60d)).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
