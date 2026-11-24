using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Data;

/// <summary>
/// The EF column configuration of <c>project_avatars</c> and <c>project_avatar_references</c>, shared by the Inference
/// API (which owns and migrates them) and the WorkflowEngine (which maps them read-only), so the two mappings cannot
/// drift. Each context adds its own <c>ToTable</c> (the API with its CHECK constraints, the engine with
/// <c>ExcludeFromMigrations()</c>).
/// </summary>
public static class ProjectAvatarModel
{
    public static void ConfigureAvatar(EntityTypeBuilder<ProjectAvatar> entity)
    {
        entity.HasKey(e => e.Id);
        entity.HasIndex(e => e.ProjectId);
        // Unique per project among live rows: a revoked avatar (kept up to 30 days before the sweep deletes it)
        // does not block re-creating one under the same name.
        entity.HasIndex(e => new { e.ProjectId, e.Name })
            .IsUnique()
            .HasFilter("consent_revoked_at IS NULL")
            .HasDatabaseName("ux_project_avatars_project_live_name");
        entity.HasOne<Project>()
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
        // SetNull: deleting the voice leaves the avatar without a linked voice; it does not delete the avatar.
        entity.HasOne<ProjectVoice>()
            .WithMany()
            .HasForeignKey(e => e.ProjectVoiceId)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasOne<ProjectFile>()
            .WithMany()
            .HasForeignKey(e => e.ConsentDocumentFileId)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasMany(e => e.References)
            .WithOne()
            .HasForeignKey(r => r.AvatarId)
            .HasConstraintName("FK_project_avatar_references_project_avatars_avatar_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Variants (docs/design/avatar-objects.md "Avatar variants"): a self reference. SetNull: deleting the original
        // leaves its variants, ungrouped. A label is unique per original among live (not revoked) rows.
        entity.HasOne<ProjectAvatar>()
            .WithMany()
            .HasForeignKey(e => e.VariantOfAvatarId)
            .HasConstraintName("FK_project_avatars_project_avatars_variant_of_avatar_id")
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasIndex(e => new { e.VariantOfAvatarId, e.VariantLabel })
            .IsUnique()
            .HasFilter("variant_of_avatar_id IS NOT NULL AND consent_revoked_at IS NULL")
            .HasDatabaseName("ux_project_avatars_variant_live_label");
        entity.Property(e => e.VariantLabel).HasMaxLength(ProjectAvatarLimits.MaxVariantLabelLength);
        entity.Property(e => e.VariantDescription).HasMaxLength(ProjectAvatarLimits.MaxVariantDescriptionLength);

        entity.Property(e => e.Name).HasMaxLength(ProjectAvatarLimits.MaxNameLength).IsRequired();
        entity.Property(e => e.Description).HasMaxLength(ProjectAvatarLimits.MaxDescriptionLength);
        entity.Property(e => e.IdentityFacts).HasMaxLength(ReelBolt.Shared.Avatars.AvatarIdentityFacts.MaxStoredChars);
        // Never null on a row (docs: "set together, never null on a live row"); nullable in C# only so the rule can
        // name a default-constructed object consent_missing instead of trusting DateTime.MinValue.
        entity.Property(e => e.ConsentAttestedByUserId).IsRequired();
        entity.Property(e => e.ConsentAttestedAt).IsRequired();
        entity.Property(e => e.ConsentStatement).HasMaxLength(ProjectAvatarLimits.MaxConsentStatementLength).IsRequired();
        entity.Property(e => e.ConsentStatementVersion)
            .HasMaxLength(ProjectAvatarLimits.MaxConsentStatementVersionLength).IsRequired();
        entity.Property(e => e.ConsentLocale).HasMaxLength(ProjectAvatarLimits.MaxConsentLocaleLength).IsRequired();
        entity.Property(e => e.ConsentSubjectName).HasMaxLength(ProjectAvatarLimits.MaxSubjectNameLength).IsRequired();
        // Person or Object (docs/design/avatar-objects.md), by name. The database default keeps a release that does not
        // know the column inserting people, which is what every row was before the column existed.
        entity.Property(e => e.SubjectKind)
            .HasConversion<string>()
            .HasMaxLength(50)
            .HasDefaultValue(AvatarSubjectKind.Person);
        entity.Property(e => e.ConsentBasis).HasConversion<string>().HasMaxLength(50);
        entity.Property(e => e.DisclosureLabel).HasConversion<string>().HasMaxLength(50);
        // PostgreSQL's xmin system column: no real column is created; a revoke and a re-attestation racing on the
        // same row are serialized by the conditional UPDATE EF emits.
        entity.Property(e => e.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }

    public static void ConfigureReference(EntityTypeBuilder<ProjectAvatarReference> entity)
    {
        entity.HasKey(e => e.Id);
        entity.HasIndex(e => new { e.AvatarId, e.Ordinal });
        // A file appears once per avatar.
        entity.HasIndex(e => new { e.AvatarId, e.ProjectFileId }).IsUnique();
        // The file-delete route's "in use by an avatar" lookup.
        entity.HasIndex(e => e.ProjectFileId);
        // NO ACTION, not RESTRICT: the reference holds its file against a standalone delete (the API refuses first
        // with 409 FILE_IN_USE_BY_AVATAR), but NO ACTION is checked at the end of the statement, so a project delete
        // that cascades to both the file and the avatar's references succeeds whatever order the cascades run in.
        entity.HasOne<ProjectFile>()
            .WithMany()
            .HasForeignKey(e => e.ProjectFileId)
            .OnDelete(DeleteBehavior.NoAction);
        entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(50);
        entity.Property(e => e.Role).HasConversion<string>().HasMaxLength(50);
        entity.Property(e => e.ContentSha256).HasMaxLength(64).IsRequired();
    }
}
