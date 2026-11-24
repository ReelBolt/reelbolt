using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.SpeedRamps;

/// <summary>The pure program retime mapping: durations, monotone maps, inverse, freeze, validation.</summary>
public class RampTimelineTests
{
    [Fact]
    public void Identity_maps_every_time_to_itself()
    {
        var t = RampTimeline.Identity(10);
        t.IsIdentity.Should().BeTrue();
        t.OutputSec.Should().Be(10);
        foreach (double s in new[] { 0, 0.3, 4.5, 10 })
        {
            t.MapToOutput(s).Should().BeApproximately(s, 1e-9);
            t.MapFromOutput(s).Should().BeApproximately(s, 1e-9);
        }
    }

    [Fact]
    public void Slow_motion_stretches_its_window_and_shifts_what_follows()
    {
        // 4..6 at half speed = 4 s: the output is 2 s longer.
        var t = RampTimeline.Build(10, [new RampWindow(4, 6, 0.5)]);
        t.OutputSec.Should().BeApproximately(12, 1e-9);
        t.MapToOutput(3).Should().BeApproximately(3, 1e-9);
        t.MapToOutput(5).Should().BeApproximately(6, 1e-9);
        t.MapToOutput(6).Should().BeApproximately(8, 1e-9);
        t.MapToOutput(9).Should().BeApproximately(11, 1e-9);
        t.MapFromOutput(6).Should().BeApproximately(5, 1e-9);
        t.MapWindow(4, 6).Should().Be((4.0, 8.0));
        t.MapDuration(0, 10).Should().BeApproximately(12, 1e-9);
    }

    [Fact]
    public void Speed_up_shortens_its_window()
    {
        var t = RampTimeline.Build(10, [new RampWindow(2, 6, 2)]);
        t.OutputSec.Should().BeApproximately(8, 1e-9);
        t.MapToOutput(6).Should().BeApproximately(4, 1e-9);
        t.MapToOutput(10).Should().BeApproximately(8, 1e-9);
    }

    [Fact]
    public void Freeze_holds_one_instant_and_pushes_the_rest_later()
    {
        var t = RampTimeline.Build(10, [RampWindow.Freeze(5, 0.5)]);
        t.OutputSec.Should().BeApproximately(10.5, 1e-9);
        t.MapToOutput(5).Should().BeApproximately(5, 1e-9, "the held instant lands where the hold begins");
        t.MapToOutput(5.001).Should().BeApproximately(5.501, 1e-9);
        t.MapFromOutput(5.25).Should().BeApproximately(5, 1e-9, "inside the hold the held instant shows");
        t.MapFromOutput(5.5).Should().BeApproximately(5, 1e-9);
        t.MapFromOutput(6).Should().BeApproximately(5.5, 1e-9);
    }

    [Fact]
    public void Mapping_is_monotone_and_the_inverse_round_trips()
    {
        var t = RampTimeline.Build(12, [new RampWindow(1, 3, 0.5), RampWindow.Freeze(4, 0.4), new RampWindow(6, 9, 2), new RampWindow(9, 10, 0.75)]);
        double prev = -1;
        for (double s = 0; s <= 12; s += 0.05)
        {
            double o = t.MapToOutput(s);
            o.Should().BeGreaterThanOrEqualTo(prev - 1e-9);
            prev = o;
            t.MapFromOutput(o).Should().BeApproximately(s, 1e-6);
        }

        t.OutputSec.Should().BeApproximately(1 + 4 + 1 + 0.4 + 2 + 1.5 + 1 / 0.75 + 2, 1e-9);
    }

    [Fact]
    public void A_stepped_ramp_in_ends_where_it_started_plus_its_slowdown()
    {
        // Four steps from rate 1 towards 0.5 over 2 s: durations 2/rate each quarter.
        double[] rates = [0.9, 0.75, 0.6, 0.5];
        RampWindow[] w = rates.Select((r, i) => new RampWindow(3 + i * 0.5, 3.5 + i * 0.5, r)).ToArray();
        var t = RampTimeline.Build(8, w);
        t.OutputSec.Should().BeApproximately(8 - 2 + rates.Sum(r => 0.5 / r), 1e-9);
    }

    [Theory]
    [InlineData(5, 4, 1.0)]
    [InlineData(-1, 2, 1.0)]
    [InlineData(1, 12, 1.0)]
    [InlineData(1, 2, 0.0)]
    [InlineData(1, 1, 2.0)]
    public void Invalid_windows_are_rejected(double start, double end, double rate)
    {
        Action act = () => RampTimeline.Build(10, [new RampWindow(start, end, rate)]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Overlapping_or_unordered_windows_are_rejected()
    {
        Action act = () => RampTimeline.Build(10, [new RampWindow(2, 5, 2), new RampWindow(4, 6, 2)]);
        act.Should().Throw<ArgumentException>();
    }
}
