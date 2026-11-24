using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.History;

/// <summary>
/// Restore, part 1 (contract section 8): preview and apply for the item and project scopes. The
/// guards run in the contract's order — the version must exist, then the run guards, then the
/// per-project session lock, then the never-deduplicated <c>beforeRestore</c> safety version, then the
/// per-kind handlers. Kinds without a handler are skipped <c>NOT_YET_SUPPORTED</c>.
/// </summary>
public sealed class ProjectHistoryRestoreService : IProjectHistoryRestoreService
{
    private readonly InferenceApiDbContext _db;
    private readonly IProjectHistoryService _history;
    private readonly IProjectRestoreLock _restoreLock;
    private readonly IReadOnlyList<IProjectRestoreKindHandler> _handlers;
    private readonly IReadOnlyDictionary<ProjectVersionItemKind, IProjectRestoreKindHandler> _handlersByKind;
    private readonly ILogger<ProjectHistoryRestoreService> _logger;
    private readonly TimeProvider _clock;

    public ProjectHistoryRestoreService(
        InferenceApiDbContext db,
        IProjectHistoryService history,
        IProjectRestoreLock restoreLock,
        IEnumerable<IProjectRestoreKindHandler> handlers,
        ILogger<ProjectHistoryRestoreService> logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _history = history;
        _restoreLock = restoreLock;
        _handlers = handlers.OrderBy(h => h.Kind).ToList();
        _handlersByKind = _handlers.ToDictionary(h => h.Kind);
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <inheritdoc />
    public async Task<ProjectHistoryRestorePreview?> PreviewAsync(
        Guid projectId,
        Guid versionId,
        ProjectHistoryRestoreScope scope,
        IReadOnlyList<ProjectHistoryRestoreItemRef> items,
        CancellationToken ct)
    {
        ProjectVersion? version = await LoadVersionAsync(projectId, versionId, ct);
        if (version is null)
            return null;

        ProjectHistoryVersionDetail? detail = await _history.GetVersionAsync(projectId, versionId, ct);
        ProjectHistoryChanges? changes = await _history.ComputeChangesAsync(projectId, versionId, toVersionId: null, ct);
        if (detail is null || changes is null)
            return null;

        HashSet<(ProjectVersionItemKind Kind, string Key)> wanted = WantedKeys(scope, items);
        List<ProjectHistoryItemInfo> relevant = detail.Items
            .Where(i => wanted.Count == 0 || wanted.Contains((i.Kind, i.Key)))
            .ToList();

        List<ProjectHistoryRestoreBlockedItem> blocked = new();
        foreach (ProjectHistoryItemInfo item in relevant)
        {
            string? reason = BlockedReason(item);
            if (reason is not null)
                blocked.Add(new ProjectHistoryRestoreBlockedItem(item.Kind, item.Key, item.Name, reason));
        }

        if (scope == ProjectHistoryRestoreScope.Item)
        {
            HashSet<(ProjectVersionItemKind Kind, string Key)> known =
                new(relevant.Select(i => (i.Kind, i.Key)));
            foreach (ProjectHistoryRestoreItemRef request in Distinct(items))
            {
                if (!known.Contains((request.Kind, request.Key)))
                {
                    blocked.Add(new ProjectHistoryRestoreBlockedItem(
                        request.Kind, request.Key, null, ProjectHistoryRestoreReasons.ItemNotFound));
                }
            }
        }

        HashSet<(ProjectVersionItemKind Kind, string Key)> changed =
            new(changes.Changes.Select(c => (c.Kind, c.Key)));
        long estimateBytes = relevant
            .Where(i => i.Kind == ProjectVersionItemKind.File
                        && i.Restorable
                        && changed.Contains((i.Kind, i.Key)))
            .Sum(i => i.SizeBytes);

        IReadOnlyList<ProjectHistoryItemChange> relevantChanges = wanted.Count == 0
            ? changes.Changes
            : changes.Changes.Where(c => wanted.Contains((c.Kind, c.Key))).ToList();

        return new ProjectHistoryRestorePreview(scope, relevantChanges, blocked, estimateBytes);
    }

    /// <inheritdoc />
    public async Task<ProjectHistoryRestoreResult?> RestoreAsync(
        Guid projectId,
        Guid versionId,
        ProjectHistoryRestoreScope scope,
        IReadOnlyList<ProjectHistoryRestoreItemRef> items,
        Guid? userId,
        CancellationToken ct)
    {
        ProjectVersion? version = await LoadVersionAsync(projectId, versionId, ct);
        if (version is null)
            return null;

        Project? project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null)
            return null;

        // Item scope: nothing the request names is in the version, so there is nothing to restore and no
        // safety version to take for it.
        if (scope == ProjectHistoryRestoreScope.Item && SelectTargets(scope, items, version).Count == 0)
            throw new ProjectHistoryNothingToRestoreException();

        // Step 3: one restore per project at a time.
        await using IAsyncDisposable? claim = await _restoreLock.TryAcquireAsync(projectId, ct);
        if (claim is null)
            throw new ProjectHistoryRestoreConflictException(
                ProjectHistoryRestoreCodes.RestoreInProgress,
                "Another restore of this project is running. Wait for it to finish, then try again.");

        // Step 2, inside the lock (run starts refuse while it is held, so no run can slip in after the
        // check): a run in flight must not be restored over. Project scope looks at every run of the
        // project; item scope only at the workflows of this project the request names and the version holds.
        if (scope == ProjectHistoryRestoreScope.Project)
        {
            bool anyRun = await _db.WorkflowExecutions.AnyAsync(
                e => e.ProjectId == projectId
                     && (e.Status == ExecutionStatus.Queued || e.Status == ExecutionStatus.Running),
                ct);
            if (anyRun)
                throw new ProjectHistoryRestoreConflictException(
                    ProjectHistoryRestoreCodes.ProjectRunActive,
                    "A run of this project is in progress. Wait for it to finish or cancel it, then restore.");
        }
        else
        {
            foreach (ProjectVersionItem workflowItem in SelectTargets(scope, items, version)
                         .Where(i => i.Kind == ProjectVersionItemKind.Workflow))
            {
                if (!Guid.TryParse(workflowItem.ItemKey, out Guid workflowId))
                    continue;

                bool ownWorkflow = await _db.WorkflowDefinitions.AnyAsync(
                    w => w.Id == workflowId && w.ProjectId == projectId, ct);
                if (!ownWorkflow)
                    continue;

                bool workflowRun = await _db.WorkflowExecutions.AnyAsync(
                    e => e.WorkflowDefinitionId == workflowId
                         && (e.Status == ExecutionStatus.Queued || e.Status == ExecutionStatus.Running),
                    ct);
                if (workflowRun)
                    throw new ProjectHistoryRestoreConflictException(
                        ProjectHistoryRestoreCodes.WorkflowRunActive,
                        "The workflow has a run in progress. Wait for it to finish or cancel it, then restore.");
            }
        }

        // Step 4: the safety version, never deduplicated, so the restore can be undone.
        ProjectVersion? safety = await _history.CaptureAsync(
            projectId, ProjectVersionCause.BeforeRestore, name: null, userId, ct);
        if (safety is null)
            return null;

        ProjectRestoreWriteContext context = new(
            project, version, version.Items.ToList(), userId,
            new ProjectHistoryContentGuard(_db, project.Id));
        List<ProjectRestoreOutcome> outcomes = new();

        // Step 6 first where it matters: a live file the version does not hold must free its path (and
        // its bytes are protected by the delete path) before a version file is recreated.
        bool completed = false;
        try
        {
            if (scope == ProjectHistoryRestoreScope.Project)
            {
                outcomes.AddRange(await RemoveAbsentAsync(context, ct));
                await RunScopeHookAsync(context, static (h, c, t) => h.PrepareProjectScopeAsync(c, t), ct);
            }

            List<ProjectVersionItem> targets = SelectTargets(scope, items, version);
            foreach (ProjectVersionItem item in targets)
            {
                if (item.ErasedAt is not null)
                {
                    outcomes.Add(ProjectRestoreOutcome.Skipped(
                        item.Kind, item.ItemKey, item.Name, ProjectHistoryRestoreReasons.Erased));
                    continue;
                }

                if (!_handlersByKind.TryGetValue(item.Kind, out IProjectRestoreKindHandler? handler))
                {
                    outcomes.Add(ProjectRestoreOutcome.Skipped(
                        item.Kind, item.ItemKey, item.Name, ProjectHistoryRestoreReasons.NotYetSupported));
                    continue;
                }

                outcomes.Add(await ApplyGuardedAsync(handler, context, item, ct));
            }

            completed = true;
        }
        finally
        {
            // A cancelled or failed restore must never strand files parked at .restore-tmp: the parking
            // is undone here whatever happened, detached from the request token and bounded in time.
            if (scope == ProjectHistoryRestoreScope.Project && context.MovedAside.Count > 0)
                await UndoParkingAsync(context, discardPending: !completed);
        }

        if (scope == ProjectHistoryRestoreScope.Item)
        {
            HashSet<(ProjectVersionItemKind Kind, string Key)> known =
                new(version.Items.Select(i => (i.Kind, i.ItemKey)));
            foreach (ProjectHistoryRestoreItemRef request in Distinct(items))
            {
                if (!known.Contains((request.Kind, request.Key)))
                {
                    outcomes.Add(ProjectRestoreOutcome.Skipped(
                        request.Kind, request.Key, null, ProjectHistoryRestoreReasons.ItemNotFound));
                }
            }
        }

        _logger.LogInformation(
            "Project {ProjectId}: restored version {VersionId} ({Scope}, safety {SafetyVersionId}), {Restored} restored, {Removed} removed, {Skipped} skipped",
            projectId, versionId, scope, safety.Id,
            outcomes.Count(o => o.Disposition == ProjectRestoreDisposition.Restored),
            outcomes.Count(o => o.Disposition == ProjectRestoreDisposition.Removed),
            outcomes.Count(o => o.Disposition == ProjectRestoreDisposition.Skipped));

        return new ProjectHistoryRestoreResult(
            safety.Id,
            outcomes.Where(o => o.Disposition == ProjectRestoreDisposition.Restored)
                .Select(o => new ProjectHistoryRestoreEntry(o.Kind, o.Key, o.Name)).ToList(),
            outcomes.Where(o => o.Disposition == ProjectRestoreDisposition.Removed)
                .Select(o => new ProjectHistoryRestoreEntry(o.Kind, o.Key, o.Name)).ToList(),
            outcomes.Where(o => o.Disposition == ProjectRestoreDisposition.Skipped && o.Reason is not null)
                .Select(o => new ProjectHistoryRestoreSkipped(o.Kind, o.Key, o.Name, o.Reason!)).ToList(),
            outcomes.Where(o => o.Notice is not null)
                .Select(o => new ProjectHistoryRestoreNotice(o.Kind, o.Key, o.Notice!)).ToList());
    }

    /// <summary>
    /// Applies one item. An exception (storage, protect, database) is logged, the item's unsaved tracked
    /// changes are dropped and it is reported skipped RESTORE_FAILED, so the rest of the restore still
    /// runs and the caller gets a partial success (contract section 8 step 7). Cancellation propagates.
    /// </summary>
    private async Task<ProjectRestoreOutcome> ApplyGuardedAsync(
        IProjectRestoreKindHandler handler, ProjectRestoreWriteContext context, ProjectVersionItem item, CancellationToken ct)
    {
        try
        {
            return await handler.ApplyAsync(context, item, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Project {ProjectId}: restoring {Kind} item failed; skipped", context.Project.Id, item.Kind);
            DiscardPendingChanges();
            return ProjectRestoreOutcome.Skipped(
                item.Kind, item.ItemKey, item.Name, ProjectHistoryRestoreReasons.RestoreFailed);
        }
    }

    /// <summary>
    /// Puts every file still parked at the temporary path back (its original path, or a free numbered one),
    /// on <see cref="CancellationToken.None"/> with a bounded timeout so a cancelled request cannot skip it.
    /// </summary>
    private async Task UndoParkingAsync(ProjectRestoreWriteContext context, bool discardPending)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            if (discardPending)
                DiscardPendingChanges();
            await RunScopeHookAsync(context, static (h, c, t) => h.CompleteProjectScopeAsync(c, t), timeout.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Project {ProjectId}: undoing the temporary file parking failed", context.Project.Id);
        }
    }

    private async Task RunScopeHookAsync(
        ProjectRestoreWriteContext context,
        Func<IProjectRestoreKindHandler, ProjectRestoreWriteContext, CancellationToken, Task> hook,
        CancellationToken ct)
    {
        foreach (IProjectRestoreKindHandler handler in _handlers)
        {
            try
            {
                await hook(handler, context, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Project {ProjectId}: {Kind} restore scope hook failed", context.Project.Id, handler.Kind);
                DiscardPendingChanges();
            }
        }
    }

    private void DiscardPendingChanges()
    {
        foreach (var entry in _db.ChangeTracker.Entries().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.State = EntityState.Detached;
                    break;
                case EntityState.Modified:
                    entry.CurrentValues.SetValues(entry.OriginalValues);
                    entry.State = EntityState.Unchanged;
                    break;
                case EntityState.Deleted:
                    entry.State = EntityState.Unchanged;
                    break;
            }
        }
    }

    private async Task<IReadOnlyList<ProjectRestoreOutcome>> RemoveAbsentAsync(
        ProjectRestoreWriteContext context, CancellationToken ct)
    {
        List<ProjectRestoreOutcome> outcomes = new();
        foreach (IProjectRestoreKindHandler handler in _handlers)
        {
            HashSet<string> present = new(
                context.VersionItems
                    .Where(i => i.Kind == handler.Kind)
                    .Select(i => i.ItemKey),
                StringComparer.Ordinal);

            try
            {
                outcomes.AddRange(await handler.RemoveAbsentAsync(context, present, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Project {ProjectId}: removing absent {Kind} items failed; skipped", context.Project.Id, handler.Kind);
                DiscardPendingChanges();
                outcomes.Add(ProjectRestoreOutcome.Skipped(
                    handler.Kind, "*", null, ProjectHistoryRestoreReasons.RestoreFailed));
            }
        }

        return outcomes;
    }

    private static List<ProjectVersionItem> SelectTargets(
        ProjectHistoryRestoreScope scope,
        IReadOnlyList<ProjectHistoryRestoreItemRef> items,
        ProjectVersion version)
    {
        if (scope == ProjectHistoryRestoreScope.Project)
        {
            return version.Items
                .OrderBy(i => i.Kind)
                .ThenBy(i => i.ItemKey, StringComparer.Ordinal)
                .ToList();
        }

        List<ProjectVersionItem> targets = new();
        foreach (ProjectHistoryRestoreItemRef request in Distinct(items))
        {
            ProjectVersionItem? item = version.Items.FirstOrDefault(
                i => i.Kind == request.Kind && string.Equals(i.ItemKey, request.Key, StringComparison.Ordinal));
            if (item is not null)
                targets.Add(item);
        }

        return targets;
    }

    private static IEnumerable<ProjectHistoryRestoreItemRef> Distinct(
        IReadOnlyList<ProjectHistoryRestoreItemRef> items) =>
        items.DistinctBy(r => (r.Kind, r.Key));

    private static HashSet<(ProjectVersionItemKind Kind, string Key)> WantedKeys(
        ProjectHistoryRestoreScope scope, IReadOnlyList<ProjectHistoryRestoreItemRef> items) =>
        scope == ProjectHistoryRestoreScope.Project
            ? []
            : new HashSet<(ProjectVersionItemKind, string)>(Distinct(items).Select(r => (r.Kind, r.Key)));

    private string? BlockedReason(ProjectHistoryItemInfo item)
    {
        if (!_handlersByKind.ContainsKey(item.Kind))
            return ProjectHistoryRestoreReasons.NotYetSupported;

        return item.Restorable ? null : item.NotRestorableReason ?? ProjectHistoryRestoreReasons.ContentGone;
    }

    private async Task<ProjectVersion?> LoadVersionAsync(Guid projectId, Guid versionId, CancellationToken ct)
    {
        DateTime now = Now;
        return await _db.ProjectVersions
            .AsNoTracking()
            .Include(v => v.Items)
            .FirstOrDefaultAsync(
                v => v.Id == versionId
                     && v.ProjectId == projectId
                     && (v.ExpiresAt == null || v.ExpiresAt > now),
                ct);
    }
}
