using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.Caching;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// WP A10, resolve half (R10): the step-cache key carries a fingerprint of the providers a step
/// actually RESOLVES to, so repointing an organization's provider invalidates cached output — and a
/// reference to a provider the resolving organization cannot see is fingerprinted as the default it
/// fell through to, never as the foreign row.
/// </summary>
/// <remarks>
/// Built on the REAL <see cref="InferenceProviderResolver"/> over a fake store rather than on a stub
/// resolver, so the org scoping and the fail-closed handling of a foreign id are the production
/// ones: a stub that "returns the id it was given" would pass every assertion here while shipping
/// the bug this WP exists to prevent.
/// </remarks>
public class ResolvedProviderFingerprintTests
{
    private static readonly Guid OrgA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid OrgB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    [Fact]
    public async Task The_fingerprint_changes_when_the_resolved_provider_changes()
    {
        ModelCapabilityView first = Provider("org-a-default", isDefault: true, OrgA);
        ModelCapabilityView second = Provider("org-a-other", isDefault: false, OrgA);

        string withFirst = await FingerprintAsync(
            new[] { first, second }, new MutableTenant(OrgA), AnalyzeStep(transcriptionProviderId: null));

        string withSecond = await FingerprintAsync(
            new[] { first, second }, new MutableTenant(OrgA), AnalyzeStep(transcriptionProviderId: second.Id));

        withSecond.Should().NotBe(withFirst,
            "pinning a different Transcription provider must change the key, or the cached output of the old one is replayed");
    }

    [Fact]
    public async Task The_fingerprint_changes_when_the_organizations_default_changes_even_though_no_workflow_was_edited()
    {
        // This is the case the component exists for: nothing about the workflow changed, an operator
        // repointed the organization's default row, and the cached step must no longer be served.
        ModelCapabilityView oldDefault = Provider("org-a-old", isDefault: true, OrgA);
        ModelCapabilityView newDefault = Provider("org-a-new", isDefault: true, OrgA);

        string before = await FingerprintAsync(new[] { oldDefault }, new MutableTenant(OrgA), AnalyzeStep(null));
        string after = await FingerprintAsync(new[] { newDefault }, new MutableTenant(OrgA), AnalyzeStep(null));

        after.Should().NotBe(before);
    }

    [Fact]
    public async Task The_fingerprint_changes_when_the_resolved_model_changes_on_the_same_row()
    {
        // Same row id, different model: a different backend answers, so the cached output is stale.
        ModelCapabilityView before = Provider("org-a", isDefault: true, OrgA, model: "model-1");
        ModelCapabilityView after = Provider("org-a", isDefault: true, OrgA, model: "model-2");
        after.Id = before.Id;

        string first = await FingerprintAsync(new[] { before }, new MutableTenant(OrgA), AnalyzeStep(null));
        string second = await FingerprintAsync(new[] { after }, new MutableTenant(OrgA), AnalyzeStep(null));

        second.Should().NotBe(first);
    }

    [Fact]
    public async Task Another_organizations_provider_id_is_refused_at_resolve_and_fingerprints_as_the_default()
    {
        // The resolve-time half of A10's goal. The resolver treats an id outside the resolving
        // organization's snapshot as MISSING (and counts
        // reelbolt.inference.foreign_provider_ref), so the step falls through to the platform
        // default — and that is what the fingerprint records. Anything else would either serve the
        // foreign provider or produce a key that differs from the identical fallback case.
        ModelCapabilityView platformDefault = Provider("platform-transcription", isDefault: true, organizationId: null);
        ModelCapabilityView orgBTranscription = Provider("org-b-transcription", isDefault: false, OrgB);

        string namingTheForeignRow = await FingerprintAsync(
            new[] { platformDefault, orgBTranscription }, new MutableTenant(OrgA), AnalyzeStep(orgBTranscription.Id));

        string namingNothing = await FingerprintAsync(
            new[] { platformDefault, orgBTranscription }, new MutableTenant(OrgA), AnalyzeStep(null));

        namingTheForeignRow.Should().Be(namingNothing,
            "a foreign id resolves to the default, so it is the same work and the same cache entry");
    }

    [Fact]
    public async Task A_visible_explicit_provider_fingerprints_differently_from_the_default()
    {
        // The other half: when the id IS usable, the fingerprint must record THAT provider — a
        // fingerprint that ignored the explicit reference would serve the default's output to a step
        // pinned elsewhere.
        ModelCapabilityView platformDefault = Provider("platform-transcription", isDefault: true, organizationId: null);
        ModelCapabilityView orgAExplicit = Provider("org-a-explicit", isDefault: false, OrgA);

        string pinned = await FingerprintAsync(
            new[] { platformDefault, orgAExplicit }, new MutableTenant(OrgA), AnalyzeStep(orgAExplicit.Id));

        string @default = await FingerprintAsync(
            new[] { platformDefault, orgAExplicit }, new MutableTenant(OrgA), AnalyzeStep(null));

        pinned.Should().NotBe(@default);
    }

    [Fact]
    public async Task A_changed_chat_provider_changes_the_fingerprint_of_an_agent_step_with_no_config_reference()
    {
        // An Agent step names no provider in its config at all: its chat provider comes from the
        // resolver, which is exactly why the chat resolution is fingerprinted unconditionally.
        ModelCapabilityView chatDefault = Provider("org-a-chat", isDefault: true, OrgA, capability: InferenceProviderCapability.Chat);

        string before = await FingerprintAsync(new[] { chatDefault }, new MutableTenant(OrgA), []);

        ModelCapabilityView replacement = Provider("org-a-chat-2", isDefault: true, OrgA, capability: InferenceProviderCapability.Chat);
        string after = await FingerprintAsync(new[] { replacement }, new MutableTenant(OrgA), []);

        after.Should().NotBe(before);
    }

    [Fact]
    public async Task A_workspace_agent_model_choice_changes_the_fingerprint_so_a_cached_run_is_not_replayed()
    {
        // agent-model-choice: the workspace picks the model for a built-in agent in organization_agent_provider_overrides.
        // Nothing about the workflow changes, but the agent now runs on another model, so the cached step must go.
        ModelCapabilityView chatDefault = Provider("platform-default", isDefault: true, null, capability: InferenceProviderCapability.Chat, model: "default-m");
        ModelCapabilityView picked = Provider("platform-picked", isDefault: false, null, capability: InferenceProviderCapability.Chat, model: "picked-m");
        ModelCapabilityView[] providers = [chatDefault, picked];
        Dictionary<Guid, IReadOnlyDictionary<AgentType, Guid?>> choice = new()
        {
            [OrgA] = new Dictionary<AgentType, Guid?> { [AgentType.VideoStoryEditor] = picked.Id }
        };

        string before = await FingerprintAsync(providers, new MutableTenant(OrgA), []);
        string after = await FingerprintAsync(providers, new MutableTenant(OrgA), [], choice);
        string otherOrg = await FingerprintAsync(providers, new MutableTenant(OrgB), [], choice);

        after.Should().NotBe(before, "the org override moved the agent to another model");
        otherOrg.Should().Be(before, "another workspace's choice never reaches this one");
    }

    [Fact]
    public async Task A_step_that_names_no_provider_asks_every_capability_for_the_default()
    {
        // Regression: the "this capability's explicit id" lookup returned default(StepProviderReference)
        // — a NON-NULL Guid.Empty — when the step named nothing, so every capability was resolved with
        // Guid.Empty rather than null. The resolution still landed on the default (Guid.Empty is simply
        // absent from the snapshot), which is why only the resolver's diagnostics showed it: a spurious
        // "not visible to this organization" warning and a reelbolt.inference.foreign_provider_ref
        // increment for every capability of every step of every run.
        RecordingResolver recorder = new();

        await ResolvedProviderFingerprint.ComputeAsync(
            recorder, AgentType.VideoStoryEditor, agentDefinitionId: null, AnalyzeStep(null), CancellationToken.None);

        recorder.RequestedIds.Should().OnlyContain(id => id == null,
            "a step with no explicit reference must ask for the capability default, never Guid.Empty");

        recorder.CapabilitiesRequested.Should().BeEquivalentTo(
            new[]
            {
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
            },
            "every capability a step can reach is fingerprinted, so an unrelated default change still invalidates the key");

        // ...and an explicit reference IS passed through, so the fix did not simply drop every id.
        recorder.RequestedIds.Clear();
        ModelCapabilityView explicitProvider = Provider("org-a", isDefault: false, OrgA);
        await ResolvedProviderFingerprint.ComputeAsync(
            recorder,
            AgentType.VideoStoryEditor,
            agentDefinitionId: null,
            AnalyzeStep(explicitProvider.Id),
            CancellationToken.None);

        recorder.RequestedIds.Should().Contain(explicitProvider.Id);
    }

    [Fact]
    public async Task The_fingerprint_is_stable_for_unchanged_inputs()
    {
        ModelCapabilityView provider = Provider("org-a", isDefault: true, OrgA);

        string first = await FingerprintAsync(new[] { provider }, new MutableTenant(OrgA), AnalyzeStep(provider.Id));
        string second = await FingerprintAsync(new[] { provider }, new MutableTenant(OrgA), AnalyzeStep(provider.Id));

        second.Should().Be(first);
        first.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    /// <summary>
    /// docs/design/model-catalog.md §5.2: the catalog split keeps every old provider row id as its model
    /// capability's id and the model name as the model code, so a migrated Chat capability fingerprints to
    /// the byte-identical line it produced as a provider row, and step-cache keys survive the upgrade
    /// (<c>StepCacheKeyInputs.SchemaVersion</c> stays <c>v5</c>). The catalog is laid out exactly as the
    /// <c>AddModelCatalog</c> migration leaves it (account and capability keep the old id, the model id is
    /// <c>md5('model:' || id)</c>) and read through the REAL engine store, not a fake.
    /// </summary>
    [Fact]
    public async Task Fingerprint_MigratedCapability_IsByteIdentical()
    {
        Guid oldId = Guid.Parse("3f1e2d4c-5b6a-4789-8a9b-0c1d2e3f4a5b");
        const string modelName = "claude-sonnet-4-5";
        Guid modelId = new(Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes("model:" + oldId))));

        using var db = new ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext(
            new DbContextOptionsBuilder<ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext>()
                .UseInMemoryDatabase($"fingerprint-migrated-{Guid.NewGuid():N}")
                .Options);
        db.AddFlat(new ModelCapabilityView
        {
            Id = oldId,
            ProviderId = oldId,
            ModelId = modelId,
            Name = "Anthropic",
            Kind = InferenceProviderKind.Anthropic,
            Capability = InferenceProviderCapability.Chat,
            Endpoint = "https://api.anthropic.com",
            ModelName = modelName,
            ApiKeyEncrypted = "cipher",
            IsDefault = true,
            IsEnabled = true
        });
        await db.SaveChangesAsync();

        ServiceCollection services = new();
        services.AddSingleton<IInferenceProviderStore>(new ReelBolt.WorkflowEngine.Services.Inference.WorkflowEngineProviderStore(db));
        InferenceProviderResolver resolver = new(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            new PassthroughSecretProtector(),
            NullLogger<InferenceProviderResolver>.Instance,
            new MutableTenant(null));

        ResolvedInferenceProvider chat = await resolver.ResolveAsync(AgentType.VideoStoryEditor, null, CancellationToken.None);
        string chatLine = $"Chat:{chat.ProviderId}:{chat.ModelName}";
        chatLine.Should().Be($"Chat:{oldId}:{modelName}", "the capability kept the provider row's id and model name");
        (chat.AccountId, chat.ModelId).Should().Be(((Guid?)oldId, (Guid?)modelId));

        // Every other step capability is unresolved here; the hash is over the sorted, distinct lines.
        string[] lines =
        [
            chatLine, "AudioIsolation:-:-", "Decision:-:-", "Depth:-:-", "FrameInterpolation:-:-", "ImageEdit:-:-", "ImageGeneration:-:-", "LipSync:-:-",
            "Segmentation:-:-", "SpeechSynthesis:-:-", "Transcription:-:-", "VideoGeneration:-:-", "VideoSegmentation:-:-",
            "VideoOutpaint:-:-", "VideoUpscale:-:-", "Vision:-:-"
        ];
        Array.Sort(lines, StringComparer.Ordinal);
        string expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines)))).ToLowerInvariant();

        string fingerprint = await ResolvedProviderFingerprint.ComputeAsync(
            resolver, AgentType.VideoStoryEditor, agentDefinitionId: null, [], CancellationToken.None);

        fingerprint.Should().Be(expected);
    }

    [Fact]
    public void The_shared_extractor_finds_the_provider_fields_of_every_referencing_step_type()
    {
        // StepProviderReferences is the one description of "which config field of which step type
        // names a provider", used by both the save check and this fingerprint. If a field stops being
        // extracted, both halves silently stop protecting it — so the extraction itself is asserted.
        Guid id = Guid.NewGuid();
        string quoted = id.ToString();

        StepProviderReferences.Extract(
            StepType.VideoAnalyze,
            videoAnalyzeConfigJson: $$"""{"transcriptionProviderId":"{{quoted}}","visionProviderId":"{{quoted}}","objectVisionProviderId":"{{quoted}}"}""")
            .Should().HaveCount(3)
            .And.OnlyContain(r => r.ProviderId == id && r.Capability == InferenceProviderCapability.Transcription
                || r.Capability == InferenceProviderCapability.Vision);

        StepProviderReferences.Extract(StepType.Voiceover, voiceoverConfigJson: $$"""{"providerId":"{{quoted}}"}""")
            .Should().ContainSingle().Which.Capability.Should().Be(InferenceProviderCapability.SpeechSynthesis);
        StepProviderReferences.Extract(StepType.Conditional, conditionalConfigJson: $$"""{"providerId":"{{quoted}}"}""")
            .Should().ContainSingle().Which.Capability.Should().Be(InferenceProviderCapability.Decision);
        StepProviderReferences.Extract(StepType.ReviewLoop, reviewLoopConfigJson: $$"""{"providerId":"{{quoted}}"}""")
            .Should().ContainSingle().Which.Capability.Should().Be(InferenceProviderCapability.Decision);
        StepProviderReferences.Extract(StepType.VideoGenerate, videoGenerateConfigJson: $$"""{"providerId":"{{quoted}}"}""")
            .Should().ContainSingle().Which.Capability.Should().Be(InferenceProviderCapability.VideoGeneration);
        StepProviderReferences.Extract(StepType.EditRoom, editRoomConfigJson: $$"""{"conveneProviderId":"{{quoted}}"}""")
            .Should().ContainSingle().Which.Capability.Should().Be(InferenceProviderCapability.Decision);
        StepProviderReferences.Extract(StepType.GraphicsRoom, graphicsRoomConfigJson: $$"""{"conveneProviderId":"{{quoted}}"}""")
            .Should().ContainSingle().Which.Capability.Should().Be(InferenceProviderCapability.Decision);
        StepProviderReferences.Extract(StepType.ColorGradeRoom, colorGradeRoomConfigJson: $$"""{"conveneProviderId":"{{quoted}}"}""")
            .Should().ContainSingle().Which.Capability.Should().Be(InferenceProviderCapability.Decision);

        // An Agent step's provider is its AgentDefinition's, not any config field; a config that
        // names nothing and a config that will not parse both yield no references rather than
        // throwing (a malformed config is the save validator's and the executor's business).
        StepProviderReferences.Extract(StepType.Agent).Should().BeEmpty();
        StepProviderReferences.Extract(StepType.Conditional, conditionalConfigJson: "not json").Should().BeEmpty();
        StepProviderReferences.Extract(StepType.Voiceover, voiceoverConfigJson: """{"providerId":null}""").Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------------------

    private sealed class MutableTenant(Guid? org) : InferenceTenantAccessorBase
    {
        private Guid? _org = org;
        protected override Guid? AmbientOrganizationId => _org;
    }

    private static IReadOnlyList<StepProviderReference> AnalyzeStep(Guid? transcriptionProviderId) =>
        StepProviderReferences.Extract(
            StepType.VideoAnalyze,
            videoAnalyzeConfigJson: transcriptionProviderId is { } id
                ? $$"""{"version":1,"source":{"kind":"ProjectFile"},"transcriptionProviderId":"{{id}}"}"""
                : """{"version":1,"source":{"kind":"ProjectFile"}}""");

    private static async Task<string> FingerprintAsync(
        IReadOnlyList<ModelCapabilityView> providers,
        IInferenceTenantAccessor tenant,
        IReadOnlyList<StepProviderReference> references,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<AgentType, Guid?>>? orgOverrides = null)
    {
        InferenceProviderResolver resolver = CreateResolver(providers, tenant, orgOverrides);

        return await ResolvedProviderFingerprint.ComputeAsync(
            resolver, AgentType.VideoStoryEditor, agentDefinitionId: null, references, CancellationToken.None);
    }

    private static ModelCapabilityView Provider(
        string name,
        bool isDefault,
        Guid? organizationId,
        InferenceProviderCapability capability = InferenceProviderCapability.Transcription,
        string model = "m1") => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Kind = InferenceProviderKind.AzureOpenAI,
            Capability = capability,
            OrganizationId = organizationId,
            Endpoint = "https://provider.example",
            ModelName = model,
            IsDefault = isDefault,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static InferenceProviderResolver CreateResolver(
        IReadOnlyList<ModelCapabilityView> providers, IInferenceTenantAccessor tenant,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<AgentType, Guid?>>? orgOverrides = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<IInferenceProviderStore>(new FakeProviderStore(providers, orgOverrides));
        ServiceProvider serviceProvider = services.BuildServiceProvider();

        return new InferenceProviderResolver(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            new PassthroughSecretProtector(),
            NullLogger<InferenceProviderResolver>.Instance,
            tenant);
    }

    private sealed class FakeProviderStore(
        IReadOnlyList<ModelCapabilityView> providers,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<AgentType, Guid?>>? orgOverrides = null) : IInferenceProviderStore
    {
        public Task<IReadOnlyList<ModelCapabilityView>> LoadEnabledAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelCapabilityView>>(providers
                .Where(p => p.OrganizationId is null || (organizationId is not null && p.OrganizationId == organizationId))
                .ToList());

        public Task<IReadOnlyDictionary<Guid, Guid?>> LoadAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, Guid?>>(new Dictionary<Guid, Guid?>());

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadOrgAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<AgentType, Guid?>>(
                organizationId is { } org && orgOverrides is not null && orgOverrides.TryGetValue(org, out IReadOnlyDictionary<AgentType, Guid?>? mine)
                    ? mine
                    : new Dictionary<AgentType, Guid?>());

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadBuiltInAgentOverridesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<AgentType, Guid?>>(new Dictionary<AgentType, Guid?>());
    }

    /// <summary>
    /// Records what the fingerprint asked each capability to resolve, and answers "nothing" (except
    /// for chat, whose resolver signature is non-nullable). Used only to observe the ARGUMENTS — the
    /// resolution behaviour itself is asserted against the real resolver above, never against this.
    /// </summary>
    private sealed class RecordingResolver : IInferenceProviderResolver
    {
        public List<Guid?> RequestedIds { get; } = [];
        public List<InferenceProviderCapability> CapabilitiesRequested { get; } = [];

        public Task<ResolvedInferenceProvider> ResolveAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct)
        {
            // Chat is resolved by AGENT rather than by id — no step config names a chat provider —
            // but it is still one of the capabilities the fingerprint covers.
            Record(InferenceProviderCapability.Chat, null);
            return Task.FromResult(new ResolvedInferenceProvider(null, "chat", InferenceProviderKind.AzureOpenAI, "https://x", "m", string.Empty, 300));
        }

        public Task<ResolvedTranscriptionProvider?> ResolveTranscriptionAsync(Guid? explicitProviderId, CancellationToken ct)
        {
            Record(InferenceProviderCapability.Transcription, explicitProviderId);
            return Task.FromResult<ResolvedTranscriptionProvider?>(null);
        }

        public Task<ResolvedInferenceProvider?> ResolveVisionAsync(Guid? explicitProviderId, CancellationToken ct)
        {
            Record(InferenceProviderCapability.Vision, explicitProviderId);
            return Task.FromResult<ResolvedInferenceProvider?>(null);
        }

        public Task<ResolvedDecisionProvider?> ResolveDecisionAsync(Guid? explicitProviderId, CancellationToken ct)
        {
            Record(InferenceProviderCapability.Decision, explicitProviderId);
            return Task.FromResult<ResolvedDecisionProvider?>(null);
        }

        public Task<ResolvedVideoGenerationProvider?> ResolveVideoGenerationAsync(Guid? explicitProviderId, CancellationToken ct)
        {
            Record(InferenceProviderCapability.VideoGeneration, explicitProviderId);
            return Task.FromResult<ResolvedVideoGenerationProvider?>(null);
        }

        public Task<ResolvedSpeechSynthesisProvider?> ResolveSpeechSynthesisAsync(Guid? explicitProviderId, CancellationToken ct)
        {
            Record(InferenceProviderCapability.SpeechSynthesis, explicitProviderId);
            return Task.FromResult<ResolvedSpeechSynthesisProvider?>(null);
        }

        public Task<ResolvedImageAnalysisProvider?> ResolveVideoSegmentationAsync(Guid? explicitProviderId, CancellationToken ct)
        {
            // The interface's own default answers null; this override exists only so the capability is
            // OBSERVED here — the fingerprint must ask for it, or a shared video segmenter repointed
            // between two runs would keep replaying the first run's cached analysis.
            Record(InferenceProviderCapability.VideoSegmentation, explicitProviderId);
            return Task.FromResult<ResolvedImageAnalysisProvider?>(null);
        }

        public Task<ResolvedImageProvider?> ResolveImageGenerationAsync(Guid? explicitProviderId, CancellationToken ct)
        {
            Record(InferenceProviderCapability.ImageGeneration, explicitProviderId);
            return Task.FromResult<ResolvedImageProvider?>(null);
        }

        public Task<ResolvedImageProvider?> ResolveImageEditAsync(Guid? explicitProviderId, CancellationToken ct)
        {
            Record(InferenceProviderCapability.ImageEdit, explicitProviderId);
            return Task.FromResult<ResolvedImageProvider?>(null);
        }

        public Task<ResolvedImageProvider?> ResolveImageJobAsync(
            InferenceProviderCapability capability, Guid? explicitProviderId, CancellationToken ct)
        {
            Record(capability, explicitProviderId);
            return Task.FromResult<ResolvedImageProvider?>(null);
        }

        public Task<ResolvedImageAnalysisProvider?> ResolveSegmentationAsync(Guid? explicitProviderId, CancellationToken ct)
        {
            Record(InferenceProviderCapability.Segmentation, explicitProviderId);
            return Task.FromResult<ResolvedImageAnalysisProvider?>(null);
        }

        public Task<ResolvedImageAnalysisProvider?> ResolveDepthAsync(Guid? explicitProviderId, CancellationToken ct)
        {
            Record(InferenceProviderCapability.Depth, explicitProviderId);
            return Task.FromResult<ResolvedImageAnalysisProvider?>(null);
        }

        public Task<ResolvedAudioGenerationProvider?> ResolveMusicGenerationAsync(Guid? explicitProviderId, CancellationToken ct)
        {
            Record(InferenceProviderCapability.MusicGeneration, explicitProviderId);
            return Task.FromResult<ResolvedAudioGenerationProvider?>(null);
        }

        public Task<ResolvedAudioGenerationProvider?> ResolveSoundEffectGenerationAsync(Guid? explicitProviderId, CancellationToken ct)
        {
            Record(InferenceProviderCapability.SoundEffectGeneration, explicitProviderId);
            return Task.FromResult<ResolvedAudioGenerationProvider?>(null);
        }

        public Task<ResolvedAudioGenerationProvider?> ResolveAudioIsolationAsync(Guid? explicitProviderId, CancellationToken ct)
        {
            Record(InferenceProviderCapability.AudioIsolation, explicitProviderId);
            return Task.FromResult<ResolvedAudioGenerationProvider?>(null);
        }

        public Task<ResolvedAudioGenerationProvider?> ResolveVideoUpscaleAsync(Guid? explicitProviderId, CancellationToken ct)
        {
            Record(InferenceProviderCapability.VideoUpscale, explicitProviderId);
            return Task.FromResult<ResolvedAudioGenerationProvider?>(null);
        }

        public Task<ResolvedAudioGenerationProvider?> ResolveFrameInterpolationAsync(Guid? explicitProviderId, CancellationToken ct)
        {
            Record(InferenceProviderCapability.FrameInterpolation, explicitProviderId);
            return Task.FromResult<ResolvedAudioGenerationProvider?>(null);
        }

        public Task<ResolvedAudioGenerationProvider?> ResolveVideoOutpaintAsync(Guid? explicitProviderId, CancellationToken ct)
        {
            Record(InferenceProviderCapability.VideoOutpaint, explicitProviderId);
            return Task.FromResult<ResolvedAudioGenerationProvider?>(null);
        }

        public Task<ResolvedVideoGenerationProvider?> ResolveLipSyncAsync(Guid? explicitProviderId, CancellationToken ct)
        {
            Record(InferenceProviderCapability.LipSync, explicitProviderId);
            return Task.FromResult<ResolvedVideoGenerationProvider?>(null);
        }

        public Task<ResolvedInferenceProvider?> ResolveChatAsync(Guid explicitProviderId, CancellationToken ct) =>
            Task.FromResult<ResolvedInferenceProvider?>(null);

        public Task<ResolvedEmbeddingProvider?> ResolveEmbeddingAsync(Guid? explicitProviderId, CancellationToken ct) =>
            Task.FromResult<ResolvedEmbeddingProvider?>(null);

        public void Invalidate(Guid? organizationId)
        {
        }

        public void InvalidateAll()
        {
        }

        private void Record(InferenceProviderCapability capability, Guid? explicitProviderId)
        {
            RequestedIds.Add(explicitProviderId);
            if (!CapabilitiesRequested.Contains(capability))
                CapabilitiesRequested.Add(capability);
        }
    }

    private sealed class PassthroughSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public bool TryUnprotect(string? ciphertext, out string plaintext)
        {
            plaintext = ciphertext ?? string.Empty;
            return ciphertext != null;
        }
    }
}
