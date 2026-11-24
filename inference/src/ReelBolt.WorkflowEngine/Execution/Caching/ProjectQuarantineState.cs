using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Execution.Caching;

/// <summary>
/// The one read of "which of this project's files are quarantined" for code that reuses earlier output instead of
/// producing it. An earlier result may depend on a file quarantined since, and nothing records which files a result
/// used, so reuse is judged per project.
/// </summary>
public static class ProjectQuarantineState
{
    /// <summary>
    /// Digest of the project's quarantined files (CSAM or moderation) together with the project's durable marker, or
    /// null when it has neither. Goes around the query filter, which hides exactly these rows; never memoize it. Ids,
    /// states and the marker only. The marker outlives the rows, so deleting, replacing or releasing a row does not
    /// reopen reuse. A read failure throws: every caller treats that as "do not reuse".
    /// </summary>
    public static async Task<string?> ReadAsync(WorkflowEngineDbContext db, Guid projectId, CancellationToken ct)
    {
        var rows = await QuarantinedRows(db, projectId).ToListAsync(ct);
        DateTime? marker = await ReadStoredMarkerAsync(db, projectId, ct);

        if (rows.Count == 0 && marker is null)
            return null;

        StringBuilder sb = new();
        foreach (var row in rows)
            sb.Append(row.Id).Append('|').Append(row.CsamStatus).Append('|').Append(row.ModerationStatus).Append('\n');
        sb.Append("marker|").Append(marker?.Ticks.ToString(CultureInfo.InvariantCulture) ?? "-").Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// The instant from which the project's earlier output may no longer be reused, or null when nothing in the
    /// project is quarantined. The stored marker; when a quarantined row exists with no marker (a write that skipped
    /// it), now. A read failure throws: callers treat that as "do not reuse".
    /// </summary>
    public static async Task<DateTime?> ReadMarkerAsync(WorkflowEngineDbContext db, Guid projectId, CancellationToken ct)
    {
        DateTime? marker = await ReadStoredMarkerAsync(db, projectId, ct);
        if (marker is not null)
            return marker;

        return await QuarantinedRows(db, projectId).AnyAsync(ct) ? DateTime.UtcNow : null;
    }

    /// <summary>See <see cref="ContentQuarantineRule.ClockSkewMargin"/>.</summary>
    public static TimeSpan ClockSkewMargin => ContentQuarantineRule.ClockSkewMargin;

    /// <summary>
    /// True when an earlier output may predate the marker (<see cref="ContentQuarantineRule.Predates"/>). Pass every
    /// stamp that bounds the output's start, because no single one is enough: a Retry overwrites the run's StartedAt, so
    /// an old output can look new by it alone, and a step that began after the marker may still read what its run made
    /// before it.
    /// </summary>
    public static bool Predates(DateTime? marker, params DateTime?[] stamps) => ContentQuarantineRule.Predates(marker, stamps);

    /// <summary>
    /// The gate for a stored step result named from outside its own attempt: true when it must not be used, because
    /// it may predate the marker, was superseded (a replaced result is no longer a source once the project is marked), or the
    /// state cannot be read. <paramref name="runFirstStartedAt"/> is the run's FirstStartedAt, never its StartedAt.
    /// Cancellation propagates.
    /// </summary>
    public static async Task<bool> ResultPredatesAsync(
        WorkflowEngineDbContext db, Guid projectId, DateTime? resultExecutedAt, DateTime? runFirstStartedAt, CancellationToken ct,
        DateTime? supersededAt = null)
    {
        try
        {
            DateTime? marker = await ReadMarkerAsync(db, projectId, ct);
            return marker is not null && (supersededAt is not null || Predates(marker, resultExecutedAt, runFirstStartedAt));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true;
        }
    }

    private static async Task<DateTime?> ReadStoredMarkerAsync(WorkflowEngineDbContext db, Guid projectId, CancellationToken ct) =>
        await db.Projects
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.ContentQuarantinedAt)
            .FirstOrDefaultAsync(ct);

    private static IQueryable<QuarantinedRow> QuarantinedRows(WorkflowEngineDbContext db, Guid projectId) =>
        db.ProjectFiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.ProjectId == projectId
                && (f.CsamStatus == CsamScanStatus.Quarantined || f.ModerationStatus == FileModerationStatus.Quarantined))
            .OrderBy(f => f.Id)
            .Select(f => new QuarantinedRow(f.Id, f.CsamStatus, f.ModerationStatus));

    private sealed record QuarantinedRow(Guid Id, CsamScanStatus CsamStatus, FileModerationStatus ModerationStatus);
}
