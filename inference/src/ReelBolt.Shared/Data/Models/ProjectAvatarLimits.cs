namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// Every avatar limit as a named constant (docs/design/avatars.md "Limits"), set inside MiniMax's reference envelope so
/// a ready avatar is never refused by the vendor for a reason we could check first. The column lengths are used by the
/// EF mappings; the media limits are enforced at attach time and returned by <c>GET .../avatars/requirements</c>.
/// </summary>
public static class ProjectAvatarLimits
{
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 2000;
    public const int MaxConsentStatementLength = 4000;
    public const int MaxConsentStatementVersionLength = 64;
    public const int MaxConsentLocaleLength = 16;
    public const int MaxSubjectNameLength = 200;
    public const int MaxRemoteAssetIdLength = 500;

    public const int MaxAvatarsPerProject = 20;

    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 730;
    public const int DefaultRetentionDays = 365;

    /// <summary>A revoked avatar is deleted by the retention sweep this many days after the revoke.</summary>
    public const int RevokedGraceDays = 30;

    public const int MaxImages = 8;
    public const int MaxVideos = 3;
    public const int MaxAudio = 3;

    public const long MaxImageBytes = 30L * 1024 * 1024;
    public const int MinImageSidePx = 256;
    public const int MaxImageSidePx = 5760;
    public const double MinImageAspect = 0.4;
    public const double MaxImageAspect = 2.5;

    public const long MaxVideoBytes = 50L * 1024 * 1024;
    public const int MinVideoMs = 2_000;
    public const int MaxVideoMs = 15_000;
    public const double MinVideoFps = 23.976;
    public const double MaxVideoFps = 60;

    public const long MaxAudioBytes = 15L * 1024 * 1024;
    public const int MinAudioMs = 2_000;
    public const int MaxAudioMs = 15_000;

    /// <summary>Image MIME types accepted (HEIC/HEIF refused: no header reader checks their size).</summary>
    public static readonly IReadOnlyList<string> ImageMimeTypes = ["image/jpeg", "image/png", "image/webp"];

    public static readonly IReadOnlyList<string> VideoMimeTypes = ["video/mp4", "video/quicktime"];

    public static readonly IReadOnlyList<string> AudioMimeTypes = ["audio/wav", "audio/x-wav", "audio/wave", "audio/mpeg", "audio/mp3"];

    // ---- Objects (docs/design/avatar-objects.md): counted apart from people, no audio. ----

    /// <summary>The most OBJECT avatars a project holds, counted apart from its <see cref="MaxAvatarsPerProject"/> people.</summary>
    public const int MaxObjectsPerProject = 20;

    /// <summary>The most pictures an object avatar holds (at least one is required).</summary>
    public const int MaxObjectImages = 8;

    /// <summary>The most short videos an object avatar holds. An object takes no audio.</summary>
    public const int MaxObjectVideos = 3;

    /// <summary>The roles a PERSON's references take, in display order (the requirements route's <c>roles</c>).</summary>
    public static readonly IReadOnlyList<AvatarReferenceRole> PersonRoles =
    [
        AvatarReferenceRole.Face,
        AvatarReferenceRole.HalfBody,
        AvatarReferenceRole.FullBody,
        AvatarReferenceRole.Motion,
        AvatarReferenceRole.Speech
    ];

    /// <summary>The roles an OBJECT's references take, in display order (the requirements route's <c>objectRoles</c>).</summary>
    public static readonly IReadOnlyList<AvatarReferenceRole> ObjectRoles =
    [
        AvatarReferenceRole.Front,
        AvatarReferenceRole.Angle,
        AvatarReferenceRole.Detail,
        AvatarReferenceRole.Motion
    ];

    // ---- Variants (docs/design/avatar-objects.md "Avatar variants", decision AO-8). ----

    /// <summary>The most variants one avatar has (<c>409 VARIANT_LIMIT_REACHED</c>). Variants do not count toward the per-kind project limit.</summary>
    public const int MaxVariantsPerAvatar = 10;

    /// <summary>The longest variant label ("Red").</summary>
    public const int MaxVariantLabelLength = 60;

    /// <summary>The longest variant description ("matte red paint, black rims").</summary>
    public const int MaxVariantDescriptionLength = 500;

    /// <summary>The longest instruction a model-made variant takes ("paint it matte red").</summary>
    public const int MaxVariantInstructionLength = 1000;

    /// <summary>The roles an avatar of <paramref name="subjectKind"/> may use.</summary>
    public static IReadOnlyList<AvatarReferenceRole> RolesOf(AvatarSubjectKind subjectKind) =>
        subjectKind == AvatarSubjectKind.Object ? ObjectRoles : PersonRoles;

    /// <summary>
    /// The ONE rule for "may a reference of <paramref name="kind"/> on an avatar of <paramref name="subjectKind"/> be
    /// described as <paramref name="role"/>". A person: pictures are <c>Face</c> / <c>HalfBody</c> / <c>FullBody</c>,
    /// videos <c>Motion</c> / <c>Speech</c>, recordings <c>Speech</c>. An object: pictures are <c>Front</c> /
    /// <c>Angle</c> / <c>Detail</c>, videos <c>Motion</c>, and no recording fits at all.
    /// </summary>
    public static bool RoleFits(AvatarSubjectKind subjectKind, AvatarReferenceKind kind, AvatarReferenceRole role) =>
        (subjectKind, kind, role) switch
        {
            (AvatarSubjectKind.Person, AvatarReferenceKind.Image,
                AvatarReferenceRole.Face or AvatarReferenceRole.HalfBody or AvatarReferenceRole.FullBody) => true,
            (AvatarSubjectKind.Person, AvatarReferenceKind.Video, AvatarReferenceRole.Motion or AvatarReferenceRole.Speech) => true,
            (AvatarSubjectKind.Person, AvatarReferenceKind.Audio, AvatarReferenceRole.Speech) => true,
            (AvatarSubjectKind.Object, AvatarReferenceKind.Image,
                AvatarReferenceRole.Front or AvatarReferenceRole.Angle or AvatarReferenceRole.Detail) => true,
            (AvatarSubjectKind.Object, AvatarReferenceKind.Video, AvatarReferenceRole.Motion) => true,
            _ => false
        };
}

/// <summary>
/// Where an avatar's stills live (avatars phase 3, the still-then-animate path): the engine writes them, the delete
/// routine and the storage sweep remove them, and nobody else reads them. One layout, so the three cannot disagree.
/// </summary>
/// <remarks>
/// A still is derived personal data: a picture of the person made from their consented references. It lives under
/// <c>projects/{projectId}/agentFiles/avatars/{avatarId}/stills/</c> as a bare object (no <c>project_files</c> row, so it
/// is never offered as a project file, an output or a media-pool item), inherits the avatar's retention (it is removed
/// whenever the avatar is) and its key carries no name, only a digest of what it was made from.
/// </remarks>
public static class AvatarStillStorage
{
    /// <summary>The most avatar pictures one still is made from (the first is the picture edited, the rest references).</summary>
    public const int MaxPictures = 3;

    /// <summary>The storage category a still is written under.</summary>
    public const string Category = "agentFiles";

    /// <summary>The path below <see cref="Category"/> of an avatar's stills folder: <c>avatars/{avatarId}/stills</c>.</summary>
    public static string RelativeDirectory(Guid avatarId) => "avatars/" + avatarId.ToString("D") + "/stills";

    /// <summary>The category-relative path of a still (<c>avatars/{avatarId}/stills/{digest}.{ext}</c>).</summary>
    public static string RelativePath(Guid avatarId, string digestHex, string extension) =>
        RelativeDirectory(avatarId) + "/" + digestHex + "." + extension;

    /// <summary>The object-key prefix of an avatar's stills: <c>projects/{projectId}/agentFiles/avatars/{avatarId}/stills/</c>.</summary>
    public static string ObjectPrefix(Guid projectId, Guid avatarId) =>
        "projects/" + projectId.ToString("D") + "/" + Category + "/" + RelativeDirectory(avatarId) + "/";

    /// <summary>The prefix every avatar's stills of a project share (<c>projects/{projectId}/agentFiles/avatars/</c>).</summary>
    public static string ProjectPrefix(Guid projectId) =>
        "projects/" + projectId.ToString("D") + "/" + Category + "/avatars/";

    /// <summary>
    /// The avatar a still key belongs to, when <paramref name="storageKey"/> is exactly a still of
    /// <paramref name="projectId"/> (<c>.../avatars/{guid}/stills/{name}</c>, no further folder); otherwise null.
    /// </summary>
    public static Guid? AvatarOf(Guid projectId, string storageKey)
    {
        string prefix = ProjectPrefix(projectId);
        if (string.IsNullOrEmpty(storageKey) || !storageKey.StartsWith(prefix, StringComparison.Ordinal)) return null;
        string[] rest = storageKey[prefix.Length..].Split('/');
        return rest.Length == 3 && rest[1] == "stills" && rest[2].Length > 0 && !rest[2].Contains("..", StringComparison.Ordinal)
               && Guid.TryParseExact(rest[0], "D", out Guid avatarId)
            ? avatarId
            : null;
    }
}
