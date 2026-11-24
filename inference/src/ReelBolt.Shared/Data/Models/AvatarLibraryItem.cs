namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One item of a workspace's avatar library (docs/design/avatar-objects.md "The workspace avatar library (global
/// avatars)", decisions AO-5 .. AO-7): the MASTER record of a person or an object that every project of ONE
/// organization may import. The item holds its own copies of the media in organization storage
/// (<see cref="AvatarLibraryStorage"/>); an import is an ordinary <see cref="ProjectAvatar"/> whose
/// <see cref="ProjectAvatar.LibraryItemId"/> names the item, plus ordinary project files copied from it.
/// Owned by the Inference API (<c>avatar_library_items</c>, <c>__EFMigrationsHistory_Api</c>); the engine does not map it.
///
/// <para>
/// <b>Tenancy.</b> A library item is addressable by a caller-chosen id and owned by one organization, so the table
/// carries the named <c>"Tenant"</c> query filter (TenantBoundaryInventoryTests): another organization's item does not
/// exist for the caller, which is why every route answers <c>404</c> for it. There is no platform stock library.
/// </para>
/// <para>
/// <b>Consent.</b> The same columns as <see cref="ProjectAvatar"/>, attested under the WORKSPACE statements
/// (<c>AvatarWorkspaceConsentStatement</c> for a person, <c>AvatarWorkspaceObjectStatement</c> for an object). An
/// object reuses them for its own, lighter attestation exactly as an object avatar does: empty subject name, basis
/// <c>Self</c>, adult flag false, <see cref="RetentionUntil"/> = <see cref="ProjectAvatar.NoRetention"/>, never revoked.
/// </para>
/// </summary>
public class AvatarLibraryItem
{
    public Guid Id { get; set; }

    /// <summary>The one organization the item belongs to. Never changes.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>A person or an object. Set at publish and never changed.</summary>
    public AvatarSubjectKind SubjectKind { get; set; } = AvatarSubjectKind.Person;

    /// <summary>Display name, 1..200, unique per organization among live (not revoked) items. Imports follow it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Plain text shown to members; imports follow it.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// The library item this one is a VARIANT of (docs/design/avatar-objects.md "Avatar variants", "Library"): the same
    /// car in another colour, saved for the whole workspace. Null for an original, and for a variant whose original is
    /// not (or no longer) in the library. One level only: it always names an original. Same organization and same
    /// <see cref="SubjectKind"/> as the original (checked in code). Set null when the original is deleted, so its
    /// variants stay as items, ungrouped, exactly as in a project.
    /// </summary>
    public Guid? VariantOfItemId { get; set; }

    /// <summary>
    /// The variant's short label, for example "Red" (1..60), copied from the project variant it was published from and
    /// unique per original among live items. Kept when the item is not (or no longer) grouped, so it can be grouped
    /// again; null on an item published from an original. Imports follow it.
    /// </summary>
    public string? VariantLabel { get; set; }

    /// <summary>
    /// What sets the variant apart, for example "matte red paint, black rims" (1..500), copied from the project variant
    /// it was published from. Untrusted text, like the project column. Imports follow it.
    /// </summary>
    public string? VariantDescription { get; set; }

    /// <summary>The workspace statement the attesting member agreed to, verbatim, in <see cref="ConsentLocale"/>.</summary>
    public string ConsentStatement { get; set; } = string.Empty;

    /// <summary>The statement version (<c>avatar-consent-workspace-2026-11</c> or <c>avatar-object-workspace-2026-11</c>).</summary>
    public string ConsentStatementVersion { get; set; } = string.Empty;

    /// <summary>The language the statement was shown in (<c>en</c> or <c>it</c>).</summary>
    public string ConsentLocale { get; set; } = string.Empty;

    /// <summary>The person shown (empty for an object). Never written to the audit events.</summary>
    public string ConsentSubjectName { get; set; } = string.Empty;

    public AvatarConsentBasis ConsentBasis { get; set; } = AvatarConsentBasis.Self;

    /// <summary>The adult checkbox of a person (false and unread for an object).</summary>
    public bool ConsentSubjectAdult { get; set; }

    /// <summary>The Owner or Admin who attested the workspace statement.</summary>
    public Guid ConsentAttestedByUserId { get; set; }

    /// <summary>When the workspace statement was (last) attested; a reconfirmation moves it.</summary>
    public DateTime ConsentAttestedAt { get; set; }

    public DateTime? ConsentRevokedAt { get; set; }

    public Guid? ConsentRevokedByUserId { get; set; }

    /// <summary>
    /// The library's own copy of the optional signed release (<see cref="AvatarLibraryStorage.ConsentDocumentKey"/>),
    /// or null. <b>Never sent to any vendor</b> and never served by a route; removed with the item.
    /// </summary>
    public string? ConsentDocumentStorageKey { get; set; }

    /// <summary>When a person's item is deleted whether or not anyone asks; <see cref="ProjectAvatar.NoRetention"/> for an object.</summary>
    public DateTime RetentionUntil { get; set; }

    public AvatarDisclosure DisclosureLabel { get; set; } = AvatarDisclosure.Visible;

    public Guid CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optimistic concurrency token, mapped to PostgreSQL's <c>xmin</c> system column (no real column), so a revoke
    /// and a reconfirmation racing on the same item cannot both win.
    /// </summary>
    public uint RowVersion { get; set; }

    /// <summary>The item's own media, when the caller loaded them.</summary>
    public List<AvatarLibraryReference> References { get; set; } = [];

    /// <summary>
    /// Why the item cannot be imported or used RIGHT NOW: <see cref="ProjectAvatar.ReasonConsentRevoked"/> or
    /// <see cref="ProjectAvatar.ReasonRetentionExpired"/> for a person, checked in that order; null when usable. An
    /// object has no person behind it, so it never has a reason. The item's media are its own, immutable copies
    /// (written once at publish, digest-verified), so there is no reference reason here; an import's own files are
    /// judged by <see cref="ProjectAvatar.UnselectableReason"/> like any avatar's.
    /// </summary>
    public string? UnusableReason(DateTime utcNow)
    {
        if (SubjectKind == AvatarSubjectKind.Object) return null;
        if (ConsentRevokedAt is not null) return ProjectAvatar.ReasonConsentRevoked;
        return RetentionUntil <= utcNow ? ProjectAvatar.ReasonRetentionExpired : null;
    }

    /// <summary>Whether the item may be imported or used right now (<see cref="UnusableReason"/> is null).</summary>
    public bool IsUsable(DateTime utcNow) => UnusableReason(utcNow) is null;

    /// <summary>Whether this item is a variant of another item of the library (it names its original).</summary>
    public bool IsVariant => VariantOfItemId is not null;
}

/// <summary>
/// One medium of an <see cref="AvatarLibraryItem"/> (<c>avatar_library_references</c>, API-owned): the library's OWN
/// copy of a reference picture, video or recording, in organization storage. Reached only through its item (cascade),
/// so it carries no tenant filter of its own. <see cref="ContentSha256"/> is the digest attested on the project avatar
/// the item was published from, re-verified on the copied bytes; an import's reference rows carry the same digest.
/// </summary>
public class AvatarLibraryReference
{
    public Guid Id { get; set; }

    public Guid LibraryItemId { get; set; }

    public AvatarReferenceKind Kind { get; set; }

    public AvatarReferenceRole Role { get; set; }

    /// <summary>The order of the source avatar's references; the first primary-role image is the item's picture.</summary>
    public int Ordinal { get; set; }

    /// <summary>Lower-case hex SHA-256 of the stored bytes.</summary>
    public string ContentSha256 { get; set; } = string.Empty;

    /// <summary>The object key (<see cref="AvatarLibraryStorage.ReferenceKey"/>). Never returned by a route.</summary>
    public string StorageKey { get; set; } = string.Empty;

    public string MimeType { get; set; } = string.Empty;

    /// <summary>The file's name at publish, reused as the name of an import's copy.</summary>
    public string FileName { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public int? DurationMs { get; set; }

    public DateTime AttachedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The durable hold marker: set when a CSAM or moderation verdict (or the project's content-quarantine marker) on
    /// a file of the organization with this digest was seen, or just before such a file's row was removed. Never
    /// cleared by a delete (docs/csam.md, docs/avatars.md "Quarantine and holds"). Null while nothing holds the copy.
    /// </summary>
    public DateTime? HeldAt { get; set; }

    /// <summary><c>csam</c>, <c>moderation</c> or <c>content_marker</c>: why <see cref="HeldAt"/> was set.</summary>
    public string? HeldReason { get; set; }
}

/// <summary>The limits of the workspace avatar library (docs/design/avatar-objects.md).</summary>
public static class AvatarLibraryLimits
{
    /// <summary>The most items of ONE kind (people, or objects) an organization's library holds.</summary>
    public const int MaxItemsPerKind = 100;

    /// <summary>
    /// The most variants one library item has (<c>409 LIBRARY_VARIANT_LIMIT_REACHED</c>), the same figure as a project
    /// avatar's (<see cref="ProjectAvatarLimits.MaxVariantsPerAvatar"/>), so a project that imports an item and all its
    /// variants can always group them under it.
    /// </summary>
    public const int MaxVariantsPerItem = ProjectAvatarLimits.MaxVariantsPerAvatar;

    /// <summary>
    /// The most projects one item may be imported into. It bounds every propagation loop (rename, reconfirm, revoke,
    /// delete), so each runs over at most this many imports.
    /// </summary>
    public const int MaxImportsPerItem = 50;

    public const int MaxFileNameLength = 260;

    public const int MaxMimeTypeLength = 200;

    public const int MaxStorageKeyLength = 600;
}

/// <summary>
/// Where the workspace avatar library keeps its objects, and how an import's copies are told apart from a project's
/// own files. One layout, so the service that writes them, the account purge and the storage sweep cannot disagree:
/// <c>organizations/{organizationId:D}/avatar-library/{itemId:D}/{referenceId:D}.{ext}</c> for a medium and
/// <c>.../consent.{ext}</c> for the signed release. Written only by the Inference API's <c>AvatarLibraryService</c>.
/// </summary>
public static class AvatarLibraryStorage
{
    /// <summary>The folder under an organization's prefix that holds its library.</summary>
    public const string Folder = "avatar-library";

    /// <summary>The project folder (below <c>userFiles/</c>) an import's copies are registered under.</summary>
    public const string ProjectDirectory = "avatars";

    private const string OrganizationsRoot = "organizations/";
    private const string RevisionPrefix = "avatar-library:";

    /// <summary>The prefix of ONE organization's library: <c>organizations/{organizationId:D}/avatar-library/</c>.</summary>
    public static string OrganizationPrefix(Guid organizationId) =>
        OrganizationsRoot + organizationId.ToString("D") + "/" + Folder + "/";

    /// <summary>The prefix of one item's objects: <c>organizations/{organizationId:D}/avatar-library/{itemId:D}/</c>.</summary>
    public static string ItemPrefix(Guid organizationId, Guid itemId) =>
        OrganizationPrefix(organizationId) + itemId.ToString("D") + "/";

    /// <summary>The key of one medium. <paramref name="extension"/> is a checked, lower-case extension without the dot.</summary>
    public static string ReferenceKey(Guid organizationId, Guid itemId, Guid referenceId, string extension) =>
        ItemPrefix(organizationId, itemId) + referenceId.ToString("D") + "." + SafeExtension(extension);

    /// <summary>The key of the item's copy of the signed release.</summary>
    public static string ConsentDocumentKey(Guid organizationId, Guid itemId, string extension) =>
        ItemPrefix(organizationId, itemId) + "consent." + SafeExtension(extension);

    /// <summary>
    /// The organization and item a key belongs to, when <paramref name="storageKey"/> is exactly an object of the
    /// library layout (<c>organizations/{guid}/avatar-library/{guid}/{name}</c>, no further folder, no <c>..</c>);
    /// otherwise null. Every read, copy and delete of the library store checks a key with this first.
    /// </summary>
    public static (Guid OrganizationId, Guid ItemId)? Parse(string? storageKey)
    {
        if (string.IsNullOrEmpty(storageKey) || !storageKey.StartsWith(OrganizationsRoot, StringComparison.Ordinal)) return null;
        string[] parts = storageKey[OrganizationsRoot.Length..].Split('/');
        return parts.Length == 4
               && parts[1] == Folder
               && parts[3].Length > 0
               && !parts[3].Contains("..", StringComparison.Ordinal)
               && Guid.TryParseExact(parts[0], "D", out Guid organizationId)
               && Guid.TryParseExact(parts[2], "D", out Guid itemId)
            ? (organizationId, itemId)
            : null;
    }

    /// <summary>
    /// The <c>project_files.source_revision</c> of a file COPIED from library item <paramref name="itemId"/> into a
    /// project by an import. It is how the avatar delete routine tells an import's copies (removed with the import)
    /// from files the project already had (the avatar a library item was published from keeps its own files as an
    /// ordinary avatar would). A user cannot write this column.
    /// </summary>
    public static string SourceRevision(Guid itemId) => RevisionPrefix + itemId.ToString("N");

    /// <summary>Whether <paramref name="file"/> is a copy an import of item <paramref name="itemId"/> made.</summary>
    public static bool IsCopyOf(ProjectFile file, Guid itemId) =>
        string.Equals(file.SourceRevision, SourceRevision(itemId), StringComparison.Ordinal);

    /// <summary>
    /// A lower-case extension of at most 8 letters and digits taken from <paramref name="fileName"/>, or <c>bin</c>
    /// when it has none that is safe to put in a key.
    /// </summary>
    public static string ExtensionOf(string? fileName)
    {
        string extension = Path.GetExtension(fileName ?? string.Empty).TrimStart('.').ToLowerInvariant();
        return SafeExtension(extension);
    }

    private static string SafeExtension(string? extension) =>
        !string.IsNullOrEmpty(extension) && extension.Length <= 8 && extension.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            ? extension
            : "bin";
}
