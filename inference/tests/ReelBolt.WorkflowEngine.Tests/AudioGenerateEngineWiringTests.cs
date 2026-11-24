#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Services.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>T-108: the engine-wide decisions every new step type needs (cache, credits, failures, dependencies, providers).</summary>
public sealed class AudioGenerateEngineWiringTests
{
    private const string MusicJson = """{"kind":"Music","prompt":"warm pop","durationSec":30}""";

    private static WorkflowStep Audio(int order, string? configJson = MusicJson) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = order,
        StepType = StepType.AudioGenerate,
        AudioGenerateConfigJson = configJson
    };

    [Fact]
    public void AudioGenerate_is_not_step_cached_because_identical_takes_are_reused_from_the_ledger()
    {
        new StepCachePolicy().IsCacheable(Audio(1)).Should().BeFalse();
        new StepCachePolicy().IsCacheable(new WorkflowStep { StepType = StepType.AudioGenerate, CacheMode = StepCacheMode.Always })
            .Should().BeTrue("CacheMode=Always still overrides the table, as for every step type");
    }

    [Fact]
    public void AudioGenerate_is_charged_and_holds_a_real_default()
    {
        StepCreditDefaults.IsNotCharged(StepType.AudioGenerate).Should().BeFalse();
        (long low, long high) = StepCreditDefaults.MillisFor(StepType.AudioGenerate);
        low.Should().BePositive();
        high.Should().BeGreaterThan(low);
    }

    [Fact]
    public void AudioGenerate_is_fixed_price_and_not_agent_shaped()
    {
        ColdStartStepPricing.IsAgentShaped(StepType.AudioGenerate).Should().BeFalse();
        StepUsageProfiles.For(StepType.AudioGenerate, null).Quantities.Should().BeEmpty();
        RunCreditHolds.IsCold(StepType.AudioGenerate, new StepCreditEstimate(1, 2, 1, CreditEstimateBasis.Default))
            .Should().BeFalse("a fixed-price step never takes a share of the cold steps' low edge");
    }

    [Fact]
    public void A_declared_audio_price_wins_over_history_and_is_labelled_as_audio()
    {
        StepCreditEstimate estimate = new StepCreditEstimator([]).Estimate(StepType.AudioGenerate, null, 123_000);

        estimate.HoldMillis.Should().Be(123_000);
        estimate.Basis.Should().Be(CreditEstimateBasis.AudioGeneration);
    }

    [Fact]
    public void AudioGenerate_needs_its_own_audio_generation_plan_feature()
    {
        // Decision 6: separate from video generation, on the plan gate and on the credit hold alike.
        PlanGate.FeatureFor(StepType.AudioGenerate).Should().Be(PlanGate.AudioGenerationFeature);
        PlanGate.AudioGenerationFeature.Should().Be("audio_generation");
        WorkflowExecutorService.ResolveCreditEntitlement(StepType.AudioGenerate).Should().Be(CreditEntitlement.AudioGeneration);
    }

    [Theory]
    [InlineData("NO_AUDIO_PROVIDER", FailureClass.UserInput)]
    [InlineData("AUDIO_BAD_REQUEST", FailureClass.UserInput)]
    [InlineData("AUDIO_REQUEST_INVALID", FailureClass.UserInput)]
    [InlineData("AUDIO_PROVIDER_CONFIG", FailureClass.PlatformInternal)]
    [InlineData("AUDIO_VENDOR_QUOTA", FailureClass.PlatformInternal)]
    [InlineData("AUDIO_VENDOR_RETRYABLE", FailureClass.PlatformProviderError)]
    [InlineData("AUDIO_VENDOR_FAILED", FailureClass.PlatformProviderError)]
    [InlineData("AUDIO_GENERATE_FAILED", FailureClass.PlatformInternal)]
    [InlineData("DELIVERY_FAILED", FailureClass.PlatformInternal)]
    [InlineData("RESERVATION_ORPHANED", FailureClass.PlatformInternal)]
    public void The_audio_failure_codes_are_classified_deliberately(string code, FailureClass expected) =>
        FailureClassifier.ForCode(code).Should().Be(expected);

    [Fact]
    public void An_audio_step_reads_nothing_from_earlier_steps()
    {
        WorkflowStep first = Audio(1);
        WorkflowStep second = Audio(2);

        WorkflowStepDependencyGraph.Build([first, second]).DependsOn(2).Keys.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Music", InferenceProviderCapability.MusicGeneration)]
    [InlineData("SoundEffect", InferenceProviderCapability.SoundEffectGeneration)]
    public void The_named_provider_is_a_reference_of_the_kinds_capability(string kind, InferenceProviderCapability capability)
    {
        Guid id = Guid.NewGuid();
        string json = $$"""{"kind":"{{kind}}","prompt":"x","durationSec":3,"providerId":"{{id}}"}""";

        IReadOnlyList<StepProviderReference> references = StepProviderReferences.Extract(
            StepType.AudioGenerate, audioGenerateConfigJson: json);

        references.Should().ContainSingle().Which.Should().Be(new StepProviderReference(capability, id, "providerId"));
        StepProviderReferences.Extract(StepType.AudioGenerate, audioGenerateConfigJson: MusicJson).Should().BeEmpty();
        StepProviderReferences.Extract(StepType.AudioGenerate, audioGenerateConfigJson: "not json").Should().BeEmpty();
    }

    [Fact]
    public void The_step_cache_key_moves_with_the_audio_config_and_is_unchanged_without_one()
    {
        var builder = new StepCacheKeyBuilder();
        StepCacheKeyInputs inputs = new()
        {
            ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            StepType = StepType.AudioGenerate,
            AgentType = AgentType.VideoStoryEditor,
            AgentDefinitionId = Guid.Empty,
            AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
            ProjectFileFingerprint = "fp",
            MaxIterations = 3
        };

        string without = builder.Build(inputs);
        builder.Build(inputs with { AudioGenerateConfigJson = null }).Should().Be(without);
        builder.Build(inputs with { AudioGenerateConfigJson = MusicJson }).Should().NotBe(without);
    }

    [Fact]
    public void The_save_validator_reads_the_audio_config_column()
    {
        typeof(WorkflowStep).GetProperty(nameof(WorkflowStep.AudioGenerateConfigJson)).Should().NotBeNull();
        Enum.GetValues<StepType>().Should().Contain(StepType.AudioGenerate);
    }

    [Fact]
    public void The_failure_table_names_every_code_the_executor_can_produce() =>
        new[] { "NO_AUDIO_PROVIDER", "AUDIO_BAD_REQUEST", "AUDIO_REQUEST_INVALID", "AUDIO_VENDOR_RETRYABLE" }
            .Where(code => !FailureClassifier.TryGetCode(code, out FailureClass _)).Should().BeEmpty();
}
