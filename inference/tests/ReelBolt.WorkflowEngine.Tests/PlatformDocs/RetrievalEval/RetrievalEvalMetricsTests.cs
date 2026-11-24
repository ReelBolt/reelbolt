using System.Collections.Generic;
using AwesomeAssertions;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.PlatformDocs.RetrievalEval;

/// <summary>The pure parts of the retrieval eval harness. These run in the default pass; the harness itself is opt-in.</summary>
public class RetrievalEvalMetricsTests
{
    private static readonly string[] Ranked = ["a#1", "b#1", "c#1", "d#1", "e#1", "f#1"];

    [Fact]
    public void Recall_is_any_gold_in_the_top_k()
    {
        RetrievalEvalMetrics.RecallAt(Ranked, ["e#1"], 5).Should().Be(1);
        RetrievalEvalMetrics.RecallAt(Ranked, ["f#1"], 5).Should().Be(0);
    }

    [Fact]
    public void Mrr_uses_the_first_gold_rank_and_stops_at_k()
    {
        RetrievalEvalMetrics.ReciprocalRank(Ranked, ["c#1", "a#1"], 10).Should().Be(1);
        RetrievalEvalMetrics.ReciprocalRank(Ranked, ["c#1"], 10).Should().BeApproximately(1d / 3, 1e-9);
        RetrievalEvalMetrics.ReciprocalRank(Ranked, ["f#1"], 3).Should().Be(0);
    }

    [Fact]
    public void Ndcg_is_one_for_the_ideal_order_and_lower_when_gold_sinks()
    {
        RetrievalEvalMetrics.Ndcg(Ranked, ["a#1", "b#1"], 10).Should().BeApproximately(1, 1e-9);
        RetrievalEvalMetrics.Ndcg(Ranked, ["c#1"], 10).Should().BeApproximately(0.5, 1e-9); // 1/log2(4) over 1/log2(2)
        RetrievalEvalMetrics.Ndcg(Ranked, ["zzz#1"], 10).Should().Be(0);
    }

    [Fact]
    public void Ndcg_with_several_gold_sections_rewards_ranking_them_all_high()
    {
        // gold a and c, found at ranks 1 and 3: dcg = 1 + 1/log2(4) = 1.5; ideal = 1 + 1/log2(3)
        RetrievalEvalMetrics.Ndcg(Ranked, ["a#1", "c#1"], 10).Should().BeApproximately(1.5 / (1 + 1 / System.Math.Log2(3)), 1e-9);
    }

    [Fact]
    public void Collapse_keeps_the_first_occurrence_of_a_split_section()
    {
        RetrievalEvalMetrics.Collapse(["a#1", "b#1", "a#1"]).Should().Equal("a#1", "b#1");
    }

    [Fact]
    public void Percentile_is_nearest_rank()
    {
        double[] v = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
        RetrievalEvalMetrics.Percentile(v, 50).Should().Be(50);
        RetrievalEvalMetrics.Percentile(v, 95).Should().Be(100);
    }

    [Fact]
    public void A_query_is_pending_kb_until_its_knowledge_base_document_exists()
    {
        var q = new EvalQuery { Id = "Q1", PendingGold = ["assistant-kb/recipes.md#x"] };
        HashSet<string> none = [];
        q.ActiveGold(none, none).Should().BeEmpty();
        HashSet<string> docs = ["assistant-kb/recipes.md"];
        HashSet<string> sections = ["assistant-kb/recipes.md#x"];
        q.ActiveGold(sections, docs).Should().Equal("assistant-kb/recipes.md#x");
    }

    [Fact]
    public void Gates_fail_when_hybrid_gains_less_than_ten_points()
    {
        ConfigResult Cfg(string name, double recall) => new(name, "", new Dictionary<string, SliceMetrics>
        {
            ["all"] = new(10, recall, 0, 0, 0, 0, 10, 10),
            ["non-english"] = new(5, recall, 0, 0, 0, 0, 10, 10)
        }, []);
        var gates = RetrievalEvalGates.Evaluate([Cfg("baseline", 0.5), Cfg("hybrid-a", 0.55)]);
        gates.Should().Contain(g => g.Name.Contains("10 points") && g.Passed == false);
        gates.Should().Contain(g => g.Name.Contains("non-English") && g.Passed == true);
    }
}
