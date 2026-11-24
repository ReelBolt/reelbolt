using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows.VideoEvents;

namespace ReelBolt.Inference.Api.Services.Background;

/// <summary>
/// The per-plan storage retention job (F10): once a day (at <c>Billing:StorageRetention:HourUtc</c>,
/// default 3), per organization, it deletes the generated storage older than that organization's
/// plan retention (<see cref="OrganizationEntitlementSet.RetentionDays"/>, D19 default 30 days):
/// <c>outputFiles</c> renders and <c>agentFiles</c> step artifacts (a <c>VideoAnalyze</c> analysis
/// document, an EDL timeline, the sandbox journal).
/// <para>
/// <b>Two-phase delete.</b> An expired object is first SOFT-MARKED: a
/// <see cref="StorageRetentionMark"/> row records it and the Inference-API-owned reference to it
/// (<c>project_files</c>) is dropped, so the object is out of the product but still on disk. Only
/// marks at least <c>Billing:StorageRetention:GraceDays</c> old (7 by default) are then PURGED, in
/// batches of 1,000 through the storage seam's S3 multi-object delete
/// (<see cref="IFileStorageService.DeleteManyAsync"/>). A mis-set plan therefore costs a week rather
/// than a moment, and the purge is resumable: a batch that fails keeps its marks for the next run.
/// </para>
/// <para>
/// <b>References.</b> A reference must never outlive the object it names, so the purge also clears the
/// <c>workflow_step_results</c> keys that named the deleted objects — <c>OutputStorageKey</c>,
/// <c>ArtifactStorageKey</c> and <c>TimelineStorageKey</c> — exactly as
/// <c>OutputsController.DeleteTakesAsync</c> nulls <c>OutputStorageKey</c> when it removes a take.
/// (<c>ExcludeFromMigrations</c> means this context does not own that table's SCHEMA, not that it may
/// not write it.) Without this a row would keep being listed and then fail on download.
/// <see cref="StorageRetentionRunResult.ClearedReferences"/> reports how many keys were cleared.
/// </para>
/// <para>
/// <b>Two graces.</b> A mark written by a delete (the delete paths, the failed-save recovery, an
/// import's failed cleanup) is an explicit instruction to remove that key, so it is due after
/// <c>Billing:StorageRetention:DeleteGraceMinutes</c> (60 by default) rather than the full retention
/// grace. Only marks the sweep itself wrote keep the week-long grace, which exists so a mis-set plan
/// is recoverable. The purge query still deletes every due mark regardless of category.
/// </para>
/// <para>
/// <b>The orphan pass.</b> A third phase lists each project's <c>userFiles</c> prefix and its
/// <c>agentFiles/render-events/</c> prefix (through the optional <see cref="IObjectListing"/>) and marks
/// any object older than <c>Billing:StorageRetention:OrphanSweep:MinAgeHours</c> (24 by default) that no
/// <c>project_files</c> row and no existing mark names — and, for a sidecar, whose mirrored render is
/// gone too (a live render's events are never marked) — so an object nothing else can reach is still
/// eventually purged. It is bounded (<c>OrphanSweep:MaxKeysPerProject</c>), fail-closed (a store that
/// cannot list, or a listing failure, skips the pass) and idempotent (the storage key is unique).
/// </para>
/// <para>
/// <b>The analysis pass cache.</b> The same phase deletes every cache entry under a project's
/// <c>agentFiles/analysis-cache/</c> prefix older than
/// <c>Billing:StorageRetention:OrphanSweep:AnalysisCacheMaxAgeDays</c> (30 by default). Those are the engine's
/// cached analysis passes of the project's videos; a frame grid among them holds small pictures of its source. No
/// row names them and the entry key does not say which file it belongs to, so age is the only rule: an entry of
/// a deleted file goes at that age, and so does one of a live file, which the engine then simply makes again.
/// They are deleted directly, with no mark and no grace: nothing is lost when a cache entry goes. Only a key with
/// the shape of an entry (<see cref="IsAnalysisCacheEntry"/>) that no <c>project_files</c> row names is deleted:
/// any other object under that folder (a project file whose own path starts with <c>analysis-cache/</c>) is left
/// to the row that names it. A deleted project is no longer listed by this phase, so the project delete writes a
/// mark for each of its entries (<see cref="MarkAnalysisCacheForProjectDeleteAsync"/>) and the purge removes them.
/// </para>
/// <para>
/// <b>What it deliberately does not clear.</b> <c>userFiles</c> is never <em>expired by retention</em>
/// while the project exists — it is what the user uploaded, and the plan's retention bounds generated
/// storage only. Only an object in it that no row names (an orphan) is swept, by the pass above.
/// <c>workflow_step_cache_entries</c> stays out: it is not mapped by <c>InferenceApiDbContext</c> at
/// all (no DbSet, no shadow entity), so this context cannot reach it without new mapping. An object
/// reachable ONLY through a cache entry is consequently invisible to this sweep — never marked,
/// never purged — and clearing that table is the engine-side slice's job.
/// </para>
/// <para>
/// <b>Also not swept:</b> <c>media_derivatives</c> (proxy/waveform/thumbnail index). It has no writer
/// yet, and deleting a <c>project_files</c> row cascades its derivative rows — so whoever adds that
/// writer must extend this sweep too, or the derivative objects are orphaned with nothing left that
/// names them. Object storage with no row at all (a project deleted before its prefix was swept) is
/// likewise out of reach: this sweep only sees keys a surviving row still points at.
/// </para>
/// <para>
/// <b>Layer cut-outs are swept, and the WRITER discharges it rather than the sweep.</b> A subject
/// cut-out and its matte live under <c>projects/{id}/agentFiles/</c> with no <c>project_files</c> row
/// to expire, so nothing here would ever mark them; <c>EditImageDocumentsController</c> therefore
/// writes a <c>storage_retention_marks</c> row for both objects at the moment a cut-out layer (or its
/// whole document) is deleted, and this sweep picks them up from there with the normal grace period.
/// That is the same obligation as the paragraph above, discharged at the other end: the party that
/// creates objects nothing else names is the party that has to name them when they go.
/// </para>
/// <para>
/// Disabled by default (<c>Billing:StorageRetention:Enabled</c>) — self-host has no per-plan
/// retention, and D19's "default retention 30 days" is a plan fact, not a self-host one.
/// </para>
/// </summary>
public sealed class StorageRetentionService : BackgroundService
{
    /// <summary>Days an object stays on disk after being marked, before the purge deletes it.</summary>
    public const int DefaultGraceDays = 7;

    /// <summary>Minutes a delete-written mark waits before the purge (a delete is explicit; no recovery window is owed).</summary>
    public const int DefaultDeleteGraceMinutes = 60;

    /// <summary>Hours an unmarked, unreferenced object must exist before the orphan pass marks it.</summary>
    public const int DefaultOrphanMinAgeHours = 24;

    /// <summary>Marks the orphan pass may write for one project in one run.</summary>
    public const int DefaultOrphanMaxKeysPerProject = 10_000;

    /// <summary>Objects per S3 multi-object delete (the hard API limit, mirrored by the storage seam).</summary>
    public const int BatchSize = 1000;

    /// <summary>Generated storage the retention job owns. <c>userFiles</c> is deliberately absent.</summary>
    private static readonly string[] GeneratedCategories = ["outputFiles", "agentFiles"];

    /// <summary>The render-event sidecars' prefix, scanned by the orphan pass beside <c>userFiles</c>.</summary>
    private const string RenderEventsCategory = "agentFiles/render-events";

    /// <summary>
    /// Avatars phase 3: the avatars' stills folders (<see cref="AvatarStillStorage"/>), scanned by the orphan pass. A still
    /// is never named by a row; it is live while its avatar exists, so the pass marks a still whose avatar is gone (a
    /// still uploaded while its avatar was being deleted, or one the delete could not list) and nothing else.
    /// </summary>
    private const string AvatarStillsCategory = "agentFiles/avatars";

    /// <summary>
    /// The video analysis folder, scanned by the orphan pass for one kind of object only: the three-frame strips a
    /// describing analysis keeps beside its document for <c>LookAtMoment</c> (<see cref="VideoEventStrips"/>). No row
    /// names a strip. It is live while a step result still names its analysis document and that document is not marked;
    /// once the document expires or its run is gone, the strip is marked too. Every other object of the folder is left
    /// to the rows that name it.
    /// </summary>
    private const string EventStripsCategory = VideoEventStrips.StorageCategory;

    /// <summary>
    /// The engine's per-source analysis pass cache (its <c>SourcePassCache.KeyPrefix</c>, without the trailing
    /// slash). The orphan pass deletes its entries by age alone.
    /// </summary>
    public const string AnalysisCacheCategory = "agentFiles/analysis-cache";

    /// <summary>
    /// True when <paramref name="storageKey"/> has the exact shape of an entry the engine's pass cache writes for
    /// <paramref name="projectId"/>: directly under the cache prefix, 64 lower-case hex characters, then
    /// <c>.bin</c>. Anything else under that folder is not a cache entry and is never deleted as one.
    /// </summary>
    public static bool IsAnalysisCacheEntry(Guid projectId, string? storageKey)
    {
        const int HashLength = 64;
        const string Extension = ".bin";
        if (storageKey is null)
            return false;

        string prefix = $"projects/{projectId:D}/{AnalysisCacheCategory}/";
        if (storageKey.Length != prefix.Length + HashLength + Extension.Length
            || !storageKey.StartsWith(prefix, StringComparison.Ordinal)
            || !storageKey.EndsWith(Extension, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char c in storageKey.AsSpan(prefix.Length, HashLength))
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                return false;
        }

        return true;
    }

    /// <summary>Days an analysis pass cache entry is kept after it was written.</summary>
    public const int DefaultAnalysisCacheMaxAgeDays = 30;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StorageRetentionService> _logger;

    public StorageRetentionService(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<StorageRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Billing:StorageRetention:Enabled", false))
            return;

        int hour = Math.Clamp(_configuration.GetValue("Billing:StorageRetention:HourUtc", 3), 0, 23);

        while (!stoppingToken.IsCancellationRequested)
        {
            DateTime now = DateTime.UtcNow;
            DateTime next = now.Date.AddHours(hour);
            if (next <= now) next = next.AddDays(1);
            try { await Task.Delay(next - now, stoppingToken); } catch (OperationCanceledException) { return; }

            try
            {
                StorageRetentionRunResult run = await RunOnceAsync(DateTime.UtcNow, stoppingToken);
                _logger.LogInformation(
                    "Storage retention marked {Marked} object(s), marked {Orphaned} orphan(s), purged {Purged} object(s) and cleared {Cleared} step-result reference(s); {Pending} marked object(s) await the purge",
                    run.MarkedObjects, run.OrphanedObjects, run.PurgedObjects, run.ClearedReferences, run.PendingObjects);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Storage retention run failed; will retry at the next scheduled hour");
            }
        }
    }

    /// <summary>
    /// One retention pass, exposed for tests: the cross-organization purge of marks old enough to
    /// delete, then the per-organization marking of what that organization's plan has expired, then the
    /// orphan pass that marks objects no row and no mark names.
    /// </summary>
    public async Task<StorageRetentionRunResult> RunOnceAsync(DateTime nowUtc, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        var entitlements = scope.ServiceProvider.GetRequiredService<IOrganizationEntitlements>();
        // Optional: a store that cannot list simply does not register it, and the orphan pass is skipped.
        IObjectListing? listing = scope.ServiceProvider.GetService<IObjectListing>();
        ITenantContext? tenant = scope.ServiceProvider.GetService<ITenantContext>();

        int budgetSeconds = _configuration.GetValue("Billing:StorageRetention:TimeBudgetSeconds", 1800);
        DateTime deadline = DateTime.UtcNow.AddSeconds(budgetSeconds);
        int graceDays = Math.Clamp(_configuration.GetValue("Billing:StorageRetention:GraceDays", DefaultGraceDays), 0, 365);
        int deleteGraceMinutes = Math.Clamp(
            _configuration.GetValue("Billing:StorageRetention:DeleteGraceMinutes", DefaultDeleteGraceMinutes),
            0, graceDays * 24 * 60);

        int purged = 0;
        int cleared = 0;

        // --- Phase 2: purge every mark old enough to delete. ---
        // Spans every organization: without a system scope the tenant filters hide all rows.
        using (tenant.System())
        {
            DateTime purgeBefore = nowUtc.AddDays(-graceDays);
            // A delete-written mark (any category the sweep never discovers on its own, so a generated
            // category can only be a delete mark when the file itself was generated) is due after the
            // short delete grace; a mark this sweep wrote keeps the full retention grace.
            DateTime deletePurgeBefore = nowUtc.AddMinutes(-deleteGraceMinutes);
            var dueByProject = (await db.StorageRetentionMarks
                    .Where(m => m.PurgedAt == null
                                && !m.StorageKey.Contains("/quarantine/")
                                && (m.MarkedAt <= purgeBefore
                                    || (m.MarkedAt <= deletePurgeBefore && !GeneratedCategories.Contains(m.Category))))
                    .OrderBy(m => m.MarkedAt)
                    .ToListAsync(ct))
                .GroupBy(m => m.ProjectId)
                .ToList();

            // A project whose outputs are kept (or whose state cannot be read) is left alone: its due marks stay.
            IReadOnlyDictionary<Guid, ProjectOutputHoldState> dueStates =
                await ProjectOutputHold.ReadManyAsync(db, dueByProject.Select(g => g.Key).ToList(), ct);

            foreach (var project in dueByProject)
            {
                if (dueStates.TryGetValue(project.Key, out ProjectOutputHoldState dueState) && dueState.IsActive)
                    continue;

                if (DateTime.UtcNow >= deadline)
                {
                    _logger.LogWarning("Storage retention hit its time budget while purging; the rest is purged on the next run");
                    break;
                }

                try
                {
                    // One project's failure must not stop the sweep: its marks survive for the next run.
                    (int projectPurged, int projectCleared) = await PurgeProjectAsync(db, storage, project.Key, project.ToList(), nowUtc, ct);
                    purged += projectPurged;
                    cleared += projectCleared;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Purging the marked objects of project {ProjectId} failed; its marks are kept", project.Key);
                }
            }
        }

        // --- Phase 1: mark what each organization's plan has expired. ---
        List<ProjectOrg> projects;
        using (tenant.System())
        {
            projects = await db.Projects.AsNoTracking()
                .Select(p => new ProjectOrg(p.Id, p.OrganizationId))
                .ToListAsync(ct);
        }

        IReadOnlyDictionary<Guid, ProjectOutputHoldState> markStates =
            await ProjectOutputHold.ReadManyAsync(db, projects.Select(p => p.Id).ToList(), ct);
        projects = projects.Where(p => !(markStates.TryGetValue(p.Id, out ProjectOutputHoldState st) && st.IsActive)).ToList();

        int marked = 0;
        foreach (var org in projects.GroupBy(p => p.OrganizationId))
        {
            if (DateTime.UtcNow >= deadline)
            {
                _logger.LogWarning("Storage retention hit its time budget while marking; remaining organizations are swept on the next run");
                break;
            }

            OrganizationEntitlementSet plan = await entitlements.GetAsync(org.Key, ct);
            if (plan.RetentionDays >= OrganizationEntitlementSet.UnlimitedInt)
                continue;   // "keep forever" (Enterprise): nothing in this organization ever expires.
            if (plan.RetentionDays < 1)
            {
                // 0 is "delete immediately" rather than "not entitled"; deleting today's storage on
                // the strength of a mis-set plan is not a recoverable mistake, so the org is skipped.
                _logger.LogWarning(
                    "Organization {OrganizationId} has retention days {Days}; retention is skipped for it",
                    org.Key, plan.RetentionDays);
                continue;
            }

            DateTime cutoff = nowUtc.AddDays(-plan.RetentionDays);

            // The organization's own scope: every read and write below is bounded to it.
            using IDisposable orgScope = tenant.Organization(org.Key);
            foreach (ProjectOrg project in org)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    _logger.LogWarning("Storage retention hit its time budget while marking; remaining projects are swept on the next run");
                    break;
                }

                try
                {
                    marked += await MarkProjectAsync(db, project.Id, org.Key, cutoff, nowUtc, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Marking the expired storage of project {ProjectId} failed; it is retried on the next run", project.Id);
                }
            }
        }

        // --- Phase 3: mark the orphans — objects no row and no mark names. ---
        // Independent of any plan's retention: an object nothing names must still be removed, whatever
        // the organization's retention days are. Fail closed: a store that cannot list, or a listing
        // failure, skips the pass for that project and never deletes a partial result.
        int orphaned = 0;
        bool orphanSweep = listing is not null
            && _configuration.GetValue("Billing:StorageRetention:OrphanSweep:Enabled", true);
        if (orphanSweep)
        {
            TimeSpan minAge = TimeSpan.FromHours(Math.Max(1,
                _configuration.GetValue("Billing:StorageRetention:OrphanSweep:MinAgeHours", DefaultOrphanMinAgeHours)));
            int maxKeys = Math.Max(1,
                _configuration.GetValue("Billing:StorageRetention:OrphanSweep:MaxKeysPerProject", DefaultOrphanMaxKeysPerProject));
            TimeSpan analysisCacheMaxAge = TimeSpan.FromDays(Math.Clamp(
                _configuration.GetValue("Billing:StorageRetention:OrphanSweep:AnalysisCacheMaxAgeDays", DefaultAnalysisCacheMaxAgeDays),
                1, 3650));

            // Spans every organization: without a system scope the tenant filters hide the rows.
            using (tenant.System())
            {
                List<ProjectOrg> sweepProjects = await db.Projects.AsNoTracking()
                    .Select(p => new ProjectOrg(p.Id, p.OrganizationId))
                    .ToListAsync(ct);

                IReadOnlyDictionary<Guid, ProjectOutputHoldState> sweepStates =
                    await ProjectOutputHold.ReadManyAsync(db, sweepProjects.Select(p => p.Id).ToList(), ct);

                foreach (ProjectOrg project in sweepProjects)
                {
                    if (sweepStates.TryGetValue(project.Id, out ProjectOutputHoldState sweepState) && sweepState.IsActive)
                        continue;

                    if (DateTime.UtcNow >= deadline)
                    {
                        _logger.LogWarning("Storage retention hit its time budget while sweeping orphans; the rest is swept on the next run");
                        break;
                    }

                    try
                    {
                        orphaned += await MarkOrphansAsync(listing!, db, project.Id, project.OrganizationId, nowUtc, minAge, maxKeys, "userFiles", ct);
                        // SQCR-S-17: the render-event sidecar a saved copy brings lives under
                        // agentFiles/render-events/, so the userFiles scan cannot see it.
                        orphaned += await MarkOrphansAsync(listing!, db, project.Id, project.OrganizationId, nowUtc, minAge, maxKeys, RenderEventsCategory, ct);
                        // Avatars phase 3: a still outlives nothing but its avatar.
                        orphaned += await MarkOrphansAsync(listing!, db, project.Id, project.OrganizationId, nowUtc, minAge, maxKeys, AvatarStillsCategory, ct);
                        // The strips of moments a describing analysis kept: frames of the customer's video that
                        // no row names, so they must go when their analysis document does.
                        orphaned += await MarkOrphansAsync(listing!, db, project.Id, project.OrganizationId, nowUtc, minAge, maxKeys, EventStripsCategory, ct);
                        // The analysis pass cache: no row ever names an entry, so age is the only rule.
                        purged += await PurgeOldAnalysisCacheAsync(listing!, db, storage, project.Id, nowUtc, analysisCacheMaxAge, maxKeys, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Sweeping the orphaned objects of project {ProjectId} failed; it is retried on the next run", project.Id);
                    }
                }
            }
        }

        // The workspace avatar library keeps its objects under an ORGANIZATION prefix, outside every project prefix, so
        // the per-project pass above never sees them. Its own orphan pass removes a library object nothing names any
        // more (an item delete that could not remove it, a copy a failed publish left behind), by the same age rule.
        if (orphanSweep && DateTime.UtcNow < deadline)
        {
            try
            {
                var avatarLibrary = scope.ServiceProvider.GetService<ReelBolt.Inference.Api.Services.Avatars.Library.AvatarLibraryService>();
                if (avatarLibrary is not null)
                {
                    TimeSpan minAge = TimeSpan.FromHours(Math.Max(1,
                        _configuration.GetValue("Billing:StorageRetention:OrphanSweep:MinAgeHours", DefaultOrphanMinAgeHours)));
                    using (tenant.System())
                    {
                        purged += await avatarLibrary.SweepOrphanObjectsAsync(nowUtc, minAge, DefaultOrphanMaxKeysPerProject, ct);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Sweeping the orphaned objects of the workspace avatar libraries failed; it is retried on the next run");
            }
        }

        // Visibility: how many marked objects still await the purge (QA-SQC-6's "bytes sit on disk
        // uncounted" is at least no longer silent).
        int pending;
        using (tenant.System())
        {
            pending = await db.StorageRetentionMarks.CountAsync(m => m.PurgedAt == null, ct);
        }

        return new StorageRetentionRunResult(marked, purged, cleared, orphaned, pending);
    }

    /// <summary>
    /// Deletes one project's analysis pass cache entries (<see cref="AnalysisCacheCategory"/>) last written more
    /// than <paramref name="maxAge"/> ago, oldest first, at most <paramref name="maxKeys"/> a run. No mark is
    /// written: an entry is a cache, and the engine makes a missing one again. Only a key with the shape of an
    /// entry that no <c>project_files</c> row names is deleted; any other object under the folder is not this
    /// pass's to remove. A failure propagates to the caller, which logs and moves on; deletes are idempotent, so
    /// the next run finishes the job.
    /// </summary>
    private static async Task<int> PurgeOldAnalysisCacheAsync(
        IObjectListing listing,
        InferenceApiDbContext db,
        IFileStorageService storage,
        Guid projectId,
        DateTime nowUtc,
        TimeSpan maxAge,
        int maxKeys,
        CancellationToken ct)
    {
        IReadOnlyList<StoredObjectEntry> listed = await listing.ListAsync(projectId, AnalysisCacheCategory, ct);
        DateTime olderThan = nowUtc - maxAge;
        List<string> keys = [.. listed
            .Where(e => IsAnalysisCacheEntry(projectId, e.StorageKey))
            .Where(e => e.LastModifiedUtc <= olderThan)
            .OrderBy(e => e.LastModifiedUtc)
            .Take(maxKeys)
            .Select(e => e.StorageKey)];
        if (keys.Count == 0) return 0;

        // A project file can be given a path that puts it under this folder with an entry's name. A row wins.
        HashSet<string> named = (await db.ProjectFiles.IgnoreQueryFilters()
                .Where(f => f.ProjectId == projectId && keys.Contains(f.StorageKey))
                .Select(f => f.StorageKey)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        keys.RemoveAll(named.Contains);

        int deleted = 0;
        for (int start = 0; start < keys.Count; start += BatchSize)
            deleted += await storage.DeleteManyAsync(projectId, keys.GetRange(start, Math.Min(BatchSize, keys.Count - start)), ct);
        return deleted;
    }

    /// <summary>
    /// Marks one project's objects under <paramref name="category"/> that no <c>project_files</c> row and
    /// no existing mark names (and, for <see cref="RenderEventsCategory"/>, whose mirrored render is gone),
    /// older than <paramref name="minAge"/>, so the normal purge removes them. Reads only from the listing;
    /// a failure propagates to the caller, which logs and moves on (never a partial delete). Bounded by
    /// <paramref name="maxKeys"/> and idempotent by the unique storage key.
    /// </summary>
    private async Task<int> MarkOrphansAsync(
        IObjectListing listing,
        InferenceApiDbContext db,
        Guid projectId,
        Guid organizationId,
        DateTime nowUtc,
        TimeSpan minAge,
        int maxKeys,
        string category,
        CancellationToken ct)
    {
        IReadOnlyList<StoredObjectEntry> listed = await listing.ListAsync(projectId, category, ct);
        // In the analysis folder this pass owns the strips only: nothing else there is ever a candidate.
        if (category == EventStripsCategory)
            listed = [.. listed.Where(e => VideoEventStrips.ArtifactKeyOf(e.StorageKey) is not null)];
        DateTime olderThan = nowUtc - minAge;
        List<string> keys = [.. listed
            // Quarantined bytes are evidence under legal hold: the orphan pass never marks them,
            // even when a failed move or a deleted row made them look unreferenced.
            .Where(e => !e.StorageKey.Contains("/quarantine/", StringComparison.Ordinal))
            .Where(e => e.LastModifiedUtc <= olderThan)
            .OrderBy(e => e.LastModifiedUtc)
            .Take(maxKeys)
            .Select(e => e.StorageKey)];
        if (keys.Count == 0) return 0;

        HashSet<string> referenced = (await db.ProjectFiles.IgnoreQueryFilters()
                .Where(f => f.ProjectId == projectId && keys.Contains(f.StorageKey))
                .Select(f => f.StorageKey)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        if (category == RenderEventsCategory)
            referenced.UnionWith(await ReferencedSidecarsAsync(db, projectId, keys, ct));
        if (category == AvatarStillsCategory)
            referenced.UnionWith(await LiveStillsAsync(db, projectId, keys, ct));
        if (category == EventStripsCategory)
            referenced.UnionWith(await LiveEventStripsAsync(db, projectId, keys, ct));

        HashSet<string> marked = (await db.StorageRetentionMarks.IgnoreQueryFilters()
                .Where(m => m.ProjectId == projectId && keys.Contains(m.StorageKey))
                .Select(m => m.StorageKey)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        List<StorageRetentionMark> fresh = [.. keys
            .Where(k => !referenced.Contains(k) && !marked.Contains(k))
            .Select(k => new StorageRetentionMark
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                ProjectId = projectId,
                StorageKey = k,
                Category = category is RenderEventsCategory or AvatarStillsCategory or EventStripsCategory ? "agentFiles" : "userFiles",
                MarkedAt = nowUtc
            })];
        if (fresh.Count == 0) return 0;

        db.StorageRetentionMarks.AddRange(fresh);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race with a concurrent sweep (the storage key is unique): retried on the next run.
            db.ChangeTracker.Clear();
            _logger.LogWarning("Lost a race marking orphaned objects of project {ProjectId}; it is retried on the next run", projectId);
            return 0;
        }

        return fresh.Count;
    }

    /// <summary>
    /// The render-event sidecars whose mirrored render still exists, so the orphan pass never marks a live
    /// render's events. A sidecar is never named by a <c>project_files</c> row: it mirrors the render's key,
    /// so it is referenced when a row or a step-result key names
    /// <c>projects/{p}/(outputFiles|userFiles)/{rest}</c> for the sidecar's <c>{rest}</c>.
    /// </summary>
    private static async Task<List<string>> ReferencedSidecarsAsync(
        InferenceApiDbContext db, Guid projectId, IReadOnlyList<string> sidecarKeys, CancellationToken ct)
    {
        string prefix = $"projects/{projectId}/agentFiles/render-events/";
        Dictionary<string, string> mirrorOf = [];
        foreach (string key in sidecarKeys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal) || !key.EndsWith(".events.json", StringComparison.Ordinal))
                continue;

            string rest = key[prefix.Length..^".events.json".Length];
            if (rest.Length > 0) mirrorOf[key] = rest;
        }

        if (mirrorOf.Count == 0) return [];

        List<string> mirrors = [.. mirrorOf.Values
            .Distinct(StringComparer.Ordinal)
            .SelectMany(rest => new[]
            {
                $"projects/{projectId}/outputFiles/{rest}",
                $"projects/{projectId}/userFiles/{rest}"
            })];
        HashSet<string> live = (await db.ProjectFiles.IgnoreQueryFilters()
                .Where(f => f.ProjectId == projectId && mirrors.Contains(f.StorageKey))
                .Select(f => f.StorageKey)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        // The engine-owned step results name the renders themselves (their sidecar is implied).
        List<StepKeys> keys = (await db.WorkflowStepResults
                .Where(r => r.WorkflowExecution.ProjectId == projectId
                            && (r.OutputStorageKey != null || r.ArtifactStorageKey != null || r.TimelineStorageKey != null))
                .Select(r => new { r.OutputStorageKey, r.ArtifactStorageKey, r.TimelineStorageKey })
                .ToListAsync(ct))
            .Select(r => new StepKeys(r.OutputStorageKey, r.ArtifactStorageKey, r.TimelineStorageKey))
            .ToList();
        foreach (StepKeys row in keys)
        {
            foreach (string? key in new[] { row.Output, row.Artifact, row.Timeline })
            {
                if (key is not null) live.Add(key);
            }
        }

        return [.. mirrorOf
            .Where(m => live.Contains($"projects/{projectId}/outputFiles/{m.Value}")
                        || live.Contains($"projects/{projectId}/userFiles/{m.Value}"))
            .Select(m => m.Key)];
    }

    private sealed record StepKeys(string? Output, string? Artifact, string? Timeline);

    /// <summary>
    /// The keys under the avatars folder that must NOT be marked: every still whose avatar still exists, and every key
    /// that is not a still at all (a shape this pass does not own is never swept).
    /// </summary>
    private static async Task<List<string>> LiveStillsAsync(
        InferenceApiDbContext db, Guid projectId, IReadOnlyList<string> keys, CancellationToken ct)
    {
        Dictionary<string, Guid?> avatarOf = keys.ToDictionary(k => k, k => AvatarStillStorage.AvatarOf(projectId, k), StringComparer.Ordinal);
        List<Guid> ids = [.. avatarOf.Values.Where(v => v is not null).Select(v => v!.Value).Distinct()];
        HashSet<Guid> live = ids.Count == 0
            ? []
            : [.. await db.ProjectAvatars.IgnoreQueryFilters()
                .Where(a => a.ProjectId == projectId && ids.Contains(a.Id))
                .Select(a => a.Id)
                .ToListAsync(ct)];
        return [.. avatarOf.Where(e => e.Value is not { } id || live.Contains(id)).Select(e => e.Key)];
    }

    /// <summary>
    /// The event strips that must NOT be marked: those whose analysis document a step result of the project still names
    /// and that no retention mark names (a marked document has expired, purged or not). A key that is not a strip is
    /// kept too: a shape this pass does not own is never swept.
    /// </summary>
    private static async Task<List<string>> LiveEventStripsAsync(
        InferenceApiDbContext db, Guid projectId, IReadOnlyList<string> keys, CancellationToken ct)
    {
        Dictionary<string, string?> artifactOf = keys.ToDictionary(k => k, VideoEventStrips.ArtifactKeyOf, StringComparer.Ordinal);
        List<string> artifacts = [.. artifactOf.Values.Where(v => v is not null).Select(v => v!).Distinct(StringComparer.Ordinal)];
        HashSet<string> live = [];
        if (artifacts.Count > 0)
        {
            live = (await db.WorkflowStepResults
                    .Where(r => r.WorkflowExecution.ProjectId == projectId
                                && r.ArtifactStorageKey != null && artifacts.Contains(r.ArtifactStorageKey))
                    .Select(r => r.ArtifactStorageKey!)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            live.ExceptWith(await db.StorageRetentionMarks.IgnoreQueryFilters()
                .Where(m => m.ProjectId == projectId && artifacts.Contains(m.StorageKey))
                .Select(m => m.StorageKey)
                .ToListAsync(ct));
        }

        return [.. artifactOf.Where(e => e.Value is not { } artifact || live.Contains(artifact)).Select(e => e.Key)];
    }

    /// <summary>
    /// A deleted project's event strips get a durable mark each, in the caller's save, so the purge removes them: the
    /// orphan pass only reaches projects that still exist, and the step results that named their analysis documents go
    /// with the project. With <paramref name="listing"/> null, or a listing that fails, nothing is marked: a project
    /// delete is never refused for it.
    /// </summary>
    public static Task MarkEventStripsForProjectDeleteAsync(
        InferenceApiDbContext db, Project project, IObjectListing? listing, CancellationToken ct) =>
        MarkForProjectDeleteAsync(db, project, listing, EventStripsCategory, k => VideoEventStrips.ArtifactKeyOf(k) is not null, ct);

    /// <summary>
    /// A deleted project's analysis pass cache entries get a durable mark each, in the caller's save, so the purge
    /// removes them: the age pass only reaches projects that still exist, and a frame grid entry holds small
    /// pictures of the project's videos. Only keys with the shape of an entry are marked. With
    /// <paramref name="listing"/> null, or a listing that fails, nothing is marked: a project delete is never
    /// refused for it.
    /// </summary>
    public static Task MarkAnalysisCacheForProjectDeleteAsync(
        InferenceApiDbContext db, Project project, IObjectListing? listing, CancellationToken ct) =>
        MarkForProjectDeleteAsync(db, project, listing, AnalysisCacheCategory, k => IsAnalysisCacheEntry(project.Id, k), ct);

    private static async Task MarkForProjectDeleteAsync(
        InferenceApiDbContext db, Project project, IObjectListing? listing, string category, Func<string, bool> owned, CancellationToken ct)
    {
        if (listing is null) return;

        List<string> keys;
        try
        {
            keys = [.. (await listing.ListAsync(project.Id, category, ct))
                .Select(e => e.StorageKey)
                .Where(k => owned(k) && !k.Contains("/quarantine/", StringComparison.Ordinal))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }

        if (keys.Count == 0) return;

        HashSet<string> already = [.. await db.StorageRetentionMarks.IgnoreQueryFilters()
            .Where(m => keys.Contains(m.StorageKey)).Select(m => m.StorageKey).ToListAsync(ct)];
        DateTime now = DateTime.UtcNow;
        db.StorageRetentionMarks.AddRange(keys.Where(k => !already.Contains(k)).Select(k => new StorageRetentionMark
        {
            Id = Guid.NewGuid(),
            OrganizationId = project.OrganizationId,
            ProjectId = project.Id,
            StorageKey = k,
            Category = "agentFiles",
            MarkedAt = now
        }));
    }

    /// <summary>
    /// Marks one project's expired generated storage, in batches of <see cref="BatchSize"/>, and drops
    /// the <c>project_files</c> rows that referenced those objects (the only reference the Inference
    /// API owns). Returns the number of objects newly marked.
    /// </summary>
    private async Task<int> MarkProjectAsync(
        InferenceApiDbContext db, Guid projectId, Guid organizationId, DateTime cutoff, DateTime nowUtc, CancellationToken ct)
    {
        // Expired project files in a generated category. A positive allowlist, never
        // "not userFiles": an unrecognised category must fail closed and keep the file.
        List<ProjectFile> expiredFiles = await db.ProjectFiles
            .Where(f => f.ProjectId == projectId && GeneratedCategories.Contains(f.Category) && f.UploadedAt < cutoff
                        // Quarantine evidence is never expired. CsamQuarantineKey also covers a failed
                        // move, whose key is still a normal path rather than /quarantine/.
                        && f.CsamStatus != CsamScanStatus.Quarantined
                        && f.CsamQuarantineKey == null)
            .ToListAsync(ct);

        // An avatar's reference is not expired by the plan's retention: the avatar has its own retention date and
        // its own sweep, and the reference link is ON DELETE NO ACTION. Neither the row nor its object (which a step
        // result may also name) is marked while an avatar lists the file.
        HashSet<Guid> avatarFileIds = await ReelBolt.Shared.Avatars.AvatarFileUse.ReferencedFileIdsAsync(
            db, [.. expiredFiles.Select(f => f.Id)], ct);
        HashSet<string> avatarKeys = [.. (await db.ProjectFiles
                .Where(f => f.ProjectId == projectId && db.ProjectAvatarReferences.Any(r => r.ProjectFileId == f.Id))
                .Select(f => f.StorageKey)
                .ToListAsync(ct))];
        if (avatarFileIds.Count > 0)
        {
            _logger.LogInformation(
                "Storage retention kept {Count} expired file(s) of project {ProjectId} that an avatar lists as a reference",
                avatarFileIds.Count, projectId);
            expiredFiles = [.. expiredFiles.Where(f => !avatarFileIds.Contains(f.Id))];
        }

        var candidates = new Dictionary<string, string>(StringComparer.Ordinal);   // key -> category
        foreach (ProjectFile file in expiredFiles)
        {
            if (!file.StorageKey.Contains("/quarantine/", StringComparison.Ordinal)
                && CategoryOf(file.StorageKey, projectId) is { } category && category == file.Category)
                candidates.TryAdd(file.StorageKey, category);
            else
                _logger.LogWarning(
                    "Project file {FileId} of project {ProjectId} has category {Category} but key {StorageKey} outside its own prefix; skipped",
                    file.Id, projectId, file.Category, file.StorageKey);
        }

        // Expired step artifacts. Their keys are the only handle on the bare agentFiles artifacts
        // (a VideoAnalyze analysis document, a compile timeline) — those have no project_files row.
        var stepKeys = await db.WorkflowStepResults
            .Where(r => r.WorkflowExecution.ProjectId == projectId
                        && (r.CompletedAt ?? r.ExecutedAt) < cutoff
                        && (r.OutputStorageKey != null || r.ArtifactStorageKey != null || r.TimelineStorageKey != null))
            .Select(r => new { r.OutputStorageKey, r.ArtifactStorageKey, r.TimelineStorageKey })
            .ToListAsync(ct);

        foreach (var row in stepKeys)
        {
            foreach (string? key in new[] { row.OutputStorageKey, row.ArtifactStorageKey, row.TimelineStorageKey })
            {
                if (key is not null && !key.Contains("/quarantine/", StringComparison.Ordinal)
                    && !avatarKeys.Contains(key)
                    && CategoryOf(key, projectId) is { } category)
                    candidates.TryAdd(key, category);
            }
        }

        List<StorageRetentionMark> marks = await db.StorageRetentionMarks
            .Where(m => m.ProjectId == projectId)
            .ToListAsync(ct);
        HashSet<string> markedKeys = marks.Select(m => m.StorageKey).ToHashSet(StringComparer.Ordinal);

        // Every key the engine-owned step-result table still points at. A purged mark for such a key
        // must survive: it is what stops the object being marked and deleted again on every run, and
        // it is the worklist the engine-side slice reads. A purged mark nothing references any more
        // has done its job and is dropped here.
        HashSet<string> referencedKeys = (await db.WorkflowStepResults
                .Where(r => r.WorkflowExecution.ProjectId == projectId)
                .Select(r => new { r.OutputStorageKey, r.ArtifactStorageKey, r.TimelineStorageKey })
                .ToListAsync(ct))
            .SelectMany(r => new[] { r.OutputStorageKey, r.ArtifactStorageKey, r.TimelineStorageKey })
            .Where(k => k is not null)
            .Select(k => k!)
            .ToHashSet(StringComparer.Ordinal);

        List<StorageRetentionMark> dormant = marks
            .Where(m => m.PurgedAt != null && !referencedKeys.Contains(m.StorageKey)
                        && !m.StorageKey.Contains("/quarantine/", StringComparison.Ordinal))
            .ToList();
        if (dormant.Count > 0)
            db.StorageRetentionMarks.RemoveRange(dormant);

        List<KeyValuePair<string, string>> toMark = candidates
            .Where(c => !markedKeys.Contains(c.Key)
                        && !c.Key.Contains("/quarantine/", StringComparison.Ordinal))
            .ToList();
        if (toMark.Count == 0)
        {
            if (dormant.Count > 0)
                await db.SaveChangesAsync(ct);
            return 0;
        }

        // The soft half of the delete: the rows go now, the objects stay on disk for the grace period.
        // Only the rows whose object is actually accounted for — dropping the row of a file this pass
        // refused to mark would strand its object with nothing left that names it.
        db.ProjectFiles.RemoveRange(expiredFiles.Where(f => candidates.ContainsKey(f.StorageKey)));

        int newlyMarked = 0;
        for (int start = 0; start < toMark.Count; start += BatchSize)
        {
            List<KeyValuePair<string, string>> batch = toMark.GetRange(start, Math.Min(BatchSize, toMark.Count - start));
            foreach (KeyValuePair<string, string> candidate in batch)
            {
                db.StorageRetentionMarks.Add(new StorageRetentionMark
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organizationId,
                    ProjectId = projectId,
                    StorageKey = candidate.Key,
                    Category = candidate.Value,
                    MarkedAt = nowUtc
                });
            }

            try
            {
                await db.SaveChangesAsync(ct);
                newlyMarked += batch.Count;
            }
            catch (DbUpdateException)
            {
                // Lost a race with a concurrent sweep (the storage key is unique): the other run
                // marked these, so this project is left to it and retried on the next run.
                db.ChangeTracker.Clear();
                _logger.LogWarning("Lost a race marking project {ProjectId}; it is retried on the next run", projectId);
                return newlyMarked;
            }
        }

        return newlyMarked;
    }

    /// <summary>
    /// Deletes one project's due marks in batches of <see cref="BatchSize"/>, clears the
    /// <c>workflow_step_results</c> keys that named those objects, and stamps every object the object
    /// store confirmed gone. A batch that deletes fewer objects than it asked for changes nothing, so
    /// a partial S3 failure only defers the purge. Returns (purged, cleared references).
    /// </summary>
    private static async Task<(int Purged, int Cleared)> PurgeProjectAsync(
        InferenceApiDbContext db,
        IFileStorageService storage,
        Guid projectId,
        List<StorageRetentionMark> marks,
        DateTime purgedAt,
        CancellationToken ct)
    {
        int purged = 0;
        int cleared = 0;
        for (int start = 0; start < marks.Count; start += BatchSize)
        {
            List<StorageRetentionMark> batch = marks.GetRange(start, Math.Min(BatchSize, marks.Count - start));
            // Belt and braces: a quarantine object is evidence and is never deleted by the sweep,
            // even if a legacy mark or a failed move put it on the worklist.
            List<StorageRetentionMark> safe = batch
                .Where(m => !m.StorageKey.Contains("/quarantine/", StringComparison.Ordinal))
                .ToList();
            if (safe.Count == 0) continue;
            string[] keys = safe.Select(m => m.StorageKey).ToArray();

            int deleted = await storage.DeleteManyAsync(projectId, keys, ct);
            if (deleted < keys.Length)
                continue;   // Partial failure: nothing is stamped and the next run retries (S3 deletes are idempotent).

            cleared += await ClearStepResultReferencesAsync(db, projectId, keys, ct);
            // The stored moderation verdict goes with the object it judged.
            db.OutputModerationVerdicts.RemoveRange(await db.OutputModerationVerdicts
                .Where(v => v.ProjectId == projectId && keys.Contains(v.StorageKey))
                .ToListAsync(ct));
            foreach (StorageRetentionMark mark in safe)
                mark.PurgedAt = purgedAt;
            await db.SaveChangesAsync(ct);
            purged += deleted;
        }

        return (purged, cleared);
    }

    /// <summary>
    /// Nulls the <c>workflow_step_results</c> keys naming the objects that were just deleted. A
    /// reference must never outlive the object it names, or the row keeps being listed and then fails
    /// on download — the same move <c>OutputsController.DeleteTakesAsync</c> makes when it deletes a
    /// take, through this context's own <c>WorkflowStepResults</c> mapping
    /// (<c>ExcludeFromMigrations</c> keeps that table's schema out of the API's migrations; it does not
    /// make it read-only). Bounded by the purge batch — at most <see cref="BatchSize"/> keys, never a
    /// whole-project row set — and idempotent: a row already cleared simply does not match.
    /// Returns the number of keys cleared.
    /// </summary>
    private static async Task<int> ClearStepResultReferencesAsync(
        InferenceApiDbContext db, Guid projectId, string[] keys, CancellationToken ct)
    {
        List<WorkflowStepResult> referencing = await db.WorkflowStepResults
            .Where(r => r.WorkflowExecution.ProjectId == projectId
                        && ((r.OutputStorageKey != null && keys.Contains(r.OutputStorageKey))
                            || (r.ArtifactStorageKey != null && keys.Contains(r.ArtifactStorageKey))
                            || (r.TimelineStorageKey != null && keys.Contains(r.TimelineStorageKey))))
            .ToListAsync(ct);

        int cleared = 0;
        foreach (WorkflowStepResult result in referencing)
        {
            if (result.OutputStorageKey is not null && keys.Contains(result.OutputStorageKey, StringComparer.Ordinal))
            {
                result.OutputStorageKey = null;
                cleared++;
            }

            if (result.ArtifactStorageKey is not null && keys.Contains(result.ArtifactStorageKey, StringComparer.Ordinal))
            {
                result.ArtifactStorageKey = null;
                cleared++;
            }

            if (result.TimelineStorageKey is not null && keys.Contains(result.TimelineStorageKey, StringComparer.Ordinal))
            {
                result.TimelineStorageKey = null;
                cleared++;
            }
        }

        return cleared;
    }

    /// <summary>The generated category a key belongs to, or <c>null</c> when it is not a retention target.</summary>
    private static string? CategoryOf(string storageKey, Guid projectId)
    {
        foreach (string category in GeneratedCategories)
        {
            if (storageKey.StartsWith($"projects/{projectId}/{category}/", StringComparison.Ordinal))
                return category;
        }

        return null;
    }

    private readonly record struct ProjectOrg(Guid Id, Guid OrganizationId);
}

/// <summary>What one retention pass did.</summary>
/// <param name="MarkedObjects">Objects newly soft-marked (they stay on disk for the grace period).</param>
/// <param name="PurgedObjects">Objects actually deleted from the object store.</param>
/// <param name="ClearedReferences">
/// <c>workflow_step_results</c> keys nulled because the object they named was deleted
/// (<c>OutputStorageKey</c>/<c>ArtifactStorageKey</c>/<c>TimelineStorageKey</c>). Expected to cover
/// every purged object a step result named; a shortfall is the signal that a reference was left
/// dangling. References this context cannot reach at all (<c>workflow_step_cache_entries</c>, not
/// mapped) are deliberately not counted, because it cannot see them.
/// </param>
public sealed record StorageRetentionRunResult(
    int MarkedObjects,
    int PurgedObjects,
    int ClearedReferences,
    int OrphanedObjects = 0,
    int PendingObjects = 0);
