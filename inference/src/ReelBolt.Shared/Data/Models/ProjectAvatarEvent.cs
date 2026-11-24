namespace ReelBolt.Shared.Data.Models;

/// <summary>What an avatar audit event records. <b>APPEND ONLY</b>: persisted by name.</summary>
public enum AvatarEventKind
{
    Created,

    /// <summary>The references were changed, which is a re-attestation.</summary>
    ReferencesChanged,
    ConsentRevoked,
    Deleted,

    /// <summary>Deleted by the retention sweep (actor null).</summary>
    RetentionDeleted,
    VendorAssetDeleted,

    /// <summary>Phase 2: a run used the avatar.</summary>
    UsedInRun,

    /// <summary>The 7-day retention warning was sent to the attesting member (one per attestation).</summary>
    RetentionWarningSent,

    /// <summary>
    /// Workspace avatar library (docs/design/avatar-objects.md): an avatar was saved to the library. Written for the
    /// new library item (<c>AvatarId</c> = the item's id, <c>ProjectId</c> = the project it was published from).
    /// </summary>
    LibraryPublished,

    /// <summary>
    /// A project avatar became a linked import of a library item: by an import into the project, or because it is the
    /// avatar the item was published from (<c>via</c> says which).
    /// </summary>
    LibraryImported,

    /// <summary>A library item was confirmed again under the workspace statement (written for the item).</summary>
    LibraryReconfirmed,

    /// <summary>Consent for a library item was withdrawn (written for the item; each import gets its own <see cref="ConsentRevoked"/>).</summary>
    LibraryConsentRevoked,

    /// <summary>A library item was deleted, by a member or by the retention sweep (written for the item; counts of imports and objects).</summary>
    LibraryDeleted,

    /// <summary>The 7-day retention warning of a library item was sent to the organization's Owners and Admins (one per attestation).</summary>
    LibraryRetentionWarningSent
}

/// <summary>
/// The append-only accountability record of an avatar (<c>project_avatar_events</c>, API-owned). Deliberately no
/// foreign keys: a row survives the avatar's and the project's deletion, so "we deleted it on this date" can be
/// proven. <see cref="OutcomesJson"/> holds counts and outcome words only, never media, URLs, storage keys or the
/// subject's name. Retained <see cref="RetentionYears"/> years (AV-7), then purged by the avatar retention sweep.
/// </summary>
public class ProjectAvatarEvent
{
    /// <summary>How long an event is kept before the sweep purges it (AV-7, decided 2026-11-05).</summary>
    public const int RetentionYears = 3;

    public Guid Id { get; set; }

    /// <summary>The avatar (no FK); for a <c>Library*</c> event about an item, the workspace avatar library item's id.</summary>
    public Guid AvatarId { get; set; }

    /// <summary>The avatar's project (no FK); <see cref="Guid.Empty"/> for a library item event that names no project.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>The project's organization at the time (no FK).</summary>
    public Guid OrganizationId { get; set; }

    public AvatarEventKind Kind { get; set; }

    /// <summary>The user who acted; null for the retention sweep.</summary>
    public Guid? ActorUserId { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>The consent statement version in force for the avatar when the event happened.</summary>
    public string? ConsentStatementVersion { get; set; }

    /// <summary>A small JSON object of counts and outcome words. Never media, URLs, storage keys or names.</summary>
    public string? OutcomesJson { get; set; }
}
