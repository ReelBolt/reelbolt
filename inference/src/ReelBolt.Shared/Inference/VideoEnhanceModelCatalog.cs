using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// One hosted video-enhancement endpoint the platform can call (an upscaler or a frame interpolator), with the request
/// fields its schema declares and its price. Transcribed from fal's public OpenAPI document and <c>llms.txt</c> page for
/// the endpoint, read 2026-11-02; <c>docs/fal-ai-models.md</c> ("13. Video enhancement") carries the same table and the
/// list of what must still be measured with a real key. A field the schema does not declare is not in
/// <see cref="AllowedFields"/> and the client REFUSES a request body that carries it.
/// </summary>
public sealed record VideoEnhanceEndpoint
{
    /// <summary>The provider kind that serves it (always <see cref="InferenceProviderKind.FalAi"/>).</summary>
    public InferenceProviderKind ProviderKind { get; init; } = InferenceProviderKind.FalAi;

    /// <summary>The fal endpoint id (its queue path).</summary>
    public required string Id { get; init; }

    /// <summary>A short human label.</summary>
    public required string Title { get; init; }

    /// <summary><see cref="InferenceProviderCapability.VideoUpscale"/> or <see cref="InferenceProviderCapability.FrameInterpolation"/>.</summary>
    public required InferenceProviderCapability Capability { get; init; }

    /// <summary>True for the one endpoint of its capability used when a row or request names none.</summary>
    public bool IsDefault { get; init; }

    /// <summary>The field that carries the source video URL.</summary>
    public string VideoField { get; init; } = "video_url";

    /// <summary>The output property that carries the enhanced clip (an object with a <c>url</c>).</summary>
    public string OutputField { get; init; } = "video";

    /// <summary>Every request field this platform may send to the endpoint (a subset of what its schema declares).</summary>
    public required IReadOnlySet<string> AllowedFields { get; init; }

    /// <summary>The usage kind metered.</summary>
    public required UsageKind Unit { get; init; }

    /// <summary>
    /// Upscalers: USD per billed OUTPUT second by tier (<c>720p</c>, <c>1080p</c>, <c>uhd</c> and their <c>-hfr</c> twins).
    /// A tier that is absent is unpriced and unofferable. Null for an interpolator.
    /// </summary>
    public IReadOnlyDictionary<string, decimal>? UsdPerSecondByTier { get; init; }

    /// <summary>Interpolators: USD per GPU compute second. Null for an upscaler.</summary>
    public decimal? UsdPerComputeSecond { get; init; }

    /// <summary>Outpainters with one flat per-second price and no resolution tier (Luma reframe). Null otherwise.</summary>
    public decimal? UsdPerSecondFlat { get; init; }

    /// <summary>Outpainters: the model's own frame ceiling, or null when it has none. Wan VACE: 241.</summary>
    public int? MaxOutputFrames { get; init; }

    /// <summary>Outpainters: the model's own frame floor, or null when it has none. Wan VACE: 81.</summary>
    public int? MinOutputFrames { get; init; }

    /// <summary>True when the vendor's schema makes <c>prompt</c> required (Wan VACE; Luma takes an optional one).</summary>
    public bool PromptRequired { get; init; }

    /// <summary>Upscalers: the smallest and largest <c>upscale_factor</c> the endpoint accepts.</summary>
    public double MinFactor { get; init; } = 1;

    /// <summary>Upscalers: the largest factor the endpoint accepts (4 for both catalogued upscalers).</summary>
    public double MaxFactor { get; init; } = 4;

    /// <summary>
    /// The largest output short side the cited price tiers cover (bytedance: 4K = 2160); above it the endpoint is
    /// unpriced and unofferable. Null = the vendor's top tier is open-ended (Topaz "above 1080p").
    /// </summary>
    public int? MaxPricedShortSide { get; init; }

    /// <summary>The highest output frame rate the cited prices cover (only "60fps" doubling is cited); above it the endpoint is unpriced. Null = no stated ceiling.</summary>
    public double? MaxPricedFps { get; init; }

    /// <summary>Interpolators: the frame multipliers the HOSTED path offers (2x and 4x; 8x is local only).</summary>
    public IReadOnlyList<int> HostedMultipliers { get; init; } = [];

    /// <summary>Where the price was read, in the vendor's own words.</summary>
    public required string PriceBasis { get; init; }

    /// <summary>True when the billing unit, rounding or compute figure is unconfirmed and must be measured with a real key.</summary>
    public bool PriceMustBeMeasured { get; init; } = true;
}

/// <summary>
/// The read catalogue of hosted video-enhancement endpoints. A model that is not here has no read schema and no price,
/// so it is unofferable (fail closed, never a default price). Defaults: <c>fal-ai/topaz/upscale/video</c> for
/// <see cref="InferenceProviderCapability.VideoUpscale"/> and <c>fal-ai/rife/video</c> for
/// <see cref="InferenceProviderCapability.FrameInterpolation"/>.
/// </summary>
public sealed class VideoEnhanceModelCatalog
{
    /// <summary>The Topaz endpoint id (default upscaler).</summary>
    public const string TopazId = "fal-ai/topaz/upscale/video";

    /// <summary>The bytedance endpoint id (alternative upscaler).</summary>
    public const string BytedanceId = "fal-ai/bytedance-upscaler/upscale/video";

    /// <summary>The RIFE endpoint id (default interpolator).</summary>
    public const string RifeId = "fal-ai/rife/video";

    /// <summary>The FILM endpoint id (alternative interpolator).</summary>
    public const string FilmId = "fal-ai/film/video";

    /// <summary>The Wan VACE endpoint id (default outpainter).</summary>
    public const string WanVaceId = "fal-ai/wan-vace-14b/outpainting";

    /// <summary>The Luma Ray 2 reframe endpoint id (alternative outpainter).</summary>
    public const string LumaReframeId = "fal-ai/luma-dream-machine/ray-2/reframe";

    /// <summary>The frame rate the Wan VACE price sentence bills video seconds at ("16 frames per second").</summary>
    public const int WanVaceBillingFps = 16;

    private const string Read = "published vendor price, fal.ai/models/{0}/llms.txt, read 2026-11-02: ";

    private static readonly Dictionary<string, decimal> TopazTiers = new(StringComparer.Ordinal)
    {
        ["720p"] = 0.01m,
        ["1080p"] = 0.02m,
        ["uhd"] = 0.08m,
        ["720p-hfr"] = 0.02m,
        ["1080p-hfr"] = 0.04m,
        ["uhd-hfr"] = 0.16m,
    };

    // Bytedance standard tier only: the vendor lists 1080p, 2K and 4K and the platform has no 2K tier, so a 2K output
    // is priced at the 4K figure (the safe, higher direction). There is no 720p row: bytedance does not output it.
    private static readonly Dictionary<string, decimal> BytedanceTiers = new(StringComparer.Ordinal)
    {
        ["1080p"] = 0.0072m,
        ["uhd"] = 0.0288m,
        ["1080p-hfr"] = 0.0144m,
        ["uhd-hfr"] = 0.0576m,
    };

    // Wan VACE: the three output resolutions its price sentence names. Its schema also lists auto/240p/360p; a
    // source whose short side is below 480 is sent 480p (the cheapest cited row), and one above 720 is sent 720p
    // (the model's ceiling) and conformed locally.
    private static readonly Dictionary<string, decimal> WanVaceTiers = new(StringComparer.Ordinal)
    {
        ["720p"] = 0.08m,
        ["580p"] = 0.06m,
        ["480p"] = 0.04m,
    };

    private static readonly VideoEnhanceEndpoint[] Endpoints =
    [
        new()
        {
            Id = TopazId,
            Title = "Topaz Video Upscale (on fal)",
            Capability = InferenceProviderCapability.VideoUpscale,
            IsDefault = true,
            // target_fps is deliberately NOT allowed: it turns on interpolation and changes frame timing.
            AllowedFields = new HashSet<string>(StringComparer.Ordinal)
                { "video_url", "upscale_factor", "model", "H264_output", "enable_safety_checker" },
            Unit = UsageKind.VideoEnhanceSeconds,
            UsdPerSecondByTier = TopazTiers,
            MinFactor = 1,
            MaxFactor = 4,
            PriceBasis = string.Format(Read, TopazId) + "\"For every second a video your request will cost $0.01 for up to " +
                         "720p, $0.02 for 720p to 1080p, and $0.08 for above 1080p output. Price doubles for 60fps " +
                         "output. For Gaia 2 output costs half of the prices.\"; whether a 50 fps output bills as 60 is " +
                         "unconfirmed (anything above 30 fps is priced doubled here)",
        },
        new()
        {
            Id = BytedanceId,
            Title = "ByteDance Video Upscaler (on fal)",
            Capability = InferenceProviderCapability.VideoUpscale,
            // scale_ratio overrides target_resolution; enhancement_tier is pinned to "standard" (pro is 10x the price).
            AllowedFields = new HashSet<string>(StringComparer.Ordinal)
                { "video_url", "scale_ratio", "target_fps", "enhancement_tier" },
            Unit = UsageKind.VideoEnhanceSeconds,
            UsdPerSecondByTier = BytedanceTiers,
            MinFactor = 1.1,
            MaxFactor = 4,
            MaxPricedShortSide = 2160,
            MaxPricedFps = 60,
            PriceBasis = string.Format(Read, BytedanceId) + "\"Upscaling to 1080p costs $0.0072/s, 2K costs $0.0144/s, and " +
                         "4K costs $0.0288/s, at a 30fps rate. Processing at 60fps doubles the cost for any resolution. " +
                         "Processing in pro mode makes the price 10 times.\" - standard tier rows only; 2K is priced as 4K",
        },
        new()
        {
            Id = RifeId,
            Title = "RIFE frame interpolation (on fal)",
            Capability = InferenceProviderCapability.FrameInterpolation,
            IsDefault = true,
            AllowedFields = new HashSet<string>(StringComparer.Ordinal)
                { "video_url", "num_frames", "use_calculated_fps", "use_scene_detection", "loop" },
            Unit = UsageKind.VideoEnhanceComputeSeconds,
            UsdPerComputeSecond = 0.0013m,
            HostedMultipliers = [2, 4],
            PriceBasis = string.Format(Read, RifeId) + "\"Your request will cost $0.0013 per compute second.\"; compute seconds " +
                         "per output frame are UNMEASURED (the result carries no compute time), so the hold is an estimate",
        },
        new()
        {
            Id = FilmId,
            Title = "FILM frame interpolation (on fal)",
            Capability = InferenceProviderCapability.FrameInterpolation,
            // sync_mode is never allowed: it would return the clip as a data URI instead of a URL.
            AllowedFields = new HashSet<string>(StringComparer.Ordinal)
                { "video_url", "num_frames", "use_calculated_fps", "use_scene_detection", "loop", "enable_safety_checker" },
            Unit = UsageKind.VideoEnhanceComputeSeconds,
            UsdPerComputeSecond = 0.0013m,
            HostedMultipliers = [2, 4],
            PriceBasis = string.Format(Read, FilmId) + "\"Your request will cost $0.0013 per compute second.\"; compute seconds " +
                         "per output frame are UNMEASURED (the result carries no compute time), so the hold is an estimate",
        },
        new()
        {
            Id = WanVaceId,
            Title = "Wan VACE video outpainting (on fal)",
            Capability = InferenceProviderCapability.VideoOutpaint,
            IsDefault = true,
            // The model requires a prompt; expand_ratio is one scalar for every enabled side. sync_mode, aspect_ratio,
            // enable_auto_downsample, num_frames and frames_per_second are deliberately NOT allowed: the platform asks
            // the model to match the input's frames and rate, and computes its own expansion.
            AllowedFields = new HashSet<string>(StringComparer.Ordinal)
            {
                "video_url", "prompt", "expand_left", "expand_right", "expand_top", "expand_bottom", "expand_ratio",
                "match_input_num_frames", "match_input_frames_per_second", "resolution", "enable_safety_checker"
            },
            Unit = UsageKind.VideoEnhanceSeconds,
            UsdPerSecondByTier = WanVaceTiers,
            MinOutputFrames = 81,
            MaxOutputFrames = 241,
            PromptRequired = true,
            PriceBasis = string.Format(Read, WanVaceId) + "\"$0.08 per video second for 720p, $0.06 per video second for " +
                         "580p, $0.04 per video second for 480p. Video seconds are calculated at 16 frames per second.\"" +
                         "; MUST MEASURE: seams and flicker, what the vendor does past 241 frames",
        },
        new()
        {
            Id = LumaReframeId,
            Title = "Luma Ray 2 reframe (on fal)",
            Capability = InferenceProviderCapability.VideoOutpaint,
            // aspect_ratio is the model's whole interface: there are no per-side margins and no output-resolution knob.
            // 4:5 is not in its enum, so that target is refused for this endpoint rather than approximated.
            AllowedFields = new HashSet<string>(StringComparer.Ordinal) { "video_url", "aspect_ratio", "prompt", "enable_safety_checker" },
            Unit = UsageKind.VideoEnhanceSeconds,
            UsdPerSecondFlat = 0.2m,
            PriceBasis = string.Format(Read, LumaReframeId) + "\"$0.2 per seconds\"; no resolution tier and no frame cap",
        },
    ];

    /// <summary>The process-wide catalogue.</summary>
    public static VideoEnhanceModelCatalog Instance { get; } = new();

    private readonly Dictionary<string, VideoEnhanceEndpoint> _byId =
        Endpoints.ToDictionary(e => e.Id, StringComparer.Ordinal);

    /// <summary>Every catalogued endpoint.</summary>
    public IReadOnlyList<VideoEnhanceEndpoint> All => Endpoints;

    /// <summary>The endpoint used for <paramref name="capability"/> when none is named.</summary>
    public VideoEnhanceEndpoint DefaultFor(InferenceProviderCapability capability) =>
        Endpoints.Single(e => e.Capability == capability && e.IsDefault);

    /// <summary>The endpoint with this id and capability, or null. A blank id is not the default here.</summary>
    public VideoEnhanceEndpoint? Find(InferenceProviderCapability capability, string? id) =>
        id is not null && _byId.TryGetValue(id.Trim(), out VideoEnhanceEndpoint? e) && e.Capability == capability ? e : null;

    /// <summary>The endpoint ids of <paramref name="capability"/>, comma-separated, for an error message.</summary>
    public string IdsFor(InferenceProviderCapability capability) =>
        string.Join(", ", Endpoints.Where(e => e.Capability == capability).Select(e => e.Id));

    /// <summary>The catalogued endpoint for a request model: blank = the capability's default, unknown or wrong capability = null.</summary>
    public VideoEnhanceEndpoint? Resolve(InferenceProviderCapability capability, string? model)
    {
        if (capability is not (InferenceProviderCapability.VideoUpscale or InferenceProviderCapability.FrameInterpolation
            or InferenceProviderCapability.VideoOutpaint))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(model) ? DefaultFor(capability) : Find(capability, model);
    }
}
