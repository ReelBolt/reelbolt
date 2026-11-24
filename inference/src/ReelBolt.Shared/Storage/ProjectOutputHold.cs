using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Storage;

/// <summary>Wording of the answer for an output that is not served. Neutral on purpose.</summary>
public static class OutputHoldResponse
{
    public const string Code = "OUTPUT_UNAVAILABLE";
    public const string Message = "This output is not available.";
}

/// <summary>
/// The outcome of reading a project's output marker. <see cref="Unreadable"/> means the read failed, which holds
/// everything. Build it with <see cref="ProjectOutputHold"/>; the checks below are pure.
/// </summary>
public readonly record struct ProjectOutputHoldState(DateTime? Marker, bool Unreadable)
{
    public static ProjectOutputHoldState None => new(null, false);
    public static ProjectOutputHoldState Failed => new(null, true);
    public static ProjectOutputHoldState At(DateTime marker) => new(marker, false);

    /// <summary>True when a marker is set or the state could not be read.</summary>
    public bool IsActive => Unreadable || Marker is not null;

    /// <summary>
    /// True when a step output may predate the marker: any stamp is missing, or at or before the marker plus the
    /// clock margin. Pass both stamps; a Retry overwrites the run's start, so neither is enough alone.
    /// </summary>
    public bool HoldsOutput(DateTime? executedAt, DateTime? runStartedAt) => Holds(executedAt, runStartedAt);

    /// <summary>True when a stored file created at <paramref name="createdAt"/> may predate the marker.</summary>
    public bool HoldsFile(DateTime? createdAt) => Holds(createdAt);

    private bool Holds(params DateTime?[] stamps)
    {
        if (Unreadable) return true;
        if (Marker is not { } marker) return false;
        return stamps.Any(s => s is null || s.Value <= marker + ContentQuarantineRule.ClockSkewMargin);
    }
}

/// <summary>
/// Reads the marker that decides whether a project's earlier outputs are served and kept. Works on any context that
/// maps <see cref="Project"/> and <see cref="ProjectFile"/>; goes around the query filters.
/// </summary>
public static class ProjectOutputHold
{
    /// <summary>
    /// The start of a run to judge its output by: the earlier of its first start and its latest start. A Retry
    /// overwrites the latest start, so it alone could make an old output look new; when neither is known the
    /// result is null, which a state treats as held.
    /// </summary>
    public static DateTime? RunStart(DateTime? firstStartedAt, DateTime? startedAt) =>
        firstStartedAt is { } first && startedAt is { } latest ? (first <= latest ? first : latest) : firstStartedAt ?? startedAt;

    /// <summary>
    /// True when the platform made the file: the engine stamped <see cref="ProjectFile.OutputProducedAt"/> when it
    /// created the row, or (a row from before that stamp) its key is an engine output key.
    /// </summary>
    public static bool IsPlatformMade(Guid projectId, string? storageKey, DateTime? outputProducedAt) =>
        outputProducedAt is not null || ProjectOutputKeys.IsDerivedOutputFile(projectId, storageKey);

    /// <summary>
    /// The stamp a platform-made file is judged by: <see cref="ProjectFile.OutputProducedAt"/> when set (written once
    /// at row creation, never by a move or rename); for an older row the upload time, which a move may have reset and
    /// which therefore cannot be trusted for a row moved before the stamp existed. Owner results are judged separately.
    /// </summary>
    public static DateTime? FileStamp(DateTime? outputProducedAt, DateTime? uploadedAt) => outputProducedAt ?? uploadedAt;

    /// <summary>
    /// The stored marker; when none but a CSAM-flagged file row exists, now. Moderation rows are ignored. Any read
    /// failure other than cancellation yields an unreadable state (which holds everything); cancellation propagates.
    /// </summary>
    public static async Task<ProjectOutputHoldState> ReadAsync(DbContext db, Guid projectId, CancellationToken ct)
    {
        try
        {
            return (await ReadManyCoreAsync(db, [projectId], ct))[projectId];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ProjectOutputHoldState.Failed;
        }
    }

    /// <summary>
    /// The batch form for list paths and sweeps: one state per requested project id, same semantics, a failure
    /// marks every requested id unreadable.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, ProjectOutputHoldState>> ReadManyAsync(
        DbContext db, IReadOnlyCollection<Guid> projectIds, CancellationToken ct)
    {
        try
        {
            return await ReadManyCoreAsync(db, projectIds, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return projectIds.Distinct().ToDictionary(id => id, _ => ProjectOutputHoldState.Failed);
        }
    }

    private static async Task<Dictionary<Guid, ProjectOutputHoldState>> ReadManyCoreAsync(
        DbContext db, IReadOnlyCollection<Guid> projectIds, CancellationToken ct)
    {
        Guid[] ids = projectIds.Distinct().ToArray();
        Dictionary<Guid, DateTime?> stored = (await db.Set<Project>()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(p => ids.Contains(p.Id))
                .Select(p => new { p.Id, p.CsamOutputHoldAt })
                .ToListAsync(ct))
            .ToDictionary(p => p.Id, p => p.CsamOutputHoldAt);

        HashSet<Guid> flagged = (await db.Set<ProjectFile>()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(f => ids.Contains(f.ProjectId) && f.CsamStatus == CsamScanStatus.Quarantined)
                .Select(f => f.ProjectId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

        DateTime now = DateTime.UtcNow;
        Dictionary<Guid, ProjectOutputHoldState> result = [];
        foreach (Guid id in ids)
        {
            if (stored.TryGetValue(id, out DateTime? marker) && marker is not null)
                result[id] = ProjectOutputHoldState.At(marker.Value);
            else
                result[id] = flagged.Contains(id) ? ProjectOutputHoldState.At(now) : ProjectOutputHoldState.None;
        }
        return result;
    }
}
