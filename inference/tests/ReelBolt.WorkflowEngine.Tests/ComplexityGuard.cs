using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Guards against quadratic blow-up without an absolute wall-clock bound (which flakes on a loaded runner).
/// <c>prepare(n)</c> builds the input for size n OUTSIDE the timed region and returns the action to time. The
/// action is timed at n and at factor*n (minimum of 7 runs each after a GC, which filters scheduler/GC noise, after a warm-up) and the growth exponent
/// ln(t_big/t_small)/ln(factor) is asserted below <c>maxExponent</c> (linear ~1, quadratic ~2). A generous
/// absolute backstop still fails a hang. Set COMPLEXITY_GUARD_LOG to a file path to append measured exponents.
/// </summary>
internal static class ComplexityGuard
{
    private const int Runs = 7;
    private const double MinBaseMs = 45;
    private const int WarmupBudgetMs = 300;
    private const double BackstopMs = 20_000;

    public static double AssertNearLinear(Func<int, Action> prepare, int n, int factor = 4, double maxExponent = 1.7, string? name = null)
    {
        // Warm up for a time budget so tier-0 JIT code is gone from the measured runs.
        Stopwatch budget = Stopwatch.StartNew();
        for (int runs = 0; runs < 3 || budget.ElapsedMilliseconds < WarmupBudgetMs; runs++)
        {
            prepare(n)();
        }

        // Pick n from WARM minimum timings so the measured small run really is >= MinBaseMs and the floor does not apply.
        for (int i = 0; i < 8 && MinMs(prepare, n, 3) < MinBaseMs * 1.15; i++)
        {
            n *= 2;
        }

        prepare(n * factor)();
        double small = MinMs(prepare, n, Runs);
        double large = MinMs(prepare, n * factor, Runs);
        bool floored = small < MinBaseMs;
        double exponent = Math.Log(large / Math.Max(small, MinBaseMs)) / Math.Log(factor);
        string? log = Environment.GetEnvironmentVariable("COMPLEXITY_GUARD_LOG");
        if (log is not null)
        {
            File.AppendAllText(log, $"{name} n={n} small={small:F1}ms large={large:F1}ms exponent={exponent:F2}{(floored ? " FLOORED" : string.Empty)}{Environment.NewLine}");
        }

        Assert.True(large < BackstopMs, $"workload at n={n * factor} took {large:F0} ms (backstop {BackstopMs:F0} ms)");
        Assert.True(
            exponent < maxExponent,
            $"not near-linear: t({n})={small:F1} ms, t({n * factor})={large:F1} ms, exponent {exponent:F2} >= {maxExponent} (linear ~1, quadratic ~2)");
        return exponent;
    }

    private static double MinMs(Func<int, Action> prepare, int n, int runs)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        double best = double.MaxValue;
        for (int i = 0; i < runs; i++)
        {
            Action act = prepare(n);
            long start = Stopwatch.GetTimestamp();
            act();
            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        return best;
    }
}
