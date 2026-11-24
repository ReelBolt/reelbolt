using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Editing;

/// <summary>
/// The seeded path's asset resolver: turns what a <see cref="TimelineManifest"/> carries (a storage
/// key, a file name, or a source's recorded identity) into an <see cref="AssetRef"/> THIS project owns.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of owner. A <c>ProjectFile</c> is indexed by <c>StorageKey</c> and <c>OriginalFileName</c>.
/// A <c>WorkflowStepResult</c> of any execution of THIS project (<c>WorkflowExecution.ProjectId</c>)
/// is indexed by <c>OutputStorageKey</c>: a render is a step result under
/// <c>projects/{p}/outputFiles/...</c>, never a project file, which is why a workflow that mixes audio
/// into an earlier run's render used to seed with its cuts silently dropped.
/// </para>
/// <para>
/// <b>ProjectFile wins</b> when a key is both (a render registered via
/// <c>register_output_as_project_file</c>): the project file is the stable, user-visible media-pool
/// asset and survives execution cleanup, and checking it first makes the answer independent of row
/// order. Among several step results sharing one output key (a step-cache replay) the newest wins,
/// ties broken by id, so the answer is deterministic.
/// </para>
/// <para>
/// Only this project's rows are ever indexed, so another project's render, a deleted row or an
/// invented id resolves to <c>null</c> and the seeder reports the item instead of placing it.
/// </para>
/// </remarks>
public sealed class SeedAssetResolver
{
    private readonly Dictionary<string, Guid> _fileByStorageKey;
    private readonly Dictionary<string, Guid> _fileByName;
    private readonly Dictionary<string, Guid> _stepResultByKey;
    private readonly HashSet<Guid> _fileIds;
    private readonly HashSet<Guid> _stepResultIds;

    private SeedAssetResolver(
        Dictionary<string, Guid> fileByStorageKey,
        Dictionary<string, Guid> fileByName,
        Dictionary<string, Guid> stepResultByKey,
        HashSet<Guid> fileIds,
        HashSet<Guid> stepResultIds)
    {
        _fileByStorageKey = fileByStorageKey;
        _fileByName = fileByName;
        _stepResultByKey = stepResultByKey;
        _fileIds = fileIds;
        _stepResultIds = stepResultIds;
    }

    public static async Task<SeedAssetResolver> LoadAsync(InferenceApiDbContext db, Guid projectId, CancellationToken ct)
    {
        var files = await db.ProjectFiles
            .AsNoTracking()
            .Where(f => f.ProjectId == projectId)
            .Select(f => new { f.Id, f.StorageKey, f.OriginalFileName, f.UploadedAt, f.OutputProducedAt })
            .ToListAsync(ct);

        var results = await db.WorkflowStepResults
            .AsNoTracking()
            .Where(r => r.WorkflowExecution.ProjectId == projectId && r.OutputStorageKey != null)
                        .Select(r => new { r.Id, r.OutputStorageKey, r.CompletedAt, r.ExecutedAt })
            .ToListAsync(ct);

        // An asset whose bytes are not served resolves as unavailable, like one this project does not own.
        HashSet<Guid> heldResults = await Outputs.OutputGate.HeldResultIdsAsync(db, projectId, results.Select(r => r.Id).ToList(), ct);
        HashSet<Guid> heldFiles = await Outputs.OutputGate.HeldFileIdsAsync(
            db, projectId,
            files.Select(f => (f.Id, f.StorageKey, f.UploadedAt, f.OutputProducedAt)), ct);
        if (heldResults.Count > 0) results = results.Where(r => !heldResults.Contains(r.Id)).ToList();
        if (heldFiles.Count > 0) files = files.Where(f => !heldFiles.Contains(f.Id)).ToList();

        Dictionary<string, Guid> byStorageKey = new(StringComparer.Ordinal);
        Dictionary<string, Guid> byName = new(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!string.IsNullOrWhiteSpace(file.StorageKey)) byStorageKey.TryAdd(file.StorageKey, file.Id);
            if (!string.IsNullOrWhiteSpace(file.OriginalFileName)) byName.TryAdd(file.OriginalFileName, file.Id);
        }

        Dictionary<string, Guid> byResultKey = new(StringComparer.Ordinal);
        foreach (var result in results
                     .OrderByDescending(r => r.CompletedAt ?? r.ExecutedAt)
                     .ThenBy(r => r.Id))
        {
            if (!string.IsNullOrWhiteSpace(result.OutputStorageKey)) byResultKey.TryAdd(result.OutputStorageKey!, result.Id);
        }

        return new SeedAssetResolver(
            byStorageKey,
            byName,
            byResultKey,
            files.Select(f => f.Id).ToHashSet(),
            results.Select(r => r.Id).ToHashSet());
    }

    /// <summary>
    /// A storage key (cuts, inserts, graphics) or a file name (sfx, music) → this project's asset.
    /// </summary>
    public AssetRef? ResolveKey(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return null;

        if (_fileByStorageKey.TryGetValue(identifier, out Guid fileId) || _fileByName.TryGetValue(identifier, out fileId))
        {
            return new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: fileId);
        }

        return _stepResultByKey.TryGetValue(identifier, out Guid stepResultId)
            ? new AssetRef(AssetRefKind.StepResult, StepResultId: stepResultId)
            : null;
    }

    /// <summary>
    /// A source's recorded identity, only when this project owns that row; else <c>null</c> so the
    /// caller falls back to the storage key. An id of another project is never echoed.
    /// </summary>
    public AssetRef? ResolveIdentity(TimelineSource source)
    {
        if (source.ProjectFileId is { } fileId && _fileIds.Contains(fileId))
        {
            return new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: fileId);
        }

        if (source.StepResultId is { } stepResultId && _stepResultIds.Contains(stepResultId))
        {
            return new AssetRef(AssetRefKind.StepResult, StepResultId: stepResultId);
        }

        return null;
    }
}
