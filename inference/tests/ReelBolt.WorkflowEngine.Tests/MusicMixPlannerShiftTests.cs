using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class MusicMixPlannerShiftTests
{
    [Fact]
    public void Windows_shift_by_the_cold_open_and_quiet_clips_are_lifted_with_the_bed()
    {
        // 6.9 s body whose whole length is lifted, 9 s cold open, 4 s end card: the bed covers 19.9 s.
        IReadOnlyList<MusicLiftWindow> w = MusicMixPlanner.ShiftForGeneratedClips(
            [new MusicLiftWindow(0, 6.9)], 9, 6.9, 4, coldOpenQuiet: true, endCardQuiet: true);

        w.Should().ContainSingle();
        w[0].StartSec.Should().Be(0);
        w[0].EndSec.Should().BeApproximately(19.9, 1e-9);
    }

    [Fact]
    public void A_clip_that_plays_its_own_sound_keeps_the_bed_ducked_and_a_ducked_edge_gets_its_own_window()
    {
        IReadOnlyList<MusicLiftWindow> w = MusicMixPlanner.ShiftForGeneratedClips(
            [new MusicLiftWindow(2, 4)], 9, 6.9, 4, coldOpenQuiet: false, endCardQuiet: true);

        w.Should().HaveCount(2);
        w[0].Should().Be(new MusicLiftWindow(11, 13));
        w[1].Should().Be(new MusicLiftWindow(15.9, 19.9));
    }
}
