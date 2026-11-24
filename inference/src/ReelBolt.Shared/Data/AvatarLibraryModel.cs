using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Data;

/// <summary>
/// The EF column configuration of the workspace avatar library (<c>avatar_library_items</c>,
/// <c>avatar_library_references</c>) and of the link a project avatar carries to its item
/// (<c>project_avatars.library_item_id</c>), docs/design/avatar-objects.md "The workspace avatar library (global
/// avatars)". Only the Inference API maps these (it owns and migrates them); it adds <c>ToTable</c> with the CHECK
/// constraints and the named <c>Tenant</c> query filter on the item. The engine maps <c>project_avatars</c> through
/// <see cref="ProjectAvatarModel"/> alone: it reads <c>library_item_id</c> as a plain column and needs neither the
/// library tables nor the foreign key.
/// </summary>
public static class AvatarLibraryModel
{
    public static void ConfigureItem(EntityTypeBuilder<AvatarLibraryItem> entity)
    {
        entity.HasKey(e => e.Id);
        entity.HasIndex(e => new { e.OrganizationId, e.SubjectKind });
        // Unique per organization among live rows: a revoked item (kept up to 30 days before the sweep deletes it)
        // does not block saving one under the same name.
        entity.HasIndex(e => new { e.OrganizationId, e.Name })
            .IsUnique()
            .HasFilter("consent_revoked_at IS NULL")
            .HasDatabaseName("ux_avatar_library_items_org_live_name");
        // The retention sweep's two lookups.
        entity.HasIndex(e => e.RetentionUntil);
        entity.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasMany(e => e.References)
            .WithOne()
            .HasForeignKey(r => r.LibraryItemId)
            // Named: the conventional name is longer than PostgreSQL's 63 characters and would be cut.
            .HasConstraintName("fk_avatar_library_references_library_item")
            .OnDelete(DeleteBehavior.Cascade);

        // Variants (docs/design/avatar-objects.md "Avatar variants", "Library"): a self reference, as on project_avatars.
        // SetNull: deleting the original leaves its variants as items, ungrouped (their label stays). A label is unique
        // per original among live (not revoked) items; an ungrouped item's label is not.
        entity.HasOne<AvatarLibraryItem>()
            .WithMany()
            .HasForeignKey(e => e.VariantOfItemId)
            // Named: the conventional name is at PostgreSQL's 63-character limit.
            .HasConstraintName("fk_avatar_library_items_variant_of_item")
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasIndex(e => new { e.VariantOfItemId, e.VariantLabel })
            .IsUnique()
            .HasFilter("variant_of_item_id IS NOT NULL AND consent_revoked_at IS NULL")
            .HasDatabaseName("ux_avatar_library_items_variant_live_label");
        entity.Property(e => e.VariantLabel).HasMaxLength(ProjectAvatarLimits.MaxVariantLabelLength);
        entity.Property(e => e.VariantDescription).HasMaxLength(ProjectAvatarLimits.MaxVariantDescriptionLength);

        entity.Property(e => e.Name).HasMaxLength(ProjectAvatarLimits.MaxNameLength).IsRequired();
        entity.Property(e => e.Description).HasMaxLength(ProjectAvatarLimits.MaxDescriptionLength);
        entity.Property(e => e.ConsentStatement).HasMaxLength(ProjectAvatarLimits.MaxConsentStatementLength).IsRequired();
        entity.Property(e => e.ConsentStatementVersion)
            .HasMaxLength(ProjectAvatarLimits.MaxConsentStatementVersionLength).IsRequired();
        entity.Property(e => e.ConsentLocale).HasMaxLength(ProjectAvatarLimits.MaxConsentLocaleLength).IsRequired();
        entity.Property(e => e.ConsentSubjectName).HasMaxLength(ProjectAvatarLimits.MaxSubjectNameLength).IsRequired();
        entity.Property(e => e.ConsentDocumentStorageKey).HasMaxLength(AvatarLibraryLimits.MaxStorageKeyLength);
        entity.Property(e => e.SubjectKind).HasConversion<string>().HasMaxLength(50);
        entity.Property(e => e.ConsentBasis).HasConversion<string>().HasMaxLength(50);
        entity.Property(e => e.DisclosureLabel).HasConversion<string>().HasMaxLength(50);
        // PostgreSQL's xmin system column: no real column is created; a revoke and a reconfirmation racing on the
        // same item are serialized by the conditional UPDATE EF emits.
        entity.Property(e => e.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }

    public static void ConfigureReference(EntityTypeBuilder<AvatarLibraryReference> entity)
    {
        entity.HasKey(e => e.Id);
        entity.HasIndex(e => new { e.LibraryItemId, e.Ordinal });
        entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(50);
        entity.Property(e => e.Role).HasConversion<string>().HasMaxLength(50);
        entity.Property(e => e.ContentSha256).HasMaxLength(64).IsRequired();
        entity.Property(e => e.StorageKey).HasMaxLength(AvatarLibraryLimits.MaxStorageKeyLength).IsRequired();
        entity.Property(e => e.MimeType).HasMaxLength(AvatarLibraryLimits.MaxMimeTypeLength).IsRequired();
        entity.Property(e => e.FileName).HasMaxLength(AvatarLibraryLimits.MaxFileNameLength).IsRequired();
        entity.Property(e => e.HeldReason).HasMaxLength(32);
    }

    /// <summary>
    /// The link from a project avatar to the library item it is an import of. <c>NO ACTION</c>: the library delete
    /// routine removes the imports first (each through the project avatar delete routine, so their copied files go
    /// too), and an item that still has an import cannot be deleted from under it. One import of an item per project.
    /// </summary>
    public static void ConfigureImportLink(EntityTypeBuilder<ProjectAvatar> entity)
    {
        entity.HasOne<AvatarLibraryItem>()
            .WithMany()
            .HasForeignKey(e => e.LibraryItemId)
            .OnDelete(DeleteBehavior.NoAction);
        entity.HasIndex(e => new { e.ProjectId, e.LibraryItemId })
            .IsUnique()
            .HasDatabaseName("ux_project_avatars_project_library_item");
    }
}
