using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Guards against quadratic blow-up without an absolute wall-clock bound (which flakes on a loaded runner).
/// <c>prepare(n)</c> builds the input for size n OUTSIDE the timed region and returns the action to time. The
/// action is timed at n and at factor*n (median of 5 runs each, after a warm-up) and the growth exponent
/// ln(t_big/t_small)/ln(factor) is asserted below <c>maxExponent</c> (linear ~1, quadratic ~2). A generous
/// absolute backstop still fails a hang. Set COMPLEXITY_GUARD_LOG to a file path to append measured exponents.
/// </summary>
internal static class ComplexityGuard
{
    private const int Runs = 5;
    private const double MinBaseMs = 20;
    private const int WarmupBudgetMs = 300;
    private const double BackstopMs = 20_000;

    public static double AssertNearLinear(Func<int, Action> prepare, int n, int factor = 8, double maxExponent = 1.5, string? name = null)
    {
        // Pick n so one run is long enough to measure (tiny timings are ratio-noisy).
        for (int i = 0; i < 6 && TimeOnceMs(prepare, n) < MinBaseMs; i++)
        {
            n *= 2;
        }

        // Warm up for a time budget so tier-0 JIT code is gone from the measured runs, then warm the big size once.
        Stopwatch budget = Stopwatch.StartNew();
        for (int runs = 0; runs < 3 || budget.ElapsedMilliseconds < WarmupBudgetMs; runs++)
        {
            prepare(n)();
        }

        prepare(n * factor)();
        GC.Collect();
        double small = MedianMs(prepare, n);
        double large = MedianMs(prepare, n * factor);
        double exponent = Math.Log(large / Math.Max(small, MinBaseMs)) / Math.Log(factor);
        string? log = Environment.GetEnvironmentVariable("COMPLEXITY_GUARD_LOG");
        if (log is not null)
        {
            File.AppendAllText(log, $"{name} n={n} small={small:F1}ms large={large:F1}ms exponent={exponent:F2}{Environment.NewLine}");
        }

        Assert.True(large < BackstopMs, $"workload at n={n * factor} took {large:F0} ms (backstop {BackstopMs:F0} ms)");
        Assert.True(
            exponent < maxExponent,
            $"not near-linear: t({n})={small:F1} ms, t({n * factor})={large:F1} ms, exponent {exponent:F2} >= {maxExponent} (linear ~1, quadratic ~2)");
        return exponent;
    }

    private static double TimeOnceMs(Func<int, Action> prepare, int n)
    {
        Action act = prepare(n);
        long start = Stopwatch.GetTimestamp();
        act();
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private static double MedianMs(Func<int, Action> prepare, int n)
    {
        double[] times = new double[Runs];
        for (int i = 0; i < Runs; i++)
        {
            Action act = prepare(n);
            long start = Stopwatch.GetTimestamp();
            act();
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        return times.OrderBy(t => t).ElementAt(Runs / 2);
    }
}
