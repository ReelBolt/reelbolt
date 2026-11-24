using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// Which billable units each <see cref="InferenceProviderCapability"/> produces, and which
/// capabilities each <see cref="InferenceProviderKind"/> may serve.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> A price row prices one unit of one model capability, but what an operator
/// configures is a <c>(provider_kind, capability)</c> pair. This table says which units a capability
/// produces, so a capability can be checked for a price on every unit it meters — and because pricing
/// fails closed (<see cref="ModelPriceService.Price"/> throws <see cref="UnpricedUsageException"/> on a
/// Platform event, which <see cref="UsageRecorder"/> turns into a row with <c>credits_millis = 0</c> and
/// no <c>price_id</c>), a missing one records as free. <see cref="PriceCoverage"/> keeps that visible.
/// </para>
/// <para>
/// <b>The real instance of it.</b> Fish Audio serves <c>Transcription</c> (<c>POST /v1/asr</c>) and
/// <c>SpeechSynthesis</c> (<c>POST /v1/tts</c>), but the old rate-card seed seeded
/// <see cref="UsageKind.AsrAudioSeconds"/> for <c>AzureOpenAI</c> and <c>OpenAICompatible</c> only.
/// On the staging deployment the default transcription provider is a <c>FishAudio</c> row
/// (<c>transcribe-1-pro</c>), so every transcription it produced matched no card. One such row is in
/// the staging ledger: <c>FishAudio | AsrAudioSeconds | transcribe-1-pro | 0.3s | $0.00 | 0 credits |
/// rate_card_id NULL</c>.
/// </para>
/// <para>
/// <b>Two sources of truth, held together by a test.</b> The authority on which kind may serve which
/// capability is <c>InferenceProvidersController.IsUnsupportedCombination</c> — it is what actually
/// refuses a bad admin request. This table restates it in the shared library so the seed, the engine
/// and the coverage check can see it, and
/// <c>ProviderCapabilityCoverageTests</c> asserts the two agree for every (kind, capability) pair in
/// both enums. Adding a capability to one side without the other therefore fails the build rather
/// than the ledger.
/// </para>
/// </remarks>
public static class ProviderCapabilityUsage
{
    /// <summary>
    /// Every capability, in enum order. (<see cref="InferenceProviderCapability"/> is append-only, so
    /// this is stable across releases.)
    /// </summary>
    public static readonly IReadOnlyList<InferenceProviderCapability> Capabilities =
        Enum.GetValues<InferenceProviderCapability>();

    /// <summary>
    /// Every provider kind, in enum order (<see cref="InferenceProviderKind"/> is append-only too).
    /// </summary>
    public static readonly IReadOnlyList<InferenceProviderKind> Kinds = Enum.GetValues<InferenceProviderKind>();

    /// <summary>
    /// The billable units a capability produces. A capability with an empty array meters nothing, so a
    /// rate card for it would be meaningless; every capability the platform ships has at least one.
    /// </summary>
    /// <remarks>
    /// <c>Chat</c>, <c>Vision</c> and <c>Decision</c> share the three token kinds: a vision call is a
    /// chat call with an image part, and the decision clients are chat clients under the hood
    /// (<c>LogprobDecisionClient</c> reads <c>logprobs</c> off a completions response), so all three
    /// resolve through <c>MeteringChatClient</c>.
    /// </remarks>
    public static readonly IReadOnlyDictionary<InferenceProviderCapability, UsageKind[]> ByCapability =
        new Dictionary<InferenceProviderCapability, UsageKind[]>
        {
            [InferenceProviderCapability.Chat] =
                [UsageKind.LlmInputTokens, UsageKind.LlmOutputTokens, UsageKind.LlmCachedInputTokens],
            [InferenceProviderCapability.Vision] =
                [UsageKind.LlmInputTokens, UsageKind.LlmOutputTokens, UsageKind.LlmCachedInputTokens],
            [InferenceProviderCapability.Decision] =
                [UsageKind.LlmInputTokens, UsageKind.LlmOutputTokens, UsageKind.LlmCachedInputTokens],
            [InferenceProviderCapability.Embedding] = [UsageKind.EmbeddingTokens],
            [InferenceProviderCapability.Transcription] = [UsageKind.AsrAudioSeconds],
            [InferenceProviderCapability.SpeechSynthesis] = [UsageKind.TtsInputBytes],
            [InferenceProviderCapability.VideoGeneration] = [UsageKind.VideoGenSeconds],

            // The two image capabilities meter the IMAGE RETURNED, one unit each. That is the unit
            // every vendor we integrate publishes a price in (MiniMax lists image-01 at $0.0035 per
            // image; BFL prices FLUX 3 Image per image and FLUX.2 per output megapixel), and it is
            // the only unit a caller can count honestly — a call returning n images is n units.
            //
            // Generation and editing are separate kinds on purpose: BFL lists FLUX.2 [pro] at "from
            // $0.03" text-to-image against "from $0.045" image editing, so one shared unit would
            // under-charge every edit by a third. OpenAI is the known gap here — it bills its image
            // models per token, not per image, and a per-image unit cannot express that, so its
            // models are listed in ModelPriceCatalog as unpriced with that reason rather than given
            // an invented per-image figure.
            [InferenceProviderCapability.ImageGeneration] = [UsageKind.ImageGenImages],
            [InferenceProviderCapability.ImageEdit] = [UsageKind.ImageEditImages],

            // The analysis arm meters the image RETURNED, which is also what fal bills: its queue
            // charges per successful output and explicitly not for queue time or a server error, so
            // one output is one unit and a call that threw costs nothing.
            [InferenceProviderCapability.Segmentation] = [UsageKind.SegmentationImages],
            [InferenceProviderCapability.Depth] = [UsageKind.DepthImages],

            // Video segmentation meters FRAMES OF INPUT, not outputs: fal's sam-3-1/video-rle bills
            // "$0.01 per 16 frames of video input", so the unit has to be the frame (rounded up to the
            // vendor's whole block by the meter) and can never be the per-request image unit above.
            [InferenceProviderCapability.VideoSegmentation] = [UsageKind.VideoSegmentationFrames],

            // The editor's per-job capabilities (split out of ImageEdit) are all edits of a picture the
            // customer already has, and every one of them goes through the editing arm of
            // MeteringImageClient (InpaintAsync / TransformAsync), which records ImageEditImages under the
            // endpoint that RAN. Splitting the capability changes which ROW serves a job, not what the job
            // is billed as, so the per-endpoint prices in ModelPriceCatalog.FalImages stay the ones charged.
            [InferenceProviderCapability.ImageErase] = [UsageKind.ImageEditImages],
            [InferenceProviderCapability.ImageFill] = [UsageKind.ImageEditImages],
            [InferenceProviderCapability.ImageExpand] = [UsageKind.ImageEditImages],
            [InferenceProviderCapability.ImageBackgroundReplace] = [UsageKind.ImageEditImages],
            [InferenceProviderCapability.ImageRelight] = [UsageKind.ImageEditImages],
            [InferenceProviderCapability.ImageUpscale] = [UsageKind.ImageEditImages],

            // The system compute provider's one capability (docs/design/model-catalog.md §5.4). No provider
            // kind SERVES it (it is absent from ServableByKind), so it adds nothing to RequiredCards; the
            // units are priced one variant per resolution tier.
            [InferenceProviderCapability.Compute] =
                [UsageKind.RenderOutputSeconds, UsageKind.EncodeOutputSeconds, UsageKind.AnalyzeMediaSeconds],

            // Audio generation meters each vendor's own unit: fal bills ElevenLabs Music per output MINUTE
            // rounded up, and every sound-effect endpoint per SECOND (ModelPriceCatalog.Audio).
            [InferenceProviderCapability.MusicGeneration] = [UsageKind.MusicGenMinutes],
            [InferenceProviderCapability.SoundEffectGeneration] = [UsageKind.SoundEffectGenSeconds],

            // Hosted voice isolation meters audio SECONDS of the source, rounded UP to whole MINUTES, into
            // AudioCleanupMinutes: fal prices fal-ai/elevenlabs/audio-isolation per minute (ModelPriceCatalog.AudioIsolation).
            [InferenceProviderCapability.AudioIsolation] = [UsageKind.AudioCleanupMinutes],

            // Hosted video enhancement meters each vendor's own unit: Topaz/bytedance upscaling per OUTPUT second
            // by resolution tier (VideoEnhanceSeconds), RIFE/FILM interpolation per GPU COMPUTE second
            // (VideoEnhanceComputeSeconds); ModelPriceCatalog.VideoEnhance.
            [InferenceProviderCapability.VideoUpscale] = [UsageKind.VideoEnhanceSeconds],
            [InferenceProviderCapability.FrameInterpolation] = [UsageKind.VideoEnhanceComputeSeconds],

            // Outpainting bills the clip it RETURNS: Wan VACE and Luma reframe both publish a per-video-second price
            // (video seconds counted at 16 fps on Wan), so it reuses VideoEnhanceSeconds rather than a new unit.
            [InferenceProviderCapability.VideoOutpaint] = [UsageKind.VideoEnhanceSeconds],

            // Talking-head lip-sync bills like the video model it runs on: output seconds, VideoGenSeconds. The
            // unit stays shared with VideoGeneration, so DefaultCapabilityFor(VideoGenSeconds) still answers VideoGeneration.
            [InferenceProviderCapability.LipSync] = [UsageKind.VideoGenSeconds],

            // Moderation meters CALLS, not inputs or seconds: OpenAI publishes the moderation models
            // as free, so the unit exists to record that a call happened and that its real price is
            // $0 — an unpriced gap would hide the integration.
            [InferenceProviderCapability.ContentModeration] = [UsageKind.ModerationCalls],

            // Rerank meters SEARCHES: one query plus up to 20 candidate texts is one unit, which is also how
            // hosted rerankers (Cohere, Jina, Voyage) bill. A self-hosted reranker is a real $0 price.
            [InferenceProviderCapability.Rerank] = [UsageKind.RerankSearches],

            // Web search meters SEARCHES (one query/action = one unit; DeepSeek bills per non-empty call). The
            // tokens of an OpenAI/Gemini search call are NOT a required card here: they are recorded under the
            // Chat capability with the same model, so they price from the chat rows (agent-web-search D-2.5).
            [InferenceProviderCapability.WebSearch] = [UsageKind.WebSearches]
        };

    /// <summary>
    /// The capability a unit is metered under when the caller did not say: the first capability (in enum order)
    /// that produces it. Tokens are Chat, images are ImageGeneration/ImageEdit, compute is Compute.
    /// </summary>
    public static InferenceProviderCapability DefaultCapabilityFor(UsageKind unit)
    {
        foreach (InferenceProviderCapability capability in Capabilities)
        {
            if (ByCapability.TryGetValue(capability, out UsageKind[]? units) && units.Contains(unit)) return capability;
        }

        return InferenceProviderCapability.Chat;
    }

    /// <summary>
    /// The capabilities each provider kind may serve — the mirror of
    /// <c>InferenceProvidersController.IsUnsupportedCombination</c>. Read that method's own remarks for
    /// the vendor-side reason behind each rejection; this is the same table in a form the seed can
    /// iterate.
    /// </summary>
    public static readonly IReadOnlyDictionary<InferenceProviderKind, InferenceProviderCapability[]> ServableByKind =
        new Dictionary<InferenceProviderKind, InferenceProviderCapability[]>
        {
            [InferenceProviderKind.AzureOpenAI] =
            [
                InferenceProviderCapability.Chat,
                InferenceProviderCapability.Transcription,
                InferenceProviderCapability.Vision,
                InferenceProviderCapability.Decision,
                InferenceProviderCapability.Embedding
            ],
            [InferenceProviderKind.OpenAICompatible] =
            [
                InferenceProviderCapability.Chat,
                InferenceProviderCapability.Transcription,
                InferenceProviderCapability.Vision,
                InferenceProviderCapability.Decision,
                InferenceProviderCapability.Embedding,
                InferenceProviderCapability.SpeechSynthesis,
                InferenceProviderCapability.ImageGeneration,
                InferenceProviderCapability.ImageEdit,

                // The OpenAI Images edit route takes a mask, so the two mask jobs are served by the same
                // InpaintAsync arm that answered them when every tool resolved ImageEdit.
                InferenceProviderCapability.ImageErase,
                InferenceProviderCapability.ImageFill,

                // OpenAI's POST /moderations is the OpenAI shape; its SDKs and compatible gateways
                // (LiteLLM, Azure OpenAI's moderation route) speak it too. No other kind is wired.
                InferenceProviderCapability.ContentModeration,

                // POST {base}/rerank (Hugging Face text-embeddings-inference and hosted rerankers). No other
                // kind is wired.
                InferenceProviderCapability.Rerank,

                // OpenAI Responses web_search, only when the row's host is api.openai.com (the controller
                // enforces that; agent-web-search D-2.2).
                InferenceProviderCapability.WebSearch
            ],
            [InferenceProviderKind.Anthropic] =
                [InferenceProviderCapability.Chat, InferenceProviderCapability.Vision],
            [InferenceProviderKind.Gemini] =
                [InferenceProviderCapability.Chat, InferenceProviderCapability.Vision,
                    InferenceProviderCapability.WebSearch],
            [InferenceProviderKind.DeepSeek] =
            [
                InferenceProviderCapability.Chat,
                InferenceProviderCapability.Vision,
                InferenceProviderCapability.Decision,
                InferenceProviderCapability.WebSearch
            ],
            [InferenceProviderKind.TypeSafe] = [InferenceProviderCapability.Decision],

            // MiniMax serves THREE capabilities from one host and one credential: the video endpoint
            // and the image endpoint (POST /v1/image_generation, model image-01) documented at
            // platform.minimax.io/docs/guides/image-generation. Before the image arm landed the
            // admin boundary compared against VideoGeneration alone, which made a MiniMax image row
            // impossible to create at all.
            [InferenceProviderKind.MiniMax] =
            [
                InferenceProviderCapability.VideoGeneration,
                InferenceProviderCapability.ImageGeneration,
                InferenceProviderCapability.ImageEdit
            ],

            // Black Forest Labs: the FLUX endpoints. Async submit + poll, x-key auth, model in the
            // URL path. Generation and instruction editing are the same route with different body
            // fields; mask inpainting is the flux-pro-1.0-fill model. See
            // BlackForestLabsImageClient for the per-field source.
            [InferenceProviderKind.BlackForestLabs] =
            [
                InferenceProviderCapability.ImageGeneration,
                InferenceProviderCapability.ImageEdit,
                InferenceProviderCapability.ImageErase,
                InferenceProviderCapability.ImageFill
            ],

            // fal.ai: one queue contract over many hosted endpoints, so ONE kind rather than one per
            // model, serving every image capability. Segmentation covers fal-ai/sam-3-1/image (point, box and
            // text prompts, several masks), fal-ai/evf-sam, fal-ai/sam2/image and fal-ai/birefnet/v2;
            // Depth covers fal-ai/imageutils/depth and marigold-depth (FalAiAnalysisClient). ImageGeneration
            // and ImageEdit are FalAiImageClient: text-to-image, instruction edit, mask erase/fill and the
            // expand / replace-background / relight / upscale operations, each endpoint chosen per call from
            // FalAiImageClient.Catalog; the six per-job capabilities (erase, fill, expand, background
            // replace, relight, upscale) are the same client, each job's endpoint read from the catalogue.
            // Every schema was read from fal's own OpenAPI document. This kind
            // serves nothing else, and InferenceProvidersController enforces that with the same negative
            // rule MiniMax, OpenJev and FishAudio use - necessary, because Chat/Vision/Transcription/
            // Decision have no per-kind rule and that method falls through to ALLOW.
            //
            // VideoGeneration was appended last when the image-to-video wave landed: fal hosts video
            // endpoints behind the same queue contract (FalAiVideoCatalog, one endpoint per direction plus
            // documented alternatives), so the capability is the same client and the same credential, not
            // a second kind. Appended rather than inserted so every existing pair keeps its position.
            [InferenceProviderKind.FalAi] =
            [
                InferenceProviderCapability.Segmentation,
                InferenceProviderCapability.Depth,
                InferenceProviderCapability.ImageGeneration,
                InferenceProviderCapability.ImageEdit,
                InferenceProviderCapability.ImageErase,
                InferenceProviderCapability.ImageFill,
                InferenceProviderCapability.ImageExpand,
                InferenceProviderCapability.ImageBackgroundReplace,
                InferenceProviderCapability.ImageRelight,
                InferenceProviderCapability.ImageUpscale,
                InferenceProviderCapability.VideoGeneration,
                InferenceProviderCapability.VideoSegmentation,

                // Music and sound effects: fal hosts ElevenLabs Music, ElevenLabs SFX v2 and
                // MMAudio behind the same queue contract (AudioModelCatalog), so the same kind and credential.
                InferenceProviderCapability.MusicGeneration,
                InferenceProviderCapability.SoundEffectGeneration,

                // Hosted voice isolation: fal hosts the ElevenLabs-based isolator; the FalAi kind serves
                // it, the dormant ElevenLabs kind does not.
                InferenceProviderCapability.AudioIsolation,

                // Hosted video enhancement: fal hosts the Topaz/bytedance upscalers and RIFE/FILM.
                InferenceProviderCapability.VideoUpscale,
                InferenceProviderCapability.FrameInterpolation,

                // Outpainting: fal hosts Wan VACE and Luma Ray 2 reframe behind the same queue contract.
                InferenceProviderCapability.VideoOutpaint,

                // Talking-head lip-sync: fal hosts the talking-head video models (FalAiVideoCatalog, Job == TalkingHead).
                InferenceProviderCapability.LipSync
            ],
            [InferenceProviderKind.OpenJev] = [InferenceProviderCapability.Decision],
            [InferenceProviderKind.FishAudio] =
                [InferenceProviderCapability.SpeechSynthesis, InferenceProviderCapability.Transcription],

            // Issue #93 phases 3 and 5: two more video-generation-only kinds. Higgsfield is a paid
            // aggregator priced per configuration by its own estimate endpoint (the VideoGenSeconds
            // card is the wildcard fallback the metering recorder prices from); ComfyUI is
            // self-hosted and bills nothing — its VideoGenSeconds card prices the GPU time the
            // platform still accounts for, at the same wildcard rate until B13 decides otherwise.
            [InferenceProviderKind.Higgsfield] = [InferenceProviderCapability.VideoGeneration],
            [InferenceProviderKind.ComfyUI] = [InferenceProviderCapability.VideoGeneration],

            // The direct ElevenLabs route is DORMANT (Product decision: music and sound effects through fal only).
            // InferenceProvidersController.IsUnsupportedCombination refuses the kind every capability, so it serves
            // nothing and must price nothing. Re-enabling it is MusicGeneration + SoundEffectGeneration here and there.
            [InferenceProviderKind.ElevenLabs] = [],

            // A model on the user's own machine, relayed through their desktop runner. Chat, Vision and Embedding only, always an
            // organization's own row (bring-your-own: metered at zero credits, never priced as a platform unit).
            [InferenceProviderKind.LocalRunner] =
                [InferenceProviderCapability.Chat, InferenceProviderCapability.Vision, InferenceProviderCapability.Embedding]
        };

    /// <summary>
    /// The distinct <c>(provider kind, usage kind)</c> pairs that must be priced for the platform to
    /// charge for everything it can be billed for, in a stable order. This is the coverage contract:
    /// one wildcard card per pair is the minimum, and per-model rows refine it.
    /// </summary>
    public static IReadOnlyList<(InferenceProviderKind Kind, UsageKind UsageKind)> RequiredCards()
    {
        List<(InferenceProviderKind, UsageKind)> pairs = [];
        HashSet<(InferenceProviderKind, UsageKind)> seen = [];
        foreach (InferenceProviderKind kind in Kinds)
        {
            if (!ServableByKind.TryGetValue(kind, out InferenceProviderCapability[]? capabilities)) continue;
            foreach (InferenceProviderCapability capability in capabilities)
            {
                foreach (UsageKind usageKind in ByCapability[capability])
                {
                    if (seen.Add((kind, usageKind))) pairs.Add((kind, usageKind));
                }
            }
        }

        return pairs;
    }

    /// <summary>
    /// The same contract, naming the capability each pair came from, for a report a human reads.
    /// </summary>
    public static IReadOnlyList<(InferenceProviderKind Kind, InferenceProviderCapability Capability, UsageKind UsageKind)>
        RequiredCardsWithCapability()
    {
        List<(InferenceProviderKind, InferenceProviderCapability, UsageKind)> rows = [];
        foreach (InferenceProviderKind kind in Kinds)
        {
            if (!ServableByKind.TryGetValue(kind, out InferenceProviderCapability[]? capabilities)) continue;
            foreach (InferenceProviderCapability capability in capabilities)
            {
                foreach (UsageKind usageKind in ByCapability[capability])
                {
                    rows.Add((kind, capability, usageKind));
                }
            }
        }

        return rows;
    }
}
