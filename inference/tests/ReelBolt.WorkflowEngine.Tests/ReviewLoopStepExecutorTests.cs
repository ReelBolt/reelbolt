using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="ReviewLoopStepExecutor.ParseReviewScore"/>/<see cref="ReviewLoopStepExecutor.ExtractFeedbackSummary"/>
/// — the two pure-JSON-parsing helpers the review loop (both the main promo pipeline's
/// AgentType.ReviewAgent and the video-editing templates' AgentType.VideoReviewAgent) shares — plus
/// <see cref="ReviewLoopStepExecutor.ApplyNarrationFitCap"/>, the deterministic narration
/// picture-window overflow gate, and the real <c>ExecuteAsync</c> run that proves the capped score
/// is what actually drives the loop-back decision.
/// </summary>
public class ReviewLoopStepExecutorTests
{
    [Theory]
    // A lone surrogate as a ROOT KEY: the verdict object cannot be read, so the review fails closed.
    [InlineData("{\"overallScore\":9,\"\\ud800\":1}", 0)]
    // A lone surrogate inside a string VALUE: the verdict itself is readable and stands; only the brief degrades.
    [InlineData("{\"overallScore\":9,\"summary\":\"\\ud800\"}", 9)]
    [InlineData("```json\n{\"overallScore\":9,\"criteria\":[{\"name\":\"\\udc00\",\"score\":9}]}\n```", 9)]
    public void A_lone_surrogate_in_the_review_never_throws_and_an_unreadable_verdict_fails_closed(string output, int expected)
    {
        // SEC-18: System.Text.Json throws InvalidOperationException (not JsonException) reading a lone surrogate;
        // before the fix it escaped both paths and failed the paid run instead of looping.
        System.Action score = () => ReviewLoopStepExecutor.ParseReviewScore(output, AgentType.ReviewAgent);
        System.Action brief = () => ReviewLoopStepExecutor.ExtractFeedbackSummary(output, 8, null, AgentType.ReviewAgent);
        score.Should().NotThrow();
        brief.Should().NotThrow();
        ReviewLoopStepExecutor.ParseReviewScore(output, AgentType.ReviewAgent).Should().Be(expected);
    }

    [Fact]
    public void ParseReviewScore_reads_the_canonical_score_property()
    {
        // VideoReviewOutput.Score serializes as "score" — the property this method checks first.
        ReviewLoopStepExecutor.ParseReviewScore("""{"score":8,"passesReview":true,"summary":"ok"}""")
            .Should().Be(8);
    }

    [Fact]
    public void ParseReviewScore_falls_back_to_overallScore_for_the_main_pipelines_ReviewOutput_shape()
    {
        // Regression test for a real pre-existing bug: ReviewOutput.OverallScore serializes as
        // "overallScore", not "score" — this method previously only ever checked "score", so the
        // main pipeline's review score silently always parsed as 0 regardless of what the review
        // agent actually judged (every ReviewLoop step always looped until MaxIterations).
        ReviewLoopStepExecutor.ParseReviewScore("""{"overallScore":9,"passesReview":true,"summary":"ok"}""")
            .Should().Be(9);
    }

    [Fact]
    public void ParseReviewScore_prefers_overallScore_over_score_when_both_are_present()
    {
        // T-015: overallScore decides whenever it is present; score is read only when it is absent.
        ReviewLoopStepExecutor.ParseReviewScore("""{"score":3,"overallScore":9}""").Should().Be(9);
    }

    [Fact]
    public void ParseReviewScore_returns_zero_for_malformed_json()
    {
        ReviewLoopStepExecutor.ParseReviewScore("not json at all").Should().Be(0);
    }

    [Fact]
    public void ParseReviewScore_returns_zero_when_neither_property_is_present()
    {
        ReviewLoopStepExecutor.ParseReviewScore("""{"passesReview":false}""").Should().Be(0);
    }

    [Fact]
    public void ExtractFeedbackSummary_combines_summary_and_video_review_issues()
    {
        string? feedback = ReviewLoopStepExecutor.ExtractFeedbackSummary(
            """{"score":4,"passesReview":false,"issues":["ends mid-sentence","overlay p0 covers 30% of the frame"],"summary":"Cut ends mid-sentence."}""");

        feedback.Should().NotBeNull();
        feedback.Should().Contain("Cut ends mid-sentence.");
        feedback.Should().Contain("ends mid-sentence");
        feedback.Should().Contain("overlay p0 covers 30% of the frame");
    }

    [Fact]
    public void ExtractFeedbackSummary_combines_summary_and_main_pipelines_improvementAreas()
    {
        // Schema-tolerant across both review output shapes, since both flow through the same
        // ReviewLoopStepExecutor.
        string? feedback = ReviewLoopStepExecutor.ExtractFeedbackSummary(
            """{"overallScore":5,"passesReview":false,"improvementAreas":["missing captions"],"summary":"Needs work."}""");

        feedback.Should().NotBeNull();
        feedback.Should().Contain("Needs work.");
        feedback.Should().Contain("missing captions");
    }

    [Fact]
    public void ExtractFeedbackSummary_returns_null_for_malformed_json()
    {
        ReviewLoopStepExecutor.ExtractFeedbackSummary("not json").Should().BeNull();
    }

    [Fact]
    public void ExtractFeedbackSummary_returns_null_when_nothing_usable_is_present()
    {
        ReviewLoopStepExecutor.ExtractFeedbackSummary("""{"score":9,"passesReview":true}""").Should().BeNull();
    }

    [Fact]
    public void ExtractFeedbackSummary_ignores_empty_or_whitespace_only_issue_strings()
    {
        string? feedback = ReviewLoopStepExecutor.ExtractFeedbackSummary(
            """{"score":4,"issues":["  ","real issue"],"summary":""}""");

        feedback.Should().NotBeNull();
        feedback.Should().Contain("real issue");
    }

    // ---- Bug group B: AgentType.VideoReviewAgent has tools bound (same as the story editor), so
    // its raw completion can carry trailing prose/markdown/leaked tool-call-closing tokens after a
    // perfectly valid JSON object — the exact failure mode RobustJsonExtractor.ExtractJsonObject
    // was written for. A bare JsonDocument.Parse over the whole string previously threw, the
    // catch (JsonException) swallowed it, and the score silently came back 0 (looping every run
    // regardless of the model's actual verdict) with no feedback extracted at all. ----

    [Fact]
    public void ParseReviewScore_tolerates_trailing_prose_after_the_json_object()
    {
        string raw = """{"score":8,"passesReview":true,"summary":"ok"}""" +
            "\n\nI have completed the review. </invoke></tool_call>";

        ReviewLoopStepExecutor.ParseReviewScore(raw).Should().Be(8);
    }

    [Fact]
    public void ExtractFeedbackSummary_tolerates_trailing_prose_after_the_json_object()
    {
        string raw = """{"score":4,"passesReview":false,"issues":["ends mid-sentence"],"summary":"Cut ends mid-sentence."}""" +
            "\n\n```\nDone.\n```";

        string? feedback = ReviewLoopStepExecutor.ExtractFeedbackSummary(raw);

        feedback.Should().NotBeNull();
        feedback.Should().Contain("Cut ends mid-sentence.");
        feedback.Should().Contain("ends mid-sentence");
    }

    // ---- Bug group B: a non-integer JSON number (e.g. 8.5 — plausible output for a "score 1-10"
    // prompt) previously crashed ParseReviewScore uncaught: JsonElement.GetInt32() throws
    // FormatException for a non-integer number, which the surrounding catch (JsonException) does
    // not cover. ----

    [Fact]
    public void ParseReviewScore_fails_closed_on_a_non_integer_score_instead_of_throwing()
    {
        // T-020 (SEC-10): the schemas are integers, so a fraction is out of schema and scores 0.
        ReviewLoopStepExecutor.ParseReviewScore("""{"score":8.5,"passesReview":true}""")
            .Should().Be(0);
    }

    [Fact]
    public void ParseReviewScore_fails_closed_on_a_non_integer_overallScore_instead_of_throwing()
    {
        ReviewLoopStepExecutor.ParseReviewScore("""{"overallScore":6.2}""")
            .Should().Be(0);
    }

    // ---- T-014: staging before arm @ b4556dbbc, promo-2 iteration 1. The reviewer wrote
    // "overallScore": 8 in a fenced json block after several lines of prose, but left every
    // `criteria` entry unclosed (`"feedback": "...",` straight into the next `{`), so the block never
    // balanced: the extractor found no object, the parse failed, and the loop recorded 0 and paid
    // for an extra iteration. The fixture is that completion, byte for byte. ----

    private static string StagingPromo2Iteration1ReviewOutput() =>
        System.IO.File.ReadAllText(System.IO.Path.Combine(
            AppContext.BaseDirectory, "TestData", "promo2-it1-review-output.txt"));

    [Fact]
    public void ParseReviewScore_reads_the_staging_promo2_review_whose_criteria_objects_are_unclosed()
    {
        ReviewLoopStepExecutor.ParseReviewScore(StagingPromo2Iteration1ReviewOutput()).Should().Be(8);
    }

    [Fact]
    public void ExtractFeedbackSummary_builds_the_revision_brief_from_the_staging_promo2_review()
    {
        string? feedback = ReviewLoopStepExecutor.ExtractFeedbackSummary(StagingPromo2Iteration1ReviewOutput(), minScore: 9);

        feedback.Should().NotBeNull();
        feedback.Should().Contain("Overall score: 8/10 (pass mark 9).");
        feedback.Should().Contain("- narrativeClarity 9/10:");
        feedback.Should().Contain("- visualAccuracy 7/10:");
        feedback.Should().Contain("- timing 9/10:");
        feedback.Should().Contain("- completeness 8/10:");
        feedback.Should().Contain("Brand typefaces are not rendered");
    }

    [Fact]
    public void ParseReviewScore_ignores_braces_in_the_prose_before_a_fenced_block()
    {
        string raw = "Checked the spans {s0,s1} and the {title} slot first.\n\n" +
            "```json\n{\"overallScore\":7,\"summary\":\"ok\"}\n```";

        ReviewLoopStepExecutor.ParseReviewScore(raw).Should().Be(7);
    }

    [Fact]
    public void ParseReviewScore_takes_the_lowest_fenced_score_and_the_feedback_comes_from_that_same_object()
    {
        string raw = "The expected shape is:\n```json\n{\"overallScore\":3,\"summary\":\"example\"}\n```\n" +
            "My review:\n```json\n{\"overallScore\":9,\"summary\":\"real\"}\n```";

        ReviewLoopStepExecutor.ParseReviewScore(raw).Should().Be(3);
        ReviewLoopStepExecutor.ExtractFeedbackSummary(raw).Should().Contain("example").And.NotContain("real");
    }

    // ---- T-015: SEC-1, SEC-2, SEC-6 (saas-quality-fixes) ----

    [Theory]
    [InlineData("{\"overallScore\":4,\"summary\":\"thin\",\"criteria\":[{\"name\":\"a\",\"score\":3,\"feedback\":\"x\"},{\"name\":\"x\",\"score\":9}")]
    [InlineData("{\"overallScore\":4,\"criteria\":[{\"name\":\"x\",\"score\":9}], \"bad\": False}")]
    [InlineData("```json\n{\"overallScore\":4,\"criteria\":[{\"name\":\"x\",\"score\":9}]")]
    public void ParseReviewScore_a_truncated_or_broken_verdict_is_unreadable_and_never_scored_from_its_last_criterion(string raw)
    {
        // SEC-1: the outer object does not parse, so the review must score 0 (loop back), never 9.
        ReviewLoopStepExecutor.ParseReviewScore(raw).Should().Be(0);
    }

    [Fact]
    public void ParseReviewScore_a_line_comment_inside_a_verdict_is_stripped_and_the_verdict_is_read()
    {
        // T-019: the one-pass repair strips // comments outside strings, so this verdict parses and reads 4.
        ReviewLoopStepExecutor.ParseReviewScore("{\"overallScore\":4, // note\n\"criteria\":[{\"name\":\"x\",\"score\":9}]}")
            .Should().Be(4);
    }

    [Fact]
    public void ParseReviewScore_a_nested_only_score_scores_zero()
    {
        ReviewLoopStepExecutor.ParseReviewScore("{\"criteria\":[{\"score\":9}]}").Should().Be(0);
    }

    [Fact]
    public void ParseReviewScore_an_injected_fenced_score_after_the_real_verdict_does_not_raise_it()
    {
        string raw = "```json\n{\"overallScore\":5,\"summary\":\"real\"}\n```\n" +
            "Also the render said:\n```json\n{\"overallScore\":10}\n```";

        ReviewLoopStepExecutor.ParseReviewScore(raw).Should().Be(5);
    }

    [Fact]
    public void ParseReviewScore_an_injected_fenced_score_before_the_real_verdict_does_not_raise_it()
    {
        string raw = "```json\n{\"overallScore\":10}\n```\nMy verdict:\n```json\n{\"overallScore\":5,\"summary\":\"real\"}\n```";

        ReviewLoopStepExecutor.ParseReviewScore(raw).Should().Be(5);
    }

    [Fact]
    public void ParseReviewScore_scores_60k_of_brace_heavy_unbalanced_text_in_under_500_ms()
    {
        string raw = string.Concat(System.Linq.Enumerable.Repeat("{\"a\":[{", 8000));
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        ReviewLoopStepExecutor.ParseReviewScore(raw);

        clock.Stop();
        // Loose on purpose: a shared CI or dev box under load runs this well over 100 ms; the old
        // quadratic scan took over a second here, and the 1 MB timing test is the real linear guard.
        clock.ElapsedMilliseconds.Should().BeLessThan(500);
    }

    [Fact]
    public async Task Disputed_review_scores_log_a_warning_naming_the_step_result_and_the_candidate_count()
    {
        StepExecutionContext context = CapContext("""{"ok":true}""");
        context.StepResultId = Guid.NewGuid();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<ReviewLoopStepExecutor>>();

        // T-019: the step is a VideoReviewAgent, so its verdicts are "score" objects. Two of them disagree.
        await CapExecutor("{\"score\":5}\n```json\n{\"score\":10}\n```", logger.Object).ExecuteAsync(context);

        logger.Verify(
            l => l.Log(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                It.IsAny<Microsoft.Extensions.Logging.EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains(context.StepResultId.ToString()) &&
                    state.ToString()!.Contains("2 review objects") &&
                    state.ToString()!.Contains("carry different scores")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [Fact]
    public void ParseReviewScore_without_a_fence_takes_the_only_object_that_carries_a_score()
    {
        string raw = "Read {\"file\":\"src/Scene.tsx\"} via the tool.\n" +
            "{\"overallScore\":6,\"summary\":\"real\"}\nDone. {\"note\":\"trailing\"}";

        ReviewLoopStepExecutor.ParseReviewScore(raw).Should().Be(6);
        ReviewLoopStepExecutor.ExtractFeedbackSummary(raw).Should().Contain("real");
    }

    [Fact]
    public void ParseReviewScore_tolerates_a_trailing_comma_before_a_closing_brace()
    {
        ReviewLoopStepExecutor.ParseReviewScore("```json\n{\"overallScore\":5,\"summary\":\"x\",}\n```")
            .Should().Be(5);
    }

    [Fact]
    public async Task An_unreadable_review_score_logs_a_warning_naming_the_step_result()
    {
        StepExecutionContext context = CapContext("""{"ok":true}""");
        context.StepResultId = Guid.NewGuid();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<ReviewLoopStepExecutor>>();

        await CapExecutor("I could not finish the review.", logger.Object).ExecuteAsync(context);

        logger.Verify(
            l => l.Log(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                It.IsAny<Microsoft.Extensions.Logging.EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(context.StepResultId.ToString())),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task An_unaccounted_verdict_key_scores_0_loops_back_and_logs_a_warning_naming_the_step_result()
    {
        // T-019: the root "score" sits in an object that does not parse, so the verdict key is unaccounted
        // for and the review must score 0 even though a score-shaped token is present.
        StepExecutionContext context = CapContext("""{"ok":true}""");
        context.StepResultId = Guid.NewGuid();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<ReviewLoopStepExecutor>>();

        StepExecutionResult result = await CapExecutor("""{"score":9,"passesReview":true,"bad": False}""", logger.Object)
            .ExecuteAsync(context);

        result.NextStepIndex.Should().Be(0, "a score of 0 loops back to the loop target");
        logger.Verify(
            l => l.Log(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                It.IsAny<Microsoft.Extensions.Logging.EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains("unaccounted verdict key") &&
                    state.ToString()!.Contains(context.StepResultId.ToString())),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task A_readable_review_score_logs_no_warning()
    {
        StepExecutionContext context = CapContext("""{"ok":true}""");
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<ReviewLoopStepExecutor>>();

        await CapExecutor(GreenReviewOutput, logger.Object).ExecuteAsync(context);

        logger.Verify(
            l => l.Log(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                It.IsAny<Microsoft.Extensions.Logging.EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Never);
    }

    // ==========================================================================================
    // The narration picture-window overflow cap (T-206)
    //
    // The prompt tells the reviewer to score no higher than 4 when `overflowLineCount > 0`. That is a
    // request to a model. This half is the guarantee: a pure function of the prior step's output that
    // the loop decision reads and the model cannot decline.
    // ==========================================================================================

    /// <summary>
    /// The compile step's output as the narration workflow produces it when a line overruns the shot it
    /// starts in. Shape copied from <c>VideoCompileStepExecutor.BuildVoiceoverFitNode</c>'s
    /// <c>applicable: true</c> arm — the key names are the contract, so they are written out here in
    /// full rather than reduced to the one key under test.
    /// </summary>
    public const string OverflowingVoiceoverOutput =
        """
        {"status":"completed","segments":3,"outputDurationSec":11.0,
         "voiceover":{"enabled":true,"applied":true,"appliedLineCount":2,"lineCount":2,
           "lines":[
             {"lineId":"vo1","outputStartSec":2.0,"playDurationSec":3.0,
              "windowStartSec":0.0,"windowEndSec":10.0,"windowSec":8.0,"overflowSec":0.0,"fits":true,"fitSource":"compile_measured"},
             {"lineId":"vo2","outputStartSec":8.0,"playDurationSec":5.0,
              "windowStartSec":0.0,"windowEndSec":10.0,"windowSec":2.0,"overflowSec":3.0,"fits":false,"fitSource":"compile_measured"}],
           "fit":{"applicable":true,"measuredLineCount":2,"overflowLineCount":1,
                  "overflowTotalSec":3.0,"maxOverflowSec":3.0,"unknownWindowLineIds":[]}}}
        """;

    /// <summary>
    /// The same node with <c>applicable: false</c> and nothing measurable — the arm
    /// <c>VideoCompileStepExecutor.BuildVoiceoverFitNode</c> takes when no line could be measured at
    /// all (<c>reason: "no_resolved_lines"</c>). Not-measurable is not a defect, so this must never
    /// change a score.
    ///
    /// <para>
    /// <b>DEFENSIVE ONLY — not a shape the shipped compile produces.</b> This node carries no
    /// <c>reason</c> and no <c>unavailable</c> key at the <c>voiceover</c> level, and every return
    /// path of <c>ResolveVoiceoverAsync</c> sets both (the initial object sets <c>unavailable</c>,
    /// and each arm sets <c>reason</c>), so nothing on this head emits it. It is kept because the
    /// fit cap's own contract is stated over the SHAPE — "not literally <c>applicable: true</c> is
    /// inert" — rather than over the current producer, and a defensive row for a hand-assembled
    /// degraded node is worth more than the accident of what today's writer happens to emit. The
    /// PRODUCIBLE not-measurable nodes are <see cref="DeliberatelyEmptyPlanVoiceoverOutput"/> (S3)
    /// and <see cref="AmixUnavailableVoiceoverOutput"/> (S2).
    /// </para>
    /// </summary>
    public const string NotApplicableVoiceoverOutput =
        """
        {"status":"completed","segments":3,
         "voiceover":{"enabled":true,"applied":false,"appliedLineCount":0,"lineCount":0,"lines":[],
           "fit":{"applicable":false,"reason":"no_resolved_lines"}}}
        """;

    /// <summary>
    /// A compile summary from a workflow with no narration at all. On this head that is the shape
    /// <c>video-derush-edit</c> and <c>video-derush-edit-graphics</c> produce: their compile config
    /// leaves <c>EnableVoiceover</c> at its <c>false</c> default, and <c>VideoCompileStepExecutor</c>
    /// only calls <c>ResolveVoiceoverAsync</c> inside <c>if (config.EnableVoiceover)</c> — so no
    /// <c>voiceover</c> node is emitted at all (proven end to end in
    /// <c>VideoCompileStepExecutorVoiceoverFitTests.EnableVoiceover_false_emits_no_voiceover_node_and_an_argv_and_EDL_untouched_by_the_fit_facts</c>).
    /// The main promo pipeline's ReviewLoop follows an Author step and likewise has no <c>voiceover</c>
    /// node.
    /// </summary>
    public const string NoVoiceoverNodeOutput =
        """{"status":"completed","segments":3,"outputDurationSec":11.0,"sentenceCheck":{"applicable":true}}""";

    /// <summary>
    /// The compile node for narration that was DECLARED and NOT APPLIED — the other half of the
    /// silence story, and the one <see cref="ReviewLoopStepExecutor.ApplyNarrationFitCap"/>
    /// structurally cannot reach, because a node with no resolved line has no measurable fit
    /// (<c>applicable:false</c>) and "not measurable" is not a defect.
    ///
    /// <para>
    /// <b>Shape S1 — the all-lines-lost arm.</b> Emitted by <c>VideoCompileStepExecutor</c>'s
    /// <c>Finish()</c> when <c>declaredLineCount &gt; 0 &amp;&amp; resolved.Count == 0 &amp;&amp;
    /// !amixUnavailable</c> — every declared line's artifact was gone — at which point the node sets
    /// <c>reason: "all_lines_unavailable"</c>, the token it sets if and only if at least one line was
    /// declared and not one resolved.
    /// </para>
    /// <para>
    /// <b><c>lineCount</c> is the DECLARED count</b> (3 here), which is media's <c>0924b7cc</c>
    /// correction and the reason <see cref="ReviewLoopStepExecutor.IsDeclaredAndLostNarration"/>'s
    /// KEY-PAIR form — not only its token form — is the live one on this head. The <c>fit</c> node is
    /// the <c>applicable:false</c> arm, which emits <c>applicable</c> and <c>reason</c> ONLY; the
    /// measured/overflow keys exist on the <c>applicable:true</c> arm alone.
    /// </para>
    /// </summary>
    public const string DeclaredAndLostVoiceoverOutput =
        """
        {"status":"completed","segments":3,"outputDurationSec":11.0,
         "sentenceCheck":{"applicable":true,"endsAtSentenceBoundary":true},
         "voiceover":{"enabled":true,"applied":false,"unavailable":false,
           "appliedLineCount":0,"lineCount":3,"lines":[],"dropped":[],
           "reason":"all_lines_unavailable",
           "fit":{"applicable":false,"reason":"no_resolved_lines"}}}
        """;

    /// <summary>
    /// <b>Shape S2 — the amix-<c>unavailable</c> arm.</b> Emitted by <c>VideoCompileStepExecutor</c>
    /// when <c>IsAmixNormalizeAvailableAsync</c> reports that this container's ffmpeg build has an
    /// <c>amix</c> filter exposing no <c>normalize</c> option. At least one declared line was usable
    /// (the earlier arms return first), so the node reports <c>lineCount &gt; 0</c> with
    /// <c>appliedLineCount == 0</c> and <c>applied:false</c>, sets <c>unavailable: true</c>, and sets
    /// <c>reason</c> to that ENVIRONMENT sentence — deliberately NOT the
    /// <c>all_lines_unavailable</c> token, which the arm is guarded away from by
    /// <c>&amp;&amp; !amixUnavailable</c>.
    ///
    /// <para>
    /// <b>This is the arm Ruling 1 puts under the cap, and the fixture that proves the key-pair form
    /// is load-bearing rather than decorative:</b> the node carries the declared-and-lost fact with NO
    /// terminal token available anywhere, so only the key pair can fire on it. A loop cannot fix an
    /// ffmpeg build, but a green render with no narration is the failure this initiative exists to
    /// prevent — a bounded loop that ends not-green beats a silent green, and the reviewer's feedback
    /// names the cause. Its <c>fit</c> node is the same <c>applicable:false</c> two-key shape as S1
    /// and S3.
    /// </para>
    /// </summary>
    public const string AmixUnavailableVoiceoverOutput =
        """
        {"status":"completed","segments":3,"outputDurationSec":11.0,
         "sentenceCheck":{"applicable":true,"endsAtSentenceBoundary":true},
         "voiceover":{"enabled":true,"applied":false,"unavailable":true,
           "appliedLineCount":0,"lineCount":3,"lines":[],"dropped":[],
           "reason":"The ffmpeg build in this container's amix filter does not expose a normalize option.",
           "fit":{"applicable":false,"reason":"no_resolved_lines"}}}
        """;

    /// <summary>
    /// <b>Shape S3 — the genuinely-empty plan.</b> Emitted by <c>VideoCompileStepExecutor</c>'s
    /// <c>voiceoverLinesList.Count == 0</c> arm when the writer's plan declared nothing at all, which
    /// sets <c>reason: "no_valid_voiceover_lines"</c>. Every key the declared-and-lost cap reads
    /// matches <see cref="DeclaredAndLostVoiceoverOutput"/> except <c>lineCount</c>, which is 0 — and
    /// that one key is the entire difference between the two fixtures. Nobody asked for narration
    /// here, so no deterministic cap may fire.
    ///
    /// <para>
    /// The SAME arm also emits a declared-and-lost node when every declared line was filtered out for
    /// a non-positive duration: that one reports <c>lineCount &gt; 0</c> and is caught by the key
    /// pair, exactly like S1 and S2. This fixture is the declared-nothing case.
    /// </para>
    /// </summary>
    public const string DeliberatelyEmptyPlanVoiceoverOutput =
        """
        {"status":"completed","segments":3,"outputDurationSec":11.0,
         "sentenceCheck":{"applicable":true,"endsAtSentenceBoundary":true},
         "voiceover":{"enabled":true,"applied":false,"unavailable":false,
           "appliedLineCount":0,"lineCount":0,"lines":[],"dropped":[],
           "reason":"no_valid_voiceover_lines",
           "fit":{"applicable":false,"reason":"no_resolved_lines"}}}
        """;

    // ---- ApplyNarrationFitCap: the pure rule ------------------------------------------------

    public static TheoryData<string, int, int> NarrationFitCapCases() => new()
    {
        // ---- the cap fires ------------------------------------------------------------------
        { OverflowingVoiceoverOutput, 9, 4 },
        { OverflowingVoiceoverOutput, 10, 4 },
        { OverflowingVoiceoverOutput, 5, 4 },
        { OverflowingVoiceoverOutput, 4, 4 },
        // A cap never RAISES: a reviewer that already scored below the ceiling keeps its own number.
        { OverflowingVoiceoverOutput, 2, 2 },
        { OverflowingVoiceoverOutput, 0, 0 },

        // ---- inert: not measurable, therefore not a defect ----------------------------------
        { NotApplicableVoiceoverOutput, 9, 9 },
        { NoVoiceoverNodeOutput, 9, 9 },
        // `voiceover` present but carrying only a reason (narration switched off / step missing).
        { """{"voiceover":{"reason":"voiceover_not_enabled","lines":[]}}""", 9, 9 },
        // `fit` present but `applicable` absent.
        { """{"voiceover":{"fit":{"overflowLineCount":1}}}""", 9, 9 },
        // `applicable` true but nothing was actually overflowing.
        { """{"voiceover":{"fit":{"applicable":true,"overflowLineCount":0}}}""", 9, 9 },
        { """{"voiceover":{"fit":{"applicable":true,"overflowLineCount":-1}}}""", 9, 9 },
        // Right number, wrong JSON type: a string is not a count.
        { """{"voiceover":{"fit":{"applicable":true,"overflowLineCount":"1"}}}""", 9, 9 },
        // Right shape, wrong type: `applicable` must be literally true, not truthy.
        { """{"voiceover":{"fit":{"applicable":"true","overflowLineCount":1}}}""", 9, 9 },

        // ---- inert: wrong shapes / malformed, and none of them may throw -------------------
        { "not json at all", 9, 9 },
        { "", 9, 9 },
        { "[1,2,3]", 9, 9 },
        { """{"voiceover":"enabled"}""", 9, 9 },
        { """{"voiceover":{"fit":"applicable"}}""", 9, 9 },
        { """{"voiceover":null}""", 9, 9 },
        // README: the node is read at the TOP LEVEL only. A `voiceover` nested somewhere else is not
        // the compile step's envelope and must not be able to cap anything.
        { """{"result":{"voiceover":{"fit":{"applicable":true,"overflowLineCount":1}}}}""", 9, 9 },
    };

    [Theory]
    [MemberData(nameof(NarrationFitCapCases))]
    public void ApplyNarrationFitCap_caps_only_a_measurable_positive_overflow(string priorOutput, int score, int expected)
    {
        ReviewLoopStepExecutor.ApplyNarrationFitCap(score, priorOutput).Should().Be(expected);
    }

    [Fact]
    public void ApplyNarrationFitCap_reads_an_integral_overflow_count_written_as_a_decimal()
    {
        // `overflowLineCount` is emitted as an int, but a hand-written or round-tripped blob may spell
        // it 1.0. That is still a positive count, so it still caps.
        ReviewLoopStepExecutor.ApplyNarrationFitCap(
                9, """{"voiceover":{"fit":{"applicable":true,"overflowLineCount":1.0}}}""")
            .Should().Be(ReviewLoopStepExecutor.NarrationOverflowScoreCap);
    }

    [Fact]
    public void ApplyNarrationFitCap_tolerates_trailing_prose_after_the_json_object()
    {
        // Same contract as ParseReviewScore/ExtractFeedbackSummary: the prior compile output is clean
        // JSON today, but a helper that throws on a suffix would fail a workflow rather than degrade.
        string raw = NoVoiceoverNodeOutput + "\n\nDone.</invoke>";

        ReviewLoopStepExecutor.ApplyNarrationFitCap(9, raw).Should().Be(9);
    }

    [Fact]
    public void ApplyNarrationFitCap_still_caps_when_a_partial_measurement_reported_unknown_windows()
    {
        // A non-empty `unknownWindowLineIds` means fewer lines were measured, never that the ones that
        // WERE measured stop counting.
        ReviewLoopStepExecutor.ApplyNarrationFitCap(
                9,
                """{"voiceover":{"lines":[],"fit":{"applicable":true,"measuredLineCount":1,"overflowLineCount":1,"overflowTotalSec":2.0,"maxOverflowSec":2.0,"unknownWindowLineIds":["vo9"]}}}""")
            .Should().Be(ReviewLoopStepExecutor.NarrationOverflowScoreCap);
    }

    // ---- ApplyNarrationDeclaredLostCap: the pure rule ----------------------------------------

    public static TheoryData<string, int, int> NarrationDeclaredLostCapCases() => new()
    {
        // ---- the cap fires: narration was declared and not one line reached the picture ---------
        // S1 — the all-lines-lost arm, the shape that also carries the `all_lines_unavailable` token.
        { DeclaredAndLostVoiceoverOutput, 9, 4 },
        { DeclaredAndLostVoiceoverOutput, 10, 4 },
        { DeclaredAndLostVoiceoverOutput, 4, 4 },
        // A cap never RAISES.
        { DeclaredAndLostVoiceoverOutput, 2, 2 },
        { DeclaredAndLostVoiceoverOutput, 0, 0 },
        // S2 — the amix-unavailable arm. The SAME declared-and-lost fact with NO terminal token
        // anywhere on the node (`reason` is the environment sentence instead), so only the
        // declared-vs-applied key pair can fire here. This row is what makes the key-pair form
        // load-bearing rather than decorative: before it, the only producible node carrying this
        // fact also carried the token, and the key pair could be deleted with the suite still green.
        { AmixUnavailableVoiceoverOutput, 9, 4 },
        { AmixUnavailableVoiceoverOutput, 10, 4 },
        { AmixUnavailableVoiceoverOutput, 4, 4 },
        { AmixUnavailableVoiceoverOutput, 2, 2 },
        // The declared-vs-applied key pair on a bare node — no token, no decoration. This is the
        // form every shipped declared-and-lost arm emits; kept separate from the real fixtures so a
        // failure says whether the RULE or a FIXTURE moved.
        { """{"voiceover":{"applied":false,"appliedLineCount":0,"lineCount":3,"lines":[]}}""", 9, 4 },
        { """{"voiceover":{"applied":false,"appliedLineCount":0.0,"lineCount":3}}""", 9, 4 },
        // The TOKEN-ONLY form: `lineCount` 0, so the key pair is false and the terminal token is the
        // only thing left to fire on. NOT a shape the merged head produces — post-0924b7cc the
        // `all_lines_unavailable` arm implies `declaredLineCount > 0`, hence `lineCount > 0`. Kept
        // as the pre-0924b7cc / defensive producer shape, so a node written by an older compile (or
        // replayed from an older run's stored output) still caps.
        { """{"voiceover":{"applied":false,"appliedLineCount":0,"lineCount":0,"reason":"all_lines_unavailable"}}""", 9, 4 },

        // ---- inert: nobody asked for narration -------------------------------------------------
        // S3 — the genuinely empty plan. Identical to S1 and S2 in every key this cap reads except
        // `lineCount`, which is the entire difference.
        { DeliberatelyEmptyPlanVoiceoverOutput, 9, 9 },
        { NoVoiceoverNodeOutput, 9, 9 },
        { NotApplicableVoiceoverOutput, 9, 9 },
        { """{"voiceover":{"enabled":false,"reason":"voiceover_not_enabled","lines":[]}}""", 9, 9 },
        // Declared count zero under the key form: the empty plan, expressed the other way.
        { """{"voiceover":{"applied":false,"appliedLineCount":0,"lineCount":0}}""", 9, 9 },

        // ---- inert: a PARTIAL resolve ----------------------------------------------------------
        // One line of three landed. That is a partial observation, never a total loss, and the fit
        // cap owns whatever may still be measurable about it. (The old `window_not_found`
        // measurement is no longer reachable from the mapped path at all, so it is not a case this
        // predicate can be asked about.)
        { """{"voiceover":{"applied":true,"appliedLineCount":2,"lineCount":3,"reason":"all_lines_unavailable"}}""", 9, 9 },
        { """{"voiceover":{"applied":false,"appliedLineCount":2,"lineCount":3}}""", 9, 9 },
        // `applied` absent, non-boolean, or true: none of those is this defect.
        { """{"voiceover":{"appliedLineCount":0,"lineCount":3}}""", 9, 9 },
        { """{"voiceover":{"applied":"false","appliedLineCount":0,"lineCount":3}}""", 9, 9 },
        { """{"voiceover":{"applied":true,"appliedLineCount":0,"lineCount":3}}""", 9, 9 },
        // `appliedLineCount` absent or wrong-typed: merely a line count, not a declaration of loss.
        { """{"voiceover":{"applied":false,"lineCount":3}}""", 9, 9 },
        { """{"voiceover":{"applied":false,"appliedLineCount":"0","lineCount":3}}""", 9, 9 },
        // `lineCount` present but zero, with no outcome token: the deliberately empty plan.
        { """{"voiceover":{"applied":false,"appliedLineCount":0,"lineCount":0,"reason":"no_valid_voiceover_lines"}}""", 9, 9 },

        // ---- inert: wrong shapes / malformed, and none of them may throw -----------------------
        { "not json at all", 9, 9 },
        { "", 9, 9 },
        { "[1,2,3]", 9, 9 },
        { """{"voiceover":"all_lines_unavailable"}""", 9, 9 },
        { """{"voiceover":null}""", 9, 9 },
        // Top level only: a `voiceover` nested elsewhere is not the compile step's envelope.
        { """{"result":{"voiceover":{"applied":false,"appliedLineCount":0,"lineCount":3}}}""", 9, 9 },
    };

    [Theory]
    [MemberData(nameof(NarrationDeclaredLostCapCases))]
    public void ApplyNarrationDeclaredLostCap_caps_only_narration_that_was_declared_and_lost(
        string priorOutput, int score, int expected)
    {
        ReviewLoopStepExecutor.ApplyNarrationDeclaredLostCap(score, priorOutput).Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(NarrationDeclaredLostCapCases))]
    public void NarrationCapsWouldFire_agrees_with_the_caps_it_is_defined_by(
        string priorOutput, int score, int expected)
    {
        // The predicate the calibrated early stop consults is the two caps evaluated at a sentinel
        // score, so it can never drift from them. What is pinned here is that the SENTINEL is above
        // both ceilings: a cap fires exactly when applying the caps to a score above both of them
        // lowers it. (Not `expected < score` — a cap that fires on an already-low score leaves that
        // score alone, and "did the number change" is a different question from "is the cap live".)
        const int AboveBothCaps = 10;

        bool anyCapIsLive =
            ReviewLoopStepExecutor.ApplyNarrationCaps(AboveBothCaps, priorOutput) < AboveBothCaps;

        ReviewLoopStepExecutor.NarrationCapsWouldFire(priorOutput).Should().Be(anyCapIsLive);

        // And the sentinel must not have been chosen below a ceiling, which would silently make the
        // early stop blind to a cap it is supposed to defer to.
        ReviewLoopStepExecutor.UncappedProbeScore.Should().BeGreaterThan(AboveBothCaps);

        // Sanity on the fixtures themselves: a live cap can only ever LOWER a capped score.
        expected.Should().BeLessThanOrEqualTo(score);
    }

    // ---- the cap on the REAL execution path -------------------------------------------------

    /// <summary>The score a reviewer that ignored the prompt's instruction would return — above every
    /// video template's MinScore (8), so only the cap can put it below the threshold.</summary>
    private const string GreenReviewOutput = """{"score":9,"passesReview":true,"summary":"looks good"}""";

    private static StepExecutionContext CapContext(string accumulatedOutput, int minScore = 8, int iterationCount = 0)
    {
        // Step order 2 is the story editor — the loop target. Step order 4 is this ReviewLoop step,
        // mirroring the video templates' shape (loop target BELOW the compile step, so the re-run
        // window [2,6) contains the step whose output is being judged).
        var loopTarget = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.Agent,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = AgentType.VideoStoryEditor, Name = "VideoStoryEditor" }
        };

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = loopTarget.WorkflowDefinitionId,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 4,
            StepType = StepType.ReviewLoop,
            MinScore = minScore,
            MaxIterations = 3,
            LoopTargetStepOrder = 2,
            AgentDefinition = new AgentDefinition
            {
                Id = Guid.NewGuid(),
                AgentType = AgentType.VideoReviewAgent,
                Name = "VideoReviewAgent"
            }
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = step,
            AllSteps = [loopTarget, step],
            AccumulatedOutput = accumulatedOutput,
            StepOutputHistory = [new StepOutputHistoryEntry(4, "VideoCompile", accumulatedOutput)],
            CurrentStepIndex = 1,
            IterationCount = iterationCount,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    private static ReviewLoopStepExecutor CapExecutor(string reviewerOutput) =>
        CapExecutor(reviewerOutput, NullLogger<ReviewLoopStepExecutor>.Instance);

    private static ReviewLoopStepExecutor CapExecutor(
        string reviewerOutput, Microsoft.Extensions.Logging.ILogger<ReviewLoopStepExecutor> logger)
    {
        var agent = new Mock<IReelBoltAgent>();
        agent.Setup(a => a.Name).Returns(AgentType.VideoReviewAgent.ToString());
        agent.Setup(a => a.AgentType).Returns(AgentType.VideoReviewAgent);
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = reviewerOutput, Success = true, TokensUsed = 120 });

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent.Object);

        var accessor = new Mock<IWorkflowExecutionContextAccessor>();
        accessor.Setup(a => a.BeginScope(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>()))
            .Returns(Mock.Of<IDisposable>());
        accessor.Setup(a => a.Current).Returns((WorkflowExecutionContext?)null);

        // The gate is never configured on these contexts (`ReviewLoopConfigJson` is null), so an
        // unconfigured mock is the honest one: reaching it would be a bug.
        return new ReviewLoopStepExecutor(
            registry.Object,
            accessor.Object,
            new Mock<IDecisionGate>().Object,
            logger);
    }

    [Fact]
    public async Task A_green_reviewer_score_over_an_overflowing_narration_still_loops_back()
    {
        StepExecutionContext context = CapContext(OverflowingVoiceoverOutput);

        StepExecutionResult result = await CapExecutor(GreenReviewOutput).ExecuteAsync(context);

        // 9 >= MinScore 8 would have advanced. The cap put it at 4, so the step iterates and the
        // retry re-runs the loop target — the whole point of the ticket.
        result.NextStepIndex.Should().Be(0, "AllSteps index 0 is step order 2, the loop target");
        result.NewIterationCount.Should().Be(1);
        result.IterationNumber.Should().Be(1);
        result.Status.Should().Be(StepStatus.Completed);

        // The reviewer's own output is persisted exactly as the reviewer wrote it: the cap adjusts the
        // number the loop decision uses, never the JSON the retry is handed as feedback.
        result.Output.Should().Be(GreenReviewOutput);
    }

    [Fact]
    public async Task A_green_reviewer_score_does_not_loop_when_the_fit_is_not_applicable()
    {
        // The degraded compile: fit present, applicable false. NOT MEASURABLE is not a defect, so the
        // reviewer's own 9 stands and the step advances.
        //
        // NOTE on scope. QA's condition on this ticket read as though THIS fixture were the
        // declared-and-lost shape that must now be gated, because it carries `applied:false,
        // appliedLineCount:0`. It is not: its declared count is ZERO and it carries no
        // `all_lines_unavailable` outcome token, so no declared-and-lost form fires on it at all —
        // it is the deliberately empty plan (S3) with the node's `reason`/`unavailable` keys
        // stripped, and the program lead's ruling requires it to stay inert. It is now labelled
        // DEFENSIVE-ONLY, since the shipped compile always writes both of those keys. The real
        // declared-and-lost shapes are `DeclaredAndLostVoiceoverOutput` (S1) and
        // `AmixUnavailableVoiceoverOutput` (S2), gated by the two tests around this one.
        StepExecutionContext context = CapContext(NotApplicableVoiceoverOutput);

        StepExecutionResult result = await CapExecutor(GreenReviewOutput).ExecuteAsync(context);

        result.NextStepIndex.Should().Be(context.CurrentStepIndex + 1);
        result.NewIterationCount.Should().Be(1);
    }

    [Fact]
    public async Task A_green_reviewer_score_over_declared_and_lost_narration_loops_back()
    {
        // QA's high finding 2, as an executed probe: a compile that applied ZERO narration lines
        // while declaring some, judged green by the reviewer. The fit cap is inert here by design
        // (the node carries no measurable fit), so before this cap the loop advanced and a
        // narration-less render shipped as a pass — the exact failure this initiative exists to
        // prevent.
        StepExecutionContext context = CapContext(DeclaredAndLostVoiceoverOutput);

        StepExecutionResult result = await CapExecutor(GreenReviewOutput).ExecuteAsync(context);

        // 9 >= MinScore 8 would have advanced. The declared-and-lost cap put it at 4, so the step
        // iterates and the retry re-runs the loop target.
        result.NextStepIndex.Should().Be(0, "AllSteps index 0 is step order 2, the loop target");
        result.NewIterationCount.Should().Be(1);
        result.Status.Should().Be(StepStatus.Completed);

        // The reviewer's own output is still persisted verbatim.
        result.Output.Should().Be(GreenReviewOutput);
    }

    [Fact]
    public async Task A_green_reviewer_score_over_amix_unavailable_narration_loops_back()
    {
        // Ruling 1, as an executed probe. The amix-unavailable arm is the one declared-and-lost
        // shape that carries NO `all_lines_unavailable` token: its `reason` describes the ffmpeg
        // build instead. So this is the test that proves the KEY PAIR — not the token — is what
        // keeps the gate alive on the shipped node; delete the `lineCount > 0` arm of
        // `IsDeclaredAndLostNarration` and this test is the one that goes red.
        //
        // It is also the arm a loop cannot fix: the cause is environmental, and the loop will end
        // not-green after MaxIterations. That is deliberate — an inert cap here would mean a green
        // render with no narration, the exact failure this initiative exists to prevent.
        StepExecutionContext context = CapContext(AmixUnavailableVoiceoverOutput);

        StepExecutionResult result = await CapExecutor(GreenReviewOutput).ExecuteAsync(context);

        result.NextStepIndex.Should().Be(0, "AllSteps index 0 is step order 2, the loop target");
        result.NewIterationCount.Should().Be(1);
        result.Status.Should().Be(StepStatus.Completed);

        result.Output.Should().Be(GreenReviewOutput);
    }

    [Fact]
    public async Task A_green_reviewer_score_does_not_loop_for_a_deliberately_empty_narration_plan()
    {
        // The decisive INERTNESS control on the real path, and the reason the two declared-and-lost
        // fixtures above differ from this one in exactly ONE key — `lineCount`, 0 here against 3
        // there. If the cap were keyed on `applied:false, appliedLineCount:0` alone, this correct run
        // would be capped and would loop the reviewer forever over narration nobody asked for.
        StepExecutionContext context = CapContext(DeliberatelyEmptyPlanVoiceoverOutput);

        StepExecutionResult result = await CapExecutor(GreenReviewOutput).ExecuteAsync(context);

        result.NextStepIndex.Should().Be(context.CurrentStepIndex + 1);
        result.NewIterationCount.Should().Be(1);
    }

    [Fact]
    public async Task A_green_reviewer_score_does_not_loop_when_there_is_no_voiceover_node_at_all()
    {
        // The other inert shape: a compile summary from a workflow with no narration.
        StepExecutionContext context = CapContext(NoVoiceoverNodeOutput);

        StepExecutionResult result = await CapExecutor(GreenReviewOutput).ExecuteAsync(context);

        result.NextStepIndex.Should().Be(context.CurrentStepIndex + 1);
    }

    [Fact]
    public async Task The_cap_applies_to_the_reviewers_score_and_not_to_a_pre_existing_low_one()
    {
        // The reviewer obeyed the prompt and scored 3. The cap is min(), so 3 survives untouched — and
        // the loop still iterates, which is what a genuinely failing edit should do.
        StepExecutionContext context = CapContext(OverflowingVoiceoverOutput);

        StepExecutionResult result = await CapExecutor("""{"score":3,"passesReview":false,"issues":["vo2 overruns its shot"]}""")
            .ExecuteAsync(context);

        result.NextStepIndex.Should().Be(0);
    }

    [Fact]
    public async Task A_malformed_prior_output_leaves_the_review_path_byte_identical()
    {
        // Malformed prior output must never throw and must never change the decision: 9 >= 8 advances.
        StepExecutionContext context = CapContext("not json at all");

        StepExecutionResult result = await CapExecutor(GreenReviewOutput).ExecuteAsync(context);

        result.NextStepIndex.Should().Be(context.CurrentStepIndex + 1);
        result.Output.Should().Be(GreenReviewOutput);
    }

    [Fact]
    public async Task The_cap_only_ever_loops_a_two_point_overflow_once_the_iteration_budget_allows_it()
    {
        // Belt and braces on the interaction with MaxIterations: at the last permitted iteration the
        // capped score can no longer loop back, and the step must still complete cleanly with the
        // reviewer's output.
        StepExecutionContext context = CapContext(OverflowingVoiceoverOutput, iterationCount: 2);

        StepExecutionResult result = await CapExecutor(GreenReviewOutput).ExecuteAsync(context);

        result.NextStepIndex.Should().Be(context.CurrentStepIndex + 1);
        result.NewIterationCount.Should().Be(3);
        result.Status.Should().Be(StepStatus.Completed);
    }

    // ---- needs attention: an exhausted loop below the minimum is reported, never silently Passed ----

    [Fact]
    public async Task An_exhausted_loop_below_the_minimum_reports_ReviewExhausted_and_keeps_the_step_Completed()
    {
        StepExecutionContext context = CapContext("""{"ok":true}""", minScore: 8, iterationCount: 2);

        StepExecutionResult result = await CapExecutor("""{"score":5,"passesReview":false}""").ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        result.Output.Should().Be("""{"score":5,"passesReview":false}""");
        result.ReviewExhausted.Should().Be(new ReviewExhausted(StepOrder: 4, Score: 5, MinScore: 8, Iterations: 3));
        ReviewExhausted.ToAttentionJson(result.ReviewExhausted!)
            .Should().Be("""{"reason":"review_below_minimum","stepOrder":4,"score":5,"minScore":8,"iterations":3}""");
    }

    [Fact]
    public async Task A_passing_review_reports_no_ReviewExhausted()
    {
        StepExecutionResult result = await CapExecutor(GreenReviewOutput).ExecuteAsync(CapContext("""{"ok":true}""", iterationCount: 2));

        result.ReviewExhausted.Should().BeNull();
    }

    [Fact]
    public async Task A_loop_that_loops_back_reports_no_ReviewExhausted()
    {
        StepExecutionResult result = await CapExecutor("""{"score":3,"passesReview":false}""").ExecuteAsync(CapContext("""{"ok":true}""", iterationCount: 0));

        result.NextStepIndex.Should().Be(0);
        result.ReviewExhausted.Should().BeNull();
    }
}
