using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace ReelBolt.WorkflowEngine.Tests.AssistantEval;

internal sealed class EvalBudgetExhaustedException(string reason) : Exception(reason);

/// <summary>
/// The spend cap. Every model request first RESERVES its worst case (estimated input plus the output cap) and is
/// refused when that would cross the token or USD cap; afterwards the reservation is replaced by the provider's
/// reported usage, or kept as the estimate when usage is missing (failed or timed-out request).
/// </summary>
internal sealed class EvalBudget(decimal? stopUsd, long stopTokens, decimal? inUsdPerToken, decimal? outUsdPerToken)
{
    public const int MaxOutputTokens = 4096;
    private const int ToolSchemaAllowanceTokens = 15_000;
    private readonly object _gate = new();
    private long _tokens;
    private decimal _usd;

    public sealed record Reservation(long InTokens, long OutTokens);

    public bool Exhausted { get; private set; }
    public string? Reason { get; private set; }
    public long Tokens { get { lock (_gate) return _tokens; } }
    public decimal Usd { get { lock (_gate) return _usd; } }

    public static long EstimateInput(IEnumerable<ChatMessage> messages, ChatOptions? options) =>
        (messages.Sum(m => (long)(m.Text?.Length ?? 0) + m.Contents.Sum(c => (long)c.ToString()!.Length))
         + (options?.Instructions?.Length ?? 0)) / 3 + ToolSchemaAllowanceTokens;

    public Reservation? TryReserve(long inTokens, long outTokens)
    {
        lock (_gate)
        {
            decimal cost = Cost(inTokens, outTokens);
            if (_tokens + inTokens + outTokens > stopTokens)
                return Refuse($"token cap of {stopTokens:N0} would be exceeded");
            if (stopUsd is { } cap && inUsdPerToken is not null && _usd + cost > cap)
                return Refuse($"USD cap of {cap.ToString("0.00", CultureInfo.InvariantCulture)} would be exceeded");
            _tokens += inTokens + outTokens;
            _usd += cost;
            return new Reservation(inTokens, outTokens);
        }
    }

    /// <summary>Replaces the reservation with actual usage; null usage keeps the worst-case estimate.</summary>
    public void Settle(Reservation r, long? actualIn, long? actualOut)
    {
        if (actualIn is null && actualOut is null) return;
        lock (_gate)
        {
            long i = actualIn ?? r.InTokens, o = actualOut ?? r.OutTokens;
            _tokens += i + o - r.InTokens - r.OutTokens;
            _usd += Cost(i, o) - Cost(r.InTokens, r.OutTokens);
        }
    }

    private decimal Cost(long i, long o) => i * (inUsdPerToken ?? 0) + o * (outUsdPerToken ?? 0);

    private Reservation? Refuse(string reason)
    {
        Exhausted = true;
        Reason = reason;
        return null;
    }
}

/// <summary>Provider exception text can echo request content; keep the type and the HTTP status only.</summary>
internal static class EvalErrors
{
    public static string Scrub(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            foreach (string prop in new[] { "Status", "StatusCode" })
            {
                object? v = e.GetType().GetProperty(prop)?.GetValue(e);
                if (v is int or Enum && Convert.ToInt32(v, CultureInfo.InvariantCulture) is > 0 and var code)
                    return $"{ex.GetType().Name} (HTTP {code})";
            }
        }
        return ex.GetType().Name;
    }
}

/// <summary>Run settings from the environment, with the fail-closed spend rules and path checks.</summary>
internal sealed record EvalSettings(int Samples, int Parallelism, EvalBudget Budget, string OutDir, string Label)
{
    public const int MaxParallelism = 4;
    private static readonly Regex LabelRx = new("^[a-z0-9-]{1,40}$", RegexOptions.Compiled);

    public static EvalSettings From(Func<string, string?> env, EvalModelSpec spec)
    {
        var prices = spec.Prices();
        string? tokensRaw = env("REELBOLT_EVAL_STOP_TOKENS");
        if (prices is null && string.IsNullOrWhiteSpace(tokensRaw))
            throw new InvalidOperationException(
                $"Refusing to start: model '{spec.Model}' has no price in ModelPriceCatalog and no REELBOLT_EVAL_PRICE_IN_PER_MTOK / "
                + "_OUT_ override, so the USD cap cannot work. Set the price override or REELBOLT_EVAL_STOP_TOKENS.");

        long stopTokens = long.TryParse(tokensRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long t) && t > 0 ? t : 25_000_000;
        decimal stopUsd = decimal.TryParse(env("REELBOLT_EVAL_STOP_USD"), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal u) && u > 0 ? u : 13.5m;
        int samples = int.TryParse(env("REELBOLT_EVAL_SAMPLES"), out int sn) && sn > 0 ? Math.Min(sn, 5) : 3;
        int parallel = int.TryParse(env("REELBOLT_EVAL_PARALLEL"), out int pn) ? Math.Clamp(pn, 1, MaxParallelism) : MaxParallelism;
        string label = env("REELBOLT_EVAL_LABEL") is { Length: > 0 } l ? l : "baseline";
        if (!LabelRx.IsMatch(label))
            throw new InvalidOperationException("REELBOLT_EVAL_LABEL must match [a-z0-9-] (1 to 40 characters).");

        string baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "assistant-eval-results"));
        string root = env("REELBOLT_EVAL_OUT") is { Length: > 0 } o ? Path.GetFullPath(o) : baseDir;
        string[] allowed = [Path.GetFullPath(AppContext.BaseDirectory), Path.GetFullPath(Path.GetTempPath())];
        if (!allowed.Any(a => root.StartsWith(a.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            throw new InvalidOperationException("REELBOLT_EVAL_OUT must be inside the test output directory or the system temp directory.");

        return new EvalSettings(samples, parallel,
            new EvalBudget(prices is null ? null : stopUsd, stopTokens, prices?.In, prices?.Out),
            Path.Combine(root, label), label);
    }
}
