using System;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video.Dynamics;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>QA-DYN-44: the program's frame layout rounds each boundary from the exact cumulative time, so no cut drifts off its beat.</summary>
public class DynamicsLayoutTests
{
    private const double Fps = 30.0;
    private const double Beat = 60.0 / 140.0; // 12.857 frames

    private static DynamicsSpan Span(int i, double startFrame, double beats, double? limit = null) =>
        new($"p{i}", 0, startFrame / Fps, startFrame / Fps + beats * Beat, true, limit);

    [Fact]
    public void Spans_of_a_fraction_of_a_frame_each_land_on_the_rounded_cumulative_boundary()
    {
        // Eight 4-beat spans (51.43 frames) from whole-frame source starts: laid per span they were all 51 frames (cut k at 51k, k x 0.43 frames early).
        DynamicsSpan[] spans = Enumerable.Range(0, 8).Select(i => Span(i, i * 100, 4)).ToArray();

        (long[] _, long[] end, long[] _) = DynamicsPlanner.Layout(spans, Fps);

        for (int i = 0; i < spans.Length; i++)
            Math.Abs(end[i] - (i + 1) * 4 * Beat * Fps).Should().BeLessThanOrEqualTo(0.5 + 1e-9, $"cut {i} is within half a frame of its exact time");
        (end[^1] / Fps).Should().BeApproximately(8 * 4 * Beat, 0.5 / Fps);
    }

    [Fact]
    public void No_span_reads_more_than_one_source_frame_past_its_window_nor_into_the_next_shot()
    {
        DynamicsSpan[] spans = Enumerable.Range(0, 8).Select(i => Span(i, i * 100, 4)).ToArray();
        (long[] start, long[] end, long[] _) = DynamicsPlanner.Layout(spans, Fps);
        for (int i = 0; i < spans.Length; i++)
            (end[i] - start[i]).Should().BeLessThanOrEqualTo((long)Math.Round(4 * Beat * Fps) + 1);

        // A span that ends on the next shot's first frame has no frame to take: its length stays its own window.
        DynamicsSpan[] limited = Enumerable.Range(0, 8).Select(i => Span(i, i * 100, 4, limit: i * 100 / Fps + 4 * Beat)).ToArray();
        (long[] s2, long[] e2, long[] _) = DynamicsPlanner.Layout(limited, Fps);
        for (int i = 0; i < limited.Length; i++)
            (e2[i] - s2[i]).Should().BeLessThanOrEqualTo((long)Math.Round(4 * Beat * Fps), "limited by the next shot");
    }

    [Fact]
    public void Whole_frame_spans_lay_out_exactly_as_before()
    {
        DynamicsSpan[] spans = [new("p0", 0, 0, 2.0, true), new("p1", 0, 5.0, 6.5, true), new("p2", 1, 1.0, 4.0, true)];

        (long[] start, long[] end, long[] src) = DynamicsPlanner.Layout(spans, Fps);

        start.Should().Equal(0, 60, 105);
        end.Should().Equal(60, 105, 195);
        src.Should().Equal(0, 150, 30);
    }
}
