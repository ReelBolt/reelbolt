using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Moderation;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// Pins the plan's thresholds: block at &gt;= 0.85, review in [0.50, 0.85), allow below 0.50, a
/// hard-block category always blocks, and the user-facing reason never leaks a score or a raw
/// category id (the constraint that keeps the model's internals out of the product UI).
/// </summary>
public sealed class ModerationPolicyTests
{
    private static ContentModerationOptions Options() => new();

    private static ModerationClassification Classification(
        double topScore,
        string topCategory = "violence",
        bool flagged = false,
        IReadOnlyDictionary<string, bool>? categories = null) =>
        new(flagged,
            new Dictionary<string, double> { [topCategory] = topScore },
            categories ?? new Dictionary<string, bool>(),
            ["text"]);

    [Theory]
    [InlineData(0.85)]
    [InlineData(0.86)]
    [InlineData(1.0)]
    public void A_score_at_or_above_the_block_threshold_blocks(double score)
    {
        ModerationPolicyVerdict verdict = ModerationPolicy.Evaluate(
            Classification(score), Options(), ModerationContentKind.Text);

        verdict.Action.Should().Be(ModerationAction.Block);
        verdict.TopCategory.Should().Be("violence");
        verdict.TopScore.Should().Be(score);
        verdict.ReasonCode.Should().Be("violence");
        verdict.UserMessage.Should().Be("This content may contain violent content.");
    }

    [Theory]
    [InlineData(0.50)]
    [InlineData(0.60)]
    [InlineData(0.849)]
    public void A_score_in_the_mid_band_is_review_and_never_blocks(double score)
    {
        ModerationPolicyVerdict verdict = ModerationPolicy.Evaluate(
            Classification(score), Options(), ModerationContentKind.Text);

        verdict.Action.Should().Be(ModerationAction.Review);
        verdict.TopScore.Should().Be(score);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.10)]
    [InlineData(0.499)]
    public void A_score_below_the_review_threshold_allows(double score)
    {
        ModerationPolicyVerdict verdict = ModerationPolicy.Evaluate(
            Classification(score), Options(), ModerationContentKind.Text);

        verdict.Action.Should().Be(ModerationAction.Allow);
        verdict.ReasonCode.Should().BeNull();
        verdict.UserMessage.Should().BeEmpty();
    }

    [Fact]
    public void Sexual_minors_blocks_even_when_the_score_is_below_the_review_threshold()
    {
        // The hard-block category is checked on its FLAG, not only on its score: the model can mark
        // a category without a high confidence, and the plan says that must still block.
        ModerationClassification classification = new(
            Flagged: false,
            Scores: new Dictionary<string, double> { ["sexual/minors"] = 0.01 },
            FlaggedCategories: new Dictionary<string, bool> { ["sexual/minors"] = true },
            InputTypes: ["text"]);

        ModerationPolicyVerdict verdict = ModerationPolicy.Evaluate(
            classification, Options(), ModerationContentKind.Text);

        verdict.Action.Should().Be(ModerationAction.Block);
        verdict.UserMessage.Should().Be("This content may involve the sexualization of minors.");
    }

    [Fact]
    public void Sexual_minors_also_blocks_from_a_score_at_the_review_threshold_without_a_flag()
    {
        ModerationClassification classification = new(
            Flagged: false,
            Scores: new Dictionary<string, double> { ["sexual/minors"] = 0.50 },
            FlaggedCategories: new Dictionary<string, bool>(),
            InputTypes: ["text"]);

        ModerationPolicy.Evaluate(classification, Options(), ModerationContentKind.Text).Action
            .Should().Be(ModerationAction.Block);
    }

    [Fact]
    public void Non_positive_thresholds_fall_back_to_the_shipped_defaults()
    {
        ContentModerationOptions options = new() { BlockThreshold = 0, ReviewThreshold = -1 };

        ModerationPolicy.Evaluate(Classification(0.10), options, ModerationContentKind.Text).Action
            .Should().Be(ModerationAction.Allow, "a zero threshold must not block every request");
        ModerationPolicy.Evaluate(Classification(0.85), options, ModerationContentKind.Text).Action
            .Should().Be(ModerationAction.Block);
    }

    [Fact]
    public void An_empty_score_map_and_no_flags_is_unavailable_never_allow()
    {
        // A malformed or empty results[] array must not look clean. The publish gate fails closed
        // on this; the generation/ingest/text surfaces fail open through the gate's own stance.
        ModerationClassification classification = new(
            false, new Dictionary<string, double>(), new Dictionary<string, bool>(), []);

        ModerationPolicyVerdict verdict = ModerationPolicy.Evaluate(
            classification, Options(), ModerationContentKind.Image);

        verdict.Action.Should().Be(ModerationAction.Unavailable);
        verdict.ReasonCode.Should().Be("unclassified");
        verdict.TopCategory.Should().BeNull();
        verdict.UserMessage.Should().BeEmpty();
    }

    [Fact]
    public void A_flagged_result_without_scores_is_a_review_never_an_allow()
    {
        // The model's own threshold fired, but it returned no per-category scores. The verdict must
        // stay in the review band (queue only, blocks nobody), never fall through to Allow.
        ModerationClassification classification = new(
            Flagged: true,
            Scores: new Dictionary<string, double>(),
            FlaggedCategories: new Dictionary<string, bool>(),
            InputTypes: ["image"]);

        ModerationPolicy.Evaluate(classification, Options(), ModerationContentKind.Image).Action
            .Should().Be(ModerationAction.Review);
    }

    [Fact]
    public void A_flagged_non_hard_block_category_is_at_least_a_review()
    {
        // A flagged category whose score is below our review threshold must still not Allow.
        ModerationClassification classification = new(
            Flagged: true,
            Scores: new Dictionary<string, double> { ["violence"] = 0.10 },
            FlaggedCategories: new Dictionary<string, bool> { ["violence"] = true },
            InputTypes: ["image"]);

        ModerationPolicy.Evaluate(classification, Options(), ModerationContentKind.Image).Action
            .Should().Be(ModerationAction.Review);
    }

    [Fact]
    public void A_flagged_hard_block_category_without_scores_blocks()
    {
        ModerationClassification classification = new(
            Flagged: false,
            Scores: new Dictionary<string, double>(),
            FlaggedCategories: new Dictionary<string, bool> { ["sexual/minors"] = true },
            InputTypes: ["text"]);

        ModerationPolicy.Evaluate(classification, Options(), ModerationContentKind.Text).Action
            .Should().Be(ModerationAction.Block);
    }

    [Theory]
    [InlineData("sexual", ModerationContentKind.Image, "This image may contain sexual content.")]
    [InlineData("sexual", ModerationContentKind.Text, "This content may contain sexual content.")]
    [InlineData("sexual", ModerationContentKind.Video, "This content may contain sexual content.")]
    [InlineData("violence/graphic", ModerationContentKind.Text, "This content may contain graphic violence.")]
    [InlineData("hate", ModerationContentKind.Text, "This content may contain hateful content.")]
    [InlineData("hate/threatening", ModerationContentKind.Text, "This content may contain hateful content.")]
    [InlineData("harassment", ModerationContentKind.Text, "This content may contain harassing content.")]
    [InlineData("harassment/threatening", ModerationContentKind.Text, "This content may contain harassing content.")]
    [InlineData("self-harm/intent", ModerationContentKind.Text, "This content may reference self-harm.")]
    [InlineData("illicit/violent", ModerationContentKind.Text, "This content may reference illegal activity.")]
    [InlineData("sexual/minors", ModerationContentKind.Text, "This content may involve the sexualization of minors.")]
    [InlineData("some/new/category", ModerationContentKind.Text, "This content may violate the content policy.")]
    public void The_user_message_is_a_plain_surface_named_sentence(
        string category, ModerationContentKind kind, string expected)
    {
        ModerationReasons.For(category, kind).Should().Be(expected);
    }

    [Theory]
    [InlineData("violence")]
    [InlineData("sexual/minors")]
    [InlineData("harassment/threatening")]
    [InlineData("unknown-category")]
    public void The_user_message_never_contains_a_score_a_raw_category_id_or_the_model_name(string category)
    {
        string message = ModerationReasons.For(category, ModerationContentKind.Image);

        message.Should().NotContainAny("0.", "1.", "85", "%");
        message.Should().NotContain("/", "category", "omni-moderation", "score");
    }

    [Fact]
    public void The_message_for_a_block_contains_no_digits_even_though_the_verdict_has_a_score()
    {
        ModerationPolicyVerdict verdict = ModerationPolicy.Evaluate(
            Classification(0.97, "violence/graphic"), Options(), ModerationContentKind.Text);

        verdict.Action.Should().Be(ModerationAction.Block);
        verdict.UserMessage.Should().Be("This content may contain graphic violence.");
        verdict.UserMessage.Any(char.IsDigit).Should().BeFalse();
        verdict.UserMessage.Should().NotContain("violence/graphic");
    }

    [Fact]
    public void The_highest_score_wins_regardless_of_dictionary_order()
    {
        ModerationClassification classification = new(
            false,
            new Dictionary<string, double> { ["hate"] = 0.20, ["violence"] = 0.90, ["sexual"] = 0.40 },
            new Dictionary<string, bool>(),
            ["text"]);

        ModerationPolicyVerdict verdict = ModerationPolicy.Evaluate(
            classification, Options(), ModerationContentKind.Text);

        verdict.TopCategory.Should().Be("violence");
        verdict.TopScore.Should().Be(0.90);
        verdict.Action.Should().Be(ModerationAction.Block);
    }
}
