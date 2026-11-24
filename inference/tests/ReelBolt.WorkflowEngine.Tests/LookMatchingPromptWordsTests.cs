using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Issue #93 phase 4: look matching turns a real shot's MEASURED colour classes into a first-party
/// English clause; camera continuity turns its classified move into a planner camera word. Both are
/// pure tables — no number ever reaches the prompt, and an unclassified shot yields nothing at all.
/// </summary>
public class LookMatchingPromptWordsTests
{
    private static VideoAnalysisShotVisual Visual(string? temperature, string? tone, string? saturation, string camera = "Static") =>
        new(
            MotionMean: 0, MotionPeak: 0, MotionStdDev: 0, MotionClass: "Still", CameraMove: camera, CameraMoveConfidence: 1,
            HeadMotion: 0, TailMotion: 0, StillWindows: [], BrightnessMean: 0.5, BrightnessStdDev: 0.1, ContrastRms: 0.2,
            ClippedHighlightRatio: 0, CrushedBlackRatio: 0, SaturationMean: 0.4, DominantColors: [], Regions: [],
            BestOverlayRegion: null, ActiveCrop: null, Sharpness: null,
            KenBurnsCandidate: false, KenBurnsReason: null, DuplicateGroupId: null, GroupRank: null, IsBestTake: false,
            ColorTemperatureClass: temperature, ToneClass: tone, SaturationClass: saturation);

    [Fact]
    public void Describe_is_null_for_a_missing_or_unclassified_shot()
    {
        LookMatchingPromptWords.Describe(null).Should().BeNull();
        LookMatchingPromptWords.Describe(Visual(null, null, null)).Should().BeNull();
        LookMatchingPromptWords.Describe(Visual("Unknown", "Odd", "Weird")).Should().BeNull("an unknown class word is not guessed at");
    }

    [Fact]
    public void Describe_joins_the_three_classes_into_one_clause_with_the_pinned_prefix()
    {
        string? clause = LookMatchingPromptWords.Describe(Visual("Warm", "Contrasty", "Vivid"));

        clause.Should().StartWith(LookMatchingPromptWords.ClausePrefix);
        clause.Should().Be(
            "Match the look of the surrounding footage: warm colour temperature with amber highlights, high contrast with deep blacks, vivid, saturated colours.");
    }

    [Fact]
    public void Describe_omits_an_unclassified_axis_rather_than_inventing_one()
    {
        LookMatchingPromptWords.Describe(Visual("Cool", null, null))
            .Should().Be("Match the look of the surrounding footage: cool colour temperature with blue-leaning shadows.");
    }

    [Fact]
    public void Describe_never_contains_a_digit()
    {
        string? clause = LookMatchingPromptWords.Describe(Visual("Neutral", "Flat", "Muted"));

        clause.Should().NotBeNull();
        clause!.Should().NotMatchRegex("[0-9]", "a number in a prompt is exactly what this pipeline refuses to emit");
    }

    [Theory]
    [InlineData("Static", "Static")]
    [InlineData("handheld", "Handheld")]
    [InlineData("Pan", null)]
    [InlineData("Tilt", null)]
    [InlineData("Zoom", null)]
    [InlineData(null, null)]
    public void ContinuityCameraWord_maps_only_moves_that_carry_no_direction(string? measured, string? expected)
    {
        LookMatchingPromptWords.ContinuityCameraWord(measured is null ? null : Visual(null, null, null, measured))
            .Should().Be(expected);
    }
}
