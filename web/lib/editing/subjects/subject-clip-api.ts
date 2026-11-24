/**
 * Generating a clip that shows the project's avatars and objects, from the timeline editor
 * (docs/design/avatar-objects.md "Generating a clip with avatars from the editor (subject clips)").
 * Routes under `/api/v1/projects/{projectId}/subject-clips`. The finished clip is an ordinary project video file:
 * the media pool shows it, and no timeline op is involved.
 */
import { apiFetch, ApiError } from '../../api/client';

export type SubjectClipState = 'queued' | 'running' | 'ready' | 'failed' | 'cancelled';
export type SubjectClipMode = 'Auto' | 'References' | 'FirstFrame';

/** The ratios the dialog offers, widest first. */
export const SUBJECT_CLIP_RATIOS = ['16:9', '1:1', '4:5', '9:16'] as const;
export type SubjectClipRatio = (typeof SUBJECT_CLIP_RATIOS)[number];
/** The sizes the dialog offers (the same words the video generation step offers); the first is the default. */
export const SUBJECT_CLIP_RESOLUTIONS: readonly string[] = ['768P', '480P', '720P', '2K'];

/**
 * The sizes offered for a model, as the video generation step offers them: the MiniMax H3 family and the
 * self-hosted graphs each take their own two. An unknown model, or the default one (null: the browser is not
 * told which it is), gets the whole list and the server refuses a size the model does not take.
 */
export function resolutionsForModel(model: string | null | undefined): readonly string[] {
  const id = (model ?? '').trim();
  if (id.startsWith('wan2.2-')) return ['480P', '720P'];
  if (/^MiniMax-H3-Max$/i.test(id)) return ['768P', '480P'];
  if (/^MiniMax-H3/i.test(id)) return ['768P', '2K'];
  return SUBJECT_CLIP_RESOLUTIONS;
}
export const SUBJECT_CLIP_PROMPT_MAX = 2000;
export const SUBJECT_CLIP_MAX_TAKES = 2;
export const SUBJECT_CLIP_MIN_SECONDS = 4;
export const SUBJECT_CLIP_MAX_SECONDS = 15;
export const SUBJECT_CLIP_DEFAULT_SECONDS = 6;

/** Body of `POST …/subject-clips` and `POST …/subject-clips/estimate`. */
export interface SubjectClipRequest {
  /** 1..3, at most one of them a person. */
  subjects: { avatarId: string }[];
  /** What happens in the clip, 1..2000 characters. */
  prompt: string;
  /** A VideoGeneration capability id; left out = the workspace's default one. */
  capabilityId?: string;
  durationSeconds: number;
  ratio: string;
  resolution: string;
  /** 1..2. */
  takes: number;
  /** A project audio file the one chosen person says (a talking clip). */
  speechFileId?: string;
  subjectMode?: SubjectClipMode;
}

/** `POST …/subject-clips/estimate`. Writes nothing. A price is only ever what this returned. */
export interface SubjectClipEstimate {
  billingEnabled: boolean;
  credits?: number | null;
  creditsHigh?: number | null;
  balanceCredits?: number | null;
  affordable: boolean;
  /** How the subjects reach the model (`References`, `FirstFrame`, `TalkingHead`). */
  path?: string | null;
  /** One still picture of the subjects is made first, and is part of the price. */
  stillIncluded?: boolean;
  /** Why it cannot run, as the server's sentence; absent or null = it can. */
  unavailableReason?: string | null;
  /** A code beside the sentence, when the server sends one. */
  unavailableCode?: string | null;
}

/** `202` of `POST …/subject-clips`. */
export interface SubjectClipStart {
  executionId: string;
  state: SubjectClipState;
  credits?: number | null;
}

/** `GET …/subject-clips/{executionId}`. */
export interface SubjectClipStatus {
  executionId: string;
  state: SubjectClipState;
  /** The new project video files, one per take; filled when `ready`. */
  outputFileIds: string[];
  message?: string | null;
  failureCode?: string | null;
  subjects?: { name: string; kind: string }[];
}

const base = (projectId: string): string => `/api/v1/projects/${encodeURIComponent(projectId)}/subject-clips`;
const post = <T>(url: string, body?: unknown): Promise<T> => apiFetch<T>(url, { method: 'POST', ...(body === undefined ? {} : { body: JSON.stringify(body) }) });

export const subjectClipsPath = base;

export const estimateSubjectClip = (projectId: string, body: SubjectClipRequest): Promise<SubjectClipEstimate> =>
  post<SubjectClipEstimate>(`${base(projectId)}/estimate`, body);

export const startSubjectClip = (projectId: string, body: SubjectClipRequest): Promise<SubjectClipStart> => post<SubjectClipStart>(base(projectId), body);

/** Reads one run. A server that leaves `outputFileIds` out answers an empty list here. */
export async function getSubjectClip(projectId: string, executionId: string): Promise<SubjectClipStatus> {
  const status = await apiFetch<SubjectClipStatus>(`${base(projectId)}/${encodeURIComponent(executionId)}`);
  return { ...status, outputFileIds: Array.isArray(status.outputFileIds) ? status.outputFileIds : [] };
}

/** Stops a run. A `409` means it ended first, which is just as good: the answer is then null. */
export async function cancelSubjectClip(projectId: string, executionId: string): Promise<unknown> {
  try {
    return await post<unknown>(`${base(projectId)}/${encodeURIComponent(executionId)}/cancel`);
  } catch (error) {
    if ((error as { status?: unknown } | null)?.status === 409) return null;
    throw error;
  }
}

export const isSubjectClipEnded = (status: Pick<SubjectClipStatus, 'state'>): boolean => status.state === 'failed' || status.state === 'cancelled';

/** A run that ended without a clip. `status.message` is the server's own sentence about why. */
export class SubjectClipFailedError extends Error {
  readonly status: SubjectClipStatus;
  constructor(status: SubjectClipStatus) {
    super(status.message?.trim() || 'The clip was not made.');
    this.name = 'SubjectClipFailedError';
    this.status = status;
  }
}

/**
 * Polls a run until it is ready (or fails, is cancelled, or `signal` aborts): every 2 s, and after a failed read
 * with a growing wait of at most 15 s. A failed read does not end the run (the server keeps it going): only a 404
 * or the timeout does. Interval, timeout and reader are injectable for tests.
 */
export async function waitForSubjectClip(
  projectId: string,
  executionId: string,
  opts: { signal?: AbortSignal; intervalMs?: number; timeoutMs?: number; maxBackoffMs?: number; onProgress?: (status: SubjectClipStatus) => void; fetchStatus?: typeof getSubjectClip } = {},
): Promise<SubjectClipStatus> {
  const { signal, intervalMs = 2000, timeoutMs = 60 * 60_000, maxBackoffMs = 15_000, onProgress, fetchStatus = getSubjectClip } = opts;
  const started = Date.now();
  let failures = 0;
  let last: SubjectClipStatus | null = null;
  for (;;) {
    if (signal?.aborted) throw new DOMException('aborted', 'AbortError');
    let wait = intervalMs;
    try {
      const status = await fetchStatus(projectId, executionId);
      if (signal?.aborted) throw new DOMException('aborted', 'AbortError');
      failures = 0;
      last = status;
      if (status.state === 'ready') return status;
      if (isSubjectClipEnded(status)) throw new SubjectClipFailedError(status);
      onProgress?.(status);
    } catch (error) {
      if (error instanceof SubjectClipFailedError || (error as { name?: string } | null)?.name === 'AbortError') throw error;
      if ((error as { status?: unknown } | null)?.status === 404) throw error;
      failures += 1;
      wait = Math.min(maxBackoffMs, intervalMs * 2 ** failures);
    }
    if (Date.now() - started > timeoutMs) {
      throw new SubjectClipFailedError({ ...(last ?? { executionId, outputFileIds: [] }), state: 'failed', failureCode: 'timeout', message: null });
    }
    await new Promise<void>((resolve) => setTimeout(resolve, wait));
  }
}

export type SubjectClipRefusalKind = 'consent' | 'plan' | 'credits' | 'busy' | 'model' | 'avatar' | 'personOff' | 'email' | 'invalid' | 'off' | 'other';
export type SubjectClipPlanFeature = 'video_generation' | 'avatars' | 'avatar_objects';

/** A refused estimate or start, sorted into the few states the dialog words itself. */
export interface SubjectClipRefusal {
  kind: SubjectClipRefusalKind;
  /** `plan`: which part of the plan is missing. */
  feature?: SubjectClipPlanFeature;
  /** The server's own sentence, shown as it is for `credits` and `invalid`. */
  message?: string;
  /** `invalid`: each thing to fix. */
  errors?: string[];
  /** `model`: the model of the same family that can show pictures. */
  sibling?: string;
  /** `avatar`: the one rule's reason (`consent_revoked`, `reference_missing`, …), when the body names it. */
  reason?: string;
  /** `busy`: seconds to wait, when the server said. */
  retryAfterSeconds?: number;
}

const PLAN_FEATURES: readonly SubjectClipPlanFeature[] = ['video_generation', 'avatars', 'avatar_objects'];

/**
 * The server's code for a clip that shows a person where this workspace makes no clips of people yet (the operator's
 * setting): a start answers `409` with it, an estimate answers it as `unavailableCode`. The dialog words it itself.
 */
export const SUBJECT_CLIP_PERSON_OFF_CODE = 'AVATAR_GENERATION_DISABLED';
const AVATAR_CODES = new Set(['AVATAR_NOT_USABLE', 'AVATAR_NOT_FOUND', 'AVATAR_CONSENT_REVOKED', 'AVATAR_CONSENT_MISSING']);

const text = (value: unknown): string | undefined => (typeof value === 'string' && value.trim() ? value.trim() : undefined);

/** The sibling model a refusal names: a plain name, or an object that carries one. */
function siblingOf(value: unknown): string | undefined {
  if (typeof value === 'object' && value !== null) {
    const v = value as Record<string, unknown>;
    return text(v.model) ?? text(v.name);
  }
  return text(value);
}

export function describeSubjectClipRefusal(error: unknown): SubjectClipRefusal {
  const e = error as { status?: unknown; body?: unknown; message?: unknown; errors?: unknown; retryAfterSeconds?: unknown } | null;
  const status = typeof e?.status === 'number' ? e.status : 0;
  const body = typeof e?.body === 'object' && e.body !== null ? (e.body as Record<string, unknown>) : {};
  const code = typeof body.code === 'string' ? body.code : typeof body.error === 'string' ? body.error : '';
  const message = text(body.message) ?? (error instanceof ApiError ? text(error.message) : undefined);
  if (code === 'INSUFFICIENT_CREDITS') return { kind: 'credits', ...(message ? { message } : {}) };
  if (code === 'PLAN_NOT_ENTITLED' || status === 402) {
    const feature = PLAN_FEATURES.find((f) => f === body.feature);
    return { kind: 'plan', ...(feature ? { feature } : {}) };
  }
  if (code === 'EMAIL_NOT_VERIFIED') return { kind: 'email' };
  // This workspace does not (yet) make clips that show a person; objects alone still work.
  if (code === SUBJECT_CLIP_PERSON_OFF_CODE) return { kind: 'personOff' };
  if (code === 'CONSENT_REQUIRED') return { kind: 'consent' };
  if (code === 'AVATAR_NOT_SUPPORTED_BY_MODEL') {
    const sibling = siblingOf(body.sibling);
    return { kind: 'model', ...(sibling ? { sibling } : {}) };
  }
  if (AVATAR_CODES.has(code)) {
    const reason = text(body.reason);
    return { kind: 'avatar', ...(reason ? { reason } : {}) };
  }
  if (code === 'RUN_RATE_LIMITED' || status === 429) {
    const wait = typeof e?.retryAfterSeconds === 'number' && e.retryAfterSeconds > 0 ? e.retryAfterSeconds : undefined;
    return { kind: 'busy', ...(wait ? { retryAfterSeconds: wait } : {}) };
  }
  if (status === 400) {
    const errors = Array.isArray(e?.errors) ? e.errors.filter((x): x is string => typeof x === 'string' && x.trim().length > 0) : [];
    return { kind: 'invalid', ...(message ? { message } : {}), ...(errors.length > 0 ? { errors } : {}) };
  }
  if (status === 404) return { kind: 'off' };
  return { kind: 'other' };
}

/**
 * The ratio the dialog starts on: the one of 16:9, 9:16, 1:1 and 4:5 nearest to the timeline's own picture
 * (compared on a log scale, so a tall and a wide picture are judged alike). No timeline = 16:9.
 */
export function nearestSubjectClipRatio(canvas: { width: number; height: number } | null | undefined): SubjectClipRatio {
  if (!canvas || !(canvas.width > 0) || !(canvas.height > 0)) return '16:9';
  const wanted = Math.log(canvas.width / canvas.height);
  let best: SubjectClipRatio = '16:9';
  let bestDistance = Number.POSITIVE_INFINITY;
  for (const ratio of SUBJECT_CLIP_RATIOS) {
    const [w, h] = ratio.split(':').map(Number);
    const distance = Math.abs(Math.log(w / h) - wanted);
    if (distance < bestDistance) {
      best = ratio;
      bestDistance = distance;
    }
  }
  return best;
}

/** What the form holds; `buildSubjectClipRequest` turns it into the body. */
export interface SubjectClipForm {
  subjectIds: readonly string[];
  prompt: string;
  /** Null = the workspace's default model. */
  capabilityId: string | null;
  durationSeconds: number;
  ratio: string;
  resolution: string;
  takes: number;
  /** Null = nobody speaks. Only sent when `speechAllowed`. */
  speechFileId: string | null;
}

/** Why the form cannot be sent yet, as a key suffix under `editor-media.subjects.dialog.blocker`, or null. */
export type SubjectClipBlocker = 'noSubject' | 'noPrompt' | 'promptTooLong';

export function subjectClipBlocker(form: Pick<SubjectClipForm, 'subjectIds' | 'prompt'>): SubjectClipBlocker | null {
  if (form.subjectIds.length === 0) return 'noSubject';
  const prompt = form.prompt.trim();
  if (prompt.length === 0) return 'noPrompt';
  if (prompt.length > SUBJECT_CLIP_PROMPT_MAX) return 'promptTooLong';
  return null;
}

const clampInt = (value: number, min: number, max: number, fallback: number): number =>
  (Number.isFinite(value) ? Math.min(max, Math.max(min, Math.round(value))) : fallback);

/**
 * The request body for the form. Optional fields are left out, never sent empty: no `capabilityId` means the
 * default model, and `speechFileId` goes only when one person and nothing else is chosen (`speechAllowed`).
 */
export function buildSubjectClipRequest(form: SubjectClipForm, speechAllowed: boolean): SubjectClipRequest {
  const request: SubjectClipRequest = {
    subjects: form.subjectIds.map((avatarId) => ({ avatarId })),
    prompt: form.prompt.trim(),
    durationSeconds: clampInt(form.durationSeconds, SUBJECT_CLIP_MIN_SECONDS, SUBJECT_CLIP_MAX_SECONDS, SUBJECT_CLIP_DEFAULT_SECONDS),
    ratio: form.ratio,
    resolution: form.resolution,
    takes: clampInt(form.takes, 1, SUBJECT_CLIP_MAX_TAKES, 1),
  };
  if (form.capabilityId) request.capabilityId = form.capabilityId;
  if (speechAllowed && form.speechFileId) request.speechFileId = form.speechFileId;
  return request;
}
