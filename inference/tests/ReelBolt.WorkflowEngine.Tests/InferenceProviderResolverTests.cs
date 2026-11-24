using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class InferenceProviderResolverTests
{
    [Fact]
    public async Task ResolveAsync_prefers_the_enabled_per_agent_override_over_the_default()
    {
        Guid agentId = Guid.NewGuid();
        ModelCapabilityView defaultProvider = MakeProvider(isDefault: true, name: "default");
        ModelCapabilityView overrideProvider = MakeProvider(isDefault: false, name: "override");

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { defaultProvider, overrideProvider },
            overrides: new Dictionary<Guid, Guid?> { [agentId] = overrideProvider.Id });

        ResolvedInferenceProvider resolved = await resolver.ResolveAsync(
            AgentType.CodeStructureAnalyzer, agentId, CancellationToken.None);

        resolved.ProviderId.Should().Be(overrideProvider.Id);
        resolved.Name.Should().Be("override");
    }

    [Fact]
    public async Task ResolveAsync_falls_back_to_the_global_default_when_there_is_no_override()
    {
        ModelCapabilityView defaultProvider = MakeProvider(isDefault: true, name: "default");

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { defaultProvider },
            overrides: new Dictionary<Guid, Guid?>());

        ResolvedInferenceProvider resolved = await resolver.ResolveAsync(
            AgentType.CodeStructureAnalyzer, agentDefinitionId: null, CancellationToken.None);

        resolved.ProviderId.Should().Be(defaultProvider.Id);
    }

    [Fact]
    public async Task ResolveAsync_falls_back_to_AzureOpenAI_config_keys_when_no_provider_rows_exist()
    {
        InferenceProviderResolver resolver = CreateResolver(
            providers: Array.Empty<ModelCapabilityView>(),
            overrides: new Dictionary<Guid, Guid?>(),
            configOverrides: new Dictionary<string, string?>
            {
                ["AzureOpenAI:Endpoint"] = "https://config-fallback.example",
                ["AzureOpenAI:ApiKey"] = "config-key",
                ["AzureOpenAI:DeploymentName"] = "config-deployment"
            });

        ResolvedInferenceProvider resolved = await resolver.ResolveAsync(
            AgentType.CodeStructureAnalyzer, agentDefinitionId: null, CancellationToken.None);

        resolved.ProviderId.Should().BeNull();
        resolved.Endpoint.Should().Be("https://config-fallback.example");
        resolved.ApiKey.Should().Be("config-key");
        resolved.ModelName.Should().Be("config-deployment");
    }

    [Fact]
    public async Task ResolveAsync_ignores_a_disabled_override_and_falls_through_to_the_default()
    {
        // A real store's LoadEnabledAsync filters to IsEnabled providers, so a disabled provider
        // referenced by an override is deliberately absent from the "enabled" list here -
        // mirroring what the resolver actually sees.
        Guid agentId = Guid.NewGuid();
        ModelCapabilityView defaultProvider = MakeProvider(isDefault: true, name: "default");
        Guid disabledProviderId = Guid.NewGuid();

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { defaultProvider },
            overrides: new Dictionary<Guid, Guid?> { [agentId] = disabledProviderId });

        ResolvedInferenceProvider resolved = await resolver.ResolveAsync(
            AgentType.CodeStructureAnalyzer, agentId, CancellationToken.None);

        resolved.ProviderId.Should().Be(defaultProvider.Id);
    }

    // --- Regression: a room step's seats and director carry no per-seat AgentDefinitionId, so
    // RoomStepExecutorBase asks for the agent TYPE with a null id. Before the built-in-by-type
    // fallback existed those turns silently ran on the global default provider, which is how an
    // edit room whose VideoStoryEditor was configured for one provider quietly ran every turn on
    // another. ---

    [Fact]
    public async Task ResolveAsync_uses_the_built_in_agent_type_override_when_no_definition_id_is_supplied()
    {
        ModelCapabilityView defaultProvider = MakeProvider(isDefault: true, name: "default");
        ModelCapabilityView overrideProvider = MakeProvider(isDefault: false, name: "override");

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { defaultProvider, overrideProvider },
            overrides: new Dictionary<Guid, Guid?>(),
            builtInOverrides: new Dictionary<AgentType, Guid?>
            {
                [AgentType.VideoStoryEditor] = overrideProvider.Id
            });

        ResolvedInferenceProvider resolved = await resolver.ResolveAsync(
            AgentType.VideoStoryEditor, agentDefinitionId: null, CancellationToken.None);

        resolved.ProviderId.Should().Be(overrideProvider.Id,
            "a room seat that names only its agent type must get the same provider a solo step of that agent would");
    }

    [Fact]
    public async Task ResolveAsync_built_in_type_override_only_applies_to_its_own_agent_type()
    {
        ModelCapabilityView defaultProvider = MakeProvider(isDefault: true, name: "default");
        ModelCapabilityView overrideProvider = MakeProvider(isDefault: false, name: "override");

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { defaultProvider, overrideProvider },
            overrides: new Dictionary<Guid, Guid?>(),
            builtInOverrides: new Dictionary<AgentType, Guid?>
            {
                [AgentType.VideoStoryEditor] = overrideProvider.Id
            });

        ResolvedInferenceProvider resolved = await resolver.ResolveAsync(
            AgentType.CodeStructureAnalyzer, agentDefinitionId: null, CancellationToken.None);

        resolved.ProviderId.Should().Be(defaultProvider.Id);
    }

    [Fact]
    public async Task ResolveAsync_prefers_an_explicit_definition_id_over_the_built_in_type_override()
    {
        // A per-seat AgentDefinitionId (or a solo step's WorkflowStep.AgentDefinitionId) is the
        // more specific statement of intent and must win over the built-in row for the same type.
        Guid seatDefinitionId = Guid.NewGuid();
        ModelCapabilityView defaultProvider = MakeProvider(isDefault: true, name: "default");
        ModelCapabilityView builtInProvider = MakeProvider(isDefault: false, name: "built-in");
        ModelCapabilityView seatProvider = MakeProvider(isDefault: false, name: "seat");

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { defaultProvider, builtInProvider, seatProvider },
            overrides: new Dictionary<Guid, Guid?> { [seatDefinitionId] = seatProvider.Id },
            builtInOverrides: new Dictionary<AgentType, Guid?>
            {
                [AgentType.VideoStoryEditor] = builtInProvider.Id
            });

        ResolvedInferenceProvider resolved = await resolver.ResolveAsync(
            AgentType.VideoStoryEditor, seatDefinitionId, CancellationToken.None);

        resolved.ProviderId.Should().Be(seatProvider.Id);
    }

    [Fact]
    public async Task ResolveAsync_ignores_a_built_in_type_override_pointing_at_a_non_chat_provider()
    {
        // Same defense in depth the by-id branch applies: a Transcription/Vision row reached
        // through the type fallback must never be handed to the chat path.
        ModelCapabilityView chatDefault = MakeProvider(isDefault: true, name: "chat-default");
        ModelCapabilityView visionProvider = MakeProvider(
            isDefault: false, name: "vision", capability: InferenceProviderCapability.Vision);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { chatDefault, visionProvider },
            overrides: new Dictionary<Guid, Guid?>(),
            builtInOverrides: new Dictionary<AgentType, Guid?>
            {
                [AgentType.VideoEditDirector] = visionProvider.Id
            });

        ResolvedInferenceProvider resolved = await resolver.ResolveAsync(
            AgentType.VideoEditDirector, agentDefinitionId: null, CancellationToken.None);

        resolved.ProviderId.Should().Be(chatDefault.Id);
    }

    // --- R4 regression: the (capability, is_default) split must never let a default row of
    // one capability answer a resolution for the other capability, regardless of which row
    // happens to appear first in the "enabled" list the store returns. ---

    [Fact]
    public async Task ResolveAsync_never_returns_the_transcription_default_when_a_chat_default_also_exists()
    {
        // Deliberately list the Transcription-capability default FIRST. The pre-fix resolver
        // logic was `enabled.FirstOrDefault(p => p.IsDefault)`, which is order-dependent and
        // would have returned this transcription row to the chat path - this ordering is what
        // makes the test genuinely exercise the bug rather than passing either way.
        ModelCapabilityView transcriptionDefault = MakeProvider(
            isDefault: true, name: "whisper-default", capability: InferenceProviderCapability.Transcription);
        ModelCapabilityView chatDefault = MakeProvider(
            isDefault: true, name: "chat-default", capability: InferenceProviderCapability.Chat);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { transcriptionDefault, chatDefault },
            overrides: new Dictionary<Guid, Guid?>());

        ResolvedInferenceProvider resolved = await resolver.ResolveAsync(
            AgentType.CodeStructureAnalyzer, agentDefinitionId: null, CancellationToken.None);

        resolved.ProviderId.Should().Be(chatDefault.Id);
        resolved.Name.Should().Be("chat-default");
    }

    [Fact]
    public async Task ResolveTranscriptionAsync_never_returns_the_chat_default_when_a_transcription_default_also_exists()
    {
        // Reverse of the above: list the Chat-capability default FIRST, so a naive
        // "first IsDefault row" lookup on the transcription path would wrongly return it.
        ModelCapabilityView chatDefault = MakeProvider(
            isDefault: true, name: "chat-default", capability: InferenceProviderCapability.Chat);
        ModelCapabilityView transcriptionDefault = MakeProvider(
            isDefault: true, name: "whisper-default", capability: InferenceProviderCapability.Transcription);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { chatDefault, transcriptionDefault },
            overrides: new Dictionary<Guid, Guid?>());

        ResolvedTranscriptionProvider? resolved = await resolver.ResolveTranscriptionAsync(
            explicitProviderId: null, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.ProviderId.Should().Be(transcriptionDefault.Id);
        resolved.Name.Should().Be("whisper-default");
    }

    [Fact]
    public async Task ResolveTranscriptionAsync_prefers_the_explicit_provider_id_over_the_default()
    {
        ModelCapabilityView defaultProvider = MakeProvider(
            isDefault: true, name: "whisper-default", capability: InferenceProviderCapability.Transcription);
        ModelCapabilityView explicitProvider = MakeProvider(
            isDefault: false, name: "whisper-explicit", capability: InferenceProviderCapability.Transcription);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { defaultProvider, explicitProvider },
            overrides: new Dictionary<Guid, Guid?>());

        ResolvedTranscriptionProvider? resolved = await resolver.ResolveTranscriptionAsync(
            explicitProviderId: explicitProvider.Id, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.ProviderId.Should().Be(explicitProvider.Id);
        resolved.Name.Should().Be("whisper-explicit");
    }

    [Fact]
    public async Task ResolveTranscriptionAsync_falls_back_to_the_default_when_the_explicit_id_is_not_found()
    {
        ModelCapabilityView defaultProvider = MakeProvider(
            isDefault: true, name: "whisper-default", capability: InferenceProviderCapability.Transcription);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { defaultProvider },
            overrides: new Dictionary<Guid, Guid?>());

        ResolvedTranscriptionProvider? resolved = await resolver.ResolveTranscriptionAsync(
            explicitProviderId: Guid.NewGuid(), CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.ProviderId.Should().Be(defaultProvider.Id);
    }

    [Fact]
    public async Task ResolveTranscriptionAsync_returns_null_when_nothing_resolves_even_with_config_fallback_available()
    {
        // Unlike ResolveAsync (chat), there is deliberately NO fallback to the legacy
        // AzureOpenAI:* configuration keys for transcription - those name a chat deployment, and
        // silently sending audio there would 404 confusingly. Prove this even when the config
        // keys ARE present and a chat default row exists, so a future "just reuse the chat
        // fallback" regression would fail this test.
        ModelCapabilityView chatDefault = MakeProvider(
            isDefault: true, name: "chat-default", capability: InferenceProviderCapability.Chat);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { chatDefault },
            overrides: new Dictionary<Guid, Guid?>(),
            configOverrides: new Dictionary<string, string?>
            {
                ["AzureOpenAI:Endpoint"] = "https://config-fallback.example",
                ["AzureOpenAI:ApiKey"] = "config-key",
                ["AzureOpenAI:DeploymentName"] = "config-deployment"
            });

        ResolvedTranscriptionProvider? resolved = await resolver.ResolveTranscriptionAsync(
            explicitProviderId: null, CancellationToken.None);

        resolved.Should().BeNull();
    }

    // --- Phase 2: Vision capability (mirrors the Transcription tests above exactly) ---

    [Fact]
    public async Task ResolveVisionAsync_prefers_the_explicit_provider_id_over_the_default()
    {
        ModelCapabilityView defaultProvider = MakeProvider(
            isDefault: true, name: "vision-default", capability: InferenceProviderCapability.Vision);
        ModelCapabilityView explicitProvider = MakeProvider(
            isDefault: false, name: "vision-explicit", capability: InferenceProviderCapability.Vision);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { defaultProvider, explicitProvider },
            overrides: new Dictionary<Guid, Guid?>());

        ResolvedInferenceProvider? resolved = await resolver.ResolveVisionAsync(
            explicitProviderId: explicitProvider.Id, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.ProviderId.Should().Be(explicitProvider.Id);
        resolved.Name.Should().Be("vision-explicit");
    }

    [Fact]
    public async Task ResolveVisionAsync_falls_back_to_the_default_when_no_explicit_id_is_given()
    {
        ModelCapabilityView defaultProvider = MakeProvider(
            isDefault: true, name: "vision-default", capability: InferenceProviderCapability.Vision);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { defaultProvider },
            overrides: new Dictionary<Guid, Guid?>());

        ResolvedInferenceProvider? resolved = await resolver.ResolveVisionAsync(
            explicitProviderId: null, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.ProviderId.Should().Be(defaultProvider.Id);
    }

    [Fact]
    public async Task ResolveVisionAsync_returns_null_when_nothing_resolves_even_with_config_fallback_available()
    {
        // Same rationale as ResolveTranscriptionAsync: no fallback to the legacy AzureOpenAI:*
        // configuration keys for vision either, even when a Chat default row also exists.
        ModelCapabilityView chatDefault = MakeProvider(
            isDefault: true, name: "chat-default", capability: InferenceProviderCapability.Chat);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { chatDefault },
            overrides: new Dictionary<Guid, Guid?>(),
            configOverrides: new Dictionary<string, string?>
            {
                ["AzureOpenAI:Endpoint"] = "https://config-fallback.example",
                ["AzureOpenAI:ApiKey"] = "config-key",
                ["AzureOpenAI:DeploymentName"] = "config-deployment"
            });

        ResolvedInferenceProvider? resolved = await resolver.ResolveVisionAsync(
            explicitProviderId: null, CancellationToken.None);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task ResolveVisionAsync_never_returns_a_Chat_provider_explicitly_passed_as_the_vision_id()
    {
        // Capability guards the inverse gap (mirrors ResolveTranscriptionAsync's equivalent
        // test): a Chat provider id authored directly into VideoAnalyzeStepConfig.VisionProviderId
        // (bypassing the UI, which only offers Vision rows) must fall through to the Vision
        // default (or null), never be sent through the vision/chat-image path as if it were
        // vision-capable by virtue of being a chat provider.
        ModelCapabilityView chatProvider = MakeProvider(
            isDefault: false, name: "chat-only", capability: InferenceProviderCapability.Chat);
        ModelCapabilityView visionDefault = MakeProvider(
            isDefault: true, name: "vision-default", capability: InferenceProviderCapability.Vision);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { chatProvider, visionDefault },
            overrides: new Dictionary<Guid, Guid?>());

        ResolvedInferenceProvider? resolved = await resolver.ResolveVisionAsync(
            explicitProviderId: chatProvider.Id, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.ProviderId.Should().Be(visionDefault.Id,
            "a Chat-capability id passed as the explicit vision provider must fall through to the Vision default");
    }

    [Fact]
    public async Task ResolveVisionAsync_and_ResolveAsync_defaults_are_fully_independent()
    {
        // R4-style regression, extended to the third capability: a Vision default and a Chat
        // default coexist independently and must never answer each other's resolution.
        ModelCapabilityView visionDefault = MakeProvider(
            isDefault: true, name: "vision-default", capability: InferenceProviderCapability.Vision);
        ModelCapabilityView chatDefault = MakeProvider(
            isDefault: true, name: "chat-default", capability: InferenceProviderCapability.Chat);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { visionDefault, chatDefault },
            overrides: new Dictionary<Guid, Guid?>());

        ResolvedInferenceProvider chatResolved = await resolver.ResolveAsync(
            AgentType.CodeStructureAnalyzer, agentDefinitionId: null, CancellationToken.None);
        ResolvedInferenceProvider? visionResolved = await resolver.ResolveVisionAsync(
            explicitProviderId: null, CancellationToken.None);

        chatResolved.ProviderId.Should().Be(chatDefault.Id);
        visionResolved.Should().NotBeNull();
        visionResolved!.ProviderId.Should().Be(visionDefault.Id);
    }

    // --- Phase 3: Decision capability (mirrors the Transcription and Vision tests above exactly) ---

    [Fact]
    public async Task ResolveDecisionAsync_returns_the_explicit_provider_when_its_capability_is_Decision()
    {
        ModelCapabilityView defaultProvider = MakeProvider(
            isDefault: true, name: "decision-default", capability: InferenceProviderCapability.Decision);
        ModelCapabilityView explicitProvider = MakeProvider(
            isDefault: false, name: "decision-explicit", capability: InferenceProviderCapability.Decision);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { defaultProvider, explicitProvider },
            overrides: new Dictionary<Guid, Guid?>());

        ResolvedDecisionProvider? resolved = await resolver.ResolveDecisionAsync(
            explicitProviderId: explicitProvider.Id, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.ProviderId.Should().Be(explicitProvider.Id);
        resolved.Name.Should().Be("decision-explicit");
    }

    [Fact]
    public async Task ResolveDecisionAsync_falls_back_to_the_default_Decision_provider_when_explicit_id_is_null()
    {
        ModelCapabilityView defaultProvider = MakeProvider(
            isDefault: true, name: "decision-default", capability: InferenceProviderCapability.Decision);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { defaultProvider },
            overrides: new Dictionary<Guid, Guid?>());

        ResolvedDecisionProvider? resolved = await resolver.ResolveDecisionAsync(
            explicitProviderId: null, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.ProviderId.Should().Be(defaultProvider.Id);
    }

    [Fact]
    public async Task ResolveDecisionAsync_ignores_a_Chat_provider_id_passed_as_explicit()
    {
        // Capability guard: a Chat provider id passed as the explicit decision provider must fall
        // through to the Decision default (or null), never be sent through the decision path.
        ModelCapabilityView chatProvider = MakeProvider(
            isDefault: false, name: "chat-only", capability: InferenceProviderCapability.Chat);
        ModelCapabilityView decisionDefault = MakeProvider(
            isDefault: true, name: "decision-default", capability: InferenceProviderCapability.Decision);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { chatProvider, decisionDefault },
            overrides: new Dictionary<Guid, Guid?>());

        ResolvedDecisionProvider? resolved = await resolver.ResolveDecisionAsync(
            explicitProviderId: chatProvider.Id, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.ProviderId.Should().Be(decisionDefault.Id,
            "a Chat-capability id passed as the explicit decision provider must fall through to the Decision default");
    }

    [Fact]
    public async Task ResolveDecisionAsync_returns_null_when_no_Decision_provider_exists()
    {
        // No fallback to the legacy AzureOpenAI:* configuration keys for decision either, even
        // when a Chat default row exists.
        ModelCapabilityView chatDefault = MakeProvider(
            isDefault: true, name: "chat-default", capability: InferenceProviderCapability.Chat);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { chatDefault },
            overrides: new Dictionary<Guid, Guid?>(),
            configOverrides: new Dictionary<string, string?>
            {
                ["AzureOpenAI:Endpoint"] = "https://config-fallback.example",
                ["AzureOpenAI:ApiKey"] = "config-key",
                ["AzureOpenAI:DeploymentName"] = "config-deployment"
            });

        ResolvedDecisionProvider? resolved = await resolver.ResolveDecisionAsync(
            explicitProviderId: null, CancellationToken.None);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task ResolveVideoGenerationAsync_returns_the_explicit_provider_when_its_capability_is_VideoGeneration()
    {
        ModelCapabilityView defaultProvider = MakeProvider(
            isDefault: true, name: "videogen-default", capability: InferenceProviderCapability.VideoGeneration);
        ModelCapabilityView explicitProvider = MakeProvider(
            isDefault: false, name: "videogen-explicit", capability: InferenceProviderCapability.VideoGeneration);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { defaultProvider, explicitProvider },
            overrides: new Dictionary<Guid, Guid?>());

        ResolvedVideoGenerationProvider? resolved = await resolver.ResolveVideoGenerationAsync(
            explicitProviderId: explicitProvider.Id, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.ProviderId.Should().Be(explicitProvider.Id);
        resolved.Name.Should().Be("videogen-explicit");
    }

    [Fact]
    public async Task ResolveVideoGenerationAsync_falls_back_to_the_default_VideoGeneration_provider_when_explicit_id_is_null()
    {
        ModelCapabilityView defaultProvider = MakeProvider(
            isDefault: true, name: "videogen-default", capability: InferenceProviderCapability.VideoGeneration);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { defaultProvider },
            overrides: new Dictionary<Guid, Guid?>());

        ResolvedVideoGenerationProvider? resolved = await resolver.ResolveVideoGenerationAsync(
            explicitProviderId: null, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.ProviderId.Should().Be(defaultProvider.Id);
    }

    [Fact]
    public async Task ResolveVideoGenerationAsync_ignores_a_Chat_provider_id_passed_as_explicit()
    {
        // Capability guard: a Chat provider id passed as the explicit video generation provider must fall
        // through to the VideoGeneration default (or null), never be sent through the video generation path.
        ModelCapabilityView chatProvider = MakeProvider(
            isDefault: false, name: "chat-only", capability: InferenceProviderCapability.Chat);
        ModelCapabilityView videoGenerationDefault = MakeProvider(
            isDefault: true, name: "videogen-default", capability: InferenceProviderCapability.VideoGeneration);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { chatProvider, videoGenerationDefault },
            overrides: new Dictionary<Guid, Guid?>());

        ResolvedVideoGenerationProvider? resolved = await resolver.ResolveVideoGenerationAsync(
            explicitProviderId: chatProvider.Id, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.ProviderId.Should().Be(videoGenerationDefault.Id,
            "a Chat-capability id passed as the explicit video generation provider must fall through to the VideoGeneration default");
    }

    [Fact]
    public async Task ResolveVideoGenerationAsync_returns_null_when_no_VideoGeneration_provider_exists()
    {
        // No fallback to the legacy AzureOpenAI:* configuration keys for video generation either, even
        // when a Chat default row exists.
        ModelCapabilityView chatDefault = MakeProvider(
            isDefault: true, name: "chat-default", capability: InferenceProviderCapability.Chat);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { chatDefault },
            overrides: new Dictionary<Guid, Guid?>(),
            configOverrides: new Dictionary<string, string?>
            {
                ["AzureOpenAI:Endpoint"] = "https://config-fallback.example",
                ["AzureOpenAI:ApiKey"] = "config-key",
                ["AzureOpenAI:DeploymentName"] = "config-deployment"
            });

        ResolvedVideoGenerationProvider? resolved = await resolver.ResolveVideoGenerationAsync(
            explicitProviderId: null, CancellationToken.None);

        resolved.Should().BeNull();
    }

    // ---------------------------------------------------------------- audio (T-107)

    [Fact]
    public async Task ResolveAudioIsolationAsync_prefers_the_explicit_row_then_the_default_and_stamps_its_capability()
    {
        ModelCapabilityView isolationDefault = MakeProvider(
            isDefault: true, name: "iso-default", capability: InferenceProviderCapability.AudioIsolation);
        ModelCapabilityView isolationExplicit = MakeProvider(
            isDefault: false, name: "iso-explicit", capability: InferenceProviderCapability.AudioIsolation);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { isolationDefault, isolationExplicit }, overrides: new Dictionary<Guid, Guid?>());

        ResolvedAudioGenerationProvider? explicitRow = await resolver.ResolveAudioIsolationAsync(isolationExplicit.Id, CancellationToken.None);
        ResolvedAudioGenerationProvider? defaultRow = await resolver.ResolveAudioIsolationAsync(null, CancellationToken.None);

        explicitRow!.ProviderId.Should().Be(isolationExplicit.Id);
        explicitRow.Capability.Should().Be(InferenceProviderCapability.AudioIsolation);
        defaultRow!.ProviderId.Should().Be(isolationDefault.Id);
    }

    [Fact]
    public async Task ResolveAudioIsolationAsync_ignores_other_capabilities_and_has_no_legacy_fallback()
    {
        ModelCapabilityView music = MakeProvider(
            isDefault: true, name: "music-default", capability: InferenceProviderCapability.MusicGeneration);
        ModelCapabilityView chat = MakeProvider(
            isDefault: true, name: "chat-default", capability: InferenceProviderCapability.Chat);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { music, chat },
            overrides: new Dictionary<Guid, Guid?>(),
            configOverrides: new Dictionary<string, string?> { ["AzureOpenAI:Endpoint"] = "https://x.example", ["AzureOpenAI:ApiKey"] = "k" });

        (await resolver.ResolveAudioIsolationAsync(music.Id, CancellationToken.None)).Should().BeNull("a music row id must not reach the isolation path");
        (await resolver.ResolveAudioIsolationAsync(null, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ResolveVideoUpscaleAsync_and_ResolveFrameInterpolationAsync_prefer_the_explicit_row_then_the_default_and_stamp_their_capability()
    {
        foreach (InferenceProviderCapability capability in new[] { InferenceProviderCapability.VideoUpscale, InferenceProviderCapability.FrameInterpolation })
        {
            ModelCapabilityView rowDefault = MakeProvider(isDefault: true, name: "default", capability: capability);
            ModelCapabilityView rowExplicit = MakeProvider(isDefault: false, name: "explicit", capability: capability);
            InferenceProviderResolver resolver = CreateResolver(
                providers: new[] { rowDefault, rowExplicit }, overrides: new Dictionary<Guid, Guid?>());

            Task<ResolvedAudioGenerationProvider?> Resolve(Guid? id) => capability == InferenceProviderCapability.VideoUpscale
                ? resolver.ResolveVideoUpscaleAsync(id, CancellationToken.None)
                : resolver.ResolveFrameInterpolationAsync(id, CancellationToken.None);

            ResolvedAudioGenerationProvider? explicitRow = await Resolve(rowExplicit.Id);
            ResolvedAudioGenerationProvider? defaultRow = await Resolve(null);

            explicitRow!.ProviderId.Should().Be(rowExplicit.Id);
            explicitRow.Capability.Should().Be(capability);
            defaultRow!.ProviderId.Should().Be(rowDefault.Id);
        }
    }

    [Fact]
    public async Task ResolveVideoOutpaintAsync_prefers_the_explicit_row_then_the_default_stamps_its_capability_and_ignores_the_other_enhance_rows()
    {
        ModelCapabilityView rowDefault = MakeProvider(isDefault: true, name: "default", capability: InferenceProviderCapability.VideoOutpaint);
        ModelCapabilityView rowExplicit = MakeProvider(isDefault: false, name: "explicit", capability: InferenceProviderCapability.VideoOutpaint);
        ModelCapabilityView upscale = MakeProvider(isDefault: true, name: "up", capability: InferenceProviderCapability.VideoUpscale);
        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { rowDefault, rowExplicit, upscale }, overrides: new Dictionary<Guid, Guid?>());

        ResolvedAudioGenerationProvider? explicitRow = await resolver.ResolveVideoOutpaintAsync(rowExplicit.Id, CancellationToken.None);
        explicitRow!.ProviderId.Should().Be(rowExplicit.Id);
        explicitRow.Capability.Should().Be(InferenceProviderCapability.VideoOutpaint);
        (await resolver.ResolveVideoOutpaintAsync(null, CancellationToken.None))!.ProviderId.Should().Be(rowDefault.Id);
        (await resolver.ResolveVideoOutpaintAsync(upscale.Id, CancellationToken.None))!.ProviderId.Should().Be(rowDefault.Id, "an upscale row id never serves extend");

        InferenceProviderResolver onlyUpscale = CreateResolver(providers: new[] { upscale }, overrides: new Dictionary<Guid, Guid?>());
        (await onlyUpscale.ResolveVideoOutpaintAsync(null, CancellationToken.None)).Should().BeNull("no row means extend is off, never a fallback");
    }

    [Fact]
    public async Task ResolveLipSyncAsync_resolves_only_LipSync_rows_explicit_then_default_and_never_falls_back_to_VideoGeneration()
    {
        ModelCapabilityView rowDefault = MakeProvider(isDefault: true, name: "ls-default", capability: InferenceProviderCapability.LipSync);
        ModelCapabilityView rowExplicit = MakeProvider(isDefault: false, name: "ls-explicit", capability: InferenceProviderCapability.LipSync);
        ModelCapabilityView video = MakeProvider(isDefault: true, name: "video", capability: InferenceProviderCapability.VideoGeneration);
        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { rowDefault, rowExplicit, video }, overrides: new Dictionary<Guid, Guid?>());

        (await resolver.ResolveLipSyncAsync(rowExplicit.Id, CancellationToken.None))!.ProviderId.Should().Be(rowExplicit.Id);
        (await resolver.ResolveLipSyncAsync(null, CancellationToken.None))!.ProviderId.Should().Be(rowDefault.Id);
        (await resolver.ResolveLipSyncAsync(video.Id, CancellationToken.None))!.ProviderId.Should().Be(rowDefault.Id,
            "a VideoGeneration row id never serves lip-sync");

        InferenceProviderResolver onlyVideo = CreateResolver(providers: new[] { video }, overrides: new Dictionary<Guid, Guid?>());
        (await onlyVideo.ResolveLipSyncAsync(null, CancellationToken.None)).Should().BeNull("no LipSync row means none, never the video default");
        (await onlyVideo.ResolveLipSyncAsync(video.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task The_video_enhance_resolvers_ignore_other_capabilities_each_other_and_have_no_legacy_fallback()
    {
        ModelCapabilityView upscale = MakeProvider(isDefault: true, name: "up", capability: InferenceProviderCapability.VideoUpscale);
        ModelCapabilityView interpolation = MakeProvider(isDefault: true, name: "fi", capability: InferenceProviderCapability.FrameInterpolation);
        ModelCapabilityView chat = MakeProvider(isDefault: true, name: "chat-default", capability: InferenceProviderCapability.Chat);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { upscale, interpolation, chat },
            overrides: new Dictionary<Guid, Guid?>(),
            configOverrides: new Dictionary<string, string?> { ["AzureOpenAI:Endpoint"] = "https://x.example", ["AzureOpenAI:ApiKey"] = "k" });

        (await resolver.ResolveVideoUpscaleAsync(interpolation.Id, CancellationToken.None))
            .Should().NotBeNull("an interpolation row id falls back to the upscale default, never to the interpolation row")
            .And.Subject.As<ResolvedAudioGenerationProvider>().ProviderId.Should().Be(upscale.Id);
        (await resolver.ResolveFrameInterpolationAsync(upscale.Id, CancellationToken.None))!.ProviderId.Should().Be(interpolation.Id);
        (await resolver.ResolveVideoUpscaleAsync(chat.Id, CancellationToken.None))!.ProviderId.Should().Be(upscale.Id);

        InferenceProviderResolver onlyChat = CreateResolver(
            providers: new[] { chat }, overrides: new Dictionary<Guid, Guid?>(),
            configOverrides: new Dictionary<string, string?> { ["AzureOpenAI:Endpoint"] = "https://x.example", ["AzureOpenAI:ApiKey"] = "k" });
        (await onlyChat.ResolveVideoUpscaleAsync(null, CancellationToken.None)).Should().BeNull();
        (await onlyChat.ResolveFrameInterpolationAsync(null, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ResolveMusicGenerationAsync_returns_the_explicit_MusicGeneration_row_stamped_with_its_capability()
    {
        ModelCapabilityView musicDefault = MakeProvider(
            isDefault: true, name: "music-default", capability: InferenceProviderCapability.MusicGeneration);
        ModelCapabilityView musicExplicit = MakeProvider(
            isDefault: false, name: "music-explicit", capability: InferenceProviderCapability.MusicGeneration);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { musicDefault, musicExplicit }, overrides: new Dictionary<Guid, Guid?>());

        ResolvedAudioGenerationProvider? resolved = await resolver.ResolveMusicGenerationAsync(musicExplicit.Id, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.ProviderId.Should().Be(musicExplicit.Id);
        resolved.Capability.Should().Be(InferenceProviderCapability.MusicGeneration);
    }

    [Fact]
    public async Task ResolveMusicGenerationAsync_ignores_a_SoundEffect_row_id_and_falls_back_to_the_music_default()
    {
        ModelCapabilityView sfx = MakeProvider(
            isDefault: true, name: "sfx-default", capability: InferenceProviderCapability.SoundEffectGeneration);
        ModelCapabilityView musicDefault = MakeProvider(
            isDefault: true, name: "music-default", capability: InferenceProviderCapability.MusicGeneration);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { sfx, musicDefault }, overrides: new Dictionary<Guid, Guid?>());

        ResolvedAudioGenerationProvider? resolved = await resolver.ResolveMusicGenerationAsync(sfx.Id, CancellationToken.None);

        resolved!.ProviderId.Should().Be(musicDefault.Id);
    }

    [Fact]
    public async Task ResolveSoundEffectGenerationAsync_uses_the_sound_effect_default_and_never_the_music_one()
    {
        ModelCapabilityView musicDefault = MakeProvider(
            isDefault: true, name: "music-default", capability: InferenceProviderCapability.MusicGeneration);
        ModelCapabilityView sfxDefault = MakeProvider(
            isDefault: true, name: "sfx-default", capability: InferenceProviderCapability.SoundEffectGeneration);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { musicDefault, sfxDefault }, overrides: new Dictionary<Guid, Guid?>());

        ResolvedAudioGenerationProvider? resolved = await resolver.ResolveSoundEffectGenerationAsync(null, CancellationToken.None);

        resolved!.ProviderId.Should().Be(sfxDefault.Id);
        resolved.Capability.Should().Be(InferenceProviderCapability.SoundEffectGeneration);
    }

    [Fact]
    public async Task The_audio_resolvers_never_return_a_dormant_ElevenLabs_row_explicit_or_default()
    {
        // Product decision 7 (defence in depth): a row of the dormant kind that predates the decision is never resolved.
        ModelCapabilityView eleven = MakeProvider(
            isDefault: true, name: "eleven-music", capability: InferenceProviderCapability.MusicGeneration);
        eleven.Kind = InferenceProviderKind.ElevenLabs;
        ModelCapabilityView elevenSfx = MakeProvider(
            isDefault: true, name: "eleven-sfx", capability: InferenceProviderCapability.SoundEffectGeneration);
        elevenSfx.Kind = InferenceProviderKind.ElevenLabs;

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { eleven, elevenSfx }, overrides: new Dictionary<Guid, Guid?>());

        (await resolver.ResolveMusicGenerationAsync(eleven.Id, CancellationToken.None)).Should().BeNull();
        (await resolver.ResolveMusicGenerationAsync(null, CancellationToken.None)).Should().BeNull();
        (await resolver.ResolveSoundEffectGenerationAsync(null, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task The_audio_resolvers_return_null_when_no_audio_row_exists_and_never_use_the_legacy_config()
    {
        ModelCapabilityView chatDefault = MakeProvider(
            isDefault: true, name: "chat-default", capability: InferenceProviderCapability.Chat);
        ModelCapabilityView musicDefault = MakeProvider(
            isDefault: true, name: "music-default", capability: InferenceProviderCapability.MusicGeneration);

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { chatDefault, musicDefault },
            overrides: new Dictionary<Guid, Guid?>(),
            configOverrides: new Dictionary<string, string?>
            {
                ["AzureOpenAI:Endpoint"] = "https://config-fallback.example",
                ["AzureOpenAI:ApiKey"] = "config-key"
            });

        (await resolver.ResolveSoundEffectGenerationAsync(null, CancellationToken.None)).Should().BeNull();
        (await resolver.ResolveSoundEffectGenerationAsync(chatDefault.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ResolveVideoGenerationAsync_returns_null_when_explicit_id_points_to_disabled_provider()
    {
        // A real store's LoadEnabledAsync filters to IsEnabled providers, so a disabled provider
        // referenced by an explicit id is deliberately absent from the "enabled" list here -
        // mirroring what the resolver actually sees. With no default VideoGeneration provider
        // configured and the explicit id pointing to a disabled row, the result must be null.
        Guid disabledProviderId = Guid.NewGuid();

        InferenceProviderResolver resolver = CreateResolver(
            providers: Array.Empty<ModelCapabilityView>(),
            overrides: new Dictionary<Guid, Guid?>());

        ResolvedVideoGenerationProvider? resolved = await resolver.ResolveVideoGenerationAsync(
            explicitProviderId: disabledProviderId, CancellationToken.None);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task ResolveEmbeddingAsync_prefers_explicit_then_default_Embedding_row()
    {
        ModelCapabilityView defaultRow = MakeProvider(true, "default-emb", InferenceProviderCapability.Embedding);
        ModelCapabilityView other = MakeProvider(false, "other-emb", InferenceProviderCapability.Embedding);
        ModelCapabilityView chatDefault = MakeProvider(true, "chat");
        InferenceProviderResolver resolver = CreateResolver(new[] { defaultRow, other, chatDefault }, new Dictionary<Guid, Guid?>());

        (await resolver.ResolveEmbeddingAsync(other.Id, CancellationToken.None))!.Name.Should().Be("other-emb");
        (await resolver.ResolveEmbeddingAsync(null, CancellationToken.None))!.Name.Should().Be("default-emb");
        // A Chat row id must never be handed to the embedding path.
        (await resolver.ResolveEmbeddingAsync(chatDefault.Id, CancellationToken.None))!.Name.Should().Be("default-emb");
    }

    [Fact]
    public async Task ResolveEmbeddingAsync_falls_back_to_legacy_AzureOpenAI_config_then_null()
    {
        InferenceProviderResolver withConfig = CreateResolver(
            Array.Empty<ModelCapabilityView>(), new Dictionary<Guid, Guid?>(),
            new Dictionary<string, string?>
            {
                ["AzureOpenAI:Endpoint"] = "https://cfg.example",
                ["AzureOpenAI:ApiKey"] = "k",
                ["VectorSearch:EmbeddingDeployment"] = "text-embedding-3-small"
            });
        ResolvedEmbeddingProvider? fallback = await withConfig.ResolveEmbeddingAsync(null, CancellationToken.None);
        fallback.Should().NotBeNull();
        fallback!.Kind.Should().Be(InferenceProviderKind.AzureOpenAI);
        fallback.ModelName.Should().Be("text-embedding-3-small");

        InferenceProviderResolver none = CreateResolver(Array.Empty<ModelCapabilityView>(), new Dictionary<Guid, Guid?>());
        (await none.ResolveEmbeddingAsync(null, CancellationToken.None)).Should().BeNull();
    }

    // --- the picture editor's per-job image capabilities ---

    [Theory]
    [InlineData(InferenceProviderCapability.ImageErase)]
    [InlineData(InferenceProviderCapability.ImageFill)]
    [InlineData(InferenceProviderCapability.ImageExpand)]
    [InlineData(InferenceProviderCapability.ImageBackgroundReplace)]
    [InlineData(InferenceProviderCapability.ImageRelight)]
    [InlineData(InferenceProviderCapability.ImageUpscale)]
    [InlineData(InferenceProviderCapability.ImageEdit)]
    [InlineData(InferenceProviderCapability.ImageGeneration)]
    public async Task ResolveImageJobAsync_prefers_explicit_then_the_default_of_exactly_that_capability(
        InferenceProviderCapability capability)
    {
        ModelCapabilityView defaultRow = MakeProvider(true, "default-job", capability);
        ModelCapabilityView other = MakeProvider(false, "other-job", capability);
        // A default of EVERY other image capability, so a resolver that read the wrong default would show.
        List<ModelCapabilityView> rows = [defaultRow, other];
        rows.AddRange(ImageJobCapabilities.All.Where(c => c != capability).Select(c => MakeProvider(true, "default-" + c, c)));
        InferenceProviderResolver resolver = CreateResolver(rows, new Dictionary<Guid, Guid?>());

        ResolvedImageProvider? explicitRow = await resolver.ResolveImageJobAsync(capability, other.Id, CancellationToken.None);
        explicitRow!.Name.Should().Be("other-job");
        explicitRow.Capability.Should().Be(capability);
        (await resolver.ResolveImageJobAsync(capability, null, CancellationToken.None))!.Name.Should().Be("default-job");

        // Another image capability's row passed as the explicit id falls through to this capability's default.
        ModelCapabilityView foreignCapability = rows.First(r => r.Capability != capability);
        (await resolver.ResolveImageJobAsync(capability, foreignCapability.Id, CancellationToken.None))!.Name.Should().Be("default-job");
    }

    [Fact]
    public async Task ResolveImageJobAsync_does_not_fall_back_to_ImageEdit_by_itself()
    {
        // The compatibility fallback is the editor's decision, made visibly; the resolver stays strict.
        InferenceProviderResolver resolver = CreateResolver(
            [MakeProvider(true, "edit", InferenceProviderCapability.ImageEdit)], new Dictionary<Guid, Guid?>());

        (await resolver.ResolveImageJobAsync(InferenceProviderCapability.ImageUpscale, null, CancellationToken.None)).Should().BeNull();
        (await resolver.ResolveImageEditAsync(null, CancellationToken.None))!.Name.Should().Be("edit");
    }

    [Fact]
    public async Task ResolveImageJobAsync_refuses_a_capability_that_is_not_an_image_job()
    {
        InferenceProviderResolver resolver = CreateResolver([], new Dictionary<Guid, Guid?>());

        Func<Task> act = () => resolver.ResolveImageJobAsync(InferenceProviderCapability.Segmentation, null, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // --- A9a: organization-scoped resolution ---

    [Fact]
    public async Task Org_cannot_resolve_another_orgs_provider_by_explicit_id()
    {
        Guid orgA = Guid.NewGuid();
        Guid orgB = Guid.NewGuid();
        ModelCapabilityView platformDefault = MakeProvider(isDefault: true, name: "platform");
        ModelCapabilityView orgBProvider = MakeProvider(isDefault: false, name: "org-b", organizationId: orgB);
        TestTenant tenant = new() { Org = orgA };

        InferenceProviderResolver resolver = CreateResolver(
            new[] { platformDefault, orgBProvider }, new Dictionary<Guid, Guid?>(), tenant: tenant);

        (await resolver.ResolveChatAsync(orgBProvider.Id, CancellationToken.None)).Should().BeNull(
            "an id outside the caller's snapshot is treated as missing, not served");

        ResolvedInferenceProvider viaAgent = await resolver.ResolveAsync(
            AgentType.CodeStructureAnalyzer, null, CancellationToken.None);
        viaAgent.ProviderId.Should().Be(platformDefault.Id);

        tenant.Org = orgB;
        ResolvedInferenceProvider? own = await resolver.ResolveChatAsync(orgBProvider.Id, CancellationToken.None);
        own.Should().NotBeNull();
        own!.OrganizationId.Should().Be(orgB);
        own.IsPlatformManaged.Should().BeFalse();
    }

    [Fact]
    public async Task Foreign_explicit_ids_fall_through_to_the_default_for_every_capability()
    {
        Guid orgA = Guid.NewGuid();
        Guid orgB = Guid.NewGuid();
        TestTenant tenant = new() { Org = orgA };
        ModelCapabilityView foreignTranscription = MakeProvider(false, "t-b", InferenceProviderCapability.Transcription, orgB);
        ModelCapabilityView foreignVision = MakeProvider(false, "v-b", InferenceProviderCapability.Vision, orgB);
        ModelCapabilityView foreignDecision = MakeProvider(false, "d-b", InferenceProviderCapability.Decision, orgB);
        ModelCapabilityView foreignVideo = MakeProvider(false, "g-b", InferenceProviderCapability.VideoGeneration, orgB);
        ModelCapabilityView foreignSpeech = MakeProvider(false, "s-b", InferenceProviderCapability.SpeechSynthesis, orgB);
        ModelCapabilityView foreignEmbedding = MakeProvider(false, "e-b", InferenceProviderCapability.Embedding, orgB);
        ModelCapabilityView platformTranscription = MakeProvider(true, "t-p", InferenceProviderCapability.Transcription);

        InferenceProviderResolver resolver = CreateResolver(
            new[] { foreignTranscription, foreignVision, foreignDecision, foreignVideo, foreignSpeech, foreignEmbedding, platformTranscription },
            new Dictionary<Guid, Guid?>(), tenant: tenant);

        (await resolver.ResolveTranscriptionAsync(foreignTranscription.Id, CancellationToken.None))!
            .ProviderId.Should().Be(platformTranscription.Id);
        (await resolver.ResolveVisionAsync(foreignVision.Id, CancellationToken.None)).Should().BeNull();
        (await resolver.ResolveDecisionAsync(foreignDecision.Id, CancellationToken.None)).Should().BeNull();
        (await resolver.ResolveVideoGenerationAsync(foreignVideo.Id, CancellationToken.None)).Should().BeNull();
        (await resolver.ResolveSpeechSynthesisAsync(foreignSpeech.Id, CancellationToken.None)).Should().BeNull();
        (await resolver.ResolveEmbeddingAsync(foreignEmbedding.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Org_default_beats_platform_default_and_other_orgs_still_get_the_platform_one()
    {
        Guid orgA = Guid.NewGuid();
        Guid orgB = Guid.NewGuid();
        ModelCapabilityView platformDefault = MakeProvider(isDefault: true, name: "platform");
        ModelCapabilityView orgADefault = MakeProvider(isDefault: true, name: "org-a", organizationId: orgA);
        TestTenant tenant = new() { Org = orgA };

        InferenceProviderResolver resolver = CreateResolver(
            new[] { platformDefault, orgADefault }, new Dictionary<Guid, Guid?>(), tenant: tenant);

        (await resolver.ResolveAsync(AgentType.CodeStructureAnalyzer, null, CancellationToken.None))
            .ProviderId.Should().Be(orgADefault.Id);

        tenant.Org = orgB;
        (await resolver.ResolveAsync(AgentType.CodeStructureAnalyzer, null, CancellationToken.None))
            .ProviderId.Should().Be(platformDefault.Id);

        tenant.Org = null;
        (await resolver.ResolveAsync(AgentType.CodeStructureAnalyzer, null, CancellationToken.None))
            .ProviderId.Should().Be(platformDefault.Id, "no tenant scope means the platform-only view");
    }

    [Fact]
    public async Task Org_default_beats_platform_default_for_speech_synthesis()
    {
        Guid orgA = Guid.NewGuid();
        ModelCapabilityView platformDefault = MakeProvider(true, "platform", InferenceProviderCapability.SpeechSynthesis);
        ModelCapabilityView orgDefault = MakeProvider(true, "org", InferenceProviderCapability.SpeechSynthesis, orgA);

        InferenceProviderResolver resolver = CreateResolver(
            new[] { platformDefault, orgDefault }, new Dictionary<Guid, Guid?>(), tenant: new TestTenant { Org = orgA });

        (await resolver.ResolveSpeechSynthesisAsync(null, CancellationToken.None))!
            .ProviderId.Should().Be(orgDefault.Id);
        (await resolver.ResolveSpeechSynthesisAsync(platformDefault.Id, CancellationToken.None))!
            .ProviderId.Should().Be(platformDefault.Id, "an explicit platform row stays selectable");
    }

    [Fact]
    public async Task Byo_disabled_org_falls_back_to_platform_rows_for_defaults_overrides_and_explicit_ids()
    {
        Guid orgA = Guid.NewGuid();
        ModelCapabilityView platformDefault = MakeProvider(isDefault: true, name: "platform");
        ModelCapabilityView orgADefault = MakeProvider(isDefault: true, name: "org-a", organizationId: orgA);
        TestTenant tenant = new() { Org = orgA };
        Dictionary<Guid, IReadOnlyDictionary<AgentType, Guid?>> orgOverrides = new()
        {
            [orgA] = new Dictionary<AgentType, Guid?> { [AgentType.CodeStructureAnalyzer] = orgADefault.Id }
        };
        bool allowed = true;

        InferenceProviderResolver resolver = CreateResolver(
            new[] { platformDefault, orgADefault }, new Dictionary<Guid, Guid?>(),
            tenant: tenant, byoAllowed: _ => allowed, orgOverrides: orgOverrides);

        (await resolver.ResolveAsync(AgentType.CodeStructureAnalyzer, null, CancellationToken.None))
            .ProviderId.Should().Be(orgADefault.Id);

        // Downgrade: the plan stops allowing BYO. Invalidate so the change is seen at once.
        allowed = false;
        resolver.Invalidate(orgA);

        (await resolver.ResolveAsync(AgentType.CodeStructureAnalyzer, null, CancellationToken.None))
            .ProviderId.Should().Be(platformDefault.Id);
        (await resolver.ResolveChatAsync(orgADefault.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Org_override_for_a_built_in_agent_type_beats_the_platform_built_in_override()
    {
        Guid orgA = Guid.NewGuid();
        ModelCapabilityView platformDefault = MakeProvider(isDefault: true, name: "platform");
        ModelCapabilityView platformPick = MakeProvider(isDefault: false, name: "platform-pick");
        ModelCapabilityView orgPick = MakeProvider(isDefault: false, name: "org-pick", organizationId: orgA);
        TestTenant tenant = new() { Org = orgA };

        InferenceProviderResolver resolver = CreateResolver(
            new[] { platformDefault, platformPick, orgPick }, new Dictionary<Guid, Guid?>(),
            builtInOverrides: new Dictionary<AgentType, Guid?> { [AgentType.VideoStoryEditor] = platformPick.Id },
            tenant: tenant,
            orgOverrides: new Dictionary<Guid, IReadOnlyDictionary<AgentType, Guid?>>
            {
                [orgA] = new Dictionary<AgentType, Guid?> { [AgentType.VideoStoryEditor] = orgPick.Id }
            });

        (await resolver.ResolveAsync(AgentType.VideoStoryEditor, null, CancellationToken.None))
            .ProviderId.Should().Be(orgPick.Id);

        tenant.Org = Guid.NewGuid();
        (await resolver.ResolveAsync(AgentType.VideoStoryEditor, null, CancellationToken.None))
            .ProviderId.Should().Be(platformPick.Id);
    }

    [Fact]
    public async Task Concurrent_org_scopes_never_leak_providers_between_orgs()
    {
        Guid[] orgs = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        List<ModelCapabilityView> rows = [MakeProvider(isDefault: true, name: "platform")];
        rows.AddRange(orgs.Select(o => MakeProvider(isDefault: true, name: o.ToString(), organizationId: o)));
        AsyncLocalTenant tenant = new();

        InferenceProviderResolver resolver = CreateResolver(rows, new Dictionary<Guid, Guid?>(), tenant: tenant);

        async Task Run(Guid org)
        {
            using IDisposable _ = tenant.Begin(org);
            for (int i = 0; i < 50; i++)
            {
                await Task.Yield();
                ResolvedInferenceProvider resolved = await resolver.ResolveAsync(
                    AgentType.CodeStructureAnalyzer, null, CancellationToken.None);
                resolved.OrganizationId.Should().Be(org);
            }
        }

        await Task.WhenAll(orgs.Select(o => Task.Run(() => Run(o))));
    }

    [Fact]
    public async Task Invalidate_makes_a_provider_change_visible_before_the_ttl()
    {
        Guid orgA = Guid.NewGuid();
        List<ModelCapabilityView> rows = [MakeProvider(isDefault: true, name: "platform")];
        InferenceProviderResolver resolver = CreateResolver(
            rows, new Dictionary<Guid, Guid?>(), tenant: new TestTenant { Org = orgA });

        (await resolver.ResolveAsync(AgentType.CodeStructureAnalyzer, null, CancellationToken.None))
            .OrganizationId.Should().BeNull();

        ModelCapabilityView added = MakeProvider(isDefault: true, name: "org", organizationId: orgA);
        rows.Add(added);

        (await resolver.ResolveAsync(AgentType.CodeStructureAnalyzer, null, CancellationToken.None))
            .OrganizationId.Should().BeNull("the snapshot is still cached");

        resolver.Invalidate(orgA);
        (await resolver.ResolveAsync(AgentType.CodeStructureAnalyzer, null, CancellationToken.None))
            .ProviderId.Should().Be(added.Id);
    }

    [Fact]
    public async Task A_platform_scope_ignores_the_ambient_org()
    {
        Guid orgA = Guid.NewGuid();
        ModelCapabilityView platformEmbedding = MakeProvider(true, "p", InferenceProviderCapability.Embedding);
        ModelCapabilityView orgEmbedding = MakeProvider(true, "o", InferenceProviderCapability.Embedding, orgA);
        TestTenant tenant = new() { Org = orgA };
        InferenceProviderResolver resolver = CreateResolver(
            new[] { platformEmbedding, orgEmbedding }, new Dictionary<Guid, Guid?>(), tenant: tenant);

        (await resolver.ResolveEmbeddingAsync(null, CancellationToken.None))!.ProviderId.Should().Be(orgEmbedding.Id);

        using (tenant.BeginPlatformScope())
        {
            (await resolver.ResolveEmbeddingAsync(null, CancellationToken.None))!.ProviderId.Should().Be(platformEmbedding.Id);
        }

        (await resolver.ResolveEmbeddingAsync(null, CancellationToken.None))!.ProviderId.Should().Be(orgEmbedding.Id);
    }

    // ================================================================ VideoSegmentation (T-003)

    [Fact]
    public async Task ResolveVideoSegmentationAsync_answers_the_capabilitys_default_row()
    {
        ModelCapabilityView video = MakeProvider(
            isDefault: true, name: "fal video", capability: InferenceProviderCapability.VideoSegmentation,
            kind: InferenceProviderKind.FalAi, modelName: "fal-ai/sam-3-1/video-rle", apiKeyEncrypted: "fal-plain-key");
        // A default row of another capability must never be selected for this one.
        ModelCapabilityView segmentation = MakeProvider(
            isDefault: true, name: "fal image segmentation", capability: InferenceProviderCapability.Segmentation,
            kind: InferenceProviderKind.FalAi, modelName: "fal-ai/sam-3-1/image");

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { video, segmentation },
            overrides: new Dictionary<Guid, Guid?>());

        ResolvedImageAnalysisProvider? resolved = await resolver.ResolveVideoSegmentationAsync(
            explicitProviderId: null, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.ProviderId.Should().Be(video.Id);
        resolved.Capability.Should().Be(InferenceProviderCapability.VideoSegmentation);
        resolved.Kind.Should().Be(InferenceProviderKind.FalAi);
        resolved.ModelName.Should().Be("fal-ai/sam-3-1/video-rle");
        resolved.ApiKey.Should().Be("fal-plain-key");
    }

    [Fact]
    public async Task ResolveVideoSegmentationAsync_refuses_an_explicit_row_of_another_capability()
    {
        ModelCapabilityView video = MakeProvider(
            isDefault: true, name: "fal video", capability: InferenceProviderCapability.VideoSegmentation,
            kind: InferenceProviderKind.FalAi, modelName: "fal-ai/sam-3-1/video-rle");
        ModelCapabilityView segmentation = MakeProvider(
            isDefault: false, name: "fal image segmentation", capability: InferenceProviderCapability.Segmentation,
            kind: InferenceProviderKind.FalAi, modelName: "fal-ai/sam-3-1/image");

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { video, segmentation },
            overrides: new Dictionary<Guid, Guid?>());

        ResolvedImageAnalysisProvider? resolved = await resolver.ResolveVideoSegmentationAsync(
            explicitProviderId: segmentation.Id, CancellationToken.None);

        resolved!.ProviderId.Should().Be(video.Id,
            "an image-segmentation row id authored into a video step must fall back to the video default, " +
            "never be handed to the video client");
    }

    [Fact]
    public async Task ResolveVideoSegmentationAsync_answers_null_when_no_row_exists()
    {
        InferenceProviderResolver resolver = CreateResolver(
            providers: Array.Empty<ModelCapabilityView>(),
            overrides: new Dictionary<Guid, Guid?>());

        (await resolver.ResolveVideoSegmentationAsync(null, CancellationToken.None)).Should().BeNull(
            "no legacy configuration fallback exists for this capability: the analyze step refuses with " +
            "SEGMENTATION_NO_PROVIDER rather than silently using the feature tracker");
    }

    [Fact]
    public async Task The_video_segmentation_resolution_carries_no_key_in_its_cache_key_or_to_string()
    {
        ModelCapabilityView video = MakeProvider(
            isDefault: true, name: "fal video", capability: InferenceProviderCapability.VideoSegmentation,
            kind: InferenceProviderKind.FalAi, modelName: "fal-ai/sam-3-1/video-rle",
            apiKeyEncrypted: "super-secret-fal-key");

        InferenceProviderResolver resolver = CreateResolver(
            providers: new[] { video }, overrides: new Dictionary<Guid, Guid?>());

        ResolvedImageAnalysisProvider resolved = (await resolver.ResolveVideoSegmentationAsync(null, CancellationToken.None))!;

        resolved.CacheKey.Should().NotContain("super-secret-fal-key");
        resolved.CacheKey.Should().MatchRegex("^[0-9A-F]{64}$", "the cache key is a hash, never the key itself");
        resolved.ToString().Should().NotContain("super-secret-fal-key");
        resolved.ToString().Should().Contain("VideoSegmentation").And.Contain("fal-ai/sam-3-1/video-rle");
    }

    // --- F-1.1: an enabled PLATFORM capability with no effective price is never resolved while billing is on ---

    private static readonly Dictionary<string, string?> BillingOn = new() { ["Billing:Enabled"] = "true" };

    [Fact]
    public async Task Billing_on_an_explicit_unpriced_platform_capability_falls_to_the_priced_default()
    {
        ModelCapabilityView pricedDefault = MakeProvider(true, "priced default", InferenceProviderCapability.Transcription);
        ModelCapabilityView unpriced = MakeProvider(false, "unpriced", InferenceProviderCapability.Transcription);
        InferenceProviderResolver resolver = CreateResolver(
            new[] { pricedDefault, unpriced }, new Dictionary<Guid, Guid?>(), configOverrides: BillingOn, unpriced: [unpriced.Id]);

        (await resolver.ResolveTranscriptionAsync(unpriced.Id, CancellationToken.None))!.ProviderId
            .Should().Be(pricedDefault.Id, "an unpriced platform capability is treated as absent");
    }

    [Fact]
    public async Task Billing_on_an_unpriced_default_with_nothing_left_refuses_with_MODEL_UNPRICED()
    {
        ModelCapabilityView unpricedDefault = MakeProvider(true, "Speechy", InferenceProviderCapability.Transcription);
        InferenceProviderResolver resolver = CreateResolver(
            new[] { unpricedDefault }, new Dictionary<Guid, Guid?>(), configOverrides: BillingOn, unpriced: [unpricedDefault.Id]);

        Func<Task> resolve = () => resolver.ResolveTranscriptionAsync(null, CancellationToken.None);

        ModelUnpricedException refused = (await resolve.Should().ThrowAsync<ModelUnpricedException>()).Which;
        refused.Code.Should().Be("MODEL_UNPRICED");
        refused.Capability.Should().Be(InferenceProviderCapability.Transcription);
        refused.Message.Should().Contain("Speechy").And.Contain("MODEL_UNPRICED");
    }

    [Fact]
    public async Task Billing_on_an_explicit_unpriced_video_model_with_no_default_refuses()
    {
        ModelCapabilityView unpriced = MakeProvider(false, "Clipper", InferenceProviderCapability.VideoGeneration);
        InferenceProviderResolver resolver = CreateResolver(
            new[] { unpriced }, new Dictionary<Guid, Guid?>(), configOverrides: BillingOn, unpriced: [unpriced.Id]);

        Func<Task> resolve = () => resolver.ResolveVideoGenerationAsync(unpriced.Id, CancellationToken.None);

        await resolve.Should().ThrowAsync<ModelUnpricedException>();
    }

    [Fact]
    public async Task Billing_on_an_unpriced_LipSync_row_with_no_other_candidate_refuses_like_video()
    {
        ModelCapabilityView unpriced = MakeProvider(true, "Lips", InferenceProviderCapability.LipSync);
        InferenceProviderResolver resolver = CreateResolver(
            new[] { unpriced }, new Dictionary<Guid, Guid?>(), configOverrides: BillingOn, unpriced: [unpriced.Id]);

        await ((Func<Task>)(() => resolver.ResolveLipSyncAsync(null, CancellationToken.None))).Should().ThrowAsync<ModelUnpricedException>();
        await ((Func<Task>)(() => resolver.ResolveLipSyncAsync(unpriced.Id, CancellationToken.None))).Should().ThrowAsync<ModelUnpricedException>();
    }

    [Fact]
    public async Task Billing_on_an_unpriced_chat_default_refuses_rather_than_using_the_config_fallback()
    {
        ModelCapabilityView unpricedDefault = MakeProvider(true, "default");
        Dictionary<string, string?> config = new(BillingOn) { ["AzureOpenAI:Endpoint"] = "https://config-fallback.example" };
        InferenceProviderResolver resolver = CreateResolver(
            new[] { unpricedDefault }, new Dictionary<Guid, Guid?>(), configOverrides: config, unpriced: [unpricedDefault.Id]);

        Func<Task> resolve = () => resolver.ResolveAsync(AgentType.CodeStructureAnalyzer, null, CancellationToken.None);

        await resolve.Should().ThrowAsync<ModelUnpricedException>();
    }

    [Fact]
    public async Task Billing_on_an_agent_override_to_an_unpriced_model_falls_to_the_chat_default()
    {
        Guid agentId = Guid.NewGuid();
        ModelCapabilityView pricedDefault = MakeProvider(true, "default");
        ModelCapabilityView unpriced = MakeProvider(false, "unpriced");
        InferenceProviderResolver resolver = CreateResolver(
            new[] { pricedDefault, unpriced }, new Dictionary<Guid, Guid?> { [agentId] = unpriced.Id },
            configOverrides: BillingOn, unpriced: [unpriced.Id]);

        (await resolver.ResolveAsync(AgentType.CodeStructureAnalyzer, agentId, CancellationToken.None)).ProviderId
            .Should().Be(pricedDefault.Id);
    }

    [Fact]
    public async Task Billing_on_a_pinned_unpriced_chat_model_does_not_resolve()
    {
        ModelCapabilityView unpriced = MakeProvider(false, "unpriced");
        InferenceProviderResolver resolver = CreateResolver(
            new[] { MakeProvider(true, "default"), unpriced }, new Dictionary<Guid, Guid?>(),
            configOverrides: BillingOn, unpriced: [unpriced.Id]);

        (await resolver.ResolveChatAsync(unpriced.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Billing_on_an_unpriced_speech_default_hands_over_to_a_priced_row_or_refuses()
    {
        ModelCapabilityView unpricedDefault = MakeProvider(true, "unpriced tts", InferenceProviderCapability.SpeechSynthesis);
        InferenceProviderResolver alone = CreateResolver(
            new[] { unpricedDefault }, new Dictionary<Guid, Guid?>(), configOverrides: BillingOn, unpriced: [unpricedDefault.Id]);

        Func<Task> resolve = () => alone.ResolveSpeechSynthesisAsync(unpricedDefault.Id, CancellationToken.None);
        await resolve.Should().ThrowAsync<ModelUnpricedException>();
    }

    [Fact]
    public async Task Billing_on_byo_and_oracle_capabilities_are_never_treated_as_unpriced()
    {
        Guid org = Guid.NewGuid();
        ModelCapabilityView byo = MakeProvider(true, "byo", InferenceProviderCapability.Transcription, org);
        ModelCapabilityView oracle = MakeProvider(true, "higgs", InferenceProviderCapability.VideoGeneration);
        oracle.PriceSource = ModelPriceSource.Oracle;
        InferenceProviderResolver resolver = CreateResolver(
            new[] { byo, oracle }, new Dictionary<Guid, Guid?>(), configOverrides: BillingOn,
            tenant: new TestTenant { Org = org }, unpriced: [byo.Id, oracle.Id]);

        (await resolver.ResolveTranscriptionAsync(null, CancellationToken.None))!.ProviderId.Should().Be(byo.Id);
        (await resolver.ResolveVideoGenerationAsync(null, CancellationToken.None))!.ProviderId.Should().Be(oracle.Id);
    }

    [Fact]
    public async Task Billing_off_unpriced_rows_resolve_exactly_as_before()
    {
        ModelCapabilityView unpricedDefault = MakeProvider(true, "default");
        ModelCapabilityView unpricedAsr = MakeProvider(false, "asr", InferenceProviderCapability.Transcription);
        InferenceProviderResolver resolver = CreateResolver(
            new[] { unpricedDefault, unpricedAsr }, new Dictionary<Guid, Guid?>(),
            unpriced: [unpricedDefault.Id, unpricedAsr.Id]);

        (await resolver.ResolveAsync(AgentType.CodeStructureAnalyzer, null, CancellationToken.None)).ProviderId
            .Should().Be(unpricedDefault.Id);
        (await resolver.ResolveTranscriptionAsync(unpricedAsr.Id, CancellationToken.None))!.ProviderId
            .Should().Be(unpricedAsr.Id);
    }

    private sealed class TestTenant : InferenceTenantAccessorBase
    {
        public Guid? Org { get; set; }
        protected override Guid? AmbientOrganizationId => Org;
    }

    private sealed class AsyncLocalTenant : InferenceTenantAccessorBase
    {
        private readonly System.Threading.AsyncLocal<Guid?> _org = new();
        protected override Guid? AmbientOrganizationId => _org.Value;

        public IDisposable Begin(Guid org)
        {
            _org.Value = org;
            return new Reset(_org);
        }

        private sealed class Reset(System.Threading.AsyncLocal<Guid?> slot) : IDisposable
        {
            public void Dispose() => slot.Value = null;
        }
    }

    private sealed class FuncEntitlements(Func<Guid, bool> byoAllowed) : IOrganizationEntitlements
    {
        public ValueTask<OrganizationEntitlementSet> GetAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new OrganizationEntitlementSet { ByoAllowed = byoAllowed(organizationId) });
    }

    private static ModelCapabilityView MakeProvider(
        bool isDefault,
        string name,
        InferenceProviderCapability capability = InferenceProviderCapability.Chat,
        Guid? organizationId = null,
        InferenceProviderKind kind = InferenceProviderKind.AzureOpenAI,
        string modelName = "gpt-4o-mini",
        string? apiKeyEncrypted = null) => new()
        {
            OrganizationId = organizationId,
            Id = Guid.NewGuid(),
            Name = name,
            Kind = kind,
            Endpoint = "https://provider.example",
            ModelName = modelName,
            ApiKeyEncrypted = apiKeyEncrypted,
            IsDefault = isDefault,
            IsEnabled = true,
            Capability = capability
        };

    private static InferenceProviderResolver CreateResolver(
        IReadOnlyList<ModelCapabilityView> providers,
        IReadOnlyDictionary<Guid, Guid?> overrides,
        IDictionary<string, string?>? configOverrides = null,
        IReadOnlyDictionary<AgentType, Guid?>? builtInOverrides = null,
        IInferenceTenantAccessor? tenant = null,
        Func<Guid, bool>? byoAllowed = null,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<AgentType, Guid?>>? orgOverrides = null,
        IReadOnlyCollection<Guid>? unpriced = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<IInferenceProviderStore>(
            new FakeInferenceProviderStore(providers, overrides, builtInOverrides, orgOverrides, unpriced));
        if (byoAllowed is not null)
            services.AddSingleton<IOrganizationEntitlements>(new FuncEntitlements(byoAllowed));
        ServiceProvider serviceProvider = services.BuildServiceProvider();

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configOverrides ?? new Dictionary<string, string?>())
            .Build();

        return new InferenceProviderResolver(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            new PassthroughSecretProtector(),
            NullLogger<InferenceProviderResolver>.Instance,
            tenant);
    }

    private sealed class FakeInferenceProviderStore : IInferenceProviderStore
    {
        private readonly IReadOnlyList<ModelCapabilityView> _providers;
        private readonly IReadOnlyDictionary<Guid, Guid?> _overrides;
        private readonly IReadOnlyDictionary<AgentType, Guid?> _builtInOverrides;
        private readonly IReadOnlyDictionary<Guid, IReadOnlyDictionary<AgentType, Guid?>> _orgOverrides;
        private readonly HashSet<Guid> _unpriced;

        public Task<IReadOnlySet<Guid>> LoadUnpricedCapabilityIdsAsync(DateTime at, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<Guid>>(_unpriced);

        public FakeInferenceProviderStore(
            IReadOnlyList<ModelCapabilityView> providers,
            IReadOnlyDictionary<Guid, Guid?> overrides,
            IReadOnlyDictionary<AgentType, Guid?>? builtInOverrides = null,
            IReadOnlyDictionary<Guid, IReadOnlyDictionary<AgentType, Guid?>>? orgOverrides = null,
            IReadOnlyCollection<Guid>? unpriced = null)
        {
            _unpriced = new HashSet<Guid>(unpriced ?? []);
            _orgOverrides = orgOverrides ?? new Dictionary<Guid, IReadOnlyDictionary<AgentType, Guid?>>();
            _providers = providers;
            _overrides = overrides;
            _builtInOverrides = builtInOverrides ?? new Dictionary<AgentType, Guid?>();
        }

        // Mirrors the real stores: platform rows plus the caller's own organization's rows.
        public Task<IReadOnlyList<ModelCapabilityView>> LoadEnabledAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelCapabilityView>>(_providers
                .Where(p => p.OrganizationId is null || (organizationId is not null && p.OrganizationId == organizationId))
                .ToList());

        public Task<IReadOnlyDictionary<Guid, Guid?>> LoadAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult(_overrides);

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadOrgAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult(organizationId is Guid org && _orgOverrides.TryGetValue(org, out var map)
                ? map
                : new Dictionary<AgentType, Guid?>());

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadBuiltInAgentOverridesAsync(CancellationToken ct) =>
            Task.FromResult(_builtInOverrides);
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
