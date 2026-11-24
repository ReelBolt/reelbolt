#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>The one rule that decides which provider and model a VideoGenerate step runs on (talking head -> LipSync).</summary>
public class LipSyncStepResolutionTests
{
    private const string Hint = "minimax/h3-max/lip-sync/image-to-video";
    private const string RowModel = "fal-ai/kling-video/ai-avatar/v2/standard";

    private static ResolvedVideoGenerationProvider Provider(string model) =>
        new(Guid.NewGuid(), "p", InferenceProviderKind.FalAi, "https://queue.fal.run", model, "k", 60);

    private static VideoGenerateStepConfig Config(bool avatarAudio, Guid? providerId = null) => new()
    {
        Model = Hint,
        ProviderId = providerId,
        AvatarAudio = avatarAudio ? new ExtractInputRef(ExtractInputSource.Step, 1) : null
    };

    [Fact]
    public void Only_a_step_with_avatar_audio_is_a_lip_sync_step_and_it_names_the_LipSync_capability()
    {
        LipSyncStepResolution.IsLipSync(Config(true)).Should().BeTrue();
        LipSyncStepResolution.CapabilityFor(Config(true)).Should().Be(InferenceProviderCapability.LipSync);
        LipSyncStepResolution.IsLipSync(Config(false)).Should().BeFalse();
        LipSyncStepResolution.CapabilityFor(Config(false)).Should().Be(InferenceProviderCapability.VideoGeneration);
        LipSyncStepResolution.IsLipSync(new VideoGenerateStepConfig { AvatarAudioProjectFileId = Guid.NewGuid() }).Should().BeTrue();
    }

    [Fact]
    public async Task A_lip_sync_step_asks_only_the_LipSync_resolver_and_a_plain_step_only_the_video_one()
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        Guid id = Guid.NewGuid();
        ResolvedVideoGenerationProvider lipSync = Provider(RowModel);
        ResolvedVideoGenerationProvider video = Provider("MiniMax-H3");
        resolver.Setup(r => r.ResolveLipSyncAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(lipSync);
        resolver.Setup(r => r.ResolveVideoGenerationAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(video);

        (await LipSyncStepResolution.ResolveAsync(resolver.Object, Config(true, id), default)).Should().BeSameAs(lipSync);
        (await LipSyncStepResolution.ResolveAsync(resolver.Object, Config(false), default)).Should().BeSameAs(video);
        resolver.Verify(r => r.ResolveVideoGenerationAsync(id, It.IsAny<CancellationToken>()), Times.Never);
        resolver.Verify(r => r.ResolveLipSyncAsync(null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_lip_sync_step_with_no_LipSync_row_resolves_to_none_and_never_the_video_default()
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveLipSyncAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync((ResolvedVideoGenerationProvider?)null);
        resolver.Setup(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Provider("MiniMax-H3"));

        (await LipSyncStepResolution.ResolveAsync(resolver.Object, Config(true), default)).Should().BeNull();
        resolver.Verify(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void The_effective_model_is_the_steps_when_a_provider_is_bound_else_the_capabilitys_own_for_lip_sync_only()
    {
        ResolvedVideoGenerationProvider row = Provider(RowModel);
        LipSyncStepResolution.EffectiveModel(Config(true), row).Should().Be(RowModel, "no providerId: the template's model is only a hint");
        LipSyncStepResolution.EffectiveModel(Config(true, row.ProviderId), row).Should().Be(Hint, "the app binder wrote both providerId and model");
        LipSyncStepResolution.EffectiveModel(Config(true, Guid.NewGuid()), row).Should().Be(RowModel,
            "a providerId that did not resolve to itself (a VideoGeneration row, or a disabled one) fell through to the default: its own model, never the step's");
        LipSyncStepResolution.EffectiveModel(Config(false), Provider("other")).Should().Be(Hint, "a plain step is unchanged");
    }

    [Fact]
    public void A_talking_heads_provider_reference_is_a_LipSync_reference_and_a_plain_steps_stays_VideoGeneration()
    {
        Guid id = Guid.NewGuid();
        string talking = "{\"planSource\":\"Inline\",\"providerId\":\"" + id + "\",\"avatarAudio\":{\"from\":\"Step\",\"stepOrder\":1}}";
        string plain = "{\"planSource\":\"Inline\",\"providerId\":\"" + id + "\"}";

        StepProviderReferences.Extract(StepType.VideoGenerate, null, null, null, talking, null, null, null, null, null, null)
            .Should().ContainSingle(r => r.Field == "providerId" && r.ProviderId == id && r.Capability == InferenceProviderCapability.LipSync);
        StepProviderReferences.Extract(StepType.VideoGenerate, null, null, null, plain, null, null, null, null, null, null)
            .Should().ContainSingle(r => r.Field == "providerId" && r.ProviderId == id && r.Capability == InferenceProviderCapability.VideoGeneration);
    }

    [Fact]
    public void An_unbound_lip_sync_step_gets_the_models_own_resolution_ratio_and_duration_but_a_bound_one_keeps_its_own()
    {
        ResolvedVideoGenerationProvider kling = Provider(RowModel);
        VideoGenerateStepConfig unbound = Config(true);
        unbound.Resolution = "768P";
        unbound.Ratio = "9:16";
        unbound.DurationSeconds = 15;
        LipSyncStepResolution.Apply(unbound, kling);
        unbound.Model.Should().Be(RowModel);
        unbound.Resolution.Should().Be(FalVideoCatalog.NativeToken, "Kling's avatar model offers only native");
        unbound.Ratio.Should().Be("9:16", "offered by the model, so kept");

        VideoGenerateStepConfig bound = Config(true, kling.ProviderId);
        bound.Resolution = "768P";
        LipSyncStepResolution.Apply(bound, kling);
        bound.Resolution.Should().Be("768P", "a bound row's own settings are the author's; a mismatch is refused, not moved");

        VideoGenerateStepConfig plain = Config(false);
        plain.Resolution = "768P";
        LipSyncStepResolution.Apply(plain, Provider("other"));
        plain.Resolution.Should().Be("768P");
    }
}
