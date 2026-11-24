using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// How the compute usage kinds (<see cref="UsageKind.RenderOutputSeconds"/>, <see cref="UsageKind.EncodeOutputSeconds"/>,
/// <see cref="UsageKind.AnalyzeMediaSeconds"/>) are priced, and the one place that records them
/// (docs/design/saas-metering.md "Pricing compute", docs/design/model-catalog.md §5.4).
/// <para>
/// <b>Quantity is a property of the job, never of the clock.</b> It is the duration of the media the platform
/// itself probed (the rendered or encoded output, or the analysed source), so a slow machine, a retry or a
/// runner's own reported time can neither inflate nor deflate a charge.
/// </para>
/// <para>
/// <b>Resolution tier.</b> A 4K second costs more than a 720p one, and that has to be data, not code. The tier is
/// the price VARIANT on the system compute capability (<see cref="WellKnownModelCatalog.ComputeCapabilityId"/>), one
/// price row per unit and tier. It is derived from the SHORT side so a vertical 1080x1920 reel is 1080p like its
/// landscape twin.
/// </para>
/// </summary>
public static class ComputeMetering
{
    /// <summary>The kind of the system compute provider (no vendor; never offered in a picker).</summary>
    public const InferenceProviderKind ProviderKind = InferenceProviderKind.OpenAICompatible;

    public const string Tier720 = "720p";
    public const string Tier1080 = "1080p";
    public const string TierUhd = "uhd";

    /// <summary>The tier name for a frame size; an unknown size (0x0, e.g. audio only) prices as the cheapest tier.</summary>
    public static string TierFor(int width, int height)
    {
        int shortSide = width > 0 && height > 0 ? Math.Min(width, height) : 0;
        if (shortSide <= 720) return Tier720;
        if (shortSide <= 1080) return Tier1080;
        return TierUhd;
    }

    /// <summary>
    /// Records <paramref name="seconds"/> of compute under the ambient scope. Never throws into the work being
    /// metered; a non-positive or non-finite duration records nothing.
    /// </summary>
    public static async ValueTask<long> RecordSecondsAsync(
        IUsageRecorder? recorder,
        UsageKind kind,
        double seconds,
        int width,
        int height,
        ILogger? logger,
        CancellationToken ct = default,
        UsageBillingMode billingMode = UsageBillingMode.Platform)
    {
        if (recorder is null || !double.IsFinite(seconds) || seconds <= 0) return 0;

        try
        {
            return await recorder.RecordAsync(new UsageRecordRequest(
                kind, (decimal)Math.Round(seconds, 3), WellKnownModelCatalog.ComputeCapabilityId, ProviderKind,
                WellKnownModelCatalog.ComputeModelCode, billingMode,
                Capability: InferenceProviderCapability.Compute,
                Variant: TierFor(width, height),
                AccountId: WellKnownModelCatalog.ComputeProviderId), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnattributedUsageException)
        {
            logger?.LogError(ex, "Metering {Kind} ({Seconds}s) failed", kind, seconds);
            return 0;
        }
    }
}
