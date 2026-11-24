using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Avatars.Library;

/// <summary>
/// A project that has an avatar saved to its organization's library cannot move to another organization.
/// A library copy follows the verdict of the file it came from (see docs/csam.md), but once the project belongs to
/// another organization a later verdict stamps only that organization's copies and the digest rule in the old
/// organization no longer sees the project's files, so the old library's copy would stay unheld. Refusing the move
/// until the copy is removed keeps the verdict able to reach it. A failed read throws, so the move fails closed.
/// </summary>
internal static class AvatarLibraryTransferGuard
{
    public const string RefusalCode = "PROJECT_HAS_LIBRARY_AVATAR";

    public const string RefusalMessage =
        "This project has an avatar saved to your workspace library. Remove that avatar from the workspace library, then move the project.";

    /// <summary>True when the source organization's library holds a reference whose digest equals one of the project's files or avatar references.</summary>
    public static async Task<bool> HasLibraryCopyAsync(DbContext db, Guid projectId, Guid organizationId, CancellationToken ct)
    {
        IQueryable<string> fileDigests = db.Set<ProjectFile>().IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.ProjectId == projectId && f.ContentSha256 != null && f.ContentSha256 != "")
            .Select(f => f.ContentSha256!);
        IQueryable<string> referenceDigests = db.Set<ProjectAvatarReference>().IgnoreQueryFilters().AsNoTracking()
            .Where(r => db.Set<ProjectFile>().IgnoreQueryFilters().Any(f => f.Id == r.ProjectFileId && f.ProjectId == projectId))
            .Select(r => r.ContentSha256);
        string[] digests = [.. (await fileDigests.Concat(referenceDigests).ToListAsync(ct)).Select(d => d.ToLowerInvariant()).Distinct()];
        if (digests.Length == 0) return false;

        List<string> libraryDigests = await db.Set<AvatarLibraryReference>().IgnoreQueryFilters().AsNoTracking()
            .Where(r => db.Set<AvatarLibraryItem>().IgnoreQueryFilters().Any(i => i.Id == r.LibraryItemId && i.OrganizationId == organizationId))
            .Select(r => r.ContentSha256)
            .ToListAsync(ct);
        return libraryDigests.Any(d => digests.Contains(d.ToLowerInvariant()));
    }
}
