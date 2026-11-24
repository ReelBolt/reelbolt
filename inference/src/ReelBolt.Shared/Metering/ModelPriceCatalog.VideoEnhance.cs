using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Metering;

// Hosted video enhancement (video-enhance). A row exists ONLY where fal publishes a price that is
// cited here; an endpoint or tier with no cited price has NO row and stays unpriced and unofferable.
//
//  * Upscalers (Topaz, bytedance): UsageKind.VideoEnhanceSeconds, USD per billed OUTPUT second, one `code#tier` card per
//    output tier (720p, 1080p, uhd) and the doubled `-hfr` twin used when the output runs above 30 fps.
//  * Interpolators (RIFE, FILM): UsageKind.VideoEnhanceComputeSeconds, USD per GPU compute second. The vendor returns no
//    compute figure, so the platform HOLDS an estimate and settles at it (VideoEnhanceCreditPricing).
//
// The figures are restated here rather than read from VideoEnhanceModelCatalog on purpose, exactly as the audio rows
// are: VideoEnhancePricingTests asserts the two cannot drift apart.
public static partial class ModelPriceCatalog
{
    private const string TopazRead =
        "published vendor price, fal.ai/models/fal-ai/topaz/upscale/video/llms.txt, read 2026-11-02: \"For every second a " +
        "video your request will cost $0.01 for up to 720p, $0.02 for 720p to 1080p, and $0.08 for above 1080p output. " +
        "Price doubles for 60fps output.\"";

    private const string BytedanceRead =
        "published vendor price, fal.ai/models/fal-ai/bytedance-upscaler/upscale/video/llms.txt, read 2026-11-02: " +
        "\"Upscaling to 1080p costs $0.0072/s, 2K costs $0.0144/s, and 4K costs $0.0288/s, at a 30fps rate. Processing at " +
        "60fps doubles the cost for any resolution.\" - standard tier; 2K is priced as 4K";

    private const string ComputeRead =
        "published vendor price, fal.ai/models/{0}/llms.txt, read 2026-11-02: \"Your request will cost $0.0013 per compute " +
        "second.\"; MUST MEASURE: compute seconds per output frame (the hold is an estimate), audio kept or dropped";

    private const string WanVaceRead =
        "published vendor price, fal.ai/models/fal-ai/wan-vace-14b/outpainting/llms.txt, read 2026-11-02: \"$0.08 per " +
        "video second for 720p, $0.06 per video second for 580p, $0.04 per video second for 480p. Video seconds are " +
        "calculated at 16 frames per second.\"";

    private const string LumaReframeRead =
        "published vendor price, fal.ai/models/fal-ai/luma-dream-machine/ray-2/reframe/llms.txt, read 2026-11-02: " +
        "\"$0.2 per seconds\"; no resolution tier and no frame cap";

    private static ModelPrice VideoUpscaleRow(string model, decimal usdPerSecond, string basis) =>
        new(InferenceProviderKind.FalAi, InferenceProviderCapability.VideoUpscale, model,
            new Dictionary<UsageKind, decimal> { [UsageKind.VideoEnhanceSeconds] = usdPerSecond },
            basis + "; MUST MEASURE: whether 50 fps bills as 60, audio kept or dropped, max input length");

    private static ModelPrice FrameInterpolationRow(string model, decimal usdPerComputeSecond) =>
        new(InferenceProviderKind.FalAi, InferenceProviderCapability.FrameInterpolation, model,
            new Dictionary<UsageKind, decimal> { [UsageKind.VideoEnhanceComputeSeconds] = usdPerComputeSecond },
            string.Format(ComputeRead, model));

    private static ModelPrice VideoOutpaintRow(string model, decimal usdPerSecond, string basis) =>
        new(InferenceProviderKind.FalAi, InferenceProviderCapability.VideoOutpaint, model,
            new Dictionary<UsageKind, decimal> { [UsageKind.VideoEnhanceSeconds] = usdPerSecond },
            basis + "; MUST MEASURE: seams and flicker, what the vendor does past 241 frames, audio kept or dropped");

    private static IEnumerable<ModelPrice> VideoEnhancePrices()
    {
        const string topaz = "fal-ai/topaz/upscale/video";
        yield return VideoUpscaleRow(topaz + "#720p", 0.01m, TopazRead);
        yield return VideoUpscaleRow(topaz + "#1080p", 0.02m, TopazRead);
        yield return VideoUpscaleRow(topaz + "#uhd", 0.08m, TopazRead);
        yield return VideoUpscaleRow(topaz + "#720p-hfr", 0.02m, TopazRead + " (doubled: output above 30 fps)");
        yield return VideoUpscaleRow(topaz + "#1080p-hfr", 0.04m, TopazRead + " (doubled: output above 30 fps)");
        yield return VideoUpscaleRow(topaz + "#uhd-hfr", 0.16m, TopazRead + " (doubled: output above 30 fps)");

        const string bytedance = "fal-ai/bytedance-upscaler/upscale/video";
        yield return VideoUpscaleRow(bytedance + "#1080p", 0.0072m, BytedanceRead);
        yield return VideoUpscaleRow(bytedance + "#uhd", 0.0288m, BytedanceRead);
        yield return VideoUpscaleRow(bytedance + "#1080p-hfr", 0.0144m, BytedanceRead + " (doubled: output above 30 fps)");
        yield return VideoUpscaleRow(bytedance + "#uhd-hfr", 0.0576m, BytedanceRead + " (doubled: output above 30 fps)");

        yield return FrameInterpolationRow("fal-ai/rife/video", 0.0013m);
        yield return FrameInterpolationRow("fal-ai/film/video", 0.0013m);

        const string wanVace = "fal-ai/wan-vace-14b/outpainting";
        yield return VideoOutpaintRow(wanVace + "#720p", 0.08m, WanVaceRead);
        yield return VideoOutpaintRow(wanVace + "#580p", 0.06m, WanVaceRead);
        yield return VideoOutpaintRow(wanVace + "#480p", 0.04m, WanVaceRead);

        yield return VideoOutpaintRow("fal-ai/luma-dream-machine/ray-2/reframe", 0.2m, LumaReframeRead);
    }
}
