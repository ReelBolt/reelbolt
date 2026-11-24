namespace ReelBolt.Shared.Data.Models;

/// <summary>What a reference file is, checked from its MIME at attach time. <b>APPEND ONLY</b>: persisted by name.</summary>
public enum AvatarReferenceKind
{
    Image,
    Video,
    Audio
}

/// <summary>
/// What a reference shows: a hint for the phase 2 and 3 mapping onto a model's inputs, never sent as free text.
/// A person takes <c>Face</c> / <c>HalfBody</c> / <c>FullBody</c> pictures, <c>Motion</c> / <c>Speech</c> videos and
/// <c>Speech</c> recordings; an object takes <c>Front</c> / <c>Angle</c> / <c>Detail</c> pictures and <c>Motion</c>
/// videos (<see cref="ProjectAvatarLimits.RoleFits"/> is the one rule). <b>APPEND ONLY</b>: persisted by name.
/// </summary>
public enum AvatarReferenceRole
{
    /// <summary>A clear picture of the face; the first <c>Face</c> image (by ordinal) is the avatar's primary picture.</summary>
    Face,
    HalfBody,
    FullBody,

    /// <summary>A short video of the person moving.</summary>
    Motion,

    /// <summary>A short recording of the person speaking (carries the voice into a reference-mode clip).</summary>
    Speech,

    /// <summary>An object seen from the front: the first <c>Front</c> image (by ordinal) is the object's primary picture.</summary>
    Front,

    /// <summary>An object seen from another side.</summary>
    Angle,

    /// <summary>A close view of a part of an object (a label, a texture, a logo).</summary>
    Detail
}

/// <summary>
/// One reference medium of a <see cref="ProjectAvatar"/> (<c>project_avatar_references</c>, API-owned, engine maps
/// it read-only). The medium IS an ordinary project file, linked, not copied (AV-3), so it keeps the CSAM scan,
/// ingest moderation, probe and derivatives. The file link is <c>ON DELETE NO ACTION</c>: a file cannot be deleted
/// from under a live avatar (the API answers <c>409 FILE_IN_USE_BY_AVATAR</c> first), while a project delete, which
/// cascades to both the file and the avatar, still succeeds because the check runs at the end of the statement.
/// </summary>
public class ProjectAvatarReference
{
    /// <summary>
    /// The <c>project_files.source_kind</c> value of a file used as an avatar reference, so the media pool can badge it
    /// and <c>ListProjectFiles</c> can tell the assistant not to use it as ordinary footage.
    /// </summary>
    public const string ProjectFileSourceKind = "avatar_reference";

    public Guid Id { get; set; }

    public Guid AvatarId { get; set; }

    public Guid ProjectFileId { get; set; }

    public AvatarReferenceKind Kind { get; set; }

    public AvatarReferenceRole Role { get; set; }

    /// <summary>User order; the first <see cref="AvatarReferenceRole.Face"/> image is the primary picture.</summary>
    public int Ordinal { get; set; }

    /// <summary>
    /// Lower-case hex SHA-256 of the file's bytes at attach time. A file whose bytes changed is
    /// <see cref="ProjectAvatar.ReasonReferenceMissing"/> until the references are re-attested.
    /// </summary>
    public string ContentSha256 { get; set; } = string.Empty;

    /// <summary>From the ingest probe (<c>source_probe_json</c>), when known.</summary>
    public int? Width { get; set; }

    public int? Height { get; set; }

    public int? DurationMs { get; set; }

    public DateTime AttachedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The state the selectability rule reads, from what the caller observed: <paramref name="currentSha256"/> is the
    /// digest of the file's bytes NOW (null when the file row or its object is gone), <paramref name="held"/> whether
    /// it is quarantined or under the output hold. A digest other than the attested one counts as missing.
    /// </summary>
    public AvatarReferenceState Observe(string? currentSha256, bool held) =>
        new(Kind,
            Present: !string.IsNullOrEmpty(currentSha256) && string.Equals(currentSha256, ContentSha256, StringComparison.OrdinalIgnoreCase),
            Held: held);
}
