using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Editing;

/// <summary>
/// The version-history retention rules (decision D14), in one place so the controller, the prune
/// job and the tests cannot drift. A snapshot is <b>kept for good</b> when it is a named version
/// (<see cref="EditTimelineSnapshotCause.Named"/>) or revision 1 (the origin: no timeline ever
/// loses its start, and every listed point stays restorable). Everything else is an
/// <b>automatic checkpoint</b>: it expires <c>Editing:AutoCheckpointDays</c> (30) after creation
/// and at most <see cref="MaxAutomaticPerTimeline"/> of them are kept per timeline (oldest first out).
/// </summary>
public static class TimelineSnapshotRetention
{
    /// <summary>Per timeline, how many automatic checkpoints are kept before the oldest go.</summary>
    public const int MaxAutomaticPerTimeline = 200;

    /// <summary>
    /// A snapshot the prune and the cap may delete: not named, not revision 1. A label carried by a
    /// "before AI suggestion" checkpoint does not pin it; only naming a version does.
    /// </summary>
    public static readonly Expression<Func<EditTimelineSnapshot, bool>> IsAutomatic = s =>
        s.Cause != EditTimelineSnapshotCause.Named
        && s.Revision != 1
        && (s.Name == null || s.Cause == EditTimelineSnapshotCause.BeforeProposal);

    /// <summary>When a new snapshot of <paramref name="revision"/> with <paramref name="cause"/> expires (null = never).</summary>
    public static DateTime? ExpiryFor(string cause, int revision, DateTime nowUtc, TimeSpan window) =>
        cause == EditTimelineSnapshotCause.Named || revision == 1 ? null : nowUtc + window;

    /// <summary>
    /// Makes room for <paramref name="adding"/> new automatic checkpoints on one timeline by dropping
    /// the oldest automatic ones beyond the cap. On a relational store the delete runs now; on one
    /// that cannot bulk-delete the rows are removed from the tracked context for the caller's save.
    /// </summary>
    public static async Task EnforceCapAsync(InferenceApiDbContext db, Guid timelineId, int adding, CancellationToken ct)
    {
        IQueryable<EditTimelineSnapshot> automatic = db.EditTimelineSnapshots
            .Where(s => s.TimelineId == timelineId)
            .Where(IsAutomatic);
        int excess = await automatic.CountAsync(ct) + adding - MaxAutomaticPerTimeline;
        if (excess <= 0) return;

        List<Guid> oldest = await automatic
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Revision)
            .Take(excess)
            .Select(s => s.Id)
            .ToListAsync(ct);
        await DeleteByIdAsync(db, oldest, ct);
    }

    /// <summary>
    /// One prune pass over every timeline: deletes expired automatic checkpoints, then trims each
    /// timeline over the cap. Idempotent. Returns how many snapshots went.
    /// </summary>
    public static async Task<int> PruneAsync(InferenceApiDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        List<Guid> expired = await db.EditTimelineSnapshots
            .Where(IsAutomatic)
            .Where(s => s.ExpiresAt != null && s.ExpiresAt < nowUtc)
            .Select(s => s.Id)
            .ToListAsync(ct);
        int removed = await DeleteByIdAsync(db, expired, ct, save: true);

        List<Guid> overCap = await db.EditTimelineSnapshots
            .Where(IsAutomatic)
            .GroupBy(s => s.TimelineId)
            .Where(g => g.Count() > MaxAutomaticPerTimeline)
            .Select(g => g.Key)
            .ToListAsync(ct);
        foreach (Guid timelineId in overCap)
        {
            IQueryable<EditTimelineSnapshot> automatic = db.EditTimelineSnapshots.Where(s => s.TimelineId == timelineId).Where(IsAutomatic);
            int excess = await automatic.CountAsync(ct) - MaxAutomaticPerTimeline;
            if (excess <= 0) continue;
            List<Guid> oldest = await automatic
                .OrderBy(s => s.CreatedAt).ThenBy(s => s.Revision)
                .Take(excess)
                .Select(s => s.Id)
                .ToListAsync(ct);
            removed += await DeleteByIdAsync(db, oldest, ct, save: true);
        }

        return removed;
    }

    private static async Task<int> DeleteByIdAsync(InferenceApiDbContext db, List<Guid> ids, CancellationToken ct, bool save = false)
    {
        if (ids.Count == 0) return 0;
        if (db.Database.IsRelational())
        {
            int total = 0;
            foreach (Guid[] batch in ids.Chunk(500))
            {
                total += await db.EditTimelineSnapshots.Where(s => batch.Contains(s.Id)).ExecuteDeleteAsync(ct);
            }

            return total;
        }

        db.EditTimelineSnapshots.RemoveRange(await db.EditTimelineSnapshots.Where(s => ids.Contains(s.Id)).ToListAsync(ct));
        if (save) await db.SaveChangesAsync(ct);
        return ids.Count;
    }
}
