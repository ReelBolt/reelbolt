using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Shared.Metering;

// The fal.ai VIDEO endpoints (FalAiVideoCatalog), priced in the vendor's own unit: USD per second of
// generated video, which is what VideoGenerate meters (UsageKind.VideoGenSeconds).
//
// WHY ONE CARD PER ENDPOINT AND NOT ONE PER RESOLUTION. A rate card keys on a model NAME, and this
// capability's model key is the endpoint id the step submitted (VideoGenerateStepExecutor's
// RecordVideoGenUsageAsync). fal prices most video endpoints per resolution, and a resolution is not in
// that key - so, exactly as ModelPriceCatalog's MiniMax video rows do, the card is the endpoint's
// VENDOR-DEFAULT tier and the basis names the tiers it cannot express. The money the budget gate
// reserves is a different path and IS per resolution: it prices the take from FalAiVideoCatalog's own
// table through Describe (VideoGenerationPricing), which is where a 4K take is charged at 4K.
//
// Every figure was read from the endpoint's own pricing sentence on its model page
// (fal.ai/models/{id}, whose llms.txt renders the same sentence) on 2026-10-16, and the catalogue entry
// carries the verbatim sentence plus the same figures; FalAiVideoGenerationClientTests asserts the two
// cannot drift apart.
public static partial class ModelPriceCatalog
{
    private const string FalVideoRead =
        "published vendor price, fal.ai/models/{0}, read 2026-10-16 (the same sentence and figures as " +
        "FalAiVideoCatalog's entry for this endpoint)";

    private static string FalVideoBasis(string id, string statement) =>
        string.Format(FalVideoRead, id) + ": " + statement;

    private static ModelPrice FalVideo(string id, decimal usdPerSecond, string statement) =>
        new(InferenceProviderKind.FalAi, InferenceProviderCapability.VideoGeneration, id,
            new Dictionary<UsageKind, decimal> { [UsageKind.VideoGenSeconds] = usdPerSecond },
            FalVideoBasis(id, statement));

    /// <summary>
    /// The per-second price of an endpoint's VENDOR-DEFAULT resolution tier, named in each basis. The
    /// post-promotion figures are taken wherever the vendor's page shows a promotion and its rate after
    /// it, so a take is never under-charged.
    /// </summary>
    private static IEnumerable<ModelPrice> FalVideoPrices()
    {
        // ---------------------------------------------------------------- image-to-video
        yield return FalVideo("fal-ai/vidu/q4/image-to-video", 0.095m,
            "\"For every second of video you generate, you will be charged $0.0315 at 540p, $0.0665 at 720p, " +
            "$0.084 at 1080p, $0.133 at 2K or $0.273 at 4K. Note: these are promotional rates, 30% off until " +
            "November 30, after which 540p is $0.045/second, 720p is $0.095/second, 1080p is $0.12/second, 2K is " +
            "$0.19/second and 4K is $0.39/second.\" - the 720p default at the POST-promotion rate; a 2K or 4K take " +
            "is under-priced by this row (the gate's own estimate is not: it prices the take's resolution)");

        yield return FalVideo("minimax/h3-max-turbo/image-to-video", 0.04m,
            "\"Video costs $0.015 per second at 480p, $0.024 per second at 768p, and $0.048 per second at 1080p. " +
            "Note: these are promotional rates, 40% off for a limited time. The discount ends October 15, after " +
            "which 480p is $0.025/second, 768p is $0.04/second, and 1080p is $0.08/second.\" - the 768P default at " +
            "the POST-discount rate, as the MiniMax video rows above");

        yield return FalVideo("fal-ai/kling-video/v3/turbo/pro/image-to-video", 0.14m,
            "\"For every second of video you generate, you will be charged $0.14. For example, a 5s video will cost " +
            "$0.70.\" - the endpoint's schema has NO resolution field, so this one flat rate is exact whatever the " +
            "output size turns out to be; the catalogue offers '" + FalVideoCatalog.NativeToken + "' as its only " +
            "resolution token");

        yield return FalVideo("alibaba/wan-3.0/image-to-video", 0.20m,
            "\"For every second of video you generate, you will be charged $0.05 480p, $0.10 720p, or $0.20 1080p. " +
            "For example, a 5s video at 720p will cost $0.50. Pricing is subjected to change.\" - the 1080p schema " +
            "default; a 480p or 720p take is over-priced by this row, which is the safe direction");

        yield return FalVideo("blackforestlabs/flux-3/first-last-frame-to-video", 0.17m,
            "\"Your request will be charged at 0.17 $ per second of generated video at 720p, and 0.29 $ per second " +
            "at 1080p.\" - the 720p schema default");

        yield return FalVideo("fal-ai/veo3.1/fast/image-to-video", 0.15m,
            "\"For every second of video you generate you will be charged $0.10 without audio or $0.15 with audio " +
            "for 720p or 1080p. At 4k, you will be charged $0.30 per second without audio, or $0.35 with.\" - the " +
            "720p WITH-AUDIO figure: generate_audio defaults to true and is not forwarded, so the request buys the " +
            "audio rate; 4k is under-priced by this row");

        yield return FalVideo("xai/grok-imagine-video/v1.5/lite/image-to-video", 0.03m,
            "\"Priced per second of output video and per input image, by resolution: 480p at $0.02/sec, 720p at " +
            "$0.03/sec, 1080p at $0.14/sec. Each input image costs $0.01.\" - the 720p schema default; the " +
            "per-input-image surcharge has no unit in this capability's metering, so an image-to-video take is " +
            "under-charged by $0.01 here");

        // ---------------------------------------------------------------- text-to-video
        yield return FalVideo("fal-ai/kling-video/v3/turbo/pro/text-to-video", 0.14m,
            "\"For every second of video you generate, you will be charged $0.14 . For example, a 5s video will " +
            "cost $0.70.\" - one flat rate, as its image-to-video sibling");

        yield return FalVideo("minimax/h3-max-turbo/text-to-video", 0.04m,
            "the same sentence as minimax/h3-max-turbo/image-to-video (\"$0.015 per second at 480p, $0.024 per " +
            "second at 768p, and $0.048 per second at 1080p ... after which 480p is $0.025/second, 768p is " +
            "$0.04/second, and 1080p is $0.08/second\") - the 768P default at the POST-discount rate");

        yield return FalVideo("alibaba/wan-3.0/text-to-video", 0.20m,
            "the same sentence as alibaba/wan-3.0/image-to-video (\"$0.05 480p, $0.10 720p, or $0.20 1080p\") - the " +
            "1080p schema default");

        yield return FalVideo("fal-ai/veo3.1", 0.40m,
            "\"For every second of video you generate you will be charged $0.20 without audio or $0.40 with audio " +
            "for 720p or 1080p. At 4k, you will be charged $0.40 per second without audio, or $0.60 with.\" - the " +
            "720p WITH-AUDIO figure, as the Fast image-to-video sibling; 4k is under-priced by this row");

        // ---------------------------------------------------------------- talking head (avatars phase 3)
        // Read 2026-11-06, the same sentences and figures as FalAiVideoCatalog's TalkingHead entries.
        yield return FalTalkingHead("minimax/h3-max/lip-sync/image-to-video", 0.08m,
            "\"Video costs $0.05 per second at 480p, $0.08 per second at 768p, $0.16 per second at 1080p, and $0.32 " +
            "per second at 2K. A 1.2x multiplier is added to videos over 15 seconds.\" - the 768P schema default; a " +
            "480P take is over-priced by this row (the safe direction), 1080P and 2K under-priced (the gate's own " +
            "estimate is not: it prices the take's resolution); the platform never sends more than 15 s of audio, so " +
            "the multiplier never applies");

        yield return FalTalkingHead("fal-ai/kling-video/ai-avatar/v2/standard", 0.0562m,
            "\"Price: $0.0562 per seconds\" - one flat rate and no resolution field, so the single card is exact");

        // ---------------------------------------------------------------- reference-to-video (subjects)
        // Read 2026-11-16, the same sentences and figures as FalAiVideoCatalog's ReferenceToVideo entries.
        yield return FalReferenceToVideo("alibaba/wan-3.0/reference-to-video", 0.20m,
            "\"For every second of video you generate, you will be charged $0.05 480p, $0.10 720p, or $0.20 1080p. " +
            "For example, a 5s video at 720p will cost $0.50. Pricing is subject to change.\" - the 1080p schema " +
            "default; a 480p or 720p take is over-priced by this row, which is the safe direction. The schema bills " +
            "INPUT VIDEO duration on top; the platform sends reference pictures only, never a reference video");

        yield return FalReferenceToVideo("fal-ai/veo3.1/reference-to-video", 0.40m,
            "\"For every second of video you generate you will be charged $0.20 without audio or $0.40 with audio " +
            "for 720p or 1080p. At 4k, you will be charged $0.40 per second without audio, or $0.60 with audio.\" - " +
            "the 720p WITH-AUDIO figure: generate_audio defaults to true and is not forwarded; 4k is under-priced by " +
            "this row (the gate's own estimate is not: it prices the take's resolution)");

        yield return FalReferenceToVideo("fal-ai/veo3.1/fast/reference-to-video", 0.15m,
            "\"For every second of video you generated, you will be charged $0.10 (audio off) or $0.15 (audio on).\" - " +
            "the WITH-AUDIO figure: generate_audio defaults to true and is not forwarded; the catalogue offers 720p " +
            "and 1080p only, because the page states no 4k price");
    }

    /// <summary>A reference-to-video row: the same unit as every fal video row, read on its own date.</summary>
    private static ModelPrice FalReferenceToVideo(string id, decimal usdPerSecond, string statement) =>
        new(InferenceProviderKind.FalAi, InferenceProviderCapability.VideoGeneration, id,
            new Dictionary<UsageKind, decimal> { [UsageKind.VideoGenSeconds] = usdPerSecond },
            "published vendor price, fal.ai/models/" + id + ", read 2026-11-16 (the same sentence and figures as " +
            "FalAiVideoCatalog's entry for this endpoint): " + statement);

    /// <summary>
    /// A talking-head row, under the LipSync capability (a talking head resolves LipSync, never VideoGeneration): the same
    /// unit as every fal video row (output seconds), read on its own date.
    /// </summary>
    private static ModelPrice FalTalkingHead(string id, decimal usdPerSecond, string statement) =>
        new(InferenceProviderKind.FalAi, InferenceProviderCapability.LipSync, id,
            new Dictionary<UsageKind, decimal> { [UsageKind.VideoGenSeconds] = usdPerSecond },
            "published vendor price, fal.ai/models/" + id + ", read 2026-11-06 (the same sentence and figures as " +
            "FalAiVideoCatalog's entry for this endpoint): " + statement);
}
