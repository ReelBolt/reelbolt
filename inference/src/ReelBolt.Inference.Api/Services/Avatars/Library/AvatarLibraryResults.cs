using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Avatars.Library;

/// <summary>
/// What a workspace avatar library call answered: the value, a refusal (a machine-readable code, a plain sentence and
/// the HTTP status a route would give it), or "there is no such item or avatar for this caller". Never an HTTP result:
/// the REST routes and the assistant tools both read it and word their own answer.
/// </summary>
/// <param name="Value">The result of a call that went through; null otherwise.</param>
/// <param name="Refusal">Why the call was refused; nothing was changed (copies made on the way are removed).</param>
/// <param name="NotFound">The item, avatar or project does not exist for this organization, or its kind is switched off.</param>
public sealed record AvatarLibraryResult<T>(T? Value, AvatarRefusal? Refusal, bool NotFound) where T : class
{
    public bool Succeeded => Value is not null;

    public static AvatarLibraryResult<T> Ok(T value) => new(value, null, false);

    public static AvatarLibraryResult<T> Refused(AvatarRefusal refusal) => new(null, refusal, false);

    public static AvatarLibraryResult<T> Missing() => new(null, null, true);
}

/// <summary>
/// One library item as listed: the row plus what the list shows about it. The variant fields
/// (<see cref="AvatarLibraryItem.VariantOfItemId"/>, <see cref="AvatarLibraryItem.VariantLabel"/>,
/// <see cref="AvatarLibraryItem.VariantDescription"/>) are the row's own.
/// </summary>
/// <param name="UnusableReason"><c>consent_revoked</c> or <c>retention_expired</c> (a person only); null when usable.</param>
/// <param name="PrimaryReferenceId">The picture to show for it: the first <c>Face</c> (person) or <c>Front</c> (object) image, else the first image.</param>
/// <param name="ImportCount">How many projects hold an import of it.</param>
/// <param name="Imported">Whether the project named in the call already holds an import; false without one.</param>
/// <param name="VariantCount">How many items of the library are variants of it (always 0 for a variant: one level only).</param>
public sealed record AvatarLibraryItemView(
    AvatarLibraryItem Item,
    string? UnusableReason,
    int Images,
    int Videos,
    int Audio,
    Guid? PrimaryReferenceId,
    int ImportCount,
    bool Imported,
    int VariantCount = 0);

/// <summary>
/// The attestation sent with a publish or a reconfirmation: the WORKSPACE statement of the kind, verbatim, in the
/// locale it was shown in; for a person also the adult tick and the keep-for days (default
/// <see cref="ProjectAvatarLimits.DefaultRetentionDays"/>).
/// </summary>
public sealed record AvatarLibraryAttestation(string? ConsentStatement, string? ConsentLocale, bool SubjectIsAdult = false, int? RetentionDays = null);

/// <summary>Why a library item is being deleted: it decides the audit event's cause word and its actor.</summary>
public enum AvatarLibraryDeleteCause
{
    /// <summary>An Owner or Admin pressed Delete.</summary>
    User,

    /// <summary>The retention sweep: past <c>retention_until</c>, or revoked longer than the grace.</summary>
    Retention
}

/// <summary>What deleting a library item actually removed: reported, never assumed.</summary>
/// <param name="ItemDeleted">
/// The item row is gone. False only when an import could not be removed (<paramref name="ImportsFailed"/> above
/// zero): the item then stays, with its objects, and the delete can be asked again.
/// </param>
/// <param name="ImportsDeleted">Imports removed from their projects, each through the project avatar delete routine (copied files included).</param>
/// <param name="ImportsFailed">Imports that could not be removed.</param>
/// <param name="ObjectsDeleted">The item's own stored objects that are gone.</param>
/// <param name="ObjectsFailed">Stored objects that could not be removed yet; the storage sweep removes an object whose item is gone.</param>
public sealed record AvatarLibraryDeleteReport(bool ItemDeleted, int ImportsDeleted, int ImportsFailed, int ObjectsDeleted, int ObjectsFailed);

/// <summary>A picture of a library item, opened for streaming. The caller disposes <paramref name="Content"/>.</summary>
public sealed record AvatarLibraryPicture(Stream Content, string MimeType, string FileName, long SizeBytes);

/// <summary>What one retention sweep did to the library.</summary>
public sealed record AvatarLibrarySweepResult(int Deleted, int Warned);
