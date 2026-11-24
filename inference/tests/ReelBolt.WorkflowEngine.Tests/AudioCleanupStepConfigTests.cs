using System;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The AudioCleanup config: validation names the bad field, the wire shape is camelCase, local vs hosted is by target.</summary>
public sealed class AudioCleanupStepConfigTests
{
    private static readonly VideoSourceRef File = new(VideoSourceKind.ProjectFile, ProjectFileId: Guid.NewGuid());

    private static AudioCleanupStepConfig Config(
        AudioCleanupTarget target = AudioCleanupTarget.Hum,
        VideoSourceRef? source = null,
        Guid? providerId = null,
        int? maxSourceMinutes = null,
        AudioCleanupOutputKind outputKind = AudioCleanupOutputKind.Auto) =>
        new(target, source ?? File, providerId, false, true, outputKind, maxSourceMinutes);

    [Theory]
    [InlineData(AudioCleanupTarget.VoiceBasic, false)]
    [InlineData(AudioCleanupTarget.Hum, false)]
    [InlineData(AudioCleanupTarget.Wind, false)]
    [InlineData(AudioCleanupTarget.Hiss, false)]
    [InlineData(AudioCleanupTarget.Echo, false)]
    [InlineData(AudioCleanupTarget.IsolateVoice, true)]
    [InlineData(AudioCleanupTarget.RemoveMusic, true)]
    public void Only_the_two_ai_targets_are_hosted_and_call_a_provider(AudioCleanupTarget target, bool hosted)
    {
        AudioCleanupStepConfig config = Config(target);
        config.IsHosted.Should().Be(hosted);
        config.CallsAnyProvider.Should().Be(hosted);
        config.ToLocalTarget().HasValue.Should().Be(!hosted);
    }

    [Fact]
    public void Every_local_target_maps_to_the_local_enum_by_name()
    {
        foreach (AudioCleanupTarget target in Enum.GetValues<AudioCleanupTarget>().Where(t => !Config(t).IsHosted))
            Config(target).ToLocalTarget().ToString().Should().Be(target.ToString());
    }

    [Fact]
    public void A_plain_config_is_valid()
    {
        Config().Validate().Should().BeEmpty();
        Config(AudioCleanupTarget.IsolateVoice, providerId: Guid.NewGuid(), maxSourceMinutes: 10).Validate().Should().BeEmpty();
    }

    [Fact]
    public void A_bad_value_names_its_field()
    {
        Config(source: new VideoSourceRef(VideoSourceKind.GeneratedClips)).Validate().Should().ContainSingle().Which.Should().Contain("Source=GeneratedClips");
        Config(source: new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: Guid.NewGuid(), InSec: 1, OutSec: 2)).Validate().Should().ContainSingle().Which.Should().Contain("InSec/OutSec");
        Config(source: new VideoSourceRef(VideoSourceKind.StepResult)).Validate().Should().ContainSingle().Which.Should().Contain("StepResultId");
        Config(maxSourceMinutes: 0).Validate().Should().ContainSingle().Which.Should().Contain("MaxSourceMinutes");
        Config(maxSourceMinutes: AudioCleanupStepConfig.MaxSourceMinutesLimit + 1).Validate().Should().ContainSingle().Which.Should().Contain("MaxSourceMinutes");
        Config((AudioCleanupTarget)99).Validate().Should().ContainSingle().Which.Should().Contain("Target");
        Config(outputKind: (AudioCleanupOutputKind)9).Validate().Should().ContainSingle().Which.Should().Contain("OutputKind");
        Config(providerId: Guid.Empty).Validate().Should().ContainSingle().Which.Should().Contain("ProviderId");
    }

    [Fact]
    public void A_provider_id_is_only_valid_with_a_hosted_target()
    {
        Config(AudioCleanupTarget.Hiss, providerId: Guid.NewGuid()).Validate().Should().ContainSingle().Which.Should().Contain("ProviderId is only valid with IsolateVoice or RemoveMusic");
    }

    [Fact]
    public void The_wire_shape_is_camelCase_with_string_enums_and_the_consent_flag_the_guard_looks_for()
    {
        string json = JsonSerializer.Serialize(Config(AudioCleanupTarget.RemoveMusic), AudioCleanupStepConfig.JsonOptions);

        json.Should().Contain("\"target\":\"RemoveMusic\"").And.Contain("\"allowSourceMediaEgress\":false")
            .And.Contain("\"outputKind\":\"Auto\"").And.NotContain("isHosted").And.NotContain("callsAnyProvider");
        JsonSerializer.Deserialize<AudioCleanupStepConfig>(json, AudioCleanupStepConfig.JsonOptions)!.Target.Should().Be(AudioCleanupTarget.RemoveMusic);
    }

    [Fact]
    public void A_config_missing_its_required_fields_never_throws_from_validate()
    {
        AudioCleanupStepConfig? config = JsonSerializer.Deserialize<AudioCleanupStepConfig>("{\"target\":\"Hum\"}", AudioCleanupStepConfig.JsonOptions);
        config!.Validate().Should().ContainSingle().Which.Should().Be("Source is required.");
    }
}
