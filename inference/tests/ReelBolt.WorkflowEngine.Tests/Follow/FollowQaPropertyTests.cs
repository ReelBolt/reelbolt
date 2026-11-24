using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Follow;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Follow;

/// <summary>QA property tests for the follow core (containment, speed, caps, non-round ranges).</summary>
public class FollowQaPropertyTests
{
    private static List<SubjectSample> Wander(double from, double to, double step = 1.0 / 30)
    {
        var l = new List<SubjectSample>();
        for (int i = 0; from + i * step <= to + 1e-9; i++)
        {
            double t = from + i * step;
            double cx = 0.5 + 0.48 * Math.Sin(t * 0.7) * Math.Cos(t * 0.13);
            double cy = 0.5 + 0.45 * Math.Sin(t * 0.41 + 1);
            double s = 0.02 + 0.2 * (0.5 + 0.5 * Math.Sin(t * 0.29));
            bool present = (int)(t / 7) % 3 != 2;
            l.Add(new SubjectSample(t, cx - s / 2, cy - s / 2, s, s * 1.3, present));
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

    public static IEnumerable<object[]> Cases()
    {
        foreach (FollowSmoothness sm in Enum.GetValues<FollowSmoothness>())
        {
            foreach (FollowOnLost lost in Enum.GetValues<FollowOnLost>())
            {
                yield return [sm, lost, 1.0, 1.0, 64];
                yield return [sm, lost, 16.0 / 9, 9.0 / 16, 32];
                yield return [sm, lost, 9.0 / 16, 16.0 / 9, 2];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void InterpolatedPath_IsContainedAt100Hz_AndValidates(FollowSmoothness sm, FollowOnLost lost, double srcAspect, double outAspect, int maxKeys)
    {
        FollowFraming fr = srcAspect == outAspect ? FollowFraming.Full : FollowFraming.Cover(srcAspect, outAspect);
        var o = new FollowOptions(FollowTightness.Tight, sm, lost, true, fr, 4320, 1080, maxKeys);
        CameraPathResult r = SubjectCameraPath.Compute(Wander(0, 120), 0, 120, o);
        r.Keys.Count.Should().BeLessThanOrEqualTo(maxKeys);
        r.Keys.Should().OnlyContain(k => k.Zoom >= 1 && k.Zoom <= r.ZoomCap);
        for (int i = 0; i <= 12000; i++)
        {
            CameraKey k = At(r.Keys, i * 0.01);
            double hw = 0.5 * fr.VisibleWidth / k.Zoom, hh = 0.5 * fr.VisibleHeight / k.Zoom;
            (k.CenterX - hw).Should().BeGreaterThanOrEqualTo(-1e-12, $"x low at {i * 0.01}");
            (k.CenterX + hw).Should().BeLessThanOrEqualTo(1 + 1e-12, $"x high at {i * 0.01}");
            (k.CenterY - hh).Should().BeGreaterThanOrEqualTo(-1e-12, $"y low at {i * 0.01}");
            (k.CenterY + hh).Should().BeLessThanOrEqualTo(1 + 1e-12, $"y high at {i * 0.01}");
        }

        VideoReframeValidation v = VideoReframe.Validate(CameraPathReframes.ToReframes(r, 0, 0, 120));
        v.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(FollowSmoothness.Gentle)]
    [InlineData(FollowSmoothness.Normal)]
    [InlineData(FollowSmoothness.Snappy)]
    public void PanAndZoomSpeed_OnTheOutputGrid_StayWithinLimits(FollowSmoothness sm)
    {
        FollowFraming fr = FollowFraming.Cover(16.0 / 9, 9.0 / 16);
        var o = new FollowOptions(FollowTightness.Tight, sm, FollowOnLost.Widen, true, fr, 4320, 1080, 497);
        CameraPathResult r = SubjectCameraPath.Compute(Wander(0, 120), 0, 120, o);
        double pan = FollowTuning.MaxPanWindowsPerSec(sm), zr = FollowTuning.MaxZoomLnPerSec(sm);
        double worstPan = 0, worstZoom = 0;
        for (int i = 1; i <= 1200; i++)
        {
            CameraKey a = At(r.Keys, (i - 1) * 0.1), b = At(r.Keys, i * 0.1);
            double win = fr.VisibleWidth / Math.Max(a.Zoom, b.Zoom);
            worstPan = Math.Max(worstPan, Math.Abs(b.CenterX - a.CenterX) / 0.1 / win);
            worstZoom = Math.Max(worstZoom, Math.Abs(Math.Log(b.Zoom) - Math.Log(a.Zoom)) / 0.1);
        }

        worstPan.Should().BeLessThanOrEqualTo(pan * 1.10, "pan speed in windows/s");
        // The decimated output interpolates linearly in zoom while the limit is in ln(zoom); a 15 percent tolerance is documented.
        worstZoom.Should().BeLessThanOrEqualTo(zr * 1.15, "zoom speed in ln/s");
    }

    [Theory]
    [InlineData(0.123456, 10.123456)]
    [InlineData(1.0 / 3, 20.0 / 3)]
    [InlineData(2.000004, 9.999996)]
    public void NonRoundRange_ConvertsToValidReframes(double from, double to)
    {
        var s = Wander(0, 30).Select(x => x with { Present = true }).ToList();
        var o = new FollowOptions(FollowTightness.Medium, FollowSmoothness.Normal, FollowOnLost.Hold, true, FollowFraming.Full, 2160, 1080, 64);
        CameraPathResult r = SubjectCameraPath.Compute(s, from, to, o);
        IReadOnlyList<VideoReframe> spans = CameraPathReframes.ToReframes(r, 0, from, to);
        VideoReframeValidation v = VideoReframe.Validate(spans);
        v.IsValid.Should().BeTrue(v.Message);
    }

    [Fact]
    public void NonRoundRange_MultiSpan_ConvertsToValidReframes()
    {
        var s = Wander(0, 300).Select(x => x with { Present = true }).ToList();
        var o = new FollowOptions(FollowTightness.Tight, FollowSmoothness.Snappy, FollowOnLost.Hold, true, FollowFraming.Full, 4320, 1080, 200);
        double from = 0.123456, to = 299.876543;
        CameraPathResult r = SubjectCameraPath.Compute(s, from, to, o);
        r.Keys.Count.Should().BeGreaterThan(VideoReframe.MaxKeyframes);
        VideoReframeValidation v = VideoReframe.Validate(CameraPathReframes.ToReframes(r, 0, from, to));
        v.IsValid.Should().BeTrue(v.Message);
    }

    [Fact]
    public void ZoomCap_SoftNeverBelowSharp_AndLimitedByMaxZoom()
    {
        var s = new List<SubjectSample> { new(0, 0.49, 0.49, 0.01, 0.01, true), new(5, 0.49, 0.49, 0.01, 0.01, true) };
        FollowOptions o(bool soft, int src) => new(FollowTightness.Medium, FollowSmoothness.Normal, FollowOnLost.Hold, soft, FollowFraming.Full, src, 1080, 8);
        SubjectCameraPath.Compute(s, 0, 5, o(true, 6480)).ZoomCap.Should().Be(6.0);
        SubjectCameraPath.Compute(s, 0, 5, o(false, 6480)).ZoomCap.Should().Be(6.0);
        SubjectCameraPath.Compute(s, 0, 5, o(true, 20000)).ZoomCap.Should().Be(VideoReframe.MaxZoom);
        SubjectCameraPath.Compute(s, 0, 5, o(true, 1080) with { MaxZoomLimit = 0.5 }).ZoomCap.Should().Be(1.0);
        // Cover framing: sharp cap uses VisibleHeight (1 for 16:9 -> 9:16).
        FollowOptions c = o(false, 2160) with { Framing = FollowFraming.Cover(9.0 / 16, 16.0 / 9) };
        SubjectCameraPath.Compute(s, 0, 5, c).ZoomCap.Should().BeApproximately(Math.Max(1, 2 * c.Framing.VisibleHeight), 1e-9);
    }

    [Fact]
    public void Determinism_WithLostSpansAndCover()
    {
        FollowFraming fr = FollowFraming.Cover(16.0 / 9, 9.0 / 16);
        var o = new FollowOptions(FollowTightness.Loose, FollowSmoothness.Gentle, FollowOnLost.Widen, false, fr, 2160, 1920, 40);
        CameraPathResult a = SubjectCameraPath.Compute(Wander(0, 90), 0, 90, o);
        CameraPathResult b = SubjectCameraPath.Compute(Wander(0, 90).AsEnumerable().Reverse().ToList(), 0, 90, o);
        a.Keys.Should().Equal(b.Keys);
        a.LostSpans.Should().Equal(b.LostSpans);
        a.ClampCount.Should().Be(b.ClampCount);
        a.LostSpans.Should().NotBeEmpty();
    }
}
