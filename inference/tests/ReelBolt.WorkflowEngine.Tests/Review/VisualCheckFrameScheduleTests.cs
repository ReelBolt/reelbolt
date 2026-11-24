using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Review;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Review;

/// <summary><see cref="VisualCheckFrameSchedule"/>: N evenly spaced frames plus the last frame, numbered from 1.</summary>
public class VisualCheckFrameScheduleTests
{
    [Fact]
    public void Eight_frames_of_a_forty_second_film_are_five_seconds_apart_plus_the_last_frame()
    {
        IReadOnlyList<VisualCheckFrameTime> times = VisualCheckFrameSchedule.For(40, 8);

        times.Select(t => t.Frame).Should().Equal(1, 2, 3, 4, 5, 6, 7, 8, 9);
        times.Select(t => t.AtSec).Should().Equal(0, 5, 10, 15, 20, 25, 30, 35, 39.9);
    }

    [Fact]
    public void Times_are_rounded_to_hundredths()
    {
        IReadOnlyList<VisualCheckFrameTime> times = VisualCheckFrameSchedule.For(10, 3);

        times.Select(t => t.AtSec).Should().Equal(0, 3.33, 6.67, 9.9);
    }

    [Fact]
    public void A_very_short_clip_never_seeks_before_zero_or_repeats_a_time()
    {
        IReadOnlyList<VisualCheckFrameTime> times = VisualCheckFrameSchedule.For(0.05, 4);

        times.Should().NotBeEmpty();
        times.Select(t => t.AtSec).Should().OnlyContain(t => t >= 0 && t < 0.05);
        times.Select(t => t.AtSec).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void An_unknown_duration_schedules_nothing(double duration) =>
        VisualCheckFrameSchedule.For(duration, 8).Should().BeEmpty();

    [Fact]
    public void The_frame_count_is_clamped_to_the_config_bounds()
    {
        VisualCheckFrameSchedule.For(60, 1).Should().HaveCount(3, "two evenly spaced plus the last");
        VisualCheckFrameSchedule.For(60, 99).Should().HaveCount(17, "sixteen evenly spaced plus the last");
    }
}
