using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.SpeedRamps;

/// <summary>Word tables, windows and the exact filter graph of the speed ramps.</summary>
public class SpeedRampFilterTests
{
    [Fact]
    public void Slow_motion_graph_is_pinned()
    {
        var t = RampTimeline.Build(10, [new RampWindow(4, 5, 0.5)]);

        string graph = SpeedRampFilterBuilder.Build(t, hasAudio: true, 30, 1);

        graph.Should().Be(
            "[0:v]trim=start=0:end=4,setpts=PTS-STARTPTS[v0];[0:a]atrim=start=0:end=4,asetpts=PTS-STARTPTS[a0];" +
            "[0:v]trim=start=4:end=5,setpts=(PTS-STARTPTS)*2[v1];[0:a]atrim=start=4:end=5,asetpts=PTS-STARTPTS,atempo=0.5[a1];" +
            "[0:v]trim=start=5:end=10,setpts=PTS-STARTPTS[v2];[0:a]atrim=start=5:end=10,asetpts=PTS-STARTPTS[a2];" +
            "[v0][a0][v1][a1][v2][a2]concat=n=3:v=1:a=1[vcat][aout];[vcat]null,fps=30/1[vout]");
    }

    [Fact]
    public void Freeze_graph_without_audio_is_pinned()
    {
        var t = RampTimeline.Build(3, [RampWindow.Freeze(1.5, 0.6)]);

        SpeedRampFilterBuilder.Build(t, hasAudio: false, 30, 1).Should().Be(
            "[0:v]trim=start=0:end=1.5,setpts=PTS-STARTPTS[v0];" +
            "[0:v]trim=start=1.5:end=1.6,setpts=PTS-STARTPTS,trim=end_frame=1,tpad=stop_mode=clone:stop_duration=0.6,trim=duration=0.6,setpts=PTS-STARTPTS[v1];" +
            "[0:v]trim=start=1.5:end=3,setpts=PTS-STARTPTS[v2];" +
            "[v0][v1][v2]concat=n=3:v=1:a=0,fps=30/1[vout]");
    }

    [Theory]
    [InlineData(1.0, "")]
    [InlineData(0.5, "atempo=0.5")]
    [InlineData(2.0, "atempo=2")]
    [InlineData(0.25, "atempo=0.5,atempo=0.5")]
    [InlineData(4.0, "atempo=2,atempo=2")]
    public void Atempo_chain_stays_inside_the_per_stage_range(double rate, string expected) =>
        SpeedRampFilterBuilder.AtempoChain(rate).Should().Be(expected);

    [Theory]
    [InlineData(0.2)]
    [InlineData(4.5)]
    public void Out_of_range_rates_have_no_chain(double rate) => SpeedRampFilterBuilder.AtempoChain(rate).Should().BeNull();

    [Fact]
    public void An_out_of_range_piece_mutes_its_audio_and_warns()
    {
        var t = RampTimeline.Build(10, [new RampWindow(4, 5, 0.1)]);
        var warnings = new List<string>();

        string graph = SpeedRampFilterBuilder.Build(t, hasAudio: true, 30, 1, warnings);

        warnings.Should().Equal("audio_muted_rate_out_of_range:0.1");
        graph.Should().Contain("[0:a]atrim=start=4:end=4.02,asetpts=PTS-STARTPTS,volume=0,apad=whole_dur=10[a1]");
    }

    [Fact]
    public void Every_word_table_rate_is_inside_the_audio_range()
    {
        foreach (SpeedRampStrength s in System.Enum.GetValues<SpeedRampStrength>())
        {
            SpeedRampFilterBuilder.AtempoChain(SpeedRampPlanner.SlowRate(s)).Should().NotBeNull();
            SpeedRampFilterBuilder.AtempoChain(SpeedRampPlanner.FastRate(s)).Should().NotBeNull();
        }
    }

    [Fact]
    public void Ramp_in_steps_down_to_the_slow_rate_on_the_event_and_ramp_out_steps_back()
    {
        var rampIn = SpeedRampPlanner.Windows(new SpeedRampRequest("e0", SpeedRampKind.RampIn), 5, 10);
        rampIn.Should().HaveCount(SpeedRampPlanner.RampSteps);
        rampIn[0].Start.Should().BeApproximately(4, 1e-9);
        rampIn[^1].End.Should().BeApproximately(5, 1e-9, "full slow motion arrives on the event");
        rampIn[^1].Rate.Should().BeApproximately(0.5, 1e-9);
        rampIn.Select(w => w.Rate).Should().BeInDescendingOrder();

        var rampOut = SpeedRampPlanner.Windows(new SpeedRampRequest("e0", SpeedRampKind.RampOut), 5, 10);
        rampOut[0].Start.Should().BeApproximately(5, 1e-9);
        rampOut[0].Rate.Should().BeApproximately(0.5, 1e-9);
        rampOut.Select(w => w.Rate).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Windows_are_clamped_to_the_program()
    {
        SpeedRampPlanner.Windows(new SpeedRampRequest("e0", SpeedRampKind.SlowMo), 9.7, 10)[0].End.Should().Be(10);
        SpeedRampPlanner.Windows(new SpeedRampRequest("e0", SpeedRampKind.RampIn), 0.4, 10)[0].Start.Should().Be(0);
    }

    [Theory]
    [InlineData("e0", true)]
    [InlineData("e12", true)]
    [InlineData("t3", false)]
    [InlineData("e01", false)]
    [InlineData("", false)]
    public void Event_ids_must_look_like_e_n(string id, bool ok) => SpeedRampRequest.IsEventId(id).Should().Be(ok);
}
