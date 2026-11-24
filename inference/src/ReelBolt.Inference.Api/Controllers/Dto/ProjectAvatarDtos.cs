using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// One reference to attach: a file of the project and what it shows. A person: <c>Face</c>, <c>HalfBody</c>,
/// <c>FullBody</c>, <c>Motion</c>, <c>Speech</c>. An object: <c>Front</c>, <c>Angle</c>, <c>Detail</c>, <c>Motion</c>.
/// </summary>
public sealed record AvatarReferenceRequest(Guid ProjectFileId, string Role);

/// <summary>
/// Creates an avatar (docs/design/avatars.md "Consent and safety"). <paramref name="ConsentStatement"/> must equal the
/// statement of <paramref name="ConsentLocale"/> (<c>en</c> or <c>it</c>) verbatim, as served by
/// <c>GET .../avatars/requirements</c>. <paramref name="ConsentBasis"/> is <c>Self</c> or <c>WrittenPermission</c>.
/// A public figure may only be the account holder (<paramref name="SubjectIsPublicFigure"/> with basis <c>Self</c>, AV-8).
/// <paramref name="DisclosureLabel"/> <c>MetadataOnly</c> needs <paramref name="DisclosesOtherwise"/>.
/// <para>
/// <paramref name="SubjectKind"/> <c>Object</c> (docs/design/avatar-objects.md) creates an avatar of an object: only
/// <paramref name="Name"/>, <paramref name="Description"/>, <paramref name="References"/> and the OBJECT statement
/// (<paramref name="ConsentStatement"/> + <paramref name="ConsentLocale"/>, as served in <c>objectStatements</c>) are
/// read. Every other field belongs to a person and is refused when sent (<c>400 field_not_for_objects</c>).
/// <paramref name="RetentionDays"/> omitted means the default <see cref="ProjectAvatarLimits.DefaultRetentionDays"/>
/// for a person.
/// </para>
/// </summary>
public sealed record CreateProjectAvatarRequest(
    string Name,
    string ConsentStatement,
    string ConsentLocale,
    string? ConsentSubjectName,
    string? ConsentBasis,
    bool SubjectIsAdult,
    IReadOnlyList<AvatarReferenceRequest> References,
    int? RetentionDays = null,
    string? Description = null,
    Guid? ConsentDocumentFileId = null,
    Guid? ProjectVoiceId = null,
    string? DisclosureLabel = null,
    bool DisclosesOtherwise = false,
    bool SubjectIsPublicFigure = false,
    string? SubjectKind = null,
    Guid? VariantOfAvatarId = null,
    string? VariantLabel = null,
    string? VariantDescription = null);

/// <summary>
/// A plain edit: name, description, voice link and disclosure label. Every field is optional; a null field is left as
/// it is. <paramref name="Description"/> <c>""</c> clears it; <paramref name="ClearProjectVoice"/> unlinks the voice.
/// An object takes <paramref name="Name"/> and <paramref name="Description"/> only (<c>400 field_not_for_objects</c> otherwise).
/// </summary>
public sealed record UpdateProjectAvatarRequest(
    string? Name = null,
    string? Description = null,
    Guid? ProjectVoiceId = null,
    bool ClearProjectVoice = false,
    string? DisclosureLabel = null,
    bool DisclosesOtherwise = false,
    string? VariantLabel = null,
    string? VariantDescription = null,
    ReelBolt.Shared.Avatars.AvatarIdentityFacts? IdentityFacts = null);

/// <summary>
/// Derives an object's identity facts from its pictures (docs/design/avatar-objects.md "Object fidelity"). Without
/// <paramref name="Overwrite"/> only fields the owner left empty are filled; with it every field is replaced.
/// </summary>
public sealed record DeriveIdentityFactsRequest(bool Overwrite = false);

/// <summary>
/// Changes the references: a re-attestation, so the full statement is sent again with the new list, and the retention
/// period starts again from now (never a silent extension). For an object the OBJECT statement is sent again; it has
/// no adult box and no retention (<c>400 field_not_for_objects</c> when either is sent).
/// </summary>
public sealed record ReplaceAvatarReferencesRequest(
    string ConsentStatement,
    string ConsentLocale,
    bool SubjectIsAdult,
    IReadOnlyList<AvatarReferenceRequest> References,
    int? RetentionDays = null);

/// <summary>One reference as listed.</summary>
/// <param name="State"><c>ok</c>, <c>missing</c> (the file is gone), <c>changed</c> (its bytes no longer hash to the attested digest) or <c>held</c> (quarantined, a pre-marker output or under the output hold).</param>
public sealed record AvatarReferenceResponse(
    Guid Id,
    Guid ProjectFileId,
    string? FileName,
    string Kind,
    string Role,
    int Ordinal,
    int? Width,
    int? Height,
    int? DurationMs,
    DateTime AttachedAt,
    string State);

/// <summary>The consent record of an avatar.</summary>
public sealed record AvatarConsentResponse(
    Guid? AttestedByUserId,
    DateTime? AttestedAt,
    string Statement,
    string StatementVersion,
    string Locale,
    string SubjectName,
    string Basis,
    bool SubjectIsAdult,
    Guid? DocumentFileId,
    DateTime? RevokedAt,
    Guid? RevokedByUserId);

/// <summary>
/// An avatar. <see cref="Selectable"/> and <see cref="UnselectableReason"/> come from <c>ProjectAvatar.UnselectableReason</c>, the one rule.
/// </summary>
/// <param name="RetentionUntil">When a person's avatar is deleted whether or not anyone asks; null for an object, which has no keep-until date.</param>
/// <param name="SubjectKind"><c>Person</c> or <c>Object</c>.</param>
/// <param name="CoversPictures">
/// Whether the avatar may be shown in a generated picture: an object always, a person only when the consent on record
/// was given under the statement version that covers pictures (else it must be confirmed again first).
/// </param>
/// <param name="VariantOfAvatarId">The original this avatar is a variant of; null for an original (and once the original is deleted).</param>
/// <param name="VariantLabel">A variant's short label ("Red"); null for an original.</param>
/// <param name="VariantDescription">What sets a variant apart from its original; null for an original.</param>
/// <param name="VariantCount">How many variants this avatar has (always 0 for a variant: one level only).</param>
/// <param name="IdentityFacts">An object's identity facts (colour, make and model, marks, scale); null for a person and when none.</param>
public sealed record ProjectAvatarResponse(
    Guid Id,
    Guid ProjectId,
    string Name,
    string? Description,
    bool Selectable,
    string? UnselectableReason,
    Guid? ProjectVoiceId,
    string? ProjectVoiceName,
    bool? ProjectVoiceSelectable,
    AvatarConsentResponse Consent,
    DateTime? RetentionUntil,
    string DisclosureLabel,
    Guid? PrimaryReferenceId,
    IReadOnlyList<AvatarReferenceResponse> References,
    DateTime CreatedAt,
    Guid CreatedByUserId,
    DateTime UpdatedAt,
    string SubjectKind = "Person",
    bool CoversPictures = false,
    // Workspace avatar library (docs/design/avatar-objects.md): the item a linked import follows, and whether it is one.
    Guid? LibraryItemId = null,
    bool FromLibrary = false,
    Guid? VariantOfAvatarId = null,
    string? VariantLabel = null,
    string? VariantDescription = null,
    int VariantCount = 0,
    ReelBolt.Shared.Avatars.AvatarIdentityFacts? IdentityFacts = null);

/// <summary>The reference-file part of a delete report.</summary>
public sealed record AvatarReferencesDeleteResponse(int Deleted, int Failed, int Kept);

/// <summary>One vendor-side copy's delete outcome.</summary>
public sealed record AvatarVendorAssetDeleteResponse(string? Provider, string Outcome);

/// <summary>Phase 3: the stills made from the avatar that were removed (<c>failed</c> -1 = could not be listed; the storage sweep removes them).</summary>
public sealed record AvatarStillsDeleteResponse(int Deleted, int Failed);

/// <summary>What deleting an avatar actually removed.</summary>
public sealed record DeleteProjectAvatarResponse(
    Guid Id,
    bool RowDeleted,
    AvatarReferencesDeleteResponse References,
    IReadOnlyList<AvatarVendorAssetDeleteResponse> VendorAssets,
    bool ConsentDocumentDeleted,
    AvatarStillsDeleteResponse? Stills = null);

/// <summary>Every limit the create flow shows and enforces (<c>ProjectAvatarLimits</c>).</summary>
public sealed record AvatarLimitsResponse(
    int MaxAvatarsPerProject,
    int MaxImages,
    int MaxVideos,
    int MaxAudio,
    long MaxImageBytes,
    int MinImageSidePx,
    int MaxImageSidePx,
    double MinImageAspect,
    double MaxImageAspect,
    long MaxVideoBytes,
    int MinVideoMs,
    int MaxVideoMs,
    double MinVideoFps,
    double MaxVideoFps,
    IReadOnlyList<string> VideoCodecs,
    long MaxAudioBytes,
    int MinAudioMs,
    int MaxAudioMs,
    int MinRetentionDays,
    int MaxRetentionDays,
    int DefaultRetentionDays,
    int RevokedGraceDays,
    int MaxNameLength,
    int MaxDescriptionLength,
    int MaxSubjectNameLength);

/// <summary>The accepted MIME types per reference kind.</summary>
public sealed record AvatarMimeTypesResponse(IReadOnlyList<string> Image, IReadOnlyList<string> Video, IReadOnlyList<string> Audio);

/// <summary>One kind of avatar in this deployment and on this plan.</summary>
/// <param name="Enabled">The operator switch of the kind is on (<c>Avatars:Enabled</c> for people, <c>Avatars:Objects:Enabled</c> for objects).</param>
/// <param name="Entitled">The workspace's plan includes creating the kind (<c>avatars</c> / <c>avatar_objects</c>).</param>
public sealed record AvatarKindResponse(bool Enabled, bool Entitled);

/// <summary>The two kinds of avatar: people and objects.</summary>
public sealed record AvatarKindsResponse(AvatarKindResponse Person, AvatarKindResponse Object);

/// <summary>The counts an object avatar is held to (sizes, lengths and types are the shared <see cref="AvatarLimitsResponse"/>).</summary>
public sealed record AvatarObjectLimitsResponse(int MaxImages, int MaxVideos);

/// <summary>
/// The project-level consent to send avatar reference media to AI providers from the editors and the assistant
/// (<c>GET</c> / <c>PUT</c> / <c>DELETE .../avatars/media-consent</c>). Given only in the app.
/// </summary>
public sealed record AvatarMediaConsentResponse(bool Consented, DateTime? ConsentedAt);

/// <summary>
/// What the create flow needs, from the server, never typed in the client: the required statement in each language
/// and its version, the limits, the roles and bases, whether the plan includes avatars and which checks run.
/// </summary>
/// <param name="StatementVersion">The CURRENT consent statement version for a person.</param>
/// <param name="Statements">The current consent statement for a person, per locale.</param>
/// <param name="Roles">The roles a PERSON's references take.</param>
/// <param name="Entitled">The plan includes avatars of people (the same value as <c>kinds.person.entitled</c>).</param>
/// <param name="Kinds">Per kind: whether its switch is on and whether the plan includes it.</param>
/// <param name="ObjectStatementVersion">The object statement version.</param>
/// <param name="ObjectStatements">The object statement, per locale.</param>
/// <param name="ObjectRoles">The roles an OBJECT's references take.</param>
/// <param name="ObjectLimits">How many pictures and videos an object holds.</param>
/// <param name="PersonCheck">A Vision provider is configured, so an object's pictures are checked for people.</param>
/// <param name="MediaConsent">Whether the project's media consent is recorded.</param>
public sealed record AvatarRequirementsResponse(
    string StatementVersion,
    IReadOnlyDictionary<string, string> Statements,
    IReadOnlyList<string> Locales,
    AvatarLimitsResponse Limits,
    AvatarMimeTypesResponse MimeTypes,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Bases,
    IReadOnlyList<string> DisclosureLabels,
    bool Entitled,
    bool ModerationEnabled,
    bool CsamScanningEnabled,
    AvatarKindsResponse Kinds,
    string ObjectStatementVersion,
    IReadOnlyDictionary<string, string> ObjectStatements,
    IReadOnlyList<string> ObjectRoles,
    AvatarObjectLimitsResponse ObjectLimits,
    bool PersonCheck,
    AvatarMediaConsentResponse MediaConsent,
    // The workspace avatar library: whether it exists, whether the caller may manage it, and the two workspace statements.
    AvatarLibraryRequirementsResponse? Library = null);

/// <summary>One generation take that used the avatar (from <c>external_generation_jobs.avatar_id</c>).</summary>
public sealed record AvatarUsageTakeResponse(
    Guid JobId,
    Guid StepResultId,
    string ClipId,
    string Status,
    bool HasOutput,
    DateTime CreatedAt);

/// <summary>The takes of one run that used the avatar.</summary>
public sealed record AvatarUsageRunResponse(
    Guid ExecutionId,
    string? WorkflowName,
    DateTime FirstUsedAt,
    DateTime LastUsedAt,
    IReadOnlyList<AvatarUsageTakeResponse> Takes);

/// <summary>
/// Where an avatar was used (<c>GET .../avatars/{id}/usage</c>): the runs and takes from the generation ledger (empty
/// until phase 2 writes <c>avatar_id</c>), and how many pictures in the editor use one of its reference photos (they
/// show a placeholder once the avatar is deleted).
/// </summary>
public sealed record AvatarUsageResponse(
    Guid AvatarId,
    int TakeCount,
    IReadOnlyList<AvatarUsageRunResponse> Runs,
    int PicturesUsingReferences,
    /// <summary>Pictures of the editor with a layer that was generated or edited WITH the avatar (the layers stay when the avatar is deleted).</summary>
    int PicturesGenerated = 0);
