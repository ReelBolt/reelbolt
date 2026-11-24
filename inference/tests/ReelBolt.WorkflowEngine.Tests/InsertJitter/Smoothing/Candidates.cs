#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Smoothing;

internal sealed record Candidate(string Id, string Group, Func<SceneData, ITrackSmoother> Make);

/// <summary>The candidate list and its parameter sweeps (T-004).</summary>
internal static class Candidates
{
    public static IEnumerable<Candidate> Baselines()
    {
        yield return new("K0", "baseline", d => new ProductionSmoother(d.S3Frames(), d.Gw, d.Gh, d.W, d.H));
        yield return new("raw", "baseline", _ => new IdentitySmoother());
        foreach (int n in new[] { 3, 5, 9 })
            yield return new($"box{n}", "baseline", _ => new BoxSmoother(n));
    }

    /// <summary>Source px per detection-grid px, the unit the tracker's accuracy scales with.</summary>
    public static double GridPx(SceneData d) => d.W / (double)d.Gw;

    /// <summary>Sigma = max(floor, own third-difference sigma, <paramref name="gridFraction"/> of one grid px); the outlier pass' number is NOT used.</summary>
    public static SigmaPolicy Prior(SceneData d, double gridFraction, double floor = 0.05) =>
        new(floor, gridFraction * GridPx(d), UseGivenNoise: false, UseOwn: true);

    public static IEnumerable<Candidate> K1()
    {
        foreach (int n in new[] { 5, 9, 13 })
        {
            foreach (double k in new[] { 0.5, 1.0, 2.0 })
            {
                yield return new($"K1-n{n}-k{k:0.#}", "K1", _ => new GatedBoxSmoother(n, k));
                yield return new($"K1p-n{n}-k{k:0.#}", "K1", d => new GatedBoxSmoother(n, k, 4, Prior(d, 0.18)));
            }
        }
    }

    public static IEnumerable<Candidate> K2()
    {
        foreach (double minCutoff in new[] { 0.5, 1.0, 2.0, 4.0 })
        {
            foreach (double beta in new[] { 0.0, 0.01, 0.05, 0.2 })
                yield return new($"K2-f{minCutoff:0.#}-b{beta:0.##}", "K2", _ => new OneEuroSmoother(minCutoff, beta));
        }

        foreach (double minCutoff in new[] { 1.0, 2.0 })
        {
            foreach (double beta in new[] { 0.05, 0.2 })
                yield return new($"K2z-f{minCutoff:0.#}-b{beta:0.##}", "K2", _ => new OneEuroSmoother(minCutoff, beta, OneEuroSmoother.Combine.ZeroPhase));
        }
    }

    public static IEnumerable<Candidate> K3()
    {
        foreach (double lambda in new[] { 0.02, 0.05, 0.1, 0.2 })
        {
            yield return new($"K3cv-L{lambda:0.###}", "K3", _ => new KalmanSmoother(2, lambda));
            yield return new($"K3cv-L{lambda:0.###}-ad", "K3", _ => new KalmanSmoother(2, lambda, adaptive: true));
            yield return new($"K3cvp-L{lambda:0.###}-ad", "K3", d => new KalmanSmoother(2, lambda, adaptive: true, sigma: Prior(d, 0.18)));
        }

        foreach (double lambda in new[] { 0.005, 0.01, 0.02, 0.05 })
        {
            yield return new($"K3ca-L{lambda:0.###}", "K3", _ => new KalmanSmoother(3, lambda));
            yield return new($"K3ca-L{lambda:0.###}-ad", "K3", _ => new KalmanSmoother(3, lambda, adaptive: true));
            yield return new($"K3cap-L{lambda:0.###}-ad", "K3", d => new KalmanSmoother(3, lambda, adaptive: true, sigma: Prior(d, 0.18)));
        }
    }

    public static IEnumerable<Candidate> K4()
    {
        // Legacy sigma (the outlier pass' number) for reference.
        yield return new("K4-legacy-t2", "K4", _ => new LocalPolySmoother(new LocalPolyOptions(2, [4, 6, 8, 12], 2.0), new SigmaPolicy(0.1)));
        foreach (double tau in new[] { 1.0, 1.5, 2.0, 3.0 })
        {
            foreach (double frac in new[] { 0.1, 0.18, 0.3 })
            {
                yield return new($"K4p-t{tau:0.#}-g{frac:0.##}", "K4",
                    d => new LocalPolySmoother(new LocalPolyOptions(2, [4, 6, 8, 12], tau), Prior(d, frac)));
                yield return new($"K4pr-t{tau:0.#}-g{frac:0.##}", "K4",
                    d => new LocalPolySmoother(new LocalPolyOptions(2, [0, 2, 4, 6, 8, 12], tau), Prior(d, frac)));
            }
        }

        yield return new("K4fix-H2", "K4", _ => new LocalPolySmoother(new LocalPolyOptions(2, [2], double.PositiveInfinity, 2, true, 0)));
        yield return new("K4fix-H3", "K4", _ => new LocalPolySmoother(new LocalPolyOptions(2, [3], double.PositiveInfinity, 2, true, 0)));
        yield return new("K4fix-H4", "K4", _ => new LocalPolySmoother(new LocalPolyOptions(2, [4], double.PositiveInfinity, 2, true, 0)));
        yield return new("K4fix-H6", "K4", _ => new LocalPolySmoother(new LocalPolyOptions(2, [6], double.PositiveInfinity, 2, true, 0)));
        yield return new("K4fix-H8", "K4", _ => new LocalPolySmoother(new LocalPolyOptions(2, [8], double.PositiveInfinity, 2, true, 0)));
        yield return new("K4fix-H12", "K4", _ => new LocalPolySmoother(new LocalPolyOptions(2, [12], double.PositiveInfinity, 2, true, 0)));
    }

    private static LocalPolyOptions Adaptive(double tau) => new(2, [4, 6, 8, 12], tau);

    private static LocalPolyOptions Fixed(int half) => new(2, [half], double.PositiveInfinity, 2, true, 0);

    public static IEnumerable<Candidate> K4Raw()
    {
        foreach (double tau in new[] { 1.5, 2.0 })
        {
            foreach (double frac in new[] { 0.1, 0.18 })
            {
                foreach (double raw in new[] { 6.0, 10.0, 20.0 })
                {
                    yield return new($"K4ra-t{tau:0.#}-g{frac:0.##}-r{raw:0.#}", "K4",
                        d => new LocalPolySmoother(new LocalPolyOptions(2, [4, 6, 8, 12], tau, RawAbove: raw), Prior(d, frac)));
                }
            }
        }

        yield return new("K4ra3-t2-g0.1-r10", "K4",
            d => new LocalPolySmoother(new LocalPolyOptions(2, [3, 5, 8, 12], 2.0, RawAbove: 10), Prior(d, 0.1)));
        yield return new("K4ra3-t1.5-g0.18-r10", "K4",
            d => new LocalPolySmoother(new LocalPolyOptions(2, [3, 5, 8, 12], 1.5, RawAbove: 10), Prior(d, 0.18)));
    }

    public static IEnumerable<Candidate> K5()
    {
        foreach (double tau in new[] { 1.5, 2.0 })
        {
            foreach (double frac in new[] { 0.1, 0.18 })
            {
                yield return new($"K5a-same-t{tau:0.#}-g{frac:0.##}", "K5",
                    d => new HadamardSmoother(Adaptive(tau), Adaptive(tau), Adaptive(tau), Prior(d, frac)));
                yield return new($"K5a-diag12-t{tau:0.#}-g{frac:0.##}", "K5",
                    d => new HadamardSmoother(Adaptive(tau), Adaptive(tau), Fixed(12), Prior(d, frac)));
                yield return new($"K5a-shape8-diag12-t{tau:0.#}-g{frac:0.##}", "K5",
                    d => new HadamardSmoother(Adaptive(tau), Fixed(8), Fixed(12), Prior(d, frac)));
            }
        }

        foreach ((int t, int l, int p) in new[] { (4, 4, 4), (4, 4, 12), (6, 6, 12), (8, 8, 12) })
            yield return new($"K5b-{t}-{l}-{p}", "K5", _ => new HomographySmoother(t, l, p));
    }

    public static IEnumerable<Candidate> K6()
    {
        foreach ((double enter, double exit) in new[] { (3.0, 5.0), (4.0, 6.0), (6.0, 8.0) })
        {
            yield return new($"K6raw-e{enter:0.#}-x{exit:0.#}", "K6", d => new LockSmoother(null, enter, exit, 8, Prior(d, 0.1)));
            yield return new($"K6k4-e{enter:0.#}-x{exit:0.#}", "K6",
                d => new LockSmoother(new LocalPolySmoother(Adaptive(2.0), Prior(d, 0.1)), enter, exit, 8, Prior(d, 0.1)));
        }
    }

    private static LocalPolySmoother Ra(SceneData d, double tau, double frac, double raw, int[]? widths = null) =>
        new(new LocalPolyOptions(2, widths ?? [4, 6, 8, 12], tau, RawAbove: raw), Prior(d, frac));

    /// <summary>The contenders stacked with the cadence wrapper (and the lock), the combinations the recommendation is chosen from.</summary>
    public static IEnumerable<Candidate> Final()
    {
        yield return new("F-K4ra", "final", d => Ra(d, 2.0, 0.1, 6.0));
        yield return new("F-cad-K4ra", "final", d => new CadenceSmoother(Ra(d, 2.0, 0.1, 6.0), d.Held));
        yield return new("F-cad-lock-K4ra", "final", d => new CadenceSmoother(new LockSmoother(Ra(d, 2.0, 0.1, 6.0), 4.0, 6.0, 8, Prior(d, 0.1)), d.Held));
        yield return new("F-cad-K4ra3", "final", d => new CadenceSmoother(Ra(d, 2.0, 0.1, 10.0, [3, 5, 8, 12]), d.Held));
        yield return new("F-cad-K4ra-t1.5-g0.18", "final", d => new CadenceSmoother(Ra(d, 1.5, 0.18, 6.0), d.Held));
        yield return new("F-cad-K1p-n5-k2", "final", d => new CadenceSmoother(new GatedBoxSmoother(5, 2.0, 4, Prior(d, 0.18)), d.Held));
        yield return new("F-cad-K3cvp-L0.05", "final", d => new CadenceSmoother(new KalmanSmoother(2, 0.05, adaptive: true, sigma: Prior(d, 0.18)), d.Held));
    }

    /// <summary>Parameter sensitivity of the recommended smoother: tau, accuracy prior, raw threshold, width set, polynomial degree.</summary>
    public static IEnumerable<Candidate> Sensitivity()
    {
        foreach (double tau in new[] { 1.5, 2.0, 3.0 })
        {
            foreach (double frac in new[] { 0.07, 0.1, 0.14 })
            {
                foreach (double raw in new[] { 6.0, 10.0 })
                    yield return new($"S-t{tau:0.#}-g{frac:0.##}-r{raw:0.#}", "sens", d => new CadenceSmoother(Ra(d, tau, frac, raw), d.Held));
            }
        }

        yield return new("S-widths[3,5,8,12]", "sens", d => new CadenceSmoother(Ra(d, 2.0, 0.1, 6.0, [3, 5, 8, 12]), d.Held));
        yield return new("S-widths[4,8,12,16]", "sens", d => new CadenceSmoother(Ra(d, 2.0, 0.1, 6.0, [4, 8, 12, 16]), d.Held));
        yield return new("S-widths[4,6,8,10,12]", "sens", d => new CadenceSmoother(Ra(d, 2.0, 0.1, 6.0, [4, 6, 8, 10, 12]), d.Held));
        yield return new("S-widths[5,8,12]", "sens", d => new CadenceSmoother(Ra(d, 2.0, 0.1, 6.0, [5, 8, 12]), d.Held));
        yield return new("S-noMedianSel", "sens", d => new CadenceSmoother(new LocalPolySmoother(new LocalPolyOptions(2, [4, 6, 8, 12], 2.0, SelectionMedianHalf: 0, RawAbove: 6.0), Prior(d, 0.1)), d.Held));
        yield return new("S-noTukey", "sens", d => new CadenceSmoother(new LocalPolySmoother(new LocalPolyOptions(2, [4, 6, 8, 12], 2.0, Tukey: false, RawAbove: 6.0), Prior(d, 0.1)), d.Held));
        yield return new("S-degree1", "sens", d => new CadenceSmoother(new LocalPolySmoother(new LocalPolyOptions(1, [4, 6, 8, 12], 2.0, RawAbove: 6.0), Prior(d, 0.1)), d.Held));
        yield return new("S-degree3", "sens", d => new CadenceSmoother(new LocalPolySmoother(new LocalPolyOptions(3, [4, 6, 8, 12], 2.0, RawAbove: 6.0), Prior(d, 0.1)), d.Held));
    }

    /// <summary>The adjacent fix on its own and stacked on the better smoothers.</summary>
    public static IEnumerable<Candidate> Cadence()
    {
        yield return new("cad-raw", "cadence", d => new CadenceSmoother(new IdentitySmoother(), d.Held));
        yield return new("cad-K4fix-H4", "cadence", d => new CadenceSmoother(new LocalPolySmoother(Fixed(4)), d.Held));
        yield return new("cad-K4p-t2-g0.1", "cadence", d => new CadenceSmoother(new LocalPolySmoother(Adaptive(2.0), Prior(d, 0.1)), d.Held));
        yield return new("cad-K4p-t1.5-g0.18", "cadence", d => new CadenceSmoother(new LocalPolySmoother(Adaptive(1.5), Prior(d, 0.18)), d.Held));
        yield return new("cad-K3cvp-L0.05-ad", "cadence", d => new CadenceSmoother(new KalmanSmoother(2, 0.05, adaptive: true, sigma: Prior(d, 0.18)), d.Held));
        yield return new("cad-K3cvp-L0.1-ad", "cadence", d => new CadenceSmoother(new KalmanSmoother(2, 0.1, adaptive: true, sigma: Prior(d, 0.18)), d.Held));
    }
}

/// <summary>Reference: the S3 track unchanged.</summary>
internal sealed class IdentitySmoother : ITrackSmoother
{
    public string Name => "raw S3 (no smoothing)";

    public IReadOnlyList<Quad> Smooth(IReadOnlyList<Quad> track, double fps, double noisePx) => track;
}
