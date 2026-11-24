using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using static ReelBolt.WorkflowEngine.Services.Video.PlatePoseMath;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The rigid-pose repair of tracked insert plates (docs/video-editing.md "Occluded corners and
/// edges"), end to end through <see cref="ChromaQuadTracker.Track"/> on rendered scenes whose true
/// corners are known: a plate (flat, or a cylinder section) in front of a pinhole camera, with
/// something drawn over one of its corners or edges for the middle of the clip.
/// </summary>
public class ChromaPlatePoseTrackerTests
{
    private const int W = 320, H = 180, Frames = 36;
    private const double Fps = 12, Focal = 280;

    private static ChromaQuadTracker.Options Options(bool rigid) => new(
        ColorName: "green", MinAreaRatio: 0.01, MinTrackSeconds: 0.5, MaxRegions: 2, MeasureCurvature: true, SolveChromaKey: false,
        SolveRigidPose: rigid);

    /// <summary>Worst distance, over every frame the track covers, between an emitted corner and the true one (grid pixels), and the frames covered.</summary>
    private static (double Worst, int Covered) Score(SyntheticPlate.Scene scene, IReadOnlyList<VideoInsertRegionTrack> tracks)
    {
        tracks.Should().NotBeEmpty();
        VideoInsertRegionTrack t = tracks.OrderByDescending(x => x.EndSec - x.StartSec).First();
        double worst = 0;
        int covered = 0;
        foreach (VideoInsertQuadKeyframe k in t.Keyframes)
        {
            int frame = (int)Math.Round(k.TimeSec * Fps);
            if (frame < 0 || frame >= scene.Frames)
                continue;
            covered++;
            (double X, double Y)[] truth = scene.Corners(frame);
            VideoInsertSurfaceQuad q = k.SurfaceCorners ?? new VideoInsertSurfaceQuad(k.X0, k.Y0, k.X1, k.Y1, k.X2, k.Y2, k.X3, k.Y3);
            (double X, double Y)[] got =
                [(q.X0 * W - 0.5, q.Y0 * H - 0.5), (q.X1 * W - 0.5, q.Y1 * H - 0.5), (q.X2 * W - 0.5, q.Y2 * H - 0.5), (q.X3 * W - 0.5, q.Y3 * H - 0.5)];
            for (int c = 0; c < 4; c++)
                worst = Math.Max(worst, Math.Sqrt(Math.Pow(got[c].X - truth[c].X, 2) + Math.Pow(got[c].Y - truth[c].Y, 2)));
        }

        return (worst, covered);
    }

    private static IReadOnlyList<VideoInsertRegionTrack> Track(SyntheticPlate.Scene scene, bool rigid) =>
        ChromaQuadTracker.Track(scene.Render(), scene.Frames, W, H, Fps, Options(rigid), W, H);

    /// <summary>
    /// How far in the occluder is: 0 until frame 6, sliding in to 1 by frame 12, in place until 24,
    /// out again by 30. Sliding matters: something that appears all at once trips the assembly's
    /// area test and the whole quad is held, which hides the very defect these tests are about. A
    /// hand or a cat comes in gradually, and takes the corner with it.
    /// </summary>
    private static double Amount(int frame) => Math.Clamp(Math.Min((frame - 6) / 6.0, (30 - frame) / 6.0), 0, 1);

    private static double[] StillPose(double yawDegrees = 8) => SyntheticPlate.Pose(yawDegrees, -5, 0, 0, 0, 2.6);

    [Fact]
    public void A_clean_track_is_left_exactly_as_the_detector_made_it()
    {
        var scene = new SyntheticPlate.Scene(W, H, Frames, Focal, 16 / 9.0, 0, _ => StillPose(), null);

        IReadOnlyList<VideoInsertRegionTrack> with = Track(scene, rigid: true), without = Track(scene, rigid: false);

        with.Should().HaveCount(1);
        with[0].Keyframes.Should().BeEquivalentTo(without[0].Keyframes, o => o.WithStrictOrdering());
        with[0].Confidence.Should().Be(without[0].Confidence);
    }

    [Fact]
    public void A_hand_over_a_corner_does_not_move_the_corner()
    {
        var scene = new SyntheticPlate.Scene(W, H, Frames, Focal, 16 / 9.0, 0, _ => StillPose(), (frame, c, x, y) =>
            Math.Sqrt(Math.Pow(x - c[3].X, 2) + Math.Pow(y - c[3].Y, 2)) < Amount(frame) * 0.3 * (c[2].Y - c[0].Y));

        (double worst, int covered) = Score(scene, Track(scene, rigid: true));

        covered.Should().Be(Frames);
        worst.Should().BeLessThan(1.0, "the covered corner is where the two edges next to it meet, and both are still partly in view");
    }

    [Fact]
    public void A_hand_over_two_corners_of_a_moving_plate_does_not_move_them()
    {
        // The plate slides and comes closer while a band covers its right edge and both right
        // corners. Holding or interpolating the last good quad is wrong here (the plate moved); the
        // hidden edge has to follow the three that are in view.
        var scene = new SyntheticPlate.Scene(W, H, Frames, Focal, 16 / 9.0, 0,
            frame => SyntheticPlate.Pose(0, 0, 3.0 * Math.Sin(frame * 0.2), 0.35 * Math.Sin(frame * 0.17), 0.05, 3.0 - 0.6 * frame / (Frames - 1)),
            (frame, c, x, _) => x > Math.Min(c[1].X, c[3].X) + (0.06 - 0.2 * Amount(frame)) * (c[1].X - c[0].X));

        (double worst, int covered) = Score(scene, Track(scene, rigid: true));
        (double before, _) = Score(scene, Track(scene, rigid: false));

        covered.Should().Be(Frames);
        worst.Should().BeLessThan(2.5);
        before.Should().BeGreaterThan(5.0, "without the rigid model the hidden corners follow the occluder or stay behind (this is what the test guards)");
    }

    [Fact]
    public void Something_lying_along_a_whole_edge_does_not_move_the_edge()
    {
        // A straight band over the bottom edge and both bottom corners: every probe on that edge
        // finds the band's upper boundary, a perfectly good straight line, a fifth of the way up.
        var scene = new SyntheticPlate.Scene(W, H, Frames, Focal, 16 / 9.0, 0, _ => StillPose(), (frame, c, _, y) =>
            y > c[2].Y + (0.1 - 0.3 * Amount(frame)) * (c[2].Y - c[0].Y));

        (double worst, int covered) = Score(scene, Track(scene, rigid: true));
        (double before, _) = Score(scene, Track(scene, rigid: false));

        covered.Should().Be(Frames);
        worst.Should().BeLessThan(1.0, "three edges fix a rigid plate's pose, so the fourth is known without being seen");
        before.Should().BeGreaterThan(2.5, "without the rigid model the bottom corners ride up with the band");
    }

    [Fact]
    public void A_hidden_edge_is_carried_through_a_turn_in_depth()
    {
        // The plate turns 40 degrees about its vertical axis while its right edge is hidden: the
        // hidden edge has to come from the pose, since no earlier frame saw it where it now is.
        var scene = new SyntheticPlate.Scene(W, H, Frames, Focal, 16 / 9.0, 0,
            frame => SyntheticPlate.Pose(-20 + 40.0 * frame / (Frames - 1), -4, 0, 0, 0, 2.4),
            (frame, c, x, _) => x > Math.Min(c[1].X, c[3].X) + (0.06 - 0.18 * Amount(frame)) * (c[1].X - c[0].X));

        (double worst, int covered) = Score(scene, Track(scene, rigid: true));

        covered.Should().Be(Frames);
        worst.Should().BeLessThan(2.5);
    }

    [Fact]
    public void A_bar_across_the_plate_does_not_cut_the_plate_in_two()
    {
        // A tail-like bar from above the plate to below it: the plate's green is two components for
        // the middle of the clip and the detector keeps only the larger.
        var scene = new SyntheticPlate.Scene(W, H, Frames, Focal, 16 / 9.0, 0, _ => StillPose(), (frame, c, x, _) =>
        {
            double at = c[0].X + (1.15 - 0.43 * Amount(frame)) * (c[1].X - c[0].X);
            return Math.Abs(x - at) < 0.035 * (c[1].X - c[0].X);
        });

        (double worst, int covered) = Score(scene, Track(scene, rigid: true));

        covered.Should().Be(Frames);
        worst.Should().BeLessThan(1.0);
    }

    [Fact]
    public void A_bent_plate_keeps_its_corners_when_one_is_covered()
    {
        var scene = new SyntheticPlate.Scene(W, H, Frames, Focal, 16 / 9.0, 0.9, _ => SyntheticPlate.Pose(6, -14, 0, 0, 0, 2.6), (frame, c, x, y) =>
            Math.Sqrt(Math.Pow(x - c[3].X, 2) + Math.Pow(y - c[3].Y, 2)) < Amount(frame) * 0.3 * (c[2].Y - c[0].Y));

        IReadOnlyList<VideoInsertRegionTrack> tracks = Track(scene, rigid: true);
        (double worst, int covered) = Score(scene, tracks);
        (double before, _) = Score(scene, Track(scene, rigid: false));

        covered.Should().Be(Frames);
        worst.Should().BeLessThan(1.5);
        before.Should().BeGreaterThan(6.0);
        tracks[0].MeanCurvature.Should().BeGreaterThan(0.02, "the plate is still reported as bent, so the compile step meshes it");
    }

    private const double BarelyBent = 0.13;

    [Fact]
    public void A_plate_that_is_barely_bent_is_tracked_as_a_plane()
    {
        // A sag of about 1.6% of the width, under what the compile step would mesh. The track must
        // say "plane" (the compile step then pins four corners) and still hold the covered corner.
        // This pins the outcome only: the rules that keep a weak or ill-fitting cylinder from being
        // chosen (MinBend, MaxBentCost) were needed on real clips (rounded screen corners, a head
        // along a television's edge) that a clean rendered cylinder does not reproduce.
        var scene = new SyntheticPlate.Scene(W, H, Frames, Focal, 16 / 9.0, BarelyBent, _ => SyntheticPlate.Pose(6, -14, 0, 0, 0, 2.6), (frame, c, x, y) =>
            Math.Sqrt(Math.Pow(x - c[3].X, 2) + Math.Pow(y - c[3].Y, 2)) < Amount(frame) * 0.3 * (c[2].Y - c[0].Y));

        IReadOnlyList<VideoInsertRegionTrack> tracks = Track(scene, rigid: true);
        (double worst, int covered) = Score(scene, tracks);

        covered.Should().Be(Frames);
        worst.Should().BeLessThan(3.0);
        tracks[0].MeanCurvature.Should().BeLessThan(0.01, "under MinInsertCurvature the compile step pins four corners");
    }

    [Fact]
    public void The_model_is_the_largest_plate_ever_seen_not_the_usual_one()
    {
        // The bottom fifth is covered for all but the last six frames: the usual shape is the
        // visible part, and only the end of the clip shows the plate's true height.
        var scene = new SyntheticPlate.Scene(W, H, Frames, Focal, 16 / 9.0, 0, _ => StillPose(), (frame, c, _, y) =>
            frame < Frames - 6 && y > c[2].Y - 0.2 * (c[2].Y - c[0].Y));

        (double worst, int covered) = Score(scene, Track(scene, rigid: true));

        covered.Should().Be(Frames);
        worst.Should().BeLessThan(1.5);
    }

    [Fact]
    public void Decompose_recovers_the_pose_and_aspect_of_a_projected_rectangle()
    {
        Camera cam = Camera.ForGrid(W, H, Focal);
        var plate = new Plate(1.6);
        double[] pose = SyntheticPlate.Pose(17, -9, 4, 0.2, -0.1, 2.8);
        (double X, double Y)[] quad = ProjectCorners(cam, pose, plate);

        var recovered = new double[6];
        Decompose(cam, quad, out double aspect, out double cosine, recovered).Should().BeTrue();

        aspect.Should().BeApproximately(1.6, 1e-6);
        cosine.Should().BeApproximately(0, 1e-6);
        (double X, double Y)[] again = ProjectCorners(cam, recovered, plate);
        for (int i = 0; i < 4; i++)
        {
            again[i].X.Should().BeApproximately(quad[i].X, 1e-6);
            again[i].Y.Should().BeApproximately(quad[i].Y, 1e-6);
        }
    }

    [Fact]
    public void A_restricted_move_keeps_the_plates_shape_on_screen()
    {
        Camera cam = Camera.ForGrid(W, H, Focal);
        var plate = new Plate(1.6);
        double[] pose = SyntheticPlate.Pose(12, -6, 0, 0, 0, 2.8);
        var moved = new double[6];
        Restricted(pose, [0, 0, 0.3], moved);

        (double X, double Y)[] a = ProjectCorners(cam, pose, plate), b = ProjectCorners(cam, moved, plate);
        double Side((double X, double Y)[] q, int i, int j) => Math.Sqrt(Math.Pow(q[i].X - q[j].X, 2) + Math.Pow(q[i].Y - q[j].Y, 2));

        // A turn about the optical axis through the principal point is a rotation of the image.
        Side(b, 0, 1).Should().BeApproximately(Side(a, 0, 1), 1e-6);
        Side(b, 0, 2).Should().BeApproximately(Side(a, 0, 2), 1e-6);
        Side(b, 1, 2).Should().BeApproximately(Side(a, 1, 2), 1e-6);
    }

    [Fact]
    public void The_loss_charges_an_outlier_a_constant_and_inward_sooner_than_outward()
    {
        Rho(-10, 1.25, 3).Should().Be(Rho(-100, 1.25, 3), "walking further from evidence must not pay");
        Rho(-2, 1.25, 3).Should().BeApproximately(1.25 * 1.25 / 6, 1e-12, "an inward residual saturates at the inward scale");
        Rho(2, 1.25, 3).Should().BeLessThan(3.0 * 3.0 / 6, "the same residual outward is still inside the outward scale");
        Weight(-2, 1.25, 3).Should().Be(0);
        Weight(2, 1.25, 3).Should().BeGreaterThan(0);
    }
}

/// <summary>
/// Renders a chroma plate — a rectangle, or a cylinder section — in front of a pinhole camera, onto
/// the tracker's RGB grid, with exact corners. The conventions are <see cref="PlatePoseMath"/>'s.
/// </summary>
internal static class SyntheticPlate
{
    /// <param name="Occluder">Per frame, true corners (TL TR BL BR) and a pixel: is something in front of the scene there?</param>
    internal sealed record Scene(
        int Width, int Height, int Frames, double Focal, double Aspect, double Kappa, Func<int, double[]> PoseAt,
        Func<int, (double X, double Y)[], double, double, bool>? Occluder)
    {
        private Plate Plate => new(Aspect, Kappa);

        public (double X, double Y)[] Corners(int frame) =>
            ProjectCorners(Camera.ForGrid(Width, Height, Focal), PoseAt(frame), Plate);

        public byte[] Render()
        {
            var rgb = new byte[Frames * Width * Height * 3];
            Camera cam = Camera.ForGrid(Width, Height, Focal);
            Span<double> r = stackalloc double[9];
            ReadOnlySpan<double> sub = [-0.25, 0.25];
            for (int f = 0; f < Frames; f++)
            {
                double[] pose = PoseAt(f);
                (double X, double Y)[] corners = Corners(f);
                Rodrigues(pose.AsSpan(0, 3), r);

                // Camera centre in plate coordinates: -R^T t.
                double ox = -(r[0] * pose[3] + r[3] * pose[4] + r[6] * pose[5]);
                double oy = -(r[1] * pose[3] + r[4] * pose[4] + r[7] * pose[5]);
                double oz = -(r[2] * pose[3] + r[5] * pose[4] + r[8] * pose[5]);
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        double sr = 0, sg = 0, sb = 0;
                        foreach (double dy in sub)
                        {
                            foreach (double dx in sub)
                            {
                                double px = x + dx, py = y + dy;
                                (double R, double G, double B) c;
                                if (Occluder is not null && Occluder(f, corners, px, py))
                                {
                                    c = (205, 160, 132);
                                }
                                else
                                {
                                    double cx = (px - cam.Cx) / cam.F, cy = (py - cam.Cy) / cam.F;
                                    double vx = r[0] * cx + r[3] * cy + r[6], vy = r[1] * cx + r[4] * cy + r[7], vz = r[2] * cx + r[5] * cy + r[8];
                                    c = Shade(ox, oy, oz, vx, vy, vz);
                                }

                                sr += c.R;
                                sg += c.G;
                                sb += c.B;
                            }
                        }

                        int o = ((f * Height + y) * Width + x) * 3;
                        rgb[o] = (byte)Math.Round(sr / 4);
                        rgb[o + 1] = (byte)Math.Round(sg / 4);
                        rgb[o + 2] = (byte)Math.Round(sb / 4);
                    }
                }
            }

            return rgb;
        }

        /// <summary>The colour seen along a ray (origin and direction in plate coordinates).</summary>
        private (double R, double G, double B) Shade(double ox, double oy, double oz, double vx, double vy, double vz)
        {
            const double bezel = 0.045;
            (double, double, double) background = (118, 100, 86);
            double s, v;
            if (Math.Abs(Kappa) < 1e-6)
            {
                if (Math.Abs(vz) < 1e-12)
                    return background;
                double lam = -oz / vz;
                if (lam <= 0)
                    return background;
                s = ox + lam * vx;
                v = oy + lam * vy;
            }
            else
            {
                double radius = Aspect / Kappa, zc = -radius;
                double a = vx * vx + vz * vz, b = 2 * (ox * vx + (oz - zc) * vz), c = ox * ox + (oz - zc) * (oz - zc) - radius * radius;
                double disc = b * b - 4 * a * c;
                if (disc <= 0 || a < 1e-12)
                    return background;
                double lam = (-b + Math.Sqrt(disc)) / (2 * a);
                if (lam <= 0)
                    return background;
                double hx = ox + lam * vx, hz = oz + lam * vz;
                s = radius * Math.Atan2(hx, hz - zc);
                v = oy + lam * vy;
            }

            if (Math.Abs(s) <= Aspect / 2 && Math.Abs(v) <= 0.5)
            {
                double light = 1 - 0.15 * (s / Aspect + 0.5) - 0.08 * (v + 0.5);
                return (28 * light, 222 * light, 40 * light);
            }

            return Math.Abs(s) <= Aspect / 2 + bezel && Math.Abs(v) <= 0.5 + bezel ? (20, 20, 23) : background;
        }
    }

    /// <summary>A pose from yaw, pitch and roll (degrees, applied in that order) and a translation.</summary>
    internal static double[] Pose(double yaw, double pitch, double roll, double tx, double ty, double tz)
    {
        double y = yaw * Math.PI / 180, p = pitch * Math.PI / 180, r = roll * Math.PI / 180;
        double[] ry = [Math.Cos(y), 0, Math.Sin(y), 0, 1, 0, -Math.Sin(y), 0, Math.Cos(y)];
        double[] rx = [1, 0, 0, 0, Math.Cos(p), -Math.Sin(p), 0, Math.Sin(p), Math.Cos(p)];
        double[] rz = [Math.Cos(r), -Math.Sin(r), 0, Math.Sin(r), Math.Cos(r), 0, 0, 0, 1];
        double[] m = Multiply(rz, Multiply(rx, ry));
        (double X, double Y, double Z) rv = RotationVector(m);
        return [rv.X, rv.Y, rv.Z, tx, ty, tz];

        static double[] Multiply(double[] a, double[] b)
        {
            var c = new double[9];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                    c[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
            }

            return c;
        }
    }
}
