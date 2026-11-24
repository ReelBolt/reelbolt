using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Services;

/// <summary>
/// The engine-side half of the per-plan retention job (F10): once a day (at
/// <c>Billing:StepCacheRetention:HourUtc</c>, default 4), per organization, it retires that
/// organization's <c>workflow_step_cache_entries</c> rows whose own age exceeds the plan's
/// <see cref="OrganizationEntitlementSet.RetentionDays"/> — the same per-plan value the Inference
/// API's <c>StorageRetentionService</c> applies to the objects those rows name.
///
/// <para>
/// <b>Why this has to be engine-side.</b> <c>workflow_step_cache_entries</c> is owned by
/// <see cref="WorkflowEngineDbContext"/> and is not mapped by <c>InferenceApiDbContext</c> at all
/// (no DbSet, no shadow entity), so the Inference API's storage sweep cannot see these rows — and
/// therefore cannot see an object whose only remaining handle is a cache entry either. That table
/// is the gap this class closes.
/// </para>
///
/// <para>
/// <b>Chosen strategy: age-based.</b> An entry is deleted when
/// <c>CreatedAt &lt; now - plan.RetentionDays</c>. <see cref="WorkflowStepCacheEntry.CreatedAt"/> is
/// the row's own age: it is set when the entry is written and refreshed by
/// <c>StepResultCache.StoreAsync</c>'s upsert — but <b>not</b> by a cache HIT, which only bumps
/// <see cref="WorkflowStepCacheEntry.HitCount"/>/<see cref="WorkflowStepCacheEntry.LastHitAt"/>. An
/// entry that keeps being hit without ever being re-produced therefore does age out here, and the
/// next execution recomputes it once and stores a fresh row. That is the cost this strategy accepts,
/// and it is a small one: one recompute, against a table that otherwise grows without bound. (Aging
/// on <c>LastHitAt</c> instead would keep such a hot row, but it buys a marginal retention fact at
/// the price of a second, weaker age definition; the plan's retention is defined on age.)
/// </para>
///
/// <para>
/// This needs no object-store probe, no cross-context coordination and no second source of truth,
/// and it is safe by construction: a deleted entry costs at most one recompute and nothing else — the
/// row is a cache, not the record — and its upstream inputs are almost certainly gone by then anyway,
/// because the cache key hashes the <i>fully resolved step input</i>, which changes whenever any
/// upstream output does.
/// </para>
///
/// <para>
/// <b>Rejected: reference-based</b> (delete entries whose referenced storage key no longer exists).
/// It would have to reuse the cache-hit path's existence probe, which today lives as private
/// methods on <c>StepResultCache</c> — republishing them as a seam is new machinery for no gain.
/// Worse, it only ever reclaims an entry <i>after</i> the Inference API has already purged its
/// object, and it can never reclaim the many entries that carry no storage key at all (an
/// <c>Extract</c>, a plan, any output that fits in <see cref="WorkflowStepCacheEntry.Output"/> is
/// the bulk of the table). So it cannot bound the table's growth, which is the actual objective.
/// The correctness question it was aimed at is already answered elsewhere: a hit whose storage key
/// has been purged degrades to a cache miss, because
/// <c>StepCacheOptions.ValidateStorageArtifacts</c> HEAD-validates those keys before serving. A
/// stale entry is therefore not a bug — it is a row this sweep exists to reclaim.
/// </para>
///
/// <para>
/// <b>What it deliberately does not do.</b> It never deletes an object: the object store belongs to
/// the Inference API's two-phase mark/purge sweep (soft-mark, grace period, batched S3 delete), and
/// a second writer there would be a second source of truth for what may be deleted. It does not
/// touch <c>workflow_step_results</c>: that table <i>is</i> reachable from the Inference API's purge
/// (it maps the engine-owned table through its own context precisely so it may write it), and it
/// already nulls <c>OutputStorageKey</c>/<c>ArtifactStorageKey</c>/<c>TimelineStorageKey</c> on every
/// row naming a deleted object, so there is no gap for the engine to close. An object whose only
/// remaining handle was a cache entry was already invisible to that sweep; retiring the row changes
/// nothing about it, and this class does not pretend to.
/// </para>
///
/// <para>
/// <b>Disabled by default</b> (<c>Billing:StepCacheRetention:Enabled</c>, absent from the shipped
/// configuration). The switch, not the entitlement, is the self-host guard: with
/// <c>Billing:Enabled=false</c> the entitlement service returns
/// <see cref="OrganizationEntitlementSet.Unlimited"/>, whose <c>RetentionDays</c> is the 30-day
/// record default rather than "no limit", so self-host is protected only by this job staying off.
/// A "keep forever" plan is expressed as <see cref="OrganizationEntitlementSet.UnlimitedInt"/> and
/// is skipped here exactly as it is there.
/// </para>
///
/// <para>
/// <b>Bounded work.</b> Projects are read one page of <see cref="BatchSize"/> at a time (ordered by
/// id, so a page is a keyset continuation and never an offset scan), entries are deleted in batches
/// of <see cref="BatchSize"/>, and the whole pass stops at
/// <c>Billing:StepCacheRetention:TimeBudgetSeconds</c> (default 600) and resumes on the next run.
/// Unlike the Inference API's sweep this one needs no tenant scope: the engine's context applies no
/// organization query filter (deliberately — see <see cref="WorkflowEngineDbContext"/>), and
/// entitlements are read by explicit organization id.
/// </para>
/// </summary>
public sealed class StepCacheRetentionSweeper : BackgroundService
{
    /// <summary>Projects paged, and cache entries deleted, per database round-trip.</summary>
    public const int BatchSize = 1000;

    /// <summary>Hour (UTC) of the daily pass, overridable by <c>Billing:StepCacheRetention:HourUtc</c>.</summary>
    public const int DefaultHourUtc = 4;

    /// <summary>Wall-clock budget of one pass, overridable by <c>Billing:StepCacheRetention:TimeBudgetSeconds</c>.</summary>
    public const int DefaultTimeBudgetSeconds = 600;

    /// <summary>Configuration section read by this job.</summary>
    public const string SectionName = "Billing:StepCacheRetention";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StepCacheRetentionSweeper> _logger;

    public StepCacheRetentionSweeper(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<StepCacheRetentionSweeper> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue($"{SectionName}:Enabled", false))
            return;

        int hour = Math.Clamp(_configuration.GetValue($"{SectionName}:HourUtc", DefaultHourUtc), 0, 23);

        while (!stoppingToken.IsCancellationRequested)
        {
            DateTime now = DateTime.UtcNow;
            DateTime next = now.Date.AddHours(hour);
            if (next <= now) next = next.AddDays(1);
            try { await Task.Delay(next - now, stoppingToken); } catch (OperationCanceledException) { return; }

            try
            {
                StepCacheRetentionRunResult run = await RunOnceAsync(DateTime.UtcNow, stoppingToken);
                _logger.LogInformation(
                    "Step cache retention removed {RemovedEntries} expired cache entr(ies) across {SweptProjects} project(s) (complete: {Completed})",
                    run.RemovedEntries, run.SweptProjects, run.Completed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Step cache retention run failed; will retry at the next scheduled hour");
            }
        }
    }

    /// <summary>
    /// One retention pass, exposed for tests: every organization whose plan bounds retention, every
    /// one of its projects, oldest cache rows first, until the work is done or the time budget runs
    /// out. A project that fails is logged and left for the next run rather than aborting the pass.
    /// </summary>
    public async Task<StepCacheRetentionRunResult> RunOnceAsync(DateTime nowUtc, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        IOrganizationEntitlements entitlements = scope.ServiceProvider.GetRequiredService<IOrganizationEntitlements>();

        int budgetSeconds = _configuration.GetValue($"{SectionName}:TimeBudgetSeconds", DefaultTimeBudgetSeconds);
        DateTime deadline = DateTime.UtcNow.AddSeconds(budgetSeconds);

        int removed = 0;
        int swept = 0;
        Guid? lastProjectId = null;

        // Keyset pagination over projects: a page is always BatchSize rows, so an install with a
        // million projects costs a million-project sweep in bounded pages rather than one query.
        while (true)
        {
            List<ProjectOrg> page = await db.Projects.AsNoTracking()
                .Where(p => lastProjectId == null || p.Id > lastProjectId.Value)
                .OrderBy(p => p.Id)
                .Take(BatchSize)
                .Select(p => new ProjectOrg(p.Id, p.OrganizationId))
                .ToListAsync(ct);

            if (page.Count == 0)
                break;

            lastProjectId = page[^1].Id;

            (int pageRemoved, int pageSwept, bool pageCompleted) =
                await SweepPageAsync(db, page, entitlements, nowUtc, deadline, ct);

            removed += pageRemoved;
            swept += pageSwept;
            if (!pageCompleted)
            {
                // The budget ran out mid-page. The rest of this page and every project after it are
                // swept on the next run; nothing is left half-deleted, because a batch is committed
                // as a unit.
                return new StepCacheRetentionRunResult(removed, swept, false);
            }

            if (page.Count < BatchSize)
                break;
        }

        return new StepCacheRetentionRunResult(removed, swept, true);
    }

    private async Task<(int Removed, int SweptProjects, bool Completed)> SweepPageAsync(
        WorkflowEngineDbContext db,
        List<ProjectOrg> page,
        IOrganizationEntitlements entitlements,
        DateTime nowUtc,
        DateTime deadline,
        CancellationToken ct)
    {
        int removed = 0;
        int swept = 0;

        foreach (IGrouping<Guid, ProjectOrg> org in page.GroupBy(p => p.OrganizationId))
        {
            OrganizationEntitlementSet plan = await entitlements.GetAsync(org.Key, ct);

            if (plan.RetentionDays >= OrganizationEntitlementSet.UnlimitedInt)
            {
                _logger.LogDebug(
                    "Organization {OrganizationId} keeps its storage forever; its step cache is left alone",
                    org.Key);
                continue;
            }

            if (plan.RetentionDays < 1)
            {
                // 0 is "delete immediately" rather than "not entitled" — a mis-set plan. Deleting on
                // the strength of it is not a recoverable mistake, so the organization is skipped,
                // exactly as the Inference API's storage sweep skips it.
                _logger.LogWarning(
                    "Organization {OrganizationId} has retention days {Days}; step cache retention is skipped for it",
                    org.Key, plan.RetentionDays);
                continue;
            }

            // The plan's retention is a whole-day window ending now; the cutoff is inclusive-free so
            // an entry written exactly RetentionDays ago is still inside it.
            DateTime cutoff = nowUtc.AddDays(-plan.RetentionDays);

            foreach (ProjectOrg project in org)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    _logger.LogWarning(
                        "Step cache retention hit its time budget after {SweptProjects} project(s); the rest is swept on the next run",
                        swept);
                    return (removed, swept, false);
                }

                try
                {
                    (int projectRemoved, bool projectComplete) =
                        await SweepProjectAsync(db, project.Id, cutoff, deadline, ct);
                    removed += projectRemoved;
                    swept++;

                    if (!projectComplete)
                    {
                        // The budget ran out inside this project's batches. Reporting the pass as
                        // incomplete is what tells an operator (and the test) that rows are still
                        // waiting, rather than that the sweep found nothing more to do.
                        _logger.LogWarning(
                            "Step cache retention hit its time budget inside project {ProjectId}; its remaining rows are swept on the next run",
                            project.Id);
                        return (removed, swept, false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One project's failure must not stop the sweep, and the rows it did not delete
                    // are simply still there for the next run.
                    _logger.LogError(ex, "Retiring the expired step cache of project {ProjectId} failed; it is retried on the next run", project.Id);
                }
            }
        }

        return (removed, swept, true);
    }

    /// <summary>
    /// Deletes one project's cache entries older than <paramref name="cutoff"/> in batches of
    /// <see cref="BatchSize"/>, oldest first — never a whole-project row set at once. Returns how many
    /// were removed and whether the project was finished before the time budget ran out (a batch that
    /// is committed as a unit, so "not finished" never means "half deleted").
    /// </summary>
    private async Task<(int Removed, bool Complete)> SweepProjectAsync(
        WorkflowEngineDbContext db, Guid projectId, DateTime cutoff, DateTime deadline, CancellationToken ct)
    {
        int removed = 0;
        while (true)
        {
            if (DateTime.UtcNow >= deadline)
                return (removed, false);

            List<WorkflowStepCacheEntry> batch = await db.WorkflowStepCacheEntries
                .Where(e => e.ProjectId == projectId && e.CreatedAt < cutoff)
                .OrderBy(e => e.CreatedAt)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
                break;

            db.WorkflowStepCacheEntries.RemoveRange(batch);
            await db.SaveChangesAsync(ct);
            removed += batch.Count;

            if (batch.Count < BatchSize)
                break;
        }

        if (removed > 0)
            _logger.LogDebug(
                "Step cache retention removed {RemovedEntries} entr(ies) for project {ProjectId} (retention cutoff {Cutoff:O})",
                removed, projectId, cutoff);

        return (removed, true);
    }

    /// <summary>A project and the organization that pays for it.</summary>
    private readonly record struct ProjectOrg(Guid Id, Guid OrganizationId);
}

/// <summary>What one step-cache retention pass did.</summary>
/// <param name="RemovedEntries"><c>workflow_step_cache_entries</c> rows deleted (each one costs a recompute at most).</param>
/// <param name="SweptProjects">Projects the pass examined, including those with nothing to remove.</param>
/// <param name="Completed">
/// False when the pass stopped at its time budget with work left; the remainder is picked up by the
/// next run. True also for a database with nothing to do.
/// </param>
public sealed record StepCacheRetentionRunResult(int RemovedEntries, int SweptProjects, bool Completed);
