namespace ReelBolt.Shared.Inference;

/// <summary>The editor job an fal.ai image endpoint serves. Append-only; persisted by name in provenance.</summary>
public enum FalImageJob
{
    /// <summary>Remove what a mask (or a named object) covers. Default: <c>fal-ai/object-removal/mask</c>.</summary>
    Erase,
    /// <summary>Regenerate the masked area from a prompt. Default: <c>fal-ai/flux-pro/v1/fill</c>.</summary>
    Fill,
    /// <summary>Outpaint to larger bounds. Default: <c>fal-ai/bria/expand</c>.</summary>
    Expand,
    /// <summary>Keep the foreground, regenerate the background. Default: <c>fal-ai/bria/background/replace</c>.</summary>
    ReplaceBackground,
    /// <summary>Change the lighting. Default: <c>fal-ai/image-apps-v2/relighting</c>.</summary>
    Relight,
    /// <summary>Raise the resolution. Default: <c>fal-ai/seedvr/upscale/image</c>.</summary>
    Upscale,
    /// <summary>An instruction edit ("make the sky purple"). Default: <c>google/nano-banana-2.1/edit</c>.</summary>
    Instruct,
    /// <summary>Text-to-image. Default: <c>ideogram/v4.5</c>.</summary>
    Generate,
}

/// <summary>The JSON type of an endpoint input field, as its OpenAPI schema declares it.</summary>
public enum FalFieldType
{
    /// <summary>A string, optionally restricted to <see cref="FalImageField.Values"/>.</summary>
    String,
    /// <summary>An integer with no fractional part.</summary>
    Integer,
    /// <summary>Any JSON number.</summary>
    Number,
    /// <summary>A boolean.</summary>
    Boolean,
}

/// <summary>What a mask means to an endpoint, as its schema states it.</summary>
public enum FalMaskPolarity
{
    /// <summary>The endpoint takes no mask.</summary>
    None,
    /// <summary>"White (255) marks the area to act on" - STATED, and the house convention.</summary>
    WhiteActs,
    /// <summary>"Black edits, white preserves" - STATED, and the OPPOSITE of the house convention.</summary>
    WhitePreserves,
    /// <summary>
    /// The schema does not say. The house convention (white = act on) is ASSUMED, and the assumption is
    /// on the "must measure with a real key" list.
    /// </summary>
    NotStated,
}

/// <summary>How an expand endpoint is told where the original goes on the bigger picture.</summary>
public enum FalExpandGeometry
{
    /// <summary>Not an expand endpoint.</summary>
    None,
    /// <summary>
    /// Pixels to add per side (<c>expand_top</c>, ...). The vendor keeps the input at its own size, so the
    /// margins are in the pixels of the picture that is SENT.
    /// </summary>
    Margins,
    /// <summary>
    /// The finished canvas plus the size and top-left position of the original on it
    /// (<c>canvas_size</c>, <c>original_image_size</c>, <c>original_image_location</c>), so the vendor
    /// scales the input itself and the result's geometry is fully stated by the request.
    /// </summary>
    Canvas,
}

/// <summary>Where an endpoint puts its output image(s).</summary>
public enum FalOutputShape
{
    /// <summary><c>images</c>: an array of image objects.</summary>
    ImagesArray,
    /// <summary><c>image</c>: one image object.</summary>
    SingleImage,
}

/// <summary>
/// One input field of an fal endpoint that this client will forward, transcribed from the endpoint's
/// own OpenAPI schema (<c>fal.ai/api/openapi/queue/openapi.json?endpoint_id={id}</c>, 2026-10-10).
/// A field not declared here is REFUSED, never dropped: fal silently ignores a field it does not
/// define, so the caller would receive a plausible result for a request nobody made.
/// </summary>
/// <param name="Name">The field's name on the wire.</param>
/// <param name="Type">Its JSON type.</param>
/// <param name="Required">The schema marks it required (so the client refuses to send without it).</param>
/// <param name="Min">Inclusive lower bound the schema states, for numbers.</param>
/// <param name="Max">Inclusive upper bound the schema states, for numbers.</param>
/// <param name="Values">The closed set the client forwards for a string field. May be a SUBSET of the schema's enum (see <paramref name="Note"/>).</param>
/// <param name="EditorName">A second name callers may use (<c>top</c> for <c>expand_top</c>, <c>factor</c> for <c>upscale_factor</c>).</param>
/// <param name="VendorDefault">The schema's default, as text. Documentation and the billing tier when the field is a price tier.</param>
/// <param name="Note">Why this field is shaped as it is, where that is not obvious from the schema.</param>
/// <param name="SendByDefault">
/// A value the client sends when the caller gave none, because the vendor's own default is a more
/// expensive tier than the rate card prices (Seedream's schema default <c>auto_2K</c> is billed at double
/// the under-1536x1536 rate). Distinct from <paramref name="VendorDefault"/>, which is only documentation.
/// </param>
/// <param name="WrapIn">When set, the value is sent as <c>{ "&lt;WrapIn&gt;": value }</c> (relighting's <c>aspect_ratio.ratio</c>).</param>
public sealed record FalImageField(
    string Name,
    FalFieldType Type,
    bool Required = false,
    double? Min = null,
    double? Max = null,
    IReadOnlyList<string>? Values = null,
    string? EditorName = null,
    string? VendorDefault = null,
    string? Note = null,
    string? WrapIn = null,
    string? SendByDefault = null);

/// <summary>
/// One fal.ai endpoint the thumbnail editor can call, with everything the client needs to build and
/// validate a request for it and to read the answer.
/// </summary>
public sealed record FalImageEndpoint
{
    /// <summary>The endpoint id, which is also its path on the queue host.</summary>
    public required string Id { get; init; }

    /// <summary>Which editor job it serves.</summary>
    public required FalImageJob Job { get; init; }

    /// <summary>A short human label.</summary>
    public required string Title { get; init; }

    /// <summary>The extra fields (beyond the wired inputs below) this client forwards.</summary>
    public required IReadOnlyList<FalImageField> Fields { get; init; }

    /// <summary>The one endpoint the editor uses for <see cref="Job"/> unless told otherwise.</summary>
    public bool IsDefault { get; init; }

    /// <summary>A named preset (a style LoRA), offered next to the general models rather than instead of them.</summary>
    public bool IsPreset { get; init; }

    /// <summary>
    /// A previous generation kept because no newer endpoint serves the job (there is no
    /// <c>ideogram/v4.5/generate-transparent</c>: fal answers 404 for it).
    /// </summary>
    public bool IsOlderGeneration { get; init; }

    /// <summary>The single-image input field (<c>image_url</c>), or <c>null</c> when the input is an array.</summary>
    public string? ImageUrlField { get; init; } = "image_url";

    /// <summary>The array-of-images input field (<c>image_urls</c>); the client sends exactly the one source.</summary>
    public string? ImageUrlsField { get; init; }

    /// <summary>
    /// The most pictures <see cref="ImageUrlsField"/> takes in one request, where the schema states a count;
    /// <c>null</c> when it states none (every catalogued endpoint today: each says "an array" without a count). A
    /// caller that adds optional pictures (further views of an avatar or object) sends no more than this.
    /// </summary>
    public int? MaxInputImages { get; init; }

    /// <summary>The mask URL field, or <c>null</c> when the endpoint takes no mask.</summary>
    public string? MaskUrlField { get; init; }

    /// <summary>What the mask means, when there is one.</summary>
    public FalMaskPolarity MaskPolarity { get; init; } = FalMaskPolarity.None;

    /// <summary>The prompt field, or <c>null</c> when the endpoint documents none (a non-blank prompt is then refused).</summary>
    public string? PromptField { get; init; } = "prompt";

    /// <summary>The client requires a prompt (the schema does, or sending the vendor default would be wrong).</summary>
    public bool PromptRequired { get; init; }

    /// <summary>Shortest prompt the schema allows.</summary>
    public int? PromptMinLength { get; init; }

    /// <summary>Longest prompt the schema allows.</summary>
    public int? PromptMaxLength { get; init; }

    /// <summary>The negative-prompt field, or <c>null</c>.</summary>
    public string? NegativePromptField { get; init; }

    /// <summary>The seed field, or <c>null</c> when the endpoint documents none.</summary>
    public string? SeedField { get; init; }

    /// <summary>Lowest seed the schema allows, if it states one.</summary>
    public long? SeedMin { get; init; }

    /// <summary>Highest seed the schema allows, if it states one.</summary>
    public long? SeedMax { get; init; }

    /// <summary>The image-count field (<c>num_images</c>), or <c>null</c> when the endpoint returns one image.</summary>
    public string? CountField { get; init; }

    /// <summary>The schema's maximum for <see cref="CountField"/>.</summary>
    public int CountMax { get; init; } = 1;

    /// <summary>The aspect-ratio field; its allowed values are the <see cref="Fields"/> entry of the same name.</summary>
    public string? AspectRatioField { get; init; }

    /// <summary>The image-size field (<c>image_size</c>), whose value is a preset name or <c>{width,height}</c>.</summary>
    public string? SizeField { get; init; }

    /// <summary>The preset names <see cref="SizeField"/> accepts.</summary>
    public IReadOnlyList<string>? SizePresets { get; init; }

    /// <summary>When set, an explicit <c>{width,height}</c> must be one of these <c>WxH</c> sizes (ideogram v4.5 lists 36).</summary>
    public IReadOnlyList<string>? SupportedSizes { get; init; }

    /// <summary>The field the client sets to <c>png</c> so layers are not JPEG-degraded, or <c>null</c> when the endpoint has none.</summary>
    public string? OutputFormatField { get; init; }

    /// <summary>The value written to <see cref="OutputFormatField"/>: <c>png</c>, or <c>jpg</c> where the enum spells it so.</summary>
    public string OutputFormatValue { get; init; } = "png";

    /// <summary>Where the output image(s) are.</summary>
    public FalOutputShape Output { get; init; } = FalOutputShape.ImagesArray;

    /// <summary>
    /// The enum field the price is a function of. When set, the call is metered under
    /// <c>{Id}#{value}</c> (<see cref="IBillingModelKeyed"/>) and the rate-card catalogue carries one
    /// priced entry per value.
    /// </summary>
    public string? BillingTierField { get; init; }

    /// <summary>
    /// The page prices the call per megapixel of output with no cap ("$0.05 per megapixel ... rounding up to the
    /// nearest megapixel" and the like), so it is metered at the delivered picture's size as an <c>{n}MP</c> variant.
    /// </summary>
    public bool PricedPerMegapixel { get; init; }

    /// <summary>
    /// The page also prices the INPUT picture's megapixels ("plus $0.015 per extra megapixel of input and output"), so
    /// the metered variant counts the input's megapixels as well as the output's (T-013 rework 1).
    /// </summary>
    public bool BillsInputMegapixels { get; init; }

    /// <summary>
    /// The price is a flat rate per megapixel with no first-unit premium ("$0.05 per megapixel", "$0.001 per
    /// megapixel"), so an <c>{n}MP</c> size with no row of its own is n times the rows' per-megapixel rate, not the
    /// next seeded size up.
    /// </summary>
    public bool LinearPerMegapixel { get; init; }

    /// <summary>How an expand endpoint takes its geometry (<see cref="FalExpandGeometry.None"/> for every other job).</summary>
    public FalExpandGeometry ExpandBy { get; init; } = FalExpandGeometry.None;

    /// <summary><see cref="FalExpandGeometry.Canvas"/>: the field taking <c>[width, height]</c> of the finished picture.</summary>
    public string? CanvasSizeField { get; init; }

    /// <summary><see cref="FalExpandGeometry.Canvas"/>: the field taking <c>[width, height]</c> of the original on the canvas.</summary>
    public string? OriginalImageSizeField { get; init; }

    /// <summary><see cref="FalExpandGeometry.Canvas"/>: the field taking <c>[x, y]</c> of the original's top-left corner.</summary>
    public string? OriginalImageLocationField { get; init; }

    /// <summary>
    /// The largest finished picture the endpoint documents, in pixels (width x height), or <c>null</c> when it
    /// states none. See <see cref="MaxOutputPixelsExclusive"/> and <see cref="LimitsBasis"/>.
    /// </summary>
    public long? MaxOutputPixels { get; init; }

    /// <summary>The documented output limit is "less than" rather than "at most".</summary>
    public bool MaxOutputPixelsExclusive { get; init; }

    /// <summary>
    /// The largest INPUT picture the endpoint takes as it is, in pixels, or <c>null</c> when it states none.
    /// A larger input is not sent: the editor sends a copy at the size the layer appears in the design.
    /// </summary>
    public long? MaxInputPixels { get; init; }

    /// <summary>Where <see cref="MaxOutputPixels"/> and <see cref="MaxInputPixels"/> were read.</summary>
    public string? LimitsBasis { get; init; }

    /// <summary>The per-side cap of a <see cref="FalExpandGeometry.Margins"/> endpoint (its <c>expand_top</c> maximum).</summary>
    public int? MaxMarginPx => ExpandBy == FalExpandGeometry.Margins && FieldNamed("expand_top")?.Max is { } max ? (int)max : null;

    /// <summary>A cross-field rule over the built body; returns a refusal sentence, or <c>null</c> when satisfied.</summary>
    internal Func<IReadOnlyDictionary<string, object?>, string?>? Rule { get; init; }

    /// <summary>The model page this was read from.</summary>
    public string SourceUrl => "https://fal.ai/models/" + Id;

    /// <summary>The field of this endpoint named <paramref name="nameOrEditorName"/>, or <c>null</c>.</summary>
    public FalImageField? FieldNamed(string nameOrEditorName) =>
        Fields.FirstOrDefault(f => f.Name == nameOrEditorName || f.EditorName == nameOrEditorName);
}

/// <summary>
/// The endpoints the thumbnail editor can call through fal.ai, one default per job plus documented
/// alternatives. <b>Every entry was transcribed from the endpoint's own OpenAPI schema on 2026-10-10</b>
/// (see <c>docs/fal-ai-models.md</c>); adding one is a data entry here, a priced entry in
/// <c>ModelPriceCatalog</c>, and a test — never a guess.
/// </summary>
/// <remarks>
/// <para>
/// <b>Newest-first.</b> The defaults are the newest family fal serves for each job: Nano Banana 2.1 for
/// instruction edits, Ideogram 4.5 for text-to-image (the typography pick), FLUX.3, GPT Image 2.5 and
/// Seedream 5 Pro as alternatives. Where no newer endpoint exists the older one stays and says so
/// (<see cref="FalImageEndpoint.IsOlderGeneration"/>): there is no <c>fal-ai/seedvr2/...</c>, no
/// <c>fal-ai/flux-3-pro/outpaint</c> and no <c>ideogram/v4.5/generate-transparent</c> (each answers 404).
/// </para>
/// <para>
/// <b>Expand is the one job whose default is not the newest family</b>: <c>fal-ai/bria/expand</c> rather
/// than <c>fal-ai/flux-2-pro/outpaint</c>, because the editor's expand is mostly "make this picture fit a
/// shape" and Bria takes the finished canvas (up to 25 MP, against FLUX.2's 4 MP), an optional prompt and a
/// flat $0.04 the rate card prices exactly, where FLUX.2 takes margins only, no prompt, and is priced per
/// input-and-output megapixel (a 4 MP outpaint of a 2 MP copy is about $0.10, metered at its $0.03 "from"
/// card). A row that names FLUX.2 keeps using it.
/// </para>
/// <para>
/// <b>What is deliberately not forwarded.</b> <c>sync_mode</c> (it returns a data URI, which this
/// client's SSRF-guarded downloader cannot and should not fetch), every safety-checker override
/// ("authorized callers only"), web search (billed extra), and the 4K / 2K tiers whose price the
/// vendor does not tabulate - each of those would make the rate card under-charge or the client
/// misbehave. A restricted enum says so in its <see cref="FalImageField.Note"/>.
/// </para>
/// </remarks>
public sealed class FalImageCatalog
{
    private readonly Dictionary<string, FalImageEndpoint> _byId;

    private FalImageCatalog()
    {
        All = Build();
        _byId = All.ToDictionary(e => e.Id, StringComparer.Ordinal);
    }

    /// <summary>Every endpoint, defaults first within a job.</summary>
    public IReadOnlyList<FalImageEndpoint> All { get; }

    /// <summary>The endpoint with this id, or <c>null</c> when no schema has been read for it.</summary>
    public FalImageEndpoint? Find(string? id) =>
        id is not null && _byId.TryGetValue(id, out FalImageEndpoint? endpoint) ? endpoint : null;

    /// <summary>The default endpoint for a job.</summary>
    public FalImageEndpoint DefaultFor(FalImageJob job) => All.Single(e => e.Job == job && e.IsDefault);

    /// <summary>The documented alternatives for a job (everything but the default).</summary>
    public IReadOnlyList<FalImageEndpoint> AlternativesFor(FalImageJob job) =>
        [.. All.Where(e => e.Job == job && !e.IsDefault)];

    /// <summary>The job a <see cref="ImageOperation"/> is served by.</summary>
    public static FalImageJob JobFor(ImageOperation operation) => operation switch
    {
        ImageOperation.Expand => FalImageJob.Expand,
        ImageOperation.ReplaceBackground => FalImageJob.ReplaceBackground,
        ImageOperation.Relight => FalImageJob.Relight,
        ImageOperation.Upscale => FalImageJob.Upscale,
        _ => throw new NotSupportedException("No fal.ai job is defined for operation " + operation + ".")
    };

    // ------------------------------------------------------------------ field helpers

    private static FalImageField Str(string name, string[] values, string? vendorDefault = null, string? note = null,
        string? editorName = null, bool required = false, string? sendByDefault = null) =>
        new(name, FalFieldType.String, required, Values: values, VendorDefault: vendorDefault, Note: note,
            EditorName: editorName, SendByDefault: sendByDefault);

    private static FalImageField Url(string name, string? note = null) =>
        new(name, FalFieldType.String, Note: note);

    private static FalImageField Bool(string name, string? vendorDefault = null) =>
        new(name, FalFieldType.Boolean, VendorDefault: vendorDefault);

    private static FalImageField Int(string name, double? min, double? max, string? editorName = null, string? vendorDefault = null) =>
        new(name, FalFieldType.Integer, Min: min, Max: max, EditorName: editorName, VendorDefault: vendorDefault);

    private static FalImageField Num(string name, double? min, double? max, string? editorName = null, string? vendorDefault = null) =>
        new(name, FalFieldType.Number, Min: min, Max: max, EditorName: editorName, VendorDefault: vendorDefault);

    // ------------------------------------------------------------------ vocabularies (verbatim)

    private static readonly string[] OutputJpegPng = ["jpeg", "png"];
    private static readonly string[] OutputJpegPngWebp = ["jpeg", "png", "webp"];

    private static readonly string[] SizePresets6 =
        ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9"];

    /// <summary>nano-banana-2.1's <c>aspect_ratio</c> (including its four extreme ratios).</summary>
    private static readonly string[] AspectNano =
        ["auto", "21:9", "16:9", "3:2", "4:3", "5:4", "1:1", "4:5", "3:4", "2:3", "9:16", "4:1", "1:4", "8:1", "1:8"];

    /// <summary>FLUX.3's <c>aspect_ratio</c>.</summary>
    private static readonly string[] AspectFlux3 =
        ["auto", "21:9", "2:1", "16:9", "3:2", "7:5", "4:3", "5:4", "1:1", "4:5", "3:4", "5:7", "2:3", "9:16", "1:2"];

    /// <summary>ideogram v3 generate-transparent's <c>aspect_ratio</c>.</summary>
    private static readonly string[] AspectIdeogramV3 =
        ["1:3", "3:1", "1:2", "2:1", "9:16", "16:9", "10:16", "16:10", "2:3", "3:2", "3:4", "4:3", "4:5", "5:4", "1:1"];

    /// <summary>The 36 explicit sizes ideogram/v4.5 accepts for <c>image_size</c> (verbatim from its description).</summary>
    private static readonly string[] IdeogramV45Sizes =
    [
        "1024x1024", "1024x3072", "1120x896", "1152x2944", "1152x864", "1248x3328", "1248x832", "1280x3072",
        "1280x720", "1280x800", "1296x3168", "1440x2560", "1440x2880", "1440x720", "1600x2560", "1664x2496",
        "1728x2304", "1792x2240", "2048x2048", "2240x1792", "2304x1728", "2496x1664", "2560x1440", "2560x1600",
        "2880x1440", "2944x1152", "3072x1024", "3072x1280", "3168x1296", "3328x1248", "720x1280", "720x1440",
        "800x1280", "832x1248", "864x1152", "896x1120"
    ];

    private static readonly string[] LightingStyles =
    [
        "natural", "studio", "golden_hour", "blue_hour", "dramatic", "soft", "hard", "backlight", "side_light",
        "front_light", "rim_light", "sunset", "sunrise", "neon", "candlelight", "moonlight", "spotlight", "ambient"
    ];

    private static readonly string[] FiboLightTypes =
    [
        "midday", "blue hour light", "low-angle sunlight", "sunrise light", "spotlight on subject", "overcast light",
        "soft overcast daylight lighting", "cloud-filtered lighting", "fog-diffused lighting", "moonlight lighting",
        "starlight nighttime", "soft bokeh lighting", "harsh studio lighting"
    ];

    private const string TierNote = "A price tier: the call is metered under '{endpoint}#{value}' and each value has its own rate card.";

    private const string Gpt25Note =
        "'auto' is not forwarded: the vendor's published price table has no 'auto' column, so it could not be priced.";

    /// <summary>
    /// The shared instance. Declared AFTER the vocabulary arrays above on purpose: static initializers
    /// run in textual order, and building the catalogue before they exist would capture nulls.
    /// </summary>
    public static FalImageCatalog Instance { get; } = new();

    // ------------------------------------------------------------------ the catalogue

    private static IReadOnlyList<FalImageEndpoint> Build() =>
    [
        // ================================================================ Erase
        new()
        {
            Id = "fal-ai/object-removal/mask", Job = FalImageJob.Erase, IsDefault = true,
            Title = "Object removal (mask)",
            MaskUrlField = "mask_url", MaskPolarity = FalMaskPolarity.WhiteActs,
            PromptField = null, BillingTierField = "model",
            Fields =
            [
                Str("model", ["low_quality", "medium_quality", "high_quality", "best_quality"],
                    vendorDefault: "best_quality", editorName: "quality", note: TierNote),
                Int("mask_expansion", 0, 50, vendorDefault: "15")
            ]
        },
        new()
        {
            Id = "fal-ai/flux-pro/v1/erase", Job = FalImageJob.Erase, PricedPerMegapixel = true,
            Title = "FLUX.1 Pro erase",
            MaskUrlField = "mask_url", MaskPolarity = FalMaskPolarity.WhiteActs,
            PromptField = null, OutputFormatField = "output_format",
            Fields = [Int("dilate_pixels", 0, 100, vendorDefault: "10"), Str("output_format", OutputJpegPng, "jpeg")]
        },

        // ================================================================ Fill (masked, prompted)
        new()
        {
            Id = "fal-ai/flux-pro/v1/fill", Job = FalImageJob.Fill, IsDefault = true, PricedPerMegapixel = true, LinearPerMegapixel = true,
            Title = "FLUX.1 Pro fill",
            MaskUrlField = "mask_url", MaskPolarity = FalMaskPolarity.NotStated,
            PromptRequired = true, SeedField = "seed", OutputFormatField = "output_format",
            Fields = [Str("output_format", OutputJpegPng, "jpeg"), Bool("enhance_prompt", "false")]
        },
        new()
        {
            Id = "ideogram/v4.5/edit", Job = FalImageJob.Fill,
            Title = "Ideogram 4.5 edit (masked)",
            MaskUrlField = "mask_url", MaskPolarity = FalMaskPolarity.WhitePreserves,
            PromptRequired = true, PromptMinLength = 1, PromptMaxLength = 10000, SeedField = "seed",
            BillingTierField = "quality",
            Fields =
            [
                Str("edit_precision", ["regular", "high"], "regular"),
                Str("quality", ["very_low", "low", "medium", "high"], "medium", TierNote)
            ]
        },

        // ================================================================ Expand
        new()
        {
            Id = "fal-ai/bria/expand", Job = FalImageJob.Expand, IsDefault = true,
            Title = "Bria expand (to a canvas, optional prompt)",
            Output = FalOutputShape.SingleImage,
            NegativePromptField = "negative_prompt", SeedField = "seed", SeedMin = 0, SeedMax = 2147483647,
            ExpandBy = FalExpandGeometry.Canvas,
            CanvasSizeField = "canvas_size", OriginalImageSizeField = "original_image_size",
            OriginalImageLocationField = "original_image_location",
            MaxOutputPixels = 5000L * 5000L, MaxOutputPixelsExclusive = true,
            LimitsBasis = "canvas_size: \"should have an area of less than 5000x5000 pixels\" (fal schema, read 2026-10-10); " +
                          "no input-size limit is documented. Element order [width, height] / [x, y]: fal's own schema " +
                          "example answers canvas_size [1200, 674] with a 1200x674 image, and Bria's API reference states " +
                          "both orders (docs.bria.ai image-expansion, read 2026-10-10).",
            // aspect_ratio is deliberately NOT forwarded: with it the vendor picks the canvas size and the
            // placement itself ("Will be used over original_image_size and original_image_location"), so the
            // editor could not put the result back where the original was. The editor states the canvas.
            Fields = []
        },
        new()
        {
            Id = "fal-ai/flux-2-pro/outpaint", Job = FalImageJob.Expand, PricedPerMegapixel = true, BillsInputMegapixels = true,
            Title = "FLUX.2 Pro outpaint (margins only, no prompt)",
            PromptField = null, OutputFormatField = "output_format",
            ExpandBy = FalExpandGeometry.Margins,
            MaxOutputPixels = 2048L * 2048, MaxInputPixels = 4_000_000,
            LimitsBasis = "Black Forest Labs, help.bfl.ai \"What are the resolution limits?\" (read 2026-10-10): FLUX.2 " +
                          "\"generates images up to 4MP (e.g., 2048x2048)\" and \"Input images over 4MP are automatically " +
                          "resized\". The output limit is the vendor's own example, 2048 x 2048 = 4,194,304 pixels; the input " +
                          "limit keeps 4,000,000, the conservative reading of \"4MP\". An input the vendor resizes by an " +
                          "unstated rule would no longer match the margins, so it is treated as a limit, not a convenience.",
            Fields =
            [
                Int("expand_top", 0, 2048, editorName: "top", vendorDefault: "0"),
                Int("expand_right", 0, 2048, editorName: "right", vendorDefault: "0"),
                Int("expand_bottom", 0, 2048, editorName: "bottom", vendorDefault: "0"),
                Int("expand_left", 0, 2048, editorName: "left", vendorDefault: "0"),
                Bool("auto_crop", "false"),
                Str("mode", ["high", "fast"], "high"),
                Str("output_format", OutputJpegPng, "jpeg")
            ],
            Rule = body => new[] { "expand_top", "expand_right", "expand_bottom", "expand_left" }
                .Any(k => body.TryGetValue(k, out object? v) && v is long n && n > 0)
                ? null
                : "An expand needs at least one margin above 0 (top, right, bottom or left, in pixels)."
        },

        // ================================================================ Replace background
        new()
        {
            Id = "fal-ai/bria/background/replace", Job = FalImageJob.ReplaceBackground, IsDefault = true,
            Title = "Bria background replace",
            PromptMinLength = 1, NegativePromptField = "negative_prompt", SeedField = "seed", SeedMin = 0, SeedMax = 2147483647,
            Fields =
            [
                Url("ref_image_url", "A reference image for the new background. Exactly one of this and the prompt must be given."),
                Bool("refine_prompt", "true"),
                Bool("fast", "true")
            ],
            Rule = body => body.ContainsKey("prompt") == body.ContainsKey("ref_image_url")
                ? "Background replace takes EITHER a prompt OR a reference image (ref_image_url), not both and not neither."
                : null
        },

        // ================================================================ Relight
        new()
        {
            Id = "fal-ai/image-apps-v2/relighting", Job = FalImageJob.Relight, IsDefault = true,
            Title = "Relighting (18 named styles)",
            PromptField = null,
            Fields =
            [
                Str("lighting_style", LightingStyles, "natural"),
                new("aspect_ratio", FalFieldType.String, Values: ["1:1", "16:9", "9:16", "4:3", "3:4"],
                    VendorDefault: "1:1", WrapIn: "ratio",
                    Note: "Sent as { \"ratio\": value }. The schema describes it as the aspect ratio for 4K output; the real output size is not stated.")
            ]
        },
        new()
        {
            Id = "fal-ai/iclight-v2", Job = FalImageJob.Relight, PricedPerMegapixel = true, LinearPerMegapixel = true,
            Title = "IC-Light v2 (prompt-driven)",
            PromptRequired = true, NegativePromptField = "negative_prompt", SeedField = "seed",
            OutputFormatField = "output_format",
            Fields =
            [
                Str("initial_latent", ["None", "Left", "Right", "Top", "Bottom"], "None"),
                Str("image_size", SizePresets6, note: "The schema gives it no default; the output size when omitted is not stated."),
                Int("num_inference_steps", 1, 50, vendorDefault: "28"),
                Num("guidance_scale", 0, 20, vendorDefault: "5"),
                Url("mask_image_url", "Mask polarity is NOT STATED by the schema."),
                Str("output_format", OutputJpegPng, "jpeg")
            ]
        },
        new()
        {
            Id = "bria/fibo-edit/relight", Job = FalImageJob.Relight,
            Title = "Bria FIBO relight",
            PromptField = null, Output = FalOutputShape.SingleImage,
            Fields =
            [
                Str("light_type", FiboLightTypes, required: true),
                Str("light_direction", ["front", "side", "bottom", "top-down"], required: true)
            ]
        },

        // ================================================================ Upscale
        new()
        {
            Id = "fal-ai/seedvr/upscale/image", Job = FalImageJob.Upscale, IsDefault = true, PricedPerMegapixel = true, LinearPerMegapixel = true,
            Title = "SeedVR upscale",
            PromptField = null, SeedField = "seed", Output = FalOutputShape.SingleImage,
            OutputFormatField = "output_format",
            Fields =
            [
                Str("upscale_mode", ["target", "factor"], "factor"),
                Num("upscale_factor", 1, 2, editorName: "factor", vendorDefault: "2"), // x2 cap (every upscale bills at or above cost)
                Str("target_resolution", ["720p", "1080p", "1440p", "2160p"], "1080p"),
                Num("noise_scale", 0, 1, vendorDefault: "0.1"),
                Str("output_format", ["png", "jpg", "webp"], "jpg")
            ],
            Rule = body =>
            {
                body.TryGetValue("upscale_mode", out object? mode);
                bool target = (mode as string) == "target";
                if (body.ContainsKey("target_resolution") && !target)
                    return "target_resolution only applies when upscale_mode is 'target'.";
                if (target && body.ContainsKey("upscale_factor"))
                    return "upscale_factor only applies when upscale_mode is 'factor'.";
                return null;
            }
        },
        new()
        {
            Id = "topaz/upscale/image/precision", Job = FalImageJob.Upscale,
            Title = "Topaz precision (Text Refine keeps type crisp)",
            PromptField = null, Output = FalOutputShape.SingleImage, OutputFormatField = "output_format",
            Fields =
            [
                Str("model", ["Standard V2", "High Fidelity V3", "High Fidelity V2", "Low Resolution V2", "CGI", "Text Refine", "Faces"], "Standard V2"),
                Num("upscale_factor", 1, 2, editorName: "factor", vendorDefault: "2"),
                Str("output_format", OutputJpegPng, "jpeg"),
                Str("subject_detection", ["All", "Foreground", "Background"], "All"),
                Bool("face_enhancement", "true"),
                Num("sharpen", 0, 1), Num("denoise", 0, 1), Num("fix_compression", 0, 1)
            ]
        },
        new()
        {
            Id = "topaz/upscale/image/generative", Job = FalImageJob.Upscale,
            Title = "Topaz generative (Wonder family)",
            PromptField = null, Output = FalOutputShape.SingleImage, OutputFormatField = "output_format",
            Fields =
            [
                Str("model", ["Wonder 3.5", "Wonder 3", "Wonder 2", "Wonder", "Recover 3", "Standard MAX", "Redefine", "Recovery V2", "Recovery"], "Wonder 3"),
                Num("upscale_factor", 1, 2, editorName: "factor", vendorDefault: "2"),
                Str("output_format", OutputJpegPng, "jpeg"),
                Str("subject_detection", ["All", "Foreground", "Background"], "All"),
                Bool("face_enhancement", "true"),
                Str("enhancement_strength", ["low", "medium", "high"], note: "Applies to Wonder 3 and Wonder 3.5 only.")
            ]
        },

        // ================================================================ Instruct (edit)
        new()
        {
            Id = "google/nano-banana-2.1/edit", Job = FalImageJob.Instruct, IsDefault = true,
            Title = "Nano Banana 2.1 edit",
            ImageUrlField = null, ImageUrlsField = "image_urls", PromptRequired = true, PromptMinLength = 3, PromptMaxLength = 50000,
            SeedField = "seed", AspectRatioField = "aspect_ratio", OutputFormatField = "output_format",
            BillingTierField = "resolution",
            Fields =
            [
                Str("aspect_ratio", AspectNano, "auto"),
                Str("output_format", OutputJpegPngWebp, "png"),
                Str("resolution", ["1K", "2K", "4K"], "1K", TierNote + " Token-priced by the vendor: the rate card carries its illustrative per-resolution figures."),
                Str("thinking_level", ["minimal", "medium", "high"], "medium")
            ]
        },
        new()
        {
            Id = "blackforestlabs/flux-3/edit-image", Job = FalImageJob.Instruct,
            Title = "FLUX.3 image edit",
            ImageUrlField = null, ImageUrlsField = "image_urls", PromptRequired = true, AspectRatioField = "aspect_ratio", OutputFormatField = "output_format",
            Fields =
            [
                Str("aspect_ratio", AspectFlux3, "auto"),
                Str("resolution", ["1k"], "1k", "Restricted to the 1k tier: the vendor states a price for a 1 MP image only."),
                Bool("enable_prompt_expansion", "false"),
                Str("output_format", OutputJpegPng, "jpeg")
            ]
        },
        new()
        {
            Id = "openai/gpt-image-2.5/sunburst/edit", Job = FalImageJob.Instruct,
            Title = "GPT Image 2.5 Sunburst edit",
            ImageUrlField = null, ImageUrlsField = "image_urls", PromptRequired = true, PromptMinLength = 1, PromptMaxLength = 32000,
            OutputFormatField = "output_format", BillingTierField = "quality",
            Fields = Gpt25Fields()
        },
        new()
        {
            Id = "openai/gpt-image-2.5/flare/edit", Job = FalImageJob.Instruct,
            Title = "GPT Image 2.5 Flare edit",
            ImageUrlField = null, ImageUrlsField = "image_urls", PromptRequired = true, PromptMinLength = 1, PromptMaxLength = 32000,
            OutputFormatField = "output_format", BillingTierField = "quality",
            Fields = Gpt25Fields()
        },
        new()
        {
            Id = "bytedance/seedream/v5/pro/edit", Job = FalImageJob.Instruct,
            Title = "Seedream 5 Pro edit",
            ImageUrlField = null, ImageUrlsField = "image_urls", PromptRequired = true, OutputFormatField = "output_format",
            Fields =
            [
                Str("image_size", ["auto_1K"], "auto_2K",
                    "Restricted to auto_1K and sent by default: the vendor prices output above 1536x1536 at double, and the schema's own default (auto_2K) is above it.",
                    sendByDefault: "auto_1K"),
                Str("output_format", OutputJpegPng, "jpeg"),
                Str("enhance_prompt_mode", ["standard", "fast"], "standard")
            ]
        },
        new()
        {
            Id = "fal-ai/qwen-image-edit-2511", Job = FalImageJob.Instruct, PricedPerMegapixel = true, LinearPerMegapixel = true,
            Title = "Qwen Image Edit 2511",
            ImageUrlField = null, ImageUrlsField = "image_urls", PromptRequired = true, NegativePromptField = "negative_prompt", SeedField = "seed",
            OutputFormatField = "output_format",
            Fields =
            [
                Str("output_format", OutputJpegPngWebp, "png"),
                Int("num_inference_steps", 1, 50, vendorDefault: "28"),
                Str("acceleration", ["none", "regular", "high"], "regular"),
                Num("guidance_scale", 1, 20, vendorDefault: "4.5")
            ]
        },
        new()
        {
            Id = "bria/fibo-edit/edit", Job = FalImageJob.Instruct,
            Title = "Bria FIBO edit (instruction)",
            PromptField = "instruction", PromptRequired = true, NegativePromptField = "negative_prompt", SeedField = "seed",
            Output = FalOutputShape.SingleImage,
            Fields = [Int("steps_num", 20, 50, vendorDefault: "30"), Num("guidance_scale", null, null, vendorDefault: "5")]
        },
        new()
        {
            Id = "bria/fibo-edit/erase_by_text", Job = FalImageJob.Instruct,
            Title = "Bria FIBO erase by name",
            PromptField = "object_name", PromptRequired = true, Output = FalOutputShape.SingleImage,
            Fields = []
        },
        new()
        {
            Id = "fal-ai/image-editing/youtube-thumbnails", Job = FalImageJob.Instruct, IsPreset = true,
            Title = "YouTube-thumbnail style (LoRA preset)",
            PromptRequired = true, SeedField = "seed",
            Fields = [Num("guidance_scale", 0, 20, vendorDefault: "3.5"), Int("num_inference_steps", 1, 50, vendorDefault: "30"), Num("lora_scale", 0, 4, vendorDefault: "0.5")]
        },

        // ================================================================ Generate
        new()
        {
            Id = "ideogram/v4.5", Job = FalImageJob.Generate, IsDefault = true,
            Title = "Ideogram 4.5",
            ImageUrlField = null, PromptRequired = true, PromptMinLength = 1, PromptMaxLength = 10000, SeedField = "seed",
            CountField = "num_images", CountMax = 8,
            SizeField = "image_size", SizePresets = SizePresets6, SupportedSizes = IdeogramV45Sizes,
            BillingTierField = "quality",
            Fields =
            [
                Str("quality", ["low", "medium", "high"], "medium", TierNote),
                Bool("enable_prompt_expansion", "true")
            ]
        },
        new()
        {
            Id = "google/nano-banana-2.1", Job = FalImageJob.Generate,
            Title = "Nano Banana 2.1",
            ImageUrlField = null, PromptRequired = true, PromptMinLength = 3, PromptMaxLength = 50000, SeedField = "seed",
            CountField = "num_images", CountMax = 4, AspectRatioField = "aspect_ratio", OutputFormatField = "output_format",
            BillingTierField = "resolution",
            Fields =
            [
                Str("aspect_ratio", AspectNano, "auto"),
                Str("output_format", OutputJpegPngWebp, "png"),
                Str("resolution", ["1K", "2K", "4K"], "1K", TierNote + " Token-priced by the vendor: the rate card carries its illustrative per-resolution figures."),
                Str("thinking_level", ["minimal", "medium", "high"], "medium")
            ]
        },
        new()
        {
            Id = "blackforestlabs/flux-3/text-to-image", Job = FalImageJob.Generate,
            Title = "FLUX.3 text-to-image",
            ImageUrlField = null, PromptRequired = true, AspectRatioField = "aspect_ratio", OutputFormatField = "output_format",
            Fields =
            [
                Str("aspect_ratio", AspectFlux3, "auto"),
                Str("resolution", ["1k"], "1k", "Restricted to the 1k tier: the vendor states a price for a 1 MP image only."),
                Bool("enable_prompt_expansion", "false"),
                Str("output_format", OutputJpegPng, "jpeg")
            ]
        },
        new()
        {
            Id = "bytedance/seedream/v5/pro/text-to-image", Job = FalImageJob.Generate,
            Title = "Seedream 5 Pro text-to-image",
            ImageUrlField = null, PromptRequired = true, CountField = "num_images", CountMax = 6,
            OutputFormatField = "output_format",
            Fields =
            [
                Str("image_size", ["auto_1K"], "auto_2K",
                    "Restricted to auto_1K and sent by default: the vendor prices output above 1536x1536 at double, and the schema's own default (auto_2K) is above it.",
                    sendByDefault: "auto_1K"),
                Str("output_format", OutputJpegPng, "jpeg"),
                Str("enhance_prompt_mode", ["standard", "fast"], "standard")
            ]
        },
        new()
        {
            Id = "fal-ai/ideogram/v3/generate-transparent", Job = FalImageJob.Generate, IsOlderGeneration = true,
            Title = "Ideogram v3 transparent element (older generation)",
            ImageUrlField = null, PromptRequired = true, NegativePromptField = "negative_prompt", SeedField = "seed",
            CountField = "num_images", CountMax = 8, AspectRatioField = "aspect_ratio",
            BillingTierField = "rendering_speed",
            Fields =
            [
                Str("aspect_ratio", AspectIdeogramV3, "1:1"),
                Str("rendering_speed", ["TURBO", "BALANCED", "QUALITY"], "BALANCED", TierNote),
                Bool("expand_prompt", "true")
            ]
        },
    ];

    private static IReadOnlyList<FalImageField> Gpt25Fields() =>
    [
        Str("quality", ["low", "medium", "high", "xhigh", "max"], "high", TierNote + " " + Gpt25Note),
        Str("image_size", ["auto", "square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9"], "auto"),
        Str("background", ["auto", "transparent", "opaque"], "auto"),
        Str("output_format", OutputJpegPngWebp, "png")
    ];
}
