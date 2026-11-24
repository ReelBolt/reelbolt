using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.Outputs;

/// <summary>
/// The one place that decides whether an output (a step result's files, or a project file the platform made)
/// is served. Every check reads the project's marker once for the call, never caches it, and treats an
/// unreadable marker as holding everything. A project without a marker costs one small read and changes nothing.
/// </summary>
public static class OutputGate
{
    /// <summary>The signed-in answer for an output that is not served.</summary>
    public static ObjectResult Unavailable() => new(new
    {
        error = OutputHoldResponse.Message,
        code = OutputHoldResponse.Code,
    })
    { StatusCode = StatusCodes.Status404NotFound };

    /// <summary>The neutral JSON a tool returns for an output that is not served.</summary>
    public static string ToolJson() =>
        System.Text.Json.JsonSerializer.Serialize(new { error = OutputHoldResponse.Message, code = OutputHoldResponse.Code });

    public static Task<ProjectOutputHoldState> ReadAsync(DbContext db, Guid projectId, CancellationToken ct) =>
        ProjectOutputHold.ReadAsync(db, projectId, ct);

    /// <summary>True when the step result's output is not served. A result that does not exist is not held.</summary>
    public static async Task<bool> IsResultHeldAsync(DbContext db, Guid stepResultId, CancellationToken ct)
    {
        var row = await db.Set<WorkflowStepResult>().AsNoTracking()
            .Where(r => r.Id == stepResultId)
            .Select(r => new { r.ExecutedAt, r.WorkflowExecution.StartedAt, r.WorkflowExecution.FirstStartedAt, r.WorkflowExecution.ProjectId })
            .FirstOrDefaultAsync(ct);
        if (row is null) return false;
        ProjectOutputHoldState state = await ReadAsync(db, row.ProjectId, ct);
        return state.IsActive && state.HoldsOutput(row.ExecutedAt, ProjectOutputHold.RunStart(row.FirstStartedAt, row.StartedAt));
    }

    /// <summary>True when a run that started at <paramref name="startedAt"/> (use <see cref="ProjectOutputHold.RunStart"/>) may predate the marker, so its run-level result text is not shown.</summary>
    public static async Task<bool> IsRunHeldAsync(DbContext db, Guid projectId, DateTime? startedAt, CancellationToken ct)
    {
        ProjectOutputHoldState state = await ReadAsync(db, projectId, ct);
        return state.IsActive && state.HoldsOutput(startedAt, startedAt);
    }

    /// <summary>
    /// True when the run is not served: the run itself may predate the marker, or any of its step results is not served.
    /// Retry and Resume refuse such a run so it cannot be restarted and watched. Reads the marker once.
    /// </summary>
    public static async Task<bool> IsExecutionHeldAsync(DbContext db, WorkflowExecution execution, CancellationToken ct)
    {
        ProjectOutputHoldState state = await ReadAsync(db, execution.ProjectId, ct);
        if (!state.IsActive) return false;
        DateTime? runStart = ProjectOutputHold.RunStart(execution.FirstStartedAt, execution.StartedAt);
        if (state.HoldsOutput(runStart, runStart)) return true;
        DateTime[] executed = await db.Set<WorkflowStepResult>().AsNoTracking()
            .Where(r => r.WorkflowExecutionId == execution.Id)
            .Select(r => r.ExecutedAt)
            .ToArrayAsync(ct);
        return executed.Any(t => state.HoldsOutput(t, runStart));
    }

    /// <summary>The subset of <paramref name="resultIds"/> whose output is not served (empty when no project is marked).</summary>
    public static async Task<HashSet<Guid>> HeldResultIdsAsync(
        DbContext db, Guid projectId, IReadOnlyCollection<Guid> resultIds, CancellationToken ct)
    {
        ProjectOutputHoldState state = await ReadAsync(db, projectId, ct);
        if (!state.IsActive || resultIds.Count == 0) return [];
        Guid[] ids = resultIds.Distinct().ToArray();
        var rows = await db.Set<WorkflowStepResult>().AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .Select(r => new { r.Id, r.ExecutedAt, r.WorkflowExecution.StartedAt, r.WorkflowExecution.FirstStartedAt })
            .ToListAsync(ct);
        return rows.Where(r => state.HoldsOutput(r.ExecutedAt, ProjectOutputHold.RunStart(r.FirstStartedAt, r.StartedAt))).Select(r => r.Id).ToHashSet();
    }

    /// <summary>True when the stored object at <paramref name="storageKey"/> is not served for this project.</summary>
    public static async Task<bool> IsKeyHeldAsync(DbContext db, Guid projectId, string? storageKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(storageKey)) return false;
        ProjectOutputHoldState state = await ReadAsync(db, projectId, ct);
        if (!state.IsActive) return false;
        var rowsAtKey = await db.Set<ProjectFile>().IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.ProjectId == projectId && f.StorageKey == storageKey)
            .Select(f => new { f.OutputProducedAt, f.UploadedAt })
            .ToListAsync(ct);
        bool platformMade = ProjectOutputKeys.IsDerivedOutputFile(projectId, storageKey) || rowsAtKey.Any(f => f.OutputProducedAt is not null);
        if (platformMade)
        {
            // Unreadable state holds everything; a readable one holds a platform-made file by stamp or by sharing
            // a key with a held step result.
            if (state.Unreadable) return true;
            if (rowsAtKey.Any(f => HoldsFileStamp(state, ProjectOutputHold.FileStamp(f.OutputProducedAt, f.UploadedAt)))) return true;
        }
        else if (state.Unreadable)
        {
            // Not a platform-made file: only a step result could own it.
            return await db.Set<WorkflowStepResult>().AsNoTracking().AnyAsync(r =>
                r.WorkflowExecution.ProjectId == projectId &&
                (r.OutputStorageKey == storageKey || r.ArtifactStorageKey == storageKey || r.TimelineStorageKey == storageKey), ct);
        }

        var owners = await db.Set<WorkflowStepResult>().AsNoTracking()
            .Where(r => r.WorkflowExecution.ProjectId == projectId &&
                (r.OutputStorageKey == storageKey || r.ArtifactStorageKey == storageKey || r.TimelineStorageKey == storageKey))
            .Select(r => new { r.ExecutedAt, r.WorkflowExecution.StartedAt, r.WorkflowExecution.FirstStartedAt })
            .ToListAsync(ct);
        // An output key that no file row and no step result names has no stamp to judge it by, and a missing stamp holds.
        if (platformMade && rowsAtKey.Count == 0 && owners.Count == 0) return true;
        return owners.Any(o => state.HoldsOutput(o.ExecutedAt, ProjectOutputHold.RunStart(o.FirstStartedAt, o.StartedAt)));
    }

    /// <summary>True when a project file's bytes are not served: platform-made and held by stamp or by key.</summary>
    public static Task<bool> IsFileHeldAsync(DbContext db, ProjectFile file, CancellationToken ct) =>
        IsFileHeldCoreAsync(db, file, 0, ct);

    // Depth bounds the chain file -> picture export -> layer -> file; a loop is held.
    private const int MaxChainDepth = 4;

    private static async Task<bool> IsFileHeldCoreAsync(DbContext db, ProjectFile file, int depth, CancellationToken ct)
    {
        if (depth >= MaxChainDepth) return true;
        ProjectOutputHoldState state = await ReadAsync(db, file.ProjectId, ct);
        if (!state.IsActive) return false;
        if ((await HeldExportFileIdsAsync(db, file.ProjectId, state, [file.Id], depth, ct)).Count > 0) return true;
        if (!ProjectOutputHold.IsPlatformMade(file.ProjectId, file.StorageKey, file.OutputProducedAt)) return false; // a user's own upload stays available
        if (HoldsFileStamp(state, ProjectOutputHold.FileStamp(file.OutputProducedAt, file.UploadedAt))) return true;
        var owners = await db.Set<WorkflowStepResult>().AsNoTracking()
            .Where(r => r.WorkflowExecution.ProjectId == file.ProjectId &&
                (r.OutputStorageKey == file.StorageKey || r.ArtifactStorageKey == file.StorageKey))
            .Select(r => new { r.ExecutedAt, r.WorkflowExecution.StartedAt, r.WorkflowExecution.FirstStartedAt })
            .ToListAsync(ct);
        return owners.Any(o => state.HoldsOutput(o.ExecutedAt, ProjectOutputHold.RunStart(o.FirstStartedAt, o.StartedAt)));
    }

    /// <summary>True when the derivative's source (a step result, a project file or a stored key) is not served.</summary>
    public static async Task<bool> IsDerivativeHeldAsync(DbContext db, MediaDerivative row, CancellationToken ct)
    {
        ProjectOutputHoldState state = await ReadAsync(db, row.ProjectId, ct);
        if (!state.IsActive) return false;
        if (state.Unreadable) return true;
        if (row.SourceStepResultId is { } resultId && await IsResultHeldAsync(db, resultId, ct)) return true;
        if (row.SourceProjectFileId is { } fileId)
        {
            ProjectFile? file = await db.Set<ProjectFile>().IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == row.ProjectId, ct);
            if (file is not null && await IsFileHeldAsync(db, file, ct)) return true;
        }
        return await IsKeyHeldAsync(db, row.ProjectId, row.SourceStorageKey, ct);
    }

    /// <summary>True when <paramref name="outputId"/> names a step result or a project file of this project that is not served.</summary>
    public static async Task<bool> IsOutputIdHeldAsync(DbContext db, Guid projectId, Guid outputId, CancellationToken ct)
    {
        ProjectOutputHoldState state = await ReadAsync(db, projectId, ct);
        if (!state.IsActive) return false;
        bool isResult = await db.Set<WorkflowStepResult>().AsNoTracking()
            .AnyAsync(r => r.Id == outputId && r.WorkflowExecution.ProjectId == projectId, ct);
        if (isResult) return await IsResultHeldAsync(db, outputId, ct);
        ProjectFile? file = await db.Set<ProjectFile>().IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == outputId && f.ProjectId == projectId, ct);
        return file is not null && await IsFileHeldAsync(db, file, ct);
    }

    /// <summary>The subset of <paramref name="files"/> whose bytes are not served; one marker read for the whole set.</summary>
    public static async Task<HashSet<Guid>> HeldFileIdsAsync(
        DbContext db, Guid projectId, IReadOnlyCollection<ProjectFile> files, CancellationToken ct)
    {
        ProjectOutputHoldState state = await ReadAsync(db, projectId, ct);
        if (!state.IsActive || files.Count == 0) return [];
        if (state.Unreadable)
        {
            // Nothing can be looked up: every platform-made file is held, and so is every picture export, as the
            // single-file check treats it. If the export lookup fails as well, every file in the list is held.
            HashSet<Guid> unreadableHeld = files.Where(f => ProjectOutputHold.IsPlatformMade(projectId, f.StorageKey, f.OutputProducedAt)).Select(f => f.Id).ToHashSet();
            try
            {
                unreadableHeld.UnionWith(await HeldExportFileIdsAsync(db, projectId, state, files.Select(f => f.Id).ToArray(), 0, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return files.Select(f => f.Id).ToHashSet();
            }

            return unreadableHeld;
        }
        HashSet<Guid> heldExports = await HeldExportFileIdsAsync(db, projectId, state, files.Select(f => f.Id).ToArray(), 0, ct);
        List<ProjectFile> derived = files.Where(f => ProjectOutputHold.IsPlatformMade(projectId, f.StorageKey, f.OutputProducedAt)).ToList();
        if (derived.Count == 0) return heldExports;
        string[] keys = derived.Select(f => f.StorageKey).Distinct().ToArray();
        var owners = await db.Set<WorkflowStepResult>().AsNoTracking()
            .Where(r => r.WorkflowExecution.ProjectId == projectId &&
                (keys.Contains(r.OutputStorageKey!) || keys.Contains(r.ArtifactStorageKey!)))
            .Select(r => new { r.OutputStorageKey, r.ArtifactStorageKey, r.ExecutedAt, r.WorkflowExecution.StartedAt, r.WorkflowExecution.FirstStartedAt })
            .ToListAsync(ct);
        HashSet<string> heldKeys = [];
        foreach (var o in owners.Where(o => state.HoldsOutput(o.ExecutedAt, ProjectOutputHold.RunStart(o.FirstStartedAt, o.StartedAt))))
        {
            if (o.OutputStorageKey is not null) heldKeys.Add(o.OutputStorageKey);
            if (o.ArtifactStorageKey is not null) heldKeys.Add(o.ArtifactStorageKey);
        }
        HashSet<Guid> held = derived.Where(f => HoldsFileStamp(state, ProjectOutputHold.FileStamp(f.OutputProducedAt, f.UploadedAt)) || heldKeys.Contains(f.StorageKey))
            .Select(f => f.Id).ToHashSet();
        held.UnionWith(heldExports);
        return held;
    }

    /// <summary>
    /// The subset of <paramref name="fileIds"/> that are picture exports whose picture is not served: a layer of the
    /// picture is held, or the picture has no layer left to tell the origin from and the export was made at or before
    /// the marker. An export's bytes are the user's own upload, so the picture is the only record of where they came from.
    /// </summary>
    private static async Task<HashSet<Guid>> HeldExportFileIdsAsync(
        DbContext db, Guid projectId, ProjectOutputHoldState state, IReadOnlyCollection<Guid> fileIds, int depth, CancellationToken ct)
    {
        if (!state.IsActive || fileIds.Count == 0) return [];
        Guid[] ids = fileIds.Distinct().ToArray();
        var exports = await db.Set<EditImageExport>().AsNoTracking()
            .Where(e => e.ProjectFileId != null && ids.Contains(e.ProjectFileId.Value)
                && db.Set<EditImageDocument>().IgnoreQueryFilters().Any(d => d.Id == e.DocumentId && d.ProjectId == projectId))
            .Select(e => new { e.ProjectFileId, e.DocumentId, e.CreatedAt })
            .ToListAsync(ct);
        HashSet<Guid> held = [];
        foreach (var export in exports)
        {
            List<EditImageLayer> layers = await db.Set<EditImageLayer>().IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.DocumentId == export.DocumentId).ToListAsync(ct);
            bool isHeld = state.Unreadable;
            foreach (EditImageLayer layer in layers)
            {
                if (isHeld) break;
                isHeld = await IsPictureLayerHeldCoreAsync(db, projectId, layer, depth + 1, ct);
            }
            if (!isHeld && layers.Count == 0) isHeld = HoldsFileStamp(state, export.CreatedAt);
            if (isHeld) held.Add(export.ProjectFileId!.Value);
        }
        return held;
    }

    /// <summary>
    /// The one place a platform-made file's own stamp is compared with the marker. Callers that hold a stamp
    /// (stubs, export rows) go through here so the stamp column can change without touching them.
    /// </summary>
    public static bool HoldsFileStamp(ProjectOutputHoldState state, DateTime? stamp) => state.IsActive && state.HoldsFile(stamp);

    /// <summary>True when a platform-made file with this key and stamp is held by its own stamp.</summary>
    public static bool HoldsDerivedFile(
        ProjectOutputHoldState state, Guid projectId, string storageKey, DateTime? uploadedAt, DateTime? outputProducedAt) =>
        state.IsActive && ProjectOutputHold.IsPlatformMade(projectId, storageKey, outputProducedAt)
        && state.HoldsFile(ProjectOutputHold.FileStamp(outputProducedAt, uploadedAt));

    /// <summary>As the file overload, for callers that only hold ids, keys and stamps.</summary>
    public static Task<HashSet<Guid>> HeldFileIdsAsync(
        DbContext db, Guid projectId, IEnumerable<(Guid Id, string StorageKey, DateTime UploadedAt, DateTime? OutputProducedAt)> files, CancellationToken ct) =>
        HeldFileIdsAsync(db, projectId,
            files.Select(f => new ProjectFile
            {
                Id = f.Id,
                ProjectId = projectId,
                StorageKey = f.StorageKey,
                UploadedAt = f.UploadedAt,
                OutputProducedAt = f.OutputProducedAt,
            }).ToList(), ct);

    /// <summary>
    /// True when a picture layer's bitmap (or its cutout) is not served. A layer is a private copy, so its own key says
    /// nothing: the origin decides. The layer's own source file decides when that row can be found;
    /// otherwise (no link, or the row is gone) a copy made at or before the marker counts as held. The picture's source
    /// file is not consulted. A text layer has no bitmap and is never held.
    /// </summary>
    public static Task<bool> IsPictureLayerHeldAsync(DbContext db, Guid projectId, EditImageLayer? layer, CancellationToken ct) =>
        IsPictureLayerHeldCoreAsync(db, projectId, layer, 0, ct);

    private static async Task<bool> IsPictureLayerHeldCoreAsync(DbContext db, Guid projectId, EditImageLayer? layer, int depth, CancellationToken ct)
    {
        if (depth >= MaxChainDepth) return true;
        if (layer is null || layer.Kind == EditImageLayerKind.Text) return false;
        ProjectOutputHoldState state = await ReadAsync(db, projectId, ct);
        if (!state.IsActive) return false;
        if (state.Unreadable) return true;

        if (await IsKeyHeldAsync(db, projectId, layer.StorageKey, ct)) return true;
        if (await IsKeyHeldAsync(db, projectId, layer.MatteStorageKey, ct)) return true;

        // Only the layer's own link to a file says where it came from. The picture's source file says nothing about a
        // layer that was generated, designed or copied privately, so it never clears one.
        // A generated or cutout layer inherits its parent's link, so a clean link says nothing about it: its own stamp
        // decides, and a held link still holds it.
        bool madeByModel = layer.Kind is EditImageLayerKind.Generated or EditImageLayerKind.Cutout;
        if (layer.SourceProjectFileId is { } fileId)
        {
            ProjectFile? file = await db.Set<ProjectFile>().IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId, ct);
            if (file is not null)
            {
                if (await IsFileHeldCoreAsync(db, file, depth + 1, ct)) return true;
                if (!madeByModel) return false;
            }
        }
        // No origin can be determined: a copy made before the marker counts as held.
        return HoldsFileStamp(state, layer.CreatedAt);
    }
}
