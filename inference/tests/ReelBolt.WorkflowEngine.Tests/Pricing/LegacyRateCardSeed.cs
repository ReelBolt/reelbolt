#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;

namespace ReelBolt.WorkflowEngine.Tests.Pricing;

/// <summary>
/// TEST FIXTURE: the rate-card seed the platform shipped before the model catalog
/// (docs/design/model-catalog.md), kept verbatim so the upgrader's one-time price port can be tested against
/// exactly the <c>rate_cards</c> rows a real install holds. Nothing in production reads it.
/// <para>Original summary: first-boot rate cards and multipliers (written only while <c>rate_cards</c> is empty).</para>
/// <para>
/// <b>The credit unit is D9, and it is LOCKED.</b> 1 credit = $0.006 of platform COGS, which is a 70%
/// gross margin at Pro's effective credit rate after payment fees (docs/design/saas-pricing-and-billing.md
/// "Credit unit and margin target"). Every card therefore charges
/// <c>credits_millis_per_unit = cost_usd_per_unit / 0.006 * 1000</c>, and the 70% margin target is
/// carried by that one divisor rather than by a second, per-card multiplier.
/// </para>
/// <para>
/// <b>The USD costs are INDICATIVE, not measured.</b> B0 fixed the cost model; B13 was to replace it
/// with provider-measured per-model prices, and that measurement does not exist yet. Rather than ship
/// nothing, these are the best figures the written record supports TODAY, each one carrying its own
/// basis in <see cref="RateCard.Notes"/> so a reviewer can tell a researched list price from a stated
/// estimate. Every row still carries <see cref="PlaceholderNote"/>'s marker word: nothing here has been
/// reconciled against a provider invoice, and the load-test cost lane keys on that marker to stay
/// BLOCKED rather than compare an estimate to an invoice (infra/loadtest/x1-cost.mjs).
/// </para>
/// <para>
/// <b>Two layers of rows.</b> <see cref="ModelPriceCatalog"/> names the models the platform supports and
/// their published vendor list prices, and each of those becomes an exact-name card. Beneath them, every
/// <c>(provider kind, usage kind)</c> pair the capability matrix requires
/// (<see cref="ProviderCapabilityUsage.RequiredCards"/>) gets one wildcard (<c>*</c>) row, so a model
/// nobody has priced still resolves to an estimate rather than to nothing. The specificity rule
/// (<c>exact &gt; longest prefix &gt; *</c>) picks the model row when one exists. Per-model
/// differentiation is what B13's measured data replaces; <c>RateCardCoverage</c> is what keeps
/// the gap between the two visible.
/// </para>
/// </summary>
internal static class LegacyRateCardSeed
{
    /// <summary>
    /// The note every seeded row carries. The word <c>PLACEHOLDER</c> is load bearing: the admin rate-card
    /// table badges on it and <c>infra/loadtest/x1-cost.mjs</c> reports its reconciliation lane BLOCKED
    /// while any active card still carries it. It is true of these rows — they are indicative figures,
    /// not measured ones — so it stays until B13 lands, and the text after it names the basis.
    /// </summary>
    public const string PlaceholderNote = "PLACEHOLDER (indicative, not measured) - derived from D9 and B0's cost model; B13 replaces with provider-measured costs.";

    /// <summary>
    /// The marker word itself. Both <see cref="PlaceholderNote"/> and <see cref="PublishedNote"/> begin
    /// with it, and two consumers key on the substring rather than the whole sentence: the admin
    /// rate-card table's badge (<c>isPlaceholderRateCard</c>) and the load-test cost lane's
    /// reconciliation gate (<c>infra/loadtest/x1-cost.mjs</c>). Changing it means changing both.
    /// </summary>
    public const string PlaceholderMarker = "PLACEHOLDER";

    /// <summary>D9 (LOCKED): one credit is this much platform COGS, the 70% gross margin at Pro's effective credit rate.</summary>
    public const decimal CreditUsdCogs = 0.006m;

    /// <summary>Millicredits per USD of COGS at 1 credit = <see cref="CreditUsdCogs"/>.</summary>
    public const decimal CreditsMillisPerUsd = 1000m / CreditUsdCogs;

    // ---------------------------------------------------------------- LLM tokens
    // B0 § "Cost model / Assumptions" prices a blended million tokens at $0.50 (Low), $3.00 (Mid) or
    // $9.00 (High) and leaves the choice of default model to B13. Rate cards need an input/output
    // split, so the Mid scenario is taken at the seed's own 1:4 input:output ratio over a 50/50 token
    // mix, which lands the blend on exactly $3.00/Mtok; cached input is 10% of input. ESTIMATED, not
    // measured: B0 labels all three scenarios "estimated".

    /// <summary>B0 Mid scenario: $3.00 per million tokens blended, split 1:4 input:output.</summary>
    public const decimal LlmUsdPerMillionTokensBlended = 3.00m;

    /// <summary>Output tokens cost this many times an input token (the ratio the seed already used).</summary>
    public const decimal LlmOutputToInputRatio = 4m;

    /// <summary>Cached input costs this fraction of an input token.</summary>
    public const decimal LlmCachedInputShare = 0.10m;

    /// <summary>USD per input token, solved from the Mid blend so <c>(input + output) / 2 = $3.00/M</c>.</summary>
    public static readonly decimal LlmInputUsdPerToken =
        LlmUsdPerMillionTokensBlended / 1_000_000m * 2m / (1m + LlmOutputToInputRatio);

    /// <summary>USD per output token.</summary>
    public static readonly decimal LlmOutputUsdPerToken = LlmInputUsdPerToken * LlmOutputToInputRatio;

    /// <summary>USD per cached input token.</summary>
    public static readonly decimal LlmCachedInputUsdPerToken = LlmInputUsdPerToken * LlmCachedInputShare;

    /// <summary>Embeddings: B0's own worked example, $0.01 per million tokens (research list price class).</summary>
    public const decimal EmbeddingUsdPerToken = 0.01m / 1_000_000m;

    /// <summary>ASR: $0.006 per audio minute — the researched list price of OpenAI whisper-1 / gpt-4o-transcribe (docs/design/cloud-provider-adr.md).</summary>
    public const decimal AsrUsdPerSecond = 0.006m / 60m;

    /// <summary>
    /// TTS: $15 per million input bytes. <b>GENUINELY UNMEASURED</b> — B0 lists "voiceover (Fish Audio,
    /// per UTF-8 byte)" under "not measured: B3c adds the units, B13 prices them", and no published
    /// per-byte list price has been recorded anywhere in this repo. This is the seed's original figure,
    /// kept rather than replaced by an invented one, and it is the weakest number on the page.
    /// </summary>
    public const decimal TtsUsdPerByte = 15m / 1_000_000m;

    // ---------------------------------------------------------------- video generation

    /// <summary>
    /// USD per generated video second. MiniMax-H3-Max — the model in the measured spend ledger — at its
    /// 768P list price of $0.08/s (<c>MiniMaxVideoGenerationClient.Describe</c>). It reconciles with the
    /// measured ledger: $9.20 over 18 succeeded jobs is $0.511 per clip, which is a ~6.4-second clip at
    /// this rate. <b>Known gap:</b> a rate card keys on the model name, not the resolution, so this single
    /// wildcard cannot price 480P ($0.05/s) and 768P ($0.08/s) differently — B13 must decide whether that
    /// needs a resolution-keyed card.
    /// </summary>
    public const decimal VideoGenUsdPerUnit = 0.08m;

    // ---------------------------------------------------------------- compute

    /// <summary>
    /// USD of compute per 720p media second. B0's researched anchor is an 8 vCPU VM at $0.25/hour with
    /// 2 vCPU effective per run, i.e. about $1.7e-5 per run-second ("Compute | ... | researched"). The
    /// conversion B0 does NOT supply is run-seconds to output-seconds; this reads the anchor at one output
    /// second per run-second at 720p, which is the conservative (upper) end — measured <c>VideoCompile</c>
    /// wall clock (p50 14.3 s) runs faster than realtime. <b>ESTIMATED conversion, not measured.</b>
    /// </summary>
    public const decimal ComputeRenderUsdPerSecond = 0.000017m;

    /// <summary>The provider kind compute cards were filed under.</summary>
    public const InferenceProviderKind ComputeProviderKind = InferenceProviderKind.OpenAICompatible;

    // ---------------------------------------------------------------- images

    /// <summary>
    /// The wildcard fallback for an image usage kind, DERIVED from the published catalogue rather
    /// than invented.
    /// </summary>
    /// <remarks>
    /// An image wildcard cannot be a researched list price the way the ASR row is, because there is
    /// no single "an image costs X" figure to research: MiniMax charges $0.0035 and BFL charges
    /// between $0.014 and $0.607 depending on model and resolution, a 43x spread. Picking any one of
    /// those by hand would under-charge most models and over-charge the rest, and picking a midpoint
    /// would be a number nobody published.
    /// <para>
    /// So it is computed: the HIGHEST published per-image price in <see cref="ModelPriceCatalog"/>
    /// for that usage kind, with the basis naming which model set it. The direction is deliberate —
    /// an uncatalogued model is never UNDER-charged, and over-charging is visible and refundable
    /// while under-charging is a silent hole. B13 replaces it with measured data like every other
    /// figure on this page.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The wildcard fallback for <see cref="UsageKind.VideoSegmentationFrames"/>, derived from the
    /// published catalogue exactly as <see cref="ImageWildcard"/> is: the HIGHEST per-frame price any
    /// catalogued video-segmentation model publishes, so an endpoint nobody has priced is never
    /// under-charged. Today the catalogue holds one such model (fal's sam-3-1/video-rle at $0.01 per 16
    /// frames = $0.000625 per frame), so the wildcard IS its published rate rather than a guess.
    /// </summary>
    private static (decimal Usd, string Basis) VideoSegmentationWildcard()
    {
        List<ModelPrice> candidates =
            [.. ModelPriceCatalog.Priced.Where(m => m.UsdPerUnit.ContainsKey(UsageKind.VideoSegmentationFrames))];

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "No catalogued model publishes a price for VideoSegmentationFrames, so the wildcard fallback " +
                "has nothing to derive from. Add a priced entry to ModelPriceCatalog rather than a guess here.");
        }

        ModelPrice dearest = candidates
            .OrderByDescending(m => m.UsdPerUnit[UsageKind.VideoSegmentationFrames])
            .ThenBy(m => m.ProviderKind.ToString(), StringComparer.Ordinal)
            .ThenBy(m => m.Model, StringComparer.Ordinal)
            .First();

        decimal usd = dearest.UsdPerUnit[UsageKind.VideoSegmentationFrames];
        return (usd,
            "derived, not invented: the highest published video-segmentation price in ModelPriceCatalog is " +
            dearest.Model + "'s $" + usd.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " per frame (itself $" +
            ModelPriceCatalog.FalVideoSegmentationUsdPerUnit.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " per " + ModelPriceCatalog.FalVideoSegmentationFramesPerUnit + " frames, divided by the block " +
            "size), so an endpoint with no catalogue row is never under-charged. The vendor rounds each " +
            "request up to a whole block; VideoSegmentationBilling applies the same rounding to the " +
            "recorded frame count.");
    }

    private static (decimal Usd, string Basis) ImageWildcard(UsageKind usageKind, string label)
    {
        List<ModelPrice> candidates =
            [.. ModelPriceCatalog.Priced.Where(m => m.UsdPerUnit.ContainsKey(usageKind))];

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No catalogued model publishes a price for {usageKind}, so the '{label}' wildcard fallback " +
                "has nothing to derive from. Add a priced entry to ModelPriceCatalog rather than a guess here.");
        }

        ModelPrice dearest = candidates
            .OrderByDescending(m => m.UsdPerUnit[usageKind])
            .ThenBy(m => m.ProviderKind.ToString(), StringComparer.Ordinal)
            .ThenBy(m => m.Model, StringComparer.Ordinal)
            .First();

        decimal usd = dearest.UsdPerUnit[usageKind];
        return (usd,
            $"derived, not invented: the highest published {label} price in ModelPriceCatalog is " +
            $"{dearest.ProviderKind}/{dearest.Model} at ${usd:0.#####} per image, and the wildcard uses it so a " +
            "model with no catalogue entry is never under-charged. The vendors' own spread is wide " +
            "(MiniMax image-01 $0.0035 against BFL FLUX.2 [max] $0.07 and FLUX 3 Image 4k $0.607), which is " +
            "why no single researched figure exists to cite here.");
    }

    // ---------------------------------------------------------------- fal.ai segmentation and depth

    /// <summary>
    /// fal.ai's HIGHEST published GPU list price, in USD per hour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a GPU-hour rate prices a per-image card.</b> fal does not publish a per-output price for
    /// any of the five segmentation and depth endpoints this platform configures — they do not appear
    /// in its per-unit price table. What it does publish, on <c>fal.ai/pricing</c> (read 2026-10-10),
    /// is its GPU list prices (<c>B300 $12.99/h</c>, <c>GB200 $9.99/h</c>, <c>B200 $7.99/h</c>,
    /// <c>H200 $6.00/h</c>, <c>H100 $4.50/h</c>, <c>RTX PRO 6000 $4.00/h</c>), and its own Model APIs
    /// pricing page states the rule that connects the two: a model with no fixed per-output price
    /// "fall[s] back to per-second billing based on the GPU machine type used to run the request".
    /// The wildcard therefore has to be a published hourly rate divided by 3600, times a stated number
    /// of seconds — there is no third source of truth to cite.
    /// </para>
    /// <para>
    /// <b>The highest rate, deliberately.</b> fal publishes six GPU classes spanning a 3.2x range, and
    /// nothing in a segmentation response says which one ran. Taking the dearest is the conservative
    /// direction and matches the image wildcard's rule: an unattributed endpoint is never
    /// UNDER-charged, and over-charging is visible and refundable while under-charging is a silent
    /// hole in the ledger. B13 replaces all of it with measured data.
    /// </para>
    /// </remarks>
    public const decimal FalGpuListUsdPerHour = 12.99m;

    /// <summary>
    /// The stated seconds-per-output this seed assumes for one segmentation or depth call.
    /// </summary>
    /// <remarks>
    /// <b>This is the one number on this page that nothing publishes, and it is labelled as such.</b>
    /// fal's per-second fallback needs a duration and the vendor states none. Four seconds is a stated
    /// order-of-magnitude figure for a single still through a small vision model on a rented GPU —
    /// including cold start, which is most of it — and it is exactly the same kind of declared
    /// conversion <see cref="ComputeRenderUsdPerSecond"/> already makes for render seconds
    /// ("ESTIMATED conversion, not measured"). It is kept as a named constant rather than folded into
    /// a single magic price so that the assumption is visible and can be replaced by a measurement.
    /// </remarks>
    public const int FalSecondsPerOutputImage = 4;

    /// <summary>USD per segmentation or depth output, from the two figures above.</summary>
    public static readonly decimal FalAnalysisUsdPerImage =
        FalGpuListUsdPerHour / 3_600m * FalSecondsPerOutputImage;

    /// <summary>An encode pass costs this share of the render pass. Kept from the previous seed's 2:1 ratio; not measured.</summary>
    public const decimal ComputeEncodeShare = 0.5m;

    /// <summary>An analyse pass costs this share of the render pass. Kept from the previous seed's 2:0.5 ratio; not measured.</summary>
    public const decimal ComputeAnalyzeShare = 0.25m;

    /// <summary>
    /// The token kinds every chat/vision/decision provider kind must be priced for. Kept as a name
    /// because it is what the <c>*</c> row set below is keyed on; the set itself comes from
    /// <see cref="ProviderCapabilityUsage"/>, not from a hand-written list here.
    /// </summary>
    private static readonly InferenceProviderKind[] LlmKinds = [.. ProviderCapabilityUsage.RequiredCards()
        .Where(p => p.UsageKind == UsageKind.LlmInputTokens)
        .Select(p => p.Kind)];

    /// <summary>
    /// The USD cost per unit of a wildcard row, per usage kind. Every one of these is B0's or the ADR's
    /// estimate, kept as the fallback a model with no catalogue price falls back to.
    /// </summary>
    private static decimal FallbackUsdPerUnit(UsageKind kind) => kind switch
    {
        UsageKind.LlmInputTokens => LlmInputUsdPerToken,
        UsageKind.LlmOutputTokens => LlmOutputUsdPerToken,
        UsageKind.LlmCachedInputTokens => LlmCachedInputUsdPerToken,
        UsageKind.EmbeddingTokens => EmbeddingUsdPerToken,
        UsageKind.AsrAudioSeconds => AsrUsdPerSecond,
        UsageKind.TtsInputBytes => TtsUsdPerByte,
        UsageKind.VideoGenSeconds => VideoGenUsdPerUnit,
        UsageKind.ImageGenImages => ImageWildcard(kind, "image generation").Usd,
        UsageKind.ImageEditImages => ImageWildcard(kind, "image editing").Usd,
        UsageKind.SegmentationImages => FalAnalysisUsdPerImage,
        UsageKind.DepthImages => FalAnalysisUsdPerImage,
        UsageKind.VideoSegmentationFrames => VideoSegmentationWildcard().Usd,
        // Audio (T-107): the HIGHEST catalogued per-unit price, so an unattributed model is never under-charged.
        UsageKind.MusicGenMinutes or UsageKind.SoundEffectGenSeconds or UsageKind.AudioCleanupMinutes or UsageKind.VideoEnhanceSeconds
            or UsageKind.VideoEnhanceComputeSeconds => ModelPriceCatalog.Priced
            .SelectMany(m => m.UsdPerUnit).Where(p => p.Key == kind).Max(p => p.Value),
        // Moderation is free at the vendor, so its wildcard is a real $0 (never an estimate).
        UsageKind.ModerationCalls => 0m,
        // Rerank is self-hosted or unpriced (priced-or-refused: an unpriced rerank model means rerank is unavailable),
        // so its wildcard is a real $0, never an invented vendor price. Per-model rows carry any catalogued figure.
        UsageKind.RerankSearches => 0m,
        // Web search: the HIGHEST catalogued per-search price (Gemini 2.5 grounded prompt), so an unattributed
        // model is never under-charged (agent-web-search D-1, retrieved 2026-10-25).
        UsageKind.WebSearches => ModelPriceCatalog.Priced
            .SelectMany(m => m.UsdPerUnit).Where(p => p.Key == kind).Max(p => p.Value),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
            "No wildcard fallback price is recorded for this usage kind.")
    };

    /// <summary>The basis a wildcard row carries, so the page says which estimate a fallback price is.</summary>
    private static string FallbackBasis(UsageKind kind) => kind switch
    {
        UsageKind.LlmInputTokens or UsageKind.LlmOutputTokens or UsageKind.LlmCachedInputTokens =>
            "B0 Mid scenario, $3.00/Mtok blended (estimated, no measured provider price yet)",
        UsageKind.EmbeddingTokens =>
            "researched list-price class ($0.01/Mtok), B0's own worked example",
        UsageKind.AsrAudioSeconds =>
            "researched list price, $0.006 per audio minute (OpenAI whisper-1 / gpt-4o-transcribe)",
        UsageKind.TtsInputBytes =>
            "UNMEASURED as a wildcard - no per-byte price for arbitrary models is recorded; the original " +
            "seed figure, kept rather than invented (Fish Audio's own s2.1-pro price is a per-model row)",
        UsageKind.VideoGenSeconds =>
            "MiniMax-H3-Max 768P list price $0.08/s; reconciles with the measured $0.511 per clip",
        UsageKind.ImageGenImages => ImageWildcard(kind, "image generation").Basis,
        UsageKind.ImageEditImages => ImageWildcard(kind, "image editing").Basis,
        UsageKind.VideoSegmentationFrames => VideoSegmentationWildcard().Basis,
        UsageKind.SegmentationImages or UsageKind.DepthImages =>
            "derived, not invented: fal.ai publishes no per-output price for its segmentation or depth " +
            "endpoints (fal.ai/pricing lists only per-unit models), and its Model APIs pricing page " +
            "(fal.ai/docs/documentation/model-apis/pricing) states that such a model falls back to " +
            "per-second billing on the GPU machine type. Its pricing page lists GPU list prices from " +
            "$4.00/h (RTX PRO 6000) to $12.99/h (B300); this wildcard is the HIGHEST of those divided by " +
            "3600 and multiplied by a stated " + FalSecondsPerOutputImage + " seconds per output, so an " +
            "unattributed endpoint is never under-charged. The seconds figure is the one unmeasured " +
            "assumption here and the rate is fal's own published list price, both read 2026-10-10",
        UsageKind.MusicGenMinutes or UsageKind.SoundEffectGenSeconds or UsageKind.AudioCleanupMinutes =>
            "the highest per-unit audio price in ModelPriceCatalog.Audio (fal/ElevenLabs list prices, read 2026-10-17)",
        UsageKind.VideoEnhanceSeconds or UsageKind.VideoEnhanceComputeSeconds =>
            "the highest per-unit video-enhance price in ModelPriceCatalog.VideoEnhance (fal list prices, read 2026-11-02)",
        UsageKind.ModerationCalls =>
            "OpenAI publishes the moderation models as free ($0 per call, read 2026-10-22) — a real price, not an estimate",
        UsageKind.RerankSearches =>
            "self-hosted or unpriced capability: a real $0 wildcard, not an estimate; a priced rerank model has its own row",
        UsageKind.WebSearches =>
            "the highest per-search list price in ModelPriceCatalog (Gemini 2.5 grounded prompt, $35/1k, read 2026-10-25; re-check before launch)",
        _ => "estimate"
    };

    /// <summary>
    /// Every seeded card: the per-model rows first, then the per-(provider kind, usage kind) wildcard
    /// fallbacks, then the compute tiers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The wildcard matrix is generated, not hand-listed.</b> It is driven by
    /// <see cref="ProviderCapabilityUsage.RequiredCards"/>, so a provider kind that serves a capability
    /// gets a card for that capability's usage kind automatically. The hand-written version of this
    /// method named three arrays by hand and had already drifted: Fish Audio serves
    /// <c>Transcription</c> (<c>POST /v1/asr</c>, shipped in
    /// <c>FishAudioTranscriptionClient</c>) but had no <see cref="UsageKind.AsrAudioSeconds"/> card, so
    /// every transcription through a Fish Audio provider — which is the default transcription provider on
    /// the staging deployment — was priced at zero.
    /// </para>
    /// <para>
    /// <b>A wildcard row is a fallback, never the only row where a model's price is known.</b>
    /// <see cref="ModelPriceCatalog"/> carries the models the platform supports with their published
    /// vendor prices, and those become exact-name cards the specificity rule
    /// (<c>exact &gt; longest prefix &gt; *</c>) prefers. A model with no catalogue price keeps the
    /// wildcard, and <c>RateCardCoverage</c> names it rather than letting the fallback read as a
    /// real price.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<RateCard> Cards(DateTime effectiveFrom)
    {
        List<RateCard> cards = [];
        HashSet<(InferenceProviderKind Kind, UsageKind UsageKind, string Model)> seen = [];

        // Per-model rows from the published catalogue. A model that serves two capabilities of one kind
        // (a chat model that also answers vision calls) is listed twice in the catalogue but is one
        // card, so the first occurrence wins.
        foreach (ModelPrice model in ModelPriceCatalog.Priced)
        {
            foreach ((UsageKind usageKind, decimal usdPerUnit) in model.UsdPerUnit)
            {
                if (!seen.Add((model.ProviderKind, usageKind, model.Model))) continue;
                cards.Add(PublishedCard(model.ProviderKind, usageKind, usdPerUnit, effectiveFrom, model.Model, model.Basis));
            }
        }

        // Wildcard fallbacks, one per (provider kind, usage kind) the capability matrix requires.
        foreach ((InferenceProviderKind kind, UsageKind usageKind) in ProviderCapabilityUsage.RequiredCards())
        {
            if (!seen.Add((kind, usageKind, "*"))) continue;
            cards.Add(Card(kind, usageKind, FallbackUsdPerUnit(usageKind), effectiveFrom, "*",
                FallbackBasis(usageKind)));
        }

        return cards;
    }

    /// <summary>The compute usage kinds, which an existing install lacks cards for until <see cref="ComputeCards"/> is applied.</summary>
    public static readonly UsageKind[] ComputeKinds =
        [UsageKind.RenderOutputSeconds, UsageKind.EncodeOutputSeconds, UsageKind.AnalyzeMediaSeconds];

    /// <summary>
    /// The short side of the frame each tier bills at, so a tier's cost multiplier is the pixel-count
    /// ratio to 720p (2.25x for 1080p, 9x for UHD) rather than a hand-set number.
    /// </summary>
    private static readonly (string Tier, int ShortSide)[] ComputeTiers =
    [
        (ComputeMetering.Tier720, 720),
        (ComputeMetering.Tier1080, 1080),
        (ComputeMetering.TierUhd, 2160)
    ];

    /// <summary>
    /// Compute cards (B3c), one per usage kind and resolution tier, filed under
    /// <see cref="ComputeProviderKind"/> with the tier as the exact <c>model_pattern</c>
    /// (<see cref="ComputeMetering.TierFor"/>). Every figure descends from
    /// <see cref="ComputeRenderUsdPerSecond"/> — B0's researched VM anchor — scaled by the tier's pixel
    /// count, with encode and analyse as fixed shares of render. A runner job is billed under
    /// <c>Runner</c> and costs the user nothing by default (the multiplier seed), so these rows price the
    /// platform-executed path only.
    /// </summary>
    public static IReadOnlyList<RateCard> ComputeCards(DateTime effectiveFrom, decimal? renderAnchor = null)
    {
        decimal anchor = renderAnchor ?? ComputeRenderUsdPerSecond;
        List<RateCard> cards = [];
        int baseShortSide = ComputeTiers[0].ShortSide;
        foreach ((string tier, int shortSide) in ComputeTiers)
        {
            decimal pixels = (decimal)shortSide * shortSide;
            decimal tierFactor = pixels / ((decimal)baseShortSide * baseShortSide);
            decimal render = anchor * tierFactor;
            string basis = $"B0's $0.25/hour, 2 vCPU effective anchor ({anchor:0.#######}/720p-second) x pixel ratio {tierFactor:0.##}; estimated conversion, not measured";

            cards.Add(Card(ComputeProviderKind, UsageKind.RenderOutputSeconds, render, effectiveFrom, tier, basis));
            cards.Add(Card(ComputeProviderKind, UsageKind.EncodeOutputSeconds, render * ComputeEncodeShare, effectiveFrom, tier,
                basis + $"; encode = {ComputeEncodeShare:0.##} of render"));
            cards.Add(Card(ComputeProviderKind, UsageKind.AnalyzeMediaSeconds, render * ComputeAnalyzeShare, effectiveFrom, tier,
                basis + $"; analyse = {ComputeAnalyzeShare:0.##} of render"));
        }

        return cards;
    }

    public static IReadOnlyList<RateMultiplier> Multipliers(DateTime effectiveFrom) =>
    [
        Multiplier(UsageBillingMode.Platform, 1m, effectiveFrom),
        Multiplier(UsageBillingMode.Byo, 0m, effectiveFrom),
        Multiplier(UsageBillingMode.Runner, 0m, effectiveFrom)
    ];

    /// <summary>
    /// The note a row seeded from <see cref="ModelPriceCatalog"/> carries. It keeps the
    /// <c>PLACEHOLDER</c> marker word — which is load bearing: the admin table badges on it and
    /// <c>infra/loadtest/x1-cost.mjs</c> keeps its invoice-comparison lane BLOCKED while any active card
    /// carries it — but says what the row actually is. A published list price is a better number than an
    /// estimate and a worse one than an invoice, so the invoice-comparison lane is right to stay blocked.
    /// </summary>
    public static string PublishedNote(string basis) =>
        $"{PlaceholderMarker} (published vendor list price, not reconciled against a provider invoice) - Basis: {basis}.";

    private static RateCard PublishedCard(
        InferenceProviderKind provider, UsageKind kind, decimal costUsdPerUnit, DateTime effectiveFrom, string modelPattern, string basis) => new()
        {
            Id = Guid.NewGuid(),
            ProviderKind = provider,
            ModelPattern = modelPattern,
            UsageKind = kind,
            CostUsdPerUnit = costUsdPerUnit,
            CreditsMillisPerUnit = costUsdPerUnit * CreditsMillisPerUsd,
            EffectiveFrom = effectiveFrom,
            Notes = PublishedNote(basis)
        };

    private static RateCard Card(
        InferenceProviderKind provider, UsageKind kind, decimal costUsdPerUnit, DateTime effectiveFrom, string modelPattern = "*", string? basis = null) => new()
        {
            Id = Guid.NewGuid(),
            ProviderKind = provider,
            ModelPattern = modelPattern,
            UsageKind = kind,
            CostUsdPerUnit = costUsdPerUnit,
            CreditsMillisPerUnit = costUsdPerUnit * CreditsMillisPerUsd,
            EffectiveFrom = effectiveFrom,
            Notes = basis is null ? PlaceholderNote : $"{PlaceholderNote} Basis: {basis}."
        };

    private static RateMultiplier Multiplier(UsageBillingMode mode, decimal value, DateTime effectiveFrom) => new()
    {
        Id = Guid.NewGuid(),
        BillingMode = mode,
        UsageKind = null,
        Multiplier = value,
        EffectiveFrom = effectiveFrom
    };
}
