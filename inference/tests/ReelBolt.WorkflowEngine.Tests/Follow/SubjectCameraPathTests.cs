using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Follow;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Follow;

/// <summary>Golden and property tests of the pure follow-subject camera path core.</summary>
public class SubjectCameraPathTests
{
    private static FollowOptions Opts(
        FollowSmoothness smooth = FollowSmoothness.Normal,
        FollowOnLost onLost = FollowOnLost.Hold,
        bool soft = true,
        FollowFraming? framing = null,
        int srcH = 1080,
        int outH = 1080,
        int maxKeys = 64,
        FollowTightness tight = FollowTightness.Medium) =>
        new(tight, smooth, onLost, soft, framing ?? FollowFraming.Full, srcH, outH, maxKeys);

    private static List<SubjectSample> Track(double from, double to, Func<double, (double cx, double cy, double w, double h)> f, double step = 0.1)
    {
        var l = new List<SubjectSample>();
        for (int i = 0; from + i * step <= to + 1e-9; i++)
        {
            double t = from + i * step;
            var (cx, cy, w, h) = f(t);
            l.Add(new SubjectSample(t, cx - w / 2, cy - h / 2, w, h, true));
        }

        return l;
    }

    private static CameraKey At(IReadOnlyList<CameraKey> keys, double t)
    {
        if (t <= keys[0].TimeSec)
            return keys[0];
        for (int i = 0; i + 1 < keys.Count; i++)
        {
            if (t <= keys[i + 1].TimeSec)
            {
                double u = (t - keys[i].TimeSec) / (keys[i + 1].TimeSec - keys[i].TimeSec);
                return new CameraKey(t,
                    keys[i].CenterX + (keys[i + 1].CenterX - keys[i].CenterX) * u,
                    keys[i].CenterY + (keys[i + 1].CenterY - keys[i].CenterY) * u,
                    keys[i].Zoom + (keys[i + 1].Zoom - keys[i].Zoom) * u);
            }
        }

        return keys[^1];
    }

    [Theory]
    [InlineData(FollowTightness.Tight, FollowSmoothness.Snappy, 0.18)]
    [InlineData(FollowTightness.Medium, FollowSmoothness.Normal, 0.12)]
    [InlineData(FollowTightness.Loose, FollowSmoothness.Gentle, 0.08)]
    public void A_fast_walker_stays_inside_the_visible_window_when_it_fits(FollowTightness tight, FollowSmoothness smooth, double boxWidth)
    {
        // Production run 3bc05f61: a 1920x1080 walker followed into a 1080x1920 canvas (zoom cap 1) reached
        // the right edge of the picture and was half cut off. A subject that physically fits the visible
        // window must never leave it, whatever the smoothing.
        FollowFraming cover = FollowFraming.Cover(16.0 / 9.0, 9.0 / 16.0);
        // A constant-speed walk at 75% of this smoothness' pan cap (Snappy: about 0.24 of the frame per second).
        double speed = 0.75 * FollowTuning.MaxPanWindowsPerSec(smooth) * cover.VisibleWidth;
        List<SubjectSample> walk = Track(0.9, 3.6, t => (0.12 + (t - 0.9) * speed, 0.55, boxWidth, 0.6));

        CameraPathResult r = SubjectCameraPath.Compute(walk, 0, 4.32,
            Opts(smooth, framing: cover, soft: false, srcH: 1080, outH: 1920, maxKeys: 497, tight: tight));

        r.ZoomCap.Should().Be(1);
        foreach (SubjectSample s in walk)
        {
            CameraKey k = At(r.Keys, s.TimeSec);
            double halfW = 0.5 * cover.VisibleWidth / k.Zoom;
            (s.X).Should().BeGreaterThanOrEqualTo(k.CenterX - halfW - 0.002, $"at {s.TimeSec:0.0}s the subject's left edge is in the window");
            (s.X + s.Width).Should().BeLessThanOrEqualTo(k.CenterX + halfW + 0.002, $"at {s.TimeSec:0.0}s the subject's right edge is in the window");
        }
    }

    private static void AssertContained(CameraPathResult r, FollowFraming fr, double from, double to)
    {
        for (double t = from; t <= to + 1e-9; t += 0.1)
        {
            CameraKey k = At(r.Keys, Math.Min(t, to));
            double hw = 0.5 * fr.VisibleWidth / k.Zoom, hh = 0.5 * fr.VisibleHeight / k.Zoom;
            (k.CenterX - hw).Should().BeGreaterThanOrEqualTo(-1e-9, $"at {t}");
            (k.CenterX + hw).Should().BeLessThanOrEqualTo(1 + 1e-9, $"at {t}");
            (k.CenterY - hh).Should().BeGreaterThanOrEqualTo(-1e-9, $"at {t}");
            (k.CenterY + hh).Should().BeLessThanOrEqualTo(1 + 1e-9, $"at {t}");
        }
    }

    private static void AssertSane(CameraPathResult r, double from, double to)
    {
        r.Keys[0].TimeSec.Should().Be(from);
        r.Keys[^1].TimeSec.Should().Be(to);
        for (int i = 1; i < r.Keys.Count; i++)
            r.Keys[i].TimeSec.Should().BeGreaterThan(r.Keys[i - 1].TimeSec);
        r.Keys.Should().OnlyContain(k => k.Zoom >= 1.0 && k.Zoom <= VideoReframe.MaxZoom);
    }

    [Fact]
    public void StaticCenteredSubject_GivesConstantPathWithTwoKeys()
    {
        var s = Track(0, 10, _ => (0.5, 0.5, 0.2, 0.3));
        CameraPathResult r = SubjectCameraPath.Compute(s, 0, 10, Opts());
        r.Keys.Should().HaveCount(2);
        r.Keys[0].CenterX.Should().Be(0.5);
        r.Keys[0].CenterY.Should().Be(0.5);
        r.Keys[0].Zoom.Should().BeApproximately(0.5 / 0.3, 1e-4); // min(0.5/0.3, 0.5/0.2)
        r.Keys[1].Zoom.Should().Be(r.Keys[0].Zoom);
        r.LostSpans.Should().BeEmpty();
        AssertSane(r, 0, 10);
    }

    [Fact]
    public void TightnessChangesFill()
    {
        var s = Track(0, 5, _ => (0.5, 0.5, 0.1, 0.1));
        double loose = SubjectCameraPath.Compute(s, 0, 5, Opts(tight: FollowTightness.Loose)).Keys[0].Zoom;
        double tight = SubjectCameraPath.Compute(s, 0, 5, Opts(tight: FollowTightness.Tight)).Keys[0].Zoom;
        loose.Should().BeApproximately(3.5, 1e-4);
        tight.Should().BeApproximately(4.0, 1e-4); // 6.5 capped by the soft cap
    }

    [Fact]
    public void LinearMotion_FollowsWithinDeadZoneAndStaysInFrame()
    {
        var s = Track(0, 30, t => (0.2 + 0.6 * t / 30, 0.5, 0.1, 0.15));
        CameraPathResult r = SubjectCameraPath.Compute(s, 0, 30, Opts());
        AssertSane(r, 0, 30);
        AssertContained(r, FollowFraming.Full, 0, 30);
        double z = 0.5 / 0.15;
        r.Keys.Should().OnlyContain(k => Math.Abs(k.Zoom - z) < 1e-3);
        for (double t = 0; t <= 30; t += 0.5)
        {
            double subject = 0.2 + 0.6 * t / 30;
            Math.Abs(At(r.Keys, t).CenterX - subject).Should().BeLessThan(0.125 * 0.25 * 2 + 0.03, $"at {t}");
        }

        At(r.Keys, 29).CenterX.Should().BeGreaterThan(0.6);
    }

    [Fact]
    public void JitterBelowDeadZone_GivesConstantCentre()
    {
        var s = Track(0, 10, t => (0.5 + 0.01 * Noise(t), 0.5 + 0.01 * Noise(t + 100), 0.2, 0.3));
        CameraPathResult r = SubjectCameraPath.Compute(s, 0, 10, Opts());
        r.Keys.Select(k => k.CenterX).Distinct().Should().HaveCount(1);
        r.Keys.Select(k => k.CenterY).Distinct().Should().HaveCount(1);
    }

    [Theory]
    [InlineData(FollowSmoothness.Gentle)]
    [InlineData(FollowSmoothness.Normal)]
    [InlineData(FollowSmoothness.Snappy)]
    public void WildJitter_NeverExceedsPanSpeedLimit(FollowSmoothness smooth)
    {
        var s = Track(0, 20, t => (0.5 + 0.2 * Noise(t), 0.5, 0.1, 0.1));
        CameraPathResult r = SubjectCameraPath.Compute(s, 0, 20, Opts(smooth, maxKeys: 256));
        double window = 1.0 / r.Keys[0].Zoom;
        double limit = FollowTuning.MaxPanWindowsPerSec(smooth);
        for (int i = 1; i < r.Keys.Count; i++)
        {
            double speed = Math.Abs(r.Keys[i].CenterX - r.Keys[i - 1].CenterX) / (r.Keys[i].TimeSec - r.Keys[i - 1].TimeSec) / window;
            speed.Should().BeLessThanOrEqualTo(limit * 1.001 + 1e-3);
        }
    }

    [Fact]
    public void SubjectAtFrameEdge_ClampsCentreAndStaysContained()
    {
        var s = Track(0, 10, _ => (0.96, 0.5, 0.08, 0.08));
        CameraPathResult r = SubjectCameraPath.Compute(s, 0, 10, Opts(srcH: 8640));
        r.ClampCount.Should().BeGreaterThan(0);
        r.Keys[0].Zoom.Should().BeApproximately(6.25, 1e-3);
        r.Keys[0].CenterX.Should().BeLessThanOrEqualTo(1 - 0.5 / r.Keys[0].Zoom + 1e-9);
        AssertContained(r, FollowFraming.Full, 0, 10);
    }

    [Fact]
    public void TinySubject_SharpCapIsOneAndSoftCapIsFour()
    {
        var s = Track(0, 5, _ => (0.5, 0.5, 0.01, 0.01));
        CameraPathResult sharp = SubjectCameraPath.Compute(s, 0, 5, Opts(soft: false));
        sharp.ZoomCap.Should().Be(1.0);
        sharp.ZoomCapLimited.Should().BeTrue();
        sharp.Keys.Should().OnlyContain(k => k.Zoom == 1.0);
        CameraPathResult soft = SubjectCameraPath.Compute(s, 0, 5, Opts(soft: true));
        soft.ZoomCap.Should().Be(4.0);
        soft.ZoomCapLimited.Should().BeTrue();
    }

    [Fact]
    public void FourKSourceTo1080_SharpCapIsTwo()
    {
        var s = Track(0, 5, _ => (0.5, 0.5, 0.5, 0.5));
        SubjectCameraPath.Compute(s, 0, 5, Opts(soft: false, srcH: 2160, outH: 1080)).ZoomCap.Should().Be(2.0);
    }

    [Fact]
    public void MaxZoomLimitLowersTheCap()
    {
        var s = Track(0, 5, _ => (0.5, 0.5, 0.05, 0.05));
        FollowOptions o = Opts(srcH: 8640) with { MaxZoomLimit = 3.0 };
        SubjectCameraPath.Compute(s, 0, 5, o).ZoomCap.Should().Be(3.0);
    }

    [Fact]
    public void ShortGapIsBridged_LongGapIsReported()
    {
        var bridged = Track(0, 10, _ => (0.5, 0.5, 0.2, 0.2)).Where(x => x.TimeSec < 4.05 || x.TimeSec > 4.25).ToList();
        SubjectCameraPath.Compute(bridged, 0, 10, Opts()).LostSpans.Should().BeEmpty();

        var gap = Track(0, 10, _ => (0.5, 0.5, 0.2, 0.2)).Where(x => x.TimeSec <= 4.0 + 1e-9 || x.TimeSec >= 6.0 - 1e-9).ToList();
        CameraPathResult r = SubjectCameraPath.Compute(gap, 0, 10, Opts());
        r.LostSpans.Should().ContainSingle();
        r.LostSpans[0].FromSec.Should().BeApproximately(4.0, 1e-6);
        r.LostSpans[0].ToSec.Should().BeApproximately(6.0, 1e-6);
    }

    [Fact]
    public void NoPresentSample_GivesWholeRangeLostAtZoomOne()
    {
        CameraPathResult r = SubjectCameraPath.Compute([new SubjectSample(1, 0.4, 0.4, 0.1, 0.1, false)], 0, 5, Opts());
        r.Keys.Should().HaveCount(2);
        r.Keys.Should().OnlyContain(k => k.Zoom == 1.0 && k.CenterX == 0.5 && k.CenterY == 0.5);
        r.LostSpans.Should().ContainSingle().Which.Should().Be(new FollowLostSpan(0, 5));
    }

    [Fact]
    public void Hold_KeepsLastFraming_AfterTheSubjectIsLost()
    {
        var s = Track(0, 4, _ => (0.3, 0.4, 0.2, 0.2));
        CameraPathResult r = SubjectCameraPath.Compute(s, 0, 10, Opts(onLost: FollowOnLost.Hold));
        r.LostSpans.Should().ContainSingle();
        CameraKey end = At(r.Keys, 10);
        end.Zoom.Should().BeApproximately(2.5, 1e-3);
        end.CenterX.Should().BeApproximately(0.3, 1e-3);
        end.CenterY.Should().BeApproximately(0.4, 1e-3);
    }

    [Fact]
    public void Widen_ReachesFullFrameWithinThreeSecondsOfTheLoss()
    {
        var s = Track(0, 4, _ => (0.3, 0.4, 0.2, 0.2));
        CameraPathResult r = SubjectCameraPath.Compute(s, 0, 10, Opts(FollowSmoothness.Snappy, FollowOnLost.Widen));
        At(r.Keys, 4.0).Zoom.Should().BeGreaterThan(2.0);
        CameraKey k = At(r.Keys, 7.0);
        k.Zoom.Should().BeLessThan(1.15); // the smoothing corner at the end of the 2 s ramp is within 15 percent of full frame
        CameraKey settled = At(r.Keys, 8.0);
        settled.Zoom.Should().BeLessThan(1.02);
        settled.CenterX.Should().BeApproximately(0.5, 0.02);
        settled.CenterY.Should().BeApproximately(0.5, 0.02);
        At(r.Keys, 10).Zoom.Should().BeApproximately(1.0, 1e-3);
    }

    [Theory]
    [InlineData(256)]
    [InlineData(32)]
    public void TenMinuteWanderingPath_RespectsMaxKeys(int maxKeys)
    {
        var s = Track(0, 600, t => (0.5 + 0.3 * Math.Sin(t * 0.37) + 0.1 * Math.Sin(t * 1.3), 0.5 + 0.2 * Math.Sin(t * 0.21), 0.1, 0.15));
        CameraPathResult r = SubjectCameraPath.Compute(s, 0, 600, Opts(maxKeys: maxKeys));
        r.Keys.Count.Should().BeLessThanOrEqualTo(maxKeys);
        AssertSane(r, 0, 600);
        AssertContained(r, FollowFraming.Full, 0, 600);
    }

    [Fact]
    public void CoverFraming_KeepsSubjectInsideTheVisibleWindow()
    {
        FollowFraming fr = FollowFraming.Cover(16.0 / 9, 9.0 / 16);
        fr.VisibleWidth.Should().BeApproximately(0.31640625, 1e-6);
        fr.VisibleHeight.Should().Be(1.0);
        var s = Track(0, 20, t => (0.4 + 0.2 * t / 20, 0.5, 0.05, 0.2));
        CameraPathResult r = SubjectCameraPath.Compute(s, 0, 20, Opts(framing: fr));
        AssertSane(r, 0, 20);
        AssertContained(r, fr, 0, 20);
        for (double t = 0; t <= 20; t += 0.1)
        {
            CameraKey k = At(r.Keys, t);
            double subject = 0.4 + 0.2 * t / 20;
            Math.Abs(k.CenterX - subject).Should().BeLessThan(0.5 * fr.VisibleWidth / k.Zoom, $"at {t}");
        }
    }

    [Fact]
    public void UnsortedDuplicatedSamples_AreAccepted()
    {
        var s = Track(0, 5, _ => (0.5, 0.5, 0.2, 0.2));
        var shuffled = s.AsEnumerable().Reverse().Concat(s.Take(10)).ToList();
        SubjectCameraPath.Compute(shuffled, 0, 5, Opts()).Keys.Should().Equal(SubjectCameraPath.Compute(s, 0, 5, Opts()).Keys);
    }

    [Fact]
    public void SameInputTwice_GivesIdenticalOutput()
    {
        var s = Track(0, 60, t => (0.5 + 0.3 * Math.Sin(t * 0.5), 0.5 + 0.1 * Noise(t), 0.1 + 0.03 * Math.Sin(t), 0.15));
        CameraPathResult a = SubjectCameraPath.Compute(s, 0, 60, Opts());
        CameraPathResult b = SubjectCameraPath.Compute(s, 0, 60, Opts());
        a.Keys.Should().Equal(b.Keys);
        a.ClampCount.Should().Be(b.ClampCount);
        a.MaxErrorAfterDecimation.Should().Be(b.MaxErrorAfterDecimation);
    }

    [Fact]
    public void InvalidInput_IsRefused()
    {
        var ok = Track(0, 5, _ => (0.5, 0.5, 0.2, 0.2));
        FollowOptions o = Opts();
        Action nan = () => SubjectCameraPath.Compute([new SubjectSample(double.NaN, 0, 0, 0.1, 0.1, true)], 0, 5, o);
        nan.Should().Throw<ArgumentException>();
        Action nanTo = () => SubjectCameraPath.Compute(ok, 0, double.NaN, o);
        nanTo.Should().Throw<ArgumentException>();
        Action order = () => SubjectCameraPath.Compute(ok, 5, 5, o);
        order.Should().Throw<ArgumentException>().WithMessage("*toSec*");
        Action keys = () => SubjectCameraPath.Compute(ok, 0, 5, o with { MaxKeys = 1 });
        keys.Should().Throw<ArgumentException>().WithMessage("*MaxKeys*");
        Action frame = () => SubjectCameraPath.Compute(ok, 0, 5, o with { Framing = new FollowFraming(0, 1) });
        frame.Should().Throw<ArgumentException>();
        Action height = () => SubjectCameraPath.Compute(ok, 0, 5, o with { OutputHeightPx = 0 });
        height.Should().Throw<ArgumentException>();
    }

    private static List<SubjectSample> ZigZag(double seconds) =>
        Track(0, seconds, t => (0.5 + 0.35 * (((int)(t / 3)) % 2 == 0 ? 1 : -1) * (((t % 3) / 1.5) - 1), 0.5, 0.1, 0.1), 0.5);

    [Fact]
    public void FourHourZigZag_FillsTheKeyBudgetAndCompletes()
    {
        var s = ZigZag(4 * 3600);
        CameraPathResult r = SubjectCameraPath.Compute(s, 0, 4 * 3600, Opts(FollowSmoothness.Snappy, maxKeys: 497));
        r.Keys.Count.Should().BeInRange(400, 497);
        double.IsFinite(r.MaxErrorAfterDecimation).Should().BeTrue();
    }

    [Fact]
    public void MaxKeysFromTwoTo497_AreHonoured()
    {
        var s = ZigZag(1800);
        foreach (int m in new[] { 2, 3, 5, 31, 32, 100, 497 })
        {
            CameraPathResult r = SubjectCameraPath.Compute(s, 0, 1800, Opts(FollowSmoothness.Snappy, maxKeys: m));
            r.Keys.Count.Should().BeLessThanOrEqualTo(m);
            r.Keys.Count.Should().BeGreaterThanOrEqualTo(2);
        }
    }

    [Fact]
    public void NegativeFrom_AndTinyFraming_AreRefused()
    {
        var ok = Track(0, 5, _ => (0.5, 0.5, 0.2, 0.2));
        Action neg = () => SubjectCameraPath.Compute(ok, -1, 5, Opts());
        neg.Should().Throw<ArgumentException>();
        Action tiny = () => SubjectCameraPath.Compute(ok, 0, 5, Opts(framing: new FollowFraming(double.Epsilon, 1)));
        tiny.Should().Throw<ArgumentException>();
    }

    private static double Noise(double t)
    {
        double v = Math.Sin(Math.Round(t * 10) * 12.9898) * 43758.5453;
        return (v - Math.Floor(v)) * 2 - 1;
    }

    [Fact]
    public void Refuses_a_range_longer_than_the_bound()
    {
        var samples = new List<SubjectSample> { new(0, 0.4, 0.4, 0.2, 0.2, true) };
        var options = new FollowOptions(FollowTightness.Medium, FollowSmoothness.Normal, FollowOnLost.Hold, false,
            FollowFraming.Full, 2160, 1080, 32);
        Action act = () => SubjectCameraPath.Compute(samples, 0, SubjectCameraPath.MaxRangeSeconds + 1, options);
        act.Should().Throw<ArgumentException>();
        Action huge = () => SubjectCameraPath.Compute(samples, 0, 1e12, options);
        huge.Should().Throw<ArgumentException>();
    }
}
