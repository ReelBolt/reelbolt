using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Resolves the inference provider a given agent run should use, with precedence:
/// per-agent override (if that provider is enabled) &gt; the BUILT-IN definition's override for
/// the running <see cref="AgentType"/> &gt; global default provider
/// (<c>IsDefault &amp;&amp; IsEnabled &amp;&amp; Capability == Chat</c>) &gt; the legacy
/// <c>AzureOpenAI:*</c> configuration keys. The configuration fallback keeps every existing
/// `.env`-only deployment working with zero rows in <c>inference_providers</c>.
/// </summary>
public interface IInferenceProviderResolver
{
    /// <param name="agentType">
    /// The built-in agent type actually running. Load-bearing, not decoration: several callers
    /// know only the type, not the <c>agent_definitions</c> row id — a room step's seats and
    /// director are the motivating case, since default seats carry no per-seat
    /// <c>AgentDefinitionId</c> at all. Before this parameter was honoured, every room turn
    /// silently resolved to the global default provider, so a per-agent override set on e.g.
    /// <c>VideoStoryEditor</c> applied to a solo editor step but NOT to the edit room's seats
    /// running that same agent — a difference nothing surfaced.
    /// </param>
    /// <param name="agentDefinitionId">
    /// The concrete <c>agent_definitions</c> row driving this run when the caller knows it;
    /// takes precedence over the <paramref name="agentType"/> fallback. Always supply it for
    /// <see cref="AgentType.Custom"/>, which has no built-in row to fall back to.
    /// </param>
    Task<ResolvedInferenceProvider> ResolveAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct);

    /// <summary>
    /// Resolves the transcription provider a video-analyze step should use, with precedence:
    /// <paramref name="explicitProviderId"/> (if given and that provider is enabled) &gt; the
    /// single enabled row with <c>Capability == Transcription &amp;&amp; IsDefault</c> &gt;
    /// <c>null</c>. Deliberately no fallback to the legacy <c>AzureOpenAI:*</c> configuration
    /// keys — those name a chat deployment, and silently sending audio there would 404
    /// confusingly. Returns <c>null</c> when nothing resolves, so callers (<c>Optional</c> mode)
    /// can degrade cleanly instead of throwing.
    /// </summary>
    Task<ResolvedTranscriptionProvider?> ResolveTranscriptionAsync(Guid? explicitProviderId, CancellationToken ct);

    /// <summary>
    /// Resolves the speech synthesis provider a voiceover step should use, with HEALTH-AWARE
    /// precedence (<see cref="ProviderHealthSelector"/>): <paramref name="explicitProviderId"/> (if
    /// given and that provider is enabled — always honoured) &gt; the single enabled row with
    /// <c>Capability == SpeechSynthesis &amp;&amp; IsDefault</c> when its last connection test did not
    /// fail &gt; the enabled SpeechSynthesis row whose last test passed, most recently tested first
    /// &gt; the default anyway &gt; <c>null</c>. The result's <c>SelectionReason</c>/<c>LastTestOk</c>
    /// say which, so the voiceover step can warn the customer. Deliberately no fallback to the legacy <c>AzureOpenAI:*</c> configuration
    /// keys — those name a chat deployment, and silently sending a synthesis request there would
    /// fail confusingly. Returns <c>null</c> when nothing resolves, so callers can degrade
    /// cleanly instead of throwing.
    /// </summary>
    Task<ResolvedSpeechSynthesisProvider?> ResolveSpeechSynthesisAsync(Guid? explicitProviderId, CancellationToken ct);

    /// <summary>
    /// Resolves the vision (image-captioning) provider a video-analyze step's shot-captioning
    /// stage should use, with precedence: <paramref name="explicitProviderId"/> (if given and
    /// that provider is enabled) &gt; the single enabled row with
    /// <c>Capability == Vision &amp;&amp; IsDefault</c> &gt; <c>null</c>. Returns
    /// <see cref="ResolvedInferenceProvider"/> (not a dedicated vision type) because a vision call
    /// reuses the same chat-completions machinery <see cref="ResolveAsync"/> does — an image is
    /// just another content part on an otherwise ordinary chat message — so
    /// <see cref="ResolvedInferenceProvider"/>/<c>IChatClientFactory</c> need no vision-specific
    /// variant. Deliberately no fallback to the legacy <c>AzureOpenAI:*</c> configuration keys,
    /// same rationale as <see cref="ResolveTranscriptionAsync"/>: silently sending an image to a
    /// deployment that may not support vision would fail confusingly. Returns <c>null</c> when
    /// nothing resolves, so callers (<c>VideoVisionMode.Optional</c>) can degrade cleanly instead
    /// of throwing.
    /// </summary>
    Task<ResolvedInferenceProvider?> ResolveVisionAsync(Guid? explicitProviderId, CancellationToken ct);

    /// <summary>
    /// Resolves one EXPLICIT chat provider by id, for callers that have already decided which
    /// provider to use and need only its configuration: <c>Capability == Chat</c> (and enabled,
    /// which the store already guarantees) or <c>null</c>.
    ///
    /// <para>
    /// Deliberately NOT a variant of <see cref="ResolveAsync"/>, and deliberately without the
    /// fall-through the other resolvers have. <see cref="ResolveAsync"/> answers "which provider
    /// should this agent run on", applying the whole override → default → legacy-config precedence;
    /// its callers want that answer. This answers "give me exactly this provider", and its one caller
    /// (cost routing) has already chosen a tier — so a pin that no longer resolves must come back as
    /// <c>null</c> and let the caller fall back to the ordinary path explicitly, rather than being
    /// silently swapped for some other provider the routing decision never evaluated. Returning a
    /// default here would make a misconfigured cheap/expensive tier indistinguishable from a working
    /// one.
    /// </para>
    ///
    /// <para>
    /// The <c>Capability</c> filter is the same defense in depth the by-id branch of
    /// <see cref="ResolveVisionAsync"/>/<see cref="ResolveDecisionAsync"/> carries: a Transcription,
    /// Vision, Decision or VideoGeneration row id arriving here (bypassing every UI) must never be
    /// handed to a chat call.
    /// </para>
    /// </summary>
    Task<ResolvedInferenceProvider?> ResolveChatAsync(Guid explicitProviderId, CancellationToken ct);

    /// <summary>
    /// Resolves the decision provider a workflow step should use for structured decision-making,
    /// with precedence: <paramref name="explicitProviderId"/> (if given and that provider is
    /// enabled) &gt; the single enabled row with <c>Capability == Decision &amp;&amp; IsDefault</c>
    /// &gt; <c>null</c>. Deliberately no fallback to the legacy <c>AzureOpenAI:*</c> configuration
    /// keys, same rationale as <see cref="ResolveTranscriptionAsync"/>: silently sending a
    /// decision request to a deployment that may not support structured decision logic would fail
    /// confusingly. Returns <c>null</c> when nothing resolves, so callers can degrade cleanly
    /// instead of throwing.
    /// </summary>
    Task<ResolvedDecisionProvider?> ResolveDecisionAsync(Guid? explicitProviderId, CancellationToken ct);

    /// <summary>
    /// Resolves the video generation provider a step should use, with precedence:
    /// <paramref name="explicitProviderId"/> (if given and that provider is enabled) &gt; the
    /// single enabled row with <c>Capability == VideoGeneration &amp;&amp; IsDefault</c> &gt;
    /// <c>null</c>. Deliberately no fallback to the legacy <c>AzureOpenAI:*</c> configuration
    /// keys — those name a chat deployment, and silently sending a video generation request
    /// there would fail confusingly. Returns <c>null</c> when nothing resolves, so callers can
    /// degrade cleanly instead of throwing.
    /// </summary>
    Task<ResolvedVideoGenerationProvider?> ResolveVideoGenerationAsync(Guid? explicitProviderId, CancellationToken ct);

    /// <summary>
    /// Resolves the provider a text-to-image call should use, with precedence:
    /// <paramref name="explicitProviderId"/> (if given and that provider is enabled and carries
    /// <c>Capability == ImageGeneration</c>) &gt; the single enabled row with
    /// <c>Capability == ImageGeneration &amp;&amp; IsDefault</c> &gt; <c>null</c>.
    /// Deliberately no fallback to the legacy <c>AzureOpenAI:*</c> configuration keys — those name a
    /// chat deployment, and silently sending an image prompt there would fail confusingly. Returns
    /// <c>null</c> when nothing resolves, so a caller can degrade to a deterministic fallback (a
    /// frame from the user's own video) instead of failing a step.
    /// </summary>
    Task<ResolvedImageProvider?> ResolveImageGenerationAsync(Guid? explicitProviderId, CancellationToken ct);

    /// <summary>
    /// Resolves the provider an image EDIT call should use, with precedence:
    /// <paramref name="explicitProviderId"/> (if given and that provider is enabled and carries
    /// <c>Capability == ImageEdit</c>) &gt; the single enabled row with
    /// <c>Capability == ImageEdit &amp;&amp; IsDefault</c> &gt; <c>null</c>.
    /// <para>
    /// Resolved independently of <see cref="ResolveImageGenerationAsync"/> on purpose: they are two
    /// rows with two independent "at most one default" constraints, so an operator can generate on
    /// one vendor and edit on another. The returned type is the same because the CLIENT is the same
    /// — <c>ResolvedImageProvider.Capability</c> is what says which of the two this resolution was
    /// for, and it is what a caller checks before calling <c>EditAsync</c>.
    /// </para>
    /// </summary>
    Task<ResolvedImageProvider?> ResolveImageEditAsync(Guid? explicitProviderId, CancellationToken ct);

    /// <summary>
    /// Resolves the provider one picture-editor JOB should use, for any capability in
    /// <see cref="ImageJobCapabilities.All"/> (the six per-job capabilities plus
    /// <c>ImageEdit</c> and <c>ImageGeneration</c>), with the same precedence as every image capability:
    /// <paramref name="explicitProviderId"/> (if given, visible, enabled and carrying exactly
    /// <paramref name="capability"/>) &gt; the single enabled default row of <paramref name="capability"/>
    /// &gt; <c>null</c>. No fallback to another capability happens here: the editor's one compatibility
    /// fallback (a job with no row of its own may use the <c>ImageEdit</c> default when that row's kind
    /// serves the job) is a decision of the caller, made visibly, not of the resolver.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capability"/> is not an image job.</exception>
    Task<ResolvedImageProvider?> ResolveImageJobAsync(
        InferenceProviderCapability capability, Guid? explicitProviderId, CancellationToken ct);

    /// <summary>
    /// Resolves the provider a subject-segmentation call should use, with precedence:
    /// <paramref name="explicitProviderId"/> (if given and that provider is enabled and carries
    /// <c>Capability == Segmentation</c>) &gt; the single enabled row with
    /// <c>Capability == Segmentation &amp;&amp; IsDefault</c> &gt; <c>null</c>.
    /// <para>
    /// No legacy-config fallback, for the reason every other non-chat capability has none: there is no
    /// configuration key that ever named a segmenter, so a fallback could only silently point at
    /// something else. <c>null</c> means the caller must degrade — an editor with no segmenter shows
    /// the source image and says so, rather than failing.
    /// </para>
    /// </summary>
    Task<ResolvedImageAnalysisProvider?> ResolveSegmentationAsync(Guid? explicitProviderId, CancellationToken ct);

    /// <summary>
    /// Resolves the provider a depth-estimation call should use, with precedence:
    /// <paramref name="explicitProviderId"/> (if given and that provider is enabled and carries
    /// <c>Capability == Depth</c>) &gt; the single enabled row with
    /// <c>Capability == Depth &amp;&amp; IsDefault</c> &gt; <c>null</c>.
    /// </summary>
    Task<ResolvedImageAnalysisProvider?> ResolveDepthAsync(Guid? explicitProviderId, CancellationToken ct);

    /// <summary>
    /// Resolves the provider a text-prompted VIDEO segmentation call should use, with precedence:
    /// <paramref name="explicitProviderId"/> (if given and that provider is enabled and carries
    /// <c>Capability == VideoSegmentation</c>) &gt; the single enabled row with
    /// <c>Capability == VideoSegmentation &amp;&amp; IsDefault</c> &gt; <c>null</c>.
    /// <para>
    /// <b>Declared here, implemented by the capability ticket (T-003).</b> The default implementation
    /// answers <c>null</c> (unresolved) so the step-cache fingerprint can name the capability from the
    /// contracts ticket onwards without a wave of no-op implementations: an unresolved capability is
    /// fingerprinted as unresolved, which is truthful while nothing can serve it yet, and it fails the
    /// analyze step closed with <c>SEGMENTATION_NO_PROVIDER</c> rather than falling back to the feature
    /// tracker. T-003 replaces this default with the real resolution — same shape as
    /// <see cref="ResolveSegmentationAsync"/> (which the same provider snapshot answers).
    /// </para>
    /// <para>
    /// Returns <see cref="ResolvedImageAnalysisProvider"/> rather than a dedicated type for the reason
    /// that record's own remarks give: the resolved shape is identical, and a second record would be a
    /// byte-identical copy that silently diverges the first time one gained a field.
    /// </para>
    /// </summary>
    Task<ResolvedImageAnalysisProvider?> ResolveVideoSegmentationAsync(Guid? explicitProviderId, CancellationToken ct)
        => Task.FromResult<ResolvedImageAnalysisProvider?>(null);

    /// <summary>
    /// Resolves the provider a content-moderation call should use, with precedence:
    /// <paramref name="explicitProviderId"/> (if given, enabled and carrying
    /// <c>Capability == ContentModeration</c>) &gt; the single enabled row with
    /// <c>Capability == ContentModeration &amp;&amp; IsDefault</c> &gt; <c>null</c>. No legacy
    /// configuration fallback: no configuration key ever named a moderation service, and sending a
    /// brief to a chat deployment would fail confusingly.
    /// <para>
    /// <b>Default implementation answers <c>null</c></b> so existing test doubles compile without a
    /// wave of no-op implementations. An unresolved capability means "moderation is off" — never a
    /// fallback — and the gate treats it as unavailable (fail open on prompts/ingest/generated
    /// text, fail closed at publish).
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Platform rows only.</b> A moderation answer is stored for good and gates what a person may publish,
    /// so an organization's own provider row (default or named) never answers it, whatever its plan allows.
    /// </remarks>
    Task<ResolvedContentModerationProvider?> ResolveContentModerationAsync(
        Guid? explicitProviderId, CancellationToken ct)
        => Task.FromResult<ResolvedContentModerationProvider?>(null);

    /// <summary>
    /// Resolves the transcription provider the SAFETY check of a file's speech uses: the platform's own default
    /// transcription row, never an organization's, for the same reason as <see cref="ResolveContentModerationAsync"/>.
    /// Run-step transcription keeps <see cref="ResolveTranscriptionAsync"/>. Null when the platform has none.
    /// <para>Default implementation answers <c>null</c> (no platform row).</para>
    /// </summary>
    Task<ResolvedTranscriptionProvider?> ResolvePlatformTranscriptionAsync(CancellationToken ct)
        => Task.FromResult<ResolvedTranscriptionProvider?>(null);

    /// <summary>
    /// Resolves the provider a rerank call should use, with precedence:
    /// <paramref name="explicitProviderId"/> (if given, enabled and carrying <c>Capability == Rerank</c>)
    /// &gt; the single enabled row with <c>Capability == Rerank &amp;&amp; IsDefault</c> &gt; <c>null</c>.
    /// No legacy-config fallback: no configuration key ever named a rerank service. With billing on, an unpriced
    /// platform row is never offered (<see cref="ModelUnpricedException"/> when it was the only candidate), which
    /// the caller reads as "rerank unavailable".
    /// <para>
    /// <b>Default implementation answers <c>null</c></b> so existing test doubles compile; an unresolved
    /// capability means "no rerank", never a fallback to a chat deployment.
    /// </para>
    /// </summary>
    Task<ResolvedRerankProvider?> ResolveRerankAsync(Guid? explicitProviderId, CancellationToken ct)
        => Task.FromResult<ResolvedRerankProvider?>(null);

    /// <summary>
    /// Resolves the provider a web search call should use: <paramref name="explicitProviderId"/> (if given, enabled
    /// and carrying <c>Capability == WebSearch</c>) &gt; the single enabled default row of that capability &gt;
    /// <c>null</c>. No legacy-config fallback. Default implementation answers <c>null</c> so existing test doubles
    /// compile; an unresolved capability means "no web search".
    /// </summary>
    Task<ResolvedWebSearchProvider?> ResolveWebSearchAsync(Guid? explicitProviderId, CancellationToken ct)
        => Task.FromResult<ResolvedWebSearchProvider?>(null);

    /// Resolves the provider a music generation should use: <paramref name="explicitProviderId"/> (if enabled, visible
    /// and carrying <c>Capability == MusicGeneration</c>) &gt; the default <c>MusicGeneration</c> row &gt; <c>null</c>.
    /// No legacy-config fallback: no configuration key ever named a music service. With billing on, an unpriced
    /// platform row is never offered (<see cref="ModelUnpricedException"/> when it was the only candidate).
    /// </summary>
    Task<ResolvedAudioGenerationProvider?> ResolveMusicGenerationAsync(Guid? explicitProviderId, CancellationToken ct);

    /// <summary>
    /// The same precedence for <c>SoundEffectGeneration</c>: explicit &gt; default <c>SoundEffectGeneration</c> row
    /// &gt; <c>null</c>; no legacy fallback.
    /// </summary>
    Task<ResolvedAudioGenerationProvider?> ResolveSoundEffectGenerationAsync(Guid? explicitProviderId, CancellationToken ct);

    /// <summary>
    /// The same precedence for <c>AudioIsolation</c> (hosted voice isolation): explicit (same capability only)
    /// &gt; the default <c>AudioIsolation</c> row &gt; <c>null</c>; no legacy fallback. An unpriced platform row is
    /// never offered (<see cref="ModelUnpricedException"/> when it was the only candidate).
    /// </summary>
    Task<ResolvedAudioGenerationProvider?> ResolveAudioIsolationAsync(Guid? explicitProviderId, CancellationToken ct);

    /// <summary>
    /// The same precedence for <c>VideoUpscale</c> (hosted video upscaling, served by FalAi): explicit (same
    /// capability only) &gt; the default <c>VideoUpscale</c> row &gt; <c>null</c>; no legacy fallback. An unpriced platform
    /// row is never offered (<see cref="ModelUnpricedException"/> when it was the only candidate). The resolved shape
    /// is the audio one (endpoint, model, key, timeout): the type name is historical, not a statement about audio.
    /// </summary>
    Task<ResolvedAudioGenerationProvider?> ResolveVideoUpscaleAsync(Guid? explicitProviderId, CancellationToken ct);

    /// <summary>The same precedence for <c>FrameInterpolation</c> (hosted frame generation, served by FalAi).</summary>
    Task<ResolvedAudioGenerationProvider?> ResolveFrameInterpolationAsync(Guid? explicitProviderId, CancellationToken ct);

    /// <summary>
    /// The same precedence for <c>VideoOutpaint</c> (hosted outpainting, served by FalAi): explicit (same capability
    /// only) &gt; the default <c>VideoOutpaint</c> row &gt; <c>null</c>; no legacy fallback, and an unpriced platform row is
    /// never offered.
    /// </summary>
    Task<ResolvedAudioGenerationProvider?> ResolveVideoOutpaintAsync(Guid? explicitProviderId, CancellationToken ct);

    /// <summary>
    /// Resolves the talking-head lip-sync provider: explicit (only when that row's capability is <c>LipSync</c>)
    /// &gt; the default <c>LipSync</c> row &gt; <c>null</c>. Never falls back to <c>VideoGeneration</c>. An unpriced
    /// platform row is never offered (<see cref="ModelUnpricedException"/> when it was the only candidate). The
    /// default body returns <c>null</c> so test fakes need not implement it; the real resolver does.
    /// </summary>
    Task<ResolvedVideoGenerationProvider?> ResolveLipSyncAsync(Guid? explicitProviderId, CancellationToken ct) =>
        Task.FromResult<ResolvedVideoGenerationProvider?>(null);

    /// <summary>
    /// Resolves the embedding provider semantic file search should use, with precedence:
    /// <paramref name="explicitProviderId"/> (if given, enabled and <c>Capability == Embedding</c>)
    /// &gt; the single enabled row with <c>Capability == Embedding &amp;&amp; IsDefault</c> &gt;
    /// the legacy <c>AzureOpenAI:Endpoint</c>/<c>AzureOpenAI:ApiKey</c> configuration keys (model from
    /// <c>VectorSearch:EmbeddingDeployment</c>, default <c>text-embedding-3-large</c>) &gt; <c>null</c>.
    /// Unlike transcription, a configuration fallback exists here because embeddings were
    /// configured that way before the capability existed, so existing deployments keep working.
    /// Returns <c>null</c> when nothing resolves.
    /// </summary>
    Task<ResolvedEmbeddingProvider?> ResolveEmbeddingAsync(Guid? explicitProviderId, CancellationToken ct);

    /// <summary>
    /// Drops the cached provider snapshot of one organization (<c>null</c> = the platform-only
    /// view). Writers call it after changing that organization's providers or overrides so the
    /// change is visible at once instead of after the TTL. A platform-row change affects every
    /// organization's snapshot, so use <see cref="InvalidateAll"/> for it.
    /// </summary>
    void Invalidate(Guid? organizationId);

    /// <summary>Drops every cached snapshot (platform provider rows changed).</summary>
    void InvalidateAll();

    /// <summary>
    /// The reasoning effort stored on the agent definition driving a run, or <c>null</c> when none is
    /// stored (the caller then uses configuration and the agent's own default - see
    /// <see cref="AgentReasoningEffort.Resolve"/>). Looked up by <paramref name="agentDefinitionId"/>
    /// first and by the built-in <paramref name="agentType"/> second, exactly like the provider
    /// override in <see cref="ResolveAsync"/>, because a room step's seats name only the type. Served
    /// from the same TTL-cached snapshot, so it is a dictionary read per run.
    /// </summary>
    Task<string?> ResolveAgentReasoningEffortAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// True when this resolver treats an enabled PLATFORM capability with no effective price as absent
    /// (<c>Billing:Enabled</c>; docs/metering-and-billing.md, "Unpriced models fail closed"). The admin surfaces ask it so
    /// they refuse to enable exactly what the resolver would refuse to run, and the pickers omit exactly that. The
    /// default (a test double, a self-hosted deployment) is false: nothing is filtered.
    /// </summary>
    bool UnpricedModelsFailClosed => false;
}
