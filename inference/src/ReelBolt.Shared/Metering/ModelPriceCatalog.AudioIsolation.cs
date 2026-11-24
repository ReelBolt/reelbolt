using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Metering;

// Hosted voice isolation (audio-cleanup): priced per billed MINUTE of source audio
// (UsageKind.AudioCleanupMinutes). A row exists ONLY where fal publishes a price that is cited here; an endpoint
// with no cited price has NO row and stays unpriced and unofferable (never a default price).
//
// The figure is restated here rather than read from AudioIsolationModelCatalog on purpose, exactly as the music rows
// are: AudioIsolationPricingTests asserts the two cannot drift apart.
public static partial class ModelPriceCatalog
{
    private static IEnumerable<ModelPrice> AudioIsolationPrices()
    {
        yield return new ModelPrice(InferenceProviderKind.FalAi, InferenceProviderCapability.AudioIsolation,
            "fal-ai/elevenlabs/audio-isolation",
            new Dictionary<UsageKind, decimal> { [UsageKind.AudioCleanupMinutes] = 0.10m },
            "published vendor price, fal.ai/models/fal-ai/elevenlabs/audio-isolation/llms.txt, read 2026-11-02: " +
            "\"$0.1 per minutes\"; MUST MEASURE: started-minute rounding, input vs output length, minimum charge");
    }
}
