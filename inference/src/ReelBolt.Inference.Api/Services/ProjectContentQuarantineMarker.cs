using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services;

/// <summary>
/// Stamps <see cref="Project.ContentQuarantinedAt"/> whenever a file of the project is written to Quarantined, in
/// the same save as the verdict. The stamp only moves forward and is never cleared.
/// </summary>
public static class ProjectContentQuarantineMarker
{
    /// <summary>
    /// Marks the project on the tracked context so the caller's next SaveChanges carries it with the verdict. Goes
    /// around the tenant filter (background services have no tenant).
    /// </summary>
    public static async Task StageAsync(InferenceApiDbContext db, Guid projectId, DateTime now, CancellationToken ct)
    {
        Project? project = await db.Projects.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return;
        if (project.ContentQuarantinedAt is null || project.ContentQuarantinedAt < now)
            project.ContentQuarantinedAt = now;
    }

    /// <summary>The set-based form for a verdict written by ExecuteUpdate; run it in the verdict's transaction.</summary>
    public static async Task<int> StampAsync(InferenceApiDbContext db, Guid projectId, DateTime now, CancellationToken ct, bool stampLibrary = true)
    {
        int moved = await db.Projects.IgnoreQueryFilters()
            .Where(p => p.Id == projectId && (p.ContentQuarantinedAt == null || p.ContentQuarantinedAt < now))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ContentQuarantinedAt, now), ct);
        // The workspace avatar library's hold marker is written in the same transaction as the project marker.
        if (stampLibrary && moved > 0) await Avatars.Library.AvatarLibraryHoldMarker.StampProjectSetBasedAsync(db, projectId, ct);
        return moved;
    }

    /// <summary>
    /// Stages <see cref="Project.CsamOutputHoldAt"/> on the tracked context for a CSAM verdict, in the same save as
    /// the verdict. Moves forward only. Moderation verdicts must not call this.
    /// </summary>
    public static async Task StageCsamAsync(InferenceApiDbContext db, Guid projectId, DateTime now, CancellationToken ct)
    {
        Project? project = await db.Projects.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return;
        if (project.CsamOutputHoldAt is null || project.CsamOutputHoldAt < now)
            project.CsamOutputHoldAt = now;
    }

    /// <summary>The set-based form of <see cref="StageCsamAsync"/>; run it in the verdict's transaction.</summary>
    public static async Task<int> StampCsamAsync(InferenceApiDbContext db, Guid projectId, DateTime now, CancellationToken ct, bool stampLibrary = true)
    {
        int moved = await db.Projects.IgnoreQueryFilters()
            .Where(p => p.Id == projectId && (p.CsamOutputHoldAt == null || p.CsamOutputHoldAt < now))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CsamOutputHoldAt, now), ct);
        if (stampLibrary && moved > 0) await Avatars.Library.AvatarLibraryHoldMarker.StampProjectSetBasedAsync(db, projectId, ct);
        return moved;
    }
}
