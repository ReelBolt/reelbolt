using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.Marketplace;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>
/// The delisting hook for content safety (docs/csam.md). When any project of an author's organization holds a CSAM output hold, or a
/// project that is the preview source of one of the organization's recipes is under a content quarantine, every recipe of that
/// organization goes <c>removed</c> with reason <c>safety</c>, by the admin transition (so <c>human_rejected_at</c> is set), and its
/// objects are deleted. It polls instead of hooking the quarantine writers, so the CSAM and moderation code paths are untouched.
/// No notice is sent: a safety removal must not tip anyone off.
/// </summary>
public sealed class MarketplaceSafetyService(
    InferenceApiDbContext db,
    IMarketplaceObjectStore objects,
    ILogger<MarketplaceSafetyService> logger)
{
    public const string Reason = "safety";

    /// <summary>How long after a safety removal the object deletion is retried on every pass.</summary>
    public static readonly TimeSpan DeletionRetryWindow = TimeSpan.FromDays(2);

    /// <summary>
    /// True when the user, or any organization the recipe's author belongs to, has a project under a CSAM output hold or a content
    /// quarantine. Restoring a recipe of such an account is refused.
    /// </summary>
    public static async Task<bool> HasActiveHoldAsync(InferenceApiDbContext db, MarketplaceRecipe recipe, IEnumerable<Guid> submitters, CancellationToken ct)
    {
        Guid[] users = [.. submitters.Append(recipe.AuthorUserId).Distinct()];
        Guid org = recipe.AuthorOrgId;
        return await db.Projects.IgnoreQueryFilters().AsNoTracking().AnyAsync(
            p => (p.CsamOutputHoldAt != null || p.ContentQuarantinedAt != null) && (p.OrganizationId == org || users.Contains(p.OwnerId)), ct);
    }

    public const string CsamMatchAudit = "preview_csam_match";

    /// <summary>
    /// The users who SUBMITTED a version whose preview matched the CSAM control (audit rows of the finalizer joined to the version's
    /// submitter). No column: the record is the audit trail. Attribution is to the submitter, not the owner of the source project, so a
    /// member who publishes from a teammate's project is flagged too.
    /// </summary>
    public static IQueryable<Guid> CsamMatchedSubmitters(InferenceApiDbContext db) =>
        db.MarketplaceAudit.AsNoTracking().Where(a => a.Action == CsamMatchAudit && a.VersionId != null)
            .Join(db.MarketplaceRecipeVersions.AsNoTracking(), a => a.VersionId, v => (Guid?)v.Id, (a, v) => v.SubmittedByUserId);

    public static Task<bool> HasCsamMatchAsync(InferenceApiDbContext db, Guid userId, CancellationToken ct) =>
        CsamMatchedSubmitters(db).AnyAsync(id => id == userId, ct);

    /// <summary>Returns how many recipes were removed in this pass.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;

        // Who is exposed: the organizations and the submitting users of every recipe that is not removed yet.
        HashSet<Guid> matchedSubmitters = [.. await CsamMatchedSubmitters(db).Distinct().ToListAsync(ct)];
        var live = await db.MarketplaceRecipes.AsNoTracking().Where(r => r.Status != RecipeStatus.Removed)
            .Select(r => new { r.Id, r.AuthorOrgId, r.AuthorUserId, Submitters = r.Versions.Select(v => v.SubmittedByUserId).ToList() }).ToListAsync(ct);

        int removed = 0;
        if (live.Count > 0)
        {
            // (the matched submitters are among the live recipes' submitters, so they are flagged below even with no project held)
            Guid[] liveOrgs = [.. live.Select(r => r.AuthorOrgId).Distinct()];
            Guid[] liveUsers = [.. live.SelectMany(r => r.Submitters.Append(r.AuthorUserId)).Distinct()];
            var held = await db.Projects.IgnoreQueryFilters().AsNoTracking()
                .Where(p => (liveOrgs.Contains(p.OrganizationId) || liveUsers.Contains(p.OwnerId)) && (p.CsamOutputHoldAt != null || p.ContentQuarantinedAt != null))
                .Select(p => new { p.Id, p.OrganizationId, p.OwnerId, Csam = p.CsamOutputHoldAt != null }).ToListAsync(ct);

            // A CSAM hold flags the project's organization AND its owner everywhere: every recipe of that organization, and every
            // recipe the owner authored or submitted a version of in any other workspace.
            HashSet<Guid> flaggedOrgs = [.. held.Where(h => h.Csam).Select(h => h.OrganizationId)];
            HashSet<Guid> flaggedUsers = [.. held.Where(h => h.Csam).Select(h => h.OwnerId).Concat(matchedSubmitters)];

            // A content quarantine matters only when the project is the preview source of a version.
            var quarantined = held.Where(h => !h.Csam).ToList();
            if (quarantined.Count > 0)
            {
                var sources = await db.MarketplaceRecipeVersions.AsNoTracking()
                    .Where(v => v.PreviewSourceJson != null && v.Recipe!.Status != RecipeStatus.Removed)
                    .Select(v => new { v.PreviewSourceJson, v.SubmittedByUserId, v.Recipe!.AuthorOrgId }).ToListAsync(ct);
                foreach (var source in sources)
                {
                    if (quarantined.Any(q => source.PreviewSourceJson!.Contains($"projects/{q.Id}/", StringComparison.OrdinalIgnoreCase)))
                    {
                        flaggedOrgs.Add(source.AuthorOrgId);
                        flaggedUsers.Add(source.SubmittedByUserId);
                    }
                }
            }

            Guid[] targets = [.. live
                .Where(r => flaggedOrgs.Contains(r.AuthorOrgId) || flaggedUsers.Contains(r.AuthorUserId) || r.Submitters.Any(flaggedUsers.Contains))
                .Select(r => r.Id)];
            if (targets.Length > 0)
                removed = await RemoveRecipesAsync(targets, now, ct);
        }

        // A removal whose object deletion failed (or was interrupted) is retried while it is recent: safety and admin removals alike.
        DateTime since = now - DeletionRetryWindow;
        Guid[] retry = await db.MarketplaceRecipes.AsNoTracking()
            .Where(r => r.Status == RecipeStatus.Removed && r.UpdatedAt >= since).Select(r => r.Id).ToArrayAsync(ct);
        foreach (Guid id in retry)
            await DeleteObjectsAsync(id, ct);

        return removed;
    }

    private async Task<int> RemoveRecipesAsync(Guid[] recipeIds, DateTime now, CancellationToken ct)
    {
        List<MarketplaceRecipe> recipes = await db.MarketplaceRecipes.Where(r => recipeIds.Contains(r.Id) && r.Status != RecipeStatus.Removed).ToListAsync(ct);
        List<Guid> done = [];
        foreach (MarketplaceRecipe recipe in recipes)
        {
            if (!RecipeStatusMachine.TryTransition(recipe, RecipeStatus.Removed, RecipeActor.Admin, now))
                continue;

            recipe.RemovedReason = Reason;
            recipe.Revision++;
            db.MarketplaceAudit.Add(new MarketplaceAuditEntry
            {
                Id = Guid.NewGuid(),
                RecipeId = recipe.Id,
                VersionId = recipe.CurrentVersionId,
                Action = "removed",
                ActorKind = "system",
                DetailJson = new JsonObject { ["reason"] = Reason }.ToJsonString(),
                CreatedAt = now
            });
            foreach (MarketplaceReport report in await db.MarketplaceReports.Where(r => r.RecipeId == recipe.Id && r.State == ReportState.Open).ToListAsync(ct))
            {
                report.State = ReportState.Actioned;
                report.HandledAt = now;
            }

            done.Add(recipe.Id);
        }

        if (done.Count == 0)
            return 0;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // A recipe changed under us; the next pass sees it again.
            logger.LogWarning(ex, "A safety removal of marketplace recipes lost a concurrent update; it is retried on the next pass");
            db.ChangeTracker.Clear();
            return 0;
        }

        logger.LogWarning("Marketplace: removed {Count} recipe(s) after a safety hold", done.Count);
        foreach (Guid id in done)
            await DeleteObjectsAsync(id, ct);
        return done.Count;
    }

    private async Task DeleteObjectsAsync(Guid recipeId, CancellationToken ct)
    {
        try
        {
            await objects.DeleteRecipeObjectsAsync(recipeId, await RecipeObjectProtection.LoadAsync(db, recipeId, ct), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Deleting the objects of safety-removed marketplace recipe {RecipeId} failed; retried on the next pass", recipeId);
        }
    }
}

/// <summary>Runs <see cref="MarketplaceSafetyService"/> every few minutes while <c>Marketplace:Enabled</c> is on.</summary>
public sealed class MarketplaceSafetySweeper(IServiceScopeFactory scopes, IOptionsMonitor<MarketplaceOptions> options, ILogger<MarketplaceSafetySweeper> logger) : BackgroundService
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (options.CurrentValue.Enabled)
                {
                    try
                    {
                        using IServiceScope scope = scopes.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<MarketplaceSafetyService>().RunOnceAsync(stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "The marketplace safety sweep failed; it runs again on the next interval");
                    }
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }
}
