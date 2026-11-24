using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Issue #93 phases 4-5 appended five fields to <see cref="VideoGenerateStepConfig"/>. These tests pin
/// their defaults (all inert), the kind-aware cap rule (USD for a priced vendor, GPU-minutes for
/// ComfyUI — exactly one of them is meaningful, and <c>ValidateForKind</c> says which) and the
/// conform-pass bounds.
/// </summary>
public class VideoGenerateStepConfigPhase5Tests
{
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static VideoGenerateStepConfig Valid() => new()
    {
        PlanSource = VideoGeneratePlanSource.Inline,
        InlinePrompt = "A lighthouse at dusk",
        Model = "MiniMax-H3",
        Resolution = "768P",
        Ratio = "16:9",
        DurationSeconds = 6,
        Takes = 1,
        MaxClips = 1,
        MaxSpendCredits = 1m
    };

    [Fact]
    public void Appended_fields_default_to_inert_values()
    {
        var config = new VideoGenerateStepConfig();

        config.LookMatching.Should().BeFalse();
        config.CameraContinuity.Should().BeFalse();
        config.MaxGpuMinutes.Should().Be(0m);
        config.ConformFps.Should().Be(0);
        config.ConformHeight.Should().Be(0);
    }

    [Fact]
    public void A_pre_phase_5_literal_still_deserializes_with_the_appended_fields_at_their_defaults()
    {
        const string json = """{"planSource":"Inline","inlinePrompt":"x","model":"MiniMax-H3","resolution":"768P","durationSeconds":6,"ratio":"16:9","takes":1,"maxClips":1,"maxSpendCredits":1.0}""";

        var config = JsonSerializer.Deserialize<VideoGenerateStepConfig>(json, ConfigJsonOptions)!;

        config.LookMatching.Should().BeFalse();
        config.MaxGpuMinutes.Should().Be(0m);
        config.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Validate_keeps_the_exact_legacy_message_when_both_caps_are_zero()
    {
        var config = Valid();
        config.MaxSpendCredits = 0m;

        config.Validate().Should().Contain("MaxSpendCredits must be positive (greater than 0)");
    }

    [Fact]
    public void Validate_accepts_a_GPU_only_budget_with_MaxSpendCredits_zero()
    {
        var config = Valid();
        config.MaxSpendCredits = 0m;
        config.MaxGpuMinutes = 5m;
        config.Model = "wan2.2-ti2v-5b-t2v";
        config.Resolution = "480P";
        config.DurationSeconds = 3;

        config.Validate().Should().BeEmpty("a ComfyUI step bills $0 and budgets GPU minutes instead");
    }

    [Fact]
    public void Validate_rejects_a_negative_MaxGpuMinutes()
    {
        var config = Valid();
        config.MaxGpuMinutes = -1m;

        config.Validate().Should().ContainSingle(e => e.Contains("MaxGpuMinutes"));
    }

    [Fact]
    public void ValidateForKind_ComfyUI_requires_GPU_minutes_and_a_priced_vendor_requires_USD()
    {
        var gpuOnly = Valid();
        gpuOnly.MaxSpendCredits = 0m;
        gpuOnly.MaxGpuMinutes = 5m;

        gpuOnly.ValidateForKind(InferenceProviderKind.ComfyUI).Should().BeEmpty();
        gpuOnly.ValidateForKind(InferenceProviderKind.MiniMax).Should().ContainSingle(e => e.Contains("MaxSpendCredits"));
        gpuOnly.ValidateForKind(InferenceProviderKind.Higgsfield).Should().ContainSingle(e => e.Contains("MaxSpendCredits"));

        var usdOnly = Valid();
        usdOnly.ValidateForKind(InferenceProviderKind.MiniMax).Should().BeEmpty();
        usdOnly.ValidateForKind(InferenceProviderKind.ComfyUI).Should().ContainSingle(e => e.Contains("MaxGpuMinutes"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(121)]
    public void Validate_rejects_a_ConformFps_outside_0_or_1_to_120(int fps)
    {
        var config = Valid();
        config.ConformFps = fps;

        config.Validate().Should().ContainSingle(e => e.Contains("ConformFps"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(120)]
    public void Validate_accepts_a_ConformFps_of_0_or_1_to_120(int fps)
    {
        var config = Valid();
        config.ConformFps = fps;

        config.Validate().Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(15)]
    [InlineData(4321)]
    public void Validate_rejects_a_ConformHeight_outside_0_or_16_to_4320(int height)
    {
        var config = Valid();
        config.ConformHeight = height;

        config.Validate().Should().ContainSingle(e => e.Contains("ConformHeight"));
    }

    [Fact]
    public void Validate_requires_Analysis_when_look_matching_is_asked_of_a_plan_sourced_step()
    {
        var config = Valid();
        config.PlanSource = VideoGeneratePlanSource.StepRef;
        config.Plan = new ExtractInputRef(ExtractInputSource.Step, StepOrder: 3);
        config.Analysis = null;
        config.LookMatching = true;

        List<string> errors = config.Validate();

        errors.Should().ContainSingle(e => e.Contains("Analysis") && e.Contains("LookMatching"));

        config.Analysis = new ExtractInputRef(ExtractInputSource.Step, StepOrder: 1);
        config.Validate().Should().BeEmpty();
    }
}
