using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;

namespace ReelBolt.Inference.Api.Services.ChangeRequests;

/// <summary>
/// The rolling-hour request window shared by composition revisions and step change requests (one bucket,
/// <c>CompositionRevisions:MaxPerHourPerUserProject</c>): counted from the request rows themselves, so the
/// limit holds across API replicas without shared memory.
/// </summary>
internal static class RevisionRateWindow
{
    /// <summary>Creation times of the user's revisions and change requests in this project in the last hour.</summary>
    public static async Task<List<DateTime>> RecentAsync(InferenceApiDbContext db, Guid projectId, Guid userId, CancellationToken ct)
    {
        DateTime since = DateTime.UtcNow.AddHours(-1);
        // One hit per replay run: a change request with several targets has one row per target (and a film
        // target adds a revision row) that all share the run's ExecutionId.
        var revisions = await db.CompositionRevisions.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.RequestedByUserId == userId && r.CreatedAt >= since)
            .Select(r => new { r.ExecutionId, r.CreatedAt })
            .ToListAsync(ct);
        var changes = await db.StepChangeRequests.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.RequestedByUserId == userId && r.CreatedAt >= since)
            .Select(r => new { r.ExecutionId, r.CreatedAt })
            .ToListAsync(ct);
        return [.. revisions.Concat(changes)
            .GroupBy(r => r.ExecutionId)
            .Select(g => g.Min(r => r.CreatedAt))];
    }

    /// <summary>The 429 message and body when <paramref name="recent"/> has reached <paramref name="limit"/>; null otherwise.</summary>
    public static (string Message, Dictionary<string, object?> Extra)? Refusal(IReadOnlyList<DateTime> recent, int limit)
    {
        if (recent.Count < limit)
            return null;
        int retryAfter = Math.Max(1, (int)Math.Ceiling((recent.Min().AddHours(1) - DateTime.UtcNow).TotalSeconds));
        return (
            $"Change limit reached — {recent.Count} of {limit} changes in this project in the last hour; try again in {RunRateRefusal.Wait(retryAfter)}.",
            new Dictionary<string, object?>
            {
                ["used"] = recent.Count,
                ["limit"] = limit,
                ["windowMinutes"] = 60,
                ["retryAfterSeconds"] = Math.Max(1, retryAfter)
            });
    }
}
