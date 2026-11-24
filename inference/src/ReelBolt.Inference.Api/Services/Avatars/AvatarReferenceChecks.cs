using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Moderation;

namespace ReelBolt.Inference.Api.Services.Avatars;

/// <summary>One reference the caller asked to attach: a project file and what it shows.</summary>
public sealed record AvatarReferenceAsk(Guid ProjectFileId, string? Role);

/// <summary>A reference that passed every attach check, ready to be written.</summary>
public sealed record CheckedAvatarReference(
    ProjectFile File,
    AvatarReferenceKind Kind,
    AvatarReferenceRole Role,
    int Ordinal,
    string ContentSha256,
    int? Width,
    int? Height,
    int? DurationMs);

/// <summary>
/// A refused avatar request: the HTTP status, a machine-readable code (sent as both <c>error</c> and <c>code</c>, the
/// two shapes the web client reads), a plain sentence and optional extra fields.
/// </summary>
public sealed record AvatarRefusal(int StatusCode, string Code, string Message, IReadOnlyDictionary<string, object?>? Extra = null)
{
    public ObjectResult ToResult()
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["error"] = Code,
            ["code"] = Code,
            ["message"] = Message
        };
        if (Extra is not null)
        {
            foreach ((string key, object? value) in Extra) body[key] = value;
        }

        return new ObjectResult(body) { StatusCode = StatusCode };
    }

    public static AvatarRefusal BadRequest(string code, string message, IReadOnlyDictionary<string, object?>? extra = null) =>
        new(StatusCodes.Status400BadRequest, code, message, extra);

    public static AvatarRefusal Conflict(string code, string message, IReadOnlyDictionary<string, object?>? extra = null) =>
        new(StatusCodes.Status409Conflict, code, message, extra);

    public static AvatarRefusal Unavailable(string code, string message, IReadOnlyDictionary<string, object?>? extra = null) =>
        new(StatusCodes.Status503ServiceUnavailable, code, message, extra);
}

/// <summary>
/// What the attach checks answered: the checked references, or the first refusal, and for an object how its person
/// check ended (<see cref="AvatarReferenceChecks.PersonCheckPassed"/>; null for a person avatar and for a refusal).
/// </summary>
public sealed record AvatarReferenceCheckOutcome(
    IReadOnlyList<CheckedAvatarReference>? References,
    AvatarRefusal? Refusal,
    string? PersonCheck = null);

/// <summary>
/// The attach-time checks of an avatar's reference media (docs/design/avatars.md "Consent and safety", "Limits").
/// Every check FAILS CLOSED and writes nothing (one exception, stated: a reference the CSAM control has not scanned
/// is queued for a scan when it is refused). Shared by create and by the reference change (re-attestation).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Each file must be THIS project's, not quarantined (CSAM or moderation) and not under the output hold;
/// any of those is the same <c>400 reference_file_not_found</c>, so a held file is not told apart.</item>
/// <item>A file another writer owns (<c>source_kind</c> <c>github</c>, <c>local_folder</c>, a cloud drive,
/// <c>output_copy</c>) is refused (<c>400 reference_file_source_not_allowed</c>): its writer would rewrite or delete it,
/// and its <c>source_kind</c> is never overwritten.</item>
/// <item>Kinds, MIME types, counts, sizes, pixel sizes, durations, frame rates and codecs are the
/// <see cref="ProjectAvatarLimits"/> constants.</item>
/// <item>With content moderation on, <c>Unmoderated</c>/<c>Scanning</c> is <c>409 AVATAR_REFERENCE_PENDING</c> and
/// <c>Error</c> is <c>409 AVATAR_REFERENCE_MODERATION_ERROR</c> (AV-5: fail closed where ingest fails open).</item>
/// <item>With CSAM matching on, anything but <c>Clean</c> is <c>409 AVATAR_REFERENCE_UNSCANNED</c>.</item>
/// <item>A generated output needs an allowed publish verdict (<see cref="PublishModeration.GateStoredCopyAsync"/>).</item>
/// <item>A video or audio reference needs the ingest probe (duration, frame rate, codec): none yet is
/// <c>409 AVATAR_REFERENCE_PENDING</c>.</item>
/// </list>
/// The bytes are read last, once each, for the SHA-256 the selectability rule compares and for an image's pixel size.
/// <para>
/// <b>Objects</b> (docs/design/avatar-objects.md "Avatar REST contract with objects") pass the same checks with their
/// own shape: 1..<see cref="ProjectAvatarLimits.MaxObjectImages"/> pictures, up to
/// <see cref="ProjectAvatarLimits.MaxObjectVideos"/> videos, no recording, the object roles
/// (<see cref="ProjectAvatarLimits.RoleFits"/>), and a platform-made file (an extracted frame, a picture export, a saved
/// output copy) is accepted while a file kept in step with a connected source is still refused. Then, last, the person
/// check (decision AO-1, <see cref="IObjectReferencePersonCheck"/>): every picture, and one frame of every video from a
/// derivative that already exists, is shown to the default Vision provider. A picture that shows a person is
/// <c>400 object_reference_shows_person</c>; a provider that errors is <c>503 OBJECT_PERSON_CHECK_UNAVAILABLE</c>
/// (fail closed); no Vision provider at all is <c>409 VISION_PROVIDER_REQUIRED</c>: the check is mandatory.
/// </para>
/// </remarks>
public sealed class AvatarReferenceChecks
{
    /// <summary>Video codecs a reference video may use (ffprobe names of H.264 and H.265).</summary>
    public static readonly IReadOnlyList<string> VideoCodecs = ["h264", "hevc"];

    private readonly InferenceApiDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly ContentModerationOptions _moderation;
    private readonly CsamOptions _csam;
    private readonly PublishModeration? _publishModeration;
    private readonly IObjectReferencePersonCheck? _personCheck;
    private readonly ILogger<AvatarReferenceChecks> _logger;
    private readonly ReelBolt.Shared.Auth.ICurrentUser? _currentUser;

    /// <summary>The Created / ReferencesChanged event word for an object whose every picture was checked and shows no person.</summary>
    public const string PersonCheckPassed = "passed";

    /// <summary>The event word older object events carry from when the check was optional. Never written now: no Vision provider is a refusal. Kept so old events still read.</summary>
    public const string PersonCheckSkipped = "skipped";

    /// <summary>
    /// The code of the refusal when no default Vision provider is configured: an object's pictures are never taken
    /// without the person check, so every path that makes or verifies them stops here (docs/design/avatar-objects.md
    /// "Deviations recorded at build time").
    /// </summary>
    public const string VisionProviderRequiredCode = "VISION_PROVIDER_REQUIRED";

    /// <summary>The plain sentence of <see cref="VisionProviderRequiredCode"/>.</summary>
    public const string VisionProviderRequiredMessage =
        "Checking that a picture shows no person needs a vision model, and none is set up for this workspace. " +
        "Ask an administrator to set up a vision model, then try again.";

    /// <summary>The refusal for a missing Vision provider (409, with <c>reason</c>, <c>capability</c> and <c>canConfigure</c> like the picture editor's NO_PROVIDER answer).</summary>
    public static AvatarRefusal VisionRequired(bool canConfigure = false) =>
        AvatarRefusal.Conflict(VisionProviderRequiredCode, VisionProviderRequiredMessage,
            new Dictionary<string, object?> { ["reason"] = "NO_PROVIDER", ["capability"] = "Vision", ["canConfigure"] = canConfigure });

    /// <summary>
    /// The <c>project_files.source_kind</c> values an OBJECT reference may carry besides none: a file an avatar of a
    /// person already uses, a saved copy of an output, a file unpacked from the member's own zip upload, and a picture
    /// or video a variant build made (<c>avatar_variant</c>, docs/design/avatar-objects.md "Avatar variants"), and a frame
    /// an extraction cut out of a video (<c>avatar_extraction</c>, "Pulling an object out of a video"). Every
    /// other value (a repository, a cloud drive, a computer folder, anything added later) is refused: an allowlist, so
    /// a new writer is refused until someone decides otherwise.
    /// </summary>
    public static readonly IReadOnlyList<string> ObjectSourceKinds =
        [ProjectAvatarReference.ProjectFileSourceKind, ReelBolt.Shared.Storage.ContentQuarantineRule.ProjectFileSourceKindOutputCopy, "zip",
            Builds.AvatarBuildFiles.VariantSourceKind, Builds.AvatarBuildFiles.ExtractionSourceKind];

    public AvatarReferenceChecks(
        InferenceApiDbContext db,
        IFileStorageService storage,
        IOptions<ContentModerationOptions> moderation,
        IOptions<CsamOptions> csam,
        ILogger<AvatarReferenceChecks> logger,
        PublishModeration? publishModeration = null,
        IObjectReferencePersonCheck? personCheck = null,
        ReelBolt.Shared.Auth.ICurrentUser? currentUser = null)
    {
        _currentUser = currentUser;
        _db = db;
        _storage = storage;
        _moderation = moderation.Value;
        _csam = csam.Value;
        _logger = logger;
        _publishModeration = publishModeration;
        _personCheck = personCheck;
    }

    /// <summary>Whether object pictures are checked for people here (a Vision provider is configured); the requirements route's <c>personCheck</c>.</summary>
    public async Task<bool> PersonCheckActiveAsync(CancellationToken ct) =>
        _personCheck is not null && await _personCheck.IsActiveAsync(ct);

    /// <summary>
    /// Null when a default Vision provider is configured; otherwise the <see cref="VisionProviderRequiredCode"/> refusal.
    /// The first thing every path that makes or verifies an object's pictures asks, before anything is charged, queued
    /// or sent.
    /// </summary>
    public async Task<AvatarRefusal?> VisionRefusalAsync(CancellationToken ct) =>
        await PersonCheckActiveAsync(ct) ? null : VisionRequired(CanConfigure());

    private bool CanConfigure() => _currentUser is { } u
        && (u.IsPlatformAdmin || u.OrgRole is ReelBolt.Shared.Tenancy.OrganizationRole.Owner or ReelBolt.Shared.Tenancy.OrganizationRole.Admin);

    /// <summary>Whether content moderation gates the references (AV-5 applies only then).</summary>
    public bool ModerationEnabled => _moderation.Enabled;

    /// <summary>Whether CSAM matching is on (an unscanned reference is refused only then).</summary>
    public bool CsamEnabled => _csam.Arachnid.Enabled;

    /// <summary>The kind a MIME type maps to, or null when avatars do not accept it.</summary>
    public static AvatarReferenceKind? KindOf(string? mimeType)
    {
        string mime = (mimeType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        if (ProjectAvatarLimits.ImageMimeTypes.Contains(mime)) return AvatarReferenceKind.Image;
        if (ProjectAvatarLimits.VideoMimeTypes.Contains(mime)) return AvatarReferenceKind.Video;
        if (ProjectAvatarLimits.AudioMimeTypes.Contains(mime)) return AvatarReferenceKind.Audio;
        return null;
    }

    /// <summary>
    /// Runs every check over the references of a PERSON avatar (in order: the order is the reference ordinal) and
    /// answers the checked references, or the first refusal. Nothing is written except the CSAM re-scan request
    /// described above.
    /// </summary>
    public async Task<(IReadOnlyList<CheckedAvatarReference>? References, AvatarRefusal? Refusal)> CheckAsync(
        Guid projectId, IReadOnlyList<AvatarReferenceAsk>? asks, CancellationToken ct)
    {
        AvatarReferenceCheckOutcome outcome = await CheckAsync(projectId, Guid.Empty, AvatarSubjectKind.Person, asks, ct);
        return (outcome.References, outcome.Refusal);
    }

    /// <summary>
    /// Runs every check over <paramref name="asks"/> for an avatar of <paramref name="subjectKind"/> (in order: the
    /// order is the reference ordinal) and answers the checked references, or the first refusal. For an object the
    /// person check runs last and its outcome word is answered too. Nothing is written except the CSAM re-scan request
    /// described above.
    /// </summary>
    /// <param name="organizationId">The project's organization: the person check's (free) usage is attributed to it.</param>
    public async Task<AvatarReferenceCheckOutcome> CheckAsync(
        Guid projectId, Guid organizationId, AvatarSubjectKind subjectKind, IReadOnlyList<AvatarReferenceAsk>? asks, CancellationToken ct)
    {
        bool isObject = subjectKind == AvatarSubjectKind.Object;
        string subjectWord = isObject ? "object" : "person";
        int maxImages = isObject ? ProjectAvatarLimits.MaxObjectImages : ProjectAvatarLimits.MaxImages;
        int maxVideos = isObject ? ProjectAvatarLimits.MaxObjectVideos : ProjectAvatarLimits.MaxVideos;
        int maxAudio = isObject ? 0 : ProjectAvatarLimits.MaxAudio;

        // An object's pictures are never taken without the person check: no Vision provider, no object (first, so nothing is read).
        if (isObject && await VisionRefusalAsync(ct) is { } noVision) return Refuse(noVision);

        if (asks is null || asks.Count == 0)
            return Refuse(AvatarRefusal.BadRequest("no_image_reference", $"Add at least one picture of the {subjectWord}."));
        int maxTotal = maxImages + maxVideos + maxAudio;
        if (asks.Count > maxTotal)
            return Refuse(TooMany($"An avatar holds at most {maxTotal} reference files."));
        if (asks.Select(a => a.ProjectFileId).Distinct().Count() != asks.Count)
            return Refuse(AvatarRefusal.BadRequest("duplicate_reference", "Each file can be a reference of an avatar only once."));

        List<AvatarReferenceRole> roles = new(asks.Count);
        foreach (AvatarReferenceAsk ask in asks)
        {
            if (!Enum.TryParse(ask.Role, ignoreCase: false, out AvatarReferenceRole role) || !Enum.IsDefined(role)
                || ask.Role!.Any(char.IsDigit))
            {
                return Refuse(AvatarRefusal.BadRequest("reference_role_invalid",
                    $"A reference's role must be one of {string.Join(", ", ProjectAvatarLimits.RolesOf(subjectKind))}.",
                    new Dictionary<string, object?> { ["fileId"] = ask.ProjectFileId }));
            }

            roles.Add(role);
        }

        // ---- The files must be THIS project's own, served, unquarantined rows. ----
        Guid[] ids = [.. asks.Select(a => a.ProjectFileId)];
        Dictionary<Guid, ProjectFile> files = await _db.ProjectFiles
            .Where(f => f.ProjectId == projectId && ids.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, ct);
        HashSet<Guid> held = await OutputGate.HeldFileIdsAsync(_db, projectId, [.. files.Values], ct);
        foreach (Guid id in ids)
        {
            if (!files.TryGetValue(id, out ProjectFile? file) || CsamScanGate.IsHeld(file)
                || file.ModerationStatus == FileModerationStatus.Quarantined || held.Contains(id))
            {
                return Refuse(AvatarRefusal.BadRequest("reference_file_not_found",
                    "Every reference must be a file of this project.",
                    new Dictionary<string, object?> { ["fileId"] = id }));
            }
        }

        // ---- Never take over a file another writer owns. ----
        foreach (Guid id in ids)
        {
            ProjectFile file = files[id];
            if (file.SourceKind is null) continue;
            if (isObject)
            {
                // An object's files stay ordinary media (never re-marked), so a platform-made file is fine; a file a
                // connected source keeps in step is not, for the reason a person's is not.
                if (ObjectSourceKinds.Contains(file.SourceKind, StringComparer.Ordinal)) continue;
                return Refuse(AvatarRefusal.BadRequest("reference_file_source_not_allowed",
                    $"\"{file.OriginalFileName}\" is kept in step with a connected source (a repository, a cloud drive or a computer folder), which may change or remove it. Upload a copy of it to the project and use that instead.",
                    new Dictionary<string, object?> { ["fileId"] = id }));
            }

            if (!string.Equals(file.SourceKind, ProjectAvatarReference.ProjectFileSourceKind, StringComparison.Ordinal))
            {
                return Refuse(AvatarRefusal.BadRequest("reference_file_source_not_allowed",
                    $"\"{file.OriginalFileName}\" is kept in step with a connected source (a repository, a cloud drive, a computer folder or a saved output copy), which may change or remove it. Upload a copy of it to the project and use that instead.",
                    new Dictionary<string, object?> { ["fileId"] = id }));
            }
        }

        // ---- Kinds, roles, counts and sizes. ----
        List<AvatarReferenceKind> kinds = new(asks.Count);
        for (int i = 0; i < asks.Count; i++)
        {
            ProjectFile file = files[ids[i]];
            if (KindOf(file.MimeType) is not { } kind)
            {
                return Refuse(AvatarRefusal.BadRequest("reference_kind_not_allowed",
                    $"\"{file.OriginalFileName}\" is not a JPEG, PNG or WebP picture, an MP4 or MOV video, or a WAV or MP3 recording.",
                    new Dictionary<string, object?> { ["fileId"] = file.Id }));
            }

            if (isObject && kind == AvatarReferenceKind.Audio)
            {
                return Refuse(AvatarRefusal.BadRequest("reference_kind_not_allowed",
                    $"\"{file.OriginalFileName}\" is a recording. An object takes pictures and short videos only.",
                    new Dictionary<string, object?> { ["fileId"] = file.Id }));
            }

            if (!ProjectAvatarLimits.RoleFits(subjectKind, kind, roles[i]))
            {
                return Refuse(AvatarRefusal.BadRequest("reference_role_not_allowed",
                    $"A {kind.ToString().ToLowerInvariant()} cannot be used as {roles[i]}.",
                    new Dictionary<string, object?> { ["fileId"] = file.Id }));
            }

            kinds.Add(kind);
        }

        int images = kinds.Count(k => k == AvatarReferenceKind.Image);
        if (images > maxImages)
            return Refuse(TooMany($"An avatar holds at most {maxImages} pictures."));
        if (kinds.Count(k => k == AvatarReferenceKind.Video) > maxVideos)
            return Refuse(TooMany($"An avatar holds at most {maxVideos} videos."));
        if (kinds.Count(k => k == AvatarReferenceKind.Audio) > maxAudio)
            return Refuse(TooMany($"An avatar holds at most {maxAudio} recordings."));
        if (images == 0)
            return Refuse(AvatarRefusal.BadRequest("no_image_reference", $"Add at least one picture of the {subjectWord}."));

        for (int i = 0; i < asks.Count; i++)
        {
            ProjectFile file = files[ids[i]];
            long max = MaxBytes(kinds[i]);
            if (file.SizeBytes > max)
            {
                return Refuse(AvatarRefusal.BadRequest("references_too_large",
                    $"\"{file.OriginalFileName}\" is larger than {max / (1024 * 1024)} MB, the most a {kinds[i].ToString().ToLowerInvariant()} reference may be.",
                    new Dictionary<string, object?> { ["fileId"] = file.Id }));
            }
        }

        // ---- Safety verdicts: fail closed (AV-5), stricter than voices on purpose. ----
        if (_moderation.Enabled)
        {
            foreach (Guid id in ids)
            {
                ProjectFile file = files[id];
                if (file.ModerationStatus is FileModerationStatus.Unmoderated or FileModerationStatus.Scanning)
                {
                    return Refuse(AvatarRefusal.Conflict("AVATAR_REFERENCE_PENDING",
                        $"\"{file.OriginalFileName}\" is still being checked. Try again in a minute.",
                        new Dictionary<string, object?> { ["fileId"] = id, ["reason"] = "moderation" }));
                }

                if (file.ModerationStatus == FileModerationStatus.Error)
                {
                    return Refuse(AvatarRefusal.Conflict("AVATAR_REFERENCE_MODERATION_ERROR",
                        $"\"{file.OriginalFileName}\" could not be checked, so it cannot be used for an avatar. Upload it again.",
                        new Dictionary<string, object?> { ["fileId"] = id }));
                }
            }
        }

        // ---- Video and audio: the ingest probe's measured duration, frame rate and codec. ----
        List<string> mediaKeys = [.. ids.Select((id, i) => (id, i)).Where(t => kinds[t.i] != AvatarReferenceKind.Image).Select(t => files[t.id].StorageKey)];
        Dictionary<string, MediaSourceProbe> probes = mediaKeys.Count == 0
            ? []
            : await StoredMediaProbes.LoadAsync(_db, projectId, mediaKeys, ct);

        if (_csam.Arachnid.Enabled)
        {
            // Hash matching covers pictures and video only; a recording never gets a Clean verdict, so it is not
            // waited on here (a quarantined one is still refused by the file lookup above). Waiting would loop:
            // every refusal re-queues the recording, and every scan settles it back to NotScanned. The kind is the
            // declared type, so a recording whose probe shows a video stream (cover art, or a mislabelled video)
            // loses the exemption and waits for Clean like a video.
            List<ProjectFile> unscanned = [.. ids
                .Select((id, i) => (File: files[id], Kind: kinds[i]))
                .Where(x => !IsPlainRecording(x.Kind, x.File, probes) && x.File.CsamStatus != CsamScanStatus.Clean)
                .Select(x => x.File)];
            if (unscanned.Count > 0)
            {
                // The one write a refusal makes: a NotScanned row is queued so the sweeper scans it, and a retry passes.
                bool queued = false;
                foreach (ProjectFile file in unscanned.Where(f => f.CsamStatus == CsamScanStatus.NotScanned))
                {
                    file.CsamStatus = CsamScanStatus.Pending;
                    file.CsamScannedAt = null;
                    queued = true;
                }

                if (queued) await _db.SaveChangesAsync(ct);
                ProjectFile first = unscanned[0];
                return Refuse(AvatarRefusal.Conflict("AVATAR_REFERENCE_UNSCANNED",
                    $"\"{first.OriginalFileName}\" has not been scanned yet. It has been queued; try again in a few minutes.",
                    new Dictionary<string, object?> { ["fileId"] = first.Id }));
            }
        }

        if (_publishModeration is not null)
        {
            foreach (Guid id in ids)
            {
                ProjectFile file = files[id];
                if (await _publishModeration.GateStoredCopyAsync(projectId, file.StorageKey, ct) is { } refused)
                {
                    return Refuse(new AvatarRefusal(
                        refused.Code == "CONTENT_BLOCKED" ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status409Conflict,
                        refused.Code ?? "CONTENT_MODERATION_PENDING",
                        refused.Message,
                        new Dictionary<string, object?> { ["fileId"] = id, ["final"] = refused.Final }));
                }
            }
        }

        List<CheckedAvatarReference> checkedRefs = new(asks.Count);
        for (int i = 0; i < asks.Count; i++)
        {
            ProjectFile file = files[ids[i]];
            AvatarReferenceKind kind = kinds[i];
            int? width = null, height = null, durationMs = null;
            if (kind != AvatarReferenceKind.Image)
            {
                if (!probes.TryGetValue(file.StorageKey, out MediaSourceProbe? probe))
                {
                    return Refuse(AvatarRefusal.Conflict("AVATAR_REFERENCE_PENDING",
                        $"\"{file.OriginalFileName}\" is still being prepared. Try again in a minute.",
                        new Dictionary<string, object?> { ["fileId"] = file.Id, ["reason"] = "probe" }));
                }

                if (kind == AvatarReferenceKind.Audio && !probe.HasAudio)
                    return Refuse(OutOfLimits(file, "it has no audio track"));

                if (MediaLimitProblem(kind, probe) is { } problem)
                    return Refuse(OutOfLimits(file, problem));

                durationMs = (int)Math.Round(probe.DurationSec * 1000);
                if (kind == AvatarReferenceKind.Video)
                {
                    width = probe.Width;
                    height = probe.Height;
                }
            }

            // ---- The bytes: once, for the digest and (pictures) the pixel size. ----
            (string? digest, (int W, int H)? size, byte[]? head) = await ReadAsync(projectId, file, kind == AvatarReferenceKind.Image, ct);
            if (digest is null)
            {
                return Refuse(AvatarRefusal.BadRequest("reference_file_not_found",
                    "Every reference must be a file of this project.",
                    new Dictionary<string, object?> { ["fileId"] = file.Id }));
            }

            if (kind == AvatarReferenceKind.Audio && ReferenceAudioFormat.Sniff(head) is null)
                return Refuse(OutOfLimits(file, ReferenceAudioFormat.NotSupportedMessage));

            if (kind == AvatarReferenceKind.Image)
            {
                if (size is not { } px)
                    return Refuse(OutOfLimits(file, "its picture size could not be read"));
                if (ImageLimitProblem(px.W, px.H) is { } problem)
                    return Refuse(OutOfLimits(file, problem));
                width = px.W;
                height = px.H;
            }

            checkedRefs.Add(new CheckedAvatarReference(file, kind, roles[i], i, digest, width, height, durationMs));
        }

        if (!isObject) return new AvatarReferenceCheckOutcome(checkedRefs, null);

        // ---- Objects only, after every other check: no picture may show a person (decision AO-1). ----
        (string? personCheck, AvatarRefusal? shown) = await CheckForPeopleAsync(projectId, organizationId, checkedRefs, ct);
        return shown is not null ? Refuse(shown) : new AvatarReferenceCheckOutcome(checkedRefs, null, personCheck);
    }

    /// <summary>
    /// The person check of an object's references: each picture, and one frame of each video (from a derivative that
    /// already exists), is shown to the Vision provider. Answers the outcome word, or the refusal: a person shown
    /// (<c>400 object_reference_shows_person</c>), a provider that could not answer
    /// (<c>503 OBJECT_PERSON_CHECK_UNAVAILABLE</c>, fail closed), or a video with no frame yet
    /// (<c>409 AVATAR_REFERENCE_PENDING</c>). With no Vision provider nothing is read and the answer is <c>409 VISION_PROVIDER_REQUIRED</c> (never a skip).
    /// </summary>
    private async Task<(string? PersonCheck, AvatarRefusal? Refusal)> CheckForPeopleAsync(
        Guid projectId, Guid organizationId, IReadOnlyList<CheckedAvatarReference> references, CancellationToken ct)
    {
        if (_personCheck is null || !await _personCheck.IsActiveAsync(ct)) return (null, VisionRequired(CanConfigure()));

        foreach (CheckedAvatarReference reference in references)
        {
            ProjectFile file = reference.File;
            byte[]? picture;
            if (reference.Kind == AvatarReferenceKind.Image)
            {
                picture = await ReadBoundedAsync(projectId, file.StorageKey, ProjectAvatarLimits.MaxImageBytes, ct);
                // The bytes shown to the checker must be the bytes attested a moment ago.
                if (picture is null
                    || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(picture)), reference.ContentSha256, StringComparison.Ordinal))
                {
                    return (null, AvatarRefusal.BadRequest("reference_file_not_found",
                        "Every reference must be a file of this project.",
                        new Dictionary<string, object?> { ["fileId"] = file.Id }));
                }
            }
            else
            {
                picture = await ReadVideoFrameAsync(projectId, file, ct);
                if (picture is null)
                {
                    return (null, AvatarRefusal.Conflict("AVATAR_REFERENCE_PENDING",
                        $"\"{file.OriginalFileName}\" is still being prepared. Try again in a minute.",
                        new Dictionary<string, object?> { ["fileId"] = file.Id, ["reason"] = "frames" }));
                }
            }

            AvatarRefusal? refused = await CheckPictureAsync(
                organizationId, projectId, picture, file.OriginalFileName,
                new Dictionary<string, object?> { ["fileId"] = file.Id }, ct);
            if (refused is not null) return (null, refused);
        }

        return (PersonCheckPassed, null);
    }

    /// <summary>
    /// Shows one picture to the default Vision provider. Null when it shows no person; otherwise the refusal: a person
    /// shown (<c>400 object_reference_shows_person</c>), a provider that could not answer
    /// (<c>503 OBJECT_PERSON_CHECK_UNAVAILABLE</c>, fail closed) or no provider (<c>409 VISION_PROVIDER_REQUIRED</c>,
    /// never a skip). Also the check the build worker makes on a still of a video before it leaves for the segmentation model.
    /// </summary>
    /// <param name="label">What the picture is called in the sentence: a file name, or "the frame at 3 s".</param>
    public async Task<AvatarRefusal?> CheckPictureAsync(
        Guid organizationId, Guid projectId, byte[] picture, string label, IReadOnlyDictionary<string, object?>? extra, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(picture);
        if (_personCheck is null) return VisionRequired(CanConfigure());

        ObjectPersonCheckVerdict verdict = await _personCheck.CheckAsync(organizationId, projectId, picture, ct);
        return verdict switch
        {
            ObjectPersonCheckVerdict.NoPerson => null,
            // The provider went away between the first question and this one: nothing was checked, so nothing is kept.
            ObjectPersonCheckVerdict.Skipped => VisionRequired(CanConfigure()),
            ObjectPersonCheckVerdict.Person => AvatarRefusal.BadRequest("object_reference_shows_person",
                $"\"{label}\" shows a person. An object avatar can only hold pictures and videos without people in them. To use a person, create an avatar of that person, with their consent.",
                extra),
            _ => AvatarRefusal.Unavailable("OBJECT_PERSON_CHECK_UNAVAILABLE",
                "The files could not be checked for people right now, so the object was not saved. Try again in a few minutes.",
                extra)
        };
    }

    /// <summary>
    /// One picture that shows frames of a video reference, from a derivative that already exists for exactly these
    /// bytes: the first sheet of its filmstrip (a grid of frames across the whole video, the best view for the check),
    /// else its first still, else its thumbnail. Null when none exists or can be read yet.
    /// </summary>
    private async Task<byte[]?> ReadVideoFrameAsync(Guid projectId, ProjectFile file, CancellationToken ct)
    {
        string prefix = $"projects/{projectId}/";
        List<MediaDerivative> rows = await _db.MediaDerivatives.AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.DeletedAt == null
                        && d.SourceKind == MediaSourceKind.ProjectFile && d.SourceProjectFileId == file.Id
                        && d.SourceStorageKey == file.StorageKey
                        && (d.Kind == MediaDerivativeKind.Filmstrip || d.Kind == MediaDerivativeKind.Stills || d.Kind == MediaDerivativeKind.Thumbnail))
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);

        foreach (MediaDerivative row in rows.Where(r => r.Kind == MediaDerivativeKind.Filmstrip))
        {
            if (string.IsNullOrWhiteSpace(row.StorageKey) || !row.StorageKey.StartsWith(prefix, StringComparison.Ordinal)) continue;
            FilmstripSheets.Sidecar? sidecar = await FilmstripSheets.TryReadAsync(_storage, projectId, row.IndexStorageKey, ct);
            if (sidecar?.Sheets is not { Count: > 0 } sheets) continue;
            if (!FilmstripSheets.TryDeriveSheetKey(row.StorageKey, sheets[0].StorageLeaf, prefix, out string sheetKey)) continue;
            if (await ReadBoundedAsync(projectId, sheetKey, MaxFrameBytes, ct) is { } sheet) return sheet;
        }

        foreach (MediaDerivative row in rows.Where(r => r.Kind == MediaDerivativeKind.Stills))
        {
            IReadOnlyList<StillsIndex.Still> stills = await StillsIndex.ReadAsync(_storage, row, ct);
            if (stills.Count == 0) continue;
            if (await ReadBoundedAsync(projectId, stills[0].StorageKey, MaxFrameBytes, ct) is { } still) return still;
        }

        foreach (MediaDerivative row in rows.Where(r => r.Kind == MediaDerivativeKind.Thumbnail))
        {
            if (string.IsNullOrWhiteSpace(row.StorageKey) || !row.StorageKey.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (await ReadBoundedAsync(projectId, row.StorageKey, MaxFrameBytes, ct) is { } thumbnail) return thumbnail;
        }

        return null;
    }

    /// <summary>The most bytes of a derivative frame picture read for the person check.</summary>
    private const long MaxFrameBytes = 20L * 1024 * 1024;

    /// <summary>The object's bytes when it can be read and is at most <paramref name="maxBytes"/>; otherwise null. Never throws except on cancellation.</summary>
    /// <summary>The bytes of a stored picture of the project (at most the image limit), or null when it cannot be read.</summary>
    public Task<byte[]?> ReadPictureAsync(Guid projectId, string storageKey, CancellationToken ct) =>
        ReadBoundedAsync(projectId, storageKey, ProjectAvatarLimits.MaxImageBytes, ct);

    private async Task<byte[]?> ReadBoundedAsync(Guid projectId, string storageKey, long maxBytes, CancellationToken ct)
    {
        try
        {
            await using Stream stream = await _storage.DownloadAsync(projectId, storageKey, ct);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > maxBytes) return null;
                buffer.Write(chunk, 0, read);
            }

            return buffer.Length == 0 ? null : buffer.ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read an object reference picture of project {ProjectId} for the person check", projectId);
            return null;
        }
    }

    /// <summary>Why a picture of <paramref name="width"/> x <paramref name="height"/> is outside the limits, or null.</summary>
    public static string? ImageLimitProblem(int width, int height)
    {
        if (width < ProjectAvatarLimits.MinImageSidePx || height < ProjectAvatarLimits.MinImageSidePx)
            return $"each side must be at least {ProjectAvatarLimits.MinImageSidePx} pixels";
        if (width > ProjectAvatarLimits.MaxImageSidePx || height > ProjectAvatarLimits.MaxImageSidePx)
            return $"each side must be at most {ProjectAvatarLimits.MaxImageSidePx} pixels";
        double aspect = (double)width / height;
        if (aspect < ProjectAvatarLimits.MinImageAspect || aspect > ProjectAvatarLimits.MaxImageAspect)
            return "it is too tall or too wide (width divided by height must be between 0.4 and 2.5)";
        return null;
    }

    /// <summary>Why a probed video or recording is outside the limits, or null.</summary>
    public static string? MediaLimitProblem(AvatarReferenceKind kind, MediaSourceProbe probe)
    {
        double ms = probe.DurationSec * 1000;
        (int min, int max) = kind == AvatarReferenceKind.Video
            ? (ProjectAvatarLimits.MinVideoMs, ProjectAvatarLimits.MaxVideoMs)
            : (ProjectAvatarLimits.MinAudioMs, ProjectAvatarLimits.MaxAudioMs);
        if (double.IsNaN(ms) || ms < min || ms > max)
            return $"it must last between {min / 1000} and {max / 1000} seconds";
        if (kind != AvatarReferenceKind.Video) return null;

        if (!VideoCodecs.Contains((probe.VideoCodec ?? string.Empty).ToLowerInvariant()))
            return "it must be H.264 or H.265 video";
        double fps = probe.FpsDen > 0 ? (double)probe.FpsNum / probe.FpsDen : 0;
        // 24000/1001 is 23.976023..., which the published floor of 23.976 means to include.
        if (fps < ProjectAvatarLimits.MinVideoFps - 0.001 || fps > ProjectAvatarLimits.MaxVideoFps + 0.001)
            return "its frame rate must be between 23.976 and 60 frames per second";
        return null;
    }

    /// <summary>A recording with no video stream: the only reference hash matching never covers. No probe yet reads as a recording (the probe wait refuses next).</summary>
    private static bool IsPlainRecording(AvatarReferenceKind kind, ProjectFile file, Dictionary<string, MediaSourceProbe> probes) =>
        kind == AvatarReferenceKind.Audio
        && !(probes.TryGetValue(file.StorageKey, out MediaSourceProbe? probe) && !string.IsNullOrWhiteSpace(probe.VideoCodec));

    private static long MaxBytes(AvatarReferenceKind kind) => kind switch
    {
        AvatarReferenceKind.Image => ProjectAvatarLimits.MaxImageBytes,
        AvatarReferenceKind.Video => ProjectAvatarLimits.MaxVideoBytes,
        _ => ProjectAvatarLimits.MaxAudioBytes
    };

    private async Task<(string? Digest, (int W, int H)? Size, byte[]? Head)> ReadAsync(Guid projectId, ProjectFile file, bool image, CancellationToken ct)
    {
        try
        {
            await using Stream stream = await _storage.DownloadAsync(projectId, file.StorageKey, ct);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920];
            byte[]? head = null;
            using MemoryStream? prefix = image ? new MemoryStream() : null;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                head ??= buffer.AsSpan(0, Math.Min(read, 16)).ToArray();
                if (prefix is not null && prefix.Length < ImageHeader.MaxJpegScanBytes)
                    prefix.Write(buffer, 0, (int)Math.Min(read, ImageHeader.MaxJpegScanBytes - prefix.Length));
            }

            string digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            (int, int)? size = null;
            if (prefix is not null)
            {
                prefix.Position = 0;
                size = await ImageHeader.ReadDisplaySizeAsync(prefix, ct: ct);
            }

            return (digest, size, head);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Unreadable bytes are not a reference: refused, never guessed.
            _logger.LogWarning(ex, "Could not read avatar reference file {FileId} of project {ProjectId}", file.Id, projectId);
            return (null, null, null);
        }
    }

    /// <summary>
    /// The <see cref="ReelBolt.Shared.Avatars.AvatarReferenceDigest"/> of the Inference API: streams the file's object and
    /// answers its lower-case hex SHA-256, or null when it cannot be read (the rule then treats the reference as missing).
    /// </summary>
    public async Task<string?> DigestAsync(ProjectFile file, CancellationToken ct) =>
        (await ReadAsync(file.ProjectId, file, image: false, ct)).Digest;

    private static AvatarRefusal TooMany(string message) => AvatarRefusal.BadRequest("too_many_references", message);

    private static AvatarRefusal OutOfLimits(ProjectFile file, string problem) =>
        AvatarRefusal.BadRequest("reference_out_of_limits",
            $"\"{file.OriginalFileName}\" cannot be used: {problem}.",
            new Dictionary<string, object?> { ["fileId"] = file.Id });

    private static AvatarReferenceCheckOutcome Refuse(AvatarRefusal refusal) => new(null, refusal);
}
