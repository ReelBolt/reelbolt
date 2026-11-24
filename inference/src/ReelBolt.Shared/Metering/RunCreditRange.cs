namespace ReelBolt.Shared.Metering;

/// <summary>What ONE execution of a step is expected to cost: its median and its interquartile band, in millicredits.</summary>
public readonly record struct StepCostDistribution(long TypicalMillis, long P25Millis, long P75Millis);

/// <summary>
/// One step of a run as <see cref="RunCreditRange.Combine"/> sees it. <paramref name="FixedMaxMillis"/>
/// &gt; 0 marks a fixed-price step (a VideoGenerate step with a declared price): it is taken out of the
/// range and counted in the "up to" ceiling instead, and <paramref name="Cost"/> is ignored.
/// </summary>
public sealed record RunStepInput(int StepOrder, StepCostDistribution Cost, long FixedMaxMillis);

/// <summary>
/// A repeating window: the steps with <c>TargetStepOrder &lt;= order &lt;= LoopStepOrder</c> run N times,
/// where N has mean <paramref name="MeanPasses"/> and variance <paramref name="PassVariance"/>.
/// <paramref name="MaxIterations"/> is the ceiling a fixed-price step inside the window can reach.
/// </summary>
public sealed record LoopInput(int LoopStepOrder, int TargetStepOrder, double MeanPasses, double PassVariance, int MaxIterations);

/// <summary>The displayed run range: the typical total, the usual band around it, and the fixed-price ceiling.</summary>
public sealed record RunRange(long TypicalMillis, long LowMillis, long HighMillis, long FixedMaxMillis,
    IReadOnlyDictionary<int, double> ExpectedRunsByStepOrder);

/// <summary>
/// How much of a run's variance each part contributed: per loop (keyed by loop step order) and per
/// step outside every loop (keyed by step order). The estimator orders its spread reasons by these.
/// </summary>
public sealed record RunVarianceBreakdown(
    IReadOnlyDictionary<int, double> LoopVarianceByLoopStepOrder,
    IReadOnlyDictionary<int, double> StepVarianceByStepOrder);

/// <summary>
/// The pure combination maths behind the pre-run "≈ X credits (usually A–B)" display. On a cold
/// start the hold uses it too: <see cref="ColdStartHoldPlan"/> splits the <c>LowMillis</c> of the
/// cold steps (cold-start profiles and loops only, no display history) across those steps.
///
/// <para>
/// Every step is a split normal: median m, with one sigma below it, (m − p25)/0.674, and another
/// above it, (p75 − m)/0.674, so a skewed step (a p25 of 0.82m and a p75 of 2.68m, say) keeps its
/// own shape and a single step shows exactly its own p25–p75. Independent steps add their variances
/// side by side, so the run's relative spread narrows as steps are added — which is what the
/// measured per-agent data supports (a p75/p25 of 1.3–3x within one agent type). A review loop
/// scales its body by a random number of passes, per side: mean E[N]·B and variance
/// E[N²]·V_side + Var(N)·B² (the law of total variance over N independent passes). The displayed
/// band is typical − 0.674·sigma_low to typical + 0.674·sigma_high.
/// </para>
/// </summary>
public static class RunCreditRange
{
    /// <summary>The p25–p75 band of a normal is ±this many sigma.</summary>
    public const double UsualZ = 0.674;

    /// <summary>A normal's interquartile range is this many sigma.</summary>
    public const double SigmaPerIqr = 1.349;

    /// <summary>Cold-start passes of a review loop: 1–2, mean 1.5 (measured staging mean 1.25, max 2).</summary>
    public const double ColdStartMeanPasses = 1.5;

    /// <summary>The variance of a pass count that is 1 or 2 with equal odds.</summary>
    public const double ColdStartPassVariance = 0.25;

    /// <summary>Combines the steps of a run into its typical total, usual band and fixed-price ceiling. Never throws.</summary>
    public static RunRange Combine(IReadOnlyList<RunStepInput> steps, IReadOnlyList<LoopInput> loops)
    {
        Analysis analysis = Analyze(steps, loops);

        double typical = analysis.Mean;
        long typicalMillis = Round(typical);
        long low = Math.Max(0, Round(typical - (UsualZ * Math.Sqrt(Math.Max(0d, analysis.VarianceLow)))));
        long high = Math.Max(typicalMillis, Round(typical + (UsualZ * Math.Sqrt(Math.Max(0d, analysis.VarianceHigh)))));

        return new RunRange(typicalMillis, Math.Min(low, typicalMillis), high, analysis.FixedMax, analysis.ExpectedRuns);
    }

    /// <summary>
    /// Where the run's variance comes from (see <see cref="RunVarianceBreakdown"/>). Each part counts
    /// both sides of its split normal, V_low + V_high, so a part that is skewed either way ranks by its
    /// whole spread.
    /// </summary>
    public static RunVarianceBreakdown Decompose(IReadOnlyList<RunStepInput> steps, IReadOnlyList<LoopInput> loops)
    {
        Analysis analysis = Analyze(steps, loops);
        return new RunVarianceBreakdown(analysis.LoopVariance, analysis.StepVariance);
    }

    /// <summary>
    /// The cold-start pass model for a review loop: 1 or 2 passes (mean 1.5, variance 0.25), never
    /// MaxIterations. A loop that may only run once (MaxIterations &lt;= 1) runs exactly once.
    /// </summary>
    public static LoopInput ColdStartLoop(int loopStepOrder, int targetStepOrder, int maxIterations)
        => maxIterations <= 1
            ? new LoopInput(loopStepOrder, targetStepOrder, 1d, 0d, Math.Max(1, maxIterations))
            : new LoopInput(loopStepOrder, targetStepOrder, ColdStartMeanPasses, ColdStartPassVariance, maxIterations);

    /// <summary>True when <paramref name="loop"/> has a body: a target at or before the loop step, and at least 1.</summary>
    public static bool HasBody(LoopInput loop)
        => loop.TargetStepOrder >= 1 && loop.TargetStepOrder <= loop.LoopStepOrder;

    /// <summary>
    /// The window <paramref name="stepOrder"/> belongs to, or null: the narrowest loop whose body
    /// contains it (ties go to the earliest loop step, so the answer is deterministic).
    /// </summary>
    public static LoopInput? WindowOf(int stepOrder, IReadOnlyList<LoopInput> loops)
    {
        LoopInput? best = null;
        foreach (LoopInput loop in loops)
        {
            if (!HasBody(loop) || stepOrder < loop.TargetStepOrder || stepOrder > loop.LoopStepOrder)
            {
                continue;
            }

            if (best is null
                || Width(loop) < Width(best)
                || (Width(loop) == Width(best) && loop.LoopStepOrder < best.LoopStepOrder))
            {
                best = loop;
            }
        }

        return best;
    }

    /// <summary>
    /// One execution's spread as <see cref="Decompose"/> counts it: sigma_low² + sigma_high², with
    /// sigma_low = (m − p25)/0.674 and sigma_high = (p75 − m)/0.674, each at least 0.
    /// </summary>
    public static double StepVariance(StepCostDistribution cost)
    {
        (double low, double high) = Sigmas(cost);
        return (low * low) + (high * high);
    }

    private static (double Low, double High) Sigmas(StepCostDistribution cost)
    {
        double m = Math.Max(0, cost.TypicalMillis);
        return (Math.Max(0d, m - cost.P25Millis) / UsualZ, Math.Max(0d, cost.P75Millis - m) / UsualZ);
    }

    private static int Width(LoopInput loop) => loop.LoopStepOrder - loop.TargetStepOrder;

    private static Analysis Analyze(IReadOnlyList<RunStepInput> steps, IReadOnlyList<LoopInput> loops)
    {
        Dictionary<int, double> expectedRuns = [];
        Dictionary<LoopInput, (double Mean, double VarianceLow, double VarianceHigh)> bodies = [];
        Dictionary<int, double> stepVariance = [];
        double mean = 0;
        double varianceLow = 0;
        double varianceHigh = 0;
        long fixedMax = 0;

        foreach (RunStepInput step in steps)
        {
            LoopInput? window = WindowOf(step.StepOrder, loops);
            double passes = window is null ? 1d : Math.Max(0d, window.MeanPasses);
            expectedRuns[step.StepOrder] = passes;

            if (step.FixedMaxMillis > 0)
            {
                // Fixed price: outside the range, inside the ceiling — MaxIterations times when a
                // loop may re-run it, because "up to" has to hold whatever the reviewer decides.
                fixedMax += step.FixedMaxMillis * (window is null ? 1 : Math.Max(1, window.MaxIterations));
                continue;
            }

            double m = Math.Max(0, step.Cost.TypicalMillis);
            (double sigmaLow, double sigmaHigh) = Sigmas(step.Cost);

            if (window is null)
            {
                mean += m;
                varianceLow += sigmaLow * sigmaLow;
                varianceHigh += sigmaHigh * sigmaHigh;
                stepVariance[step.StepOrder] = (sigmaLow * sigmaLow) + (sigmaHigh * sigmaHigh);
            }
            else
            {
                (double b, double vLow, double vHigh) = bodies.TryGetValue(window, out (double, double, double) body) ? body : (0d, 0d, 0d);
                bodies[window] = (b + m, vLow + (sigmaLow * sigmaLow), vHigh + (sigmaHigh * sigmaHigh));
            }
        }

        Dictionary<int, double> loopVariance = [];
        foreach ((LoopInput loop, (double b, double vLow, double vHigh)) in bodies)
        {
            double n = Math.Max(0d, loop.MeanPasses);
            double varN = Math.Max(0d, loop.PassVariance);
            double n2 = varN + (n * n);
            double low = (n2 * vLow) + (varN * b * b);
            double high = (n2 * vHigh) + (varN * b * b);
            mean += n * b;
            varianceLow += low;
            varianceHigh += high;
            loopVariance[loop.LoopStepOrder] = loopVariance.GetValueOrDefault(loop.LoopStepOrder) + low + high;
        }

        return new Analysis(mean, varianceLow, varianceHigh, fixedMax, expectedRuns, loopVariance, stepVariance);
    }

    private static long Round(double value)
        => double.IsFinite(value) ? (long)Math.Round(value, MidpointRounding.AwayFromZero) : 0;

    private sealed record Analysis(
        double Mean,
        double VarianceLow,
        double VarianceHigh,
        long FixedMax,
        Dictionary<int, double> ExpectedRuns,
        Dictionary<int, double> LoopVariance,
        Dictionary<int, double> StepVariance);
}
