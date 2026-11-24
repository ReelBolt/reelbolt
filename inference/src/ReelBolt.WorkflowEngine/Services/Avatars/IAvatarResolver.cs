using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Services.Avatars;

/// <summary>
/// The engine's read of a project's avatars (docs/avatars.md "The engine resolver"; design docs/design/avatars.md
/// "Engine and generation"). A copy of <c>IProjectVoiceResolver</c>: the Inference API owns <c>project_avatars</c> and
/// <c>project_avatar_references</c>; this resolver reads them through the engine's read-only mapping, re-reads the
/// database on every call (never memoized), observes every reference with the shared
/// <see cref="AvatarReferenceObserver"/> and applies the ONE rule, <see cref="ProjectAvatar.UnselectableReason"/>,
/// so it cannot disagree with the API's <c>selectable</c> flag.
/// <para>
/// An avatar is only ever resolved WITHIN the run's project: an id of another project's avatar resolves to
/// <see cref="ResolvedAvatar.ReasonNotFound"/>, never to the other project's avatar. An unusable avatar is never
/// turned into "no avatar": <see cref="ResolveRequiredAsync"/> throws <see cref="AvatarUnusableException"/>, which a
/// step turns into a <see cref="FailureClass.UserInput"/> failure with the code and the reason.
/// </para>
/// <para>Phase 1 wires the resolver into nothing that spends money; phase 2's <c>VideoGenerate</c> calls it.</para>
/// </summary>
public interface IAvatarResolver
{
    /// <summary>Resolves one avatar of the project; <see cref="ResolvedAvatar.Reason"/> says why it is unusable, when it is.</summary>
    Task<ResolvedAvatar> ResolveAsync(Guid projectId, Guid avatarId, CancellationToken ct);

    /// <summary>
    /// Resolves one avatar and fails closed: returns it only when usable, otherwise throws
    /// <see cref="AvatarUnusableException"/> carrying the failure code and the reason. Never returns "no avatar".
    /// </summary>
    Task<ResolvedAvatar> ResolveRequiredAsync(Guid projectId, Guid avatarId, CancellationToken ct);

    /// <summary>Every avatar of the project that may be used RIGHT NOW, oldest first.</summary>
    Task<IReadOnlyList<ResolvedAvatar>> ListSelectableAsync(Guid projectId, CancellationToken ct);

    /// <summary>
    /// Whether avatar <paramref name="avatarId"/> of the project is of a person or of an object
    /// (docs/design/avatar-objects.md), read from the row alone: no reference and no byte is read. Null when the
    /// project has no such avatar. A step asks this first because the deployment's switches differ per kind and are
    /// checked before anything else. The default answers "a person", which is what every avatar was before objects.
    /// </summary>
    Task<AvatarSubjectKind?> SubjectKindAsync(Guid projectId, Guid avatarId, CancellationToken ct) =>
        Task.FromResult<AvatarSubjectKind?>(AvatarSubjectKind.Person);
}

/// <summary>
/// One reference medium of a usable avatar: the project file it is, its kind, role and order. Only ever present on a
/// usable avatar.
/// </summary>
public sealed record ResolvedAvatarReference(
    Guid Id,
    Guid ProjectFileId,
    AvatarReferenceKind Kind,
    AvatarReferenceRole Role,
    int Ordinal,
    string ContentSha256,
    string StorageKey,
    string MimeType,
    int? DurationMs = null);

/// <summary>
/// One avatar as a step sees it. <see cref="References"/> is EMPTY unless <see cref="Usable"/> is true, so a caller
/// cannot send the media of an unusable avatar by reading the wrong field (the voice resolver's
/// <c>RemoteVoiceId</c> rule).
/// </summary>
/// <param name="SubjectKind">A person (the default) or an object (docs/design/avatar-objects.md).</param>
/// <param name="Description">
/// The avatar's own description, as the member typed it (untrusted text); only on a usable avatar. For a VARIANT
/// (docs/design/avatar-objects.md "Avatar variants") the variant's description follows it
/// (<see cref="DescribeVariant"/>), so a prompt built from this field says which variant is meant.
/// </param>
/// <param name="CoversPictures">
/// Whether a usable avatar may be shown in a generated picture (<see cref="ProjectAvatar.CoversPictures"/>): an object
/// always, a person only under the statement version that covers pictures. False on an unusable avatar.
/// </param>
/// <param name="VariantLabel">A variant's label ("Red"); null for an original. Only on a usable avatar.</param>
/// <param name="VariantOfAvatarId">The original a variant belongs to; null for an original, and once the original is deleted.</param>
/// <param name="IdentityFacts">An object's colour, make and model, marks and scale (cleaned); null for a person and when none.</param>
public sealed record ResolvedAvatar(
    Guid Id,
    string Name,
    bool Usable,
    string? Reason,
    IReadOnlyList<ResolvedAvatarReference> References,
    Guid? ProjectVoiceId,
    AvatarDisclosure Disclosure,
    DateTime? ConsentAttestedAt = null,
    AvatarSubjectKind SubjectKind = AvatarSubjectKind.Person,
    string? Description = null,
    bool CoversPictures = false,
    string? VariantLabel = null,
    Guid? VariantOfAvatarId = null,
    AvatarIdentityFacts? IdentityFacts = null)
{
    /// <summary>Reason: no avatar with this id in this project (another project's id included).</summary>
    public const string ReasonNotFound = "avatar_not_found";

    /// <summary>
    /// The description a step sees: the avatar's own description, then, for a variant, <c>Variant: </c> and what sets
    /// it apart (<c>"Red sports car. Variant: matte red paint, black rims"</c>). Either part may be missing; null when
    /// both are. An avatar that is not a variant gets exactly its own description back.
    /// </summary>
    public static string? DescribeVariant(string? description, string? variantDescription)
    {
        string? own = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        string? variant = string.IsNullOrWhiteSpace(variantDescription) ? null : variantDescription.Trim();
        if (variant is null) return description;
        return own is null ? "Variant: " + variant : own + " Variant: " + variant;
    }

    public static ResolvedAvatar NotFound(Guid id) =>
        new(id, string.Empty, false, ReasonNotFound, [], null, AvatarDisclosure.Visible);
}

/// <summary>The step failure codes an unusable avatar maps to (docs/design/avatars.md "Engine and generation").</summary>
public static class AvatarFailureCodes
{
    public const string NotFound = "AVATAR_NOT_FOUND";
    public const string ConsentRevoked = "AVATAR_CONSENT_REVOKED";
    public const string ConsentMissing = "AVATAR_CONSENT_MISSING";

    /// <summary>The resolved model cannot carry the avatar (no reference mode, frames alongside, driving audio).</summary>
    public const string NotSupportedByModel = "AVATAR_NOT_SUPPORTED_BY_MODEL";

    /// <summary>
    /// Generating with an avatar is switched off in this deployment: <c>Avatars:Enabled</c> / <c>Avatars:Generation</c>
    /// for a person, <c>Avatars:Objects:Enabled</c> for an object.
    /// </summary>
    public const string GenerationDisabled = "AVATAR_GENERATION_DISABLED";

    /// <summary>A reference file could not be read from storage; nothing was submitted (a platform fault, refunded).</summary>
    public const string ReferenceUnavailable = "AVATAR_REFERENCE_UNAVAILABLE";

    /// <summary>
    /// The code for a resolver reason: not found, revoked, or (every other rule failure, the reason travelling in the
    /// failure details) consent missing. Every one is <see cref="FailureClass.UserInput"/>.
    /// </summary>
    public static string For(string? reason) => reason switch
    {
        ResolvedAvatar.ReasonNotFound => NotFound,
        ProjectAvatar.ReasonConsentRevoked => ConsentRevoked,
        _ => ConsentMissing,
    };
}

/// <summary>Thrown by <see cref="IAvatarResolver.ResolveRequiredAsync"/> for an avatar that must not be used.</summary>
public sealed class AvatarUnusableException : Exception
{
    public AvatarUnusableException(Guid avatarId, string reason)
        : base($"The avatar cannot be used ({reason}).")
    {
        AvatarId = avatarId;
        Reason = reason;
        Code = AvatarFailureCodes.For(reason);
    }

    public Guid AvatarId { get; }

    /// <summary>The machine-readable reason (<see cref="ProjectAvatar.UnselectableReasons"/> or <see cref="ResolvedAvatar.ReasonNotFound"/>).</summary>
    public string Reason { get; }

    /// <summary>The step failure code (<see cref="AvatarFailureCodes"/>).</summary>
    public string Code { get; }

    /// <summary>An unusable avatar is the customer's input to fix, never a platform failure.</summary>
    public FailureClass FailureClass => FailureClass.UserInput;
}

public sealed class AvatarResolver : IAvatarResolver
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAvatarReferenceDigestReader _digests;
    private readonly TimeProvider _time;

    public AvatarResolver(IServiceScopeFactory scopeFactory, IAvatarReferenceDigestReader digests, TimeProvider? time = null)
    {
        _scopeFactory = scopeFactory;
        _digests = digests;
        _time = time ?? TimeProvider.System;
    }

    public async Task<ResolvedAvatar> ResolveAsync(Guid projectId, Guid avatarId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

        ProjectAvatar? avatar = await db.ProjectAvatars
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == avatarId && a.ProjectId == projectId, ct);
        return avatar is null ? ResolvedAvatar.NotFound(avatarId) : await ProjectAsync(db, avatar, ct);
    }

    public async Task<ResolvedAvatar> ResolveRequiredAsync(Guid projectId, Guid avatarId, CancellationToken ct)
    {
        ResolvedAvatar resolved = await ResolveAsync(projectId, avatarId, ct);
        return resolved.Usable ? resolved : throw new AvatarUnusableException(avatarId, resolved.Reason ?? ProjectAvatar.ReasonConsentMissing);
    }

    public async Task<IReadOnlyList<ResolvedAvatar>> ListSelectableAsync(Guid projectId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

        List<ProjectAvatar> avatars = await db.ProjectAvatars
            .AsNoTracking()
            .Where(a => a.ProjectId == projectId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);

        List<ResolvedAvatar> usable = [];
        foreach (ProjectAvatar avatar in avatars)
        {
            ResolvedAvatar resolved = await ProjectAsync(db, avatar, ct);
            if (resolved.Usable)
                usable.Add(resolved);
        }
        return usable;
    }

    public async Task<AvatarSubjectKind?> SubjectKindAsync(Guid projectId, Guid avatarId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

        return await db.ProjectAvatars
            .AsNoTracking()
            .Where(a => a.Id == avatarId && a.ProjectId == projectId)
            .Select(a => (AvatarSubjectKind?)a.SubjectKind)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>The one place a row becomes a usable-or-not answer; shared by every read so they cannot disagree.</summary>
    private async Task<ResolvedAvatar> ProjectAsync(WorkflowEngineDbContext db, ProjectAvatar avatar, CancellationToken ct)
    {
        List<ProjectAvatarReference> references = await db.ProjectAvatarReferences
            .AsNoTracking()
            .Where(r => r.AvatarId == avatar.Id)
            .OrderBy(r => r.Ordinal)
            .ThenBy(r => r.Id)
            .ToListAsync(ct);

        DateTime now = _time.GetUtcNow().UtcDateTime;
        // The consent, revoke and retention checks come first in the rule, so their answer needs no reference read:
        // an avatar that fails them never has its media read. (An object has only the first: its own attestation.)
        IReadOnlyDictionary<Guid, ProjectAvatar> originals = await AvatarVariantOriginals.LoadAsync(db, avatar.ProjectId, [avatar], ct);
        string? reason = avatar.UnselectableReason(now, [new AvatarReferenceState(AvatarReferenceKind.Image, true, false)], originals);
        if (reason is ProjectAvatar.ReasonConsentMissing or ProjectAvatar.ReasonConsentRevoked or ProjectAvatar.ReasonRetentionExpired)
            return Unusable(avatar, reason);

        IReadOnlyList<AvatarReferenceState> states = await AvatarReferenceObserver.ObserveAsync(
            db, avatar.ProjectId, references, (file, token) => _digests.ReadSha256Async(avatar.ProjectId, file.StorageKey, token), ct);
        reason = avatar.UnselectableReason(now, [.. states], originals);
        if (reason is not null)
            return Unusable(avatar, reason);

        Guid[] fileIds = [.. references.Select(r => r.ProjectFileId).Distinct()];
        Dictionary<Guid, (string StorageKey, string MimeType)> files = await db.ProjectFiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.ProjectId == avatar.ProjectId && fileIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, f => (f.StorageKey, f.MimeType), ct);

        List<ResolvedAvatarReference> resolved = [];
        foreach (ProjectAvatarReference r in references)
        {
            // The observer saw every file a moment ago; a file gone since is missing, never skipped.
            if (!files.TryGetValue(r.ProjectFileId, out var file))
                return Unusable(avatar, ProjectAvatar.ReasonReferenceMissing);
            resolved.Add(new ResolvedAvatarReference(
                r.Id, r.ProjectFileId, r.Kind, r.Role, r.Ordinal, r.ContentSha256, file.StorageKey, file.MimeType, r.DurationMs));
        }

        return new ResolvedAvatar(
            avatar.Id, avatar.Name, true, null, resolved, avatar.ProjectVoiceId, avatar.DisclosureLabel, avatar.ConsentAttestedAt,
            avatar.SubjectKind,
            // An original keeps exactly its stored description; a variant's own description follows it.
            ResolvedAvatar.DescribeVariant(avatar.Description, avatar.VariantDescription),
            avatar.CoversPictures,
            avatar.VariantLabel,
            avatar.VariantOfAvatarId,
            // An object's identity facts (docs/design/avatar-objects.md "Object fidelity"); a person has none.
            avatar.SubjectKind == AvatarSubjectKind.Object ? AvatarIdentityFacts.Parse(avatar.IdentityFacts) : null);
    }

    private static ResolvedAvatar Unusable(ProjectAvatar avatar, string reason) =>
        new(avatar.Id, avatar.Name, false, reason, [], avatar.ProjectVoiceId, avatar.DisclosureLabel, SubjectKind: avatar.SubjectKind);
}
