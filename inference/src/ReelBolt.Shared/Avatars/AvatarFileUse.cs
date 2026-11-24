using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Avatars;

/// <summary>
/// Which project files are avatar references (docs/design/avatars.md "References are project files, linked, not
/// copied"). <c>project_avatar_references.project_file_id</c> is <c>ON DELETE NO ACTION</c>, so a path that deletes or
/// replaces a project file must ask here first: a user-facing delete answers <c>409 FILE_IN_USE_BY_AVATAR</c> naming the
/// avatar, and a background path (retention, sync, superseded renders) keeps the file and logs it. Shared by the
/// Inference API and the WorkflowEngine (both map <c>project_avatar_references</c> and <c>project_avatars</c>).
/// </summary>
public static class AvatarFileUse
{
    /// <summary>The machine-readable code of a delete or replace refused because an avatar holds the file.</summary>
    public const string InUseCode = "FILE_IN_USE_BY_AVATAR";

    /// <summary>The subset of <paramref name="fileIds"/> that at least one avatar of any project lists as a reference.</summary>
    public static async Task<HashSet<Guid>> ReferencedFileIdsAsync(
        DbContext db, IReadOnlyCollection<Guid> fileIds, CancellationToken ct)
    {
        if (fileIds.Count == 0) return [];
        Guid[] ids = [.. fileIds.Distinct()];
        List<Guid> referenced = await db.Set<ProjectAvatarReference>().AsNoTracking()
            .Where(r => ids.Contains(r.ProjectFileId))
            .Select(r => r.ProjectFileId)
            .Distinct()
            .ToListAsync(ct);
        return [.. referenced];
    }

    /// <summary>
    /// The first avatar of <paramref name="projectId"/> (by name) that lists one of <paramref name="fileIds"/>, with
    /// how many avatars hold any of them; null when none does.
    /// </summary>
    public static async Task<AvatarFileUseConflict?> FirstUseAsync(
        DbContext db, Guid projectId, IReadOnlyCollection<Guid> fileIds, CancellationToken ct)
    {
        if (fileIds.Count == 0) return null;
        Guid[] ids = [.. fileIds.Distinct()];
        var uses = await db.Set<ProjectAvatarReference>().AsNoTracking()
            .Where(r => ids.Contains(r.ProjectFileId))
            .Join(db.Set<ProjectAvatar>().AsNoTracking().Where(a => a.ProjectId == projectId),
                r => r.AvatarId, a => a.Id, (r, a) => new { a.Id, a.Name, r.ProjectFileId })
            .ToListAsync(ct);
        if (uses.Count == 0) return null;

        var first = uses.OrderBy(u => u.Name, StringComparer.Ordinal).ThenBy(u => u.Id).First();
        return new AvatarFileUseConflict(first.Id, first.Name, first.ProjectFileId, uses.Select(u => u.Id).Distinct().Count());
    }
}

/// <summary>A file delete or replace refused because an avatar lists the file as a reference.</summary>
/// <param name="AvatarId">The avatar to open (the web links to it).</param>
/// <param name="AvatarName">Its display name, named in the message.</param>
/// <param name="FileId">The file that is in use.</param>
/// <param name="Avatars">How many avatars hold any of the files asked about.</param>
public sealed record AvatarFileUseConflict(Guid AvatarId, string AvatarName, Guid FileId, int Avatars)
{
    /// <summary>The sentence shown to the user.</summary>
    public string Message => Avatars > 1
        ? $"This file is a reference of the avatar \"{AvatarName}\" and of {Avatars - 1} other avatar(s). Remove it from those avatars, or delete them, first."
        : $"This file is a reference of the avatar \"{AvatarName}\". Remove it from the avatar, or delete the avatar, first.";

    /// <summary>The JSON body of the <c>409</c>: <c>{ code, message, avatarId, avatarName, fileId, avatars }</c>.</summary>
    public object ToBody() => new
    {
        code = AvatarFileUse.InUseCode,
        message = Message,
        avatarId = AvatarId,
        avatarName = AvatarName,
        fileId = FileId,
        avatars = Avatars
    };
}
