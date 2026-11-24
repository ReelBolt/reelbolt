#nullable enable
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>QA-DYN-54: the track's grid facts read at the body's time (the bed starts with the film, a cold open comes before the body).</summary>
public class MusicFilmTimeTests
{
    // 140 bpm, beat phase 0.1, bar phase 0.1 + one beat, drop at 6.884 (the prod film f1e9be7a's track), outro at 20.
    private static MusicBeatAnalysis Grid() => new(140, 60.0 / 140, 0.1, 0.1 + 60.0 / 140, 60, 6.884, 20, 0.9);

    [Fact]
    public void A_zero_shift_is_the_analysis_as_it_always_was()
    {
        MusicBeatAnalysis g = Grid();
        MusicFilmTime.ToBodyTime(g, 0, out bool inColdOpen).Should().BeSameAs(g);
        inColdOpen.Should().BeFalse();
        MusicFilmTime.BodyShiftSec(0).Should().Be(0);
    }

    [Fact]
    public void A_drop_that_plays_inside_the_cold_open_is_no_drop_of_the_body_and_is_reported()
    {
        MusicBeatAnalysis body = MusicFilmTime.ToBodyTime(Grid(), MusicFilmTime.BodyShiftSec(9.44), out bool inColdOpen);
        inColdOpen.Should().BeTrue("6.884 s is before the body starts at 9.44 s");
        body.DropSec.Should().BeNull();
        body.OutroSec.Should().BeApproximately(20 - 9.44, 1e-9);
    }

    [Fact]
    public void A_drop_inside_the_body_moves_back_by_the_cold_open_and_the_phases_keep_the_true_bar_phase()
    {
        MusicBeatAnalysis body = MusicFilmTime.ToBodyTime(Grid(), MusicFilmTime.BodyShiftSec(2.2), out bool inColdOpen);
        inColdOpen.Should().BeFalse();
        body.DropSec.Should().BeApproximately(6.884 - 2.2, 1e-9);
        // The body's beat lines are the track's own beats seen 2.2 s later: every one of them is a beat of the track.
        foreach (double t in body.BeatTimes(10))
            ((t + 2.2 - 0.1) / (60.0 / 140)).Should().BeApproximately(System.Math.Round((t + 2.2 - 0.1) / (60.0 / 140)), 1e-3);
        // and the bar lines stay a whole bar apart on that phase (one beat after the beat phase: 4 beats per bar)
        foreach (double t in body.BarTimes(10))
            ((t + 2.2 - (0.1 + 60.0 / 140)) / (4 * 60.0 / 140)).Should().BeApproximately(System.Math.Round((t + 2.2 - (0.1 + 60.0 / 140)) / (4 * 60.0 / 140)), 1e-3);
        body.BeatPhaseSec.Should().BeInRange(0, body.BeatSec);
        body.BarPhaseSec.Should().BeInRange(0, body.BarSec);
    }

    [Fact]
    public void Lines_that_play_before_the_body_are_left_out()
    {
        List<double> lines = MusicFilmTime.LinesInBodyTime([0.1, 0.5, 1.0, 3.0], -1.0);
        lines.Should().Equal(0.0, 2.0);
    }
}
