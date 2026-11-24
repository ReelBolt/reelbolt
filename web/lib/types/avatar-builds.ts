/**
 * Mirrors the variant and build DTOs of the Inference API (docs/design/avatar-objects.md "Avatar variants" and
 * "Pulling an object out of a video"). A build is background work that ends in a new avatar: a variant made by
 * the picture model (`variant`), or an object pulled out of a video (`extraction`).
 */
export type AvatarBuildKind = 'variant' | 'extraction';

/**
 * Where a build is. A variant goes `queued` → `pictures` → `videos` → `creating`; an extraction goes
 * `queued` → `finding` → `cutting` → `creating`; both end `ready`, `failed` or `cancelled`. Any other word a
 * later server sends counts as still running (`lib/avatars/builds.ts`).
 */
export type AvatarBuildState =
  | 'queued' | 'pictures' | 'videos' | 'finding' | 'cutting' | 'creating' | 'ready' | 'failed' | 'cancelled';

export interface AvatarBuild {
  jobId: string;
  kind: AvatarBuildKind | (string & {});
  state: AvatarBuildState | (string & {});
  /** The step inside a running state, when the server names one. */
  stage?: string | null;
  /** How many items of the current step are done, e.g. pictures changed. */
  progress?: { done: number; total: number } | null;
  /** The name of the avatar being made, e.g. "Car · Red". */
  name?: string | null;
  /** The new avatar, once it exists. */
  avatarId?: string | null;
  failureCode?: string | null;
  message?: string | null;
  credits?: number | null;
}

/** `GET …/avatars/builds`: a bare array, or an envelope around one. */
export type AvatarBuildListResponse = AvatarBuild[] | { items?: AvatarBuild[] | null; builds?: AvatarBuild[] | null };

/** `POST …/avatars/{id}/variants/estimate`: which of the original's files to change. Nothing is written. */
export interface VariantEstimateRequest {
  /** The ids of the original's references to change (`AvatarReference.id`): pictures, and videos with `includeVideos`. */
  referenceIds: string[];
  includeVideos: boolean;
}

export interface VariantEstimate {
  billingEnabled: boolean;
  credits?: number | null;
  creditsHigh?: number | null;
  balanceCredits?: number | null;
  affordable: boolean;
  pictures: number;
  videos: number;
  unavailableReason?: string | null;
}

/**
 * `POST …/avatars/{id}/variants`: asks the picture model to change the object's own pictures (and, when asked,
 * to make its videos again). `consentStatement` carries `requirements.objectStatements[consentLocale]` verbatim.
 * The app never sends `allowMediaEgress`: it relies on the project's recorded permission
 * (`PUT …/avatars/media-consent`), which only a person gives.
 */
export interface StartVariantRequest {
  instruction: string;
  variantLabel: string;
  variantDescription: string;
  referenceIds: string[];
  includeVideos: boolean;
  consentStatement: string;
  consentLocale: string;
  name?: string;
  description?: string;
}

/** `202` of a started build. */
export interface AvatarBuildStarted {
  jobId: string;
  state: AvatarBuildState | (string & {});
}
