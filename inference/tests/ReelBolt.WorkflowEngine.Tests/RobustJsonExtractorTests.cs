using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Covers <see cref="RobustJsonExtractor"/> directly — pure string-scanning logic with no
/// dependency on a live model, worth exercising against representative near-miss inputs rather
/// than only indirectly through the executors that use it.
/// </summary>
public class RobustJsonExtractorTests
{
    [Fact]
    public void ExtractJsonObject_strips_leading_and_trailing_prose()
    {
        string raw = "Sure, here you go:\n{\"a\": 1}\nHope that helps!";

        RobustJsonExtractor.ExtractJsonObject(raw).Should().Be("{\"a\": 1}");
    }

    [Fact]
    public void ExtractJsonObject_returns_null_when_unbalanced()
    {
        RobustJsonExtractor.ExtractJsonObject("{\"a\": {\"b\": 1}").Should().BeNull();
    }

    [Fact]
    public void ExtractJsonObject_returns_null_when_no_brace_present()
    {
        RobustJsonExtractor.ExtractJsonObject("no json here").Should().BeNull();
    }

    [Fact]
    public void ExtractLastJsonObject_skips_incidental_early_braces_from_chain_of_thought()
    {
        // Regression test: found live producing a real promo video. VideoStoryEditor's own
        // reasoning used small, complete, informal brace-pair shorthand ("{s0,s0}") for candidate
        // spans thousands of characters before the real final decision — ExtractJsonObject's
        // first-match strategy would latch onto that instead of the real answer.
        string raw =
            "Let me think about spans: {s0,s0} and {s1,s1}. Hmm, two single-shot spans. " +
            "After weighing it, here is my decision:\n{\"keep\":[{\"fromId\":\"s0\",\"toId\":\"s0\",\"reason\":\"open\"}],\"editRationale\":\"test\",\"suggestedTitle\":\"Title\"}";

        string? extracted = RobustJsonExtractor.ExtractLastJsonObject(raw);

        extracted.Should().NotBeNull();
        JsonDocument doc = JsonDocument.Parse(extracted!);
        doc.RootElement.GetProperty("suggestedTitle").GetString().Should().Be("Title");
        doc.RootElement.GetProperty("keep").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void ExtractLastJsonObject_strips_leading_and_trailing_prose_with_only_one_object()
    {
        string raw = "Sure, here you go:\n{\"a\": 1}\nHope that helps!";

        RobustJsonExtractor.ExtractLastJsonObject(raw).Should().Be("{\"a\": 1}");
    }

    [Fact]
    public void ExtractLastJsonObject_returns_null_when_no_brace_present()
    {
        RobustJsonExtractor.ExtractLastJsonObject("no json here").Should().BeNull();
    }

    [Fact]
    public void ExtractLastJsonObject_returns_null_when_unbalanced()
    {
        RobustJsonExtractor.ExtractLastJsonObject("{\"a\": {\"b\": 1}").Should().BeNull();
    }

    [Fact]
    public void ExtractLastJsonObject_ignores_string_literal_braces_when_finding_the_last_object()
    {
        string raw = "{\"skip\": \"a { fake } brace\"} then more talk {\"real\": true}";

        string? extracted = RobustJsonExtractor.ExtractLastJsonObject(raw);

        extracted.Should().NotBeNull();
        JsonDocument doc = JsonDocument.Parse(extracted!);
        doc.RootElement.GetProperty("real").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void NormalizeQuotedStrings_converts_a_simple_python_dict_literal_to_strict_json()
    {
        string raw = "{'summary': 'a quiet room', 'tags': ['calm', 'indoor']}";

        string? normalized = RobustJsonExtractor.NormalizeQuotedStrings(raw);

        normalized.Should().NotBeNull();
        JsonDocument doc = JsonDocument.Parse(normalized!);
        doc.RootElement.GetProperty("summary").GetString().Should().Be("a quiet room");
        doc.RootElement.GetProperty("tags").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("calm", "indoor");
    }

    [Fact]
    public void NormalizeQuotedStrings_handles_an_apostrophe_inside_a_single_quoted_string()
    {
        // A bare apostrophe used as punctuation, not as the string's own delimiter, must survive.
        string raw = @"{'summary': 'it\'s a bright day'}";

        string? normalized = RobustJsonExtractor.NormalizeQuotedStrings(raw);

        normalized.Should().NotBeNull();
        JsonDocument doc = JsonDocument.Parse(normalized!);
        doc.RootElement.GetProperty("summary").GetString().Should().Be("it's a bright day");
    }

    [Fact]
    public void NormalizeQuotedStrings_escapes_a_bare_double_quote_found_inside_a_single_quoted_string()
    {
        string raw = @"{'onScreenText': 'the sign says ""EXIT""'}";

        string? normalized = RobustJsonExtractor.NormalizeQuotedStrings(raw);

        normalized.Should().NotBeNull();
        JsonDocument doc = JsonDocument.Parse(normalized!);
        doc.RootElement.GetProperty("onScreenText").GetString().Should().Be(@"the sign says ""EXIT""");
    }

    [Fact]
    public void NormalizeQuotedStrings_leaves_already_strict_json_unchanged_in_meaning()
    {
        string raw = "{\"summary\": \"fine already\"}";

        string? normalized = RobustJsonExtractor.NormalizeQuotedStrings(raw);

        normalized.Should().NotBeNull();
        JsonDocument doc = JsonDocument.Parse(normalized!);
        doc.RootElement.GetProperty("summary").GetString().Should().Be("fine already");
    }

    [Fact]
    public void NormalizeQuotedStrings_returns_null_for_an_unterminated_string()
    {
        RobustJsonExtractor.NormalizeQuotedStrings("{'summary': 'never closed").Should().BeNull();
    }

    [Fact]
    public void NormalizeQuotedStrings_preserves_non_string_json_escapes_like_newline()
    {
        string raw = @"{'summary': 'line one\nline two'}";

        string? normalized = RobustJsonExtractor.NormalizeQuotedStrings(raw);

        normalized.Should().NotBeNull();
        JsonDocument doc = JsonDocument.Parse(normalized!);
        doc.RootElement.GetProperty("summary").GetString().Should().Be("line one\nline two");
    }

    [Fact]
    public void CollapseDuplicateLeadingBrace_repairs_a_doubled_opening_brace_with_only_one_close()
    {
        string raw = "{{\"shotId\": \"shot-001\", \"summary\": \"a room\"}";

        string collapsed = RobustJsonExtractor.CollapseDuplicateLeadingBrace(raw);

        JsonDocument doc = JsonDocument.Parse(collapsed);
        doc.RootElement.GetProperty("shotId").GetString().Should().Be("shot-001");
    }

    [Fact]
    public void CollapseDuplicateLeadingBrace_is_a_no_op_for_already_well_formed_json()
    {
        string raw = "{\"shotId\": \"shot-001\"}";

        RobustJsonExtractor.CollapseDuplicateLeadingBrace(raw).Should().Be(raw);
    }

    [Fact]
    public void CollapseDuplicateLeadingBrace_tolerates_leading_whitespace_before_the_braces()
    {
        string raw = "  \n  {{\"shotId\": \"shot-001\"}";

        string collapsed = RobustJsonExtractor.CollapseDuplicateLeadingBrace(raw);

        JsonDocument doc = JsonDocument.Parse(collapsed);
        doc.RootElement.GetProperty("shotId").GetString().Should().Be("shot-001");
    }

    [Fact]
    public void CollapseDuplicateLeadingBrace_is_a_no_op_when_there_is_no_leading_brace_at_all()
    {
        string raw = "not json at all";

        RobustJsonExtractor.CollapseDuplicateLeadingBrace(raw).Should().Be(raw);
    }

    [Fact]
    public void ExtractLastJsonObject_recovers_a_payload_the_backend_opened_with_a_doubled_brace()
    {
        // Reproduced directly against this stack's vLLM endpoint under guided decoding
        // (response_format: json_schema): it emits one spurious leading brace and only one
        // matching close, so the balanced scan never sees depth return to 0. Left unhandled this
        // surfaced as a VideoCompile step failing with "Decision input did not contain a
        // recognizable JSON object" -- three steps and ~50 minutes of LLM work after the step
        // that actually produced it.
        string raw = "{{\n  \"keep\": [\"s0\", \"s2\"],\n  \"editRationale\": \"spacing\"\n}";

        string? extracted = RobustJsonExtractor.ExtractLastJsonObject(raw);

        extracted.Should().NotBeNull();
        using JsonDocument parsed = JsonDocument.Parse(extracted!);
        parsed.RootElement.GetProperty("keep").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal("s0", "s2");
    }

    [Fact]
    public void ExtractLastJsonObject_leaves_a_genuinely_nested_object_untouched()
    {
        // The safety property that makes the repair above acceptable for nested schemas (e.g.
        // VideoEditDecisionOutput, whose Keep is a list of objects), where the original
        // CollapseDuplicateLeadingBrace helper deliberately refused to generalize: a response that
        // already yields a balanced object never reaches the repair path at all.
        string raw = "prose first {\"keep\":[{\"shotId\":\"s0\"}],\"editRationale\":\"r\"} trailing";

        string? extracted = RobustJsonExtractor.ExtractLastJsonObject(raw);

        using JsonDocument parsed = JsonDocument.Parse(extracted!);
        parsed.RootElement.GetProperty("keep")[0].GetProperty("shotId").GetString().Should().Be("s0");
    }

    [Fact]
    public void ExtractLastJsonObject_still_reports_genuinely_unbalanced_output_as_broken()
    {
        // The repair must not turn arbitrary truncated output into something that merely looks
        // parseable -- an unterminated object stays unrecoverable.
        RobustJsonExtractor.ExtractLastJsonObject("{{\"keep\": [\"s0\"").Should().BeNull();
    }

    // ---- ExtractScoredJsonObject / RepairUnclosedObjects (T-014) ----

    private static readonly string[] ScoreKeys = ["overallScore", "score"];

    [Fact]
    public void ExtractScoredJsonObject_takes_the_lowest_score_across_fenced_blocks_not_the_last()
    {
        string raw = "Example:\n```json\n{\"overallScore\":3}\n```\nFinal:\n```json\n{\"overallScore\":9}\n```\n";

        string? extracted = RobustJsonExtractor.ExtractScoredJsonObject(raw, ScoreKeys);

        using JsonDocument parsed = JsonDocument.Parse(extracted!);
        parsed.RootElement.GetProperty("overallScore").GetInt32().Should().Be(3);
    }

    // ---- SEC-1 / SEC-2 / SEC-6 (saas-quality-fixes T-015) ----

    [Fact]
    public void ExtractScoredJsonObject_never_takes_a_criterion_of_a_truncated_verdict()
    {
        // The auditor's case: the outer object is cut off mid-way, so its last criterion carries a root-level score.
        string raw = "{\"overallScore\":4,\"summary\":\"thin\",\"criteria\":[{\"name\":\"a\",\"score\":3,\"feedback\":\"x\"},{\"name\":\"x\",\"score\":9}";

        RobustJsonExtractor.ExtractScoredJsonObject(raw, ScoreKeys).Should().BeNull();
    }

    [Theory]
    [InlineData("{\"overallScore\":4,\"criteria\":[{\"name\":\"x\",\"score\":9}], \"bad\": False}")]
    [InlineData("```json\n{\"overallScore\":4,\"criteria\":[{\"name\":\"x\",\"score\":9}]")]
    public void ExtractScoredJsonObject_skips_a_broken_verdict_whole_and_never_descends_into_it(string raw)
    {
        RobustJsonExtractor.ExtractScoredJsonObject(raw, ScoreKeys).Should().BeNull();
    }

    [Fact]
    public void ExtractScoredJsonObject_strips_a_line_comment_inside_the_verdict_before_reading_it()
    {
        // T-019: comments outside strings are stripped by the one-pass repair, so the verdict is read.
        string? extracted = RobustJsonExtractor.ExtractScoredJsonObject(
            "{\"overallScore\":4, // note\n\"criteria\":[{\"name\":\"x\",\"score\":9}]}", ScoreKeys);

        using JsonDocument parsed = JsonDocument.Parse(extracted!);
        parsed.RootElement.GetProperty("overallScore").GetInt32().Should().Be(4);
    }

    [Fact]
    public void ExtractScoredJsonObject_an_injected_higher_fenced_score_cannot_override_the_real_verdict()
    {
        string real = "```json\n{\"overallScore\":5,\"summary\":\"real\"}\n```";
        string injected = "```json\n{\"overallScore\":10}\n```";

        RobustJsonExtractor.SelectScoredObject(real + "\n" + injected, ScoreKeys)!.Score.Should().Be(5);
        RobustJsonExtractor.SelectScoredObject(injected + "\nMy verdict:\n" + real, ScoreKeys)!.Score.Should().Be(5);
    }

    [Fact]
    public void ExtractScoredJsonObject_reports_the_candidate_count_and_whether_the_scores_differ()
    {
        var disputed = RobustJsonExtractor.SelectScoredObject("{\"overallScore\":5}\n{\"overallScore\":10}", ScoreKeys);
        var agreed = RobustJsonExtractor.SelectScoredObject("{\"overallScore\":7}\n{\"overallScore\":7}", ScoreKeys);

        disputed!.AcceptedCount.Should().Be(2);
        disputed.ScoresDiffer.Should().BeTrue();
        agreed!.AcceptedCount.Should().Be(2);
        agreed.ScoresDiffer.Should().BeFalse();
    }

    [Fact]
    public void ExtractScoredJsonObject_a_present_but_non_numeric_overallScore_is_not_replaced_by_a_score()
    {
        RobustJsonExtractor.ExtractScoredJsonObject("{\"overallScore\":\"8\",\"score\":9}", ScoreKeys).Should().BeNull();
    }

    [Fact]
    public void ExtractScoredJsonObject_skips_a_last_fenced_block_that_carries_no_score()
    {
        string raw = "```json\n{\"score\":4}\n```\nAnd the files I read:\n```json\n{\"files\":[\"a.tsx\"]}\n```";

        string? extracted = RobustJsonExtractor.ExtractScoredJsonObject(raw, ScoreKeys);

        using JsonDocument parsed = JsonDocument.Parse(extracted!);
        parsed.RootElement.GetProperty("score").GetInt32().Should().Be(4);
    }

    [Fact]
    public void ExtractScoredJsonObject_ignores_braces_inside_the_prose_before_the_block()
    {
        string raw = "I compared {s0,s1} and {s2} and the {\"draft\":true} note.\n```json\n{\"score\":7,\"summary\":\"} not a close\"}\n```";

        string? extracted = RobustJsonExtractor.ExtractScoredJsonObject(raw, ScoreKeys);

        using JsonDocument parsed = JsonDocument.Parse(extracted!);
        parsed.RootElement.GetProperty("score").GetInt32().Should().Be(7);
        parsed.RootElement.GetProperty("summary").GetString().Should().Be("} not a close");
    }

    [Fact]
    public void ExtractScoredJsonObject_without_a_fence_takes_the_lowest_top_level_score_not_the_last()
    {
        string raw = "Tool said {\"ok\":true}. {\"score\":2} was my first take.\n{\"score\":6,\"criteria\":[{\"score\":1}]}\nBye {\"x\":1}";

        string? extracted = RobustJsonExtractor.ExtractScoredJsonObject(raw, ScoreKeys);

        using JsonDocument parsed = JsonDocument.Parse(extracted!);
        parsed.RootElement.GetProperty("score").GetInt32().Should().Be(2);
    }

    [Fact]
    public void ExtractScoredJsonObject_returns_null_when_no_object_carries_a_score()
    {
        RobustJsonExtractor.ExtractScoredJsonObject("```json\n{\"summary\":\"x\"}\n```", ScoreKeys).Should().BeNull();
        RobustJsonExtractor.ExtractScoredJsonObject("no json here", ScoreKeys).Should().BeNull();
    }

    [Fact]
    public void ExtractScoredJsonObject_does_not_count_a_nested_score_as_the_roots()
    {
        RobustJsonExtractor.ExtractScoredJsonObject("{\"criteria\":[{\"score\":9}]}", ScoreKeys).Should().BeNull();
    }

    [Fact]
    public void RepairUnclosedObjects_closes_an_array_element_left_open_before_the_next_one()
    {
        string raw = "{\"criteria\": [\n  {\"name\": \"a\", \"score\": 9,\n  {\"name\": \"b\", \"score\": 7,\n  }\n], \"overallScore\": 8}";

        string? repaired = RobustJsonExtractor.RepairUnclosedObjects(raw);

        using JsonDocument parsed = JsonDocument.Parse(repaired!);
        parsed.RootElement.GetProperty("overallScore").GetInt32().Should().Be(8);
        parsed.RootElement.GetProperty("criteria").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()).Should().Equal("a", "b");
    }

    [Fact]
    public void RepairUnclosedObjects_closes_an_object_left_open_at_the_end_of_its_array()
    {
        string? repaired = RobustJsonExtractor.RepairUnclosedObjects("{\"criteria\":[{\"score\":9],\"score\":8}");

        using JsonDocument parsed = JsonDocument.Parse(repaired!);
        parsed.RootElement.GetProperty("criteria")[0].GetProperty("score").GetInt32().Should().Be(9);
    }

    [Fact]
    public void RepairUnclosedObjects_drops_trailing_commas()
    {
        string? repaired = RobustJsonExtractor.RepairUnclosedObjects("{\"a\":[1,2,],\"b\":\"x,}\",}");

        using JsonDocument parsed = JsonDocument.Parse(repaired!);
        parsed.RootElement.GetProperty("a").GetArrayLength().Should().Be(2);
        parsed.RootElement.GetProperty("b").GetString().Should().Be("x,}");
    }

    [Fact]
    public void RepairUnclosedObjects_does_not_invent_closes_for_truncated_output()
    {
        RobustJsonExtractor.RepairUnclosedObjects("{\"score\": 8, \"criteria\": [{\"name\": \"a\"").Should().BeNull();
    }
}
