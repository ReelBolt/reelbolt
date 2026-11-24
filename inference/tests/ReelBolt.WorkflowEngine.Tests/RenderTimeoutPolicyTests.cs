using System;
using System.Threading;
using Xunit;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;

namespace ReelBolt.WorkflowEngine.Tests;

public class RenderTimeoutPolicyTests
{
    private static readonly RenderTimeoutPolicy Policy = RenderTimeoutPolicy.Default;

    private static CompositionInfo Film(int frames, int width = 1920, int height = 1080) =>
        new("Main", 30, width, height, frames, IsStill: false);

    [Fact]
    public void The_two_minute_render_that_was_killed_at_600s_now_fits()
    {
        // Measured: 3591 frames at 1080p needed ~13 min on a 2-CPU sandbox and was killed at 10.
        int seconds = Policy.Compute(Film(3591));
        seconds.Should().BeGreaterThan(13 * 60 * 2, "it must leave room for a box twice as slow");
        seconds.Should().BeLessThan(45 * 60);
    }

    [Fact]
    public void The_deadline_grows_with_the_films_length()
    {
        int twoMinutes = Policy.Compute(Film(2 * 60 * 30));
        int halfAnHour = Policy.Compute(Film(30 * 60 * 30));
        halfAnHour.Should().BeGreaterThan(twoMinutes * 10);
        halfAnHour.Should().BeGreaterThan(30 * 60 * 30 / 5, "it must cover 5 frames/s");
    }

    [Fact]
    public void Hours_long_films_get_hours_up_to_the_ceiling()
    {
        Policy.Compute(Film(90 * 60 * 30)).Should().Be(RenderTimeoutPolicy.DefaultMaxSeconds);
        Policy.Compute(Film(30 * 60 * 30, 3840, 2160)).Should().Be(RenderTimeoutPolicy.DefaultMaxSeconds);
    }

    [Fact]
    public void A_short_sting_keeps_the_floor_so_a_hang_is_not_waited_on_for_hours()
    {
        Policy.Compute(Film(150)).Should().Be(RenderTimeoutPolicy.DefaultMinSeconds);
        Policy.Compute(new CompositionInfo("Still", null, 1280, 720, 1, IsStill: true))
            .Should().Be(RenderTimeoutPolicy.DefaultMinSeconds);
    }

    [Fact]
    public void Frames_and_scale_arguments_narrow_the_work()
    {
        int whole = Policy.Compute(Film(18000));
        Policy.Compute(Film(18000), ["--frames=0-899"]).Should().BeLessThan(whole);
        Policy.Compute(Film(18000), ["--scale=0.5"]).Should().BeLessThan(whole);
        Policy.Compute(Film(18000), ["--scale", "2"]).Should().BeGreaterThan(whole);
    }

    [Fact]
    public void An_unknown_composition_gets_a_generous_deadline_rather_than_a_short_one()
    {
        Policy.Compute(null).Should().Be(RenderTimeoutPolicy.UnknownCompositionSeconds);
    }
}

public class AgentRunBudgetTests
{
    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Excluded_time_is_credited_back_to_the_run()
    {
        ManualTime time = new(DateTimeOffset.UnixEpoch);
        using CancellationTokenSource cts = new();
        AgentRunBudget budget = new(cts, TimeSpan.FromMinutes(50), time);
        DateTimeOffset original = budget.Deadline;

        using (budget.Exclude(TimeSpan.FromHours(3)))
        {
            budget.Deadline.Should().Be(original + TimeSpan.FromHours(3), "the run must not be cancelled mid-render");
            time.Now += TimeSpan.FromHours(2);
        }

        budget.Deadline.Should().Be(original + TimeSpan.FromHours(2));
        cts.IsCancellationRequested.Should().BeFalse();
    }
}
