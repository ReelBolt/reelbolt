using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// One model the platform supports, the capability it serves for its provider kind, and what a unit of
/// each usage kind it can produce costs us. <see cref="UsdPerUnit"/> empty means <b>documented but not
/// priced from a published source</b> — such a model is not a card, it is a named gap, and it falls
/// back to its provider kind's wildcard estimate.
/// </summary>
public sealed record ModelPrice(
    InferenceProviderKind ProviderKind,
    InferenceProviderCapability Capability,
    string Model,
    IReadOnlyDictionary<UsageKind, decimal> UsdPerUnit,
    string Basis)
{
    /// <summary>True when this model has at least one published price behind it.</summary>
    public bool IsPriced => UsdPerUnit.Count > 0;
}

/// <summary>
/// The per-model price list, and the one place a new model's cost is declared.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where these numbers come from, and what they are not.</b> Every priced row below is a
/// <i>published vendor list price</i>, read from the vendor's own documentation on the date named in
/// its <see cref="ModelPrice.Basis"/>, or from this repository's own recorded vendor figure where that
/// is the primary source available. None of them is <i>measured</i>: nothing here has been reconciled
/// against a provider invoice, which is why every price the price book pre-fills from this
/// catalog still carries the <c>PLACEHOLDER</c> marker word and
/// <c>infra/loadtest/x1-cost.mjs</c> keeps its invoice-comparison lane BLOCKED. A list price is a
/// better number than an estimate and a worse number than an invoice.
/// </para>
/// <para>
/// <b>What is deliberately not priced, and why.</b> A model this file cannot source is listed with an
/// empty <see cref="ModelPrice.UsdPerUnit"/> rather than being given an invented figure, and
/// the price book leaves it unpriced (a <see cref="PriceCoverage"/> gap until an admin prices it). The unpriced entries today are the
/// <c>AzureOpenAI</c> and <c>OpenAICompatible</c> chat models (the vendor's per-model price table was
/// not retrievable on 2026-10-09, so they keep the B0 Mid estimate) and the two self-hosted decision
/// kinds (TypeSafe and OpenJev have no vendor list price at all — an operator supplies the endpoint).
/// </para>
/// <para>
/// <b>Unit conventions.</b> Token prices are USD per single token, not per million; audio prices are
/// USD per second; TTS is USD per UTF-8 byte; generated video is USD per second. Each is the same unit
/// as its <see cref="UsageKind"/> (docs/metering-and-billing.md "Usage kinds"), so the catalog entry
/// and <c>rate_cards.cost_usd_per_unit</c> are the same number and no conversion can drift.
/// </para>
/// </remarks>
public static partial class ModelPriceCatalog
{
    private const decimal PerMillionTokens = 1_000_000m;
    private const decimal SecondsPerHour = 3_600m;

    /// <summary>Tokens: dollars per million tokens to dollars per token.</summary>
    private static decimal PerMtok(decimal usdPerMillion) => usdPerMillion / PerMillionTokens;

    /// <summary>Source line for Anthropic's own pricing page, read 2026-10-09.</summary>
    private const string AnthropicSource =
        "published vendor list price, Anthropic (platform.claude.com/docs/en/about-claude/pricing), read 2026-10-09";

    /// <summary>Source line for DeepSeek's own pricing page, read 2026-10-09.</summary>
    private const string DeepSeekSource =
        "published vendor list price, DeepSeek (api-docs.deepseek.com/quick_start/pricing), read 2026-10-09";

    /// <summary>Source line for Fish Audio's own pricing page, read 2026-10-09.</summary>
    private const string FishAudioSource =
        "published vendor list price, Fish Audio (docs.fish.audio/developer-guide/models-pricing/pricing-and-rate-limits), read 2026-10-09";

    /// <summary>
    /// The models the platform supports, one entry per (provider kind, capability, model). A model that
    /// serves two capabilities of one kind (a chat model that also answers vision calls) appears twice;
    /// the price book matches entries by the units a capability meters, so either entry prices both.
    /// </summary>
    public static readonly IReadOnlyList<ModelPrice> All =
    [
        // ---------------------------------------------------------------- Anthropic (Chat + Vision)
        new(InferenceProviderKind.Anthropic, InferenceProviderCapability.Chat, "claude-fable-5-1",
            Tokens(10m, 50m, 0.25m), AnthropicSource),
        new(InferenceProviderKind.Anthropic, InferenceProviderCapability.Vision, "claude-fable-5-1",
            Tokens(10m, 50m, 0.25m), AnthropicSource),
        new(InferenceProviderKind.Anthropic, InferenceProviderCapability.Chat, "claude-opus-5-5",
            Tokens(4m, 20m, 0.20m), AnthropicSource),
        new(InferenceProviderKind.Anthropic, InferenceProviderCapability.Vision, "claude-opus-5-5",
            Tokens(4m, 20m, 0.20m), AnthropicSource),
        new(InferenceProviderKind.Anthropic, InferenceProviderCapability.Chat, "claude-sonnet-5-5",
            Tokens(2m, 10m, 0.10m), AnthropicSource),
        new(InferenceProviderKind.Anthropic, InferenceProviderCapability.Vision, "claude-sonnet-5-5",
            Tokens(2m, 10m, 0.10m), AnthropicSource),
        // Haiku 5.5 is the one model with a two-tier price. The card keys on the model name and cannot
        // express the tier, so the <=100k-token tier is the card and the long-context tier is recorded
        // here rather than dropped.
        new(InferenceProviderKind.Anthropic, InferenceProviderCapability.Chat, "claude-haiku-5-5",
            Tokens(0.10m, 0.50m, 0.01m),
            AnthropicSource + "; the <=100,000-token tier — a prompt over that is billed at " +
            "$2.50 in / $5.00 out / $0.05 cache-hit per MTok, which one model-keyed card cannot express"),
        new(InferenceProviderKind.Anthropic, InferenceProviderCapability.Vision, "claude-haiku-5-5",
            Tokens(0.10m, 0.50m, 0.01m),
            AnthropicSource + "; the <=100,000-token tier, as above"),

        // ---------------------------------------------------------------- DeepSeek (Chat + Vision + Decision)
        // DeepSeek's page prices PEAK and OFF-PEAK (off-peak is half), and a card cannot express a
        // time of day. Peak is taken, which is the same conservative (upper) direction
        // PricingDefaults.ComputeRenderUsdPerSecond already takes, and the basis says so.
        new(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Chat, "deepseek-flash",
            Tokens(0.30m, 1.20m, 0.006m),
            DeepSeekSource + "; PEAK-hour rate (01:00-04:00 and 06:00-10:00 UTC Mon-Fri) — the off-peak " +
            "rate is half of it, and one model-keyed card cannot express both"),
        new(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Vision, "deepseek-flash",
            Tokens(0.30m, 1.20m, 0.006m), DeepSeekSource + "; PEAK-hour rate, as above"),
        new(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Decision, "deepseek-flash",
            Tokens(0.30m, 1.20m, 0.006m), DeepSeekSource + "; PEAK-hour rate, as above"),
        new(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Chat, "deepseek-v4-pro",
            Tokens(1.32m, 3.96m, 0.044m), DeepSeekSource + "; PEAK-hour rate, as above"),
        new(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Vision, "deepseek-v4-pro",
            Tokens(1.32m, 3.96m, 0.044m), DeepSeekSource + "; PEAK-hour rate, as above"),
        new(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Decision, "deepseek-v4-pro",
            Tokens(1.32m, 3.96m, 0.044m), DeepSeekSource + "; PEAK-hour rate, as above"),
        // Legacy names still accepted by the API and still present in deployed provider rows, but the
        // 2026 pricing page does not price them: named, unpriced, and left on the wildcard estimate.
        new(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Chat, "deepseek-chat",
            NoPrice,
            "no published price found: DeepSeek's 2026 pricing page lists deepseek-flash and " +
            "deepseek-v4-pro only, and does not price the legacy deepseek-chat name (checked 2026-10-09)"),
        new(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Chat, "deepseek-reasoner",
            NoPrice,
            "no published price found: DeepSeek's 2026 pricing page lists deepseek-flash and " +
            "deepseek-v4-pro only, and does not price the legacy deepseek-reasoner name (checked 2026-10-09)"),

        // ---------------------------------------------------------------- Azure OpenAI + OpenAI-compatible
        // The two kinds share one model list, because an OpenAI-compatible endpoint serving gpt-4o
        // costs whatever OpenAI charges for it. Audio and embedding rows come from this repository's
        // own recorded vendor figures (docs/design/cloud-provider-adr.md section 3, read 2026-10-05),
        // which carried a third-party confidence tag — stated, not hidden.
        new(InferenceProviderKind.AzureOpenAI, InferenceProviderCapability.Transcription, "whisper-1",
            new Dictionary<UsageKind, decimal> { [UsageKind.AsrAudioSeconds] = 0.006m / 60m },
            OpenAiAudioSource),
        new(InferenceProviderKind.AzureOpenAI, InferenceProviderCapability.Transcription, "gpt-4o-transcribe",
            new Dictionary<UsageKind, decimal> { [UsageKind.AsrAudioSeconds] = 0.006m / 60m },
            OpenAiAudioSource),
        new(InferenceProviderKind.AzureOpenAI, InferenceProviderCapability.Transcription, "gpt-4o-mini-transcribe",
            new Dictionary<UsageKind, decimal> { [UsageKind.AsrAudioSeconds] = 0.003m / 60m },
            OpenAiAudioSource),
        new(InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.Transcription, "whisper-1",
            new Dictionary<UsageKind, decimal> { [UsageKind.AsrAudioSeconds] = 0.006m / 60m },
            OpenAiAudioSource),
        new(InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.Transcription, "gpt-4o-transcribe",
            new Dictionary<UsageKind, decimal> { [UsageKind.AsrAudioSeconds] = 0.006m / 60m },
            OpenAiAudioSource),
        new(InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.Transcription, "gpt-4o-mini-transcribe",
            new Dictionary<UsageKind, decimal> { [UsageKind.AsrAudioSeconds] = 0.003m / 60m },
            OpenAiAudioSource),
        // OpenAI's moderation models are free to use (developers.openai.com/api/docs/pricing,
        // read 2026-10-21: "$0 / 1M tokens" for omni-moderation-latest). This is a REAL $0 price,
        // not an unpriced gap: the capability meters calls so the integration is visible in the
        // ledger at zero cost rather than silently absent.
        new(InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.ContentModeration, "omni-moderation-latest",
            new Dictionary<UsageKind, decimal> { [UsageKind.ModerationCalls] = 0m },
            "published vendor list price, OpenAI (developers.openai.com/api/docs/pricing), read 2026-10-21: " +
            "the moderation models are free; $0 per call is the vendor's own figure, not a missing price"),
        // Self-hosted reranker (Hugging Face text-embeddings-inference serving BAAI/bge-reranker-base beside the
        // `embeddings` service): the operator runs it, so a call costs the platform nothing per search. A real $0
        // price, the same precedent as ContentModeration above. NO hosted-vendor rerank price is seeded: a hosted
        // reranker (Cohere, Jina, Voyage...) stays unpriced, which makes the capability read "rerank unavailable" and
        // list in GET /api/v1/pricing/coverage until an admin sources and enters its published price.
        new(InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.Rerank, "BAAI/bge-reranker-base",
            new Dictionary<UsageKind, decimal> { [UsageKind.RerankSearches] = 0m },
            "self-hosted open-weights reranker (BAAI/bge-reranker-base served by text-embeddings-inference): " +
            "the operator's own container, no per-search vendor charge; $0 is the real cost, not a missing price"),
        // Cloudflare Workers AI native rerank (the staging reranker). Published price: $0.00311 per M input tokens
        // (developers.cloudflare.com/workers-ai/models/bge-reranker-base; pricing page
        // developers.cloudflare.com/workers-ai/platform/pricing/ read 2026-10-24, "Last updated Oct 20, 2026").
        // Per search, worst case: 20 candidates x (<=500-char query + <=600-char text) ~ 20 x 275 tokens ~= 5,500
        // input tokens => ~$0.000017, rounded UP to $0.00002. An upper bound; the actual charge is lower.
        new(InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.Rerank, "@cf/baai/bge-reranker-base",
            new Dictionary<UsageKind, decimal> { [UsageKind.RerankSearches] = 0.00002m },
            "published vendor list price, Cloudflare Workers AI (developers.cloudflare.com/workers-ai/platform/pricing/), " +
            "read 2026-10-24: $0.00311 per M input tokens; worst case 20 x ~275 tokens ~= 5,500 tokens ~= $0.000017 per " +
            "search, rounded up to $0.00002 (an upper bound, actual is lower)"),
        // ---------------------------------------------------------------- Web search (agent-web-search D-1,
        // vendor pricing retrieved 2026-10-25; RE-CHECK BEFORE LAUNCH). One unit = one search query/action.
        // Tokens of an OpenAI/Gemini search call are metered under the Chat capability, not here.
        new(InferenceProviderKind.DeepSeek, InferenceProviderCapability.WebSearch, "deepseek-search",
            new Dictionary<UsageKind, decimal> { [UsageKind.WebSearches] = 0.00075m },
            "published vendor list price, DeepSeek Search API ($0.75 per 1,000 calls, billed on non-empty results), " +
            "agent-web-search D-1, retrieved 2026-10-25; re-check before launch"),
        new(InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.WebSearch, "gpt-4o-mini",
            new Dictionary<UsageKind, decimal> { [UsageKind.WebSearches] = 0.01m },
            "published vendor list price, OpenAI Responses web_search tool ($10 per 1,000 calls, tokens billed " +
            "separately), agent-web-search D-1, retrieved 2026-10-25; re-check before launch"),
        new(InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.WebSearch, "gpt-4o",
            new Dictionary<UsageKind, decimal> { [UsageKind.WebSearches] = 0.01m },
            "published vendor list price, OpenAI Responses web_search tool ($10 per 1,000 calls, tokens billed " +
            "separately), agent-web-search D-1, retrieved 2026-10-25; re-check before launch"),
        new(InferenceProviderKind.Gemini, InferenceProviderCapability.WebSearch, "gemini-3.8-flash",
            new Dictionary<UsageKind, decimal> { [UsageKind.WebSearches] = 0.014m },
            "published vendor list price, Google Gemini 3+ Grounding with Google Search ($14 per 1,000 search " +
            "queries), agent-web-search D-1, retrieved 2026-10-25; re-check before launch"),
        new(InferenceProviderKind.Gemini, InferenceProviderCapability.WebSearch, "gemini-2.5-pro",
            new Dictionary<UsageKind, decimal> { [UsageKind.WebSearches] = 0.035m },
            "published vendor list price, Google Gemini 2.5 Grounding with Google Search ($35 per 1,000 grounded " +
            "prompts), agent-web-search D-1, retrieved 2026-10-25; re-check before launch"),
        new(InferenceProviderKind.Gemini, InferenceProviderCapability.WebSearch, "gemini-2.5-flash",
            new Dictionary<UsageKind, decimal> { [UsageKind.WebSearches] = 0.035m },
            "published vendor list price, Google Gemini 2.5 Grounding with Google Search ($35 per 1,000 grounded " +
            "prompts), agent-web-search D-1, retrieved 2026-10-25; re-check before launch"),
        new(InferenceProviderKind.AzureOpenAI, InferenceProviderCapability.Embedding, "text-embedding-3-small",
            new Dictionary<UsageKind, decimal> { [UsageKind.EmbeddingTokens] = PerMtok(0.02m) },
            OpenAiEmbeddingSource),
        new(InferenceProviderKind.AzureOpenAI, InferenceProviderCapability.Embedding, "text-embedding-3-large",
            NoPrice,
            "no published price read: only text-embedding-3-small was recorded in this repository " +
            "(docs/design/cloud-provider-adr.md section 3) and no vendor page was retrievable on 2026-10-09"),
        new(InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.Embedding, "text-embedding-3-small",
            new Dictionary<UsageKind, decimal> { [UsageKind.EmbeddingTokens] = PerMtok(0.02m) },
            OpenAiEmbeddingSource),
        new(InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.Embedding, "text-embedding-3-large",
            NoPrice,
            "no published price read, as above"),
        // A locally hosted OpenAI-compatible endpoint (vLLM, Ollama, the bundled speaches container)
        // costs the platform nothing, and this is the row that says so out loud.
        new(InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.Chat, "local",
            new Dictionary<UsageKind, decimal>
            {
                [UsageKind.LlmInputTokens] = 0m, [UsageKind.LlmOutputTokens] = 0m, [UsageKind.LlmCachedInputTokens] = 0m
            },
            "self-hosted OpenAI-compatible endpoint: the platform pays no per-token vendor charge, so the " +
            "card is zero — compute for it is metered separately under the compute kinds"),
        // Unpriced chat models named in this repository but not sourceable on 2026-10-09.
        new(InferenceProviderKind.AzureOpenAI, InferenceProviderCapability.Chat, "gpt-4o", NoPrice, OpenAiChatUnpriced),
        new(InferenceProviderKind.AzureOpenAI, InferenceProviderCapability.Chat, "gpt-4o-mini", NoPrice, OpenAiChatUnpriced),
        new(InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.Chat, "gpt-4o", NoPrice, OpenAiChatUnpriced),
        new(InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.Chat, "gpt-4o-mini", NoPrice, OpenAiChatUnpriced),

        // ---------------------------------------------------------------- Gemini
        new(InferenceProviderKind.Gemini, InferenceProviderCapability.Chat, "gemini-3.8-flash", NoPrice, GeminiUnpriced),
        new(InferenceProviderKind.Gemini, InferenceProviderCapability.Chat, "gemini-2.5-pro", NoPrice, GeminiUnpriced),
        new(InferenceProviderKind.Gemini, InferenceProviderCapability.Chat, "gemini-2.5-flash", NoPrice, GeminiUnpriced),

        // ---------------------------------------------------------------- Fish Audio
        new(InferenceProviderKind.FishAudio, InferenceProviderCapability.SpeechSynthesis, "s2.1-pro",
            new Dictionary<UsageKind, decimal> { [UsageKind.TtsInputBytes] = 15m / 1_000_000m }, FishAudioTtsSource),
        new(InferenceProviderKind.FishAudio, InferenceProviderCapability.SpeechSynthesis, "s2-pro",
            new Dictionary<UsageKind, decimal> { [UsageKind.TtsInputBytes] = 15m / 1_000_000m }, FishAudioTtsSource),
        new(InferenceProviderKind.FishAudio, InferenceProviderCapability.SpeechSynthesis, "s1",
            new Dictionary<UsageKind, decimal> { [UsageKind.TtsInputBytes] = 15m / 1_000_000m },
            FishAudioTtsSource + "; deprecated, retiring 2026-12-31, after which s1 requests are served by " +
            "s2.1-pro at the same price"),
        new(InferenceProviderKind.FishAudio, InferenceProviderCapability.SpeechSynthesis, "s2.1-pro-free",
            new Dictionary<UsageKind, decimal> { [UsageKind.TtsInputBytes] = 0m },
            FishAudioTtsSource + "; the free tier is published at $0.00 per M UTF-8 bytes, which is a real " +
            "price and not an unpriced row"),
        // The ASR half — the pair that was missing and produced the unpriced staging row.
        new(InferenceProviderKind.FishAudio, InferenceProviderCapability.Transcription, "transcribe-1-pro",
            new Dictionary<UsageKind, decimal> { [UsageKind.AsrAudioSeconds] = 0.36m / SecondsPerHour },
            FishAudioAsrSource),
        new(InferenceProviderKind.FishAudio, InferenceProviderCapability.Transcription, "transcribe-1",
            new Dictionary<UsageKind, decimal> { [UsageKind.AsrAudioSeconds] = 0.36m / SecondsPerHour },
            FishAudioAsrSource),

        // ---------------------------------------------------------------- MiniMax (video generation)
        // Source: the platform's own vendor table, MiniMaxVideoGenerationClient.Describe, which is what
        // the fail-closed budget gate (VideoGenerationPricing) already prices every take against.
        new(InferenceProviderKind.MiniMax, InferenceProviderCapability.VideoGeneration, "MiniMax-H3",
            new Dictionary<UsageKind, decimal> { [UsageKind.VideoGenSeconds] = 0.08m },
            MiniMaxSource + "; 768P. The same model is $0.13/s at 2K, and a model-keyed card cannot " +
            "express the resolution — a 2K take is therefore under-priced by this row"),
        new(InferenceProviderKind.MiniMax, InferenceProviderCapability.VideoGeneration, "MiniMax-H3-Max",
            new Dictionary<UsageKind, decimal> { [UsageKind.VideoGenSeconds] = 0.08m },
            MiniMaxSource + "; 768P, reconciling with the measured spend ledger ($9.20 over 18 succeeded " +
            "jobs is $0.511 per clip at about 6.4 s). The same model is $0.05/s at 480P"),
        new(InferenceProviderKind.MiniMax, InferenceProviderCapability.VideoGeneration, "MiniMax-H3-Highspeed",
            new Dictionary<UsageKind, decimal> { [UsageKind.VideoGenSeconds] = 0.06m },
            MiniMaxSource + "; 768P (pricing page read 2026-11-05). The same model is $0.04/s at 480P"),
        new(InferenceProviderKind.MiniMax, InferenceProviderCapability.VideoGeneration, "MiniMax-H3-Lite",
            new Dictionary<UsageKind, decimal> { [UsageKind.VideoGenSeconds] = 0.04m },
            MiniMaxSource + "; 768P (pricing page read 2026-11-05). The same model is $0.025/s at 480P"),

        // ---------------------------------------------------------------- MiniMax (images)
        // Source: MiniMax's own pricing page (platform.minimax.io/docs/pricing/overview), section
        // "#models/image", read 2026-10-09: "| image-01 | $0.0035 per image |". The page prices the
        // MODEL rather than the operation, and subject_reference — MiniMax's image-to-image path —
        // is the same endpoint and the same model, so both capabilities carry the published figure
        // instead of an edit price MiniMax never published.
        new(InferenceProviderKind.MiniMax, InferenceProviderCapability.ImageGeneration, "image-01",
            PerImage(UsageKind.ImageGenImages, 0.0035m), MiniMaxImageSource),
        new(InferenceProviderKind.MiniMax, InferenceProviderCapability.ImageEdit, "image-01",
            PerImage(UsageKind.ImageEditImages, 0.0035m),
            MiniMaxImageSource + "; subject_reference is documented on the same model at the same " +
            "endpoint, and MiniMax publishes no separate editing price"),

        // ---------------------------------------------------------------- OpenAI image models (named, unpriced)
        // OpenAI bills its image models PER TOKEN — "per 1M tokens, not per image" on its pricing
        // page — so no per-image figure exists to cite and the usage kinds these capabilities meter
        // cannot express a token price. Listed rather than omitted so the gap is named, and left on
        // the derived wildcard so a call is estimated rather than recorded at zero.
        new(InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.ImageGeneration, "gpt-image-2",
            NoPrice, OpenAiImageUnpriced),
        new(InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.ImageEdit, "gpt-image-2",
            NoPrice, OpenAiImageUnpriced),
        new(InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.ImageGeneration, "gpt-image-1",
            NoPrice, OpenAiImageUnpriced),
        new(InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.ImageEdit, "gpt-image-1",
            NoPrice, OpenAiImageUnpriced),

        // ---------------------------------------------------------------- Black Forest Labs (FLUX)
        // Source: BFL's own pricing page (docs.bfl.ml/quick_start/pricing), read 2026-10-09. Every
        // FLUX.2 figure is quoted there as "from" that amount because FLUX.2 is megapixel-priced:
        // "the first megapixel costs a flat rate, then each additional MP adds to the total". A rate
        // card keys on the model name and cannot express a resolution or a megapixel count, so the
        // base figure is the card and the basis says so — a 4 MP request costs MORE than this row.
        // Note the generation/editing split on FLUX.2 [pro]: $0.03 against $0.045. That 50% gap is
        // the concrete reason ImageGeneration and ImageEdit meter different usage kinds.
        new(InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageGeneration, "flux-2-klein-4b",
            PerImage(UsageKind.ImageGenImages, 0.014m), BflImageSource + "; FLUX.2 [klein] 4B, from $0.014 text-to-image (first MP flat, +$0.001 per additional MP — BFL's own worked example)"),
        new(InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageEdit, "flux-2-klein-4b",
            PerImage(UsageKind.ImageEditImages, 0.014m), BflImageSource + "; FLUX.2 [klein] 4B, from $0.014 image editing"),
        new(InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageGeneration, "flux-2-klein-9b",
            PerImage(UsageKind.ImageGenImages, 0.015m), BflImageSource + "; FLUX.2 [klein] 9B, from $0.015 text-to-image"),
        new(InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageEdit, "flux-2-klein-9b",
            PerImage(UsageKind.ImageEditImages, 0.015m), BflImageSource + "; FLUX.2 [klein] 9B, from $0.015 image editing"),
        new(InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageGeneration, "flux-2-pro",
            PerImage(UsageKind.ImageGenImages, 0.03m), BflImageSource + "; FLUX.2 [pro], from $0.03 text-to-image"),
        new(InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageEdit, "flux-2-pro",
            PerImage(UsageKind.ImageEditImages, 0.045m), BflImageSource + "; FLUX.2 [pro], from $0.045 image editing — half again its own text-to-image price, the measured reason the two capabilities meter different usage kinds"),
        new(InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageGeneration, "flux-2-flex",
            PerImage(UsageKind.ImageGenImages, 0.05m), BflImageSource + "; FLUX.2 [flex], from $0.05 text-to-image"),
        new(InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageEdit, "flux-2-flex",
            PerImage(UsageKind.ImageEditImages, 0.05m), BflImageSource + "; FLUX.2 [flex], from $0.05 image editing"),
        new(InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageGeneration, "flux-2-max",
            PerImage(UsageKind.ImageGenImages, 0.07m), BflImageSource + "; FLUX.2 [max], from $0.07 text-to-image"),
        new(InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageEdit, "flux-2-max",
            PerImage(UsageKind.ImageEditImages, 0.07m), BflImageSource + "; FLUX.2 [max], from $0.07 image editing"),
        new(InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageGeneration, "flux-kontext-pro",
            PerImage(UsageKind.ImageGenImages, 0.04m), BflImageSource + "; FLUX.1 Kontext [pro], 4 credits = $0.04, which BFL describes as create-and-edit with text and images"),
        new(InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageEdit, "flux-kontext-pro",
            PerImage(UsageKind.ImageEditImages, 0.04m), BflImageSource + "; FLUX.1 Kontext [pro], 4 credits = $0.04"),
        new(InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageGeneration, "flux-kontext-max",
            PerImage(UsageKind.ImageGenImages, 0.08m), BflImageSource + "; FLUX.1 Kontext [max], 8 credits = $0.08"),
        new(InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageEdit, "flux-kontext-max",
            PerImage(UsageKind.ImageEditImages, 0.08m), BflImageSource + "; FLUX.1 Kontext [max], 8 credits = $0.08"),
        new(InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageGeneration, "flux-pro-1.1",
            PerImage(UsageKind.ImageGenImages, 0.04m), BflImageSource + "; FLUX1.1 [pro], 4 credits = $0.04. Generation only: BFL lists it as text-to-image with no edit operation, which is why it has no ImageEdit entry"),
        new(InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageEdit, "flux-pro-1.0-fill",
            PerImage(UsageKind.ImageEditImages, 0.05m), BflImageSource + "; FLUX.1 Fill [pro], 5 credits = $0.05 — the mask-inpainting model, and the only BFL model whose documentation defines image plus mask (black preserved, white inpainted)"),
        // ---------------------------------------------------------------- fal.ai (segmentation and depth)
        // Every one of these is NAMED AND UNPRICED, which is the honest state rather than a gap that
        // was overlooked: fal publishes no per-output price for any of them, and its own pricing page
        // states that a model with no fixed price falls back to per-second billing on the GPU machine
        // type. There is therefore no per-image list price to record here, and inventing one by
        // assuming a duration would be exactly the kind of guess this catalogue exists to prevent —
        // the duration belongs on the RATE CARD's basis, where it is labelled as an assumption. See
        // the pre-catalog seed's FalGpuListUsdPerHour and FalSecondsPerOutputImage (ported once as rows).
        new(InferenceProviderKind.FalAi, InferenceProviderCapability.Segmentation, "fal-ai/evf-sam", NoPrice, FalUnpriced),
        new(InferenceProviderKind.FalAi, InferenceProviderCapability.Segmentation, "fal-ai/sam2/image", NoPrice, FalUnpriced),
        new(InferenceProviderKind.FalAi, InferenceProviderCapability.Segmentation, "fal-ai/birefnet/v2", NoPrice, FalUnpriced),
        new(InferenceProviderKind.FalAi, InferenceProviderCapability.Depth, "fal-ai/imageutils/depth", NoPrice, FalUnpriced),
        new(InferenceProviderKind.FalAi, InferenceProviderCapability.Depth, "fal-ai/imageutils/marigold-depth", NoPrice, FalUnpriced),

        // ---------------------------------------------------------------- fal.ai (SAM 3 and the image endpoints)
        // Priced from the model pages; see ModelPriceCatalog.FalImages.cs for the three rules that
        // express fal's tier, per-megapixel and token prices as model-keyed cards.
        .. FalImagePrices(),

        // ---------------------------------------------------------------- fal.ai (video)
        // USD per second of generated video, read from each endpoint's own pricing sentence; see
        // ModelPriceCatalog.FalVideos.cs for why a card is keyed on the endpoint's DEFAULT resolution
        // rather than on a resolution the key cannot carry.
        .. FalVideoPrices(),

        // ---------------------------------------------------------------- fal.ai (text-prompted video segmentation)
        // USD per FRAME of video input, with the vendor's 16-frame block divided into the card; see
        // ModelPriceCatalog.FalVideoSegmentation.cs and VideoSegmentationBilling for the rounding.
        .. FalVideoSegmentationPrices(),
        // ---------------------------------------------------------------- music and sound effects (fal + ElevenLabs)
        // See ModelPriceCatalog.Audio.cs: billed minutes of music, seconds of sound effect.
        .. AudioPrices(),

        // ---------------------------------------------------------------- hosted voice isolation (fal)
        // See ModelPriceCatalog.AudioIsolation.cs: billed minutes of source audio, a row only where a price is cited.
        .. AudioIsolationPrices(),

        // ---------------------------------------------------------------- hosted video enhancement (fal)
        // See ModelPriceCatalog.VideoEnhance.cs: per OUTPUT second by tier (upscale), per compute second (interpolation).
        .. VideoEnhancePrices(),

        // ---------------------------------------------------------------- self-hosted decision kinds
        new(InferenceProviderKind.TypeSafe, InferenceProviderCapability.Decision, "jev-latest", NoPrice,
            "self-hosted System One endpoint: the operator supplies the host, so there is no vendor list " +
            "price to record and the wildcard estimate stands until an invoice exists"),
        new(InferenceProviderKind.OpenJev, InferenceProviderCapability.Decision, "jev-latest", NoPrice,
            "self-hosted System One endpoint, as above")
    ];

    /// <summary>The entries with a published price, which are the ones that become rate cards.</summary>
    public static IReadOnlyList<ModelPrice> Priced { get; } = All.Where(m => m.IsPriced).ToList();

    /// <summary>
    /// The entries that are documented but carry no published price. These are not failures — they are
    /// the list a human needs to see.
    /// </summary>
    public static IReadOnlyList<ModelPrice> Unpriced { get; } = All.Where(m => !m.IsPriced).ToList();

    /// <summary>The priced models of one provider kind, for a report or a test.</summary>
    public static IReadOnlyList<ModelPrice> PricedFor(InferenceProviderKind kind) =>
        Priced.Where(m => m.ProviderKind == kind).ToList();

    private static IReadOnlyDictionary<UsageKind, decimal> NoPrice => new Dictionary<UsageKind, decimal>();

    /// <summary>One image at a published per-image price, in the unit its capability meters.</summary>
    private static IReadOnlyDictionary<UsageKind, decimal> PerImage(UsageKind kind, decimal usdPerImage) =>
        new Dictionary<UsageKind, decimal> { [kind] = usdPerImage };

    private const string FalUnpriced =
        "no published price in a per-image unit: fal does not list a per-output price for its " +
        "segmentation or depth endpoints (checked at fal.ai/pricing on 2026-10-10), and its Model APIs " +
        "pricing page states that a model with no fixed per-output price falls back to per-second " +
        "billing on the GPU machine type. The wildcard card carries the derived figure and its stated " +
        "assumption (ported from the pre-catalog rate-card seed's FalGpuListUsdPerHour).";

    private const string MiniMaxImageSource =
        "published vendor list price, MiniMax pricing overview " +
        "(platform.minimax.io/docs/pricing/overview), section #models/image, read 2026-10-09";

    private const string BflImageSource =
        "published vendor list price, Black Forest Labs (docs.bfl.ml/quick_start/pricing), read " +
        "2026-10-09, where 1 credit = $0.01 and every FLUX.2 figure is quoted as \"from\" because " +
        "FLUX.2 is megapixel-priced";

    private const string OpenAiImageUnpriced =
        "no published price in a per-image unit: OpenAI bills its image models per 1M tokens " +
        "(input, cached input and output), not per image, and the usage kinds the image capabilities " +
        "meter count images — so a per-image figure would have to be invented by assuming a token " +
        "count. The model keeps the derived wildcard estimate rather than a made-up number.";

    /// <summary>The three LLM token kinds at a published dollars-per-million-token rate.</summary>
    private static IReadOnlyDictionary<UsageKind, decimal> Tokens(decimal input, decimal output, decimal cachedInput) =>
        new Dictionary<UsageKind, decimal>
        {
            [UsageKind.LlmInputTokens] = PerMtok(input),
            [UsageKind.LlmOutputTokens] = PerMtok(output),
            [UsageKind.LlmCachedInputTokens] = PerMtok(cachedInput)
        };

    private const string OpenAiAudioSource =
        "published vendor list price recorded in this repository (docs/design/cloud-provider-adr.md " +
        "section 3 'Hosted ASR', read 2026-10-05), whose own confidence tag was T (third-party " +
        "aggregator) — not read from OpenAI's page";

    private const string OpenAiEmbeddingSource =
        "published vendor list price recorded in this repository (docs/design/cloud-provider-adr.md " +
        "section 3 'Hosted embeddings', read 2026-10-05), whose own confidence tag was T (third-party " +
        "aggregator) — not read from OpenAI's page";

    private const string OpenAiChatUnpriced =
        "no published price read: OpenAI's pricing page was not retrievable from this environment on " +
        "2026-10-09, so the model keeps its provider kind's wildcard estimate rather than an invented figure";

    private const string GeminiUnpriced =
        "no published price read: Google's Gemini pricing page did not yield a machine-readable table from " +
        "this environment on 2026-10-09, so the model keeps its provider kind's wildcard estimate rather " +
        "than an invented figure";

    private const string FishAudioTtsSource =
        FishAudioSource + "; TTS is billed per UTF-8 byte, so $15.00 per M UTF-8 bytes is " +
        "$15.00 / 1,000,000 per byte";

    private const string FishAudioAsrSource =
        FishAudioSource + "; ASR is billed per audio second, so $0.36 per audio hour is " +
        "$0.36 / 3,600 per second (which is the same $0.006 per audio minute the seed's wildcard ASR row " +
        "already used)";

    private const string MiniMaxSource =
        "published vendor list price recorded in this repository (MiniMaxVideoGenerationClient.Describe), " +
        "read 2026-10-05";
}
