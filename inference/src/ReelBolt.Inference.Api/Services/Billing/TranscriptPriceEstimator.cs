using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>A transcript's price: the media length, what it costs in millicredits and the organization's lock-free balance.</summary>
public sealed record TranscriptPrice(double Seconds, long RequiredMillis, long BalanceMillis);

/// <summary>
/// The one place a transcript is priced (the media's length through the catalogue's speech-to-text price), shared by the
/// media analysis routes and the post text estimate so the two never disagree.
/// </summary>
public static class TranscriptPriceEstimator
{
    /// <summary>Null when the length is unknown or no transcription provider resolves.</summary>
    public static async Task<TranscriptPrice?> PriceAsync(
        IInferenceProviderResolver providers, INonWorkflowChargeService charges, double? durationSec, Guid organizationId, CancellationToken ct)
    {
        if (durationSec is not { } seconds || seconds <= 0) return null;
        ResolvedTranscriptionProvider? provider = await providers.ResolveTranscriptionAsync(null, ct);
        if (provider is null) return null;

        long millis = await charges.EstimateAsync(new PriceRequest(
            null, provider.AccountId, provider.Kind, InferenceProviderCapability.Transcription, provider.ModelName,
            UsageKind.AsrAudioSeconds, null, MeteringChatClient.BillingModeOf(provider.OrganizationId),
            (decimal)Math.Round(seconds, 3)), ct);
        long available = await charges.GetAvailableMillisAsync(organizationId, ct) ?? 0;
        return new TranscriptPrice(seconds, millis, available);
    }
}
