using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.Avatars;

/// <summary>What creating an object avatar needs, whoever asks (the REST route, or a build job for the member who started it).</summary>
/// <param name="Name">The avatar's name; a variant with none is named "original · label".</param>
/// <param name="ConsentLocale">The language the OBJECT statement was shown in.</param>
/// <param name="ConsentStatement">The object statement, verbatim.</param>
/// <param name="References">The files and what each shows.</param>
/// <param name="Variant">Set when the new avatar is a variant of another one.</param>
/// <param name="Via">The audit word for who made it: <c>app</c>, <c>variant_build</c> or <c>extraction</c>.</param>
public sealed record ObjectAvatarCreateRequest(
    string? Name,
    string? Description,
    string? ConsentLocale,
    string? ConsentStatement,
    IReadOnlyList<AvatarReferenceAsk>? References,
    AvatarVariantAsk? Variant = null,
    string Via = ObjectAvatarCreator.ViaApp);

/// <summary>The created avatar, or the refusal (nothing was written).</summary>
public sealed record ObjectAvatarCreateOutcome(ProjectAvatar? Avatar, AvatarRefusal? Refusal, string? PersonCheck = null);

/// <summary>
/// The ONE create path of an OBJECT avatar (docs/design/avatar-objects.md "Avatar REST contract with objects"), shared
/// by <c>POST .../avatars</c> and by the build jobs (a variant made by the picture model, an object pulled out of a
/// video), so every one of them passes the same statement check, the same limits, every attach check of
/// <see cref="AvatarReferenceChecks"/> and the person check, and writes the same rows and the same audit event.
/// <para>
/// The caller has already passed the switch, project access, verified-email and plan checks, which differ by who is
/// asking. The attesting member is passed in: a build job creates the avatar for the member who sent the statement
/// with the request.
/// </para>
/// </summary>
public sealed class ObjectAvatarCreator
{
    /// <summary>Made in the app's Avatars tab (or through the REST route).</summary>
    public const string ViaApp = "app";

    /// <summary>Made by a variant build job.</summary>
    public const string ViaVariantBuild = "variant_build";

    /// <summary>Made by an extraction job (an object pulled out of a video).</summary>
    public const string ViaExtraction = "extraction";

    /// <summary>Made by the platform assistant in the app chat, after the person allowed its card.</summary>
    public const string ViaAssistant = "assistant";

    /// <summary>Made by a connected tool over MCP, with the object statement it sent.</summary>
    public const string ViaMcp = "mcp";

    private readonly InferenceApiDbContext _db;
    private readonly AvatarReferenceChecks _checks;
    private readonly AvatarOptions _options;
    private readonly IAvatarIdentityDescriber? _describer;

    public ObjectAvatarCreator(
        InferenceApiDbContext db, AvatarReferenceChecks checks, IOptions<AvatarOptions> options, IAvatarIdentityDescriber? describer = null)
        : this(db, checks, options.Value, describer)
    {
    }

    internal ObjectAvatarCreator(
        InferenceApiDbContext db, AvatarReferenceChecks checks, AvatarOptions options, IAvatarIdentityDescriber? describer = null)
    {
        _db = db;
        _checks = checks;
        _options = options;
        _describer = describer;
    }

    /// <summary>Null when a default Vision provider is configured; else the <c>VISION_PROVIDER_REQUIRED</c> refusal (the person check is mandatory), so a caller can refuse before it shows or sends anything.</summary>
    public Task<AvatarRefusal?> VisionRefusalAsync(CancellationToken ct) => _checks.VisionRefusalAsync(ct);

    /// <summary>The OBJECT statement for the locale, verbatim. Answers the required statement(s) in the body.</summary>
    public static AvatarRefusal? StatementRefusal(string? locale, string? statement)
    {
        if (AvatarObjectStatement.Matches(locale, statement)) return null;

        return AvatarRefusal.BadRequest("object_statement_required",
            "Creating an object avatar requires the object statement, verbatim, in the language it was shown in.",
            new Dictionary<string, object?>
            {
                ["requiredStatement"] = AvatarObjectStatement.For(locale) ?? AvatarObjectStatement.English,
                ["statementVersion"] = AvatarObjectStatement.Version,
                ["locale"] = AvatarObjectStatement.For(locale) is null ? "en" : locale,
                ["statements"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["en"] = AvatarObjectStatement.English,
                    ["it"] = AvatarObjectStatement.Italian
                }
            });
    }

    /// <summary>A project with quarantined content takes no new avatar and no new references (one blocked file is reason enough).</summary>
    public static async Task<AvatarRefusal?> QuarantineRefusalAsync(InferenceApiDbContext db, Project project, CancellationToken ct)
    {
        ProjectOutputHoldState hold = await OutputGate.ReadAsync(db, project.Id, ct);
        return project.ContentQuarantinedAt is not null || hold.IsActive
            ? AvatarRefusal.Conflict("PROJECT_CONTENT_QUARANTINED", "Avatars cannot be created or changed in this project.")
            : null;
    }

    /// <summary>
    /// Creates the object avatar for <paramref name="userId"/>, or answers the first refusal with nothing written. The
    /// row reuses the consent columns for the member's own attestation (the object statement) and never expires.
    /// </summary>
    public async Task<ObjectAvatarCreateOutcome> CreateAsync(Project project, Guid userId, ObjectAvatarCreateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(request);
        Guid projectId = project.Id;

        if (StatementRefusal(request.ConsentLocale, request.ConsentStatement) is { } statement) return Refuse(statement);

        // ---- A variant: its original (one level only), same kind, a label and a description. ----
        AvatarVariantPlacement? variant = null;
        if (request.Variant is { Any: true } ask)
        {
            if (ask.VariantOfAvatarId is not { } namedId) return Refuse(AvatarVariantRules.FieldsWithoutOriginal());
            ProjectAvatar? original = await AvatarVariantRules.FindOriginalAsync(_db, _options, projectId, namedId, ct);
            if (original is null) return Refuse(AvatarVariantRules.OriginalNotFound());
            if (original.SubjectKind != AvatarSubjectKind.Object) return Refuse(AvatarVariantRules.KindMismatch(original));
            (string label, string description, AvatarRefusal? invalid) = AvatarVariantRules.Validate(ask.VariantLabel, ask.VariantDescription);
            if (invalid is not null) return Refuse(invalid);
            variant = new AvatarVariantPlacement(original, label, description);
        }

        string name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 && variant is not null) name = ProjectAvatar.VariantDisplayName(variant.Original.Name, variant.Label);
        if (name.Length == 0 || name.Length > ProjectAvatarLimits.MaxNameLength)
            return Refuse(AvatarRefusal.BadRequest("name_invalid", $"Give the avatar a name of at most {ProjectAvatarLimits.MaxNameLength} characters."));
        if (request.Description is { Length: > ProjectAvatarLimits.MaxDescriptionLength })
            return Refuse(AvatarRefusal.BadRequest("description_too_long", $"The description can be at most {ProjectAvatarLimits.MaxDescriptionLength} characters."));

        // ---- The project and its limits (objects are counted apart from people; variants are not counted). ----
        if (await QuarantineRefusalAsync(_db, project, ct) is { } quarantined) return Refuse(quarantined);
        if (variant is not null)
        {
            if (await AvatarVariantRules.LimitRefusalAsync(_db, variant.Original.Id, ct) is { } full) return Refuse(full);
            if (await AvatarVariantRules.LabelTakenAsync(_db, variant.Original.Id, variant.Label, null, ct))
                return Refuse(AvatarVariantRules.LabelTaken(variant.Label));
        }
        else if (await _db.ProjectAvatars.CountAsync(
                     a => a.ProjectId == projectId && a.SubjectKind == AvatarSubjectKind.Object && a.VariantOfAvatarId == null, ct)
                 >= ProjectAvatarLimits.MaxObjectsPerProject)
        {
            return Refuse(AvatarRefusal.Conflict("AVATAR_LIMIT_REACHED",
                $"A project can hold at most {ProjectAvatarLimits.MaxObjectsPerProject} object avatars. Delete one first."));
        }

        if (await NameTakenAsync(projectId, name, null, ct)) return Refuse(NameTaken(name));

        // ---- Every reference, fail closed, then the person check. ----
        AvatarReferenceCheckOutcome outcome = await _checks.CheckAsync(
            projectId, project.OrganizationId, AvatarSubjectKind.Object, request.References, ct);
        if (outcome.Refusal is not null) return Refuse(outcome.Refusal);
        IReadOnlyList<CheckedAvatarReference> references = outcome.References!;

        // The identity facts (docs/design/avatar-objects.md "Object fidelity"): one Vision call over up to three of the
        // pictures, fail open (no provider or any error = no facts, never a refusal).
        AvatarIdentityFacts? identityFacts = await DeriveIdentityFactsAsync(project, references, ct);

        DateTime now = DateTime.UtcNow;
        var avatar = new ProjectAvatar
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            SubjectKind = AvatarSubjectKind.Object,
            Name = name,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            IdentityFacts = identityFacts?.ToJson(),
            VariantOfAvatarId = variant?.Original.Id,
            VariantLabel = variant?.Label,
            VariantDescription = variant?.Description,
            // The consent columns hold the member's own, lighter attestation (the object statement); the person-only
            // ones carry their neutral value and are never read for an object.
            ConsentAttestedByUserId = userId,
            ConsentAttestedAt = now,
            ConsentStatement = AvatarObjectStatement.For(request.ConsentLocale)!,
            ConsentStatementVersion = AvatarObjectStatement.Version,
            ConsentLocale = request.ConsentLocale!,
            ConsentSubjectName = string.Empty,
            ConsentBasis = AvatarConsentBasis.Self,
            ConsentSubjectAdult = false,
            RetentionUntil = ProjectAvatar.NoRetention,
            DisclosureLabel = AvatarDisclosure.MetadataOnly,
            CreatedAt = now,
            CreatedByUserId = userId,
            UpdatedAt = now
        };
        _db.ProjectAvatars.Add(avatar);
        // An object's files stay ordinary media: they are linked, never re-marked as avatar references.
        foreach (CheckedAvatarReference reference in references)
        {
            _db.ProjectAvatarReferences.Add(new ProjectAvatarReference
            {
                Id = Guid.NewGuid(),
                AvatarId = avatar.Id,
                ProjectFileId = reference.File.Id,
                Kind = reference.Kind,
                Role = reference.Role,
                Ordinal = reference.Ordinal,
                ContentSha256 = reference.ContentSha256,
                Width = reference.Width,
                Height = reference.Height,
                DurationMs = reference.DurationMs,
                AttachedAt = now
            });
        }

        Dictionary<string, object> outcomes = new()
        {
            ["references"] = references.Count,
            ["images"] = references.Count(r => r.Kind == AvatarReferenceKind.Image),
            ["videos"] = references.Count(r => r.Kind == AvatarReferenceKind.Video),
            ["audio"] = references.Count(r => r.Kind == AvatarReferenceKind.Audio),
            ["via"] = request.Via,
            ["locale"] = avatar.ConsentLocale,
            ["subject_kind"] = "object",
            ["person_check"] = outcome.PersonCheck ?? throw new InvalidOperationException("An object was accepted without a person check outcome.")
        };
        if (variant is not null) outcomes["variant"] = true;
        ProjectAvatarEvents.Add(_db, avatar, project.OrganizationId, AvatarEventKind.Created, userId, outcomes);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The partial unique indexes on live names and on a variant's label: a concurrent create took one between
            // the check and the write. Nothing of this create was saved.
            DetachPending(avatar);
            if (await NameTakenAsync(projectId, name, avatar.Id, CancellationToken.None)) return Refuse(NameTaken(name));
            if (variant is not null
                && await AvatarVariantRules.LabelTakenAsync(_db, variant.Original.Id, variant.Label, avatar.Id, CancellationToken.None))
                return Refuse(AvatarVariantRules.LabelTaken(variant.Label));
            throw;
        }

        return new ObjectAvatarCreateOutcome(avatar, null, outcome.PersonCheck ?? throw new InvalidOperationException("An object was accepted without a person check outcome."));
    }

    private async Task<AvatarIdentityFacts?> DeriveIdentityFactsAsync(
        Project project, IReadOnlyList<CheckedAvatarReference> references, CancellationToken ct)
    {
        if (_describer is null) return null;
        List<byte[]> pictures = [];
        foreach (CheckedAvatarReference reference in references.Where(r => r.Kind == AvatarReferenceKind.Image))
        {
            if (pictures.Count >= AvatarIdentityDescriber.MaxPictures) break;
            if (await _checks.ReadPictureAsync(project.Id, reference.File.StorageKey, ct) is { } picture) pictures.Add(picture);
        }

        return await _describer.DescribeAsync(project.OrganizationId, project.Id, pictures, ct);
    }

    /// <summary>Drops the rows of a create whose save failed from the unit of work, so a later save does not retry them.</summary>
    private void DetachPending(ProjectAvatar avatar)
    {
        foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry in _db.ChangeTracker.Entries().ToList())
        {
            bool own = entry.Entity switch
            {
                ProjectAvatar a => a.Id == avatar.Id,
                ProjectAvatarReference r => r.AvatarId == avatar.Id,
                ProjectAvatarEvent e => e.AvatarId == avatar.Id,
                _ => false
            };
            if (own && entry.State == EntityState.Added) entry.State = EntityState.Detached;
        }
    }

    private Task<bool> NameTakenAsync(Guid projectId, string name, Guid? except, CancellationToken ct) =>
        _db.ProjectAvatars.AnyAsync(a => a.ProjectId == projectId && a.ConsentRevokedAt == null && a.Name == name
                                         && (except == null || a.Id != except), ct);

    private static AvatarRefusal NameTaken(string name) =>
        AvatarRefusal.Conflict("AVATAR_NAME_TAKEN", $"This project already has an avatar called \"{name}\".");

    private static ObjectAvatarCreateOutcome Refuse(AvatarRefusal refusal) => new(null, refusal);
}
