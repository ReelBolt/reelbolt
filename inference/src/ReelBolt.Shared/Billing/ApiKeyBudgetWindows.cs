using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Billing;

/// <summary>One cap window of an API key.</summary>
public enum ApiKeyBudgetWindow
{
    Daily,
    Weekly,
    Monthly
}

/// <summary>Why a hold was refused by a key's cap: the tightest failing window, its cap, what it already used and when it resets.</summary>
public sealed record ApiKeyBudgetRefusal(ApiKeyBudgetWindow Window, long CapMillis, long UsedMillis, DateTime ResetsAt);

/// <summary>One window of a key's budget: the cap (null = none), what the window used so far, and when it resets (UTC).</summary>
public sealed record ApiKeyWindowUsage(ApiKeyBudgetWindow Window, long? CapMillis, long UsedMillis, DateTime ResetsAt);

/// <summary>
/// The single home of the per-key window math (UTC day, ISO week from Monday, calendar month), shared by
/// the ledger's locked reserve, the API preflight and the dashboard usage read. A spend counts in the
/// window its hold was made in; open holds count at what they hold, closed rows at what they charged.
/// </summary>
public static class ApiKeyBudgetWindows
{
    public static readonly ApiKeyBudgetWindow[] All = [ApiKeyBudgetWindow.Daily, ApiKeyBudgetWindow.Weekly, ApiKeyBudgetWindow.Monthly];

    public static DateTime WindowStart(ApiKeyBudgetWindow window, DateTime nowUtc)
    {
        DateTime day = DateTime.SpecifyKind(nowUtc.Date, DateTimeKind.Utc);
        return window switch
        {
            ApiKeyBudgetWindow.Daily => day,
            ApiKeyBudgetWindow.Weekly => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
            _ => new DateTime(day.Year, day.Month, 1, 0, 0, 0, DateTimeKind.Utc)
        };
    }

    public static DateTime ResetsAt(ApiKeyBudgetWindow window, DateTime nowUtc)
    {
        DateTime start = WindowStart(window, nowUtc);
        return window switch
        {
            ApiKeyBudgetWindow.Daily => start.AddDays(1),
            ApiKeyBudgetWindow.Weekly => start.AddDays(7),
            _ => start.AddMonths(1)
        };
    }

    public static long? CapOf(ApiKey key, ApiKeyBudgetWindow window)
        => window switch
        {
            ApiKeyBudgetWindow.Daily => key.DailyCapMillis,
            ApiKeyBudgetWindow.Weekly => key.WeeklyCapMillis,
            _ => key.MonthlyCapMillis
        };

    public static string Name(ApiKeyBudgetWindow window)
        => window switch
        {
            ApiKeyBudgetWindow.Daily => "daily",
            ApiKeyBudgetWindow.Weekly => "weekly",
            _ => "monthly"
        };

    /// <summary>Usage of every listed key in all three windows, from ONE grouped query (rows since the month start cover the widest window).</summary>
    public static async Task<Dictionary<Guid, long[]>> UsedAsync(DbContext db, IReadOnlyCollection<Guid> keyIds, DateTime nowUtc, CancellationToken ct)
    {
        Dictionary<Guid, long[]> used = keyIds.ToDictionary(id => id, _ => new long[3]);
        if (keyIds.Count == 0)
        {
            return used;
        }

        DateTime widest = All.Min(w => WindowStart(w, nowUtc));
        var rows = await db.Set<ApiKeySpend>().AsNoTracking()
            .Where(s => keyIds.Contains(s.ApiKeyId) && s.CreatedAt >= widest)
            .Select(s => new { s.ApiKeyId, s.CreatedAt, Amount = s.Status == ApiKeySpendStatus.Open ? s.HeldMillis : (s.ChargedMillis ?? 0) })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            long[] sums = used[row.ApiKeyId];
            for (int i = 0; i < All.Length; i++)
            {
                if (row.CreatedAt >= WindowStart(All[i], nowUtc))
                {
                    sums[i] += row.Amount;
                }
            }
        }

        return used;
    }

    public static List<ApiKeyWindowUsage> Usage(ApiKey key, long[] used, DateTime nowUtc)
        => All.Select((w, i) => new ApiKeyWindowUsage(w, CapOf(key, w), used[i], ResetsAt(w, nowUtc))).ToList();

    /// <summary>The tightest failing window (daily before weekly before monthly) for a hold of <paramref name="amountMillis"/>, or null when it fits.</summary>
    public static ApiKeyBudgetRefusal? FirstExceeded(ApiKey key, long[] used, long amountMillis, DateTime nowUtc)
    {
        for (int i = 0; i < All.Length; i++)
        {
            long? cap = CapOf(key, All[i]);
            if (cap is not null && used[i] + amountMillis > cap.Value)
            {
                return new ApiKeyBudgetRefusal(All[i], cap.Value, used[i], ResetsAt(All[i], nowUtc));
            }
        }

        return null;
    }
}
