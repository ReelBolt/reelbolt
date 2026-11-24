using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Avatars.Library;

/// <summary>
/// Variants in the workspace library (docs/design/avatar-objects.md "Avatar variants", "Library"): an item may name
/// the item it is a variant of (<see cref="AvatarLibraryItem.VariantOfItemId"/>) with a label and a description, the
/// library twin of a project variant. The rules are the project's, held at the item: one level only (a variant of a
/// variant attaches to the original), the kind of the original, at most <see cref="AvatarLibraryLimits.MaxVariantsPerItem"/>
/// variants per original and a label used once per original among live items.
/// <list type="bullet">
/// <item><b>Publish.</b> A project variant whose original is linked to a library item becomes a variant of that item;
/// publishing an original links the items its project variants were already published as.</item>
/// <item><b>Import.</b> A variant item imported into a project that holds an import of its original is grouped under
/// that import (the project's own variant rules apply and refuse in their own codes); importing an original groups
/// the imports of its variants the project already holds.</item>
/// <item><b>Delete.</b> Deleting an original leaves its variants as items, ungrouped (the foreign key is
/// <c>ON DELETE SET NULL</c>, and the delete routine says so in the same save).</item>
/// </list>
/// </summary>
public sealed partial class AvatarLibraryService
{
    public const string LibraryVariantLimitCode = "LIBRARY_VARIANT_LIMIT_REACHED";
    public const string LibraryVariantLabelTakenCode = "LIBRARY_VARIANT_LABEL_TAKEN";

    /// <summary>
    /// What a publish writes about being a variant: the item it becomes a variant of (null when the project original is
    /// not in the library, or there is no original any more) and the label and description it carries either way.
    /// </summary>
    private sealed record LibraryVariantPlan(Guid? OriginalItemId, string Label, string Description);

    /// <summary>Where an import is grouped in its project: under the import of the item's original, with the item's label.</summary>
    private sealed record ImportGrouping(Guid OriginalAvatarId, string Label, string Description);

    // ------------------------------------------------------------------ publish

    /// <summary>
    /// The variant fields of the item <paramref name="avatar"/> is published as: null for an avatar that carries no
    /// valid label and description (an original); otherwise its label and description, and the library item its
    /// project original is linked to (or that item's own original: one level only), when there is one of the same kind.
    /// </summary>
    private async Task<LibraryVariantPlan?> PublishVariantPlanAsync(Project project, ProjectAvatar avatar, CancellationToken ct)
    {
        if (avatar.VariantOfAvatarId is null && avatar.VariantLabel is null) return null;
        (string label, string description, AvatarRefusal? invalid) = AvatarVariantRules.Validate(avatar.VariantLabel, avatar.VariantDescription);
        if (invalid is not null) return null;

        Guid? originalItemId = null;
        if (avatar.VariantOfAvatarId is { } originalId)
        {
            Guid? linked = await _db.ProjectAvatars.AsNoTracking()
                .Where(a => a.Id == originalId && a.ProjectId == project.Id && a.SubjectKind == avatar.SubjectKind)
                .Select(a => a.LibraryItemId)
                .FirstOrDefaultAsync(ct);
            if (linked is { } itemId) originalItemId = await LibraryOriginalOfAsync(project.OrganizationId, itemId, avatar.SubjectKind, ct);
        }

        return new LibraryVariantPlan(originalItemId, label, description);
    }

    /// <summary>
    /// The item a variant of item <paramref name="itemId"/> attaches to: that item, or its original when it is itself a
    /// variant (one level only). Null when there is no such item of <paramref name="kind"/> in the organization.
    /// </summary>
    private async Task<Guid?> LibraryOriginalOfAsync(Guid organizationId, Guid itemId, AvatarSubjectKind kind, CancellationToken ct)
    {
        var named = await _db.AvatarLibraryItems.AsNoTracking()
            .Where(i => i.Id == itemId && i.OrganizationId == organizationId)
            .Select(i => new { i.Id, i.SubjectKind, i.VariantOfItemId })
            .FirstOrDefaultAsync(ct);
        if (named is null || named.SubjectKind != kind) return null;
        if (named.VariantOfItemId is not { } rootId) return named.Id;

        var root = await _db.AvatarLibraryItems.AsNoTracking()
            .Where(i => i.Id == rootId && i.OrganizationId == organizationId)
            .Select(i => new { i.Id, i.SubjectKind, i.VariantOfItemId })
            .FirstOrDefaultAsync(ct);
        return root is not null && root.SubjectKind == kind && root.VariantOfItemId is null ? root.Id : null;
    }

    /// <summary>
    /// The library's variant rules for a new variant of <paramref name="originalItemId"/>: at most
    /// <see cref="AvatarLibraryLimits.MaxVariantsPerItem"/> (<c>409 LIBRARY_VARIANT_LIMIT_REACHED</c>) and a label no
    /// live variant of the same original uses (<c>409 LIBRARY_VARIANT_LABEL_TAKEN</c>).
    /// </summary>
    private async Task<AvatarRefusal?> LibraryVariantRefusalAsync(Guid organizationId, Guid originalItemId, string label, CancellationToken ct)
    {
        if (await _db.AvatarLibraryItems.CountAsync(i => i.OrganizationId == organizationId && i.VariantOfItemId == originalItemId, ct)
            >= AvatarLibraryLimits.MaxVariantsPerItem)
        {
            return AvatarRefusal.Conflict(LibraryVariantLimitCode,
                $"Its original in the workspace library already has {AvatarLibraryLimits.MaxVariantsPerItem} variants, the most it can have. Delete one from the library first.");
        }

        return await LibraryLabelTakenAsync(organizationId, originalItemId, label, null, ct) ? LibraryLabelTaken(label) : null;
    }

    private Task<bool> LibraryLabelTakenAsync(Guid organizationId, Guid originalItemId, string label, Guid? except, CancellationToken ct) =>
        _db.AvatarLibraryItems.AnyAsync(i => i.OrganizationId == organizationId && i.VariantOfItemId == originalItemId
                                             && i.ConsentRevokedAt == null && i.VariantLabel == label
                                             && (except == null || i.Id != except), ct);

    private static AvatarRefusal LibraryLabelTaken(string label) =>
        AvatarRefusal.Conflict(LibraryVariantLabelTakenCode,
            $"Its original in the workspace library already has a variant labelled \"{label}\". Change this variant's label, then try again.");

    /// <summary>
    /// When ORIGINAL <paramref name="original"/> is published as <paramref name="item"/>: the items its project variants
    /// were already published as become variants of the new item (tracked; written by the caller's save). An item that
    /// is already grouped, has variants of its own (one level only), carries no valid label, would exceed the limit or
    /// repeat a live label is left ungrouped and counted in <c>Skipped</c>, never dropped.
    /// </summary>
    private async Task<(int Linked, int Skipped)> LinkPublishedVariantsAsync(
        Project project, ProjectAvatar original, AvatarLibraryItem item, DateTime now, CancellationToken ct)
    {
        var variants = await _db.ProjectAvatars.AsNoTracking()
            .Where(a => a.ProjectId == project.Id && a.VariantOfAvatarId == original.Id && a.LibraryItemId != null)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new { ItemId = a.LibraryItemId!.Value, a.VariantLabel, a.VariantDescription })
            .Take(ProjectAvatarLimits.MaxVariantsPerAvatar * 2)
            .ToListAsync(ct);
        if (variants.Count == 0) return (0, 0);

        Guid organizationId = item.OrganizationId;
        Guid[] itemIds = [.. variants.Select(v => v.ItemId).Distinct()];
        Dictionary<Guid, AvatarLibraryItem> candidates = await _db.AvatarLibraryItems
            .Where(i => i.OrganizationId == organizationId && itemIds.Contains(i.Id) && i.Id != item.Id)
            .ToDictionaryAsync(i => i.Id, ct);
        HashSet<Guid> withVariants = [.. await _db.AvatarLibraryItems.AsNoTracking()
            .Where(i => i.OrganizationId == organizationId && i.VariantOfItemId != null && itemIds.Contains(i.VariantOfItemId.Value))
            .Select(i => i.VariantOfItemId!.Value)
            .ToListAsync(ct)];

        HashSet<string> liveLabels = new(StringComparer.Ordinal);
        int linked = 0, skipped = 0;
        foreach (var variant in variants)
        {
            if (!candidates.TryGetValue(variant.ItemId, out AvatarLibraryItem? candidate)) continue;
            if (candidate.VariantOfItemId is not null) continue; // already a variant of an item: nothing to link
            (string label, string description, AvatarRefusal? invalid) = AvatarVariantRules.Validate(
                candidate.VariantLabel ?? variant.VariantLabel, candidate.VariantDescription ?? variant.VariantDescription);
            if (invalid is not null || candidate.SubjectKind != item.SubjectKind || withVariants.Contains(candidate.Id)
                || linked >= AvatarLibraryLimits.MaxVariantsPerItem
                || (candidate.ConsentRevokedAt is null && !liveLabels.Add(label)))
            {
                skipped++;
                continue;
            }

            candidate.VariantOfItemId = item.Id;
            candidate.VariantLabel = label;
            candidate.VariantDescription = description;
            candidate.UpdatedAt = now;
            linked++;
        }

        if (skipped > 0)
        {
            _logger.LogWarning(
                "Published avatar library item {ItemId}: {Skipped} item(s) published from its project variants could not be grouped under it and stay ungrouped",
                item.Id, skipped);
        }

        return (linked, skipped);
    }

    // ------------------------------------------------------------------ import

    /// <summary>
    /// Where an import of <paramref name="item"/> goes in <paramref name="project"/>: grouped under the project's import
    /// of the item's original (or that import's own original: one level only) when the item is a variant with a valid
    /// label and the project holds such an import of the same kind; else null (an ordinary avatar of the project).
    /// </summary>
    private async Task<ImportGrouping?> ImportGroupingAsync(Project project, AvatarLibraryItem item, CancellationToken ct)
    {
        if (item.VariantOfItemId is not { } originalItemId) return null;
        (string label, string description, AvatarRefusal? invalid) = AvatarVariantRules.Validate(item.VariantLabel, item.VariantDescription);
        if (invalid is not null) return null;

        ProjectAvatar? original = await _db.ProjectAvatars.AsNoTracking()
            .FirstOrDefaultAsync(a => a.ProjectId == project.Id && a.LibraryItemId == originalItemId, ct);
        if (original?.VariantOfAvatarId is { } rootId)
        {
            ProjectAvatar? root = await _db.ProjectAvatars.AsNoTracking().FirstOrDefaultAsync(a => a.Id == rootId && a.ProjectId == project.Id, ct);
            if (root is not null) original = root;
        }

        return original is not null && original.SubjectKind == item.SubjectKind
            ? new ImportGrouping(original.Id, label, description)
            : null;
    }

    /// <summary>
    /// The variant fields an import of <paramref name="item"/> takes from it: the label and description when the item
    /// carries valid ones (kept on an import that is not grouped, as on a project variant whose original was deleted),
    /// and the original it is grouped under.
    /// </summary>
    private static void ApplyVariantFields(AvatarLibraryItem item, ImportGrouping? grouping, ProjectAvatar import)
    {
        (string label, string description, AvatarRefusal? invalid) = AvatarVariantRules.Validate(item.VariantLabel, item.VariantDescription);
        import.VariantOfAvatarId = invalid is null ? grouping?.OriginalAvatarId : null;
        import.VariantLabel = invalid is null ? label : null;
        import.VariantDescription = invalid is null ? description : null;
    }

    /// <summary>
    /// When ORIGINAL item <paramref name="item"/> is imported into <paramref name="project"/> as
    /// <paramref name="import"/>: the project's ungrouped imports of its variants are grouped under the new import
    /// (tracked; written by the caller's save), under the project's variant rules. An import that has variants of its
    /// own (one level only), carries no valid label, would exceed the limit or repeat a live label stays ungrouped and is
    /// counted in <c>NotGrouped</c>, never dropped.
    /// </summary>
    private async Task<(int Grouped, int NotGrouped)> GroupImportedVariantsAsync(
        Project project, AvatarLibraryItem item, ProjectAvatar import, DateTime now, CancellationToken ct)
    {
        Dictionary<Guid, AvatarLibraryItem> variantItems = await _db.AvatarLibraryItems.AsNoTracking()
            .Where(i => i.OrganizationId == item.OrganizationId && i.VariantOfItemId == item.Id)
            .Take(AvatarLibraryLimits.MaxVariantsPerItem * 2)
            .ToDictionaryAsync(i => i.Id, ct);
        if (variantItems.Count == 0) return (0, 0);

        Guid[] variantItemIds = [.. variantItems.Keys];
        List<ProjectAvatar> candidates = await _db.ProjectAvatars
            .Where(a => a.ProjectId == project.Id && a.VariantOfAvatarId == null && a.LibraryItemId != null
                        && variantItemIds.Contains(a.LibraryItemId.Value))
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);
        if (candidates.Count == 0) return (0, 0);

        Guid[] candidateIds = [.. candidates.Select(a => a.Id)];
        HashSet<Guid> withVariants = [.. await _db.ProjectAvatars.AsNoTracking()
            .Where(a => a.ProjectId == project.Id && a.VariantOfAvatarId != null && candidateIds.Contains(a.VariantOfAvatarId.Value))
            .Select(a => a.VariantOfAvatarId!.Value)
            .ToListAsync(ct)];

        HashSet<string> liveLabels = new(StringComparer.Ordinal);
        int grouped = 0, notGrouped = 0;
        foreach (ProjectAvatar candidate in candidates)
        {
            AvatarLibraryItem variantItem = variantItems[candidate.LibraryItemId!.Value];
            (string label, string description, AvatarRefusal? invalid) = AvatarVariantRules.Validate(variantItem.VariantLabel, variantItem.VariantDescription);
            if (invalid is not null || candidate.SubjectKind != import.SubjectKind || withVariants.Contains(candidate.Id)
                || grouped >= ProjectAvatarLimits.MaxVariantsPerAvatar
                || (candidate.ConsentRevokedAt is null && !liveLabels.Add(label)))
            {
                notGrouped++;
                continue;
            }

            candidate.VariantOfAvatarId = import.Id;
            candidate.VariantLabel = label;
            candidate.VariantDescription = description;
            candidate.UpdatedAt = now;
            grouped++;
        }

        if (notGrouped > 0)
        {
            _logger.LogWarning(
                "Imported avatar library item {ItemId} into project {ProjectId}: {NotGrouped} import(s) of its variants could not be grouped under it and stay ungrouped",
                item.Id, project.Id, notGrouped);
        }

        return (grouped, notGrouped);
    }

    // ------------------------------------------------------------------ delete

    /// <summary>
    /// Ungroups the variants of item <paramref name="itemId"/> before it is deleted (tracked; written by the caller's
    /// save): they stay as items with their labels, exactly what the foreign key's <c>ON DELETE SET NULL</c> does,
    /// written here so the change is part of the delete's own unit of work. Answers how many were ungrouped.
    /// </summary>
    private async Task<int> UngroupVariantsAsync(Guid organizationId, Guid itemId, DateTime now, CancellationToken ct)
    {
        List<AvatarLibraryItem> variants = await _db.AvatarLibraryItems
            .Where(i => i.OrganizationId == organizationId && i.VariantOfItemId == itemId)
            .ToListAsync(ct);
        foreach (AvatarLibraryItem variant in variants)
        {
            variant.VariantOfItemId = null;
            variant.UpdatedAt = now;
        }

        return variants.Count;
    }
}
