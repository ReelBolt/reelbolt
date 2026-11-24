#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.RenderPath;

/// <summary>Fast tests (no ffmpeg) for the T-005 render-path tooling.</summary>
public class RenderPathTests
{
    private static Quad CentredQuad(double w, double h, double dx = 0, double dy = 0) =>
        Quad.From(0, (672 - w / 2 + dx, 384 - h / 2 + dy), (672 + w / 2 + dx, 384 - h / 2 + dy), (672 - w / 2 + dx, 384 + h / 2 + dy), (672 + w / 2 + dx, 384 + h / 2 + dy));

    private static RigInput Input(int frames, double dxStep = 0.05)
    {
        var quads = new List<Quad>();
        var keys = new List<InsertQuadFrame>();
        for (int f = 0; f < frames; f++)
        {
            Quad q = CentredQuad(700, 400, f * dxStep, f * dxStep * 0.5);
            quads.Add(q with { T = f / 24.0 });
            keys.Add(new InsertQuadFrame(f, q.X0, q.Y0, q.X1, q.Y1, q.X2, q.Y2, q.X3, q.Y3));
        }

        return new RigInput(1344, 768, 24, 1, frames, keys, quads, "0xFFFFFF", null);
    }

    [Fact]
    public void Rig_default_graph_is_the_pre_prescale_full_canvas_chain()
    {
        // The default variant is the BASELINE the F3 numbers are quoted against: the original recipe (content and mask scaled to the
        // canvas's inner size, a 1 px black border), written out here independently of the shipped builder, which now prescales.
        RigInput input = Input(40);
        double end = input.Frames * input.FpsDen / (double)input.FpsNum;
        string persp = ScreenInsertFilterBuilder.BuildPerspectiveFilter(input.Keyframes);
        string expected =
            "[0:v]null[b0];" +
            $"[1:v]scale=1342:766:flags=bilinear,fps=24/1,format=yuv444p,pad=1344:768:1:1:black,{persp},setpts=PTS-STARTPTS+0/TB[sic0];" +
            $"color=c=white:size=1342x766:rate=24/1:duration={FfmpegArgvFormat.Number(end + 1)},format=gray,pad=1344:768:1:1:black,{persp},setpts=PTS-STARTPTS+0/TB[sim0];" +
            "[sic0][sim0]alphamerge[sia0];" +
            $"[b0][sia0]overlay=x=0:y=0:eof_action=pass:enable='between(t,0,{FfmpegArgvFormat.Number(end)})'[vc];" +
            "[vc]format=rgb24[vout]";
        RenderRig.BuildGraph(input, new RigVariant("base", "baseline"), out string pipe).Should().Be(expected);
        pipe.Should().Be("rgb24");
    }

    [Fact]
    public void Production_variant_is_the_shipped_chain()
    {
        RigInput input = Input(40);
        double end = input.Frames * input.FpsDen / (double)input.FpsNum;
        var insert = new ResolvedScreenInsert("r0", "x", 0, end, input.Keyframes, input.FpsNum, input.FpsDen);
        string production = ScreenInsertFilterBuilder.BuildFilterChain("[b0]", [insert], input.W, input.H, _ => 1, "[vc]");
        RenderRig.BuildGraph(input, new RigVariant("prod", "shipped", Production: true), out _)
            .Should().Be("[0:v]null[b0];" + production + ";[vc]format=rgb24[vout]");
    }

    [Fact]
    public void Compensated_pin_lands_the_white_region_on_the_commanded_quad()
    {
        RigInput input = Input(3, 7);
        IReadOnlyList<InsertQuadFrame> keys = RenderRig.Remap(input, new RigVariant("c", "c", CompensateBorder: true));
        for (int f = 0; f < 3; f++)
        {
            InsertQuadFrame k = keys[f];
            UnitSquareMap m = new Quad(0, k.X0, k.Y0, k.X1, k.Y1, k.X2, k.Y2, k.X3, k.Y3).ToMap();
            // The white region is [1, W-1] x [1, H-1] of the frame: its corners must be the commanded corners.
            (double X, double Y) tl = m.Map(1.0 / 1344, 1.0 / 768);
            (double X, double Y) br = m.Map(1343.0 / 1344, 767.0 / 768);
            tl.X.Should().BeApproximately(input.Commanded[f].X0, 1e-6);
            tl.Y.Should().BeApproximately(input.Commanded[f].Y0, 1e-6);
            br.X.Should().BeApproximately(input.Commanded[f].X3, 1e-6);
            br.Y.Should().BeApproximately(input.Commanded[f].Y3, 1e-6);
        }
    }

    [Fact]
    public void Prescale_region_covers_the_largest_quad_and_pins_it()
    {
        RigInput input = Input(30, 4);
        (int cw, int ch, int ox, int oy) = RenderRig.ContentRegion(input, prescale: true);
        cw.Should().BeInRange(700, 704);
        ch.Should().BeInRange(400, 404);
        (cw % 2).Should().Be(0);
        IReadOnlyList<InsertQuadFrame> keys = RenderRig.Remap(input, new RigVariant("p", "p", Prescale: true, CompensateBorder: true));
        InsertQuadFrame k = keys[10];
        UnitSquareMap m = new Quad(0, k.X0, k.Y0, k.X1, k.Y1, k.X2, k.Y2, k.X3, k.Y3).ToMap();
        (double X, double Y) tl = m.Map(ox / 1344.0, oy / 768.0);
        tl.X.Should().BeApproximately(input.Commanded[10].X0, 1e-6);
        tl.Y.Should().BeApproximately(input.Commanded[10].Y0, 1e-6);
    }

    [Fact]
    public void Edge_meter_recovers_an_antialiased_quad_to_a_few_hundredths_of_a_pixel()
    {
        // 0.05 px steps in x and y plus a rotation: the analytic anti-aliased control the meter is judged against.
        var cov = new float[1344 * 768];
        var measured = new List<Quad>();
        var truth = new List<Quad>();
        for (int f = 0; f < 41; f++)
        {
            double a = f * 0.0125 * Math.PI / 180;
            Quad q = Rotated(CentredQuad(700, 400, f * 0.05, f * 0.03), a);
            EdgeMeter.Coverage(cov, 1344, 768, q);
            Quad? m = EdgeMeter.Measure(cov, 1344, 768, q with { X0 = q.X0 + 1.2, Y0 = q.Y0 - 0.8, X3 = q.X3 - 1.0, Y3 = q.Y3 + 1.1 });
            m.Should().NotBeNull();
            measured.Add(m!.Value with { T = f / 24.0 });
            truth.Add(q with { T = f / 24.0 });
        }

        JitterStats st = JitterMetrics.Compute(measured, truth, 24);
        st.ErrMax.Should().BeLessThan(0.08);
        st.SlipRms.Should().BeLessThan(0.03);
    }

    private static Quad Rotated(Quad q, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        (double X, double Y) R((double X, double Y) p) => (672 + (p.X - 672) * c - (p.Y - 384) * s, 384 + (p.X - 672) * s + (p.Y - 384) * c);
        return Quad.From(q.T, R(q.Corner(0)), R(q.Corner(1)), R(q.Corner(2)), R(q.Corner(3)));
    }

    [Fact]
    public void Overscan_amplifies_independent_corner_noise_by_the_closed_form_and_the_smoothed_shape_does_not()
    {
        // Static plate, i.i.d. noise sigma 0.2 px per coordinate: slip of the noise alone is 2 sigma.
        var rng = new Random(7);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        Quad truth = CentredQuad(700, 400);
        var raw = new List<Quad>();
        var truthList = new List<Quad>();
        for (int f = 0; f < 600; f++)
        {
            raw.Add(Quad.From(f / 24.0, (truth.X0 + 0.2 * Gauss(), truth.Y0 + 0.2 * Gauss()), (truth.X1 + 0.2 * Gauss(), truth.Y1 + 0.2 * Gauss()),
                (truth.X2 + 0.2 * Gauss(), truth.Y2 + 0.2 * Gauss()), (truth.X3 + 0.2 * Gauss(), truth.Y3 + 0.2 * Gauss())));
            truthList.Add(truth with { T = f / 24.0 });
        }

        const double o = 0.10;
        double rawSlip = JitterMetrics.Compute(raw, truthList, 24).SlipRms;
        var expTruth = truthList.Select(q => OverscanBench.Expand(q, o)).ToList();
        double p = JitterMetrics.Compute(raw.Select(q => OverscanBench.Expand(q, o)).ToList(), expTruth, 24).SlipRms / rawSlip;
        double m = JitterMetrics.Compute(OverscanBench.Smoothed(raw, o, 15), expTruth, 24).SlipRms / rawSlip;
        p.Should().BeApproximately(Math.Sqrt(1 + 1.5 * o + 0.75 * o * o), 0.02);
        m.Should().BeApproximately(1.0, 0.02);
    }
}
