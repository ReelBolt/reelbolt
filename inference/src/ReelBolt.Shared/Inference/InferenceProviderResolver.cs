using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// TTL-cached resolver over the two small provider tables. Registered as a singleton but the
/// underlying <see cref="IInferenceProviderStore"/> is scoped (it depends on a per-request
/// DbContext), so a <see cref="IServiceScopeFactory"/> is used to create a short-lived scope on
/// every cache refresh — the same pattern <c>WorkflowExecutorService</c> already uses for
/// singleton-adjacent DbContext access.
/// </summary>
public sealed class InferenceProviderResolver : IInferenceProviderResolver
{
    private const string DefaultFallbackModelName = "gpt-4o-mini";
    private const string DefaultEmbeddingDeployment = "text-embedding-3-large";
    private const string FallbackProviderName = "AzureOpenAI (config fallback)";
    private const int DefaultTimeoutSeconds = 300;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ISecretProtector _secretProtector;
    private readonly ILogger<InferenceProviderResolver> _logger;
    private readonly TimeSpan _cacheTtl;
    private readonly IInferenceTenantAccessor? _tenantAccessor;

    // One snapshot per organization (Guid.Empty = the platform-only view: no tenant scope, or a
    // platform scope). Each holds the platform rows plus that organization's own rows, so an
    // organization's resolution can never observe another organization's providers.
    private readonly ConcurrentDictionary<Guid, Snapshot> _snapshots = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _refreshLocks = new();

    // Bumped by Invalidate so a load that was already in flight when the writer invalidated does
    // not publish the pre-write rows it read.
    private readonly ConcurrentDictionary<Guid, int> _generations = new();

    public InferenceProviderResolver(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ISecretProtector secretProtector,
        ILogger<InferenceProviderResolver> logger,
        IInferenceTenantAccessor? tenantAccessor = null)
    {
        _tenantAccessor = tenantAccessor;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _secretProtector = secretProtector;
        _logger = logger;

        int cacheSeconds = configuration.GetValue("Inference:ProviderCacheSeconds", 60);
        _cacheTtl = TimeSpan.FromSeconds(Math.Max(1, cacheSeconds));
    }

    public async Task<ResolvedInferenceProvider> ResolveAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct)
    {
        Snapshot snapshot = await GetSnapshotAsync(ct);

        ModelCapabilityView? provider = null;

        if (agentDefinitionId.HasValue &&
            snapshot.AgentOverrides.TryGetValue(agentDefinitionId.Value, out Guid? overrideProviderId) &&
            overrideProviderId.HasValue &&
            snapshot.ProvidersById.TryGetValue(overrideProviderId.Value, out ModelCapabilityView? overrideProvider) &&
            overrideProvider.Capability == InferenceProviderCapability.Chat)
        {
            // The store's LoadEnabledAsync only returns enabled providers, so a reference to a
            // disabled (or deleted) provider simply misses here and falls through below. The
            // Capability check is the resolver's own defense in depth: the API/UI should never
            // let a Transcription provider be assigned as a chat override, but if one somehow
            // is (found by Copilot review), it must not be handed to a chat call — fall through
            // to the Chat default instead of building an IChatClient against an ASR endpoint.
            provider = overrideProvider;
        }

        // The organization's own choice for a BUILT-IN agent type beats the platform-wide built-in
        // override below: built-in definitions are shared rows, so an org cannot write its choice on
        // them and keeps it in organization_agent_provider_overrides instead. A BYO provider that
        // is no longer visible (plan downgrade, disabled, deleted) is absent from the snapshot and
        // simply misses here.
        if (provider is null &&
            snapshot.OrgAgentOverrides.TryGetValue(agentType, out Guid? orgOverrideId) &&
            orgOverrideId.HasValue &&
            snapshot.ProvidersById.TryGetValue(orgOverrideId.Value, out ModelCapabilityView? orgOverride) &&
            orgOverride.Capability == InferenceProviderCapability.Chat)
        {
            provider = orgOverride;
        }

        // Fall back to the BUILT-IN definition's own override for the agent type that is running.
        // This is what makes "set VideoStoryEditor to Opus" mean the same thing everywhere: a solo
        // Agent step passes WorkflowStep.AgentDefinitionId and is handled above, but a room step's
        // default seats carry no per-seat AgentDefinitionId, and RoomStepExecutorBase therefore
        // asks for the agent TYPE with a null id. Without this branch those turns silently used
        // the global default provider instead of the override the operator configured — the one
        // place in the system where a configured override did not apply.
        if (provider is null &&
            snapshot.BuiltInAgentOverrides.TryGetValue(agentType, out Guid? builtInProviderId) &&
            builtInProviderId.HasValue &&
            snapshot.ProvidersById.TryGetValue(builtInProviderId.Value, out ModelCapabilityView? builtInProvider) &&
            builtInProvider.Capability == InferenceProviderCapability.Chat)
        {
            // Same enabled/Capability discipline as the by-id branch above: a disabled, deleted
            // or non-Chat provider simply misses here and falls through to the Chat default.
            provider = builtInProvider;
        }

        provider ??= snapshot.DefaultChatProvider;

        if (provider is null)
        {
            // F-1.1: an override or default that pointed at an unpriced platform model leaves nothing to run on;
            // the config fallback is platform-paid too and has no price row, so it is not a way out.
            ThrowIfUnpriced(snapshot, InferenceProviderCapability.Chat,
                UnpricedOverride(snapshot, agentType, agentDefinitionId));
        }

        return provider != null ? ResolveFromProvider(provider) : ResolveConfigFallback();
    }

    public async Task<string?> ResolveAgentReasoningEffortAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct)
    {
        Snapshot snapshot = await GetSnapshotAsync(ct);

        if (agentDefinitionId.HasValue &&
            snapshot.AgentReasoningEfforts.ById.TryGetValue(agentDefinitionId.Value, out string? stored) &&
            stored is not null)
        {
            return stored;
        }

        return snapshot.AgentReasoningEfforts.BuiltInByType.GetValueOrDefault(agentType);
    }

    public async Task<ResolvedTranscriptionProvider?> ResolveTranscriptionAsync(Guid? explicitProviderId, CancellationToken ct)
    {
        Snapshot snapshot = await GetSnapshotAsync(ct);

        ModelCapabilityView? provider = null;

        // Explicit ids resolve only inside this organization's snapshot (platform rows plus its own);
        // anything else is treated as missing, warned about and counted (D3).
        if (TryGetExplicit(snapshot, explicitProviderId, InferenceProviderCapability.Transcription) is { } explicitProvider)
        {
            // The store's LoadEnabledAsync only returns enabled providers, so a reference to a
            // disabled (or deleted) provider simply misses here and falls through to the default.
            // The Capability check guards the inverse gap (found by Copilot review): a Chat
            // provider id authored directly into VideoAnalyzeStepConfig.TranscriptionProviderId
            // (bypassing the UI, which only offers Transcription rows) must not be sent through
            // the transcription client factory.
            provider = explicitProvider;
        }

        provider ??= snapshot.DefaultTranscriptionProvider;
        if (provider is null) ThrowIfUnpriced(snapshot, InferenceProviderCapability.Transcription, explicitProviderId);

        return provider != null ? ResolveTranscriptionFromProvider(provider) : null;
    }

    public async Task<ResolvedSpeechSynthesisProvider?> ResolveSpeechSynthesisAsync(Guid? explicitProviderId, CancellationToken ct)
    {
        Snapshot snapshot = await GetSnapshotAsync(ct);

        // Health-aware (ProviderHealthSelector): an explicit, enabled SpeechSynthesis row always wins
        // (the store's LoadEnabledAsync only returns enabled providers, so a disabled or deleted id
        // simply misses; the Capability filter guards a Chat id authored into a voiceover config);
        // otherwise the default row — unless its last connection test FAILED and another row's
        // passed, in which case the healthy row narrates and the step says so.
        // When the organization has its own speech default, the platform's default row must not
        // compete as "the default" (the selector reads IsDefault straight off the rows), but a
        // platform row stays selectable when named explicitly or as a healthy non-default.
        IEnumerable<ModelCapabilityView> candidates = snapshot.ProvidersById.Values;
        if (snapshot.DefaultSpeechSynthesisProvider is { OrganizationId: not null })
        {
            candidates = candidates.Where(p => !(p.OrganizationId is null && p.IsDefault &&
                                                 p.Capability == InferenceProviderCapability.SpeechSynthesis));
        }

        ProviderSelection selection = ProviderHealthSelector.Select(
            candidates, InferenceProviderCapability.SpeechSynthesis, explicitProviderId);

        if (explicitProviderId.HasValue && selection.Reason != ProviderSelectionReason.Explicit)
        {
            // Not in the (possibly filtered) candidate list: either a platform default we hid, or
            // an id this organization cannot see at all.
            if (TryGetExplicit(snapshot, explicitProviderId, InferenceProviderCapability.SpeechSynthesis) is { } hidden)
                selection = new ProviderSelection(hidden, ProviderSelectionReason.Explicit, selection.ConfiguredDefault);
        }

        if (selection.Provider is null)
        {
            ThrowIfUnpriced(snapshot, InferenceProviderCapability.SpeechSynthesis, explicitProviderId);
            return null;
        }

        if (selection.UsedFallback && selection.ConfiguredDefault is { } passedOver)
        {
            _logger.LogWarning(
                "Default speech provider '{DefaultName}' failed its last connection test; using '{ProviderName}', whose last test passed.",
                passedOver.Name, selection.Provider.Name);
        }

        return ResolveSpeechSynthesisFromProvider(selection.Provider) with
        {
            SelectionReason = selection.Reason,
            LastTestOk = selection.Provider.LastTestOk,
            PassedOverDefaultName = selection.UsedFallback ? selection.ConfiguredDefault?.Name : null
        };
    }

    public async Task<ResolvedInferenceProvider?> ResolveVisionAsync(Guid? explicitProviderId, CancellationToken ct)
    {
        Snapshot snapshot = await GetSnapshotAsync(ct);

        ModelCapabilityView? provider = null;

        // Explicit ids resolve only inside this organization's snapshot (platform rows plus its own);
        // anything else is treated as missing, warned about and counted (D3).
        if (TryGetExplicit(snapshot, explicitProviderId, InferenceProviderCapability.Vision) is { } explicitProvider)
        {
            // Same defense-in-depth shape as ResolveTranscriptionAsync's Capability guard: a Chat
            // (or Transcription) provider id authored directly into
            // VideoAnalyzeStepConfig.VisionProviderId must not be sent through the vision path.
            provider = explicitProvider;
        }

        provider ??= snapshot.DefaultVisionProvider;
        if (provider is null) ThrowIfUnpriced(snapshot, InferenceProviderCapability.Vision, explicitProviderId);

        // Vision reuses the exact same chat-completions client construction as ResolveAsync —
        // ResolveFromProvider, not a dedicated "ResolveVisionFromProvider" — since a vision call
        // is just a chat call with an image content part (see IInferenceProviderResolver.ResolveVisionAsync).
        return provider != null ? ResolveFromProvider(provider) : null;
    }

    public async Task<ResolvedInferenceProvider?> ResolveChatAsync(Guid explicitProviderId, CancellationToken ct)
    {
        Snapshot snapshot = await GetSnapshotAsync(ct);

        ModelCapabilityView? provider = null;

        if (TryGetExplicit(snapshot, explicitProviderId, InferenceProviderCapability.Chat) is { } explicitProvider)
        {
            // Same by-id shape (and same Capability defense in depth) as the explicit-id branch of
            // ResolveVisionAsync — and, unlike every other resolver here, deliberately NO default
            // fallback: see IInferenceProviderResolver.ResolveChatAsync's doc comment for why a pin
            // that does not resolve must come back as null rather than being swapped for a provider
            // the caller never chose.
            provider = explicitProvider;
        }

        return provider != null ? ResolveFromProvider(provider) : null;
    }

    public async Task<ResolvedDecisionProvider?> ResolveDecisionAsync(Guid? explicitProviderId, CancellationToken ct)
    {
        Snapshot snapshot = await GetSnapshotAsync(ct);

        ModelCapabilityView? provider = null;

        // Explicit ids resolve only inside this organization's snapshot (platform rows plus its own);
        // anything else is treated as missing, warned about and counted (D3).
        if (TryGetExplicit(snapshot, explicitProviderId, InferenceProviderCapability.Decision) is { } explicitProvider)
        {
            // The store's LoadEnabledAsync only returns enabled providers, so a reference to a
            // disabled (or deleted) provider simply misses here and falls through to the default.
            // The Capability check guards the inverse gap (mirrors ResolveTranscriptionAsync):
            // a Chat provider id authored directly into a decision step config (bypassing the UI,
            // which only offers Decision rows) must not be sent through the decision client factory.
            provider = explicitProvider;
        }

        provider ??= snapshot.DefaultDecisionProvider;
        if (provider is null) ThrowIfUnpriced(snapshot, InferenceProviderCapability.Decision, explicitProviderId);

        return provider != null ? ResolveDecisionFromProvider(provider) : null;
    }

    public async Task<ResolvedVideoGenerationProvider?> ResolveVideoGenerationAsync(Guid? explicitProviderId, CancellationToken ct)
    {
        Snapshot snapshot = await GetSnapshotAsync(ct);

        ModelCapabilityView? provider = null;

        // Explicit ids resolve only inside this organization's snapshot (platform rows plus its own);
        // anything else is treated as missing, warned about and counted (D3).
        if (TryGetExplicit(snapshot, explicitProviderId, InferenceProviderCapability.VideoGeneration) is { } explicitProvider)
        {
            // The store's LoadEnabledAsync only returns enabled providers, so a reference to a
            // disabled (or deleted) provider simply misses here and falls through to the default.
            // The Capability check guards the inverse gap (mirrors ResolveTranscriptionAsync):
            // a Chat provider id authored directly into a video generation step config (bypassing the UI,
            // which only offers VideoGeneration rows) must not be sent through the video generation client factory.
            provider = explicitProvider;
        }

        provider ??= snapshot.DefaultVideoGenerationProvider;
        if (provider is null) ThrowIfUnpriced(snapshot, InferenceProviderCapability.VideoGeneration, explicitProviderId);

        return provider != null ? ResolveVideoGenerationFromProvider(provider) : null;
    }

    public async Task<ResolvedVideoGenerationProvider?> ResolveLipSyncAsync(Guid? explicitProviderId, CancellationToken ct)
    {
        Snapshot snapshot = await GetSnapshotAsync(ct);

        // Same capability guard as the other arms: a VideoGeneration row id authored into a lip-sync step never resolves here.
        ModelCapabilityView? provider = TryGetExplicit(snapshot, explicitProviderId, InferenceProviderCapability.LipSync)
                                        ?? snapshot.DefaultsByCapability.GetValueOrDefault(InferenceProviderCapability.LipSync);
        if (provider is null) ThrowIfUnpriced(snapshot, InferenceProviderCapability.LipSync, explicitProviderId);

        return provider != null ? ResolveVideoGenerationFromProvider(provider) : null;
    }

    public Task<ResolvedAudioGenerationProvider?> ResolveMusicGenerationAsync(Guid? explicitProviderId, CancellationToken ct) =>
        ResolveAudioAsync(InferenceProviderCapability.MusicGeneration, explicitProviderId, ct);

    public Task<ResolvedAudioGenerationProvider?> ResolveSoundEffectGenerationAsync(Guid? explicitProviderId, CancellationToken ct) =>
        ResolveAudioAsync(InferenceProviderCapability.SoundEffectGeneration, explicitProviderId, ct);

    public Task<ResolvedAudioGenerationProvider?> ResolveAudioIsolationAsync(Guid? explicitProviderId, CancellationToken ct) =>
        ResolveAudioAsync(InferenceProviderCapability.AudioIsolation, explicitProviderId, ct);

    public Task<ResolvedAudioGenerationProvider?> ResolveVideoUpscaleAsync(Guid? explicitProviderId, CancellationToken ct) =>
        ResolveAudioAsync(InferenceProviderCapability.VideoUpscale, explicitProviderId, ct);

    public Task<ResolvedAudioGenerationProvider?> ResolveFrameInterpolationAsync(Guid? explicitProviderId, CancellationToken ct) =>
        ResolveAudioAsync(InferenceProviderCapability.FrameInterpolation, explicitProviderId, ct);

    public Task<ResolvedAudioGenerationProvider?> ResolveVideoOutpaintAsync(Guid? explicitProviderId, CancellationToken ct) =>
        ResolveAudioAsync(InferenceProviderCapability.VideoOutpaint, explicitProviderId, ct);

    /// <summary>
    /// The one body behind both audio capabilities: explicit (same capability only) &gt; that capability's default
    /// &gt; none, with the same capability guard as the image arm, so a sound-effect row id authored into a music
    /// step can never be sent to the music path.
    /// </summary>
    private async Task<ResolvedAudioGenerationProvider?> ResolveAudioAsync(
        InferenceProviderCapability capability, Guid? explicitProviderId, CancellationToken ct)
    {
        Snapshot snapshot = await GetSnapshotAsync(ct);

        ModelCapabilityView? provider = Offered(TryGetExplicit(snapshot, explicitProviderId, capability))
                                        ?? Offered(snapshot.DefaultsByCapability.GetValueOrDefault(capability));
        if (provider is null) ThrowIfUnpriced(snapshot, capability, explicitProviderId);

        return provider is null ? null : ResolveAudioFromProvider(provider, capability);

        // Product decision (defence in depth): a row of the dormant ElevenLabs kind, e.g. one written before the
        // decision, is never resolved; the admin boundary already refuses to create one.
        ModelCapabilityView? Offered(ModelCapabilityView? row)
        {
            if (row?.Kind != InferenceProviderKind.ElevenLabs) return row;
            _logger.LogWarning("Audio provider '{ProviderName}' ({ProviderId}) is of the dormant ElevenLabs kind and is not used.", row.Name, row.Id);
            return null;
        }
    }

    public Task<ResolvedImageProvider?> ResolveImageGenerationAsync(Guid? explicitProviderId, CancellationToken ct) =>
        ResolveImageAsync(InferenceProviderCapability.ImageGeneration, explicitProviderId, ct);

    public Task<ResolvedImageProvider?> ResolveImageEditAsync(Guid? explicitProviderId, CancellationToken ct) =>
        ResolveImageAsync(InferenceProviderCapability.ImageEdit, explicitProviderId, ct);

    /// <summary>
    /// The one body behind every image capability (generation, the instruction edit and the six
    /// per-job editor capabilities). They differ ONLY in which capability they filter on, so writing it
    /// more than once would be several places to forget the Capability check — the defense in
    /// depth that stops an ImageGeneration row id authored into an edit step's config (bypassing the
    /// UI, which only ever offers matching rows) from being handed to the image client as if it were
    /// the other capability.
    /// </summary>
    private async Task<ResolvedImageProvider?> ResolveImageAsync(
        InferenceProviderCapability capability, Guid? explicitProviderId, CancellationToken ct)
    {
        Snapshot snapshot = await GetSnapshotAsync(ct);

        ModelCapabilityView? provider = null;

        // Explicit ids resolve only inside this organization's snapshot (platform rows plus its own);
        // anything else is treated as missing, warned about and counted (D3).
        if (TryGetExplicit(snapshot, explicitProviderId, capability) is { } explicitProvider)
        {
            provider = explicitProvider;
        }

        provider ??= snapshot.DefaultsByCapability.GetValueOrDefault(capability);
        if (provider is null) ThrowIfUnpriced(snapshot, capability, explicitProviderId);

        return provider != null ? ResolveImageFromProvider(provider, capability) : null;
    }

    public Task<ResolvedImageProvider?> ResolveImageJobAsync(
        InferenceProviderCapability capability, Guid? explicitProviderId, CancellationToken ct)
    {
        if (!ImageJobCapabilities.IsImageJob(capability))
        {
            throw new ArgumentOutOfRangeException(nameof(capability), capability,
                "Not an image job capability. Expected one of: " + string.Join(", ", ImageJobCapabilities.All) + ".");
        }

        return ResolveImageAsync(capability, explicitProviderId, ct);
    }

    public Task<ResolvedImageAnalysisProvider?> ResolveSegmentationAsync(Guid? explicitProviderId, CancellationToken ct) =>
        ResolveImageAnalysisAsync(InferenceProviderCapability.Segmentation, explicitProviderId, ct);

    public Task<ResolvedImageAnalysisProvider?> ResolveDepthAsync(Guid? explicitProviderId, CancellationToken ct) =>
        ResolveImageAnalysisAsync(InferenceProviderCapability.Depth, explicitProviderId, ct);

    /// <summary>
    /// Resolves the provider a <see cref="InferenceProviderCapability.VideoSegmentation"/> call uses:
    /// the explicit id when it is enabled and carries exactly that capability, else the single default
    /// row of that capability, else <c>null</c>. No legacy configuration fallback: this capability was
    /// born after provider rows, so there is no pre-existing deployment to keep working, and an
    /// unresolved answer is the truthful one (the analyze step refuses with <c>SEGMENTATION_NO_PROVIDER</c>
    /// rather than falling back to the feature tracker).
    /// </summary>
    public async Task<ResolvedImageAnalysisProvider?> ResolveVideoSegmentationAsync(
        Guid? explicitProviderId, CancellationToken ct)
    {
        Snapshot snapshot = await GetSnapshotAsync(ct);

        // The explicit id resolves only inside this organization's snapshot (platform rows plus its own)
        // and only when it carries THIS capability: a Segmentation or Depth row id cannot serve a video
        // request even if an author writes one into a step config (TryGetExplicit filters on it).
        ModelCapabilityView? provider =
            TryGetExplicit(snapshot, explicitProviderId, InferenceProviderCapability.VideoSegmentation)
            ?? snapshot.DefaultsByCapability.GetValueOrDefault(InferenceProviderCapability.VideoSegmentation);
        if (provider is null)
        {
            ThrowIfUnpriced(snapshot, InferenceProviderCapability.VideoSegmentation, explicitProviderId);
        }

        return provider is null
            ? null
            : ResolveImageAnalysisFromProvider(provider, InferenceProviderCapability.VideoSegmentation);
    }

    /// <summary>
    /// Resolves the provider a <see cref="InferenceProviderCapability.ContentModeration"/> call uses:
    /// the explicit id when it is enabled and carries exactly that capability, else the single
    /// default row of that capability, else <c>null</c>. No legacy configuration fallback (no key
    /// ever named a moderation service), and an unpriced platform default still fails closed with
    /// <see cref="ModelUnpricedException"/> exactly like the other capabilities.
    /// </summary>
    public async Task<ResolvedContentModerationProvider?> ResolveContentModerationAsync(
        Guid? explicitProviderId, CancellationToken ct)
    {
        Snapshot snapshot = await GetSnapshotAsync(ct);

        // The explicit id resolves only inside this organization's snapshot (platform rows plus its
        // own) and only when it carries THIS capability: a Chat/Vision row id written straight into
        // a config must not be sent through the moderation client (TryGetExplicit filters on it).
        // Platform rows only: an organization's own row is never trusted to answer a moderation call.
        ModelCapabilityView? provider = PlatformRow(snapshot, InferenceProviderCapability.ContentModeration, explicitProviderId);
        if (provider is null)
        {
            ThrowIfUnpriced(snapshot, InferenceProviderCapability.ContentModeration, explicitProviderId);
        }

        return provider is null ? null : ResolveContentModerationFromProvider(provider);
    }

    /// <summary>
    /// Resolves the transcription provider the safety check of a file's speech uses: the platform's default
    /// transcription row only, never an organization's own (see <see cref="ResolveContentModerationAsync"/>).
    /// </summary>
    public async Task<ResolvedTranscriptionProvider?> ResolvePlatformTranscriptionAsync(CancellationToken ct)
    {
        Snapshot snapshot = await GetSnapshotAsync(ct);
        ModelCapabilityView? provider = PlatformRow(snapshot, InferenceProviderCapability.Transcription, null);
        if (provider is null) ThrowIfUnpriced(snapshot, InferenceProviderCapability.Transcription, null);
        return provider is null ? null : ResolveTranscriptionFromProvider(provider);
    }

    /// <summary>The explicit platform row when named, else the platform's default row of the capability; never an organization row.</summary>
    private static ModelCapabilityView? PlatformRow(Snapshot snapshot, InferenceProviderCapability capability, Guid? explicitProviderId)
    {
        IEnumerable<ModelCapabilityView> platform = snapshot.ProvidersById.Values
            .Where(p => p.OrganizationId is null && p.Capability == capability);
        ModelCapabilityView? named = explicitProviderId is Guid id ? platform.FirstOrDefault(p => p.Id == id) : null;
        return named ?? platform.FirstOrDefault(p => p.IsDefault);
    }

    /// <summary>
    /// The one body behind both analysis capabilities, exactly as
    /// <see cref="ResolveImageAsync(InferenceProviderCapability, Guid?, CancellationToken)"/> is the one
    /// body behind both image ones. They differ only in which capability they filter on, so writing it
    /// twice would be two places to forget the Capability check — the defense in depth that stops a
    /// Segmentation row id authored into a depth step's config, bypassing the UI, from being handed to
    /// the depth call.
    /// </summary>
    private async Task<ResolvedImageAnalysisProvider?> ResolveImageAnalysisAsync(
        InferenceProviderCapability capability, Guid? explicitProviderId, CancellationToken ct)
    {
        Snapshot snapshot = await GetSnapshotAsync(ct);

        ModelCapabilityView? provider = null;

        // Explicit ids resolve only inside this organization's snapshot (platform rows plus its own);
        // anything else is treated as missing, warned about and counted (D3).
        if (TryGetExplicit(snapshot, explicitProviderId, capability) is { } explicitProvider)
        {
            provider = explicitProvider;
        }

        provider ??= capability == InferenceProviderCapability.Segmentation
            ? snapshot.DefaultSegmentationProvider
            : snapshot.DefaultDepthProvider;
        if (provider is null) ThrowIfUnpriced(snapshot, capability, explicitProviderId);

        return provider != null ? ResolveImageAnalysisFromProvider(provider, capability) : null;
    }

    public async Task<ResolvedEmbeddingProvider?> ResolveEmbeddingAsync(Guid? explicitProviderId, CancellationToken ct)
    {
        Snapshot snapshot = await GetSnapshotAsync(ct);

        ModelCapabilityView? provider = null;

        // Explicit ids resolve only inside this organization's snapshot (platform rows plus its own);
        // anything else is treated as missing, warned about and counted (D3).
        if (TryGetExplicit(snapshot, explicitProviderId, InferenceProviderCapability.Embedding) is { } explicitProvider)
        {
            // Same enabled/Capability defense in depth as ResolveTranscriptionAsync.
            provider = explicitProvider;
        }

        provider ??= snapshot.DefaultEmbeddingProvider;
        if (provider is null) ThrowIfUnpriced(snapshot, InferenceProviderCapability.Embedding, explicitProviderId);

        if (provider != null)
        {
            ResolvedTranscriptionProvider shape = ResolveTranscriptionFromProvider(provider);
            return new ResolvedEmbeddingProvider(
                shape.ProviderId, shape.Name, shape.Kind, shape.Endpoint, shape.ModelName, shape.ApiKey, shape.TimeoutSeconds,
                shape.OrganizationId, shape.IsPlatformManaged, shape.AccountId, shape.ModelId);
        }

        // Legacy fallback: embeddings used to be wired to AzureOpenAI:* config directly.
        string endpoint = _configuration["AzureOpenAI:Endpoint"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        string deployment = _configuration["VectorSearch:EmbeddingDeployment"] is { Length: > 0 } d
            ? d
            : DefaultEmbeddingDeployment;

        return new ResolvedEmbeddingProvider(
            null,
            "AzureOpenAI (config fallback)",
            InferenceProviderKind.AzureOpenAI,
            endpoint,
            deployment,
            _configuration["AzureOpenAI:ApiKey"] ?? string.Empty,
            DefaultTimeoutSeconds);
    }

    private ResolvedInferenceProvider ResolveFromProvider(ModelCapabilityView provider)
    {
        string apiKey = string.Empty;

        if (!string.IsNullOrEmpty(provider.ApiKeyEncrypted))
        {
            if (_secretProtector.TryUnprotect(provider.ApiKeyEncrypted, out string plaintext))
            {
                apiKey = plaintext;
            }
            else
            {
                _logger.LogWarning(
                    "Could not decrypt the API key for inference provider '{ProviderName}' ({ProviderId}); proceeding without a key.",
                    provider.Name,
                    provider.Id);
            }
        }

        return new ResolvedInferenceProvider(
            provider.Id,
            provider.Name,
            provider.Kind,
            provider.Endpoint,
            provider.ModelName,
            apiKey,
            provider.TimeoutSeconds ?? DefaultTimeoutSeconds,
            provider.PersonalOAuthMode,
            provider.OrganizationId,
            provider.OrganizationId is null,
            AccountId: provider.ProviderId,
            ModelId: provider.ModelId,
            Capability: provider.Capability);
    }

    private ResolvedTranscriptionProvider ResolveTranscriptionFromProvider(ModelCapabilityView provider)
    {
        string apiKey = string.Empty;

        if (!string.IsNullOrEmpty(provider.ApiKeyEncrypted))
        {
            if (_secretProtector.TryUnprotect(provider.ApiKeyEncrypted, out string plaintext))
            {
                apiKey = plaintext;
            }
            else
            {
                _logger.LogWarning(
                    "Could not decrypt the API key for inference provider '{ProviderName}' ({ProviderId}); proceeding without a key.",
                    provider.Name,
                    provider.Id);
            }
        }

        return new ResolvedTranscriptionProvider(
            provider.Id,
            provider.Name,
            provider.Kind,
            provider.Endpoint,
            provider.ModelName,
            apiKey,
            provider.TimeoutSeconds ?? DefaultTimeoutSeconds,
            provider.OrganizationId,
            provider.OrganizationId is null,
            AccountId: provider.ProviderId,
            ModelId: provider.ModelId);
    }

    /// <summary>
    /// Resolves the provider a <see cref="InferenceProviderCapability.Rerank"/> call uses: the explicit id when it
    /// is enabled and carries exactly that capability, else the single default row of that capability, else
    /// <c>null</c>. No legacy configuration fallback, and an unpriced platform default fails closed with
    /// <see cref="ModelUnpricedException"/> exactly like the other capabilities.
    /// </summary>
    public async Task<ResolvedRerankProvider?> ResolveRerankAsync(Guid? explicitProviderId, CancellationToken ct)
    {
        Snapshot snapshot = await GetSnapshotAsync(ct);

        ModelCapabilityView? provider =
            TryGetExplicit(snapshot, explicitProviderId, InferenceProviderCapability.Rerank)
            ?? snapshot.DefaultsByCapability.GetValueOrDefault(InferenceProviderCapability.Rerank);
        if (provider is null)
        {
            ThrowIfUnpriced(snapshot, InferenceProviderCapability.Rerank, explicitProviderId);
        }

        return provider is null ? null : ResolveRerankFromProvider(provider);
    }

    /// <summary>
    /// Resolves the provider a <see cref="InferenceProviderCapability.WebSearch"/> call uses: the explicit id when it
    /// is enabled and carries exactly that capability, else the single default row of that capability, else
    /// <c>null</c>. No legacy configuration fallback.
    /// </summary>
    public async Task<ResolvedWebSearchProvider?> ResolveWebSearchAsync(Guid? explicitProviderId, CancellationToken ct)
    {
        Snapshot snapshot = await GetSnapshotAsync(ct);

        ModelCapabilityView? provider =
            TryGetExplicit(snapshot, explicitProviderId, InferenceProviderCapability.WebSearch)
            ?? snapshot.DefaultsByCapability.GetValueOrDefault(InferenceProviderCapability.WebSearch);
        if (provider is null)
        {
            ThrowIfUnpriced(snapshot, InferenceProviderCapability.WebSearch, explicitProviderId);
        }

        if (provider is null) return null;
        ResolvedContentModerationProvider m = ResolveContentModerationFromProvider(provider);
        return new ResolvedWebSearchProvider(
            m.ProviderId, m.Name, m.Kind, m.Endpoint, m.ModelName, m.ApiKey, m.TimeoutSeconds,
            m.OrganizationId, m.IsPlatformManaged, m.AccountId, m.ModelId);
    }

    private ResolvedRerankProvider ResolveRerankFromProvider(ModelCapabilityView provider)
    {
        ResolvedContentModerationProvider m = ResolveContentModerationFromProvider(provider);
        return new ResolvedRerankProvider(
            m.ProviderId, m.Name, m.Kind, m.Endpoint, m.ModelName, m.ApiKey, m.TimeoutSeconds,
            m.OrganizationId, m.IsPlatformManaged, m.AccountId, m.ModelId);
    }

    /// <summary>
    /// The same decrypt-and-default shape as <see cref="ResolveTranscriptionFromProvider"/>. The
    /// provider row's general timeout is carried through for diagnostics only; the moderation
    /// client deliberately applies the short <c>ContentModeration:TimeoutSeconds</c> instead.
    /// </summary>
    private ResolvedContentModerationProvider ResolveContentModerationFromProvider(ModelCapabilityView provider)
    {
        string apiKey = string.Empty;

        if (!string.IsNullOrEmpty(provider.ApiKeyEncrypted))
        {
            if (_secretProtector.TryUnprotect(provider.ApiKeyEncrypted, out string plaintext))
            {
                apiKey = plaintext;
            }
            else
            {
                _logger.LogWarning(
                    "Could not decrypt the API key for inference provider '{ProviderName}' ({ProviderId}); proceeding without a key.",
                    provider.Name,
                    provider.Id);
            }
        }

        return new ResolvedContentModerationProvider(
            provider.Id,
            provider.Name,
            provider.Kind,
            provider.Endpoint,
            provider.ModelName,
            apiKey,
            provider.TimeoutSeconds ?? DefaultTimeoutSeconds,
            provider.OrganizationId,
            provider.OrganizationId is null,
            AccountId: provider.ProviderId,
            ModelId: provider.ModelId);
    }

    private ResolvedSpeechSynthesisProvider ResolveSpeechSynthesisFromProvider(ModelCapabilityView provider)
    {
        string apiKey = string.Empty;

        if (!string.IsNullOrEmpty(provider.ApiKeyEncrypted))
        {
            if (_secretProtector.TryUnprotect(provider.ApiKeyEncrypted, out string plaintext))
            {
                apiKey = plaintext;
            }
            else
            {
                _logger.LogWarning(
                    "Could not decrypt the API key for inference provider '{ProviderName}' ({ProviderId}); proceeding without a key.",
                    provider.Name,
                    provider.Id);
            }
        }

        return new ResolvedSpeechSynthesisProvider(
            provider.Id,
            provider.Name,
            provider.Kind,
            provider.Endpoint,
            provider.ModelName,
            apiKey,
            provider.TimeoutSeconds ?? DefaultTimeoutSeconds,
            OrganizationId: provider.OrganizationId,
            IsPlatformManaged: provider.OrganizationId is null,
            AccountId: provider.ProviderId,
            ModelId: provider.ModelId);
    }

    private ResolvedDecisionProvider ResolveDecisionFromProvider(ModelCapabilityView provider)
    {
        string apiKey = string.Empty;

        if (!string.IsNullOrEmpty(provider.ApiKeyEncrypted))
        {
            if (_secretProtector.TryUnprotect(provider.ApiKeyEncrypted, out string plaintext))
            {
                apiKey = plaintext;
            }
            else
            {
                _logger.LogWarning(
                    "Could not decrypt the API key for inference provider '{ProviderName}' ({ProviderId}); proceeding without a key.",
                    provider.Name,
                    provider.Id);
            }
        }

        return new ResolvedDecisionProvider(
            provider.Id,
            provider.Name,
            provider.Kind,
            provider.Endpoint,
            provider.ModelName,
            apiKey,
            provider.TimeoutSeconds ?? DefaultTimeoutSeconds,
            provider.OrganizationId,
            provider.OrganizationId is null,
            AccountId: provider.ProviderId,
            ModelId: provider.ModelId);
    }

    private ResolvedVideoGenerationProvider ResolveVideoGenerationFromProvider(ModelCapabilityView provider)
    {
        string apiKey = string.Empty;

        if (!string.IsNullOrEmpty(provider.ApiKeyEncrypted))
        {
            if (_secretProtector.TryUnprotect(provider.ApiKeyEncrypted, out string plaintext))
            {
                apiKey = plaintext;
            }
            else
            {
                _logger.LogWarning(
                    "Could not decrypt the API key for inference provider '{ProviderName}' ({ProviderId}); proceeding without a key.",
                    provider.Name,
                    provider.Id);
            }
        }

        return new ResolvedVideoGenerationProvider(
            provider.Id,
            provider.Name,
            provider.Kind,
            provider.Endpoint,
            provider.ModelName,
            apiKey,
            provider.TimeoutSeconds ?? DefaultTimeoutSeconds,
            provider.OrganizationId,
            provider.OrganizationId is null,
            AccountId: provider.ProviderId,
            ModelId: provider.ModelId);
    }

    /// <summary>
    /// The same decryption-and-default shape as <see cref="ResolveVideoGenerationFromProvider"/>;
    /// <paramref name="capability"/> is stamped onto the result so the caller can tell a generation
    /// resolution from an edit one without having to remember which method it called.
    /// </summary>
    private ResolvedImageProvider ResolveImageFromProvider(ModelCapabilityView provider, InferenceProviderCapability capability)
    {
        string apiKey = string.Empty;

        if (!string.IsNullOrEmpty(provider.ApiKeyEncrypted))
        {
            if (_secretProtector.TryUnprotect(provider.ApiKeyEncrypted, out string plaintext))
            {
                apiKey = plaintext;
            }
            else
            {
                _logger.LogWarning(
                    "Could not decrypt the API key for inference provider '{ProviderName}' ({ProviderId}); proceeding without a key.",
                    provider.Name,
                    provider.Id);
            }
        }

        return new ResolvedImageProvider(
            provider.Id,
            provider.Name,
            provider.Kind,
            capability,
            provider.Endpoint,
            provider.ModelName,
            apiKey,
            provider.TimeoutSeconds ?? DefaultTimeoutSeconds,
            provider.OrganizationId,
            provider.OrganizationId is null,
            AccountId: provider.ProviderId,
            ModelId: provider.ModelId);
    }

    /// <summary>
    /// The same decryption-and-default shape as <see cref="ResolveImageFromProvider"/>;
    /// <paramref name="capability"/> is stamped onto the result so the caller can tell a segmentation
    /// resolution from a depth one without having to remember which method it called.
    /// </summary>
    /// <summary>The same decryption-and-default shape as <see cref="ResolveImageFromProvider"/>.</summary>
    private ResolvedAudioGenerationProvider ResolveAudioFromProvider(ModelCapabilityView provider, InferenceProviderCapability capability)
    {
        string apiKey = string.Empty;

        if (!string.IsNullOrEmpty(provider.ApiKeyEncrypted))
        {
            if (_secretProtector.TryUnprotect(provider.ApiKeyEncrypted, out string plaintext))
            {
                apiKey = plaintext;
            }
            else
            {
                // No ambient fallback: a blank key makes the audio client THROW when it is built.
                _logger.LogWarning(
                    "Could not decrypt the API key for inference provider '{ProviderName}' ({ProviderId}); proceeding without a key.",
                    provider.Name,
                    provider.Id);
            }
        }

        return new ResolvedAudioGenerationProvider(
            provider.Id,
            provider.Name,
            provider.Kind,
            capability,
            provider.Endpoint,
            provider.ModelName,
            apiKey,
            provider.TimeoutSeconds ?? DefaultTimeoutSeconds,
            provider.OrganizationId,
            provider.OrganizationId is null,
            AccountId: provider.ProviderId,
            ModelId: provider.ModelId);
    }

    private ResolvedImageAnalysisProvider ResolveImageAnalysisFromProvider(
        ModelCapabilityView provider, InferenceProviderCapability capability)
    {
        string apiKey = string.Empty;

        if (!string.IsNullOrEmpty(provider.ApiKeyEncrypted))
        {
            if (_secretProtector.TryUnprotect(provider.ApiKeyEncrypted, out string plaintext))
            {
                apiKey = plaintext;
            }
            else
            {
                _logger.LogWarning(
                    "Could not decrypt the API key for inference provider '{ProviderName}' ({ProviderId}); proceeding without a key.",
                    provider.Name,
                    provider.Id);
            }
        }

        return new ResolvedImageAnalysisProvider(
            provider.Id,
            provider.Name,
            provider.Kind,
            capability,
            provider.Endpoint,
            provider.ModelName,
            apiKey,
            provider.TimeoutSeconds ?? DefaultTimeoutSeconds,
            provider.OrganizationId,
            provider.OrganizationId is null,
            AccountId: provider.ProviderId,
            ModelId: provider.ModelId);
    }

    private ResolvedInferenceProvider ResolveConfigFallback()
    {
        string endpoint = _configuration["AzureOpenAI:Endpoint"] ?? string.Empty;
        string apiKey = _configuration["AzureOpenAI:ApiKey"] ?? string.Empty;
        string deploymentName = _configuration["AzureOpenAI:DeploymentName"] ?? DefaultFallbackModelName;

        return new ResolvedInferenceProvider(
            ProviderId: null,
            Name: FallbackProviderName,
            Kind: InferenceProviderKind.AzureOpenAI,
            Endpoint: endpoint,
            ModelName: deploymentName,
            ApiKey: apiKey,
            TimeoutSeconds: DefaultTimeoutSeconds);
    }

    /// <inheritdoc />
    public void Invalidate(Guid? organizationId)
    {
        Guid key = organizationId ?? Guid.Empty;
        _generations.AddOrUpdate(key, 1, (_, g) => g + 1);
        _snapshots.TryRemove(key, out _);
    }

    /// <inheritdoc />
    public void InvalidateAll()
    {
        foreach (Guid key in _snapshots.Keys.Concat(_generations.Keys).Append(Guid.Empty).Distinct().ToList())
        {
            Invalidate(key == Guid.Empty ? null : key);
        }
    }

    /// <summary>
    /// The explicit provider this organization may use, or null. A capability mismatch is a silent
    /// miss (the long-standing defense in depth); an id that is not in the snapshot at all is
    /// FOREIGN (another organization's row, disabled, deleted, or BYO after a downgrade): it is
    /// logged and counted, then treated as missing so the caller falls through.
    /// </summary>
    private ModelCapabilityView? TryGetExplicit(Snapshot snapshot, Guid? explicitProviderId, InferenceProviderCapability capability)
    {
        if (explicitProviderId is not Guid id)
            return null;

        if (snapshot.ProvidersById.TryGetValue(id, out ModelCapabilityView? found))
            return found.Capability == capability ? found : null;

        if (snapshot.UnpricedById.TryGetValue(id, out ModelCapabilityView? unpriced))
        {
            if (unpriced.Capability == capability)
            {
                _logger.LogWarning(
                    "Model capability {ProviderId} ({Capability}) has no effective price; treating it as absent and using the default.",
                    id, capability);
            }

            return null;
        }

        InferenceTenancyDiagnostics.ForeignProviderRef.Add(
            1,
            new KeyValuePair<string, object?>("capability", capability.ToString()));
        _logger.LogWarning(
            "Inference provider {ProviderId} ({Capability}) is not visible to organization {OrganizationId}; ignoring the reference.",
            id, capability, snapshot.OrganizationId == Guid.Empty ? "platform" : snapshot.OrganizationId.ToString());
        return null;
    }

    /// <summary>
    /// F-1.1: nothing was left to resolve. When that is because the explicit choice or the capability's default is an
    /// unpriced platform model (only ever true with <c>Billing:Enabled</c>), refuse with <see cref="ModelUnpricedException"/>
    /// rather than answering "nothing configured" (or, for chat and embeddings, the platform-paid config fallback).
    /// </summary>
    private static void ThrowIfUnpriced(Snapshot snapshot, InferenceProviderCapability capability, Guid? explicitProviderId)
    {
        ModelCapabilityView? skipped =
            (explicitProviderId is Guid id && snapshot.UnpricedById.TryGetValue(id, out ModelCapabilityView? named)
             && named.Capability == capability ? named : null)
            ?? snapshot.UnpricedDefaults.GetValueOrDefault(capability);

        if (skipped is not null)
            throw new ModelUnpricedException(capability, skipped.Name);
    }

    /// <summary>The first chat override (custom agent, org, built-in) of this call that names an unpriced model, if any.</summary>
    private static Guid? UnpricedOverride(Snapshot snapshot, AgentType agentType, Guid? agentDefinitionId)
    {
        if (snapshot.UnpricedById.Count == 0) return null;

        Guid?[] candidates =
        [
            agentDefinitionId is Guid a ? snapshot.AgentOverrides.GetValueOrDefault(a) : null,
            snapshot.OrgAgentOverrides.GetValueOrDefault(agentType),
            snapshot.BuiltInAgentOverrides.GetValueOrDefault(agentType)
        ];
        return candidates.FirstOrDefault(id => id is Guid g && snapshot.UnpricedById.ContainsKey(g));
    }

    /// <summary>
    /// A row the resolver must treat as absent: billing is on and it is a PLATFORM capability priced from its table with
    /// no effective price. BYO rows are never priced and oracle rows are priced by the vendor's quote, so neither is.
    /// </summary>
    private static bool IsUnpriced(ModelCapabilityView row, IReadOnlySet<Guid> unpricedIds) =>
        row.OrganizationId is null && row.PriceSource == ModelPriceSource.Table && unpricedIds.Contains(row.Id);

    private bool BillingEnabled => _configuration.GetValue("Billing:Enabled", false);

    /// <inheritdoc />
    public bool UnpricedModelsFailClosed => BillingEnabled;

    private async Task<Snapshot> GetSnapshotAsync(CancellationToken ct)
    {
        Guid key = _tenantAccessor?.OrganizationId ?? Guid.Empty;

        if (_snapshots.TryGetValue(key, out Snapshot? current) && DateTime.UtcNow - current.LoadedAt < _cacheTtl)
        {
            return current;
        }

        SemaphoreSlim refreshLock = _refreshLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(ct);
        try
        {
            if (_snapshots.TryGetValue(key, out current) && DateTime.UtcNow - current.LoadedAt < _cacheTtl)
            {
                return current;
            }

            int generation = _generations.GetValueOrDefault(key);
            Guid? organizationId = key == Guid.Empty ? null : key;

            using IServiceScope scope = _scopeFactory.CreateScope();
            IInferenceProviderStore store = scope.ServiceProvider.GetRequiredService<IInferenceProviderStore>();

            IReadOnlyList<ModelCapabilityView> loaded = await store.LoadEnabledAsync(organizationId, ct);

            // Defense in depth over the store: never keep another organization's row, whatever the
            // store returned. A null organization sees platform rows only.
            IEnumerable<ModelCapabilityView> visible = loaded.Where(
                p => p.OrganizationId is null || p.OrganizationId == organizationId);

            if (organizationId is Guid org && !await AllowsByoAsync(scope.ServiceProvider, org, ct))
            {
                // Plan without BYO (Trial, or after a downgrade): the organization's own rows stay
                // in the database but are invisible, so it resolves to platform providers.
                visible = visible.Where(p => p.OrganizationId is null);
            }

            List<ModelCapabilityView> all = visible.ToList();

            // F-1.1 (billing on only; self-host keeps every row exactly as before): an enabled platform capability with
            // no effective price is never offered. It leaves the resolvable set, but is kept aside so an explicit
            // reference to it is not mistaken for a foreign one and an empty result can say WHY it is empty.
            IReadOnlySet<Guid> unpricedIds = BillingEnabled
                ? await store.LoadUnpricedCapabilityIdsAsync(DateTime.UtcNow, ct)
                : new HashSet<Guid>();
            List<ModelCapabilityView> enabled = [.. all.Where(p => !IsUnpriced(p, unpricedIds))];
            Dictionary<Guid, ModelCapabilityView> unpricedById = all.Where(p => IsUnpriced(p, unpricedIds)).ToDictionary(p => p.Id);

            IReadOnlyDictionary<Guid, Guid?> overrides = await store.LoadAgentOverridesAsync(organizationId, ct);
            IReadOnlyDictionary<AgentType, Guid?> orgOverrides = await store.LoadOrgAgentOverridesAsync(organizationId, ct);
            IReadOnlyDictionary<AgentType, Guid?> builtInOverrides = await store.LoadBuiltInAgentOverridesAsync(ct);
            AgentReasoningEfforts reasoningEfforts = await store.LoadAgentReasoningEffortsAsync(organizationId, ct);

            Dictionary<Guid, ModelCapabilityView> providersById = enabled.ToDictionary(p => p.Id);

            // R4: the capability split means "the default row" is no longer a single concept.
            // Each lookup filters on Capability explicitly, and prefers the organization's own
            // default over the platform's (D3: org default -> platform default).
            ModelCapabilityView? DefaultAmong(List<ModelCapabilityView> rows, InferenceProviderCapability capability) =>
                rows.FirstOrDefault(p => p.IsDefault && p.Capability == capability && p.OrganizationId is not null)
                ?? rows.FirstOrDefault(p => p.IsDefault && p.Capability == capability && p.OrganizationId is null);
            ModelCapabilityView? DefaultFor(InferenceProviderCapability capability) => DefaultAmong(enabled, capability);

            // The default each capability WOULD have had, when that is an unpriced row (what a refusal names).
            Dictionary<InferenceProviderCapability, ModelCapabilityView> unpricedDefaults = unpricedById.Count == 0
                ? []
                : Enum.GetValues<InferenceProviderCapability>()
                    .Select(c => (Capability: c, Provider: DefaultAmong(all, c)))
                    .Where(pair => pair.Provider is not null && unpricedById.ContainsKey(pair.Provider.Id))
                    .ToDictionary(pair => pair.Capability, pair => pair.Provider!);

            Snapshot snapshot = new(
                key, providersById, overrides, orgOverrides, builtInOverrides, reasoningEfforts,
                DefaultFor(InferenceProviderCapability.Chat),
                DefaultFor(InferenceProviderCapability.Transcription),
                DefaultFor(InferenceProviderCapability.SpeechSynthesis),
                DefaultFor(InferenceProviderCapability.Vision),
                DefaultFor(InferenceProviderCapability.Decision),
                DefaultFor(InferenceProviderCapability.VideoGeneration),
                DefaultFor(InferenceProviderCapability.Embedding),
                DefaultFor(InferenceProviderCapability.ImageGeneration),
                DefaultFor(InferenceProviderCapability.ImageEdit),
                DefaultFor(InferenceProviderCapability.Segmentation),
                DefaultFor(InferenceProviderCapability.Depth),
                Enum.GetValues<InferenceProviderCapability>()
                    .Select(c => (Capability: c, Provider: DefaultFor(c)))
                    .Where(pair => pair.Provider is not null)
                    .ToDictionary(pair => pair.Capability, pair => pair.Provider!),
                unpricedById,
                unpricedDefaults,
                DateTime.UtcNow);

            if (_generations.GetValueOrDefault(key) == generation)
            {
                _snapshots[key] = snapshot;
            }

            return snapshot;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private static async Task<bool> AllowsByoAsync(IServiceProvider services, Guid organizationId, CancellationToken ct)
    {
        // No registered entitlements (a bare test container) means no plan gating, which is also
        // what self-hosted deployments get from the config-backed default.
        IOrganizationEntitlements? entitlements = services.GetService<IOrganizationEntitlements>();
        return entitlements is null || await entitlements.AllowsByoProvidersAsync(organizationId, ct);
    }

    private sealed record Snapshot(
        Guid OrganizationId,
        IReadOnlyDictionary<Guid, ModelCapabilityView> ProvidersById,
        IReadOnlyDictionary<Guid, Guid?> AgentOverrides,
        IReadOnlyDictionary<AgentType, Guid?> OrgAgentOverrides,
        IReadOnlyDictionary<AgentType, Guid?> BuiltInAgentOverrides,
        AgentReasoningEfforts AgentReasoningEfforts,
        ModelCapabilityView? DefaultChatProvider,
        ModelCapabilityView? DefaultTranscriptionProvider,
        ModelCapabilityView? DefaultSpeechSynthesisProvider,
        ModelCapabilityView? DefaultVisionProvider,
        ModelCapabilityView? DefaultDecisionProvider,
        ModelCapabilityView? DefaultVideoGenerationProvider,
        ModelCapabilityView? DefaultEmbeddingProvider,
        ModelCapabilityView? DefaultImageGenerationProvider,
        ModelCapabilityView? DefaultImageEditProvider,
        ModelCapabilityView? DefaultSegmentationProvider,
        ModelCapabilityView? DefaultDepthProvider,
        IReadOnlyDictionary<InferenceProviderCapability, ModelCapabilityView> DefaultsByCapability,
        IReadOnlyDictionary<Guid, ModelCapabilityView> UnpricedById,
        IReadOnlyDictionary<InferenceProviderCapability, ModelCapabilityView> UnpricedDefaults,
        DateTime LoadedAt);
}
