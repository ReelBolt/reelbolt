using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Editing.Proposals;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

// Scaling guards (see ComplexityGuard). They run alone, outside the parallel collections.
[Collection(ComplexityGuardCollection.Name)]
public sealed class ComplexityGuardRegressionTests
{
    private static readonly string[] ScoreKeys = ["overallScore", "score"];

    internal static (double Start, double End)[] GuardWords(int n) => [.. Enumerable.Range(0, 2 * n).Select(i => (i * 1.0, i * 1.0 + 0.5))];

    internal static (double Start, double End)[] GuardSpans(int n) => [.. Enumerable.Range(0, n).Select(i => (i * 2.0 + 0.5, i * 2.0 + 2.3))];

    [Fact]
    public void Merge_word_guard_stays_fast_on_a_very_long_transcript()
    {
        // Scaling guard, not an absolute bound: 8x the transcript must cost ~8x, not ~64x. Gaps are 0.2 s (< MinKeepSec)
        // so every gap reaches the word guard.
        ComplexityGuard.AssertNearLinear(
            n =>
            {
                (double Start, double End)[] words = GuardWords(n);
                (double Start, double End)[] spans = GuardSpans(n);
                return () => ProposalCuts.Merge(spans.ToList(), words.ToList()).Should().NotBeEmpty();
            },
            2_000,
            name: nameof(Merge_word_guard_stays_fast_on_a_very_long_transcript));
    }

    [Fact]
    public void ParseReviewScore_scores_brace_heavy_unbalanced_text_in_linear_time()
    {
        // Scaling guard (4x input must cost ~4x, not ~16x); the old quadratic scan took over a second at 60k.
        ComplexityGuard.AssertNearLinear(
            n =>
            {
                string raw = string.Concat(System.Linq.Enumerable.Repeat("{\"a\":[{", n));
                return () => ReviewLoopStepExecutor.ParseReviewScore(raw);
            },
            2_000,
            name: "ParseReviewScore");
    }

    [Fact]
    public void ExtractScoredJsonObject_is_linear_on_brace_heavy_unbalanced_text()
    {
        // Scaling guard (4x input must cost ~4x, not ~16x); the old quadratic scan took over a second at 60k.
        ComplexityGuard.AssertNearLinear(
            n =>
            {
                string raw = string.Concat(System.Linq.Enumerable.Repeat("{\"a\":[{", n));
                return () => RobustJsonExtractor.ExtractScoredJsonObject(raw, ScoreKeys);
            },
            2_000,
            name: "ExtractScoredJsonObject");
    }
}
