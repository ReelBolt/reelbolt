#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Tests.InsertJitter.RenderPath;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo.Plates;

/// <summary>Fast tests (no ffmpeg, no plates) for the raw-plate harness (T-011 part 1): the meter, the scorer, the reproduction diff, the label guard.</summary>
public class PlatesTests
{
    private static Quad Plate(double t = 0, double dx = 0) => Quad.From(t, (300 + dx, 90), (1000 + dx, 84), (292 + dx, 505), (1002 + dx, 500));

    [Fact]
    public void Dominance_is_green_minus_the_stronger_of_red_and_blue_clamped_at_zero()
    {
        byte[] rgb = [61, 190, 81, 20, 20, 20, 200, 100, 50, 0, 236, 1];
        var d = new float[4];
        PlatesMeter.Dominance(rgb, d);
        d.Should().Equal(109f, 0f, 0f, 235f);
    }

    [Fact]
    public void The_dominance_meter_recovers_an_anti_aliased_green_plate_to_a_tenth_of_a_pixel()
    {
        const int w = 1344, h = 768;
        Quad truth = Plate();
        var cov = new float[w * h];
        EdgeMeter.Coverage(cov, w, h, truth);
        var field = cov.Select(c => 105f * c).ToArray();
        Quad seed = truth with { X0 = truth.X0 + 3, Y0 = truth.Y0 - 3, X3 = truth.X3 - 2.5, Y3 = truth.Y3 + 3 };
        PromoQuadFit fit = PromoEdgeFit.FitQuad(field, w, h, seed, PlatesMeter.Options);
        fit.Quad.Should().NotBeNull();
        for (int c = 0; c < 4; c++)
        {
            fit.Quad!.Value.Corner(c).X.Should().BeApproximately(truth.Corner(c).X, 0.1);
            fit.Quad!.Value.Corner(c).Y.Should().BeApproximately(truth.Corner(c).Y, 0.1);
        }
    }

    private static PlateMeasurement Perfect(IReadOnlyList<Quad> truth)
    {
        PromoEdge Edge((double X, double Y) a, (double X, double Y) b) => new(true, "ok", a, b, 0, 1, 100, 0, 100);
        var quads = new Quad?[truth.Count];
        var edges = new PromoEdge[truth.Count][];
        for (int i = 0; i < truth.Count; i++)
        {
            Quad q = truth[i];
            quads[i] = q;
            edges[i] = [Edge(q.Corner(0), q.Corner(1)), Edge(q.Corner(1), q.Corner(3)), Edge(q.Corner(3), q.Corner(2)), Edge(q.Corner(2), q.Corner(0))];
        }

        return new PlateMeasurement(truth.Count, quads, edges);
    }

    [Fact]
    public void The_scorer_reports_horizontal_wobble_on_the_vertical_edges_and_the_x_axis_only()
    {
        // A smooth drift plus an alternating +-0.5 px horizontal error: every frame step changes the error by 1.0 px in x.
        List<Quad> truth = Enumerable.Range(0, 60).Select(i => Plate(i / 24.0, dx: 0.3 * i)).ToList();
        List<Quad> track = truth.Select((q, i) => q with { X0 = q.X0 + (i % 2 == 0 ? 0.5 : -0.5), X1 = q.X1 + (i % 2 == 0 ? 0.5 : -0.5), X2 = q.X2 + (i % 2 == 0 ? 0.5 : -0.5), X3 = q.X3 + (i % 2 == 0 ? 0.5 : -0.5) }).ToList();
        TrackScore s = PlateScorer.Score("t", track, Perfect(truth));
        s.Quad.Row.Stats!.SlipRms.Should().BeApproximately(1.0, 0.02);
        s.QuadSlipX.Should().BeApproximately(1.0, 0.02);
        s.QuadSlipY.Should().BeLessThan(0.01);
        // The left and right edges carry it (their normal is horizontal); the top and bottom edges are tilted by < 1 degree, so they barely see it.
        s.VerticalEdges.Row.Stats!.SlipRms.Should().BeGreaterThan(0.9);
        s.HorizontalEdges.Row.Stats!.SlipRms.Should().BeLessThan(0.1);
        s.EdgesPooled.AbsMax.Should().BeApproximately(0.5, 0.05);
    }

    [Fact]
    public void A_perfect_track_scores_zero_slip_and_a_straight_line_has_no_leave_one_out_residual()
    {
        List<Quad> truth = Enumerable.Range(0, 40).Select(i => Plate(i / 24.0, dx: 0.4 * i)).ToList();
        TrackScore s = PlateScorer.Score("t", truth, Perfect(truth));
        s.Quad.Row.Stats!.SlipRms.Should().BeLessThan(1e-9);
        s.EdgesPooled.Row.Stats!.SlipRms.Should().BeLessThan(1e-9);
        s.NoTruth.ResidualRms.Should().BeLessThan(1e-6);
    }

    private static string Artifact(string dir, string name, double shiftNorm, string? colorTag = null)
    {
        JsonObject Kf(int i)
        {
            double Sx(double v) => v + shiftNorm;
            var corners = new JsonObject { ["x0"] = Sx(0.2), ["y0"] = 0.1, ["x1"] = Sx(0.7), ["y1"] = 0.1, ["x2"] = Sx(0.2), ["y2"] = 0.6, ["x3"] = Sx(0.7), ["y3"] = 0.6 };
            var surface = new JsonObject { ["x0"] = Sx(0.21), ["y0"] = 0.11, ["x1"] = Sx(0.69), ["y1"] = 0.11, ["x2"] = Sx(0.21), ["y2"] = 0.59, ["x3"] = Sx(0.69), ["y3"] = 0.59 };
            return new JsonObject
            {
                ["timeSec"] = i / 24.0,
                ["x0"] = corners["x0"]!.DeepClone(),
                ["y0"] = 0.1,
                ["x1"] = corners["x1"]!.DeepClone(),
                ["y1"] = 0.1,
                ["x2"] = corners["x2"]!.DeepClone(),
                ["y2"] = 0.6,
                ["x3"] = corners["x3"]!.DeepClone(),
                ["y3"] = 0.6,
                ["surfaceCorners"] = surface
            };
        }

        var root = new JsonObject
        {
            ["media"] = new JsonObject { ["width"] = 1344, ["height"] = 768 },
            ["insertRegions"] = new JsonArray(new JsonObject
            {
                ["confidence"] = 0.8,
                ["label"] = colorTag,
                ["keyframes"] = new JsonArray(Enumerable.Range(0, 5).Select(i => (JsonNode)Kf(i)).ToArray())
            })
        };
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, root.ToJsonString());
        return path;
    }

    [Fact]
    public void The_reproduction_diff_calls_identical_artifacts_identical_and_reports_a_shift_in_source_px()
    {
        string dir = Directory.CreateTempSubdirectory("plates-repro").FullName;
        try
        {
            string a = Artifact(dir, "a.json", 0), b = Artifact(dir, "b.json", 0), c = Artifact(dir, "c.json", 0.001);
            PlatesRepro.Compare(a, b).Identical.Should().BeTrue();
            ReproResult shifted = PlatesRepro.Compare(a, c);
            shifted.Identical.Should().BeFalse();
            shifted.CornersMax.Should().BeApproximately(1.344, 1e-6);
            shifted.SurfaceMax.Should().BeApproximately(1.344, 1e-6);
            shifted.FramesOverTolerance.Should().Be(5);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("shipped")]
    [InlineData("f1-decode")]
    [InlineData("integrated_2")]
    public void Plain_labels_make_an_output_directory_under_the_root(string label)
    {
        string root = Directory.CreateTempSubdirectory("plates-env").FullName;
        try
        {
            PlatesEnv env = PlatesEnv.FromLabel(label, root);
            env.OutDir.Should().Be(Path.Combine(root, label));
            Directory.Exists(env.OutDir).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData(".hidden")]
    [InlineData("")]
    public void A_label_that_could_leave_the_output_root_is_refused(string label)
    {
        string root = Directory.CreateTempSubdirectory("plates-env").FullName;
        try
        {
            Action act = () => PlatesEnv.FromLabel(label, root);
            act.Should().Throw<ArgumentException>();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
