using System;
using Xunit;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Quality;

namespace ReelBolt.WorkflowEngine.Tests.Video.Quality;

public class QualityHelpersTests
{
    private static FrameGridResult Grid(double fps, params byte[] frameValues)
    {
        const int w = 2, h = 2;
        var data = new byte[frameValues.Length * w * h * 3];
        for (var f = 0; f < frameValues.Length; f++)
            Array.Fill(data, frameValues[f], f * w * h * 3, w * h * 3);
        return new FrameGridResult(data, w, h, fps, frameValues.Length);
    }

    [Fact]
    public void Contrast_BlackWhite_Is21()
    {
        ContrastMath.Ratio(new Rgba(0, 0, 0, 1), new Rgba(255, 255, 255, 1)).Should().BeApproximately(21, 0.001);
    }

    [Fact]
    public void Contrast_Grey777OnWhite()
    {
        ContrastMath.TryParseColor("#777777", out var g).Should().BeTrue();
        ContrastMath.Ratio(g, new Rgba(255, 255, 255, 1)).Should().BeApproximately(4.48, 0.02);
    }

    [Theory]
    [InlineData("white", 255, 255, 255, 1.0)]
    [InlineData("black@0.45", 0, 0, 0, 0.45)]
    [InlineData("0xFF8000", 255, 128, 0, 1.0)]
    [InlineData("#00ff00@0.5", 0, 255, 0, 0.5)]
    public void ParseColor_Valid(string s, int r, int g, int b, double a)
    {
        ContrastMath.TryParseColor(s, out var c).Should().BeTrue();
        c.R.Should().Be((byte)r); c.G.Should().Be((byte)g); c.B.Should().Be((byte)b);
        c.A.Should().BeApproximately(a, 1e-9);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("notacolour")]
    [InlineData("#12")]
    [InlineData("white@abc")]
    [InlineData("0xZZZZZZ")]
    public void ParseColor_Garbage_ReturnsFalse(string? s) =>
        ContrastMath.TryParseColor(s, out _).Should().BeFalse();

    [Fact]
    public void Blend_HalfBlackOverWhite_IsMidGrey()
    {
        var c = ContrastMath.Blend(new Rgba(0, 0, 0, 0.5), new Rgba(255, 255, 255, 1));
        c.R.Should().BeInRange(127, 128);
        c.A.Should().Be(1);
    }

    [Fact]
    public void RegionLuma_WhiteAndBlackAndEmpty()
    {
        var white = Grid(1, 255);
        FrameGridStats.RegionLumaPercentiles(white, 0, 0, 0, 1, 1, 50)![0].Should().BeApproximately(1, 1e-6);
        FrameGridStats.RegionLumaPercentiles(Grid(1, 0), 0, 0, 0, 1, 1, 10, 90)!.Should().OnlyContain(v => v == 0);
        FrameGridStats.RegionLumaPercentiles(white, 0, 0.5, 0.5, 0, 0, 50).Should().BeNull();
        FrameGridStats.RegionLumaPercentiles(white, 5, 0, 0, 1, 1, 50).Should().BeNull();
        FrameGridStats.RegionLumaPercentiles(white, 0, -3, -3, 10, 10, 50).Should().NotBeNull();
    }

    [Fact]
    public void Frozen_RunInMiddle_Detected()
    {
        // 1 fps: frames 0..2 differ, 3..7 identical (run), then change.
        var g = Grid(1, 0, 50, 100, 150, 150, 150, 150, 150, 220, 10);
        var runs = FrozenFrameDetector.Find(g);
        runs.Should().ContainSingle();
        runs[0].StartSec.Should().Be(3);
        runs[0].Duration.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Frozen_ShortRun_Ignored() =>
        FrozenFrameDetector.Find(Grid(2, 0, 90, 90, 90, 200, 10)).Should().BeEmpty();

    [Fact]
    public void Frozen_TrailingHoldUpTo4s_Ignored() =>
        FrozenFrameDetector.Find(Grid(1, 0, 60, 120, 200, 200, 200, 200)).Should().BeEmpty();

    [Fact]
    public void Frozen_LongTrailingHold_Reported()
    {
        var g = Grid(1, 0, 60, 200, 200, 200, 200, 200, 200, 200);
        FrozenFrameDetector.Find(g).Should().ContainSingle();
    }

    [Fact]
    public void Frozen_DegenerateInputs_DoNotThrow()
    {
        FrozenFrameDetector.Find(Grid(1)).Should().BeEmpty();
        FrozenFrameDetector.Find(Grid(1, 5)).Should().BeEmpty();
        FrozenFrameDetector.Find(Grid(0, 1, 1, 1, 1)).Should().BeEmpty();
        FrozenFrameDetector.Find(Grid(double.NaN, 1, 1, 1, 1)).Should().BeEmpty();
        FrozenFrameDetector.Assess([], double.NaN).Should().BeEmpty();
    }

    [Fact]
    public void Frozen_AssessSeverity()
    {
        FrozenFrameDetector.Assess([new FrozenRun(1, 3.5)], 30)[0].Severity.Should().Be("medium");
        FrozenFrameDetector.Assess([new FrozenRun(0, 9)], 30)[0].Severity.Should().Be("medium");
        FrozenFrameDetector.Assess([new FrozenRun(0, 9)], 16)[0].Severity.Should().Be("high");
        FrozenFrameDetector.Assess([new FrozenRun(0, 1)], 16).Should().BeEmpty();
    }

    [Fact]
    public void Frozen_Deterministic()
    {
        var g = Grid(2, 0, 50, 50, 50, 50, 50, 50, 50, 9, 99);
        FrozenFrameDetector.Find(g).Should().Equal(FrozenFrameDetector.Find(g));
    }

    private static LoudnessFacts Facts(double? i = -16, double? tp = -3, double? lra = 6,
        (double, double)[]? sil = null, double dur = 30, bool track = true, bool norm = false) =>
        new(i, tp, lra, sil ?? [], dur, track, norm);

    [Fact]
    public void Loudness_OnTarget_NoFindings() =>
        LoudnessAssessor.Assess(Facts(), LoudnessTarget.Web).Should().BeEmpty();

    [Fact]
    public void Loudness_TooQuiet_MediumAndReadable()
    {
        var f = LoudnessAssessor.Assess(Facts(i: -23.1), LoudnessTarget.Web).Should().ContainSingle().Subject;
        f.Kind.Should().Be("loudness_off_target");
        f.Severity.Should().Be("medium");
        f.Detail.Should().Be("The sound is quieter than the usual online level (-23.1 LUFS, aim for -16).");
    }

    [Fact]
    public void Loudness_VeryOff_StillMedium() =>
        LoudnessAssessor.Assess(Facts(i: -40), LoudnessTarget.Web)[0].Severity.Should().Be("medium");

    [Fact]
    public void Loudness_Normalised_SkipsOffTargetAndLra() =>
        LoudnessAssessor.Assess(Facts(i: -30, lra: 20, norm: true), LoudnessTarget.Web).Should().BeEmpty();

    [Fact]
    public void Loudness_TruePeak_Rules()
    {
        LoudnessAssessor.Assess(Facts(tp: -1.4), LoudnessTarget.Web).Should().BeEmpty();
        LoudnessAssessor.Assess(Facts(tp: -1.2), LoudnessTarget.Web)[0].Kind.Should().Be("true_peak_high");
        var c = LoudnessAssessor.Assess(Facts(tp: 0.3), LoudnessTarget.Web)[0];
        c.Kind.Should().Be("clipping");
        c.Severity.Should().Be("high");
    }

    [Fact]
    public void Loudness_Lra_Low() =>
        LoudnessAssessor.Assess(Facts(lra: 16), LoudnessTarget.Web).Should().ContainSingle(x => x.Severity == "low" && x.Kind == "loudness_range_wide");

    [Fact]
    public void Loudness_Silence_Windows()
    {
        LoudnessAssessor.Assess(Facts(sil: [(5, 9)])).Should().ContainSingle(x => x.Kind == "long_silence" && x.Severity == "low");
        LoudnessAssessor.Assess(Facts(sil: [(5, 12)])).Should().ContainSingle(x => x.Severity == "medium");
        LoudnessAssessor.Assess(Facts(sil: [(5, 7)])).Should().BeEmpty();
        LoudnessAssessor.Assess(Facts(sil: [(0, 3)])).Should().BeEmpty();
        LoudnessAssessor.Assess(Facts(sil: [(27, 30)])).Should().BeEmpty();
    }

    [Fact]
    public void Loudness_NoSound()
    {
        var f = LoudnessAssessor.Assess(Facts(i: null, tp: null, lra: null, sil: [(0, 30)]));
        f.Should().ContainSingle(x => x.Kind == "no_sound" && x.Severity == "high");
        LoudnessAssessor.Assess(Facts(i: double.NegativeInfinity, sil: [(0, 10)])).Should().NotContain(x => x.Kind == "no_sound");
        LoudnessAssessor.Assess(Facts(i: null, sil: [(0, 30)], track: false)).Should().BeEmpty();
    }

    [Fact]
    public void Loudness_Deterministic()
    {
        var facts = Facts(i: -23.14, sil: [(5, 12)]);
        LoudnessAssessor.Assess(facts, LoudnessTarget.Web).Should().Equal(LoudnessAssessor.Assess(facts, LoudnessTarget.Web));
    }
}
