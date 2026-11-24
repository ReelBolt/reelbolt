using System.Security.Cryptography;
using System.Text;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Execution.Caching;

/// <summary>
/// A10 / R10: the RESOLVED-provider half of the step-cache key.
///
/// <para>
/// The key already hashed the step's own config blob and the agent's
/// <c>AgentDefinition.InferenceProviderId</c> — i.e. what the workflow ASKED for. It did not hash
/// what the run actually got: the same config resolves to a different backend the moment an
/// operator repoints an organization's default provider, and without this component a cached step
/// would replay output produced by the old one. This computes a fingerprint of every provider the
/// step would resolve to, so switching provider invalidates the cached result.
/// </para>
///
/// <para>
/// <b>What is fingerprinted.</b> One line per capability — <c>{capability}:{providerId}:{model}</c>
/// — for the eleven capabilities a step can reach (Chat, Transcription, Vision, Decision,
/// VideoGeneration, SpeechSynthesis, ImageGeneration, ImageEdit, Segmentation, Depth,
/// VideoSegmentation), each resolved
/// with the step's own explicit id for that
/// capability where its config names one and with the configured default otherwise; plus one line
/// per explicit reference on its own, so a step naming two providers of one capability
/// (<c>VideoAnalyze</c>'s <c>visionProviderId</c> and <c>objectVisionProviderId</c>) fingerprints
/// both. The lines are sorted and SHA-256'd, so the value is stable, bounded, and carries no key
/// material: a provider id and a model name are not secrets, and the API key never appears here
/// (<see cref="ResolvedInferenceProvider.CacheKey"/>, which does hash it, is deliberately not used).
/// </para>
///
/// <para>
/// <b>An unresolved capability is fingerprinted as unresolved</b> (<c>-</c> for both fields), never
/// by the id that was asked for. Two steps whose references both fail to resolve behave identically,
/// so they are the same work and may share a cache entry — and, importantly, a step naming ANOTHER
/// ORGANIZATION'S provider id resolves to the default (the resolver treats a foreign id as missing
/// and counts <c>reelbolt.inference.foreign_provider_ref</c>), so its fingerprint is the same as one
/// that named nothing at all. That is the resolve-time half of "no workflow can reference another
/// organization's provider": the foreign id reaches neither the backend nor the cache key.
/// </para>
///
/// <para>
/// Over-inclusion is deliberate and safe — an unrelated default changing costs a cache miss, never a
/// wrong hit — while under-inclusion would be a correctness bug, so every capability a step could
/// reach is fingerprinted even when this particular step type would not use it.
/// </para>
/// </summary>
public static class ResolvedProviderFingerprint
{
    /// <summary>The placeholder for a capability that resolved to nothing at all.</summary>
    private const string Unresolved = "-";

    /// <summary>
    /// The capabilities a workflow step can reach. <c>Embedding</c> is deliberately absent: it is a
    /// file-search capability nothing in the step pipeline resolves.
    /// </summary>
    /// <remarks>
    /// <see cref="InferenceProviderCapability.Segmentation"/> and
    /// <see cref="InferenceProviderCapability.Depth"/> joined with the subject-detection arm: the
    /// editor resolves a segmenter to cut a subject out onto a layer, so repointing the default
    /// segmenter must invalidate a cached layer result exactly as repointing the image provider does.
    /// <para>
    /// <see cref="InferenceProviderCapability.VideoSegmentation"/> joined with the prompted-region-effects
    /// contracts: a <c>VideoAnalyze</c> step can now resolve a hosted video segmenter, so repointing the
    /// default <c>VideoSegmentation</c> row must invalidate a cached analysis exactly as repointing the
    /// vision or transcription provider does. It is fingerprinted from the contracts ticket onwards even
    /// though the capability's resolution lands in a later ticket — an unresolved capability is
    /// fingerprinted as unresolved (<c>-</c>), which is truthful, and the alternative (leaving it out
    /// until the resolver exists) is the under-inclusion this class exists to prevent.
    /// <para>
    /// <see cref="InferenceProviderCapability.ImageGeneration"/> and
    /// <see cref="InferenceProviderCapability.ImageEdit"/> joined when the image arm landed
    /// (<c>ThumbnailGenerate</c> can be configured to seed a cover from a model, and the editor's
    /// subject/layer operations call the edit capability). Leaving them out would be the
    /// under-inclusion this class's own remarks call a correctness bug: repointing the default image
    /// provider would leave a cached thumbnail or layer result replaying output produced by the old
    /// one.
    /// <para>
    /// <see cref="InferenceProviderCapability.AudioIsolation"/> joined with the audio-cleanup vendor layer
    /// (SchemaVersion v10): a later audio-cleanup step resolves a hosted isolator, so repointing the default
    /// isolation row must invalidate a cached result.
    /// </para>
    /// <para>
    /// <see cref="InferenceProviderCapability.VideoUpscale"/> and
    /// <see cref="InferenceProviderCapability.FrameInterpolation"/> joined with the video-enhance vendor layer
    /// (SchemaVersion v11; v10 is the audio-cleanup initiative's): a later enhance step resolves a hosted upscaler or
    /// interpolator, so repointing either default row must invalidate a cached result.
    /// </para>
    /// <para>
    /// <see cref="InferenceProviderCapability.VideoOutpaint"/> joined with the Extend op (SchemaVersion v12): the
    /// fingerprint gains a line for every step, so every cached key changes and the version is bumped to say so.
    /// </para>
    /// <para>
    /// <b>This list is the reason a change here is not free.</b>
    /// <c>ResolvedProviderFingerprintTests</c> asserts the exact capability set, so adding a
    /// capability to the enum without adding it here fails the suite — deliberately, and in the same
    /// commit, per the checklist <c>docs/thumbnails-and-image-editing.md</c> lays out.
    /// </para>
    /// </remarks>
    private static readonly InferenceProviderCapability[] StepCapabilities =
    [
        InferenceProviderCapability.Chat,
        InferenceProviderCapability.Transcription,
        InferenceProviderCapability.Vision,
        InferenceProviderCapability.Decision,
        InferenceProviderCapability.VideoGeneration,
        InferenceProviderCapability.SpeechSynthesis,
        InferenceProviderCapability.ImageGeneration,
        InferenceProviderCapability.ImageEdit,
        InferenceProviderCapability.Segmentation,
        InferenceProviderCapability.Depth,
        InferenceProviderCapability.VideoSegmentation,
        InferenceProviderCapability.AudioIsolation,
        InferenceProviderCapability.VideoUpscale,
        InferenceProviderCapability.FrameInterpolation,
        InferenceProviderCapability.VideoOutpaint,
        InferenceProviderCapability.LipSync
    ];

    /// <summary>
    /// The fingerprint for one step. <paramref name="explicitReferences"/> is that step's own
    /// provider references (<see cref="StepProviderReferences.Extract"/>).
    /// </summary>
    public static Task<string> ComputeAsync(
        IInferenceProviderResolver resolver,
        AgentType agentType,
        Guid? agentDefinitionId,
        IReadOnlyList<StepProviderReference> explicitReferences,
        CancellationToken ct)
        => ComputeAsync(resolver, agentType, agentDefinitionId, appChatProviderId: null, explicitReferences, ct);

    /// <summary>
    /// The fingerprint for one step whose agent may run on an app-chosen Chat model (docs/design/app-inputs.md 8.4):
    /// <paramref name="appChatProviderId"/> is that choice. The Chat line then states the chosen model (resolved by id, fail
    /// closed: one that no longer resolves states the agent's ordinary model, which is what the run then uses), so a
    /// different choice is a different key through the existing line. Its MEANING is unchanged (what the step's chat
    /// resolves to), so no <c>StepCacheKeyInputs.SchemaVersion</c> bump: with no choice the lines are exactly as before.
    /// </summary>
    public static async Task<string> ComputeAsync(
        IInferenceProviderResolver resolver,
        AgentType agentType,
        Guid? agentDefinitionId,
        Guid? appChatProviderId,
        IReadOnlyList<StepProviderReference> explicitReferences,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        explicitReferences ??= [];

        List<string> lines = [];

        foreach (InferenceProviderCapability capability in StepCapabilities)
        {
            // The step's own id for this capability, when its config names one — NULL, never
            // Guid.Empty, when it names none. That distinction matters: Guid.Empty is not a
            // "no reference" sentinel to the resolver, it is an id that is not in the snapshot, so
            // passing it would log a spurious "not visible to this organization" warning and bump
            // the foreign-provider counter for every step in every run.
            //
            // First-wins: the per-reference loop below adds every explicit id anyway, so a
            // capability named twice (VideoAnalyze's visionProviderId/objectVisionProviderId)
            // fingerprints both.
            Guid? explicitId = explicitReferences
                .Where(reference => reference.Capability == capability)
                .Select(reference => (Guid?)reference.ProviderId)
                .FirstOrDefault();

            if (capability == InferenceProviderCapability.Chat && appChatProviderId is Guid chosenId
                && await resolver.ResolveChatAsync(chosenId, ct) is { } chosen)
            {
                lines.Add(Line(capability, chosen.ProviderId, chosen.ModelName));
                continue;
            }

            lines.Add(await ResolveLineAsync(resolver, capability, explicitId, agentType, agentDefinitionId, ct));
        }

        foreach (StepProviderReference reference in explicitReferences)
        {
            lines.Add(await ResolveLineAsync(resolver, reference.Capability, reference.ProviderId, agentType, agentDefinitionId, ct));
        }

        lines.Sort(StringComparer.Ordinal);
        List<string> distinct = [.. lines.Distinct(StringComparer.Ordinal)];

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', distinct)))).ToLowerInvariant();
    }

    /// <summary>
    /// One <c>{capability}:{providerId}:{model}</c> line for <paramref name="capability"/> resolved
    /// with <paramref name="explicitProviderId"/>. Every resolver here already fails closed — an id
    /// the resolving organization cannot see, a disabled row, or a capability mismatch is treated as
    /// missing and falls through to that capability's default — so the line states what the step will
    /// actually use. Chat is the one capability resolved by agent rather than by id
    /// (<see cref="IInferenceProviderResolver.ResolveAsync"/>), since no step config names a chat
    /// provider: an Agent step's comes from its <c>AgentDefinition</c>.
    /// </summary>
    private static async Task<string> ResolveLineAsync(
        IInferenceProviderResolver resolver,
        InferenceProviderCapability capability,
        Guid? explicitProviderId,
        AgentType agentType,
        Guid? agentDefinitionId,
        CancellationToken ct)
    {
        switch (capability)
        {
            case InferenceProviderCapability.Chat:
                ResolvedInferenceProvider chat = await resolver.ResolveAsync(agentType, agentDefinitionId, ct);
                return Line(capability, chat.ProviderId, chat.ModelName);

            case InferenceProviderCapability.Transcription:
                ResolvedTranscriptionProvider? transcription = await resolver.ResolveTranscriptionAsync(explicitProviderId, ct);
                return Line(capability, transcription?.ProviderId, transcription?.ModelName);

            case InferenceProviderCapability.Vision:
                ResolvedInferenceProvider? vision = await resolver.ResolveVisionAsync(explicitProviderId, ct);
                return Line(capability, vision?.ProviderId, vision?.ModelName);

            case InferenceProviderCapability.Decision:
                ResolvedDecisionProvider? decision = await resolver.ResolveDecisionAsync(explicitProviderId, ct);
                return Line(capability, decision?.ProviderId, decision?.ModelName);

            case InferenceProviderCapability.VideoGeneration:
                ResolvedVideoGenerationProvider? video = await resolver.ResolveVideoGenerationAsync(explicitProviderId, ct);
                return Line(capability, video?.ProviderId, video?.ModelName);

            case InferenceProviderCapability.SpeechSynthesis:
                ResolvedSpeechSynthesisProvider? speech = await resolver.ResolveSpeechSynthesisAsync(explicitProviderId, ct);
                return Line(capability, speech?.ProviderId, speech?.ModelName);

            case InferenceProviderCapability.ImageGeneration:
                ResolvedImageProvider? imageGeneration = await resolver.ResolveImageGenerationAsync(explicitProviderId, ct);
                return Line(capability, imageGeneration?.ProviderId, imageGeneration?.ModelName);

            case InferenceProviderCapability.ImageEdit:
                ResolvedImageProvider? imageEdit = await resolver.ResolveImageEditAsync(explicitProviderId, ct);
                return Line(capability, imageEdit?.ProviderId, imageEdit?.ModelName);

            case InferenceProviderCapability.Segmentation:
                ResolvedImageAnalysisProvider? segmentation = await resolver.ResolveSegmentationAsync(explicitProviderId, ct);
                return Line(capability, segmentation?.ProviderId, segmentation?.ModelName);

            case InferenceProviderCapability.Depth:
                ResolvedImageAnalysisProvider? depth = await resolver.ResolveDepthAsync(explicitProviderId, ct);
                return Line(capability, depth?.ProviderId, depth?.ModelName);

            case InferenceProviderCapability.VideoSegmentation:
                ResolvedImageAnalysisProvider? videoSegmentation = await resolver.ResolveVideoSegmentationAsync(explicitProviderId, ct);
                return Line(capability, videoSegmentation?.ProviderId, videoSegmentation?.ModelName);

            // T-108: reached only through an AudioGenerate step's explicit providerId (the two capabilities are not
            // in StepCapabilities: AudioGenerate is not step-cached, and adding them would move every step's key).
            case InferenceProviderCapability.MusicGeneration:
                ResolvedAudioGenerationProvider? music = await resolver.ResolveMusicGenerationAsync(explicitProviderId, ct);
                return Line(capability, music?.ProviderId, music?.ModelName);

            case InferenceProviderCapability.SoundEffectGeneration:
                ResolvedAudioGenerationProvider? sfx = await resolver.ResolveSoundEffectGenerationAsync(explicitProviderId, ct);
                return Line(capability, sfx?.ProviderId, sfx?.ModelName);

            // Audio cleanup (hosted voice isolation). Part of StepCapabilities, so it is on every step's line set;
            // an unresolved capability is "-", and adding the line is what took StepCacheKeyInputs.SchemaVersion to v10.
            case InferenceProviderCapability.AudioIsolation:
                ResolvedAudioGenerationProvider? isolation = await resolver.ResolveAudioIsolationAsync(explicitProviderId, ct);
                return Line(capability, isolation?.ProviderId, isolation?.ModelName);

            // Video enhancement (hosted upscale / frame generation). Part of StepCapabilities, so it is on every step's
            // line set; an unresolved capability is "-", and adding the two lines is what took SchemaVersion to v11.
            case InferenceProviderCapability.VideoUpscale:
                ResolvedAudioGenerationProvider? upscale = await resolver.ResolveVideoUpscaleAsync(explicitProviderId, ct);
                return Line(capability, upscale?.ProviderId, upscale?.ModelName);

            case InferenceProviderCapability.FrameInterpolation:
                ResolvedAudioGenerationProvider? interpolation = await resolver.ResolveFrameInterpolationAsync(explicitProviderId, ct);
                return Line(capability, interpolation?.ProviderId, interpolation?.ModelName);

            case InferenceProviderCapability.VideoOutpaint:
                ResolvedAudioGenerationProvider? outpaint = await resolver.ResolveVideoOutpaintAsync(explicitProviderId, ct);
                return Line(capability, outpaint?.ProviderId, outpaint?.ModelName);

            // Talking-head lip-sync (SchemaVersion v13). Part of StepCapabilities, so it is on every step's line set; an
            // unresolved capability is "-".
            case InferenceProviderCapability.LipSync:
                ResolvedVideoGenerationProvider? lipSync = await resolver.ResolveLipSyncAsync(explicitProviderId, ct);
                return Line(capability, lipSync?.ProviderId, lipSync?.ModelName);

            default:
                return Line(capability, null, null);
        }
    }

    private static string Line(InferenceProviderCapability capability, Guid? providerId, string? modelName) =>
        $"{capability}:{providerId?.ToString() ?? Unresolved}:{(string.IsNullOrEmpty(modelName) ? Unresolved : modelName)}";
}
