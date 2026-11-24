using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <c>TargetDurationMode: WholeShots</c> (docs/video-editing.md "Picking whole shots for a target length"):
/// a subset of whole spans closest to the target, never above +10%, never cutting inside a span.
/// </summary>
public class WholeShotPlannerTests
{
    [Fact]
    public void A_cut_already_within_the_ceiling_keeps_every_span()
    {
        WholeShotPlan plan = WholeShotPlanner.Plan([10, 8, 7], null, 25);

        plan.Kept.Should().Equal(0, 1, 2);
        plan.Dropped.Should().BeEmpty();
        plan.AchievedSec.Should().Be(25);
    }

    [Fact]
    public void A_cut_shorter_than_the_target_is_never_lengthened_or_dropped_from()
    {
        WholeShotPlan plan = WholeShotPlanner.Plan([3, 4], null, 60);

        plan.Kept.Should().Equal(0, 1);
        plan.AchievedSec.Should().Be(7);
    }

    [Fact]
    public void Without_a_target_nothing_is_dropped()
    {
        WholeShotPlanner.Plan([30, 30], null, 0).Dropped.Should().BeEmpty();
    }

    [Fact]
    public void Without_ranks_the_longest_spans_are_taken_first_after_the_first_and_last_that_fit()
    {
        // first 10 + last 12 = 22; of the rest 8 fits (30), 7/6/5/4 would pass 33.
        WholeShotPlan plan = WholeShotPlanner.Plan([10, 8, 7, 6, 5, 4, 12], null, 30);

        plan.Kept.Should().Equal(0, 1, 6);
        plan.Dropped.Should().Equal(2, 3, 4, 5);
        plan.AchievedSec.Should().Be(30);
    }

    [Fact]
    public void The_total_never_exceeds_the_target_by_more_than_ten_percent()
    {
        double[] durations = [7, 9, 6, 8, 5, 10, 4];
        foreach (double target in new double[] { 12, 15, 20, 26, 31 })
        {
            WholeShotPlan plan = WholeShotPlanner.Plan(durations, null, target);

            plan.AchievedSec.Should().BeLessThanOrEqualTo(target * 1.1 + 1e-9, $"target {target}");
            plan.Kept.Sum(i => durations[i]).Should().BeApproximately(plan.AchievedSec, 1e-9);
        }
    }

    [Fact]
    public void A_first_or_last_span_that_does_not_fit_is_dropped()
    {
        // 40 s opener cannot fit a 20 s target; the closer (6) does.
        WholeShotPlan plan = WholeShotPlanner.Plan([40, 9, 8, 6], null, 20);

        plan.Kept.Should().NotContain(0);
        plan.Kept.Should().Contain(3);
        plan.AchievedSec.Should().BeLessThanOrEqualTo(22);
    }

    [Fact]
    public void Higher_ranked_spans_are_preferred_and_without_ranks_the_longer_wins_ties_by_order()
    {
        double[] durations = [2, 4, 4, 4, 2];

        WholeShotPlanner.Plan(durations, [0, 1, 3, 2, 0], 8).Kept.Should().Equal(0, 2, 4);
        WholeShotPlanner.Plan(durations, null, 8).Kept.Should().Equal(0, 1, 4);
    }

    [Fact]
    public void Kept_spans_stay_in_the_editors_order()
    {
        WholeShotPlan plan = WholeShotPlanner.Plan([1, 9, 2, 8, 3], [0, 0, 5, 4, 0], 11);

        plan.Kept.Should().BeInAscendingOrder();
    }

    [Fact]
    public void When_no_span_fits_the_ceiling_nothing_is_chosen_and_the_reason_says_so()
    {
        WholeShotPlan plan = WholeShotPlanner.Plan([30, 40], null, 10);

        plan.Kept.Should().BeEmpty();
        plan.Reason.Should().Be("no_span_fits");
    }

    [Fact]
    public void A_swap_between_equal_ranked_spans_gets_closer_to_the_target()
    {
        // Opener and closer (3 + 3) fit; longest-first adds the 6 (12 s), but swapping it for the 5 lands on 11.
        WholeShotPlan plan = WholeShotPlanner.Plan([3, 6, 5, 4, 3], null, 11);

        plan.AchievedSec.Should().BeApproximately(11, 1e-9);
        plan.Kept.Should().Equal(0, 2, 4);
    }

    [Fact]
    public void The_closest_whole_shot_set_wins_over_the_longest_first_fill_and_no_kept_span_is_cut_inside()
    {
        // A 10 s target on 0.5, 7, 4, 4, 0.5 (ceiling 11 s). The longest-first fill takes the opener, the
        // 7 s shot and the closer for 8.0 s, and its one-for-one swap has no equal-ranked pair to try; the
        // opener + both 4 s shots + closer is 9.0 s, nearer the target, and needs a 1-for-2 replacement.
        WholeShotPlan plan = WholeShotPlanner.Plan([0.5, 7, 4, 4, 0.5], null, 10);

        plan.Dropped.Should().Equal(new[] { 1 }, "the fill's 7 s shot is not in the nearest set");
        plan.Kept.Should().Equal(0, 2, 3, 4);
        plan.AchievedSec.Should().BeApproximately(9.0, 1e-9);

        // And the cut is made of whole planned spans: every kept span is one of them, untouched.
        double[] durations = [0.5, 7, 4, 4, 0.5];
        var spans = Spans(durations);
        var (keptSpans, report) = WholeShotFit.Apply(spans, 10);

        keptSpans.Should().Equal(plan.Kept.Select(i => spans[i]));
        keptSpans.Should().OnlyContain(span => spans.Contains(span), "no span is cut inside");
        keptSpans.Sum(s => s.End - s.Start).Should().BeApproximately(9.0, 1e-9);
        report!["achievedSec"]!.GetValue<double>().Should().Be(9.0);
    }

    [Fact]
    public void A_long_span_list_still_lands_on_the_nearest_total_under_the_ceiling()
    {
        // 400 spans of 1.00..2.00 s against a 100 s target: the search has to stay bounded (it quantises
        // into at most WholeShotPlanner.MaxSearchUnits units) and still reach the target exactly.
        double[] durations = [.. Enumerable.Range(0, 400).Select(i => 1.0 + (i % 5) * 0.25)];

        WholeShotPlan plan = WholeShotPlanner.Plan(durations, null, 100);

        plan.AchievedSec.Should().BeLessThanOrEqualTo(110 + 1e-9);
        plan.AchievedSec.Should().BeGreaterThanOrEqualTo(100 - 1e-6, "quarter-second spans can reach 100 s");
        plan.Kept.Should().BeInAscendingOrder();
        plan.Kept.Count.Should().BeLessThan(400, "a 400-span cut runs far past 110 s");
    }

    [Fact]
    public void The_report_names_chosen_and_dropped_spans_and_the_achieved_duration()
    {
        WholeShotPlan plan = WholeShotPlanner.Plan([10, 8, 7, 6, 5, 4, 12], null, 30);
        (double Start, double End, int SourceIndex)[] spans =
            [(0, 10, 0), (10, 18, 0), (0, 7, 1), (7, 13, 1), (13, 18, 1), (18, 22, 1), (0, 12, 2)];

        JsonObject report = plan.ToReport(spans);

        report["mode"]!.GetValue<string>().Should().Be("WholeShots");
        report["targetSec"]!.GetValue<double>().Should().Be(30);
        report["achievedSec"]!.GetValue<double>().Should().Be(30);
        report["editedSec"]!.GetValue<double>().Should().Be(52);
        report["keptSpans"]!.AsArray().Select(n => n!["index"]!.GetValue<int>()).Should().Equal(0, 1, 6);
        report["droppedSpans"]!.AsArray().Select(n => n!["index"]!.GetValue<int>()).Should().Equal(2, 3, 4, 5);
        report["droppedSpans"]![0]!["sourceIndex"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public void Ranks_of_the_wrong_length_are_ignored()
    {
        WholeShotPlanner.Plan([2, 4, 4, 4, 2], [9, 9], 8).Kept.Should().Equal(0, 1, 4);
    }

    private static List<(double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds)> Spans(params double[] durations)
    {
        List<(double, double, int, IReadOnlyList<string>)> spans = [];
        double at = 0;
        for (int i = 0; i < durations.Length; i++)
        {
            spans.Add((at, at + durations[i], 0, new[] { $"s{i}" }));
            at += durations[i];
        }
        return spans;
    }

    [Fact]
    public void Apply_returns_only_the_kept_spans_with_their_origin_ids_and_a_report()
    {
        var (spans, report) = WholeShotFit.Apply(Spans(10, 8, 7, 6, 5, 4, 12), 30);

        spans.Select(s => s.OriginIds[0]).Should().Equal("s0", "s1", "s6");
        report!["achievedSec"]!.GetValue<double>().Should().Be(30);
        report["droppedSpans"]!.AsArray().Should().HaveCount(4);
    }

    [Fact]
    public void Apply_leaves_a_cut_that_already_fits_alone_and_reports_nothing()
    {
        var input = Spans(5, 5);

        var (spans, report) = WholeShotFit.Apply(input, 10);

        spans.Should().Equal(input);
        report.Should().BeNull();
    }

    [Fact]
    public void Apply_keeps_the_cut_and_says_why_when_no_span_fits()
    {
        var input = Spans(30, 40);

        var (spans, report) = WholeShotFit.Apply(input, 10);

        spans.Should().Equal(input);
        report!["reason"]!.GetValue<string>().Should().Be("no_span_fits");
    }
}
