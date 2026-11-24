using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The insert outlier pass (<see cref="ChromaQuadTracker.RejectImplausibleSamples"/>) judges a detection by the smaller of its
/// two-sided straight-line residual and its residual against the best single-kink continuous path through its neighbours
/// (initiative insert-tracking-smoothness, F2a). A whip's start and stop is one kink and must not be mistaken for an outlier;
/// a spike, or a short excursion that needs two kinks, still is. Pure analytic tracks, no ffmpeg.
/// </summary>
public class ChromaTrackRejectionTests
{
    // A wide grid so a 30 px/frame whip stays inside it; the plate is small so the 2 % diagonal floor is ~3 px.
    private const int GW = 2000, GH = 1000;
    private const double Fps = 24;

    private static ChromaQuadTracker.FrameQuad Quad(int frame, double xPx, double yPx = 300, double wPx = 100, double hPx = 100) =>
        new(frame,
            xPx / GW, yPx / GH, (xPx + wPx) / GW, yPx / GH,
            xPx / GW, (yPx + hPx) / GH, (xPx + wPx) / GW, (yPx + hPx) / GH,
            wPx * hPx / (GW * (double)GH), 1.0, 1.0);

    private static readonly ChromaQuadTracker.Options Opts = new();

    [Fact]
    public void Whip_start_and_stop_are_not_rejected()
    {
        // Still for 20 frames, 12 frames at 30 px a frame, still again: two velocity kinks and a noise-free path. A single
        // straight line through either side of a kink mispredicts its frame by the full step, so the shipped residual threw the
        // kink frames out and interpolated across the whip.
        var run = new List<ChromaQuadTracker.FrameQuad>();
        for (int f = 0; f < 60; f++)
            run.Add(Quad(f, 200 + 30 * Math.Clamp(f - 19, 0, 12)));

        (List<ChromaQuadTracker.FrameQuad> cleaned, _) = ChromaQuadTracker.RejectImplausibleSamples(run, Fps, GW, GH, Opts);

        cleaned.Count(q => q.Interpolated).Should().Be(0, "every sample lies on a continuous two-segment path through its neighbours");
        cleaned.Count.Should().Be(run.Count);
    }

    [Fact]
    public void Whip_with_sub_pixel_noise_keeps_the_kink_frames()
    {
        var rng = new Random(11);
        var run = new List<ChromaQuadTracker.FrameQuad>();
        for (int f = 0; f < 60; f++)
            run.Add(Quad(f, 200 + 30 * Math.Clamp(f - 19, 0, 12) + 0.3 * (rng.NextDouble() - 0.5)));

        (List<ChromaQuadTracker.FrameQuad> cleaned, _) = ChromaQuadTracker.RejectImplausibleSamples(run, Fps, GW, GH, Opts);

        cleaned.Count(q => q.Interpolated).Should().Be(0);
        cleaned.Select(q => q.FrameIndex).Should().Equal(run.Select(q => q.FrameIndex));
    }

    [Fact]
    public void Isolated_spike_is_still_rejected()
    {
        List<ChromaQuadTracker.FrameQuad> run = Enumerable.Range(0, 40).Select(f => Quad(f, 600)).ToList();
        run[17] = Quad(17, 600 + 60);

        (List<ChromaQuadTracker.FrameQuad> cleaned, _) = ChromaQuadTracker.RejectImplausibleSamples(run, Fps, GW, GH, Opts);

        ChromaQuadTracker.FrameQuad bridged = cleaned.Single(q => q.FrameIndex == 17);
        bridged.Interpolated.Should().BeTrue("a one-frame excursion needs two kinks to explain and is not a continuous path");
        bridged.X0.Should().BeApproximately(600.0 / GW, 1e-9);
        cleaned.Count(q => q.Interpolated).Should().Be(1);
    }

    [Fact]
    public void A_two_frame_occluder_excursion_is_still_rejected()
    {
        // The occlusion scene: something covers the plate for two frames and the fit follows it. Two consecutive displaced
        // samples are two kinks (away and back), which a single-kink path cannot absorb.
        List<ChromaQuadTracker.FrameQuad> run = Enumerable.Range(0, 40).Select(f => Quad(f, 600)).ToList();
        run[20] = Quad(20, 600 + 80);
        run[21] = Quad(21, 600 + 80);

        (List<ChromaQuadTracker.FrameQuad> cleaned, _) = ChromaQuadTracker.RejectImplausibleSamples(run, Fps, GW, GH, Opts);

        cleaned.Where(q => q.FrameIndex is 20 or 21).Should().OnlyContain(q => q.Interpolated);
        cleaned.Where(q => q.FrameIndex is 20 or 21).Should().OnlyContain(q => Math.Abs(q.X0 - 600.0 / GW) < 1e-9, "the excursion is replaced by the plate's own position, not by invented motion");
    }

    [Fact]
    public void Corrupted_end_frames_are_still_dropped_and_the_body_is_untouched()
    {
        // A plate leaving the frame: the last detection is cut by the border (its right edge pinned, a fraction of the plate), and
        // the first one bounded only half the plate. Neither has a neighbour on its outer side, so no hinge can explain them.
        List<ChromaQuadTracker.FrameQuad> run = Enumerable.Range(0, 40).Select(f => Quad(f, 400 + 10 * f)).ToList();
        run[0] = Quad(0, 400, wPx: 30);
        run[39] = Quad(39, 400 + 10 * 39, wPx: 30);

        (List<ChromaQuadTracker.FrameQuad> cleaned, _) = ChromaQuadTracker.RejectImplausibleSamples(run, Fps, GW, GH, Opts);

        cleaned[0].FrameIndex.Should().Be(1);
        cleaned[^1].FrameIndex.Should().Be(38);
        cleaned.Count.Should().Be(38);
        cleaned.Should().OnlyContain(q => !q.Interpolated, "the body of the run is kept verbatim; nothing is invented");
    }
}
