#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>T-108: <see cref="AudioGenerateStepConfig"/> parsing and the bounds it mirrors from the audio model catalogue.</summary>
public sealed class AudioGenerateStepConfigTests
{
    private static AudioGenerateStepConfig Music(double duration = 30, string prompt = "Warm uplifting pop, no vocals") =>
        new(AudioGenerationKind.Music, prompt, duration, Instrumental: true);

    private static AudioGenerateStepConfig Sfx(double duration = 1.5) =>
        new(AudioGenerationKind.SoundEffect, "A short whoosh", duration, Loop: false, PromptInfluence: 0.3);

    [Fact]
    public void Defaults_are_one_take_registered_as_a_project_file()
    {
        AudioGenerateStepConfig config = JsonSerializer.Deserialize<AudioGenerateStepConfig>(
            """{"kind":"Music","prompt":"x","durationSec":30}""", AudioGenerateStepConfig.JsonOptions)!;

        config.Kind.Should().Be(AudioGenerationKind.Music);
        config.Takes.Should().Be(1);
        config.RegisterProjectFile.Should().BeTrue();
        config.Validate().Should().BeEmpty();
    }

    [Fact]
    public void A_valid_music_and_a_valid_sound_effect_have_no_errors()
    {
        Music().Validate().Should().BeEmpty();
        Sfx().Validate().Should().BeEmpty();
    }

    [Theory]
    [InlineData(2.9)]
    [InlineData(300.5)]
    [InlineData(double.NaN)]
    public void Music_duration_is_bounded_by_the_catalogue(double duration) =>
        Music(duration).Validate().Should().ContainSingle(e => e.Contains("DurationSec"));

    [Theory]
    [InlineData(0.4)]
    [InlineData(30.5)]
    public void Sound_effect_duration_is_bounded_by_the_catalogue(double duration) =>
        Sfx(duration).Validate().Should().ContainSingle(e => e.Contains("DurationSec"));

    [Fact]
    public void Prompt_is_required_and_capped_at_4000_characters()
    {
        Music(prompt: "  ").Validate().Should().ContainSingle(e => e.Contains("Prompt"));
        Music(prompt: new string('a', 4001)).Validate().Should().ContainSingle(e => e.Contains("4000"));
        Music(prompt: new string('a', 4000)).Validate().Should().BeEmpty();
    }

    [Fact]
    public void Takes_are_one_to_four()
    {
        (Music() with { Takes = 0 }).Validate().Should().ContainSingle(e => e.Contains("Takes"));
        (Music() with { Takes = 5 }).Validate().Should().ContainSingle(e => e.Contains("Takes"));
        (Music() with { Takes = 4 }).Validate().Should().BeEmpty();
    }

    [Fact]
    public void Fields_of_the_other_kind_are_refused()
    {
        (Music() with { Loop = true }).Validate().Should().ContainSingle(e => e.Contains("Loop"));
        (Music() with { PromptInfluence = 0.5 }).Validate().Should().ContainSingle(e => e.Contains("PromptInfluence"));
        (Sfx() with { Instrumental = true }).Validate().Should().ContainSingle(e => e.Contains("Instrumental"));
        (Sfx() with { CompositionPlanJson = "{}" }).Validate().Should().Contain(e => e.Contains("CompositionPlanJson"));
    }

    [Fact]
    public void Prompt_influence_is_zero_to_one() =>
        (Sfx() with { PromptInfluence = 1.5 }).Validate().Should().ContainSingle(e => e.Contains("PromptInfluence"));

    [Fact]
    public void Spend_limit_and_provider_id_must_be_meaningful()
    {
        (Music() with { MaxSpendCredits = 0 }).Validate().Should().ContainSingle(e => e.Contains("MaxSpendCredits"));
        (Music() with { ProviderId = Guid.Empty }).Validate().Should().ContainSingle(e => e.Contains("ProviderId"));
    }

    [Fact]
    public void A_model_must_be_catalogued_for_the_kind()
    {
        (Music() with { Model = "fal-ai/elevenlabs/music" }).Validate().Should().BeEmpty();
        (Music() with { Model = "fal-ai/elevenlabs/sound-effects/v2" }).Validate().Should().ContainSingle(e => e.Contains("Model"));
        (Sfx() with { Model = "made-up" }).Validate().Should().ContainSingle(e => e.Contains("Model"));
    }

    [Theory]
    [InlineData("music_bed")]
    [InlineData("Intro-1")]
    public void A_safe_file_name_prefix_is_accepted(string prefix) =>
        (Music() with { FileNamePrefix = prefix }).Validate().Should().BeEmpty();

    [Theory]
    [InlineData("../x")]
    [InlineData("a b")]
    [InlineData("")]
    public void An_unsafe_file_name_prefix_is_refused(string prefix) =>
        (Music() with { FileNamePrefix = prefix }).Validate().Should().ContainSingle(e => e.Contains("FileNamePrefix"));

    [Fact]
    public void A_composition_plan_replaces_the_prompt_and_the_duration()
    {
        AudioGenerateStepConfig plan = new(AudioGenerationKind.Music, "", 0, CompositionPlanJson: """{"chunks":[]}""");
        plan.Validate().Should().BeEmpty();

        (plan with { Prompt = "also a prompt" }).Validate().Should().ContainSingle(e => e.Contains("Prompt"));
        (plan with { DurationSec = 30 }).Validate().Should().ContainSingle(e => e.Contains("DurationSec"));
        (plan with { CompositionPlanJson = "[1]" }).Validate().Should().ContainSingle(e => e.Contains("CompositionPlanJson"));
    }

    [Fact]
    public void ValidateFor_applies_the_chosen_endpoints_own_range_and_fields()
    {
        AudioEndpoint falSfx = AudioModelCatalog.Instance.Find(InferenceProviderKind.FalAi, "fal-ai/elevenlabs/sound-effects/v2")!;
        AudioEndpoint mmaudio = AudioModelCatalog.Instance.Find(InferenceProviderKind.FalAi, "fal-ai/mmaudio-v2/text-to-audio")!;

        Sfx(22).ValidateFor(falSfx).Should().BeEmpty();
        Sfx(25).ValidateFor(falSfx).Should().ContainSingle(e => e.Contains("22"));
        Sfx(5).ValidateFor(mmaudio).Should().Contain(e => e.Contains("Loop")).And.Contain(e => e.Contains("PromptInfluence"));
    }

    [Fact]
    public void ResolveModel_prefers_the_step_then_the_row_then_the_catalogue_default()
    {
        (Music() with { Model = "music_v2" }).ResolveModel(InferenceProviderKind.ElevenLabs, "music_v2_5").Should().Be("music_v2");
        Music().ResolveModel(InferenceProviderKind.ElevenLabs, "music_v2").Should().Be("music_v2");
        Music().ResolveModel(InferenceProviderKind.ElevenLabs, "eleven_text_to_sound_v2").Should().Be("music_v2_5");
        Sfx().ResolveModel(InferenceProviderKind.FalAi, null).Should().Be("fal-ai/elevenlabs/sound-effects/v2");
        Sfx().ResolveModel(InferenceProviderKind.MiniMax, null).Should().BeNull();
    }

    [Fact]
    public void The_priced_duration_is_the_plans_total_in_plan_mode()
    {
        AudioEndpoint falMusic = AudioModelCatalog.Instance.Find(InferenceProviderKind.FalAi, "fal-ai/elevenlabs/music")!;
        string plan = Inference.AudioFixtures.Text("fal-plan-sections.json");
        AudioGenerateStepConfig config = new(AudioGenerationKind.Music, "", 0, CompositionPlanJson: plan);

        config.ValidateFor(falMusic).Should().BeEmpty();
        config.PricedDurationSec(falMusic).Should().BeGreaterThan(0);
        Music(45).PricedDurationSec(falMusic).Should().Be(45);
    }

    [Fact]
    public void ToRequest_sends_the_plan_without_prompt_or_duration()
    {
        AudioGenerateStepConfig config = new(AudioGenerationKind.Music, "", 0, CompositionPlanJson: """{"chunks":[]}""");
        AudioGenerationRequest request = config.ToRequest("music_v2_5");

        request.Prompt.Should().BeNull();
        request.DurationSec.Should().BeNull();
        request.CompositionPlanJson.Should().NotBeNull();
        request.Model.Should().Be("music_v2_5");

        Music(30).ToRequest("music_v2_5").DurationSec.Should().Be(30);
    }

    [Fact]
    public void Every_config_property_is_described_for_the_schema()
    {
        var schema = new StepConfigSchemaExporter().GetSchema(StepType.AudioGenerate);

        schema.HasConfig.Should().BeTrue();
        schema.Schema!["properties"]!.AsObject().Select(p => p.Key).Should().Contain(["kind", "prompt", "durationSec", "takes"]);
    }
}
