using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Editing.Proposals;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

[Collection(ComplexityGuardCollection.Name)]
public sealed class ComplexityGuardTests
{
    private static long sink;

    private static Action Linear(int n) => () =>
    {
        long acc = 0;
        for (int i = 0; i < n; i++)
        {
            acc += (i * 31L) ^ (acc >> 3);
        }

        sink = acc;
    };

    private static Action Quadratic(int n) => () =>
    {
        long acc = 0;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                acc += (i ^ j) + (acc >> 3);
            }
        }

        sink = acc;
    };

    /// <summary>Stand-in for the old quadratic word guard: every close gap scans all words.</summary>
    private static Action OldQuadraticMerge(int n)
    {
        (double Start, double End)[] words = ComplexityGuardRegressionTests.GuardWords(n);
        (double Start, double End)[] spans = ComplexityGuardRegressionTests.GuardSpans(n);
        return () =>
        {
            var merged = new List<(double Start, double End)>();
            foreach ((double s, double e) in spans.OrderBy(x => x.Start))
            {
                bool join = false;
                if (merged.Count > 0)
                {
                    double prevEnd = merged[^1].End;
                    join = s <= prevEnd || (s - prevEnd < ProposalCuts.MinKeepSec && !words.Any(w => w.Start < s && w.End > prevEnd));
                }

                if (join) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, e));
                else merged.Add((s, e));
            }

            sink = merged.Count;
        };
    }

    [Fact]
    public void A_linear_workload_passes() => ComplexityGuard.AssertNearLinear(Linear, 5_000_000, name: "linear-standin");

    [Fact]
    public void A_quadratic_workload_fails()
    {
        Exception ex = Assert.ThrowsAny<Exception>(() => ComplexityGuard.AssertNearLinear(Quadratic, 2_000, name: "quadratic-standin"));
        ex.Message.Should().Contain("not near-linear");
    }

    [Fact]
    public void The_old_quadratic_merge_word_guard_shape_is_detected()
    {
        Exception ex = Assert.ThrowsAny<Exception>(() => ComplexityGuard.AssertNearLinear(OldQuadraticMerge, 4_000, factor: 4, name: "old-merge-standin"));
        ex.Message.Should().Contain("not near-linear");
    }
}
