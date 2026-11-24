using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Review;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Review;

/// <summary>
/// The closed allowlist of settings a review loop may change (docs/design/review-loop-adjustments.md): every entry maps
/// to a real config member of the right type, nothing on it touches consent, providers, models, spend, takes or prompts,
/// and values are range-checked.
/// </summary>
public class ReviewAdjustableSettingsTests
{
    [Fact]
    public void Every_allowlisted_setting_is_a_real_member_of_its_step_config_with_a_matching_type()
    {
        foreach (ReviewAdjustableSetting setting in ReviewAdjustableSettings.All)
        {
            Type configType = ReviewAdjustableSettings.ConfigType(setting.StepType)!;
            configType.Should().NotBeNull($"{setting.StepType} must have a config record");
            PropertyInfo? property = configType.GetProperty(setting.PropertyName);
            property.Should().NotBeNull($"{setting.Name} must be a member of {configType.Name}");
            Type type = Nullable.GetUnderlyingType(property!.PropertyType) ?? property.PropertyType;
            switch (setting.Kind)
            {
                case ReviewAdjustableValueKind.Integer:
                    type.Should().Be(typeof(int), setting.Name);
                    break;
                case ReviewAdjustableValueKind.Number:
                    type.Should().Be(typeof(double), setting.Name);
                    break;
                case ReviewAdjustableValueKind.Choice:
                    type.IsEnum.Should().BeTrue(setting.Name);
                    setting.Choices.Should().OnlyContain(c => Enum.GetNames(type).Contains(c), setting.Name);
                    break;
            }

            char.IsLower(setting.Name[0]).Should().BeTrue("names are the camelCase JSON names");
            ReviewAdjustableSettings.ForbiddenReason(setting.Name).Should().BeNull($"{setting.Name} must not look like a forbidden setting");
        }
    }

    [Fact]
    public void The_allowlist_is_exactly_the_agreed_list()
    {
        ReviewAdjustableSettings.All.Select(s => $"{s.StepType}.{s.Name}").Should().BeEquivalentTo(
        [
            "VideoCompile.overlayFontSizePct", "VideoCompile.overlayBoxHeightPct", "VideoCompile.maxSfxCueSeconds",
            "VideoCompile.targetDurationSec", "VideoCompile.minSegmentMs", "VideoCompile.outputFit", "VideoCompile.subjectSafeArea",
            "VideoCompile.captionFontSizePct", "VideoCompile.musicGainDb", "VideoCompile.transitionPolicy",
            "VideoAnalyze.maxOutputChars", "VideoAnalyze.maxSubShotsPerShot",
            "VideoCompile.minOverlayCapHeightPct",
        ]);
    }

    [Fact]
    public void No_paid_step_type_has_an_adjustable_setting()
    {
        foreach (StepType type in ReviewAdjustableSettings.NeverAdjustableStepTypes)
            ReviewAdjustableSettings.For(type).Should().BeEmpty();
        ReviewAdjustableSettings.All.Select(s => s.StepType).Distinct().Should().BeEquivalentTo([StepType.VideoCompile, StepType.VideoAnalyze]);
    }

    [Theory]
    [InlineData("allowSourceMediaEgress")]
    [InlineData("transcriptionProviderId")]
    [InlineData("visionProviderId")]
    [InlineData("maxSpendCredits")]
    [InlineData("takes")]
    [InlineData("inlinePrompt")]
    [InlineData("model")]
    [InlineData("projectVoiceId")]
    [InlineData("maxClips")]
    public void Consent_provider_model_spend_take_prompt_and_voice_settings_are_forbidden(string name)
    {
        ReviewAdjustableSettings.ForbiddenReason(name).Should().NotBeNull();
    }

    [Theory]
    [InlineData("overlayFontSizePct", "8", true)]
    [InlineData("overlayFontSizePct", "13", false)]
    [InlineData("overlayFontSizePct", "2", false)]
    [InlineData("overlayFontSizePct", "7.5", false)]
    [InlineData("maxSfxCueSeconds", "1.25", true)]
    [InlineData("maxSfxCueSeconds", "0.4", false)]
    [InlineData("minOverlayCapHeightPct", "4.5", true)]
    [InlineData("minOverlayCapHeightPct", "0.5", false)]
    [InlineData("musicGainDb", "-6", true)]
    [InlineData("musicGainDb", "3", false)]
    [InlineData("outputFit", "pad", true)]
    [InlineData("outputFit", "Stretch", false)]
    [InlineData("transitionPolicy", "Editor", false)]
    [InlineData("minSegmentMs", "abc", false)]
    public void Values_are_parsed_and_range_checked(string name, string raw, bool ok)
    {
        ReviewAdjustableSetting setting = ReviewAdjustableSettings.Find(StepType.VideoCompile, name)!;
        ReviewAdjustmentValue parsed = ReviewAdjustableSettings.Parse(setting, JsonValue.Create(raw), baseLengthSec: null);
        (parsed.Refused is null).Should().Be(ok, parsed.Refused);
    }

    [Fact]
    public void A_target_length_moves_at_most_a_quarter_around_its_base_and_needs_one()
    {
        ReviewAdjustableSetting setting = ReviewAdjustableSettings.Find(StepType.VideoCompile, "targetDurationSec")!;
        ReviewAdjustableSettings.Parse(setting, JsonValue.Create(37), 30).Refused.Should().BeNull();
        ReviewAdjustableSettings.Parse(setting, JsonValue.Create(38), 30).Refused.Should().NotBeNull();
        ReviewAdjustableSettings.Parse(setting, JsonValue.Create(23), 30).Refused.Should().BeNull();
        ReviewAdjustableSettings.Parse(setting, JsonValue.Create(22), 30).Refused.Should().NotBeNull();
        ReviewAdjustableSettings.Parse(setting, JsonValue.Create(30), null).Refused.Should().Contain("no target length");
    }

    [Fact]
    public void Apply_writes_one_member_and_keeps_every_other()
    {
        ReviewAdjustableSetting setting = ReviewAdjustableSettings.Find(StepType.VideoCompile, "overlayFontSizePct")!;
        const string config = """{"version":1,"enableGraphics":true,"OverlayFontSizePct":5,"minSegmentMs":800}""";

        string json = ReviewAdjustableSettings.Apply(setting, config, JsonValue.Create(9))!;

        JsonObject root = JsonNode.Parse(json)!.AsObject();
        root["overlayFontSizePct"]!.GetValue<int>().Should().Be(9);
        root.ContainsKey("OverlayFontSizePct").Should().BeFalse("one spelling per member");
        root["minSegmentMs"]!.GetValue<int>().Should().Be(800);
        root["enableGraphics"]!.GetValue<bool>().Should().BeTrue();
        ReviewAdjustableSettings.Display(ReviewAdjustableSettings.ReadCurrent(setting, json)).Should().Be("9");
    }

    [Fact]
    public void An_unset_optional_member_reads_as_unset()
    {
        ReviewAdjustableSetting setting = ReviewAdjustableSettings.Find(StepType.VideoCompile, "musicGainDb")!;
        ReviewAdjustableSettings.Display(ReviewAdjustableSettings.ReadCurrent(setting, "{}")).Should().Be("unset");
    }
}
