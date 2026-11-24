using System;
using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing.Color;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>log-footage-color T-025: camera-profile versions reach the step cache key; skip reasons are plain.</summary>
public sealed class CameraProfileCacheKeyTests
{
    private static readonly IStepCacheKeyBuilder Builder = new StepCacheKeyBuilder();

    private static StepCacheKeyInputs Inputs(string? configJson) => new()
    {
        ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        StepType = StepType.VideoCompile,
        AgentType = AgentType.Custom,
        AgentDefinitionId = Guid.Empty,
        AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
        VideoCompileConfigJson = configJson,
        MaxIterations = 1,
        ProjectFileFingerprint = "fp",
        WorkflowDefinitionId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
        InputProfileRecipes = SourceCameraProfile.CacheKeyComponentFromJson(configJson)
    };

    private static string Key(StepCacheKeyInputs i) => Builder.Build(i);

    [Fact]
    public void No_profiles_keeps_the_key_explicit_and_auto_change_it()
    {
        const string none = "{\"mode\":\"Reencode\"}";
        const string explicitCfg = "{\"mode\":\"Reencode\",\"sourceInputProfiles\":{\"0\":\"sonySLog3\"}}";
        const string autoCfg = "{\"mode\":\"Reencode\",\"sourceInputProfiles\":{\"0\":\"auto\"}}";

        Inputs(none).InputProfileRecipes.Should().BeNull();
        Key(Inputs(none)).Should().Be(Key(Inputs(none) with { InputProfileRecipes = null }), "null is not hashed");
        Key(Inputs(explicitCfg)).Should().NotBe(Key(Inputs(explicitCfg) with { InputProfileRecipes = null }));
        // The config JSON itself differs too, so compare what the component alone contributes.
        Key(Inputs(autoCfg)).Should().NotBe(Key(Inputs(autoCfg) with { InputProfileRecipes = null }));
    }

    [Fact]
    public void The_rule_version_is_folded_only_for_auto()
    {
        string explicitComponent = SourceCameraProfile.CacheKeyComponent(new Dictionary<int, string> { [0] = "sonySLog3" })!;
        string autoComponent = SourceCameraProfile.CacheKeyComponent(new Dictionary<int, string> { [0] = "auto" })!;

        explicitComponent.Should().Contain(CameraProfileStage_RecipeVersion).And.Contain(CameraProfileTable.TableVersion)
            .And.NotContain(CameraProfileDetector.RuleVersion);
        autoComponent.Should().Contain(CameraProfileDetector.RuleVersion);

        // A bumped rule version changes the auto key and leaves the explicit one alone.
        StepCacheKeyInputs baseInputs = Inputs("{}");
        string autoKey = Key(baseInputs with { InputProfileRecipes = autoComponent });
        string bumpedAuto = Key(baseInputs with { InputProfileRecipes = autoComponent.Replace(CameraProfileDetector.RuleVersion, "camera-detect-v2") });
        string explicitKey = Key(baseInputs with { InputProfileRecipes = explicitComponent });
        autoKey.Should().NotBe(bumpedAuto);
        explicitKey.Should().Be(Key(baseInputs with { InputProfileRecipes = explicitComponent.Replace(CameraProfileDetector.RuleVersion, "camera-detect-v2") }));
    }

    private const string CameraProfileStage_RecipeVersion = CameraProfileVariants.RecipeVersion;

    [Theory]
    [InlineData(true, true, false, false, false, true, null)]
    [InlineData(false, false, false, false, false, true, null)]
    [InlineData(true, false, true, false, false, true, "still image")]
    [InlineData(true, false, false, true, false, true, "authored scenes")]
    [InlineData(true, false, false, false, true, true, "source's own pixels")]
    [InlineData(true, false, false, false, false, false, "no shot or visual analysis")]
    [InlineData(true, false, false, false, false, true, "could not be made")]
    public void A_resolved_profile_that_did_not_apply_names_its_reason(
        bool resolved, bool applied, bool still, bool authored, bool pixels, bool analyses, string? contains)
    {
        string? reason = VideoAnalyzeStepExecutor.InputProfileNotAppliedReasonFor(resolved, applied, still, authored, pixels, analyses);
        if (contains is null) reason.Should().BeNull();
        else reason.Should().Contain(contains);
    }
}
