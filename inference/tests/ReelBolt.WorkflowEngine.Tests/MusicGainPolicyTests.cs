using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <c>VideoCompileStepConfig.MusicGainDb</c> (docs/video-editing.md "Background music"): an absolute
/// track gain that bypasses the -6 dB clamp, the sole-soundtrack level and the narration-gap lift.
/// Unset must reproduce the pre-feature bed levels exactly.
/// </summary>
public class MusicGainPolicyTests
{
    private const int Sole = VideoCompileStepExecutor.SoleSoundtrackMusicDb;
    private const int Lift = VideoCompileStepExecutor.NarrationGapLiftDb;

    private static MusicBedLevel Resolve(double? absolute, int intensityDb, bool sole = false, bool narration = false) =>
        MusicGainPolicy.Resolve(absolute, intensityDb, sole, Sole, narration, Lift);

    [Fact]
    public void Absolute_zero_is_unity_with_no_limiter_even_for_a_sole_soundtrack()
    {
        MusicBedLevel level = Resolve(0, intensityDb: -20, sole: true);

        level.BedDb.Should().Be(0);
        level.Limit.Should().BeFalse("a limiter would alter the track's peaks and defeat a pass-through");
        level.Source.Should().Be("absolute");
        level.Basis.Should().Be("absolute");
        level.ClampedFromDb.Should().BeNull();
    }

    [Theory]
    [InlineData(-3.5)]
    [InlineData(6)]
    [InlineData(-40)]
    public void Absolute_gain_is_used_exactly_and_is_not_clamped_to_minus_six(double db)
    {
        Resolve(db, intensityDb: -20).BedDb.Should().Be(db);
    }

    [Fact]
    public void Absolute_gain_skips_the_narration_gap_lift()
    {
        Resolve(-12, intensityDb: -20, narration: true).BedDb.Should().Be(-12);
    }

    [Fact]
    public void Unset_gain_clamps_an_intensity_level_above_minus_six_and_says_where_it_came_from()
    {
        MusicBedLevel level = Resolve(null, intensityDb: -3);

        level.BedDb.Should().Be(-6);
        level.Source.Should().Be("intensity");
        level.Basis.Should().Be("intensity");
        level.ClampedFromDb.Should().Be(-3);
    }

    [Fact]
    public void Unset_gain_inside_the_clamp_reports_no_clamp()
    {
        MusicBedLevel level = Resolve(null, intensityDb: -20);

        level.BedDb.Should().Be(-20);
        level.ClampedFromDb.Should().BeNull();
    }

    [Fact]
    public void Unset_gain_keeps_the_sole_soundtrack_level_and_limiter()
    {
        MusicBedLevel level = Resolve(null, intensityDb: -20, sole: true);

        level.BedDb.Should().Be(Sole);
        level.Limit.Should().BeTrue();
        level.Basis.Should().Be("sole_soundtrack");
    }

    [Fact]
    public void Unset_gain_keeps_the_narration_gap_lift_capped_at_minus_six()
    {
        MusicBedLevel level = Resolve(null, intensityDb: -20, narration: true);

        level.BedDb.Should().Be(-8);
        level.Basis.Should().Be("narration_gaps");
        Resolve(null, intensityDb: -14, narration: true).BedDb.Should().Be(-6);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0.0, true)]
    [InlineData(6.0, true)]
    [InlineData(-40.0, true)]
    [InlineData(6.1, false)]
    [InlineData(-40.1, false)]
    [InlineData(double.NaN, false)]
    public void Only_minus_forty_to_plus_six_is_a_valid_gain(double? db, bool valid)
    {
        MusicGainPolicy.IsValid(db).Should().Be(valid);
    }

    [Fact]
    public void The_gain_node_reports_gain_source_and_clamp()
    {
        MusicGainPolicy.ToNode(Resolve(null, intensityDb: -3))["clampedFromDb"]!.GetValue<double>().Should().Be(-3);
        var absolute = MusicGainPolicy.ToNode(Resolve(0, intensityDb: -20));
        absolute["gainDb"]!.GetValue<double>().Should().Be(0);
        absolute["source"]!.GetValue<string>().Should().Be("absolute");
        absolute.ContainsKey("clampedFromDb").Should().BeFalse();
    }
}
