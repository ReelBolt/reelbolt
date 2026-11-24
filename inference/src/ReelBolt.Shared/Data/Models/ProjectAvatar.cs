namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// On what basis the attesting user may make an avatar of the person (docs/design/avatars.md "Consent and
/// safety"). <b>APPEND ONLY</b>: persisted by name.
/// </summary>
public enum AvatarConsentBasis
{
    /// <summary>The attesting user IS the person shown and heard (the only basis a public figure may use, AV-8).</summary>
    Self,

    /// <summary>The attesting user holds the person's written permission (a signed release may be attached, AV-4).</summary>
    WrittenPermission
}

/// <summary>
/// How a clip made with the avatar is disclosed as AI-generated (docs/design/avatars.md "AI Act disclosure").
/// Recorded in phase 1, enforced on output in phase 2. <b>APPEND ONLY</b>: persisted by name.
/// </summary>
public enum AvatarDisclosure
{
    /// <summary>A visible "AI-generated" label is burned in, plus machine-readable marking. The default.</summary>
    Visible,

    /// <summary>Machine-readable marking only; the customer ticked that they disclose the content another way.</summary>
    MetadataOnly
}

/// <summary>
/// What an avatar is a likeness of (docs/design/avatar-objects.md "Avatar subject kind"). <b>APPEND ONLY</b>:
/// persisted by name (<c>project_avatars.subject_kind</c>, default <c>Person</c>).
/// </summary>
public enum AvatarSubjectKind
{
    /// <summary>One real person, under that person's recorded consent, retention and revoke. The default.</summary>
    Person,

    /// <summary>
    /// An object, a product, a vehicle or a place: no person, so no consent of a person, no retention date and no
    /// revoke. The member attests the object statement (<c>AvatarObjectStatement</c>) instead.
    /// </summary>
    Object
}

/// <summary>
/// A likeness a video or picture model can use: a named set of reference pictures, videos and speech of ONE real
/// person, held under recorded consent, retention, revoke and delete rules like a cloned <see cref="ProjectVoice"/>
/// (docs/design/avatars.md), or, since docs/design/avatar-objects.md, a named set of pictures and short videos of
/// ONE object (<see cref="AvatarSubjectKind.Object"/>), which needs no consent of a person. Owned by the Inference API (<c>project_avatars</c>, <c>__EFMigrationsHistory_Api</c>);
/// the WorkflowEngine maps the table read-only so its resolver applies the same rule.
///
/// <para>
/// <b>The one read-path rule</b> is <see cref="IsSelectable"/> / <see cref="UnselectableReason"/>: the API's
/// <c>selectable</c> flag, the web picker and the engine resolver all call it, so they cannot disagree. A linked
/// voice that has become unselectable does NOT make the avatar unselectable (the voice has its own rule and its
/// own failure code).
/// </para>
/// <para>
/// <b>Project-scoped by construction</b> (AV-1): cascade-deleted with its project and never offered to, or
/// resolvable from, another project. No tenant query filter: every route loads the project through
/// <c>IProjectAccessService</c> first (TenantBoundaryInventoryTests).
/// </para>
/// </summary>
public class ProjectAvatar
{
    /// <summary>Reason: no complete consent attestation on the row (or the subject is not attested as an adult).</summary>
    public const string ReasonConsentMissing = "consent_missing";

    /// <summary>Reason: consent was revoked.</summary>
    public const string ReasonConsentRevoked = "consent_revoked";

    /// <summary>Reason: <see cref="RetentionUntil"/> has passed.</summary>
    public const string ReasonRetentionExpired = "retention_expired";

    /// <summary>Reason: a listed reference file is gone, or its bytes changed since attestation.</summary>
    public const string ReasonReferenceMissing = "reference_missing";

    /// <summary>Reason: a listed reference file is quarantined (CSAM or moderation) or under the output hold.</summary>
    public const string ReasonReferenceHeld = "reference_held";

    /// <summary>Reason: no usable image reference (a picture of the person is the minimum).</summary>
    public const string ReasonNoImageReference = "no_image_reference";

    /// <summary>Every reason <see cref="UnselectableReason"/> can return, in the order it checks them.</summary>
    public static readonly IReadOnlyList<string> UnselectableReasons =
    [
        ReasonConsentMissing,
        ReasonConsentRevoked,
        ReasonRetentionExpired,
        ReasonReferenceMissing,
        ReasonReferenceHeld,
        ReasonNoImageReference
    ];

    /// <summary>
    /// The <see cref="RetentionUntil"/> of an object avatar: an object has no retention period, so its row carries
    /// this far-future instant (the column stays NOT NULL). Never swept, never warned, never shown as a date.
    /// </summary>
    public static readonly DateTime NoRetention = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>
    /// A person (the default) or an object. Set at creation and never changed: the two kinds are attested under
    /// different statements, so a row cannot become the other kind.
    /// </summary>
    public AvatarSubjectKind SubjectKind { get; set; } = AvatarSubjectKind.Person;

    /// <summary>Display name, 1..200, unique per project among live (not revoked) rows.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Shown to the user and, in phase 2, to a planner as plain text (sanitized, never an instruction).</summary>
    public string? Description { get; set; }

    /// <summary>
    /// An OBJECT's structured identity facts (colour, make and model, marks, scale) as JSON
    /// (<see cref="ReelBolt.Shared.Avatars.AvatarIdentityFacts"/>; docs/design/avatar-objects.md "Object fidelity"). Derived
    /// by the Vision provider at creation, editable by the owner. Null for a person and for an object with no facts.
    /// </summary>
    public string? IdentityFacts { get; set; }

    /// <summary>
    /// The avatar this one is a VARIANT of (docs/design/avatar-objects.md "Avatar variants", decision AO-8): the same
    /// car in another colour. Null for an original. One level only: a variant of a variant attaches to the original.
    /// Same project and same <see cref="SubjectKind"/> as the original (checked in code). Set null when the original
    /// is deleted, so its variants stay, ungrouped.
    /// </summary>
    public Guid? VariantOfAvatarId { get; set; }

    /// <summary>The variant's short label, for example "Red" (1..60), unique per original among live rows. Null on an original.</summary>
    public string? VariantLabel { get; set; }

    /// <summary>
    /// What sets the variant apart from its original, for example "matte red paint, black rims" (1..500, required on a
    /// variant). Untrusted text: shown to people, and given to a model only sanitized and quoted.
    /// </summary>
    public string? VariantDescription { get; set; }

    /// <summary>
    /// An optional linked <see cref="ProjectVoice"/> of the SAME project (checked in code; a CHECK cannot see the
    /// other row). Set null when the voice is deleted. The voice keeps its own consent and lifecycle.
    /// </summary>
    public Guid? ProjectVoiceId { get; set; }

    /// <summary>The user who attested the subject's consent. Required on every row (the column is NOT NULL).</summary>
    public Guid? ConsentAttestedByUserId { get; set; }

    /// <summary>When consent was (last) attested; a reference change is a re-attestation and moves it.</summary>
    public DateTime? ConsentAttestedAt { get; set; }

    /// <summary>The consent statement the attesting user agreed to, verbatim, in <see cref="ConsentLocale"/>.</summary>
    public string ConsentStatement { get; set; } = string.Empty;

    /// <summary>The statement version both language twins are registered under, e.g. <c>avatar-consent-2026-11</c> (AV-6).</summary>
    public string ConsentStatementVersion { get; set; } = string.Empty;

    /// <summary>The language the statement was shown in (<c>en</c> or <c>it</c>).</summary>
    public string ConsentLocale { get; set; } = string.Empty;

    /// <summary>The person shown, as the attesting user typed it (1..200). Never written to the audit events.</summary>
    public string ConsentSubjectName { get; set; } = string.Empty;

    public AvatarConsentBasis ConsentBasis { get; set; } = AvatarConsentBasis.Self;

    /// <summary>The adult checkbox. Minors are refused outright (AV-12); a row without it is never selectable.</summary>
    public bool ConsentSubjectAdult { get; set; }

    /// <summary>
    /// Optional signed release (a project file, PDF or image). <b>Never sent to any vendor</b> and excluded from
    /// every egress path (AV-4). Set null when the file is deleted.
    /// </summary>
    public Guid? ConsentDocumentFileId { get; set; }

    public DateTime? ConsentRevokedAt { get; set; }

    public Guid? ConsentRevokedByUserId { get; set; }

    /// <summary>When the avatar is deleted whether or not anyone asks (1..730 days from attestation, default 365).</summary>
    public DateTime RetentionUntil { get; set; }

    public AvatarDisclosure DisclosureLabel { get; set; } = AvatarDisclosure.Visible;

    /// <summary>
    /// The workspace avatar library item this avatar is a LINKED IMPORT of (docs/design/avatar-objects.md "The
    /// workspace avatar library (global avatars)"), or null for an avatar made in its project. A linked import follows
    /// its item: name, description, consent columns, retention and revoke are the item's, copied by the library
    /// service; only the voice link is the project's own. The avatar a library item was published from becomes a linked
    /// import of it too. A project holds at most one import of an item. API-owned column (migration
    /// <c>AddAvatarLibrary</c>); the engine reads it like any other column and gives it no meaning.
    /// </summary>
    public Guid? LibraryItemId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Guid CreatedByUserId { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optimistic concurrency token, mapped to PostgreSQL's <c>xmin</c> system column (no real column), so a revoke
    /// and a re-attestation racing on the same row cannot both win.
    /// </summary>
    public uint RowVersion { get; set; }

    /// <summary>The references, when the caller loaded them. Not required by the rule (it takes reference states).</summary>
    public List<ProjectAvatarReference> References { get; set; } = [];

    /// <summary>Whether the row carries a complete consent attestation for an adult subject.</summary>
    public bool HasCompleteConsent =>
        ConsentAttestedAt is not null &&
        ConsentAttestedByUserId is not null &&
        !string.IsNullOrWhiteSpace(ConsentStatement) &&
        ConsentSubjectAdult;

    /// <summary>
    /// Whether an OBJECT row carries its own attestation: who ticked the object statement, when, and the statement.
    /// The adult flag is not read (an object has no age).
    /// </summary>
    public bool HasObjectAttestation =>
        ConsentAttestedAt is not null &&
        ConsentAttestedByUserId is not null &&
        !string.IsNullOrWhiteSpace(ConsentStatement);

    /// <summary>Whether this avatar is a linked import of a workspace avatar library item.</summary>
    public bool IsLibraryImport => LibraryItemId is not null;

    /// <summary>
    /// Whether the avatar may be shown in a generated PICTURE (docs/design/avatar-objects.md, decision AO-2): an
    /// object always; a person only when the consent on record was given under a statement version that covers
    /// pictures (<see cref="Avatars.AvatarConsentStatement.CoversPictures"/>). A person attested under the first,
    /// videos-only text keeps working for video and must be confirmed again before a picture.
    /// </summary>
    public bool CoversPictures =>
        SubjectKind == AvatarSubjectKind.Object || Avatars.AvatarConsentStatement.CoversPictures(ConsentStatementVersion);

    /// <summary>Whether this row is a variant of another avatar (it names its original).</summary>
    public bool IsVariant => VariantOfAvatarId is not null;

    /// <summary>
    /// The name a variant gets when none is given, "Car · Red": the original's name, a middle dot, the label
    /// (docs/design/avatar-objects.md "Avatar variants"). Cut to the name column's length.
    /// </summary>
    public static string VariantDisplayName(string originalName, string variantLabel)
    {
        string name = originalName.Trim() + " · " + variantLabel.Trim();
        return name.Length <= ProjectAvatarLimits.MaxNameLength ? name : name[..ProjectAvatarLimits.MaxNameLength];
    }

    /// <summary>
    /// Whether the avatar may be selected or used RIGHT NOW: the single read-path rule. The caller passes ONE state
    /// per reference row of the avatar, as it observed it (file present with the attested digest, not held), built
    /// with <see cref="ProjectAvatarReference.Observe"/>; an avatar with no reference rows has no image and is not
    /// selectable.
    /// </summary>
    public bool IsSelectable(DateTime utcNow, IReadOnlyCollection<AvatarReferenceState> references) =>
        UnselectableReason(utcNow, references) is null;

    /// <summary>
    /// Why <see cref="IsSelectable"/> is false, as a machine-readable reason from <see cref="UnselectableReasons"/>,
    /// checked in that order; null when selectable.
    /// <para>
    /// An <see cref="AvatarSubjectKind.Object"/> has no person behind it, so it is never
    /// <see cref="ReasonConsentRevoked"/> or <see cref="ReasonRetentionExpired"/>: it is
    /// <see cref="ReasonConsentMissing"/> without its own attestation (<see cref="HasObjectAttestation"/>), then the
    /// reference reasons apply as for a person.
    /// </para>
    /// </summary>
    public string? UnselectableReason(DateTime utcNow, IReadOnlyCollection<AvatarReferenceState> references) =>
        UnselectableReason(utcNow, references, originals: null);

    /// <summary>
    /// The same rule with the variant's original taken into account. A VARIANT OF A PERSON shows the same person as its
    /// original and carries that person's consent, so it is only as usable as the original: an original that is revoked,
    /// deleted or missing makes it <see cref="ReasonConsentRevoked"/>, one whose retention has passed makes it
    /// <see cref="ReasonRetentionExpired"/> (checked right after the variant's own consent, so no media is read). Pass
    /// the project's avatars (at least the originals) by id; <c>null</c> skips this half (callers that only look at the
    /// row, such as the library). A person avatar that is not a variant, and any object, behave exactly as before.
    /// </summary>
    public string? UnselectableReason(
        DateTime utcNow, IReadOnlyCollection<AvatarReferenceState> references, IReadOnlyDictionary<Guid, ProjectAvatar>? originals)
    {
        ArgumentNullException.ThrowIfNull(references);

        if (SubjectKind == AvatarSubjectKind.Object)
        {
            if (!HasObjectAttestation)
                return ReasonConsentMissing;
        }
        else
        {
            if (!HasCompleteConsent)
                return ReasonConsentMissing;
            if (ConsentRevokedAt is not null)
                return ReasonConsentRevoked;
            if (RetentionUntil <= utcNow)
                return ReasonRetentionExpired;
            if (originals is not null && (VariantOfAvatarId is not null || VariantLabel is not null))
            {
                // A person's variant whose original is gone (deleted, or ungrouped) is not usable: its person withdrew with it.
                if (VariantOfAvatarId is not { } originalId || !originals.TryGetValue(originalId, out ProjectAvatar? original)
                    || original.SubjectKind != AvatarSubjectKind.Person || original.ConsentRevokedAt is not null)
                    return ReasonConsentRevoked;
                if (original.RetentionUntil <= utcNow)
                    return ReasonRetentionExpired;
            }
        }

        if (references.Any(r => !r.Present))
            return ReasonReferenceMissing;
        if (references.Any(r => r.Held))
            return ReasonReferenceHeld;
        if (!references.Any(r => r.Kind == AvatarReferenceKind.Image))
            return ReasonNoImageReference;
        return null;
    }
}

/// <summary>
/// One reference as the caller observed it when applying <see cref="ProjectAvatar.IsSelectable"/>. Build it with
/// <see cref="ProjectAvatarReference.Observe"/> so the "bytes changed means missing" comparison lives in one place.
/// </summary>
/// <param name="Kind">The reference's kind.</param>
/// <param name="Present">The project file still exists AND its bytes are the ones attested (digest match).</param>
/// <param name="Held">The file is quarantined (CSAM or moderation) or under the output hold.</param>
public readonly record struct AvatarReferenceState(AvatarReferenceKind Kind, bool Present, bool Held);
