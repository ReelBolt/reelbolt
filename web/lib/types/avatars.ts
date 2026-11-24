/**
 * Mirrors `ProjectAvatarDtos.cs` (Inference API), see docs/design/avatars.md "API, assistant and web" and
 * docs/design/avatar-objects.md "Avatar REST contract with objects".
 * An avatar is a consented likeness of one person, or an object (a product, a vehicle, a building: anything
 * that is not a person). `selectable` is the ONE read-path rule (`ProjectAvatar.UnselectableReason`), and the
 * UI never offers an avatar that is false here.
 * The consent statement and the object statement are never typed in the client: they come from
 * `GET .../avatars/requirements`.
 */
export type AvatarReferenceKind = 'Image' | 'Video' | 'Audio';
/** `Front`, `Angle` and `Detail` are an object's picture roles; an object's video is `Motion`. */
export type AvatarReferenceRole = 'Face' | 'HalfBody' | 'FullBody' | 'Motion' | 'Speech' | 'Front' | 'Angle' | 'Detail';
/** What an avatar shows. A server from before objects sends no kind: read it with `subjectKindOf`. */
export type AvatarSubjectKind = 'Person' | 'Object';
export type AvatarConsentBasis = 'Self' | 'WrittenPermission';
export type AvatarDisclosureLabel = 'Visible' | 'MetadataOnly';
export type AvatarReferenceState = 'ok' | 'missing' | 'changed' | 'held';

/** `ProjectAvatar.UnselectableReasons`, in the order the server checks them. */
export const AVATAR_UNSELECTABLE_REASONS = [
  'consent_missing',
  'consent_revoked',
  'retention_expired',
  'reference_missing',
  'reference_held',
  'no_image_reference',
] as const;
export type AvatarUnselectableReason = (typeof AVATAR_UNSELECTABLE_REASONS)[number];

/** The roles each reference kind of a PERSON may take (`ProjectAvatarLimits.RoleFits`). */
export const ROLES_BY_KIND: Record<AvatarReferenceKind, readonly AvatarReferenceRole[]> = {
  Image: ['Face', 'HalfBody', 'FullBody'],
  Video: ['Speech', 'Motion'],
  Audio: ['Speech'],
};

/** The roles each reference kind of an OBJECT may take; an object takes no audio. */
export const OBJECT_ROLES_BY_KIND: Record<AvatarReferenceKind, readonly AvatarReferenceRole[]> = {
  Image: ['Front', 'Angle', 'Detail'],
  Video: ['Motion'],
  Audio: [],
};

export interface AvatarLimits {
  maxAvatarsPerProject: number;
  maxImages: number;
  maxVideos: number;
  maxAudio: number;
  maxImageBytes: number;
  minImageSidePx: number;
  maxImageSidePx: number;
  minImageAspect: number;
  maxImageAspect: number;
  maxVideoBytes: number;
  minVideoMs: number;
  maxVideoMs: number;
  minVideoFps: number;
  maxVideoFps: number;
  videoCodecs: string[];
  maxAudioBytes: number;
  minAudioMs: number;
  maxAudioMs: number;
  minRetentionDays: number;
  maxRetentionDays: number;
  defaultRetentionDays: number;
  revokedGraceDays: number;
  maxNameLength: number;
  maxDescriptionLength: number;
  maxSubjectNameLength: number;
}

/** Whether one kind of avatar is switched on for this server and included in the workspace's plan. */
export interface AvatarKindAvailability {
  enabled: boolean;
  entitled: boolean;
}

/** The project-level consent to send avatar and object files to AI services from the editors and the assistant. */
export interface AvatarMediaConsent {
  consented: boolean;
  consentedAt: string | null;
}

export interface AvatarRequirements {
  statementVersion: string;
  /** The CURRENT person consent statement per locale (`en`, `it`), verbatim. */
  statements: Record<string, string>;
  locales: string[];
  limits: AvatarLimits;
  mimeTypes: { image: string[]; video: string[]; audio: string[] };
  roles: string[];
  bases: string[];
  disclosureLabels: string[];
  /** Whether the workspace's plan includes avatars of people (`avatarsAllowed`); equals `kinds.person.entitled`. */
  entitled: boolean;
  moderationEnabled: boolean;
  csamScanningEnabled: boolean;
  /**
   * The fields below came with objects. A server from before them sends none: read `kinds` with
   * `kindAvailability`, which then answers "people as before, no objects".
   */
  kinds?: { person: AvatarKindAvailability; object: AvatarKindAvailability };
  objectStatementVersion?: string;
  /** The object statement per locale (`en`, `it`), verbatim. */
  objectStatements?: Record<string, string>;
  /** The roles an object's files may take (`Front`, `Angle`, `Detail`, `Motion`). */
  objectRoles?: string[];
  objectLimits?: { maxImages: number; maxVideos: number };
  /** True when object pictures are checked automatically and one that shows a person is refused. */
  personCheck?: boolean;
  mediaConsent?: AvatarMediaConsent;
  /**
   * The workspace avatar library (docs/design/avatar-objects.md "The workspace avatar library"). A server from
   * before the library sends none: read it with `libraryOf`, which then answers "no library".
   */
  library?: AvatarLibraryRequirements;
}

/**
 * What the library flows need. The two statements are the WORKSPACE texts (a person's consent and an object's
 * statement for every project of the workspace), verbatim per locale; they are never typed in the client.
 */
export interface AvatarLibraryRequirements {
  enabled: boolean;
  /** The caller is an Owner or Admin of the active workspace: publish, rename, confirm again, withdraw, delete. */
  canManage: boolean;
  consentStatementVersion: string;
  consentStatements: Record<string, string>;
  objectStatementVersion: string;
  objectStatements: Record<string, string>;
  maxItemsPerKind: number;
}

export interface AvatarReference {
  id: string;
  projectFileId: string;
  fileName: string | null;
  kind: AvatarReferenceKind;
  role: AvatarReferenceRole;
  ordinal: number;
  width: number | null;
  height: number | null;
  durationMs: number | null;
  attachedAt: string;
  state: AvatarReferenceState;
}

export interface AvatarConsent {
  attestedByUserId: string | null;
  attestedAt: string | null;
  statement: string;
  statementVersion: string;
  locale: string;
  subjectName: string;
  basis: AvatarConsentBasis;
  subjectIsAdult: boolean;
  documentFileId: string | null;
  revokedAt: string | null;
  revokedByUserId: string | null;
}

/** An object's identity facts (docs/design/avatar-objects.md "Object fidelity"): plain words, each null when unknown. */
export interface AvatarIdentityFacts {
  colour: string | null;
  makeModel: string | null;
  marks: string | null;
  scale: string | null;
}

/** What a take check said about a generated take (`subjectFidelity.status` of a VideoGenerate output). */
export type SubjectFidelityStatus = 'match' | 'mismatch' | 'unsure' | 'unavailable' | 'skipped';

export interface ProjectAvatar {
  id: string;
  projectId: string;
  /** Absent from a server from before objects, where every avatar is a person. */
  subjectKind?: AvatarSubjectKind;
  /**
   * Whether it may be used in pictures: an object always, a person only when the consent was confirmed under
   * a statement that covers pictures. Absent from an older server (unknown, so nothing is said).
   */
  coversPictures?: boolean;
  name: string;
  description: string | null;
  selectable: boolean;
  unselectableReason: AvatarUnselectableReason | string | null;
  projectVoiceId: string | null;
  projectVoiceName: string | null;
  projectVoiceSelectable: boolean | null;
  consent: AvatarConsent;
  /** Null for an object: it has no keep-until date. */
  retentionUntil: string | null;
  disclosureLabel: AvatarDisclosureLabel;
  primaryReferenceId: string | null;
  references: AvatarReference[];
  createdAt: string;
  createdByUserId: string;
  updatedAt: string;
  /**
   * Set on an import from the workspace library: the avatar follows that library item, so in the project only
   * its voice link can be set and it can be removed. Absent from a server from before the library.
   */
  /** An object's colour, make and model, marks and scale; absent or null for a person and when none are known. */
  identityFacts?: AvatarIdentityFacts | null;
  libraryItemId?: string | null;
  fromLibrary?: boolean;
  /**
   * Variants (docs/design/avatar-objects.md "Avatar variants"). A variant is a complete avatar of its own, grouped
   * under its original: `variantOfAvatarId` names the original (null on an original, and on a variant whose
   * original was deleted), `variantLabel` is its short label ("Red") and `variantDescription` says what sets it
   * apart. `variantCount` is how many variants an avatar has. A server from before variants sends none of the
   * four: read them with `lib/avatars/variants.ts`, which then answers "no variants".
   */
  variantOfAvatarId?: string | null;
  variantLabel?: string | null;
  variantDescription?: string | null;
  variantCount?: number;
}

/**
 * What makes a new avatar a variant of another one: the original's id, the label (1..60) and the description
 * (1..500, required). Everything else of the create request is the normal create of that kind.
 */
export interface VariantCreateFields {
  variantOfAvatarId: string;
  variantLabel: string;
  variantDescription: string;
}

export interface AvatarReferenceRequest {
  projectFileId: string;
  role: AvatarReferenceRole;
}

export interface CreateAvatarRequest {
  name: string;
  /** Must equal `requirements.statements[consentLocale]` verbatim; the API refuses anything else. */
  consentStatement: string;
  consentLocale: string;
  consentSubjectName: string;
  consentBasis: AvatarConsentBasis;
  subjectIsAdult: boolean;
  references: AvatarReferenceRequest[];
  retentionDays: number;
  description?: string | null;
  consentDocumentFileId?: string | null;
  projectVoiceId?: string | null;
  disclosureLabel?: AvatarDisclosureLabel;
  disclosesOtherwise?: boolean;
  subjectIsPublicFigure?: boolean;
  /** Set together to make a variant of an existing person: the server copies the person's name and basis from the original. */
  variantOfAvatarId?: string;
  variantLabel?: string;
  variantDescription?: string;
}

/**
 * A new object. Only these fields: a person's field sent with an object is refused
 * (`400 field_not_for_objects`). `consentStatement` carries `requirements.objectStatements[consentLocale]` verbatim.
 */
export interface CreateObjectAvatarRequest {
  subjectKind: 'Object';
  name: string;
  description?: string;
  consentStatement: string;
  consentLocale: string;
  references: AvatarReferenceRequest[];
  /** Set together to make a variant of an existing object from other files. */
  variantOfAvatarId?: string;
  variantLabel?: string;
  variantDescription?: string;
}

/** An object takes `name` and `description` only; a variant of either kind also takes `variantDescription`. */
export interface UpdateAvatarRequest {
  name?: string;
  description?: string;
  variantDescription?: string;
  projectVoiceId?: string;
  clearProjectVoice?: boolean;
  disclosureLabel?: AvatarDisclosureLabel;
  disclosesOtherwise?: boolean;
  /** An object's facts; an empty string clears a field. */
  identityFacts?: Partial<Record<keyof AvatarIdentityFacts, string | null>>;
}

export interface ReplaceAvatarReferencesRequest {
  consentStatement: string;
  consentLocale: string;
  subjectIsAdult: boolean;
  references: AvatarReferenceRequest[];
  retentionDays: number;
}

/** Changing an object's files: the object statement again, no keep-for period. */
export interface ReplaceObjectReferencesRequest {
  consentStatement: string;
  consentLocale: string;
  references: AvatarReferenceRequest[];
}

export interface DeleteAvatarResponse {
  id: string;
  rowDeleted: boolean;
  /** An object's files are ordinary project files and are all kept (`deleted: 0, kept: n`). */
  references: { deleted: number; failed: number; kept: number };
  vendorAssets: { provider: string | null; outcome: string }[];
  consentDocumentDeleted: boolean;
  /** Phase 3: pictures made from the avatar for talking-head clips; failed -1 = not listed (the storage sweep removes them). */
  stills?: { deleted: number; failed: number } | null;
}

export interface AvatarUsageTake {
  jobId: string;
  stepResultId: string;
  clipId: string;
  status: string;
  hasOutput: boolean;
  createdAt: string;
}

export interface AvatarUsageRun {
  executionId: string;
  workflowName: string | null;
  firstUsedAt: string;
  lastUsedAt: string;
  takes: AvatarUsageTake[];
}

export interface AvatarUsage {
  avatarId: string;
  takeCount: number;
  runs: AvatarUsageRun[];
  picturesUsingReferences: number;
}
