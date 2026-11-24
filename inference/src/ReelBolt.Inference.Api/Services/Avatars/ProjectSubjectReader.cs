using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.Avatars;

/// <summary>What the subjects are read for. A picture needs a consent that covers pictures (decision AO-2); a video does not.</summary>
public enum ProjectSubjectPurpose
{
    /// <summary>A generated or edited picture: a person must have been attested under a statement that covers pictures.</summary>
    Picture,

    /// <summary>A generated clip: every statement version covers video.</summary>
    Video
}

/// <summary>
/// How many pictures of the subjects a caller can send (docs/design/avatar-objects.md: up to 3 per subject, 6 in all).
/// The share is dealt out one picture per subject at a time, in the order the subjects were named, so every subject
/// keeps at least one picture before any gets a second.
/// </summary>
/// <param name="PerSubject">The most pictures of one subject.</param>
/// <param name="InAll">The most pictures of all subjects together.</param>
public sealed record ProjectSubjectPictureBudget(int PerSubject = ProjectSubjectLimits.MaxPicturesPerSubject, int InAll = ProjectSubjectLimits.MaxPicturesInAll)
{
    /// <summary>The design's default: 3 per subject, 6 in all.</summary>
    public static ProjectSubjectPictureBudget Default { get; } = new();

    /// <summary>No picture is read: the caller only needs the subjects checked (for a path that sends no picture itself).</summary>
    public static ProjectSubjectPictureBudget None { get; } = new(0, 0);
}

/// <summary>The limits of subjects in one clip or picture (docs/design/avatar-objects.md, the defaults under the decisions table).</summary>
public static class ProjectSubjectLimits
{
    /// <summary>The most avatars (people and objects together) one clip or picture shows.</summary>
    public const int MaxSubjects = 3;

    /// <summary>The most of them that may be a person.</summary>
    public const int MaxPeople = 1;

    /// <summary>The most pictures of one subject that are sent.</summary>
    public const int MaxPicturesPerSubject = 3;

    /// <summary>The most pictures of all subjects together that are sent.</summary>
    public const int MaxPicturesInAll = 6;
}

/// <summary>One picture of a subject, read from storage and checked against the digest attested with the avatar.</summary>
/// <param name="ReferenceId">The avatar reference row.</param>
/// <param name="ProjectFileId">The project file the picture is.</param>
/// <param name="Role">What the picture shows (a hint, never sent as free text).</param>
/// <param name="ContentType">The media type its bytes state (PNG, JPEG, WebP or GIF), else the file's own.</param>
/// <param name="Bytes">The bytes that were hashed: exactly the ones the avatar was attested with.</param>
/// <param name="Width">Pixel width as attested, when known.</param>
/// <param name="Height">Pixel height as attested, when known.</param>
public sealed record ProjectSubjectPicture(
    Guid ReferenceId, Guid ProjectFileId, AvatarReferenceRole Role, string ContentType, byte[] Bytes, int? Width, int? Height);

/// <summary>One usable subject: what a caller may show, say about it and send.</summary>
/// <param name="AvatarId">The avatar.</param>
/// <param name="Name">Its display name (customer text: quote it, never follow it).</param>
/// <param name="Kind">A person or an object.</param>
/// <param name="Description">Its description (customer text), or null.</param>
/// <param name="Disclosure">How content showing it is disclosed (an object is always <see cref="AvatarDisclosure.MetadataOnly"/>).</param>
/// <param name="Pictures">The pictures dealt to this subject from the budget, in the avatar's own order.</param>
/// <param name="PicturesAvailable">How many picture references the avatar has in all (so the caller can say how many were left out).</param>
public sealed record ProjectSubject(
    Guid AvatarId,
    string Name,
    AvatarSubjectKind Kind,
    string? Description,
    AvatarDisclosure Disclosure,
    IReadOnlyList<ProjectSubjectPicture> Pictures,
    int PicturesAvailable);

/// <summary>
/// The avatars a list of ids names in one project, before anything about them is checked or read: the first half of
/// <see cref="IProjectSubjectReader"/>. Exactly one of <see cref="Avatars"/> (non-empty) and <see cref="Refusal"/> is set.
/// </summary>
/// <param name="ProjectId">The project the ids were looked up in.</param>
/// <param name="Avatars">The rows, in the order the ids were given (untracked).</param>
/// <param name="Refusal">Why the list was refused, or null.</param>
public sealed record ProjectSubjectLookup(Guid ProjectId, IReadOnlyList<ProjectAvatar> Avatars, AvatarRefusal? Refusal)
{
    /// <summary>Whether a person is among the subjects.</summary>
    public bool HasPerson => Avatars.Any(a => a.SubjectKind == AvatarSubjectKind.Person);

    /// <summary>The kinds named, each once.</summary>
    public IReadOnlyList<AvatarSubjectKind> Kinds => [.. Avatars.Select(a => a.SubjectKind).Distinct()];

    internal static ProjectSubjectLookup Refused(Guid projectId, AvatarRefusal refusal) => new(projectId, [], refusal);
}

/// <summary>The usable subjects, or the refusal. Exactly one is set.</summary>
/// <param name="Subjects">The subjects, in the order they were named.</param>
/// <param name="Refusal">Why they cannot be used, or null.</param>
public sealed record ProjectSubjectRead(IReadOnlyList<ProjectSubject> Subjects, AvatarRefusal? Refusal)
{
    /// <summary>Whether a person is among the subjects.</summary>
    public bool HasPerson => Subjects.Any(s => s.Kind == AvatarSubjectKind.Person);

    /// <summary>
    /// How the result is disclosed: the person's own label when a person is shown, else
    /// <see cref="AvatarDisclosure.MetadataOnly"/> (objects only).
    /// </summary>
    public AvatarDisclosure Disclosure =>
        Subjects.FirstOrDefault(s => s.Kind == AvatarSubjectKind.Person)?.Disclosure ?? AvatarDisclosure.MetadataOnly;

    internal static ProjectSubjectRead Refused(AvatarRefusal refusal) => new([], refusal);
}

/// <summary>
/// Reads the avatars and objects a caller wants to show in a clip or a picture (docs/design/avatar-objects.md
/// "Avatars and objects in the picture editor"): the Inference API's twin of the engine's <c>IAvatarResolver</c>.
/// Every surface of the API that sends an avatar's media (the picture editor, and the lanes that follow it) goes
/// through here, so they apply the same limits, the same one rule and the same digest check.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two steps, so a caller can put its own gates between them.</b> <see cref="FindAsync"/> answers which avatars the
/// ids name and reads no media: an unknown id, an avatar of another project and an avatar whose kind is switched off
/// are all <c>404 AVATAR_NOT_FOUND</c> (the engine resolver's own code); more than <see cref="ProjectSubjectLimits.MaxSubjects"/> is
/// <c>400 too_many_subjects</c>; the same avatar twice is <c>400 duplicate_subject</c>; more than one person is
/// <c>400 too_many_people</c>. The caller then checks its plan feature per kind and its permission to send media, and
/// only then calls <see cref="ReadAsync(ProjectSubjectLookup, ProjectSubjectPurpose, ProjectSubjectPictureBudget, CancellationToken)"/>,
/// which applies the one rule and reads the pictures.
/// </para>
/// <para>
/// <b>The one rule is not re-written here.</b> A subject is usable exactly when
/// <see cref="ProjectAvatar.UnselectableReason"/> answers null over the states
/// <see cref="AvatarReferenceObserver.ObserveAsync"/> observed: the same two calls the avatars controller makes for its
/// <c>selectable</c> flag and the engine resolver makes before a take. Anything else is <c>409 AVATAR_NOT_USABLE</c>
/// with the rule's <c>reason</c> and the avatar's name. A held file is never read (the observer does not ask for its
/// bytes). For <see cref="ProjectSubjectPurpose.Picture"/> a person attested under a statement that does not cover
/// pictures is <c>409 AVATAR_CONSENT_NO_PICTURES</c>.
/// </para>
/// <para>
/// <b>The pictures are the bytes that were hashed.</b> Each reference is read once: the bytes that produce the digest
/// the rule compares with the attested one are the bytes handed back, so a file cannot change between the check and
/// the use.
/// </para>
/// </remarks>
public interface IProjectSubjectReader
{
    /// <summary>Which avatars <paramref name="avatarIds"/> name in <paramref name="projectId"/>. Reads no media.</summary>
    Task<ProjectSubjectLookup> FindAsync(Guid projectId, IReadOnlyList<Guid>? avatarIds, CancellationToken ct);

    /// <summary>The one rule, the purpose's own rule, then the pictures of the subjects <paramref name="found"/> names.</summary>
    Task<ProjectSubjectRead> ReadAsync(
        ProjectSubjectLookup found, ProjectSubjectPurpose purpose, ProjectSubjectPictureBudget budget, CancellationToken ct);

    /// <summary>Both steps in one call, for a caller with no gate of its own between them.</summary>
    async Task<ProjectSubjectRead> ReadAsync(
        Guid projectId, IReadOnlyList<Guid>? avatarIds, ProjectSubjectPurpose purpose, ProjectSubjectPictureBudget budget, CancellationToken ct)
    {
        ProjectSubjectLookup found = await FindAsync(projectId, avatarIds, ct);
        return found.Refusal is { } refusal ? ProjectSubjectRead.Refused(refusal) : await ReadAsync(found, purpose, budget, ct);
    }
}

/// <inheritdoc />
public sealed class ProjectSubjectReader : IProjectSubjectReader
{
    /// <summary>The code of an id that names no usable avatar of the project (unknown, another project's, or its kind is switched off).</summary>
    public const string NotFoundCode = "AVATAR_NOT_FOUND";

    /// <summary>The code of an empty list.</summary>
    public const string RequiredCode = "subjects_required";

    /// <summary>The code of more avatars than one clip or picture shows.</summary>
    public const string TooManySubjectsCode = "too_many_subjects";

    /// <summary>The code of more than one person.</summary>
    public const string TooManyPeopleCode = "too_many_people";

    /// <summary>The code of the same avatar named twice.</summary>
    public const string DuplicateCode = "duplicate_subject";

    /// <summary>The code of an avatar the one rule refuses; the body carries the rule's <c>reason</c>.</summary>
    public const string NotUsableCode = "AVATAR_NOT_USABLE";

    /// <summary>The code of a person whose consent on record does not cover pictures.</summary>
    public const string ConsentNoPicturesCode = "AVATAR_CONSENT_NO_PICTURES";

    private readonly InferenceApiDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly AvatarOptions _options;
    private readonly ILogger<ProjectSubjectReader> _logger;
    private readonly TimeProvider _clock;

    public ProjectSubjectReader(
        InferenceApiDbContext db,
        IFileStorageService storage,
        IOptions<AvatarOptions> options,
        ILogger<ProjectSubjectReader> logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _storage = storage;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<ProjectSubjectLookup> FindAsync(Guid projectId, IReadOnlyList<Guid>? avatarIds, CancellationToken ct)
    {
        if (avatarIds is not { Count: > 0 })
        {
            return ProjectSubjectLookup.Refused(projectId, AvatarRefusal.BadRequest(RequiredCode,
                "Choose at least one avatar or object."));
        }

        if (avatarIds.Count > ProjectSubjectLimits.MaxSubjects)
        {
            return ProjectSubjectLookup.Refused(projectId, AvatarRefusal.BadRequest(TooManySubjectsCode,
                $"At most {ProjectSubjectLimits.MaxSubjects} avatars and objects can be shown together."));
        }

        if (avatarIds.Distinct().Count() != avatarIds.Count)
        {
            return ProjectSubjectLookup.Refused(projectId, AvatarRefusal.BadRequest(DuplicateCode,
                "The same avatar or object was chosen twice. Choose each one once."));
        }

        // Neither kind on: no avatar exists as far as any caller can tell.
        if (!_options.AnyEnabled) return ProjectSubjectLookup.Refused(projectId, NotFound());

        Guid[] ids = [.. avatarIds];
        List<ProjectAvatar> rows = await _db.ProjectAvatars.AsNoTracking()
            .Where(a => a.ProjectId == projectId && ids.Contains(a.Id))
            .ToListAsync(ct);

        List<ProjectAvatar> ordered = new(ids.Length);
        foreach (Guid id in ids)
        {
            // A row of a kind whose switch is off is treated as absent, exactly as the avatar routes treat it.
            ProjectAvatar? row = rows.FirstOrDefault(a => a.Id == id);
            if (row is null || !_options.IsEnabled(row.SubjectKind)) return ProjectSubjectLookup.Refused(projectId, NotFound());
            ordered.Add(row);
        }

        if (ordered.Count(a => a.SubjectKind == AvatarSubjectKind.Person) > ProjectSubjectLimits.MaxPeople)
        {
            return ProjectSubjectLookup.Refused(projectId, AvatarRefusal.BadRequest(TooManyPeopleCode,
                "At most one person can be shown. Choose one person; the others can be objects."));
        }

        return new ProjectSubjectLookup(projectId, ordered, null);
    }

    /// <inheritdoc />
    public async Task<ProjectSubjectRead> ReadAsync(
        ProjectSubjectLookup found, ProjectSubjectPurpose purpose, ProjectSubjectPictureBudget budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(found);
        ArgumentNullException.ThrowIfNull(budget);
        if (found.Refusal is { } refused) return ProjectSubjectRead.Refused(refused);
        if (found.Avatars.Count == 0)
            return ProjectSubjectRead.Refused(AvatarRefusal.BadRequest(RequiredCode, "Choose at least one avatar or object."));

        Guid projectId = found.ProjectId;
        Guid[] avatarIds = [.. found.Avatars.Select(a => a.Id)];
        List<ProjectAvatarReference> references = await _db.ProjectAvatarReferences.AsNoTracking()
            .Where(r => avatarIds.Contains(r.AvatarId))
            .ToListAsync(ct);

        // Which pictures each subject would send: dealt one per subject at a time, so every subject keeps one first.
        Dictionary<Guid, List<ProjectAvatarReference>> pictureRows = found.Avatars.ToDictionary(
            a => a.Id,
            a => references.Where(r => r.AvatarId == a.Id && r.Kind == AvatarReferenceKind.Image).OrderBy(r => r.Ordinal).ToList());
        Dictionary<Guid, int> share = Deal(found.Avatars, pictureRows, budget);

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        IReadOnlyDictionary<Guid, ProjectAvatar> originals = await ReelBolt.Shared.Avatars.AvatarVariantOriginals.LoadAsync(_db, projectId, found.Avatars, ct);
        List<ProjectSubject> subjects = new(found.Avatars.Count);
        foreach (ProjectAvatar avatar in found.Avatars)
        {
            List<ProjectAvatarReference> own = [.. references.Where(r => r.AvatarId == avatar.Id).OrderBy(r => r.Ordinal)];

            // Consent, revoke and retention come first in the rule, so an avatar failing them has no media read.
            string? reason = avatar.UnselectableReason(now, [new AvatarReferenceState(AvatarReferenceKind.Image, true, false)], originals);
            Dictionary<Guid, byte[]> captured = [];
            if (reason is null)
            {
                HashSet<Guid> wanted = [.. pictureRows[avatar.Id].Take(share[avatar.Id]).Select(r => r.ProjectFileId)];
                IReadOnlyList<AvatarReferenceState> states = await AvatarReferenceObserver.ObserveAsync(
                    _db, projectId, own, (file, token) => DigestAsync(projectId, file, wanted, captured, token), ct);
                reason = avatar.UnselectableReason(now, [.. states], originals);
            }

            if (reason is not null) return ProjectSubjectRead.Refused(NotUsable(avatar, reason, purpose));

            if (purpose == ProjectSubjectPurpose.Picture && !avatar.CoversPictures)
            {
                return ProjectSubjectRead.Refused(AvatarRefusal.Conflict(ConsentNoPicturesCode,
                    "Confirm this person's consent again in the Avatars tab before using them in a picture.",
                    Named(avatar)));
            }

            List<ProjectSubjectPicture> pictures = [];
            foreach (ProjectAvatarReference row in pictureRows[avatar.Id].Take(share[avatar.Id]))
            {
                // The rule passed, so the file was read and its digest matched; bytes missing here mean it could not be
                // kept (larger than a reference picture may be), which is the same as a file that changed.
                if (!captured.TryGetValue(row.ProjectFileId, out byte[]? bytes))
                    return ProjectSubjectRead.Refused(NotUsable(avatar, ProjectAvatar.ReasonReferenceMissing, purpose));
                pictures.Add(new ProjectSubjectPicture(
                    row.Id, row.ProjectFileId, row.Role, ContentTypeOf(bytes), bytes, row.Width, row.Height));
            }

            subjects.Add(new ProjectSubject(
                avatar.Id,
                avatar.Name,
                avatar.SubjectKind,
                string.IsNullOrWhiteSpace(avatar.Description) ? null : avatar.Description,
                avatar.SubjectKind == AvatarSubjectKind.Object ? AvatarDisclosure.MetadataOnly : avatar.DisclosureLabel,
                pictures,
                pictureRows[avatar.Id].Count));
        }

        return new ProjectSubjectRead(subjects, null);
    }

    /// <summary>
    /// How many pictures each subject sends: one per subject in turn until the per-subject cap, the overall cap or the
    /// subject's own pictures run out.
    /// </summary>
    internal static Dictionary<Guid, int> Deal(
        IReadOnlyList<ProjectAvatar> avatars, IReadOnlyDictionary<Guid, List<ProjectAvatarReference>> pictureRows, ProjectSubjectPictureBudget budget)
    {
        Dictionary<Guid, int> share = avatars.ToDictionary(a => a.Id, _ => 0);
        int perSubject = Math.Max(0, budget.PerSubject);
        int left = Math.Max(0, budget.InAll);
        bool dealt = true;
        while (left > 0 && dealt)
        {
            dealt = false;
            foreach (ProjectAvatar avatar in avatars)
            {
                if (left == 0) break;
                if (share[avatar.Id] >= perSubject || share[avatar.Id] >= pictureRows[avatar.Id].Count) continue;
                share[avatar.Id]++;
                left--;
                dealt = true;
            }
        }

        return share;
    }

    /// <summary>
    /// The digest of one reference's bytes as they are now, or null when they cannot be read (the rule then treats the
    /// reference as missing). A picture the caller will send is kept while it is hashed, so the bytes handed back are
    /// the bytes that were checked; anything else is hashed as a stream and not kept.
    /// </summary>
    private async Task<string?> DigestAsync(
        Guid projectId, ProjectFile file, HashSet<Guid> wanted, Dictionary<Guid, byte[]> captured, CancellationToken ct)
    {
        try
        {
            await using Stream stream = await _storage.DownloadAsync(projectId, file.StorageKey, ct);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using MemoryStream? keep = wanted.Contains(file.Id) ? new MemoryStream() : null;
            bool tooLarge = false;
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                if (keep is null || tooLarge) continue;
                if (keep.Length + read > ProjectAvatarLimits.MaxImageBytes) tooLarge = true;
                else keep.Write(buffer, 0, read);
            }

            if (keep is not null && !tooLarge) captured[file.Id] = keep.ToArray();
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail closed: an object that cannot be read counts as missing.
            _logger.LogInformation(ex, "An avatar reference of project {ProjectId} could not be read", projectId);
            return null;
        }
    }

    /// <summary>The media type the bytes themselves state; a picture whose header is not recognised is sent as PNG.</summary>
    internal static string ContentTypeOf(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
        if (PngAlpha.IsPng(bytes)) return "image/png";
        if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P') return "image/webp";
        if (bytes.Length >= 4 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'8') return "image/gif";
        return "image/png";
    }

    private static AvatarRefusal NotFound() => new(StatusCodes.Status404NotFound, NotFoundCode,
        "An avatar or object that was chosen is not in this project.");

    private static AvatarRefusal NotUsable(ProjectAvatar avatar, string reason, ProjectSubjectPurpose purpose)
    {
        Dictionary<string, object?> extra = Named(avatar);
        extra["reason"] = reason;
        return AvatarRefusal.Conflict(NotUsableCode, $"\"{avatar.Name}\" cannot be used {Where(purpose)} right now: {Why(avatar, reason)}", extra);
    }

    private static Dictionary<string, object?> Named(ProjectAvatar avatar) => new(StringComparer.Ordinal)
    {
        ["avatarId"] = avatar.Id,
        ["avatarName"] = avatar.Name
    };

    private static string Where(ProjectSubjectPurpose purpose) => purpose == ProjectSubjectPurpose.Picture ? "in a picture" : "in a clip";

    /// <summary>The rule's reason in plain words, with what the customer can do about it.</summary>
    internal static string Why(ProjectAvatar avatar, string reason) => reason switch
    {
        ProjectAvatar.ReasonConsentMissing => avatar.SubjectKind == AvatarSubjectKind.Object
            ? "its confirmation is not complete. Confirm it again in the Avatars tab."
            : "the consent on record is not complete. Confirm it again in the Avatars tab.",
        ProjectAvatar.ReasonConsentRevoked => "consent for this avatar was withdrawn.",
        ProjectAvatar.ReasonRetentionExpired => "the time this avatar could be kept has ended. Confirm the consent again in the Avatars tab.",
        ProjectAvatar.ReasonReferenceMissing => "one of its files is gone or was changed. Confirm its files again in the Avatars tab.",
        ProjectAvatar.ReasonReferenceHeld => "one of its files is held for review.",
        ProjectAvatar.ReasonNoImageReference => "it has no picture. Add one in the Avatars tab.",
        _ => "it is not ready."
    };
}
