using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.Marketplace;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>
/// Enforces the published retention period, in batches. Routine moderation log rows and actioned reports older than
/// <c>RetentionMonths</c> are deleted. Log rows tied to a safety or illegal-content finding, and every row of a recipe or version that
/// has one, are kept (the safety checks and legal obligations read them). A dismissed report is reduced to a minimal record (recipe,
/// reporter, state, dates) because it still stops that reporter counting toward an auto-hide. Open reports are never touched.
/// Does nothing when the marketplace is off.
/// </summary>
public sealed class MarketplaceRetentionSweeper(
    IServiceScopeFactory scopes,
    IOptions<MarketplaceOptions> options,
    ILogger<MarketplaceRetentionSweeper> logger) : BackgroundService
{
    internal const int BatchSize = 500;
    private const int MaxIntervalMinutes = 7 * 24 * 60;

    /// <summary>Actions whose rows can record a finding; their reason (read from the detail in memory) decides.</summary>
    private static readonly string[] FindingActions =
        ["removed", "rejected", "version_rejected", "text_blocked", "preview_blocked", "preview_failed", MarketplaceSafetyService.CsamMatchAudit];

    /// <summary>Reasons that mark a safety, illegal-content, copyright or moderation-rejection finding. Exact matches only.</summary>
    private static readonly HashSet<string> FindingReasons = new(StringComparer.Ordinal)
    {
        "safety", "unsafe", "illegal", "csam", "csam_match", "copyright", "content", "text_blocked", "preview_blocked", "admin_rejected"
    };

    internal static int ClampInterval(int minutes) => Math.Clamp(minutes, 1, MaxIntervalMinutes);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MarketplaceOptions o = options.Value;
        if (!o.Enabled || o.RetentionSweepIntervalMinutes <= 0 || o.RetentionMonths <= 0)
            return;

        using PeriodicTimer timer = new(TimeSpan.FromMinutes(ClampInterval(o.RetentionSweepIntervalMinutes)));
        do
        {
            try
            {
                using IServiceScope scope = scopes.CreateScope();
                (int audit, int reports) = await SweepAsync(
                    scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>(), DateTime.UtcNow, stoppingToken);
                if (audit + reports > 0)
                    logger.LogInformation("Marketplace retention removed {Audit} log rows and {Reports} reports", audit, reports);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "A marketplace retention sweep failed");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>One pass. Returns how many log rows and how many reports were deleted (tombstoned dismissed reports are not counted).</summary>
    internal async Task<(int Audit, int Reports)> SweepAsync(InferenceApiDbContext db, DateTime now, CancellationToken ct)
    {
        MarketplaceOptions o = options.Value;
        if (!o.Enabled || o.RetentionMonths <= 0)
            return (0, 0);

        DateTime cutoff = now.AddMonths(-o.RetentionMonths);
        int audit = 0;
        int reports = 0;

        try
        {
            audit = await SweepAuditAsync(db, cutoff, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The marketplace log retention step failed");
            db.ChangeTracker.Clear(); // a failed save leaves rows tracked; the next step must not retry them
        }

        try
        {
            reports = await DeleteActionedReportsAsync(db, cutoff, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The marketplace report retention step failed");
            db.ChangeTracker.Clear(); // a failed save leaves rows tracked; the next step must not retry them
        }

        try
        {
            await TombstoneDismissedReportsAsync(db, cutoff, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The marketplace dismissed-report retention step failed");
            db.ChangeTracker.Clear(); // a failed save leaves rows tracked; the next step must not retry them
        }

        return (audit, reports);
    }

    /// <summary>
    /// Deletes old routine log rows. A finding is classified in memory (exact action, then the exact <c>reason</c> of its detail), and
    /// every row of a recipe or version that has a finding is kept. Candidates are read in pages of plain-column SQL; protected rows
    /// stay at the front of the order, so each page skips them and the loop ends.
    /// </summary>
    private static async Task<int> SweepAuditAsync(InferenceApiDbContext db, DateTime cutoff, CancellationToken ct)
    {
        var seeds = await db.MarketplaceAudit.AsNoTracking().Where(a => FindingActions.Contains(a.Action))
            .Select(a => new { a.Id, a.RecipeId, a.VersionId, a.Action, a.DetailJson }).ToListAsync(ct);
        HashSet<Guid> recipes = [];
        HashSet<Guid> versions = [];
        HashSet<Guid> rows = [];
        foreach (var seed in seeds)
        {
            if (seed.Action != MarketplaceSafetyService.CsamMatchAudit && !IsFindingReason(seed.DetailJson))
                continue;
            rows.Add(seed.Id);
            if (seed.RecipeId is { } r)
                recipes.Add(r);
            if (seed.VersionId is { } v)
                versions.Add(v);
        }

        int deleted = 0;
        int skipped = 0;
        while (true)
        {
            List<MarketplaceAuditEntry> page = await db.MarketplaceAudit.Where(a => a.CreatedAt < cutoff)
                .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).Skip(skipped).Take(BatchSize).ToListAsync(ct);
            if (page.Count == 0)
                break;
            List<MarketplaceAuditEntry> doomed = [.. page.Where(a => !rows.Contains(a.Id)
                && !(a.RecipeId is { } r && recipes.Contains(r)) && !(a.VersionId is { } v && versions.Contains(v)))];
            skipped += page.Count - doomed.Count;
            if (doomed.Count == 0)
                continue;
            db.MarketplaceAudit.RemoveRange(doomed);
            await db.SaveChangesAsync(ct);
            deleted += doomed.Count;
        }

        return deleted;
    }

    private static bool IsFindingReason(string? detailJson)
    {
        if (string.IsNullOrEmpty(detailJson))
            return false;
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(detailJson) is System.Text.Json.Nodes.JsonObject o
                && o["reason"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out string? reason) && reason is not null
                && FindingReasons.Contains(reason);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static async Task<int> DeleteActionedReportsAsync(InferenceApiDbContext db, DateTime cutoff, CancellationToken ct)
    {
        int reports = 0;
        while (true)
        {
            List<MarketplaceReport> rows = await db.MarketplaceReports
                .Where(r => r.State == ReportState.Actioned && (r.HandledAt ?? r.CreatedAt) < cutoff)
                .OrderBy(r => r.CreatedAt).Take(BatchSize).ToListAsync(ct);
            if (rows.Count == 0)
                break;
            db.MarketplaceReports.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
            reports += rows.Count;
        }

        return reports;
    }

    private static async Task TombstoneDismissedReportsAsync(InferenceApiDbContext db, DateTime cutoff, CancellationToken ct)
    {
        // Dismissed reports keep only what the auto-hide check reads; rows already reduced no longer match, so a repeat is a no-op.
        while (true)
        {
            List<MarketplaceReport> rows = await db.MarketplaceReports
                .Where(r => r.State == ReportState.Dismissed && (r.HandledAt ?? r.CreatedAt) < cutoff
                    && (r.Note != null || r.HandledBy != null || r.Reason != ReportReason.Other))
                .OrderBy(r => r.CreatedAt).Take(BatchSize).ToListAsync(ct);
            if (rows.Count == 0)
                break;
            foreach (MarketplaceReport r in rows)
            {
                r.Note = null;
                r.HandledBy = null;
                r.Reason = ReportReason.Other;
            }

            await db.SaveChangesAsync(ct);
        }
    }
}
