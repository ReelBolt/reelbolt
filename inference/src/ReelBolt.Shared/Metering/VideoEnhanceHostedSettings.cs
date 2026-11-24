using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// The ONE reading of the video-enhancement settings the hold, the estimate, the route's caps and the executor all share, so a re-tune of
/// <c>VideoEnhance:Hosted:ComputeSecondsPerOutputFrame</c> or of a source cap moves every one of them together and the price the
/// customer is shown is the price the engine holds. A missing, unparsable or non-positive value falls back to the default.
/// </summary>
public sealed record VideoEnhanceHostedSettings(
    int HostedMaxSourceSeconds,
    int LocalMaxSourceSeconds,
    decimal ComputeSecondsPerOutputFrame)
{
    /// <summary>The configuration key of the local cap (the section <c>LocalEnhanceOptions</c> binds).</summary>
    public const string LocalMaxSourceSecondsKey = "VideoEnhance:Local:MaxSourceSeconds";

    /// <summary>The configuration key of the compute-second rate.</summary>
    public const string ComputeSecondsPerOutputFrameKey = "VideoEnhance:Hosted:ComputeSecondsPerOutputFrame";

    /// <summary>The defaults: 120 s hosted, 600 s local, 0.05 compute seconds per 1080p output frame.</summary>
    public static VideoEnhanceHostedSettings Default { get; } = new(
        VideoEnhanceCreditPricing.DefaultMaxSourceSeconds,
        VideoEnhanceStepConfigLimits.LocalMaxSourceSeconds,
        VideoEnhanceCreditPricing.DefaultComputeSecondsPerOutputFrame);

    /// <summary>Reads the settings; a hosted cap above the platform limit is clamped (a deployment can only lower it).</summary>
    public static VideoEnhanceHostedSettings From(IConfiguration? configuration)
    {
        if (configuration is null)
            return Default;

        int hosted = Positive(configuration[VideoEnhanceCreditPricing.MaxSourceSecondsKey], Default.HostedMaxSourceSeconds);
        int local = Positive(configuration[LocalMaxSourceSecondsKey], Default.LocalMaxSourceSeconds);
        decimal rate = PositiveDecimal(configuration[ComputeSecondsPerOutputFrameKey], Default.ComputeSecondsPerOutputFrame);
        return new VideoEnhanceHostedSettings(
            Math.Min(hosted, VideoEnhanceStepConfigLimits.LocalMaxSourceSeconds),
            Math.Min(local, VideoEnhanceStepConfigLimits.LocalMaxSourceSeconds),
            rate);
    }

    private static int Positive(string? raw, int fallback) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0 ? value : fallback;

    private static decimal PositiveDecimal(string? raw, decimal fallback) =>
        decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value) && value > 0m ? value : fallback;
}

/// <summary>The platform's hard limits (not deployment settings).</summary>
public static class VideoEnhanceStepConfigLimits
{
    /// <summary>The longest source any engine accepts: the local cap's default and ceiling.</summary>
    public const int LocalMaxSourceSeconds = 600;
}
