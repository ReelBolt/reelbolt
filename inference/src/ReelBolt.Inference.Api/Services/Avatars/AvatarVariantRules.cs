using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Avatars;

/// <summary>What a create says about being a variant: the original it belongs to, its label and what sets it apart.</summary>
public sealed record AvatarVariantAsk(Guid? VariantOfAvatarId, string? VariantLabel, string? VariantDescription)
{
    /// <summary>Whether the request says anything about a variant at all.</summary>
    public bool Any => VariantOfAvatarId is not null || VariantLabel is not null || VariantDescription is not null;
}

/// <summary>A variant that passed the rules: its original (never itself a variant), the trimmed label and description.</summary>
public sealed record AvatarVariantPlacement(ProjectAvatar Original, string Label, string Description);

/// <summary>
/// The rules of an avatar VARIANT (docs/design/avatar-objects.md "Avatar variants", decision AO-8), in one place so the
/// REST create, the edit and the build job cannot disagree:
/// <list type="bullet">
/// <item>One level only: a variant of a variant attaches to the original (<see cref="FindOriginalAsync"/>).</item>
/// <item>The original is an avatar of the SAME project; a variant has the kind of its original (the caller compares).</item>
/// <item>A variant needs a label (1..<see cref="ProjectAvatarLimits.MaxVariantLabelLength"/>) and a description
/// (1..<see cref="ProjectAvatarLimits.MaxVariantDescriptionLength"/>).</item>
/// <item>An avatar has at most <see cref="ProjectAvatarLimits.MaxVariantsPerAvatar"/> variants
/// (<c>409 VARIANT_LIMIT_REACHED</c>) and a label is used once per original among live rows
/// (<c>409 VARIANT_LABEL_TAKEN</c>).</item>
/// </list>
/// Variants are not counted toward the per-kind limit of the project; that count simply leaves them out.
/// </summary>
public static class AvatarVariantRules
{
    public const string OriginalNotFoundCode = "variant_original_not_found";
    public const string KindMismatchCode = "variant_kind_mismatch";
    public const string LabelInvalidCode = "variant_label_invalid";
    public const string DescriptionInvalidCode = "variant_description_invalid";
    public const string FieldsWithoutOriginalCode = "variant_fields_without_original";
    public const string NotAVariantCode = "not_a_variant";
    public const string SubjectMismatchCode = "variant_subject_mismatch";
    public const string LimitReachedCode = "VARIANT_LIMIT_REACHED";
    public const string LabelTakenCode = "VARIANT_LABEL_TAKEN";

    /// <summary>
    /// The avatar a new variant attaches to when the request names <paramref name="avatarId"/>: that avatar, or, when it
    /// is itself a variant, ITS original (one level only). Null when the project has no such avatar or its kind is
    /// switched off. Tracked, so the caller can read it again in the same unit of work.
    /// </summary>
    public static async Task<ProjectAvatar?> FindOriginalAsync(
        InferenceApiDbContext db, AvatarOptions options, Guid projectId, Guid avatarId, CancellationToken ct)
    {
        ProjectAvatar? named = await db.ProjectAvatars.FirstOrDefaultAsync(a => a.Id == avatarId && a.ProjectId == projectId, ct);
        if (named is null || !options.IsEnabled(named.SubjectKind)) return null;
        if (named.VariantOfAvatarId is not { } rootId) return named;

        ProjectAvatar? root = await db.ProjectAvatars.FirstOrDefaultAsync(a => a.Id == rootId && a.ProjectId == projectId, ct);
        // The original is gone (its variants stay, ungrouped): the named avatar is then an original itself.
        return root ?? named;
    }

    /// <summary>The refusal for a request that names no usable original.</summary>
    public static AvatarRefusal OriginalNotFound() =>
        AvatarRefusal.BadRequest(OriginalNotFoundCode, "A variant must belong to an avatar of this project.");

    /// <summary>The refusal for variant fields sent without an original.</summary>
    public static AvatarRefusal FieldsWithoutOriginal() =>
        AvatarRefusal.BadRequest(FieldsWithoutOriginalCode,
            "A variant label and description belong to a variant. Say which avatar it is a variant of.");

    /// <summary>The refusal for a variant whose kind is not its original's.</summary>
    public static AvatarRefusal KindMismatch(ProjectAvatar original) =>
        AvatarRefusal.BadRequest(KindMismatchCode,
            original.SubjectKind == AvatarSubjectKind.Object
                ? "A variant of an object is an object."
                : "A variant of a person is a person.");

    /// <summary>The trimmed label and description of a variant, or the first refusal.</summary>
    public static (string Label, string Description, AvatarRefusal? Refusal) Validate(string? label, string? description)
    {
        string trimmedLabel = label?.Trim() ?? string.Empty;
        if (trimmedLabel.Length == 0 || trimmedLabel.Length > ProjectAvatarLimits.MaxVariantLabelLength || trimmedLabel.Any(char.IsControl))
        {
            return (string.Empty, string.Empty, AvatarRefusal.BadRequest(LabelInvalidCode,
                $"Give the variant a short label of at most {ProjectAvatarLimits.MaxVariantLabelLength} characters, for example \"Red\"."));
        }

        string trimmedDescription = description?.Trim() ?? string.Empty;
        if (trimmedDescription.Length == 0 || trimmedDescription.Length > ProjectAvatarLimits.MaxVariantDescriptionLength)
        {
            return (string.Empty, string.Empty, AvatarRefusal.BadRequest(DescriptionInvalidCode,
                $"Say what sets this variant apart in at most {ProjectAvatarLimits.MaxVariantDescriptionLength} characters, for example \"matte red paint, black rims\"."));
        }

        return (trimmedLabel, trimmedDescription, null);
    }

    /// <summary>How many variants <paramref name="originalId"/> has now.</summary>
    public static Task<int> CountAsync(InferenceApiDbContext db, Guid originalId, CancellationToken ct) =>
        db.ProjectAvatars.CountAsync(a => a.VariantOfAvatarId == originalId, ct);

    /// <summary><c>409 VARIANT_LIMIT_REACHED</c> when the original already has every variant it may have; else null.</summary>
    public static async Task<AvatarRefusal?> LimitRefusalAsync(InferenceApiDbContext db, Guid originalId, CancellationToken ct) =>
        await CountAsync(db, originalId, ct) >= ProjectAvatarLimits.MaxVariantsPerAvatar
            ? AvatarRefusal.Conflict(LimitReachedCode,
                $"An avatar can have at most {ProjectAvatarLimits.MaxVariantsPerAvatar} variants. Delete one first.")
            : null;

    /// <summary>Whether a live variant of <paramref name="originalId"/> other than <paramref name="except"/> already uses <paramref name="label"/>.</summary>
    public static Task<bool> LabelTakenAsync(InferenceApiDbContext db, Guid originalId, string label, Guid? except, CancellationToken ct) =>
        db.ProjectAvatars.AnyAsync(a => a.VariantOfAvatarId == originalId && a.ConsentRevokedAt == null && a.VariantLabel == label
                                        && (except == null || a.Id != except), ct);

    /// <summary>
    /// Withdrawing a PERSON withdraws every variant of that person (a variant shows the same person under the same
    /// consent): stamps each live variant of <paramref name="original"/> with the original's revoke time and actor, and
    /// writes a <c>ConsentRevoked</c> event for each, in the caller's unit of work (nothing is saved here). Safe to call
    /// again; returns how many variants it stamped. Any path that revokes a person avatar calls this.
    /// </summary>
    public static async Task<int> RevokeVariantsAsync(
        InferenceApiDbContext db, ProjectAvatar original, Guid organizationId, Guid? actorUserId, string via, CancellationToken ct)
    {
        if (original.SubjectKind != AvatarSubjectKind.Person || original.ConsentRevokedAt is not { } stamp) return 0;
        List<ProjectAvatar> variants = await db.ProjectAvatars
            .Where(a => a.VariantOfAvatarId == original.Id && a.SubjectKind == AvatarSubjectKind.Person && a.ConsentRevokedAt == null)
            .ToListAsync(ct);
        foreach (ProjectAvatar variant in variants)
        {
            variant.ConsentRevokedAt = stamp;
            variant.ConsentRevokedByUserId = actorUserId;
            variant.UpdatedAt = stamp;
            ProjectAvatarEvents.Add(db, variant, organizationId, AvatarEventKind.ConsentRevoked, actorUserId,
                new Dictionary<string, object> { ["via"] = via, ["original_revoked"] = true });
        }

        return variants.Count;
    }

    /// <summary>The refusal for a label another variant of the same original uses.</summary>
    public static AvatarRefusal LabelTaken(string label) =>
        AvatarRefusal.Conflict(LabelTakenCode, $"This avatar already has a variant labelled \"{label}\".");
}
