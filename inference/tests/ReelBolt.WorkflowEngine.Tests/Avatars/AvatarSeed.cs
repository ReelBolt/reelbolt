#nullable enable
using System;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>Writes an avatar that lists <c>files</c> straight into a context, for the tests of paths that delete files.</summary>
internal static class AvatarSeed
{
    public static ProjectAvatar Attach(DbContext db, Guid projectId, string name, params ProjectFile[] files)
    {
        var avatar = new ProjectAvatar
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = name,
            ConsentAttestedByUserId = Guid.NewGuid(),
            ConsentAttestedAt = DateTime.UtcNow,
            ConsentStatement = ReelBolt.Shared.Avatars.AvatarConsentStatement.English,
            ConsentStatementVersion = ReelBolt.Shared.Avatars.AvatarConsentStatement.Version,
            ConsentLocale = "en",
            ConsentSubjectName = "Subject",
            ConsentSubjectAdult = true,
            RetentionUntil = DateTime.UtcNow.AddDays(30)
        };
        db.Add(avatar);
        int ordinal = 0;
        foreach (ProjectFile file in files)
        {
            db.Add(new ProjectAvatarReference
            {
                Id = Guid.NewGuid(),
                AvatarId = avatar.Id,
                ProjectFileId = file.Id,
                Kind = AvatarReferenceKind.Image,
                Role = AvatarReferenceRole.Face,
                Ordinal = ordinal++,
                ContentSha256 = new string('a', 64)
            });
        }

        db.SaveChanges();
        return avatar;
    }
}
