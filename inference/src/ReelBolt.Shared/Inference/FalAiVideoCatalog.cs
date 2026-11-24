namespace ReelBolt.Shared.Inference;

/// <summary>
/// The direction a fal.ai video endpoint generates in. Append-only; persisted by name in provenance.
/// </summary>
public enum FalVideoJob
{
    /// <summary>
    /// Animate a first frame (and, where the endpoint offers one, an end frame). The wave this
    /// catalogue leads with. Default: <c>fal-ai/vidu/q4/image-to-video</c>.
    /// </summary>
    ImageToVideo,

    /// <summary>Generate from a prompt alone. Default: <c>fal-ai/kling-video/v3/turbo/pro/text-to-video</c>.</summary>
    TextToVideo,

    /// <summary>
    /// Animate ONE picture of a person so they say the words of an audio track (avatars phase 3, the still-then-animate
    /// path). Only an avatar step uses it: the picture is the avatar's still, the audio its speech. Default:
    /// <c>minimax/h3-max/lip-sync/image-to-video</c>.
    /// </summary>
    TalkingHead,

    /// <summary>
    /// Generate from a prompt and a list of reference PICTURES of the subjects to keep consistent (people and objects;
    /// docs/design/avatar-objects.md, the <c>References</c> path). No frame: the pictures are subject material, not the
    /// opening image. Default: <c>alibaba/wan-3.0/reference-to-video</c>.
    /// </summary>
    ReferenceToVideo,
}

/// <summary>
/// How an endpoint spells a duration on the wire. The platform's own
/// <see cref="VideoGenerationRequest.DurationSeconds"/> is an integer, and the vendors disagree:
/// Vidu, MiniMax, Wan, Grok and FLUX.3 take a JSON number, Kling takes a JSON STRING holding that
/// number, and veo takes a JSON string holding seconds (<c>"8s"</c>). The schema's shape is
/// transcribed rather than guessed, because fal's queue validates the input document when the job
/// runs: a duration sent in the wrong shape is a failed take, not a coercion.
/// </summary>
public enum FalDurationForm
{
    /// <summary>A JSON integer: <c>"duration": 5</c>.</summary>
    Integer,

    /// <summary>A JSON string holding a whole number: <c>"duration": "5"</c> (Kling's enum).</summary>
    StringNumber,

    /// <summary>A JSON string holding seconds: <c>"duration": "8s"</c> (veo's enum).</summary>
    StringSeconds,
}

/// <summary>
/// One fal.ai video endpoint this client can call, with everything it needs to build and validate a
/// request for it and to read the answer. Every member was transcribed from the endpoint's own
/// OpenAPI schema (<c>fal.ai/api/openapi/queue/openapi.json?endpoint_id={id}</c>) and pricing sentence
/// (<c>fal.ai/models/{id}/llms.txt</c>) on 2026-10-16; <c>docs/fal-ai-models.md</c> carries the table
/// with both URLs. Nothing is guessed: a field the schema does not declare is REFUSED, an enum is
/// validated BEFORE it is sent, and a required field is required.
/// </summary>
public sealed record FalVideoEndpoint
{
    /// <summary>The endpoint id, which is also its path on the queue host.</summary>
    public required string Id { get; init; }

    /// <summary>Which direction it generates in.</summary>
    public required FalVideoJob Job { get; init; }

    /// <summary>A short human label.</summary>
    public required string Title { get; init; }

    /// <summary>The one endpoint the platform uses for <see cref="Job"/> unless a step names another.</summary>
    public bool IsDefault { get; init; }

    /// <summary>
    /// The first-frame image field (<c>image_url</c> on Kling, MiniMax, Vidu and veo;
    /// <c>start_image_url</c> on Wan and FLUX.3), or <c>null</c> on a text-to-video endpoint - which
    /// then REFUSES an image rather than dropping it.
    /// </summary>
    public string? FirstFrameField { get; init; }

    /// <summary>The schema marks <see cref="FirstFrameField"/> required.</summary>
    public bool FirstFrameRequired { get; init; }

    /// <summary>The end-frame field, or <c>null</c> when the endpoint documents none.</summary>
    public string? LastFrameField { get; init; }

    /// <summary>The schema marks <see cref="LastFrameField"/> required (FLUX.3's first-last-frame route).</summary>
    public bool LastFrameRequired { get; init; }

    /// <summary>The prompt field, or <c>null</c> when the endpoint documents none.</summary>
    public string? PromptField { get; init; } = "prompt";

    /// <summary>
    /// The schema marks the prompt required, or the endpoint would generate something nobody asked for.
    /// Kling's text-to-video declares <c>required: []</c> and is still required HERE: with neither a
    /// prompt nor a storyboard the vendor's own default is the request, which is not a request.
    /// </summary>
    public bool PromptRequired { get; init; }

    /// <summary>Longest prompt the schema allows.</summary>
    public int? PromptMaxLength { get; init; }

    /// <summary>How the duration is spelled on the wire.</summary>
    public FalDurationForm DurationForm { get; init; } = FalDurationForm.Integer;

    /// <summary>Shortest duration the schema allows.</summary>
    public required int MinDurationSeconds { get; init; }

    /// <summary>Longest duration the schema allows, or the last value of its enum.</summary>
    public required int MaxDurationSeconds { get; init; }

    /// <summary>
    /// The exact wire values when the schema is a CLOSED duration enum whose values are not derivable
    /// from the range (veo's <c>"4s"/"6s"/"8s"</c>). Null when any whole number in the range is legal.
    /// </summary>
    public IReadOnlyList<string>? AllowedDurations { get; init; }

    /// <summary>
    /// The resolution field, or <c>null</c> when the schema has none at all (Kling v3 turbo/pro: the
    /// output size follows the request, and the price is flat). A null field means the only resolution
    /// token this model offers is <see cref="FalVideoCatalog.NativeToken"/>, which is never sent.
    /// </summary>
    public string? ResolutionField { get; init; }

    /// <summary>The resolution tokens the schema accepts, in the vendor's own spelling, or the one native token.</summary>
    public required IReadOnlyList<string> Resolutions { get; init; }

    /// <summary>The schema's own default resolution. Documentation; the platform always states one.</summary>
    public string? VendorDefaultResolution { get; init; }

    /// <summary>
    /// The price in USD per second of generated video, keyed by the resolution token that is SENT
    /// (so the keys are the vendor's spelling: Vidu's <c>720p</c>, MiniMax's <c>768P</c>), or by
    /// <see cref="FalVideoCatalog.NativeToken"/> on a flat-priced endpoint.
    /// <para>
    /// This is the same table <c>ModelPriceCatalog.FalVideos</c> cites, so the budget gate's estimate
    /// and the metering rate card cannot disagree about the same endpoint.
    /// </para>
    /// </summary>
    public required IReadOnlyDictionary<string, decimal> PricePerSecondByResolution { get; init; }

    /// <summary>The aspect-ratio field, or <c>null</c> when the canvas follows the input image.</summary>
    public string? RatioField { get; init; }

    /// <summary>
    /// The ratio tokens this model accepts. A null <see cref="RatioField"/> means the vendor has no
    /// ratio input, so the list holds exactly <see cref="FalVideoCatalog.NativeToken"/>: a step that
    /// asks for <c>16:9</c> on such a model is refused rather than silently given whatever the input
    /// image's shape produces, which would be a config that says one thing and gets another.
    /// </summary>
    public required IReadOnlyList<string> AllowedRatios { get; init; }

    /// <summary>The schema's own default ratio. Documentation; the platform always states one.</summary>
    public string? VendorDefaultRatio { get; init; }

    /// <summary>
    /// Values the client sends when the caller supplied none, because the schema marks the field
    /// REQUIRED and gives it a default (MiniMax's <c>prompt_expansion_mode</c>). Distinct from a
    /// vendor default the client leaves alone: those are documentation, this is on the wire.
    /// </summary>
    public IReadOnlyDictionary<string, object?> SendByDefault { get; init; } =
        FalVideoCatalog.NoDefaults;

    /// <summary>Where the price in <see cref="PricePerSecondByResolution"/> was read, in the vendor's own words.</summary>
    public required string PriceBasis { get; init; }

    // -- Talking-head endpoints (FalVideoJob.TalkingHead, avatars phase 3). Defaults keep every other entry unchanged.

    /// <summary>The driving-audio field (<c>audio_url</c>), or <c>null</c> on an endpoint that takes no audio.</summary>
    public string? AudioField { get; init; }

    /// <summary>The shortest driving audio this platform sends, in milliseconds (the schema's own floor where it states one).</summary>
    public int MinAudioMs { get; init; }

    /// <summary>
    /// The longest driving audio this platform sends, in milliseconds. Set by the platform where the schema states a
    /// larger ceiling or none, and named in <see cref="LimitsBasis"/>: the clip is as long as the audio, so this is
    /// also the longest clip a take can bill.
    /// </summary>
    public int MaxAudioMs { get; init; }

    /// <summary>
    /// The schema has a duration field. False on a talking-head route, whose clip is as long as its audio: no duration
    /// is sent, and the request's duration must cover the audio (it is what the take is reserved and billed at).
    /// </summary>
    public bool SendsDuration { get; init; } = true;

    /// <summary>
    /// The endpoint has no ratio field but the canvas follows the first frame, which the platform makes itself (the
    /// avatar's still is made at the step's ratio): the ratio is validated against <see cref="AllowedRatios"/> and
    /// NOT sent, instead of the <see cref="FalVideoCatalog.NativeToken"/> rule.
    /// </summary>
    public bool RatioFromFirstFrame { get; init; }

    /// <summary>Where the platform-set audio bounds came from, when they are not the schema's own.</summary>
    public string? LimitsBasis { get; init; }

    // -- Reference-to-video endpoints (FalVideoJob.ReferenceToVideo). Defaults keep every other entry unchanged.

    /// <summary>
    /// The reference-pictures array field (<c>reference_image_urls</c> on Wan, <c>image_urls</c> on veo), or
    /// <c>null</c> on an endpoint that takes no reference pictures.
    /// </summary>
    public string? ReferenceImagesField { get; init; }

    /// <summary>
    /// The most reference pictures this platform sends: the schema's own <c>maxItems</c> where it states one, else a
    /// platform ceiling named in <see cref="LimitsBasis"/>.
    /// </summary>
    public int MaxReferenceImages { get; init; }

    /// <summary>The model page this was read from.</summary>
    public string SourceUrl => "https://fal.ai/models/" + Id;
}

/// <summary>
/// The video endpoints the platform can call through fal.ai: <b>image-to-video first</b> (the
/// first-frame image is what this wave is for), text-to-video alongside, newest model families first,
/// each with its documented alternatives.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every entry was transcribed from the endpoint's own OpenAPI schema and pricing sentence on
/// 2026-10-16</b> (see <c>docs/fal-ai-models.md</c>), the same discipline as
/// <see cref="FalImageCatalog"/>. Adding one is a data entry here, a priced entry in
/// <c>ModelPriceCatalog.FalVideos</c>, and a test - never a guess.
/// </para>
/// <para>
/// <b>Two tokens are the platform's, not the vendor's, and they are named.</b>
/// <see cref="NativeToken"/> is what a step writes for an axis the endpoint does not have: Kling v3
/// turbo/pro publish no resolution field at all (and one flat per-second price), and Vidu Q4, MiniMax
/// H3 Max Turbo and Grok publish no aspect-ratio field (the canvas follows the input image). The
/// client then sends NOTHING for that axis, and the token is what makes that explicit in the step's
/// own configuration instead of being an invisible omission. A resolution the vendor ignores is never
/// invented, and a ratio it ignores is never claimed.
/// </para>
/// <para>
/// <b>Prices are the CONSERVATIVE reading.</b> Where a vendor page shows a promotional rate and the
/// rate after it (Vidu Q4 is 30% off until 2026-11-30; MiniMax H3 Max Turbo is 40% off to
/// 2026-10-15), the table takes the post-promotion figure and the basis names both, so the budget
/// gate can never under-charge a take. That matches the image catalogue's FLUX.3 rows.
/// </para>
/// <para>
/// <b>Deliberately absent.</b> <c>bytedance/seedance-2.5/*</c> (its only per-second figure is the
/// vendor's own "roughly" figure derived from a token formula, and a per-second card cannot express a
/// token count) and every <c>lightricks/ltx-*</c> endpoint (LTX-2's open-weights licence caps free
/// commercial use at US$10M annual revenue, the same reason the ComfyUI arm refuses LTX-2). Neither is
/// a judgement about quality; both are the pricing rule this catalogue refuses to bend.
/// </para>
/// </remarks>
public sealed class FalVideoCatalog
{
    /// <summary>
    /// The one token standing for "the vendor decides this axis, and nothing is sent": the resolution
    /// tier of a flat-priced endpoint with no resolution field, and the ratio of an endpoint whose
    /// canvas follows the input image.
    /// </summary>
    public const string NativeToken = "native";

    /// <summary>The empty <see cref="FalVideoEndpoint.SendByDefault"/> value.</summary>
    internal static readonly IReadOnlyDictionary<string, object?> NoDefaults =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    private readonly Dictionary<string, FalVideoEndpoint> _byId;

    private FalVideoCatalog()
    {
        All = Build();
        _byId = All.ToDictionary(e => e.Id, StringComparer.Ordinal);
    }

    /// <summary>Every endpoint, defaults first within a direction.</summary>
    public IReadOnlyList<FalVideoEndpoint> All { get; }

    /// <summary>
    /// The shared instance. Declared AFTER <see cref="NoDefaults"/> on purpose: static initializers run
    /// in textual order, and a catalogue built before it exists would capture a null default.
    /// </summary>
    public static FalVideoCatalog Instance { get; } = new();

    /// <summary>The endpoint with this id, or <c>null</c> when no schema has been read for it.</summary>
    public FalVideoEndpoint? Find(string? id) =>
        id is not null && _byId.TryGetValue(id, out FalVideoEndpoint? endpoint) ? endpoint : null;

    /// <summary>The default endpoint for a direction.</summary>
    public FalVideoEndpoint DefaultFor(FalVideoJob job) => All.Single(e => e.Job == job && e.IsDefault);

    /// <summary>The documented alternatives for a direction (everything but the default).</summary>
    public IReadOnlyList<FalVideoEndpoint> AlternativesFor(FalVideoJob job) =>
        [.. All.Where(e => e.Job == job && !e.IsDefault)];

    /// <summary>The endpoint ids of a direction, for a refusal sentence an operator can act on.</summary>
    public string IdsFor(FalVideoJob job) =>
        string.Join(", ", All.Where(e => e.Job == job).Select(e => e.Id));

    /// <summary>Every endpoint id, for a refusal that has no direction to narrow by.</summary>
    public string AllIds => string.Join(", ", All.Select(e => e.Id));

    // ------------------------------------------------------------------ the catalogue

    private static IReadOnlyList<FalVideoEndpoint> Build() =>
    [
        // ================================================================ Image-to-video

        // fal's newest image-to-video family, and the wave's default. The only endpoint here whose
        // schema names base64 explicitly ("URL or base64 data URI"), and the only one with five price
        // tiers including 2K and 4K.
        new()
        {
            Id = "fal-ai/vidu/q4/image-to-video", Job = FalVideoJob.ImageToVideo, IsDefault = true,
            Title = "Vidu Q4 image-to-video",
            FirstFrameField = "image_url", FirstFrameRequired = true,
            PromptMaxLength = 5000,
            MinDurationSeconds = 3, MaxDurationSeconds = 16,
            ResolutionField = "resolution",
            Resolutions = ["540p", "720p", "1080p", "2K", "4K"], VendorDefaultResolution = "720p",
            // No aspect-ratio field: the canvas follows the first frame.
            AllowedRatios = [NativeToken],
            PricePerSecondByResolution = new Dictionary<string, decimal>
            {
                ["540p"] = 0.045m, ["720p"] = 0.095m, ["1080p"] = 0.12m, ["2K"] = 0.19m, ["4K"] = 0.39m
            },
            PriceBasis = "published vendor price, fal.ai/models/fal-ai/vidu/q4/image-to-video, read 2026-10-16: " +
                         "\"For every second of video you generate, you will be charged $0.0315 at 540p, $0.0665 at " +
                         "720p, $0.084 at 1080p, $0.133 at 2K or $0.273 at 4K. Note: these are promotional rates, " +
                         "30% off until November 30, after which 540p is $0.045/second, 720p is $0.095/second, " +
                         "1080p is $0.12/second, 2K is $0.19/second and 4K is $0.39/second.\" - the POST-promotion " +
                         "figures are the cards, so a take is never under-charged"
        },
        new()
        {
            Id = "minimax/h3-max-turbo/image-to-video", Job = FalVideoJob.ImageToVideo,
            Title = "MiniMax H3 Max Turbo image-to-video",
            FirstFrameField = "image_url", LastFrameField = "end_image_url",
            PromptRequired = true, PromptMaxLength = 50000,
            // The schema says a number from 0.92; the platform's own duration is an integer number of
            // seconds, so the ceiling of the vendor's floor is 1.
            MinDurationSeconds = 1, MaxDurationSeconds = 15,
            ResolutionField = "resolution",
            Resolutions = ["480P", "768P", "1080P"], VendorDefaultResolution = "768P",
            AllowedRatios = [NativeToken],
            SendByDefault = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                // Required by the schema, with its own documented default. The client sends the default
                // rather than leaving a required field absent; the vendor's "quality" mode spends up to
                // ~30 s rewriting the prompt and is NOT selected for the operator.
                ["prompt_expansion_mode"] = "balanced"
            },
            PricePerSecondByResolution = new Dictionary<string, decimal>
            {
                ["480P"] = 0.025m, ["768P"] = 0.04m, ["1080P"] = 0.08m
            },
            PriceBasis = "published vendor price, fal.ai/models/minimax/h3-max-turbo/image-to-video, read 2026-10-16: " +
                         "\"Video costs $0.015 per second at 480p, $0.024 per second at 768p, and $0.048 per second at " +
                         "1080p. Note: these are promotional rates, 40% off for a limited time. The discount ends " +
                         "October 15, after which 480p is $0.025/second, 768p is $0.04/second, and 1080p is " +
                         "$0.08/second.\" - the POST-discount figures are the cards (the promotion ended), matching " +
                         "ModelPriceCatalog's MiniMax video rows"
        },
        new()
        {
            Id = "fal-ai/kling-video/v3/turbo/pro/image-to-video", Job = FalVideoJob.ImageToVideo,
            Title = "Kling v3 Turbo Pro image-to-video (flat price, no resolution field)",
            FirstFrameField = "image_url", FirstFrameRequired = true,
            PromptMaxLength = 3072,
            DurationForm = FalDurationForm.StringNumber,
            MinDurationSeconds = 3, MaxDurationSeconds = 15,
            // NO resolution field in the schema at all, and one flat price per second: the output size
            // follows the first-frame image.
            ResolutionField = null, Resolutions = [NativeToken],
            AllowedRatios = [NativeToken],
            PricePerSecondByResolution = new Dictionary<string, decimal> { [NativeToken] = 0.14m },
            PriceBasis = "published vendor price, fal.ai/models/fal-ai/kling-video/v3/turbo/pro/image-to-video, " +
                         "read 2026-10-16: \"For every second of video you generate, you will be charged $0.14. For " +
                         "example, a 5s video will cost $0.70.\" - one flat rate, so the single card is exact"
        },
        new()
        {
            Id = "alibaba/wan-3.0/image-to-video", Job = FalVideoJob.ImageToVideo,
            Title = "Wan 3.0 image-to-video",
            FirstFrameField = "start_image_url", FirstFrameRequired = true, LastFrameField = "end_image_url",
            PromptMaxLength = 20000,
            MinDurationSeconds = 2, MaxDurationSeconds = 30,
            ResolutionField = "resolution",
            Resolutions = ["480p", "720p", "1080p"], VendorDefaultResolution = "1080p",
            RatioField = "aspect_ratio",
            AllowedRatios = ["adaptive", "16:9", "4:3", "1:1", "3:4", "9:16"], VendorDefaultRatio = "adaptive",
            PricePerSecondByResolution = new Dictionary<string, decimal>
            {
                ["480p"] = 0.05m, ["720p"] = 0.10m, ["1080p"] = 0.20m
            },
            PriceBasis = "published vendor price, fal.ai/models/alibaba/wan-3.0/image-to-video, read 2026-10-16: " +
                         "\"For every second of video you generate, you will be charged $0.05 480p, $0.10 720p, or " +
                         "$0.20 1080p. For example, a 5s video at 720p will cost $0.50. Pricing is subjected to " +
                         "change.\""
        },
        new()
        {
            Id = "blackforestlabs/flux-3/first-last-frame-to-video", Job = FalVideoJob.ImageToVideo,
            Title = "FLUX.3 first-last-frame-to-video (both frames required)",
            FirstFrameField = "start_image_url", FirstFrameRequired = true,
            LastFrameField = "end_image_url", LastFrameRequired = true,
            PromptRequired = true,
            // The enum is 5..20 inclusive and states no other bound: "An explicit duration is required to
            // place the end frame".
            MinDurationSeconds = 5, MaxDurationSeconds = 20,
            ResolutionField = "resolution",
            Resolutions = ["720p", "1080p"], VendorDefaultResolution = "720p",
            RatioField = "aspect_ratio",
            AllowedRatios = ["auto", "21:9", "2:1", "16:9", "4:3", "1:1", "3:4", "9:16"], VendorDefaultRatio = "auto",
            PricePerSecondByResolution = new Dictionary<string, decimal> { ["720p"] = 0.17m, ["1080p"] = 0.29m },
            PriceBasis = "published vendor price, fal.ai/models/blackforestlabs/flux-3/first-last-frame-to-video, " +
                         "read 2026-10-16: \"Your request will be charged at 0.17 $ per second of generated video at " +
                         "720p, and 0.29 $ per second at 1080p.\""
        },
        new()
        {
            Id = "fal-ai/veo3.1/fast/image-to-video", Job = FalVideoJob.ImageToVideo,
            Title = "Veo 3.1 Fast image-to-video (with audio)",
            FirstFrameField = "image_url", FirstFrameRequired = true,
            PromptRequired = true, PromptMaxLength = 20000,
            DurationForm = FalDurationForm.StringSeconds,
            MinDurationSeconds = 4, MaxDurationSeconds = 8,
            AllowedDurations = ["4s", "6s", "8s"],
            ResolutionField = "resolution",
            Resolutions = ["720p", "1080p", "4k"], VendorDefaultResolution = "720p",
            RatioField = "aspect_ratio",
            AllowedRatios = ["auto", "16:9", "9:16"], VendorDefaultRatio = "auto",
            // generate_audio is not forwarded and the schema defaults it to true, so the WITH-AUDIO
            // figure is the card: the platform must not assume a rate the request does not buy.
            PricePerSecondByResolution = new Dictionary<string, decimal>
            {
                ["720p"] = 0.15m, ["1080p"] = 0.15m, ["4k"] = 0.35m
            },
            PriceBasis = "published vendor price, fal.ai/models/fal-ai/veo3.1/fast/image-to-video, read 2026-10-16: " +
                         "\"For every second of video you generate you will be charged $0.10 without audio or $0.15 " +
                         "with audio for 720p or 1080p. At 4k, you will be charged $0.30 per second without audio, or " +
                         "$0.35 with.\" - generate_audio defaults to true and is not forwarded, so the WITH-AUDIO " +
                         "figures are the cards"
        },
        new()
        {
            Id = "xai/grok-imagine-video/v1.5/lite/image-to-video", Job = FalVideoJob.ImageToVideo,
            Title = "Grok Imagine Video 1.5 Lite image-to-video",
            FirstFrameField = "image_url", FirstFrameRequired = true,
            PromptRequired = true, PromptMaxLength = 4096,
            MinDurationSeconds = 1, MaxDurationSeconds = 15,
            ResolutionField = "resolution",
            Resolutions = ["480p", "720p", "1080p"], VendorDefaultResolution = "720p",
            AllowedRatios = [NativeToken],
            PricePerSecondByResolution = new Dictionary<string, decimal>
            {
                ["480p"] = 0.02m, ["720p"] = 0.03m, ["1080p"] = 0.14m
            },
            PriceBasis = "published vendor price, fal.ai/models/xai/grok-imagine-video/v1.5/lite/image-to-video, " +
                         "read 2026-10-16: \"Priced per second of output video and per input image, by resolution: " +
                         "480p at $0.02/sec, 720p at $0.03/sec, 1080p at $0.14/sec. Each input image costs $0.01.\" - " +
                         "the per-second figures are the cards; the per-input-image surcharge is NOT expressible " +
                         "here, so an image-to-video take on this endpoint is under-charged by $0.01"
        },

        // ================================================================ Text-to-video

        // The t2v default. Its schema declares required: [] - which is why the video probe sends a body
        // that is not JSON rather than an empty one: an empty body here IS a valid request and would be
        // generated and billed.
        new()
        {
            Id = "fal-ai/kling-video/v3/turbo/pro/text-to-video", Job = FalVideoJob.TextToVideo, IsDefault = true,
            Title = "Kling v3 Turbo Pro text-to-video (flat price, no resolution field)",
            FirstFrameField = null,
            // Its schema declares required: [] - every input optional. The PLATFORM still requires a
            // prompt: with neither a prompt nor a storyboard the vendor's own default is the request.
            PromptRequired = true,
            PromptMaxLength = 3072,
            DurationForm = FalDurationForm.StringNumber,
            MinDurationSeconds = 3, MaxDurationSeconds = 15,
            ResolutionField = null, Resolutions = [NativeToken],
            RatioField = "aspect_ratio",
            AllowedRatios = ["16:9", "9:16", "1:1"], VendorDefaultRatio = "16:9",
            PricePerSecondByResolution = new Dictionary<string, decimal> { [NativeToken] = 0.14m },
            PriceBasis = "published vendor price, fal.ai/models/fal-ai/kling-video/v3/turbo/pro/text-to-video, " +
                         "read 2026-10-16: \"For every second of video you generate, you will be charged $0.14 . For " +
                         "example, a 5s video will cost $0.70.\" - one flat rate, so the single card is exact"
        },
        new()
        {
            Id = "minimax/h3-max-turbo/text-to-video", Job = FalVideoJob.TextToVideo,
            Title = "MiniMax H3 Max Turbo text-to-video",
            FirstFrameField = null,
            PromptRequired = true, PromptMaxLength = 50000,
            MinDurationSeconds = 1, MaxDurationSeconds = 15,
            ResolutionField = "resolution",
            Resolutions = ["480P", "768P", "1080P"], VendorDefaultResolution = "768P",
            RatioField = "aspect_ratio",
            AllowedRatios = ["21:9", "16:9", "4:3", "1:1", "3:4", "9:16"], VendorDefaultRatio = "16:9",
            SendByDefault = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["prompt_expansion_mode"] = "balanced"
            },
            PricePerSecondByResolution = new Dictionary<string, decimal>
            {
                ["480P"] = 0.025m, ["768P"] = 0.04m, ["1080P"] = 0.08m
            },
            PriceBasis = "published vendor price, fal.ai/models/minimax/h3-max-turbo/text-to-video, read 2026-10-16: " +
                         "the same sentence as the image-to-video route (\"$0.015 per second at 480p, $0.024 per second " +
                         "at 768p, and $0.048 per second at 1080p ... after which 480p is $0.025/second, 768p is " +
                         "$0.04/second, and 1080p is $0.08/second\"), post-discount figures as the cards"
        },
        new()
        {
            Id = "alibaba/wan-3.0/text-to-video", Job = FalVideoJob.TextToVideo,
            Title = "Wan 3.0 text-to-video",
            FirstFrameField = null,
            PromptRequired = true, PromptMaxLength = 20000,
            MinDurationSeconds = 2, MaxDurationSeconds = 30,
            ResolutionField = "resolution",
            Resolutions = ["480p", "720p", "1080p"], VendorDefaultResolution = "1080p",
            RatioField = "aspect_ratio",
            AllowedRatios = ["adaptive", "16:9", "4:3", "1:1", "3:4", "9:16"], VendorDefaultRatio = "adaptive",
            PricePerSecondByResolution = new Dictionary<string, decimal>
            {
                ["480p"] = 0.05m, ["720p"] = 0.10m, ["1080p"] = 0.20m
            },
            PriceBasis = "published vendor price, fal.ai/models/alibaba/wan-3.0/text-to-video, read 2026-10-16: the " +
                         "same sentence as the image-to-video route (\"$0.05 480p, $0.10 720p, or $0.20 1080p\")"
        },
        new()
        {
            Id = "fal-ai/veo3.1", Job = FalVideoJob.TextToVideo,
            Title = "Veo 3.1 text-to-video (with audio)",
            FirstFrameField = null,
            PromptRequired = true, PromptMaxLength = 20000,
            DurationForm = FalDurationForm.StringSeconds,
            MinDurationSeconds = 4, MaxDurationSeconds = 8,
            AllowedDurations = ["4s", "6s", "8s"],
            ResolutionField = "resolution",
            Resolutions = ["720p", "1080p", "4k"], VendorDefaultResolution = "720p",
            RatioField = "aspect_ratio",
            AllowedRatios = ["16:9", "9:16"], VendorDefaultRatio = "16:9",
            PricePerSecondByResolution = new Dictionary<string, decimal>
            {
                ["720p"] = 0.40m, ["1080p"] = 0.40m, ["4k"] = 0.60m
            },
            PriceBasis = "published vendor price, fal.ai/models/fal-ai/veo3.1, read 2026-10-16: \"For every second of " +
                         "video you generate you will be charged $0.20 without audio or $0.40 with audio for 720p or " +
                         "1080p. At 4k, you will be charged $0.40 per second without audio, or $0.60 with.\" - " +
                         "generate_audio defaults to true and is not forwarded, so the WITH-AUDIO figures are the cards"
        },

        // ================================================================ Reference-to-video (subjects, 2026-11-16)

        // Every entry below was read on 2026-11-16 from the endpoint's own OpenAPI schema and the pricing sentence of
        // its model page. Only the reference PICTURES are sent. Candidates that were read and NOT catalogued, each
        // for a price that cannot be stated per second without a guess, are listed in docs/fal-ai-models.md section 15.

        // The default: the widest (10 pictures), the cheapest tier, and a schema that states its own limits
        // (Wan30ReferenceToVideoInput: reference_image_urls maxItems 10; duration integer 2..30 or null; resolution
        // enum; aspect_ratio enum). Its schema marks NOTHING required; the platform still requires a prompt and at
        // least one picture, because without either the request is not one this path makes. reference_video_urls
        // ("Input video duration is billed in addition to output duration", "Each clip must be at least 16 fps") and
        // reference_audio_urls (no price stated) are NOT sent: the first needs a frame rate the platform does not
        // store, the second has no stated price.
        new()
        {
            Id = "alibaba/wan-3.0/reference-to-video", Job = FalVideoJob.ReferenceToVideo, IsDefault = true,
            Title = "Wan 3.0 reference-to-video (up to 10 reference pictures)",
            FirstFrameField = null,
            ReferenceImagesField = "reference_image_urls", MaxReferenceImages = 10,
            PromptRequired = true, PromptMaxLength = 20000,
            MinDurationSeconds = 2, MaxDurationSeconds = 30,
            ResolutionField = "resolution",
            Resolutions = ["480p", "720p", "1080p"], VendorDefaultResolution = "1080p",
            RatioField = "aspect_ratio",
            AllowedRatios = ["adaptive", "16:9", "4:3", "1:1", "3:4", "9:16"], VendorDefaultRatio = "adaptive",
            LimitsBasis = "schema: reference_image_urls maxItems 10 (\"Up to 10 reference image URLs.\"); reference videos " +
                          "and reference audio are never sent, so the per-second card is the whole price",
            PricePerSecondByResolution = new Dictionary<string, decimal>
            {
                ["480p"] = 0.05m, ["720p"] = 0.10m, ["1080p"] = 0.20m
            },
            PriceBasis = "published vendor price, fal.ai/models/alibaba/wan-3.0/reference-to-video, read 2026-11-16: " +
                         "\"For every second of video you generate, you will be charged $0.05 480p, $0.10 720p, or " +
                         "$0.20 1080p. For example, a 5s video at 720p will cost $0.50. Pricing is subject to change.\" - " +
                         "the schema adds that INPUT VIDEO duration is billed on top; the platform sends no reference " +
                         "video, so the figures are exact"
        },

        // Veo 3.1 with reference pictures (Veo31ReferenceToVideoInput: required prompt, image_urls; resolution enum
        // 720p/1080p/4k; aspect_ratio enum 16:9/9:16; duration a STRING defaulting to "8s" with no enum). The schema
        // states no item limit and no other duration, so the platform sends at most 3 pictures (the count of the
        // vendor's own example request) and only "8s".
        new()
        {
            Id = "fal-ai/veo3.1/reference-to-video", Job = FalVideoJob.ReferenceToVideo,
            Title = "Veo 3.1 reference-to-video (with audio, up to 3 reference pictures)",
            FirstFrameField = null,
            ReferenceImagesField = "image_urls", MaxReferenceImages = 3,
            PromptRequired = true, PromptMaxLength = 20000,
            DurationForm = FalDurationForm.StringSeconds,
            MinDurationSeconds = 8, MaxDurationSeconds = 8,
            AllowedDurations = ["8s"],
            ResolutionField = "resolution",
            Resolutions = ["720p", "1080p", "4k"], VendorDefaultResolution = "720p",
            RatioField = "aspect_ratio",
            AllowedRatios = ["16:9", "9:16"], VendorDefaultRatio = "16:9",
            LimitsBasis = "schema states no maxItems for image_urls and no duration enum (default \"8s\"); the platform " +
                          "sends at most 3 pictures (the vendor's own example request) and only \"8s\" until a live call " +
                          "measures more",
            PricePerSecondByResolution = new Dictionary<string, decimal>
            {
                ["720p"] = 0.40m, ["1080p"] = 0.40m, ["4k"] = 0.60m
            },
            PriceBasis = "published vendor price, fal.ai/models/fal-ai/veo3.1/reference-to-video, read 2026-11-16: " +
                         "\"For every second of video you generate you will be charged $0.20 without audio or $0.40 with " +
                         "audio for 720p or 1080p. At 4k, you will be charged $0.40 per second without audio, or $0.60 " +
                         "with audio. For example, 8 second video at 1080p with audio on will cost $3.20\" - " +
                         "generate_audio defaults to true and is not forwarded, so the WITH-AUDIO figures are the cards"
        },

        // The Fast tier (Veo31FastReferenceToVideoInput: the same fields). Its pricing sentence states ONE pair of
        // figures and names no resolution, while the schema also lists 4k and every other veo route charges more at
        // 4k: the platform offers 720p and 1080p only, so a take is never under-charged by a 4k rate the page does
        // not state.
        new()
        {
            Id = "fal-ai/veo3.1/fast/reference-to-video", Job = FalVideoJob.ReferenceToVideo,
            Title = "Veo 3.1 Fast reference-to-video (with audio, up to 3 reference pictures)",
            FirstFrameField = null,
            ReferenceImagesField = "image_urls", MaxReferenceImages = 3,
            PromptRequired = true, PromptMaxLength = 20000,
            DurationForm = FalDurationForm.StringSeconds,
            MinDurationSeconds = 8, MaxDurationSeconds = 8,
            AllowedDurations = ["8s"],
            ResolutionField = "resolution",
            Resolutions = ["720p", "1080p"], VendorDefaultResolution = "720p",
            RatioField = "aspect_ratio",
            AllowedRatios = ["16:9", "9:16"], VendorDefaultRatio = "16:9",
            LimitsBasis = "schema states no maxItems for image_urls and no duration enum (default \"8s\"); the platform " +
                          "sends at most 3 pictures and only \"8s\"; the schema's 4k tier is NOT offered because the page " +
                          "states no 4k price",
            PricePerSecondByResolution = new Dictionary<string, decimal>
            {
                ["720p"] = 0.15m, ["1080p"] = 0.15m
            },
            PriceBasis = "published vendor price, fal.ai/models/fal-ai/veo3.1/fast/reference-to-video, read 2026-11-16: " +
                         "\"For every second of video you generated, you will be charged $0.10 (audio off) or $0.15 " +
                         "(audio on). For example, a 5s video with audio on will cost $0.75.\" - generate_audio defaults " +
                         "to true and is not forwarded, so the WITH-AUDIO figure is the card; 4k is not offered"
        },

        // ================================================================ Talking head (avatars phase 3)

        // The still-then-animate default. Read 2026-11-06 from the endpoint's OpenAPI schema
        // (H3MaxLipSyncImageToVideoInput: required image_url, audio_url; resolution enum 480P/768P/1080P/2K default
        // 768P; enable_transcription default true; seed; enable_safety_checker) and its llms.txt pricing sentence.
        // Chosen over the shortlist because it is the one route whose schema STATES its input bounds (audio "at least
        // 5 seconds long. Supports up to 15 minutes", image aspect 0.4..2.5) and its cheapest tier is the cheapest
        // published per-second figure on the shortlist ($0.05 at 480P). No prompt, duration or ratio field: the clip
        // is as long as the audio and its canvas follows the image (the avatar's still, made at the step's ratio).
        // The platform caps the audio at 15 s: above 15 s the vendor adds a 1.2x multiplier the per-second card
        // cannot express, so a longer take would be under-charged.
        new()
        {
            Id = "minimax/h3-max/lip-sync/image-to-video", Job = FalVideoJob.TalkingHead, IsDefault = true,
            Title = "MiniMax H3 Max lip sync (talking head from a still and a speech track)",
            FirstFrameField = "image_url", FirstFrameRequired = true,
            PromptField = null,
            AudioField = "audio_url", MinAudioMs = 5_000, MaxAudioMs = 15_000,
            LimitsBasis = "schema: audio_url \"at least 5 seconds long. Supports up to 15 minutes\"; the platform's " +
                          "15 s ceiling keeps every take under the vendor's 1.2x multiplier for videos over 15 seconds",
            SendsDuration = false,
            MinDurationSeconds = 5, MaxDurationSeconds = 15,
            ResolutionField = "resolution",
            Resolutions = ["480P", "768P", "1080P", "2K"], VendorDefaultResolution = "768P",
            RatioFromFirstFrame = true,
            AllowedRatios = ["9:16", "16:9", "1:1", "4:5", "3:4", "4:3"],
            PricePerSecondByResolution = new Dictionary<string, decimal>
            {
                ["480P"] = 0.05m, ["768P"] = 0.08m, ["1080P"] = 0.16m, ["2K"] = 0.32m
            },
            PriceBasis = "published vendor price, fal.ai/models/minimax/h3-max/lip-sync/image-to-video, read " +
                         "2026-11-06: \"Video costs $0.05 per second at 480p, $0.08 per second at 768p, $0.16 per " +
                         "second at 1080p, and $0.32 per second at 2K. A 1.2x multiplier is added to videos over 15 " +
                         "seconds.\" - the platform never sends more than 15 s of audio, so the plain figures are exact"
        },

        // The alternative: one flat price, and a motion prompt. Read 2026-11-06 (KlingVideoAiAvatarV2StandardInput:
        // required image_url, audio_url; prompt default "."; enable_safety_checker). Its schema states NO audio bounds,
        // so the platform sends the same 5..15 s as the default (must measure: the real floor and ceiling).
        new()
        {
            Id = "fal-ai/kling-video/ai-avatar/v2/standard", Job = FalVideoJob.TalkingHead,
            Title = "Kling AI Avatar v2 Standard (talking head, flat price, motion prompt)",
            FirstFrameField = "image_url", FirstFrameRequired = true,
            PromptMaxLength = 2500,
            AudioField = "audio_url", MinAudioMs = 5_000, MaxAudioMs = 15_000,
            LimitsBasis = "schema states no audio bounds; the platform sends 5..15 s (the default's bounds) until a live " +
                          "call measures the vendor's own; the 2500-character prompt cap is the platform's",
            SendsDuration = false,
            MinDurationSeconds = 5, MaxDurationSeconds = 15,
            ResolutionField = null, Resolutions = [NativeToken],
            RatioFromFirstFrame = true,
            AllowedRatios = ["9:16", "16:9", "1:1", "4:5", "3:4", "4:3"],
            PricePerSecondByResolution = new Dictionary<string, decimal> { [NativeToken] = 0.0562m },
            PriceBasis = "published vendor price, fal.ai/models/fal-ai/kling-video/ai-avatar/v2/standard, read " +
                         "2026-11-06: \"Price: $0.0562 per seconds\" - one flat rate, so the single card is exact"
        },
    ];
}
