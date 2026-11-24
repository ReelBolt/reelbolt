using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// A project's avatars (docs/design/avatars.md): a consented likeness of ONE real person, a named set of reference
/// pictures, videos and recordings held under recorded consent, retention, revoke and delete rules like a cloned voice.
///
/// <para>
/// <b>Project-scoped, like voices</b> (AV-1): every action resolves the project through <see cref="IProjectAccessService"/>
/// as its one access check (<c>404</c> across organisations and for an absent project, <c>403</c> for a role too low:
/// View to read, Edit to change, Delete to delete) and then reads only <c>WHERE project_id = @p</c>, so another
/// project's avatar is <c>404</c>, never <c>403</c>.
/// </para>
/// <para>
/// <b>Consent is not optional and not free text.</b> Create and the reference change (a re-attestation) need the
/// statement of <see cref="AvatarConsentStatement"/> verbatim in the locale the form showed, the subject's name, the
/// basis, the adult box (minors refused outright, AV-12), a retention period, a verified email and the plan feature
/// <c>avatars</c>. A public figure may only be the account holder (AV-8). Every reference passes
/// <see cref="AvatarReferenceChecks"/>, which fails closed. Nothing is written on a refusal.
/// </para>
/// <para>
/// <b>Objects</b> (docs/design/avatar-objects.md): an avatar may also be of an object, a product or a place
/// (<c>subjectKind: "Object"</c>). It needs no consent of a person: the member ticks the object statement
/// (<see cref="AvatarObjectStatement"/>), every picture is checked for people (decision AO-1), and it has no retention
/// date and no revoke. Its plan feature is <c>avatar_objects</c>, and a project holds 20 objects beside its 20 people.
/// </para>
/// <para>
/// <b>Switches.</b> While neither <c>Avatars:Enabled</c> (people) nor <c>Avatars:Objects:Enabled</c> (objects) is on,
/// every action answers <c>404</c> before it reads or writes anything. With one on, a row of the other kind is treated
/// as absent (not listed, <c>404</c> by id); only delete still works on it.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/avatars")]
[Authorize]
public partial class ProjectAvatarsController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly AvatarOptions _options;
    private readonly AvatarReferenceChecks _checks;
    private readonly ProjectAvatarDeletion _deletion;
    private readonly ILogger<ProjectAvatarsController> _logger;
    private readonly IOrganizationEntitlements? _entitlements;
    private readonly ObjectAvatarCreator _objectCreator;
    private readonly ReelBolt.Inference.Api.Services.Avatars.Builds.IAvatarBuildService? _builds;
    private readonly IMediaEgressCallPermission _mediaEgressPermission;
    private readonly IAvatarIdentityDescriber? _identityDescriber;

    public ProjectAvatarsController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IProjectAccessService projectAccess,
        IOptions<AvatarOptions> options,
        AvatarReferenceChecks checks,
        ProjectAvatarDeletion deletion,
        ILogger<ProjectAvatarsController> logger,
        IOrganizationEntitlements? entitlements = null,
        ReelBolt.Inference.Api.Services.Avatars.Builds.IAvatarBuildService? builds = null,
        IMediaEgressCallPermission? mediaEgressPermission = null,
        IAvatarIdentityDescriber? identityDescriber = null)
    {
        _identityDescriber = identityDescriber;
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _options = options.Value;
        _checks = checks;
        _deletion = deletion;
        _logger = logger;
        _entitlements = entitlements;
        _objectCreator = new ObjectAvatarCreator(db, checks, options.Value, identityDescriber);
        _builds = builds;
        _mediaEgressPermission = mediaEgressPermission ?? new MediaEgressCallPermission(db);
    }

    /// <summary>The required statement in each language and its version, the limits, and what the plan and the deployment allow.</summary>
    [HttpGet("requirements")]
    public async Task<ActionResult<AvatarRequirementsResponse>> Requirements(Guid projectId, CancellationToken ct)
    {
        if (!_options.AnyEnabled) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        bool entitled = await PlanRefusalAsync(project, AvatarSubjectKind.Person, ct) is null;
        bool objectsEntitled = await PlanRefusalAsync(project, AvatarSubjectKind.Object, ct) is null;
        return Ok(new AvatarRequirementsResponse(
            AvatarConsentStatement.Version,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["en"] = AvatarConsentStatement.English,
                ["it"] = AvatarConsentStatement.Italian
            },
            AvatarConsentStatement.Locales,
            new AvatarLimitsResponse(
                ProjectAvatarLimits.MaxAvatarsPerProject,
                ProjectAvatarLimits.MaxImages,
                ProjectAvatarLimits.MaxVideos,
                ProjectAvatarLimits.MaxAudio,
                ProjectAvatarLimits.MaxImageBytes,
                ProjectAvatarLimits.MinImageSidePx,
                ProjectAvatarLimits.MaxImageSidePx,
                ProjectAvatarLimits.MinImageAspect,
                ProjectAvatarLimits.MaxImageAspect,
                ProjectAvatarLimits.MaxVideoBytes,
                ProjectAvatarLimits.MinVideoMs,
                ProjectAvatarLimits.MaxVideoMs,
                ProjectAvatarLimits.MinVideoFps,
                ProjectAvatarLimits.MaxVideoFps,
                AvatarReferenceChecks.VideoCodecs,
                ProjectAvatarLimits.MaxAudioBytes,
                ProjectAvatarLimits.MinAudioMs,
                ProjectAvatarLimits.MaxAudioMs,
                ProjectAvatarLimits.MinRetentionDays,
                ProjectAvatarLimits.MaxRetentionDays,
                ProjectAvatarLimits.DefaultRetentionDays,
                ProjectAvatarLimits.RevokedGraceDays,
                ProjectAvatarLimits.MaxNameLength,
                ProjectAvatarLimits.MaxDescriptionLength,
                ProjectAvatarLimits.MaxSubjectNameLength),
            new AvatarMimeTypesResponse(ProjectAvatarLimits.ImageMimeTypes, ProjectAvatarLimits.VideoMimeTypes, ProjectAvatarLimits.AudioMimeTypes),
            [.. ProjectAvatarLimits.PersonRoles.Select(r => r.ToString())],
            Enum.GetNames<AvatarConsentBasis>(),
            Enum.GetNames<AvatarDisclosure>(),
            entitled,
            _checks.ModerationEnabled,
            _checks.CsamEnabled,
            new AvatarKindsResponse(
                new AvatarKindResponse(_options.Enabled, entitled),
                new AvatarKindResponse(_options.Objects.Enabled, objectsEntitled)),
            AvatarObjectStatement.Version,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["en"] = AvatarObjectStatement.English,
                ["it"] = AvatarObjectStatement.Italian
            },
            [.. ProjectAvatarLimits.ObjectRoles.Select(r => r.ToString())],
            new AvatarObjectLimitsResponse(ProjectAvatarLimits.MaxObjectImages, ProjectAvatarLimits.MaxObjectVideos),
            await _checks.PersonCheckActiveAsync(ct),
            ToMediaConsent(project),
            LibraryRequirements(project)));
    }

    // ------------------------------------------------------------------ media consent

    /// <summary>
    /// Whether the project's avatar media consent is recorded: the one agreement that reference pictures and videos of
    /// its avatars (people and objects) may be sent to AI providers from the editors and the assistant. View.
    /// </summary>
    [HttpGet("media-consent")]
    public async Task<ActionResult<AvatarMediaConsentResponse>> GetMediaConsent(Guid projectId, CancellationToken ct)
    {
        if (!_options.AnyEnabled) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        return Ok(ToMediaConsent(access.Project!));
    }

    /// <summary>
    /// Records the media consent now, with who gave it. Idempotent: an existing record is kept. A UI-only act: it is
    /// deliberately not an assistant or MCP tool and never a field of another request (Edit, verified email).
    /// </summary>
    [HttpPut("media-consent")]
    public async Task<ActionResult<AvatarMediaConsentResponse>> GrantMediaConsent(Guid projectId, CancellationToken ct)
    {
        if (!_options.AnyEnabled) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;

        Project project = access.Project!;
        if (project.SubjectMediaConsentAt is null)
        {
            project.SubjectMediaConsentAt = DateTime.UtcNow;
            project.SubjectMediaConsentByUserId = _currentUser.UserId;
            await _db.SaveChangesAsync(ct);
        }

        return Ok(ToMediaConsent(project));
    }

    /// <summary>Withdraws the media consent. Idempotent (Edit).</summary>
    [HttpDelete("media-consent")]
    public async Task<ActionResult<AvatarMediaConsentResponse>> RevokeMediaConsent(Guid projectId, CancellationToken ct)
    {
        if (!_options.AnyEnabled) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        Project project = access.Project!;
        if (project.SubjectMediaConsentAt is not null || project.SubjectMediaConsentByUserId is not null)
        {
            project.SubjectMediaConsentAt = null;
            project.SubjectMediaConsentByUserId = null;
            await _db.SaveChangesAsync(ct);
        }

        return Ok(ToMediaConsent(project));
    }

    private static AvatarMediaConsentResponse ToMediaConsent(Project project) =>
        new(project.SubjectMediaConsentAt is not null, project.SubjectMediaConsentAt);

    // ------------------------------------------------------------------ avatars

    [HttpGet]
    public async Task<ActionResult<List<ProjectAvatarResponse>>> List(Guid projectId, CancellationToken ct)
    {
        if (!_options.AnyEnabled) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        // A kind whose switch is off is not listed.
        bool people = _options.Enabled;
        bool objects = _options.Objects.Enabled;
        List<ProjectAvatar> avatars = await _db.ProjectAvatars
            .Where(a => a.ProjectId == projectId
                        && ((people && a.SubjectKind == AvatarSubjectKind.Person) || (objects && a.SubjectKind == AvatarSubjectKind.Object)))
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);
        return Ok(await ToResponsesAsync(projectId, avatars, ct));
    }

    [HttpGet("{avatarId:guid}")]
    public async Task<ActionResult<ProjectAvatarResponse>> Get(Guid projectId, Guid avatarId, CancellationToken ct)
    {
        if (!_options.AnyEnabled) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        ProjectAvatar? avatar = await FindAsync(projectId, avatarId, ct);
        if (avatar is null) return NotFound();
        return Ok((await ToResponsesAsync(projectId, [avatar], ct))[0]);
    }

    /// <summary>
    /// Creates an avatar. Every consent field and every reference check runs before anything is written; a refusal is
    /// <c>400</c>/<c>402</c>/<c>403</c>/<c>409</c> with nothing created.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ProjectAvatarResponse>> Create(Guid projectId, [FromBody] CreateProjectAvatarRequest request, CancellationToken ct)
    {
        if (!_options.AnyEnabled) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        if (!TryParseSubjectKind(request.SubjectKind, out AvatarSubjectKind subjectKind))
            return Bad("subject_kind_invalid", "An avatar is of a person (Person) or of an object (Object).");

        // ---- A variant (docs/design/avatar-objects.md "Avatar variants"): its original decides the kind. ----
        var variantAsk = new AvatarVariantAsk(request.VariantOfAvatarId, request.VariantLabel, request.VariantDescription);
        ProjectAvatar? variantOriginal = null;
        if (variantAsk.Any)
        {
            if (request.VariantOfAvatarId is not { } namedOriginalId) return AvatarVariantRules.FieldsWithoutOriginal().ToResult();
            variantOriginal = await AvatarVariantRules.FindOriginalAsync(_db, _options, projectId, namedOriginalId, ct);
            if (variantOriginal is null) return AvatarVariantRules.OriginalNotFound().ToResult();
            if (string.IsNullOrEmpty(request.SubjectKind)) subjectKind = variantOriginal.SubjectKind;
            else if (subjectKind != variantOriginal.SubjectKind) return AvatarVariantRules.KindMismatch(variantOriginal).ToResult();
        }

        // A kind whose switch is off does not exist here.
        if (!_options.IsEnabled(subjectKind)) return NotFound();

        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;
        if (await PlanRefusalAsync(project, subjectKind, ct) is { } notEntitled) return notEntitled;
        if (subjectKind == AvatarSubjectKind.Object) return await CreateObjectAsync(project, request, ct);

        // ---- Consent first, before any file is read. ----
        if (ConsentRefusal(request.ConsentLocale, request.ConsentStatement, request.SubjectIsAdult) is { } consent)
            return consent.ToResult();
        // A variant of a person is new files of the SAME person: the full form again, but who the person is and on
        // what basis come from the original and cannot be changed here (left out = taken from it; different = refused).
        string? subjectAsked = request.ConsentSubjectName;
        string? basisAsked = request.ConsentBasis;
        if (variantOriginal is not null)
        {
            if (variantOriginal.ConsentRevokedAt is not null) return Revoked();
            if (string.IsNullOrWhiteSpace(subjectAsked)) subjectAsked = variantOriginal.ConsentSubjectName;
            else if (!string.Equals(subjectAsked.Trim(), variantOriginal.ConsentSubjectName, StringComparison.Ordinal))
                return VariantSubjectMismatch("consentSubjectName");
            if (string.IsNullOrWhiteSpace(basisAsked)) basisAsked = variantOriginal.ConsentBasis.ToString();
            else if (!string.Equals(basisAsked, variantOriginal.ConsentBasis.ToString(), StringComparison.Ordinal))
                return VariantSubjectMismatch("consentBasis");
        }

        string subject = subjectAsked?.Trim() ?? string.Empty;
        if (subject.Length == 0 || subject.Length > ProjectAvatarLimits.MaxSubjectNameLength)
            return Bad("consent_subject_required", $"Name the person shown in the files (at most {ProjectAvatarLimits.MaxSubjectNameLength} characters).");
        if (!TryParseName(basisAsked, out AvatarConsentBasis basis))
            return Bad("consent_basis_invalid", "Say whether this is you (Self) or a person whose written permission you have (WrittenPermission).");
        if (request.SubjectIsPublicFigure && basis != AvatarConsentBasis.Self)
            return Bad("public_figure_requires_self", "An avatar of a public figure can only be made by that person, for themselves.");

        AvatarVariantPlacement? variant = null;
        if (variantOriginal is not null)
        {
            (string variantLabel, string variantDescription, AvatarRefusal? variantInvalid) =
                AvatarVariantRules.Validate(request.VariantLabel, request.VariantDescription);
            if (variantInvalid is not null) return variantInvalid.ToResult();
            variant = new AvatarVariantPlacement(variantOriginal, variantLabel, variantDescription);
        }

        string name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 && variant is not null) name = ProjectAvatar.VariantDisplayName(variant.Original.Name, variant.Label);
        if (name.Length == 0 || name.Length > ProjectAvatarLimits.MaxNameLength)
            return Bad("name_invalid", $"Give the avatar a name of at most {ProjectAvatarLimits.MaxNameLength} characters.");
        if (request.Description is { Length: > ProjectAvatarLimits.MaxDescriptionLength })
            return Bad("description_too_long", $"The description can be at most {ProjectAvatarLimits.MaxDescriptionLength} characters.");
        int retentionDays = request.RetentionDays ?? ProjectAvatarLimits.DefaultRetentionDays;
        if (RetentionRefusal(retentionDays) is { } retention) return retention.ToResult();
        if (DisclosureRefusal(request.DisclosureLabel, request.DisclosesOtherwise, out AvatarDisclosure disclosure) is { } disclosureRefused)
            return disclosureRefused.ToResult();

        // ---- The project and its limits (people are counted apart from objects). ----
        if (await QuarantineRefusalAsync(project, ct) is { } quarantined) return quarantined.ToResult();
        if (variant is not null)
        {
            // A variant is not counted toward the project's limit; its original holds a limited number of them.
            if (await AvatarVariantRules.LimitRefusalAsync(_db, variant.Original.Id, ct) is { } variantsFull) return variantsFull.ToResult();
            if (await AvatarVariantRules.LabelTakenAsync(_db, variant.Original.Id, variant.Label, null, ct))
                return AvatarVariantRules.LabelTaken(variant.Label).ToResult();
        }
        else if (await _db.ProjectAvatars.CountAsync(
                     a => a.ProjectId == projectId && a.SubjectKind == AvatarSubjectKind.Person && a.VariantOfAvatarId == null, ct)
                 >= ProjectAvatarLimits.MaxAvatarsPerProject)
            return ConflictCode("AVATAR_LIMIT_REACHED", $"A project can hold at most {ProjectAvatarLimits.MaxAvatarsPerProject} avatars. Delete one first.");
        if (await NameTakenAsync(projectId, name, null, ct))
            return ConflictCode("AVATAR_NAME_TAKEN", $"This project already has an avatar called \"{name}\".");
        if (request.ProjectVoiceId is { } voiceId && await VoiceRefusalAsync(projectId, voiceId, ct) is { } voiceRefused)
            return voiceRefused.ToResult();
        if (request.ConsentDocumentFileId is { } documentId
            && await DocumentRefusalAsync(projectId, documentId, request.References, ct) is { } documentRefused)
            return documentRefused.ToResult();

        // ---- Every reference, fail closed. ----
        (IReadOnlyList<CheckedAvatarReference>? references, AvatarRefusal? refusal) = await _checks.CheckAsync(
            projectId, ToAsks(request.References), ct);
        if (refusal is not null) return refusal.ToResult();

        DateTime now = DateTime.UtcNow;
        var avatar = new ProjectAvatar
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = name,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            VariantOfAvatarId = variant?.Original.Id,
            VariantLabel = variant?.Label,
            VariantDescription = variant?.Description,
            ProjectVoiceId = request.ProjectVoiceId,
            ConsentAttestedByUserId = _currentUser.UserId,
            ConsentAttestedAt = now,
            ConsentStatement = AvatarConsentStatement.For(request.ConsentLocale)!,
            ConsentStatementVersion = AvatarConsentStatement.Version,
            ConsentLocale = request.ConsentLocale,
            ConsentSubjectName = subject,
            ConsentBasis = basis,
            ConsentSubjectAdult = true,
            ConsentDocumentFileId = request.ConsentDocumentFileId,
            RetentionUntil = now.AddDays(retentionDays),
            DisclosureLabel = disclosure,
            CreatedAt = now,
            CreatedByUserId = _currentUser.UserId,
            UpdatedAt = now
        };
        _db.ProjectAvatars.Add(avatar);
        AttachReferences(avatar, references!, now);
        ProjectAvatarEvents.Add(_db, avatar, project.OrganizationId, AvatarEventKind.Created, _currentUser.UserId,
            new Dictionary<string, object>(ReferenceCounts(references!))
            {
                ["via"] = "app",
                ["locale"] = avatar.ConsentLocale,
                ["basis"] = basis == AvatarConsentBasis.Self ? "self" : "written_permission",
                ["public_figure"] = request.SubjectIsPublicFigure,
                ["consent_document"] = request.ConsentDocumentFileId is not null,
                ["retention_days"] = retentionDays
            });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The partial unique index on live names: a concurrent create took the name between check and write.
            if (await NameTakenAsync(projectId, name, avatar.Id, CancellationToken.None))
                return ConflictCode("AVATAR_NAME_TAKEN", $"This project already has an avatar called \"{name}\".");
            if (variant is not null
                && await AvatarVariantRules.LabelTakenAsync(_db, variant.Original.Id, variant.Label, avatar.Id, CancellationToken.None))
                return AvatarVariantRules.LabelTaken(variant.Label).ToResult();
            throw;
        }

        return CreatedAtAction(nameof(Get), new { projectId, avatarId = avatar.Id },
            (await ToResponsesAsync(projectId, [avatar], ct))[0]);
    }

    /// <summary>
    /// Creates an avatar of an OBJECT (docs/design/avatar-objects.md "Avatar REST contract with objects"). The caller
    /// has already passed the switch, access, verified-email and plan checks. Nothing a person's avatar carries is
    /// accepted (no subject name, basis, adult box, retention, release, voice or label); everything else is
    /// <see cref="ObjectAvatarCreator"/>, the one create path the build jobs share: the object statement verbatim, the
    /// limits, every attach check and then the person check.
    /// </summary>
    private async Task<ActionResult<ProjectAvatarResponse>> CreateObjectAsync(Project project, CreateProjectAvatarRequest request, CancellationToken ct)
    {
        Guid projectId = project.Id;
        if (PersonOnlyField(request) is { } field) return NotForObjects(field);

        var variantAsk = new AvatarVariantAsk(request.VariantOfAvatarId, request.VariantLabel, request.VariantDescription);
        ObjectAvatarCreateOutcome created = await _objectCreator.CreateAsync(
            project,
            _currentUser.UserId,
            new ObjectAvatarCreateRequest(
                request.Name, request.Description, request.ConsentLocale, request.ConsentStatement, ToAsks(request.References),
                variantAsk.Any ? variantAsk : null),
            ct);
        if (created.Refusal is { } refused) return refused.ToResult();

        ProjectAvatar avatar = created.Avatar!;
        return CreatedAtAction(nameof(Get), new { projectId, avatarId = avatar.Id },
            (await ToResponsesAsync(projectId, [avatar], ct))[0]);
    }

    /// <summary>A plain edit of the name, description, voice link and disclosure label. Consent and references are untouched.</summary>
    [HttpPatch("{avatarId:guid}")]
    public async Task<ActionResult<ProjectAvatarResponse>> Update(Guid projectId, Guid avatarId, [FromBody] UpdateProjectAvatarRequest request, CancellationToken ct)
    {
        if (!_options.AnyEnabled) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        ProjectAvatar? avatar = await FindAsync(projectId, avatarId, ct);
        if (avatar is null) return NotFound();
        if (LinkedImportEditRefusal(avatar, request) is { } linked) return linked;

        // An object has a name and a description, nothing else to edit.
        if (avatar.SubjectKind == AvatarSubjectKind.Object)
        {
            if (request.ProjectVoiceId is not null) return NotForObjects("projectVoiceId");
            if (request.ClearProjectVoice) return NotForObjects("clearProjectVoice");
            if (request.DisclosureLabel is not null) return NotForObjects("disclosureLabel");
            if (request.DisclosesOtherwise) return NotForObjects("disclosesOtherwise");
        }

        if (request.Name is not null)
        {
            string name = request.Name.Trim();
            if (name.Length == 0 || name.Length > ProjectAvatarLimits.MaxNameLength)
                return Bad("name_invalid", $"Give the avatar a name of at most {ProjectAvatarLimits.MaxNameLength} characters.");
            if (avatar.ConsentRevokedAt is null && await NameTakenAsync(projectId, name, avatar.Id, ct))
                return ConflictCode("AVATAR_NAME_TAKEN", $"This project already has an avatar called \"{name}\".");
            avatar.Name = name;
        }

        if (request.Description is not null)
        {
            if (request.Description.Length > ProjectAvatarLimits.MaxDescriptionLength)
                return Bad("description_too_long", $"The description can be at most {ProjectAvatarLimits.MaxDescriptionLength} characters.");
            avatar.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        }

        // The identity facts of an object (docs/design/avatar-objects.md "Object fidelity"): typed by the owner, cleaned
        // again here. All fields empty clears them; null leaves them.
        if (request.IdentityFacts is not null)
        {
            if (avatar.SubjectKind != AvatarSubjectKind.Object) return Bad("field_not_for_people", "Identity facts belong to objects only.");
            avatar.IdentityFacts = request.IdentityFacts.ToJson();
        }

        // A variant's label and description (docs/design/avatar-objects.md "Avatar variants"); an original has neither.
        if (request.VariantLabel is not null || request.VariantDescription is not null)
        {
            if (avatar.VariantLabel is null && avatar.VariantOfAvatarId is null)
                return Bad(AvatarVariantRules.NotAVariantCode, "Only a variant has a label and a variant description.");
            (string variantLabel, string variantDescription, AvatarRefusal? variantInvalid) = AvatarVariantRules.Validate(
                request.VariantLabel ?? avatar.VariantLabel, request.VariantDescription ?? avatar.VariantDescription);
            if (variantInvalid is not null) return variantInvalid.ToResult();
            if (avatar.VariantOfAvatarId is { } originalId && avatar.ConsentRevokedAt is null
                && !string.Equals(variantLabel, avatar.VariantLabel, StringComparison.Ordinal)
                && await AvatarVariantRules.LabelTakenAsync(_db, originalId, variantLabel, avatar.Id, ct))
                return AvatarVariantRules.LabelTaken(variantLabel).ToResult();
            avatar.VariantLabel = variantLabel;
            avatar.VariantDescription = variantDescription;
        }

        if (request.ClearProjectVoice)
        {
            avatar.ProjectVoiceId = null;
        }
        else if (request.ProjectVoiceId is { } voiceId && voiceId != avatar.ProjectVoiceId)
        {
            if (await VoiceRefusalAsync(projectId, voiceId, ct) is { } voiceRefused) return voiceRefused.ToResult();
            avatar.ProjectVoiceId = voiceId;
        }

        if (request.DisclosureLabel is not null)
        {
            if (DisclosureRefusal(request.DisclosureLabel, request.DisclosesOtherwise, out AvatarDisclosure disclosure) is { } refused)
                return refused.ToResult();
            avatar.DisclosureLabel = disclosure;
        }

        avatar.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConflictCode("AVATAR_CHANGED", "The avatar was changed meanwhile. Reload it and try again.");
        }
        catch (DbUpdateException) when (request.Name is not null)
        {
            if (await NameTakenAsync(projectId, avatar.Name, avatar.Id, CancellationToken.None))
                return ConflictCode("AVATAR_NAME_TAKEN", $"This project already has an avatar called \"{avatar.Name}\".");
            throw;
        }
        catch (DbUpdateException) when (request.VariantLabel is not null && avatar.VariantOfAvatarId is not null && avatar.VariantLabel is not null)
        {
            if (await AvatarVariantRules.LabelTakenAsync(_db, avatar.VariantOfAvatarId.Value, avatar.VariantLabel, avatar.Id, CancellationToken.None))
                return AvatarVariantRules.LabelTaken(avatar.VariantLabel).ToResult();
            throw;
        }

        return Ok((await ToResponsesAsync(projectId, [avatar], ct))[0]);
    }

    /// <summary>
    /// Replaces the references: a re-attestation. The statement is sent again, every attach check runs again, consent is
    /// stamped again and the retention period starts again from now. A revoked avatar cannot be re-attested
    /// (<c>409 AVATAR_CONSENT_REVOKED</c>): withdrawn consent is final for that avatar.
    /// </summary>
    [HttpPut("{avatarId:guid}/references")]
    public async Task<ActionResult<ProjectAvatarResponse>> ReplaceReferences(
        Guid projectId, Guid avatarId, [FromBody] ReplaceAvatarReferencesRequest request, CancellationToken ct)
    {
        if (!_options.AnyEnabled) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        ProjectAvatar? avatar = await FindAsync(projectId, avatarId, ct);
        if (avatar is null) return NotFound();
        if (avatar.IsLibraryImport) return FromLibrary();
        bool isObject = avatar.SubjectKind == AvatarSubjectKind.Object;

        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;
        if (await PlanRefusalAsync(project, avatar.SubjectKind, ct) is { } notEntitled) return notEntitled;
        int retentionDays = request.RetentionDays ?? ProjectAvatarLimits.DefaultRetentionDays;
        if (isObject)
        {
            // The object statement again; an object has no adult box and no retention.
            if (request.SubjectIsAdult) return NotForObjects("subjectIsAdult");
            if (request.RetentionDays is not null) return NotForObjects("retentionDays");
            if (ObjectStatementRefusal(request.ConsentLocale, request.ConsentStatement) is { } statement) return statement.ToResult();
        }
        else
        {
            if (avatar.ConsentRevokedAt is not null) return Revoked();
            if (ConsentRefusal(request.ConsentLocale, request.ConsentStatement, request.SubjectIsAdult) is { } consent)
                return consent.ToResult();
            if (RetentionRefusal(retentionDays) is { } retention) return retention.ToResult();
        }

        if (await QuarantineRefusalAsync(project, ct) is { } quarantined) return quarantined.ToResult();
        if (avatar.ConsentDocumentFileId is { } documentId && request.References?.Any(r => r.ProjectFileId == documentId) == true)
            return Bad("consent_document_invalid", "The signed release cannot also be a reference.");

        AvatarReferenceCheckOutcome outcome = await _checks.CheckAsync(
            projectId, project.OrganizationId, avatar.SubjectKind, ToAsks(request.References), ct);
        if (outcome.Refusal is { } refusal) return refusal.ToResult();
        IReadOnlyList<CheckedAvatarReference>? references = outcome.References;

        DateTime now = DateTime.UtcNow;
        List<ProjectAvatarReference> existing = await _db.ProjectAvatarReferences.Where(r => r.AvatarId == avatarId).ToListAsync(ct);
        HashSet<Guid> keptFiles = [.. references!.Select(r => r.File.Id)];
        List<ProjectAvatarReference> removed = [.. existing.Where(r => !keptFiles.Contains(r.ProjectFileId))];
        _db.ProjectAvatarReferences.RemoveRange(removed);
        foreach (CheckedAvatarReference next in references!)
        {
            if (existing.FirstOrDefault(r => r.ProjectFileId == next.File.Id) is { } same)
            {
                same.Kind = next.Kind;
                same.Role = next.Role;
                same.Ordinal = next.Ordinal;
                same.ContentSha256 = next.ContentSha256;
                same.Width = next.Width;
                same.Height = next.Height;
                same.DurationMs = next.DurationMs;
                same.AttachedAt = now;
            }
            else
            {
                _db.ProjectAvatarReferences.Add(NewReference(avatar.Id, next, now));
            }

            // An object's files stay ordinary media: never re-marked.
            if (!isObject) MarkAsReference(next.File);
        }

        if (!isObject) await ReleaseSourceKindAsync(avatar.Id, removed.Select(r => r.ProjectFileId), ct);

        avatar.ConsentAttestedByUserId = _currentUser.UserId;
        avatar.ConsentAttestedAt = now;
        avatar.ConsentLocale = request.ConsentLocale;
        avatar.UpdatedAt = now;
        Dictionary<string, object> outcomes = new(ReferenceCounts(references!))
        {
            ["via"] = "app",
            ["locale"] = avatar.ConsentLocale,
            ["added"] = references!.Count(r => existing.All(e => e.ProjectFileId != r.File.Id)),
            ["removed"] = removed.Count
        };
        if (isObject)
        {
            avatar.ConsentStatement = AvatarObjectStatement.For(request.ConsentLocale)!;
            avatar.ConsentStatementVersion = AvatarObjectStatement.Version;
            outcomes["subject_kind"] = "object";
            outcomes["person_check"] = outcome.PersonCheck ?? throw new InvalidOperationException("An object was accepted without a person check outcome.");
        }
        else
        {
            // Always the CURRENT statement: re-attesting an avatar made under the first, videos-only text moves it to
            // the version that also covers pictures.
            avatar.ConsentStatement = AvatarConsentStatement.For(request.ConsentLocale)!;
            avatar.ConsentStatementVersion = AvatarConsentStatement.Version;
            avatar.ConsentSubjectAdult = true;
            avatar.RetentionUntil = now.AddDays(retentionDays);
            outcomes["retention_days"] = retentionDays;
            // The variants of this person follow its keep-until date (their consent is the person's, and so is its end).
            if (avatar.VariantOfAvatarId is null)
            {
                foreach (ProjectAvatar variant in await _db.ProjectAvatars
                             .Where(a => a.VariantOfAvatarId == avatar.Id && a.ConsentRevokedAt == null).ToListAsync(ct))
                {
                    variant.RetentionUntil = avatar.RetentionUntil;
                    variant.UpdatedAt = now;
                }
            }
        }

        ProjectAvatarEvents.Add(_db, avatar, project.OrganizationId, AvatarEventKind.ReferencesChanged, _currentUser.UserId, outcomes);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A revoke (or another change) won the race on xmin: nothing of this re-attestation was written.
            _db.ChangeTracker.Clear();
            ProjectAvatar? current = await _db.ProjectAvatars.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == avatarId && a.ProjectId == projectId, CancellationToken.None);
            if (current is null) return NotFound();
            return current.ConsentRevokedAt is not null
                ? Revoked()
                : ConflictCode("AVATAR_CHANGED", "The avatar was changed meanwhile. Reload it and try again.");
        }

        return Ok((await ToResponsesAsync(projectId, [avatar], ct))[0]);
    }

    /// <summary>
    /// Revokes consent: from this instant the avatar is unselectable everywhere (the API flag, the web picker and the
    /// engine resolver read the same rule). Idempotent; the first revoke's time and actor stay. A revoke that races a
    /// re-attestation on the row's <c>xmin</c> retries on a fresh read, so the revoke always lands.
    /// </summary>
    [HttpPost("{avatarId:guid}/revoke-consent")]
    public async Task<ActionResult<ProjectAvatarResponse>> RevokeConsent(Guid projectId, Guid avatarId, CancellationToken ct)
    {
        if (!_options.AnyEnabled) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Guid organizationId = access.Project!.OrganizationId;

        for (int attempt = 1; ; attempt++)
        {
            ProjectAvatar? avatar = await FindAsync(projectId, avatarId, ct);
            if (avatar is null) return NotFound();
            if (avatar.IsLibraryImport) return FromLibrary();
            // No person stands behind an object, so there is no consent to withdraw: it is deleted instead.
            if (avatar.SubjectKind == AvatarSubjectKind.Object)
                return ConflictCode("AVATAR_NOT_A_PERSON", "An object avatar has no consent to withdraw. Delete it instead.");
            if (avatar.ConsentRevokedAt is not null)
            {
                // Already withdrawn: make sure its variants are too (a variant made or missed meanwhile), then answer.
                if (await AvatarVariantRules.RevokeVariantsAsync(_db, avatar, organizationId, avatar.ConsentRevokedByUserId, "app", ct) > 0)
                    await _db.SaveChangesAsync(ct);
                return Ok((await ToResponsesAsync(projectId, [avatar], ct))[0]);
            }

            DateTime now = DateTime.UtcNow;
            avatar.ConsentRevokedAt = now;
            avatar.ConsentRevokedByUserId = _currentUser.UserId;
            avatar.UpdatedAt = now;
            // {"via":"app"}: the in-app revoke. The privacy@ runbook writes the same kind with {"via":"privacy_request"}.
            ProjectAvatarEvents.Add(_db, avatar, organizationId, AvatarEventKind.ConsentRevoked, _currentUser.UserId,
                new Dictionary<string, object> { ["via"] = "app" });
            // The variants of a person share its consent: they are withdrawn in the same save, with the same stamp.
            await AvatarVariantRules.RevokeVariantsAsync(_db, avatar, organizationId, _currentUser.UserId, "app", ct);
            try
            {
                await _db.SaveChangesAsync(ct);
                _logger.LogInformation("Consent revoked for avatar {AvatarId} of project {ProjectId}", avatarId, projectId);
                return Ok((await ToResponsesAsync(projectId, [avatar], ct))[0]);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                _db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// Deletes the avatar: vendor-side copies (best effort), its reference files, the signed release and the row, and
    /// reports what was actually removed. Project Delete permission, as voices. An OBJECT's reference files are
    /// ordinary project files and are kept (<c>references.kept</c>); its row, reference links and stills go. Works on a
    /// row of either kind while any switch is on: removing a likeness is never blocked by a product switch.
    /// </summary>
    [HttpDelete("{avatarId:guid}")]
    public async Task<ActionResult<DeleteProjectAvatarResponse>> Delete(Guid projectId, Guid avatarId, CancellationToken ct)
    {
        if (!_options.AnyEnabled) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Delete, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        AvatarDeleteReport? report = await _deletion.DeleteAsync(
            projectId, avatarId, project.OrganizationId, _currentUser.UserId, AvatarDeleteCause.User, ct);
        if (report is null) return NotFound();

        return Ok(new DeleteProjectAvatarResponse(
            avatarId,
            RowDeleted: true,
            new AvatarReferencesDeleteResponse(report.ReferencesDeleted, report.ReferencesFailed, report.ReferencesKept),
            [.. report.VendorAssets.Select(v => new AvatarVendorAssetDeleteResponse(v.Provider, v.Outcome))],
            report.ConsentDocumentDeleted,
            new AvatarStillsDeleteResponse(report.StillsDeleted, report.StillsFailed)));
    }

    /// <summary>
    /// Where the avatar was used: the runs and takes of the generation ledger that name it
    /// (<c>external_generation_jobs.avatar_id</c>, written from phase 2, or <c>subject_ids_json</c> for a take that
    /// showed several subjects), and how many editor pictures use one of its reference photos. View; free.
    /// </summary>
    [HttpGet("{avatarId:guid}/usage")]
    public async Task<ActionResult<AvatarUsageResponse>> Usage(Guid projectId, Guid avatarId, CancellationToken ct)
    {
        if (!_options.AnyEnabled) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        if (await FindAsync(projectId, avatarId, ct) is null) return NotFound();

        // A take that showed several subjects lists every one of them in subject_ids_json, and names only the person
        // (or the first object) in avatar_id. The array is read here, in memory, over this project's subject takes
        // alone: bounded by the project, and the same on every database provider.
        List<ExternalGenerationJob> candidates = await _db.ExternalGenerationJobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && (j.AvatarId == avatarId || j.SubjectIdsJson != null))
            .OrderBy(j => j.CreatedAt)
            .Take(5000)
            .ToListAsync(ct);
        List<ExternalGenerationJob> jobs = [.. candidates
            .Where(j => j.AvatarId == avatarId || ExternalGenerationJob.ReadSubjectIds(j.SubjectIdsJson).Contains(avatarId))
            .Take(500)];
        Guid[] executionIds = [.. jobs.Select(j => j.ExecutionId).Distinct()];
        Dictionary<Guid, string> workflowNames = executionIds.Length == 0
            ? []
            : await _db.WorkflowExecutions.AsNoTracking()
                .Where(e => e.ProjectId == projectId && executionIds.Contains(e.Id))
                .Select(e => new { e.Id, e.WorkflowDefinition.Name })
                .ToDictionaryAsync(e => e.Id, e => e.Name, ct);

        List<AvatarUsageRunResponse> runs = [.. jobs
            .GroupBy(j => j.ExecutionId)
            .Select(g => new AvatarUsageRunResponse(
                g.Key,
                workflowNames.GetValueOrDefault(g.Key),
                g.Min(j => j.CreatedAt),
                g.Max(j => j.CreatedAt),
                [.. g.Select(j => new AvatarUsageTakeResponse(j.Id, j.StepResultId, j.ClipId, j.Status.ToString(), j.StorageKey is not null, j.CreatedAt))]))
            .OrderByDescending(r => r.LastUsedAt)];

        List<string> referenceKeys = await _db.ProjectAvatarReferences.AsNoTracking()
            .Where(r => r.AvatarId == avatarId)
            .Join(_db.ProjectFiles.AsNoTracking().Where(f => f.ProjectId == projectId), r => r.ProjectFileId, f => f.Id, (r, f) => f.StorageKey)
            .ToListAsync(ct);
        int pictures = referenceKeys.Count == 0
            ? 0
            : await _db.EditImageLayers.AsNoTracking()
                .Where(l => referenceKeys.Contains(l.StorageKey) && l.Kind != EditImageLayerKind.Text)
                .Join(_db.EditImageDocuments.AsNoTracking().Where(d => d.ProjectId == projectId), l => l.DocumentId, d => d.Id, (_, d) => d.Id)
                .Distinct()
                .CountAsync(ct);

        return Ok(new AvatarUsageResponse(avatarId, jobs.Count, runs, pictures, await PicturesGeneratedWithAsync(projectId, avatarId, ct)));
    }

    // ------------------------------------------------------------------ checks

    /// <summary>
    /// The avatar <paramref name="avatarId"/> of the project, or null when there is none OR its kind is switched off: a
    /// row of a kind whose switch is off is treated as absent by every route but delete.
    /// </summary>
    private async Task<ProjectAvatar?> FindAsync(Guid projectId, Guid avatarId, CancellationToken ct)
    {
        ProjectAvatar? avatar = await _db.ProjectAvatars.FirstOrDefaultAsync(a => a.Id == avatarId && a.ProjectId == projectId, ct);
        return avatar is not null && _options.IsEnabled(avatar.SubjectKind) ? avatar : null;
    }

    /// <summary>The plan gate of the kind: <c>avatars</c> for a person, <c>avatar_objects</c> for an object.</summary>
    private async Task<ObjectResult?> PlanRefusalAsync(Project project, AvatarSubjectKind subjectKind, CancellationToken ct)
    {
        if (_entitlements is null) return null;
        OrganizationEntitlementSet set = await _entitlements.GetAsync(project.OrganizationId, ct);
        PlanGateViolation? violation = subjectKind == AvatarSubjectKind.Object
            ? PlanGate.CheckAvatarObjects(set)
            : PlanGate.CheckAvatars(set);
        return violation is null ? null : new ObjectResult(violation.ToBody()) { StatusCode = violation.StatusCode };
    }

    /// <summary><c>subjectKind</c> of a create: absent or <c>Person</c> is a person, <c>Object</c> an object; anything else is refused.</summary>
    private static bool TryParseSubjectKind(string? value, out AvatarSubjectKind subjectKind)
    {
        subjectKind = AvatarSubjectKind.Person;
        return string.IsNullOrEmpty(value) || TryParseName(value, out subjectKind);
    }

    /// <summary>The first field of a create that belongs to a person's avatar and was sent for an object; null when none was.</summary>
    private static string? PersonOnlyField(CreateProjectAvatarRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ConsentSubjectName)) return "consentSubjectName";
        if (!string.IsNullOrWhiteSpace(request.ConsentBasis)) return "consentBasis";
        if (request.SubjectIsAdult) return "subjectIsAdult";
        if (request.RetentionDays is not null) return "retentionDays";
        if (request.ConsentDocumentFileId is not null) return "consentDocumentFileId";
        if (request.ProjectVoiceId is not null) return "projectVoiceId";
        if (request.DisclosureLabel is not null) return "disclosureLabel";
        if (request.DisclosesOtherwise) return "disclosesOtherwise";
        if (request.SubjectIsPublicFigure) return "subjectIsPublicFigure";
        return null;
    }

    private static ObjectResult NotForObjects(string field) =>
        AvatarRefusal.BadRequest("field_not_for_objects",
            $"\"{field}\" belongs to an avatar of a person and cannot be sent for an object.",
            new Dictionary<string, object?> { ["field"] = field }).ToResult();

    /// <summary>The OBJECT statement for the locale, verbatim. Answers the required statement(s) in the body.</summary>
    private static AvatarRefusal? ObjectStatementRefusal(string? locale, string? statement) =>
        ObjectAvatarCreator.StatementRefusal(locale, statement);

    /// <summary>A variant of a person names the same person on the same basis as its original; a different value is refused.</summary>
    private static ObjectResult VariantSubjectMismatch(string field) =>
        AvatarRefusal.BadRequest(AvatarVariantRules.SubjectMismatchCode,
            "A variant shows the same person as its original, on the same basis. Leave the person's name and the basis out, or send the original's.",
            new Dictionary<string, object?> { ["field"] = field }).ToResult();

    /// <summary>The statement for the locale, verbatim, and the adult box. Answers the required statement(s) in the body.</summary>
    private static AvatarRefusal? ConsentRefusal(string? locale, string? statement, bool subjectIsAdult)
    {
        if (!AvatarConsentStatement.Matches(locale, statement))
        {
            return AvatarRefusal.BadRequest("consent_statement_required",
                "Creating an avatar requires the consent statement, verbatim, in the language it was shown in.",
                new Dictionary<string, object?>
                {
                    ["requiredStatement"] = AvatarConsentStatement.For(locale) ?? AvatarConsentStatement.English,
                    ["statementVersion"] = AvatarConsentStatement.Version,
                    ["locale"] = AvatarConsentStatement.For(locale) is null ? "en" : locale,
                    ["statements"] = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["en"] = AvatarConsentStatement.English,
                        ["it"] = AvatarConsentStatement.Italian
                    }
                });
        }

        return subjectIsAdult
            ? null
            : AvatarRefusal.BadRequest("subject_must_be_adult", "Avatars can only be made of people who are 18 or older.");
    }

    private static AvatarRefusal? RetentionRefusal(int days) =>
        days < ProjectAvatarLimits.MinRetentionDays || days > ProjectAvatarLimits.MaxRetentionDays
            ? AvatarRefusal.BadRequest("retention_out_of_range",
                $"Keep the avatar between {ProjectAvatarLimits.MinRetentionDays} and {ProjectAvatarLimits.MaxRetentionDays} days.")
            : null;

    private static AvatarRefusal? DisclosureRefusal(string? label, bool disclosesOtherwise, out AvatarDisclosure disclosure)
    {
        disclosure = AvatarDisclosure.Visible;
        if (label is null) return null;
        if (!TryParseName(label, out disclosure))
            return AvatarRefusal.BadRequest("disclosure_label_invalid", "The disclosure label is Visible or MetadataOnly.");
        return disclosure == AvatarDisclosure.MetadataOnly && !disclosesOtherwise
            ? AvatarRefusal.BadRequest("disclosure_attestation_required",
                "Leave out the visible \"AI-generated\" label only if you disclose the content another way.")
            : null;
    }

    /// <summary>A project with quarantined content takes no new avatar and no new references (one blocked file is reason enough).</summary>
    private async Task<AvatarRefusal?> QuarantineRefusalAsync(Project project, CancellationToken ct)
    {
        ProjectOutputHoldState hold = await OutputGate.ReadAsync(_db, project.Id, ct);
        return project.ContentQuarantinedAt is not null || hold.IsActive
            ? AvatarRefusal.Conflict("PROJECT_CONTENT_QUARANTINED", "Avatars cannot be created or changed in this project.")
            : null;
    }

    private Task<bool> NameTakenAsync(Guid projectId, string name, Guid? except, CancellationToken ct) =>
        _db.ProjectAvatars.AnyAsync(a => a.ProjectId == projectId && a.ConsentRevokedAt == null && a.Name == name
                                         && (except == null || a.Id != except), ct);

    private async Task<AvatarRefusal?> VoiceRefusalAsync(Guid projectId, Guid voiceId, CancellationToken ct)
    {
        ProjectVoice? voice = await _db.ProjectVoices.AsNoTracking().FirstOrDefaultAsync(v => v.Id == voiceId && v.ProjectId == projectId, ct);
        if (voice is null)
            return AvatarRefusal.BadRequest("project_voice_not_found", "The linked voice must be a voice of this project.");
        return voice.IsSelectable(DateTime.UtcNow)
            ? null
            : AvatarRefusal.BadRequest("project_voice_not_selectable", "That voice cannot be used right now. Choose a voice that is ready.");
    }

    /// <summary>The optional signed release: a picture or PDF of this project that is served, and not one of the references.</summary>
    private async Task<AvatarRefusal?> DocumentRefusalAsync(
        Guid projectId, Guid documentId, IReadOnlyList<AvatarReferenceRequest>? references, CancellationToken ct)
    {
        AvatarRefusal invalid = AvatarRefusal.BadRequest("consent_document_invalid",
            "The signed release must be a PDF or a picture in this project, and not one of the reference files.");
        if (references?.Any(r => r.ProjectFileId == documentId) == true) return invalid;

        ProjectFile? file = await _db.ProjectFiles.FirstOrDefaultAsync(f => f.Id == documentId && f.ProjectId == projectId, ct);
        if (file is null || CsamScanGate.IsHeld(file) || file.ModerationStatus == FileModerationStatus.Quarantined
            || await OutputGate.IsFileHeldAsync(_db, file, ct))
            return invalid;
        string mime = (file.MimeType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        return mime == "application/pdf" || mime.StartsWith("image/", StringComparison.Ordinal) ? null : invalid;
    }

    private static bool TryParseName<TEnum>(string? value, out TEnum parsed) where TEnum : struct, Enum
    {
        parsed = default;
        return !string.IsNullOrEmpty(value)
               && Enum.GetNames<TEnum>().Contains(value, StringComparer.Ordinal)
               && Enum.TryParse(value, ignoreCase: false, out parsed);
    }

    /// <summary>Counts per kind, for the audit events (numbers only, never file names or ids).</summary>
    private static Dictionary<string, object> ReferenceCounts(IReadOnlyList<CheckedAvatarReference> references) => new()
    {
        ["references"] = references.Count,
        ["images"] = references.Count(r => r.Kind == AvatarReferenceKind.Image),
        ["videos"] = references.Count(r => r.Kind == AvatarReferenceKind.Video),
        ["audio"] = references.Count(r => r.Kind == AvatarReferenceKind.Audio)
    };

    private static List<AvatarReferenceAsk>? ToAsks(IReadOnlyList<AvatarReferenceRequest>? references) =>
        references?.Select(r => new AvatarReferenceAsk(r.ProjectFileId, r.Role)).ToList();

    // ------------------------------------------------------------------ writes

    private void AttachReferences(ProjectAvatar avatar, IReadOnlyList<CheckedAvatarReference> references, DateTime now)
    {
        foreach (CheckedAvatarReference reference in references)
        {
            _db.ProjectAvatarReferences.Add(NewReference(avatar.Id, reference, now));
            MarkAsReference(reference.File);
        }
    }

    private static ProjectAvatarReference NewReference(Guid avatarId, CheckedAvatarReference reference, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        AvatarId = avatarId,
        ProjectFileId = reference.File.Id,
        Kind = reference.Kind,
        Role = reference.Role,
        Ordinal = reference.Ordinal,
        ContentSha256 = reference.ContentSha256,
        Width = reference.Width,
        Height = reference.Height,
        DurationMs = reference.DurationMs,
        AttachedAt = now
    };

    /// <summary>Badges the file in the media pool. Only an unowned file is marked; the checks refused any other owner.</summary>
    private static void MarkAsReference(ProjectFile file)
    {
        if (file.SourceKind is null) file.SourceKind = ProjectAvatarReference.ProjectFileSourceKind;
    }

    /// <summary>A file no avatar lists any more becomes an ordinary file again.</summary>
    private async Task ReleaseSourceKindAsync(Guid avatarId, IEnumerable<Guid> fileIds, CancellationToken ct)
    {
        Guid[] ids = [.. fileIds.Distinct()];
        if (ids.Length == 0) return;
        // Only an avatar of a person marks its files; an object that lists the same file keeps it an ordinary file.
        HashSet<Guid> stillUsed = [.. await _db.ProjectAvatarReferences
            .Where(r => r.AvatarId != avatarId && ids.Contains(r.ProjectFileId)
                        && _db.ProjectAvatars.Any(a => a.Id == r.AvatarId && a.SubjectKind == AvatarSubjectKind.Person))
            .Select(r => r.ProjectFileId)
            .ToListAsync(ct)];
        foreach (ProjectFile file in await _db.ProjectFiles.Where(f => ids.Contains(f.Id)).ToListAsync(ct))
        {
            if (!stillUsed.Contains(file.Id)
                && string.Equals(file.SourceKind, ProjectAvatarReference.ProjectFileSourceKind, StringComparison.Ordinal))
                file.SourceKind = null;
        }
    }

    // ------------------------------------------------------------------ responses

    private async Task<List<ProjectAvatarResponse>> ToResponsesAsync(Guid projectId, IReadOnlyList<ProjectAvatar> avatars, CancellationToken ct)
    {
        Guid[] avatarIds = [.. avatars.Select(a => a.Id)];
        List<ProjectAvatarReference> references = avatarIds.Length == 0
            ? []
            : await _db.ProjectAvatarReferences.AsNoTracking()
                .Where(r => avatarIds.Contains(r.AvatarId))
                .ToListAsync(ct);
        Guid[] fileIds = [.. references.Select(r => r.ProjectFileId).Distinct()];
        Dictionary<Guid, ProjectFile> files = fileIds.Length == 0
            ? []
            : await _db.ProjectFiles.AsNoTracking()
                .Where(f => f.ProjectId == projectId && fileIds.Contains(f.Id))
                .ToDictionaryAsync(f => f.Id, ct);
        Guid[] voiceIds = [.. avatars.Where(a => a.ProjectVoiceId is not null).Select(a => a.ProjectVoiceId!.Value).Distinct()];
        Dictionary<Guid, ProjectVoice> voices = voiceIds.Length == 0
            ? []
            : await _db.ProjectVoices.AsNoTracking()
                .Where(v => v.ProjectId == projectId && voiceIds.Contains(v.Id))
                .ToDictionaryAsync(v => v.Id, ct);

        // How many variants each listed avatar has (a variant itself has none: one level only).
        Dictionary<Guid, int> variantCounts = avatarIds.Length == 0
            ? []
            : (await _db.ProjectAvatars.AsNoTracking()
                .Where(a => a.ProjectId == projectId && a.VariantOfAvatarId != null && avatarIds.Contains(a.VariantOfAvatarId.Value))
                .Select(a => a.VariantOfAvatarId!.Value)
                .ToListAsync(ct))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        DateTime now = DateTime.UtcNow;
        // A variant of a person is only as usable as its original (the one rule reads the originals).
        IReadOnlyDictionary<Guid, ProjectAvatar> originals = await ReelBolt.Shared.Avatars.AvatarVariantOriginals.LoadAsync(_db, projectId, avatars, ct);
        List<ProjectAvatarResponse> responses = new(avatars.Count);
        foreach (ProjectAvatar avatar in avatars)
        {
            List<ProjectAvatarReference> own = [.. references.Where(r => r.AvatarId == avatar.Id).OrderBy(r => r.Ordinal)];
            // The one observation shared with the engine resolver and the assistant tool (AvatarReferenceObserver):
            // present = the row is in the project AND its bytes hash to the attested digest; held = quarantine,
            // pre-marker output or output hold (held bytes are never read).
            IReadOnlyList<AvatarReferenceState> states = await AvatarReferenceObserver.ObserveAsync(
                _db, projectId, own, _checks.DigestAsync, ct);
            List<AvatarReferenceResponse> rows = new(own.Count);
            for (int i = 0; i < own.Count; i++)
            {
                ProjectAvatarReference reference = own[i];
                AvatarReferenceState state = states[i];
                files.TryGetValue(reference.ProjectFileId, out ProjectFile? file);
                string label = file is null ? "missing"
                    : state.Held ? "held"
                    : state.Present ? "ok" : "changed";
                rows.Add(new AvatarReferenceResponse(
                    reference.Id,
                    reference.ProjectFileId,
                    state.Held ? null : file?.OriginalFileName,
                    reference.Kind.ToString(),
                    reference.Role.ToString(),
                    reference.Ordinal,
                    reference.Width,
                    reference.Height,
                    reference.DurationMs,
                    reference.AttachedAt,
                    label));
            }

            string? reason = avatar.UnselectableReason(now, [.. states], originals);
            ProjectVoice? voice = avatar.ProjectVoiceId is { } vid && voices.TryGetValue(vid, out ProjectVoice? v) ? v : null;
            bool isObject = avatar.SubjectKind == AvatarSubjectKind.Object;
            AvatarReferenceRole primaryRole = isObject ? AvatarReferenceRole.Front : AvatarReferenceRole.Face;
            Guid? primary = own.FirstOrDefault(r => r.Kind == AvatarReferenceKind.Image && r.Role == primaryRole)?.Id
                            ?? own.FirstOrDefault(r => r.Kind == AvatarReferenceKind.Image)?.Id;
            responses.Add(new ProjectAvatarResponse(
                avatar.Id,
                avatar.ProjectId,
                avatar.Name,
                avatar.Description,
                reason is null,
                reason,
                avatar.ProjectVoiceId,
                voice?.Name,
                voice?.IsSelectable(now),
                new AvatarConsentResponse(
                    avatar.ConsentAttestedByUserId,
                    avatar.ConsentAttestedAt,
                    avatar.ConsentStatement,
                    avatar.ConsentStatementVersion,
                    avatar.ConsentLocale,
                    avatar.ConsentSubjectName,
                    avatar.ConsentBasis.ToString(),
                    avatar.ConsentSubjectAdult,
                    avatar.ConsentDocumentFileId,
                    avatar.ConsentRevokedAt,
                    avatar.ConsentRevokedByUserId),
                isObject ? null : avatar.RetentionUntil,
                avatar.DisclosureLabel.ToString(),
                primary,
                rows,
                avatar.CreatedAt,
                avatar.CreatedByUserId,
                avatar.UpdatedAt,
                avatar.SubjectKind.ToString(),
                avatar.CoversPictures,
                avatar.LibraryItemId,
                avatar.IsLibraryImport,
                avatar.VariantOfAvatarId,
                avatar.VariantLabel,
                avatar.VariantDescription,
                variantCounts.GetValueOrDefault(avatar.Id),
                isObject ? AvatarIdentityFacts.Parse(avatar.IdentityFacts) : null));
        }

        return responses;
    }

    private static ObjectResult Bad(string code, string message) => AvatarRefusal.BadRequest(code, message).ToResult();

    private static ObjectResult ConflictCode(string code, string message) => AvatarRefusal.Conflict(code, message).ToResult();

    private static ObjectResult Revoked() =>
        ConflictCode("AVATAR_CONSENT_REVOKED", "Consent for this avatar was withdrawn. Create a new avatar instead.");
}
