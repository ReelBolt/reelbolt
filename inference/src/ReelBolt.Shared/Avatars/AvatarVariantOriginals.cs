using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Avatars;

/// <summary>
/// Reads the originals of the person variants among some avatars, for the one selectability rule
/// (<see cref="ProjectAvatar.UnselectableReason(DateTime, IReadOnlyCollection{AvatarReferenceState}, IReadOnlyDictionary{Guid, ProjectAvatar}?)"/>).
/// The Inference API and the workflow engine both call it, so a variant of a withdrawn, expired or deleted person is
/// refused by every reader the same way.
/// </summary>
public static class AvatarVariantOriginals
{
    /// <summary>
    /// The originals (by id, untracked) of the person variants in <paramref name="avatars"/>, read fresh. Empty when none
    /// is a person variant (no query is made). An original that no longer exists is simply absent from the answer, which
    /// the rule reads as "missing".
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, ProjectAvatar>> LoadAsync(
        DbContext db, Guid projectId, IEnumerable<ProjectAvatar> avatars, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(avatars);
        Guid[] ids = [.. avatars
            .Where(a => a.SubjectKind == AvatarSubjectKind.Person && a.VariantOfAvatarId is not null)
            .Select(a => a.VariantOfAvatarId!.Value)
            .Distinct()];
        if (ids.Length == 0) return new Dictionary<Guid, ProjectAvatar>();
        return await db.Set<ProjectAvatar>().AsNoTracking()
            .Where(a => a.ProjectId == projectId && ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);
    }
}
