using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using AwesomeAssertions;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="ShotPlanParameterMapper"/> is the first-party C# that makes the rushcut invariant hold
/// for generated video: a <c>ShotDirector</c> plan emits only enum WORDS and opaque ids, and every
/// concrete provider parameter (duration in seconds, camera phrasing) is decided here. That makes
/// these pure functions the trust boundary — the model cannot express a duration, so these tests are
/// what proves the words resolve to the values the provider is actually billed for.
/// </summary>
public class ShotPlanParameterMapperTests
{
    /// <summary>
    /// The REAL capability table, via the production <c>Describe</c> path. Constructing the client
    /// performs no I/O — <c>Describe</c> is a pure switch — so this needs no network and no paid call.
    /// Using the real table rather than a hand-copied literal is the point: a stale literal would let
    /// the mapper and the shipped price/duration table drift apart unnoticed.
    /// </summary>
    private static readonly VideoGenerationCapabilities MiniMaxH3 =
        new MiniMaxVideoGenerationClient(new HttpClient(), "test-key-never-sent").Describe("MiniMax-H3");

    private static VideoGenerationCapabilities Caps(int minDuration, int maxDuration, params (string Res, decimal Price)[] prices)
    {
        var table = new Dictionary<string, decimal>();
        foreach (var (res, price) in prices)
        {
            table[res] = price;
        }

        return new VideoGenerationCapabilities(table, minDuration, maxDuration, new[] { "16:9" });
    }

    private static ShotPlan Plan(
        string duration = "Short", string camera = "Static", string? firstFrame = null, string? lastFrame = null) =>
        new()
        {
            Purpose = "Cutaway",
            Anchor = "s1",
            Camera = camera,
            Duration = duration,
            Prompt = "A chef plating pasta.",
            Reason = "Bridges the cut.",
            FirstFrame = firstFrame,
            LastFrame = lastFrame,
        };

    // ---------------------------------------------------------------------
    // SnapDurationSeconds — against the REAL MiniMax-H3 range
    // ---------------------------------------------------------------------

    [Fact]
    public void SnapDurationSeconds_Short_against_the_real_model_is_the_models_minimum()
    {
        ShotPlanParameterMapper.SnapDurationSeconds(MiniMaxH3, "Short").Should().Be(4,
            "MiniMax-H3's documented minimum is 4s; Short must snap UP-free to the shortest clip the model can actually produce, because a shorter request is rejected by the provider");
    }

    [Fact]
    public void SnapDurationSeconds_Medium_against_the_real_model_is_the_range_midpoint()
    {
        // 4 + (15 - 4) / 2 == 4 + 5 == 9, integer division.
        ShotPlanParameterMapper.SnapDurationSeconds(MiniMaxH3, "Medium").Should().Be(9,
            "Medium is defined as the integer midpoint of the model's own range, so it stays inside the range even after the range is revised");
    }

    [Fact]
    public void SnapDurationSeconds_Long_against_the_real_model_is_the_models_maximum()
    {
        ShotPlanParameterMapper.SnapDurationSeconds(MiniMaxH3, "Long").Should().Be(15,
            "MiniMax-H3's documented maximum is 15s; Long must snap to it rather than to some larger round number the provider would reject");
    }

    [Fact]
    public void SnapDurationSeconds_every_word_lands_inside_the_models_own_range()
    {
        // The general invariant behind the three exact values above: the mapper never asks a provider
        // for a duration that provider does not offer.
        foreach (string word in new[] { "Short", "Medium", "Long" })
        {
            int snapped = ShotPlanParameterMapper.SnapDurationSeconds(MiniMaxH3, word);

            snapped.Should().BeInRange(MiniMaxH3.MinDurationSeconds, MiniMaxH3.MaxDurationSeconds,
                $"'{word}' must land inside the model's real {MiniMaxH3.MinDurationSeconds}..{MiniMaxH3.MaxDurationSeconds}s range");
        }
    }

    [Fact]
    public void SnapDurationSeconds_model_whose_min_equals_its_max_snaps_every_word_to_that_value()
    {
        // A fixed-length model is a legitimate capability row; every word has exactly one legal answer.
        var fixedLength = Caps(6, 6, ("768P", 0.08m));

        foreach (string word in new[] { "Short", "Medium", "Long" })
        {
            ShotPlanParameterMapper.SnapDurationSeconds(fixedLength, word).Should().Be(6,
                $"a model offering only 6s must generate 6s for '{word}' — returning anything else would order a clip the model cannot produce");
        }
    }

    [Fact]
    public void SnapDurationSeconds_model_with_no_exact_midpoint_still_stays_in_range()
    {
        // 4 + (5 - 4) / 2 == 4 with integer division: flooring is fine, escaping the range is not.
        var narrow = Caps(4, 5, ("768P", 0.08m));

        ShotPlanParameterMapper.SnapDurationSeconds(narrow, "Medium").Should().Be(4,
            "integer division must floor the midpoint, never round outside the model's range");
    }

    [Theory]
    [InlineData("short")]
    [InlineData("SHORT")]
    [InlineData("  short  ")]
    [InlineData(" lOnG ")]
    public void SnapDurationSeconds_trims_and_ignores_case(string word)
    {
        // Model-authored output routinely carries surrounding whitespace or unexpected casing; that
        // noise cannot invent a duration because the word must still be one of the three pinned
        // spellings after trimming.
        int snapped = ShotPlanParameterMapper.SnapDurationSeconds(MiniMaxH3, word);

        new[] { 4, 9, 15 }.Should().Contain(snapped,
            $"'{word}' is one of the three pinned words after trimming and case folding, so it must resolve rather than throw");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Epic")]
    [InlineData("shorten")]
    [InlineData("4")]
    public void SnapDurationSeconds_unreadable_word_throws(string? word)
    {
        // The snapped duration drives BOTH the spend gate and the persisted ledger, so there is no
        // safe guess: a silent fallback would under-quote (passing a cap it should not) or
        // over-quote (refusing work that is affordable). It must fail closed instead.
        Action act = () => ShotPlanParameterMapper.SnapDurationSeconds(MiniMaxH3, word);

        act.Should().Throw<ArgumentException>(
            "'{0}' is not one of Short/Medium/Long, and the duration it would have produced drives real money so it is never defaulted",
            word ?? "null");
    }

    [Fact]
    public void SnapDurationSeconds_inverted_model_range_throws()
    {
        // A model whose max is below its min cannot produce a billable clip at all; pricing one
        // would be fiction.
        var inverted = Caps(15, 4, ("768P", 0.08m));

        Action act = () => ShotPlanParameterMapper.SnapDurationSeconds(inverted, "Short");

        act.Should().Throw<ArgumentException>(
            "a model with an inverted duration range cannot generate anything, so no duration may be snapped from it");
    }

    [Fact]
    public void SnapDurationSeconds_minimum_below_one_second_throws()
    {
        var subSecond = Caps(0, 10, ("768P", 0.08m));

        Action act = () => ShotPlanParameterMapper.SnapDurationSeconds(subSecond, "Short");

        act.Should().Throw<ArgumentOutOfRangeException>(
            "a sub-second minimum cannot be billed or generated, so snapping Short to it would persist a spend that never happened");
    }

    [Fact]
    public void SnapDurationSeconds_null_capabilities_throws()
    {
        Action act = () => ShotPlanParameterMapper.SnapDurationSeconds(null!, "Short");

        act.Should().Throw<ArgumentNullException>(
            "a null capability table means the resolved model was never resolved, which must surface rather than silently pick a duration");
    }

    // ---------------------------------------------------------------------
    // CameraPhraseFor
    // ---------------------------------------------------------------------

    [Fact]
    public void CameraPhraseFor_every_canonical_word_is_distinct_and_non_null()
    {
        // A duplicated phrase would silently collapse two different directorial intents into one
        // camera move; a null would drop one entirely.
        var phrases = ShotPlanParameterMapper.CameraWords
            .Select(word => ShotPlanParameterMapper.CameraPhraseFor(word))
            .ToList();

        phrases.Should().HaveCount(ShotPlanParameterMapper.CameraWords.Count,
            "every canonical camera word must have a clause, or the vocabulary would advertise a move the mapper cannot express");
        phrases.Should().NotContainNulls(
            "a canonical word with no clause would silently omit the camera direction from the prompt");
        phrases.Should().OnlyHaveUniqueItems(
            "two words resolving to the same clause would make distinct directorial intents indistinguishable to the provider");
    }

    [Fact]
    public void CameraPhraseFor_is_case_insensitive_and_trims()
    {
        string canonical = ShotPlanParameterMapper.CameraPhraseFor("PushIn")!;

        ShotPlanParameterMapper.CameraPhraseFor(" pushin ").Should().Be(canonical,
            "model-authored output routinely carries stray whitespace and casing, and neither changes which camera move was meant");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DollyZoom")]
    [InlineData("Pan")]
    public void CameraPhraseFor_unknown_or_blank_word_returns_null_without_throwing(string? word)
    {
        // Deliberate, documented degradation: a guess would put a camera move into the clip that no
        // human or agent asked for, whereas omitting the clause costs only a little directorial
        // control. Nothing downstream depends on the value, so it never throws.
        Action act = () => ShotPlanParameterMapper.CameraPhraseFor(word);

        act.Should().NotThrow("an unknown camera word is a degradation, not an error — nothing downstream depends on this value");
        ShotPlanParameterMapper.CameraPhraseFor(word).Should().BeNull(
            "'{0}' is not a canonical camera word, so the clause is omitted rather than guessed at",
            word ?? "null");
    }

    [Fact]
    public void CameraWords_is_pinned_to_the_documented_vocabulary()
    {
        // The prompt the model is given, this table and the prompt-consistency test all key off this
        // one list; pinning it here makes an accidental vocabulary change a visible failure.
        ShotPlanParameterMapper.CameraWords.Should().BeEquivalentTo(
            new[] { "Static", "PushIn", "PullOut", "PanLeft", "PanRight", "TiltUp", "TiltDown", "ZoomIn", "ZoomOut", "Handheld", "Orbit" },
            "the camera vocabulary is the single source of truth shared by the agent prompt and this mapper, so it must not change silently");
    }

    // ---------------------------------------------------------------------
    // BuildRequest
    // ---------------------------------------------------------------------

    [Fact]
    public void BuildRequest_carries_the_snapped_duration_the_model_the_resolution_and_the_ratio()
    {
        var request = ShotPlanParameterMapper.BuildRequest(
            MiniMaxH3, "MiniMax-H3", "768P", "16:9", Plan(duration: "Long"), "A chef plating pasta.");

        request.DurationSeconds.Should().Be(15,
            "the request must carry the SNAPPED duration, because that number is what the provider bills and what the estimate reserved");
        request.Model.Should().Be("MiniMax-H3", "the resolved model name must reach the provider verbatim");
        request.Resolution.Should().Be("768P", "the resolution token drives the per-second price and must not be rewritten in transit");
        request.Ratio.Should().Be("16:9", "the aspect-ratio token must reach the provider unchanged");
    }

    [Fact]
    public void BuildRequest_appends_the_camera_clause_to_the_sanitized_prompt()
    {
        var request = ShotPlanParameterMapper.BuildRequest(
            MiniMaxH3, "MiniMax-H3", "768P", "16:9", Plan(camera: "PushIn"), "A chef plating pasta.");

        request.PromptText.Should().StartWith("A chef plating pasta.",
            "the sanitized prompt is the model's own words and must lead the text, or the directive would override the content");
        request.PromptText.Should().Contain(ShotPlanParameterMapper.CameraPhraseFor("PushIn")!,
            "the camera clause is our own first-party prose appended to the prompt, which is how a camera word becomes a provider instruction without vendor-specific directive syntax");
    }

    [Fact]
    public void BuildRequest_without_a_camera_word_sends_the_sanitized_prompt_unchanged()
    {
        var request = ShotPlanParameterMapper.BuildRequest(
            MiniMaxH3, "MiniMax-H3", "768P", "16:9", Plan(camera: "DollyZoom"), "A chef plating pasta.");

        request.PromptText.Should().Be("A chef plating pasta.",
            "an unrecognised camera word must not add, pad or alter the prompt text — omission is the whole documented degradation");
    }

    [Fact]
    public void BuildRequest_with_a_blank_prompt_and_a_camera_word_still_produces_the_clause()
    {
        // The sanitizer reports an empty prompt and the caller fails the shot closed, but this method
        // must still be total: the clause alone, rather than a leading space or a dangling separator.
        var request = ShotPlanParameterMapper.BuildRequest(
            MiniMaxH3, "MiniMax-H3", "768P", "16:9", Plan(camera: "Static"), string.Empty);

        request.PromptText.Should().Be(ShotPlanParameterMapper.CameraPhraseFor("Static"),
            "a blank prompt with a camera word must yield exactly the clause — no stray leading separator that would be sent to the provider");
    }

    [Fact]
    public void BuildRequest_null_sanitized_prompt_with_a_camera_word_yields_exactly_the_clause()
    {
        var request = ShotPlanParameterMapper.BuildRequest(
            MiniMaxH3, "MiniMax-H3", "768P", "16:9", Plan(camera: "Static"), null!);

        request.PromptText.Should().Be(ShotPlanParameterMapper.CameraPhraseFor("Static"),
            "a null prompt must degrade to the clause rather than to a literal 'null' or a formatted separator");
    }

    [Fact]
    public void BuildRequest_unresolvable_duration_word_throws_rather_than_shipping_a_default()
    {
        Action act = () => ShotPlanParameterMapper.BuildRequest(
            MiniMaxH3, "MiniMax-H3", "768P", "16:9", Plan(duration: "Epic"), "A chef plating pasta.");

        act.Should().Throw<ArgumentException>(
            "a request whose duration cannot be snapped must never be built, because the cost estimate was computed from the same word and the two must agree");
    }

    [Fact]
    public void BuildRequest_null_shot_or_capabilities_throws()
    {
        Action nullShot = () => ShotPlanParameterMapper.BuildRequest(
            MiniMaxH3, "MiniMax-H3", "768P", "16:9", null!, "p");
        Action nullCapabilities = () => ShotPlanParameterMapper.BuildRequest(
            null!, "MiniMax-H3", "768P", "16:9", Plan(), "p");

        nullShot.Should().Throw<ArgumentNullException>(
            "a missing shot has no duration word to snap, so there is nothing to build a request from");
        nullCapabilities.Should().Throw<ArgumentNullException>(
            "a null capability table means the model was never resolved, and guessing its duration range would price the shot wrongly");
    }
}
