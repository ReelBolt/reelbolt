using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Metering;

// Music and sound-effect generation (Product decision: fal's hosted endpoints AND a direct ElevenLabs kind),
// priced in each vendor's own unit: billed MINUTES of music (UsageKind.MusicGenMinutes; fal rounds each started
// minute up) and SECONDS of sound effect (UsageKind.SoundEffectGenSeconds).
//
// The figures are restated here rather than read from AudioModelCatalog on purpose, exactly as the fal video
// rows are: AudioGenerationCatalogPricingTests asserts the two cannot drift apart, which a derived row could not
// prove. Source: research/audio-vendors.md (fal llms.txt and elevenlabs.io/pricing/api, read 2026-10-17). The
// ElevenLabs rows are the conservative reading of an ambiguous page ("per minute" in the table, "billed per
// generation" in the FAQ) and are on docs/elevenlabs-provider.md's must-measure list.
public static partial class ModelPriceCatalog
{
    private const string FalAudioRead = "published vendor price, fal.ai/models/{0}/llms.txt, read 2026-10-17: ";
    private const string ElevenLabsAudioRead = "published vendor price, elevenlabs.io/pricing/api, read 2026-10-17: ";

    private static ModelPrice Audio(
        InferenceProviderKind kind, InferenceProviderCapability capability, string model, UsageKind unit, decimal usd, string basis) =>
        new(kind, capability, model, new Dictionary<UsageKind, decimal> { [unit] = usd }, basis);

    private static IEnumerable<ModelPrice> AudioPrices()
    {
        yield return Audio(InferenceProviderKind.FalAi, InferenceProviderCapability.MusicGeneration,
            "fal-ai/elevenlabs/music", UsageKind.MusicGenMinutes, 0.60m,
            string.Format(FalAudioRead, "fal-ai/elevenlabs/music") +
            "\"$0.6 per output audio minute, rounded up to the next minute\"");

        yield return Audio(InferenceProviderKind.FalAi, InferenceProviderCapability.SoundEffectGeneration,
            "fal-ai/elevenlabs/sound-effects/v2", UsageKind.SoundEffectGenSeconds, 0.002m,
            string.Format(FalAudioRead, "fal-ai/elevenlabs/sound-effects/v2") +
            "\"$0.002 per seconds\"; requested vs delivered seconds is unconfirmed");

        yield return Audio(InferenceProviderKind.FalAi, InferenceProviderCapability.SoundEffectGeneration,
            "fal-ai/mmaudio-v2/text-to-audio", UsageKind.SoundEffectGenSeconds, 0.001m,
            string.Format(FalAudioRead, "fal-ai/mmaudio-v2/text-to-audio") + "\"$0.001 per second\"");

        yield return Audio(InferenceProviderKind.ElevenLabs, InferenceProviderCapability.MusicGeneration,
            "music_v2_5", UsageKind.MusicGenMinutes, 0.15m,
            ElevenLabsAudioRead + "Eleven Music \"$0.15 per minute\"; MUST MEASURE (FAQ: \"billed per generation\")");

        yield return Audio(InferenceProviderKind.ElevenLabs, InferenceProviderCapability.MusicGeneration,
            "music_v2", UsageKind.MusicGenMinutes, 0.15m,
            ElevenLabsAudioRead + "Eleven Music \"$0.15 per minute\"; MUST MEASURE (FAQ: \"billed per generation\")");

        yield return Audio(InferenceProviderKind.ElevenLabs, InferenceProviderCapability.SoundEffectGeneration,
            "eleven_text_to_sound_v2", UsageKind.SoundEffectGenSeconds, 0.12m / 60m,
            ElevenLabsAudioRead + "Sound Effects \"$0.12 per minute\" = $0.002 per second; MUST MEASURE (FAQ: \"billed " +
            "per generation\")");
    }
}
