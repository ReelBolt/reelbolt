/**
 * The edit-timeline API client — **the only module that knows the wire shape** of
 * `/api/v1/projects/{projectId}/edit-timelines/**`. Every call routes through `apiFetch`
 * (`__tests__/api-boundary.test.ts` asserts no component calls `fetch`).
 *
 * The nine routes: list, create, get, patch (P1); history, revisions/{rev}, duplicate (P3); render
 * and renders (P4 — the Deliver workspace's queue).
 * Status semantics the sync client depends on (reached through `ApiError.status`/`.body`):
 *   - PATCH `409` → `{ currentRevision }`           stale `baseRevision`, nothing written
 *   - PATCH `400` → `[{ path, message }]`           validator violations, nothing written
 *   - PATCH `413`                                   body/document/ops over the ceiling
 *   - GET revisions `404` → `{ restorableRevisions }` when no stored document answers for `rev`
 *   - any call `401` → the session expired (the sync client keeps the edits and asks for a sign-in)
 *   - GET `{id}` carries `canEdit` (R02.5): `false` = view-only, nothing is ever PATCHed
 */

import type { RenderCheck } from '@/lib/editing/audio/render-checks';
import { ApiError, apiFetch } from '../../api/client';
import type { EditOp } from '../engine';
import type { EditTimeline, EditTimelineSettings } from '../types';
import type {
  TimelineRenderEncodeSettings,
  TimelineRenderLoudness,
  TimelineRenderPreset,
  TimelineRenderRange,
} from '../../types/workflow';

// ─────────────────────────────────────────────────────────────────────────────
// Wire shapes (mirrors of EditTimelineDto.cs, camelCase)
// ─────────────────────────────────────────────────────────────────────────────

export interface EditTimelineSummary {
  id: string;
  name: string;
  revision: number;
  seedStepResultId: string | null;
  createdAt: string;
  updatedAt: string;
  /** Trash list only: when it was deleted and when it is removed for good. */
  deletedAt?: string;
  purgesAt?: string;
}

/** A manifest item the seeder could not place when a timeline was created from a run. */
export interface UnplacedItem {
  track: 'V1' | 'V2' | 'V3' | 'A2' | 'A3' | 'censor';
  itemId: string;
  label: string | null;
  reason: 'source_unresolved' | 'asset_unresolved' | 'seam_overlap_dropped' | 'censored_stretch_withheld';
}

export interface EditTimelineResponse {
  id: string;
  projectId: string;
  name: string;
  revision: number;
  document: EditTimeline;
  seedStepResultId: string | null;
  createdAt: string;
  updatedAt: string;
  /** Present only on create (empty when everything fit); absent on GET/PATCH and from older servers. */
  unplacedItems?: UnplacedItem[];
  /**
   * R02.5: whether the caller may change this timeline (project Edit permission). Only `GET {id}` carries it;
   * an older server omits it — `undefined` means "can edit", a `false` opens the editor view-only.
   */
  canEdit?: boolean;
  /** Import only: clips whose media this project does not have (`itemId` is the red "Offline: <name>" marker's id). */
  offline?: OfflineClip[];
  /** Import only (R12.3): the files the import matched, and how. */
  matched?: ImportMatchedMedia[];
  /** Import only (R12.3): names several project files share and nothing tells apart. */
  ambiguous?: ImportAmbiguousMedia[];
}

export interface OfflineClip {
  itemId: string;
  name: string;
  /** `notFound` | `ambiguous` | `durationMismatch` (older servers send none). */
  reason: string;
  message?: string;
}

export interface ImportMatchedMedia {
  name: string;
  fileId?: string | null;
  stepResultId?: string | null;
  /** `id` | `name` | `nameAndDuration`. */
  how: string;
}

export interface ImportMediaCandidate {
  fileId: string;
  name: string;
  durationSec?: number | null;
  sizeBytes: number;
}

export interface ImportAmbiguousMedia {
  name: string;
  candidates: ImportMediaCandidate[];
}

export interface EditTimelineConflict {
  currentRevision: number;
}

export interface EditTimelineViolation {
  path: string;
  message: string;
}

/**
 * What goes into the PATCH's `ops`. The engine's own ops, plus the one non-engine entry a client
 * legitimately writes: a `restore` marker recording which revision a restore PATCH reinstated. The
 * server stores the array verbatim and never applies it; the history panel describes it.
 */
export type WireOp = EditOp | { type: 'restore'; revision: number };

export interface PatchEditTimelineRequest {
  baseRevision: number;
  ops: WireOp[];
  document: EditTimeline;
}

export interface EditTimelineHistoryEntry {
  revision: number;
  createdAt: string;
  ops: unknown;
  hasSnapshot: boolean;
}

export interface EditTimelineHistory {
  id: string;
  currentRevision: number;
  entries: EditTimelineHistoryEntry[];
  restorableRevisions: number[];
}

export interface EditTimelineRevision {
  id: string;
  revision: number;
  source: 'current' | 'snapshot';
  document: EditTimeline;
}

export interface EditTimelineRevisionUnavailable {
  message: string;
  requestedRevision: number;
  restorableRevisions: number[];
}

// ─────────────────────────────────────────────────────────────────────────────
// Paths
// ─────────────────────────────────────────────────────────────────────────────

export const editTimelinesKey = (projectId: string): string =>
  `/api/v1/projects/${projectId}/edit-timelines`;

export const editTimelineKey = (projectId: string, timelineId: string): string =>
  `${editTimelinesKey(projectId)}/${timelineId}`;

export const editTimelineHistoryKey = (projectId: string, timelineId: string, limit?: number, before?: number): string => {
  const params = new URLSearchParams();
  if (limit !== undefined) params.set('limit', String(limit));
  if (before !== undefined) params.set('before', String(before));
  const query = params.toString();
  return `${editTimelineKey(projectId, timelineId)}/history${query ? `?${query}` : ''}`;
};

export const editTimelineRevisionKey = (projectId: string, timelineId: string, revision: number): string =>
  `${editTimelineKey(projectId, timelineId)}/revisions/${revision}`;

// ─────────────────────────────────────────────────────────────────────────────
// Calls
// ─────────────────────────────────────────────────────────────────────────────

export const listEditTimelines = (projectId: string): Promise<EditTimelineSummary[]> =>
  apiFetch<EditTimelineSummary[]>(editTimelinesKey(projectId));

/** Trash: the project's deleted timelines still inside the 30-day window, newest deletion first. */
export const editTimelinesTrashKey = (projectId: string): string => `${editTimelinesKey(projectId)}?state=deleted`;

export const listDeletedTimelines = (projectId: string): Promise<EditTimelineSummary[]> =>
  apiFetch<EditTimelineSummary[]>(editTimelinesTrashKey(projectId));

/** `POST …/{id}/restore`: a deleted timeline comes back with its name, edits and review link. */
export const restoreTimeline = (projectId: string, timelineId: string): Promise<EditTimelineSummary> =>
  apiFetch<EditTimelineSummary>(`${editTimelineKey(projectId, timelineId)}/restore`, { method: 'POST' });

export const createEditTimeline = (
  projectId: string,
  request: { name: string; seedStepResultId?: string | null; settings?: EditTimelineSettings | null },
): Promise<EditTimelineResponse> =>
  apiFetch<EditTimelineResponse>(editTimelinesKey(projectId), {
    method: 'POST',
    body: JSON.stringify({
      name: request.name,
      seedStepResultId: request.seedStepResultId ?? null,
      ...(request.settings ? { settings: request.settings } : {}),
    }),
  });

export const getEditTimeline = (projectId: string, timelineId: string): Promise<EditTimelineResponse> =>
  apiFetch<EditTimelineResponse>(editTimelineKey(projectId, timelineId));

/**
 * JSON with every `type` discriminator written first. The server reads items polymorphically by
 * `type`, and System.Text.Json (before `AllowOutOfOrderMetadataProperties`) refuses a discriminator
 * that is not the first property; object literals across the editor build `{ id, type, ... }`, so
 * the order is fixed here, once, for every document that is sent.
 */
export function stringifyDiscriminatorFirst(value: unknown): string {
  return JSON.stringify(value, (_key, v: unknown) => {
    if (v && typeof v === 'object' && !Array.isArray(v) && typeof (v as { type?: unknown }).type === 'string') {
      const { type, ...rest } = v as { type: string } & Record<string, unknown>;
      return { type, ...rest };
    }
    return v;
  });
}

/**
 * The browser's ceiling for a `keepalive` request body (the Fetch spec's 64 KiB in-flight quota).
 * A larger body sent with `keepalive` is refused outright, so it goes as a normal (best-effort) fetch.
 */
export const KEEPALIVE_BODY_LIMIT = 64 * 1024;

/** UTF-8 byte length of a request body — what the keepalive quota counts. */
export function bodyByteLength(body: string): number {
  return new TextEncoder().encode(body).length;
}

/**
 * Bytes of the keepalive PATCH bodies still in flight from this page. The browser's 64 KiB quota is
 * shared by ALL in-flight keepalive requests (Fetch spec: the sum, not each body), and one that
 * would overflow it is refused outright — then lost on unload. Released when the request settles.
 */
let keepaliveBytesInFlight = 0;

/**
 * `options.keepalive` asks for a request that outlives the page (a flush on `pagehide`/hide/route
 * change). It is honoured only when the body fits in what is left of the shared
 * {@link KEEPALIVE_BODY_LIMIT} quota; otherwise the request goes as a normal (best-effort) fetch.
 */
export const patchEditTimeline = (
  projectId: string,
  timelineId: string,
  request: PatchEditTimelineRequest,
  options?: { keepalive?: boolean },
): Promise<EditTimelineResponse> => {
  const body = stringifyDiscriminatorFirst(request);
  const bytes = bodyByteLength(body);
  const keepalive = options?.keepalive === true && keepaliveBytesInFlight + bytes < KEEPALIVE_BODY_LIMIT;
  if (keepalive) keepaliveBytesInFlight += bytes;
  const sent = apiFetch<EditTimelineResponse>(editTimelineKey(projectId, timelineId), {
    method: 'PATCH',
    body,
    ...(keepalive ? { keepalive: true } : {}),
  });
  if (!keepalive) return sent;
  return sent.finally(() => {
    keepaliveBytesInFlight -= bytes;
  });
};

export const getEditTimelineHistory = (
  projectId: string,
  timelineId: string,
  options?: { limit?: number; before?: number },
): Promise<EditTimelineHistory> =>
  apiFetch<EditTimelineHistory>(editTimelineHistoryKey(projectId, timelineId, options?.limit, options?.before));

export const getEditTimelineRevision = (
  projectId: string,
  timelineId: string,
  revision: number,
): Promise<EditTimelineRevision> =>
  apiFetch<EditTimelineRevision>(editTimelineRevisionKey(projectId, timelineId, revision));

// ─────────────────────────────────────────────────────────────────────────────
// Versions (R01.2 / R02.4): the restorable points of a timeline.
// ─────────────────────────────────────────────────────────────────────────────

export type EditTimelineVersionCause =
  | 'auto' | 'idle' | 'beforeRestore' | 'beforeProposal' | 'beforeRender' | 'milestone' | 'duplicate' | 'import' | 'named';

export interface EditTimelineVersion {
  revision: number;
  name?: string;
  cause: EditTimelineVersionCause | string;
  createdAt: string;
  /** Automatic checkpoints are removed at this time; absent for a named version (kept forever). */
  expiresAt?: string;
  createdByName?: string;
  /** `apiKey` when a public-API key made the version; absent means a person. */
  actorKind?: 'person' | 'apiKey';
}

export const editTimelineVersionsKey = (projectId: string, timelineId: string): string =>
  `${editTimelineKey(projectId, timelineId)}/versions`;

export const listEditTimelineVersions = async (projectId: string, timelineId: string): Promise<EditTimelineVersion[]> =>
  (await apiFetch<{ versions: EditTimelineVersion[] }>(editTimelineVersionsKey(projectId, timelineId))).versions;

/** `POST …/versions`: a named version of the CURRENT revision (`named`, needs a name) or the point before an AI suggestion. */
export const createEditTimelineVersion = (
  projectId: string,
  timelineId: string,
  request: { name?: string | null; cause?: 'named' | 'beforeProposal' },
): Promise<EditTimelineVersion> =>
  apiFetch<EditTimelineVersion>(editTimelineVersionsKey(projectId, timelineId), {
    method: 'POST',
    body: JSON.stringify({ name: request.name ?? null, cause: request.cause ?? 'named' }),
  });

export const nameEditTimelineVersion = (projectId: string, timelineId: string, revision: number, name: string): Promise<EditTimelineVersion> =>
  apiFetch<EditTimelineVersion>(`${editTimelineVersionsKey(projectId, timelineId)}/${revision}/name`, {
    method: 'PUT',
    body: JSON.stringify({ name }),
  });

export const removeEditTimelineVersionName = (projectId: string, timelineId: string, revision: number): Promise<EditTimelineVersion> =>
  apiFetch<EditTimelineVersion>(`${editTimelineVersionsKey(projectId, timelineId)}/${revision}/name`, { method: 'DELETE' });

export const duplicateEditTimeline = (
  projectId: string,
  timelineId: string,
  name?: string | null,
): Promise<EditTimelineResponse> =>
  apiFetch<EditTimelineResponse>(`${editTimelineKey(projectId, timelineId)}/duplicate`, {
    method: 'POST',
    body: JSON.stringify({ name: name ?? null }),
  });

/** `PUT …/name` (W0.9): row metadata only, the revision does not advance. Returns the timeline as `GET` would. */
export const renameEditTimeline = (projectId: string, timelineId: string, name: string): Promise<EditTimelineResponse> =>
  apiFetch<EditTimelineResponse>(`${editTimelineKey(projectId, timelineId)}/name`, {
    method: 'PUT',
    body: JSON.stringify({ name }),
  });

/** `DELETE …` (soft delete): the timeline vanishes from the list and every `{id}` route; `204`. */
export const deleteEditTimeline = (projectId: string, timelineId: string): Promise<void> =>
  apiFetch<void>(editTimelineKey(projectId, timelineId), { method: 'DELETE' });

// ─────────────────────────────────────────────────────────────────────────────
// Render (P4) — mirrors RenderEditTimelineRequest/Response and EditTimelineRenderResponse
// ─────────────────────────────────────────────────────────────────────────────

/** `POST …/render` body. Every field is optional; the server fills the preset/file-name defaults. */
export interface RenderEditTimelineRequest {
  revision?: number | null;
  range?: TimelineRenderRange | null;
  preset?: TimelineRenderPreset | null;
  encode?: TimelineRenderEncodeSettings | null;
  outputFileName?: string | null;
  registerProjectFile?: boolean | null;
  loudness?: TimelineRenderLoudness | null;
  /** R09.7: where the captions go; absent = burned in. */
  captions?: 'none' | 'burnIn' | 'sidecar' | 'both' | null;
}

/** `202 Accepted` body of `POST …/render`. */
export interface RenderEditTimelineResponse {
  executionId: string;
  workflowId: string;
  timelineId: string;
  revision: number;
  preset: string;
  outputFileName: string;
}

export interface EditTimelineRenderRegion {
  startFrame: number;
  endFrame: number;
  cached: boolean;
}

export interface EditTimelineRenderRegions {
  total: number;
  cached: number;
  list: EditTimelineRenderRegion[];
}

/** One row of the render queue; see EditTimelineRenderResponse in EditTimelineDto.cs. */
/** A sidecar caption file registered as a project file by a render (R09.7). */
export interface RenderCaptionFile {
  format: 'srt' | 'vtt';
  fileName: string;
  sizeBytes: number;
  fileId: string;
}

export interface EditTimelineRenderSummary {
  captionFiles?: RenderCaptionFile[] | null;
  executionId: string;
  workflowId: string;
  timelineId: string;
  status: 'Queued' | 'Running' | 'Passed' | 'Failed' | 'Cancelled' | string;
  startedAt: string | null;
  completedAt: string | null;
  revision: number | null;
  preset: string;
  outputFileName: string;
  errorMessage: string | null;
  stepResultId: string | null;
  hasOutput: boolean;
  regions: EditTimelineRenderRegions | null;
  /** P5: the loudness the render measured on its final mix; null until the render finished. */
  loudness?: EditTimelineRenderLoudness | null;
  /** What the render left out, one plain sentence each (≤ 50); empty or absent when nothing was skipped. */
  skippedItems?: EditTimelineRenderSkippedItem[] | null;
  /** The render's plain warnings (≤ 50, each ≤ 300 chars); empty or absent when there are none. */
  warnings?: string[] | null;
  /** Length of the finished film in seconds; null until it finished (or when the file was not measured). */
  durationSec?: number | null;
  /** Size of the finished file in bytes; null when it was not saved to the project's files. */
  sizeBytes?: number | null;
  /** The project-file row of the finished film, for "Open in project files"; null when there is none. */
  outputFileId?: string | null;
  /** When the render was queued. */
  createdAt?: string | null;
}

/** See EditTimelineRenderSkippedItemResponse in EditTimelineDto.cs. */
export interface EditTimelineRenderSkippedItem {
  itemId: string | null;
  type: string | null;
  message: string;
}

/** See EditTimelineRenderLoudnessResponse in EditTimelineDto.cs. */
export interface EditTimelineRenderLoudness {
  integratedLufs: number;
  truePeakDbtp: number;
  loudnessRange: number;
  normalised: boolean;
  checks?: RenderCheck[] | null;
}

export const editTimelineRendersKey = (projectId: string, timelineId: string): string =>
  `${editTimelineKey(projectId, timelineId)}/renders`;

export const renderEditTimeline = (
  projectId: string,
  timelineId: string,
  request: RenderEditTimelineRequest,
): Promise<RenderEditTimelineResponse> =>
  apiFetch<RenderEditTimelineResponse>(`${editTimelineKey(projectId, timelineId)}/render`, {
    method: 'POST',
    body: JSON.stringify(request),
  });

/** `POST …/render/estimate` answer: what the render would cost and take, from the same request the render takes. */
export interface EditTimelineRenderEstimate {
  billingEnabled: boolean;
  credits?: number | null;
  creditsLow?: number | null;
  creditsHigh?: number | null;
  estimateSeconds?: number | null;
  balanceCredits?: number | null;
  affordable: boolean;
  unavailableReason?: string | null;
  /** About how big the file will be; absent when unknown. */
  estimatedSizeBytes?: number | null;
  sizeIsApproximate?: boolean | null;
}

export const estimateEditTimelineRender = (
  projectId: string,
  timelineId: string,
  request: RenderEditTimelineRequest,
): Promise<EditTimelineRenderEstimate> =>
  apiFetch<EditTimelineRenderEstimate>(`${editTimelineKey(projectId, timelineId)}/render/estimate`, {
    method: 'POST',
    body: JSON.stringify(request),
  });

export const listEditTimelineRenders = (projectId: string, timelineId: string): Promise<EditTimelineRenderSummary[]> =>
  apiFetch<EditTimelineRenderSummary[]>(editTimelineRendersKey(projectId, timelineId));

/** True while a render in the list is still queued or running, so the queue keeps itself current. */
export const hasActiveRender = (renders: readonly EditTimelineRenderSummary[] | undefined | null): boolean =>
  (renders ?? []).some((r) => r.status === 'Queued' || r.status === 'Running');

/** The subset of the client the sync client depends on — injectable for tests. */
export interface EditTimelineSyncApi {
  patchEditTimeline: typeof patchEditTimeline;
  getEditTimeline: typeof getEditTimeline;
  /** P9: read for offline reconciliation; absent means "cannot read history" (a moved server is refused). */
  getEditTimelineHistory?: typeof getEditTimelineHistory;
  /** R02.1: "Save my edits as a new timeline" duplicates, then writes the local document into the copy. */
  duplicateEditTimeline?: typeof duplicateEditTimeline;
}

export const defaultSyncApi: EditTimelineSyncApi = { patchEditTimeline, getEditTimeline, getEditTimelineHistory, duplicateEditTimeline };

// ─────────────────────────────────────────────────────────────────────────────
// Interchange (P6) — `GET …/export?format=` and `POST …/import`
// ─────────────────────────────────────────────────────────────────────────────

/** The formats `GET …/edit-timelines/{id}/export` writes (`EditTimelinesController.ExportFormats`). */
export const EXPORT_FORMATS = ['otio', 'edl', 'srt', 'vtt', 'fcpxml'] as const;
export type EditTimelineExportFormat = (typeof EXPORT_FORMATS)[number];

/**
 * The download URL of one timeline in one interchange format. A plain link: the browser follows it
 * with the session cookie, so nothing here fetches the bytes.
 */
export const editTimelineExportUrl = (projectId: string, timelineId: string, format: EditTimelineExportFormat, options?: { includeAudio?: boolean; roles?: boolean }): string => {
  const extra = format === 'fcpxml' && options ? `${options.includeAudio === false ? '&includeAudio=false' : ''}${options.roles === false ? '&roles=false' : ''}` : '';
  return `${editTimelineKey(projectId, timelineId)}/export?format=${format}${extra}`;
};

/** What one export format carries and drops, from the server (`GET …/export/scope?format=`). */
export interface ExportScope {
  format: EditTimelineExportFormat;
  carries: string[];
  dropped: string[];
  /** `reelbolt-url`: the file points at this project's media by URL; `none`: it names no media. */
  mediaMode: 'reelbolt-url' | 'none';
  /** R12.2: what a hand-off package would carry (absent from older servers). */
  handOff?: HandOffScope;
}

export interface HandOffScope {
  supported: boolean;
  mediaFiles: number;
  mediaBytes: number;
  wouldExceedCap: boolean;
  capBytes: number;
}

export type HandOffMedia = 'zip' | 'manifest';

/** R12.2: the hand-off package URL. A plain link: the browser streams the zip to disk, nothing is held in memory. */
export const editTimelineHandOffUrl = (
  projectId: string,
  timelineId: string,
  format: Exclude<EditTimelineExportFormat, 'srt' | 'vtt'>,
  options: { media: HandOffMedia; track?: string | null; includeAudio?: boolean; roles?: boolean },
): string => {
  const parts = [`format=${format}`, `media=${options.media}`];
  if (format === 'edl' && options.track) parts.push(`track=${encodeURIComponent(options.track)}`);
  if (format === 'fcpxml') {
    if (options.includeAudio === false) parts.push('includeAudio=false');
    if (options.roles === false) parts.push('roles=false');
  }
  return `${editTimelineKey(projectId, timelineId)}/handoff?${parts.join('&')}`;
};

export const editTimelineExportScopeKey = (projectId: string, timelineId: string, format: EditTimelineExportFormat): string =>
  `${editTimelineKey(projectId, timelineId)}/export/scope?format=${format}`;

export const getExportScope = (projectId: string, timelineId: string, format: EditTimelineExportFormat): Promise<ExportScope> =>
  apiFetch<ExportScope>(editTimelineExportScopeKey(projectId, timelineId, format));

/** `POST …/edit-timelines/import` body: the OTIO timeline as parsed JSON plus an optional name. */
export interface ImportEditTimelineRequest {
  name?: string | null;
  otio: unknown;
}

/** The 400 body of a refused import: what was wrong, and every media URL the project could not resolve. */
export interface EditTimelineImportRefused {
  message: string;
  errors: string[];
  unresolvedUrls: string[];
}

export const importEditTimeline = (projectId: string, request: ImportEditTimelineRequest, options: { strict?: boolean } = {}): Promise<EditTimelineResponse> =>
  apiFetch<EditTimelineResponse>(`${editTimelinesKey(projectId)}/import${options.strict ? '?strict=true' : ''}`, {
    method: 'POST',
    body: JSON.stringify({ name: request.name ?? null, otio: request.otio }),
  });

// ─────────────────────────────────────────────────────────────────────────────
// P8 — AI-assist proposals (`POST …/edit-timelines/{id}/proposals`). Nothing is written by the
// call: the client reviews the groups and applies the accepted ones through `applyProposal`.
// `503 { code: 'no_chat_provider' }` when no chat provider resolves for the agent.
// ─────────────────────────────────────────────────────────────────────────────

export const PROPOSAL_AGENTS = ['recut', 'graphics', 'grade', 'sfx', 'review'] as const;
export type ProposalAgent = (typeof PROPOSAL_AGENTS)[number];

export interface EditProposalRequest {
  agent: ProposalAgent;
  range?: { inFrame: number; outFrame: number } | null;
  brief?: string | null;
}

export interface EditProposalGroupResponse {
  label: string;
  ops: EditOp[];
  touchedItemIds: string[];
  touchesUserOwned: boolean;
  reason: string | null;
  /**
   * A content cut: spans of the item's asset in SOURCE seconds (same basis as `sourceIn / fps`). The
   * client compiles them into ripple-cut ops at apply time (`ops` is empty for such a group).
   */
  cuts?: ProposalCut[] | null;
}

export interface ProposalCut {
  itemId: string;
  startSec: number;
  endSec: number;
}

/** What a recut did, counted by the server, for the plain "what it did" line. */
export interface ProposalDid {
  cut: number;
  cutSeconds: number;
  remove: number;
  trim: number;
  mute: number;
  other: number;
  /** Per kind of content cut: how many stretches and how long (additive; absent on older servers). */
  cuts?: ProposalDidCut[] | null;
}

export interface ProposalDidCut {
  kind: 'speech' | 'quiet' | 'fillers' | 'lines' | 'keep';
  spans: number;
  seconds: number;
}

/** Footage that still needs analysing before the request can be answered, limited to what is missing. */
export interface ProposalNeedsAnalysis {
  kinds: string[];
  mediaRefs: string[];
}

export interface EditProposalResponse {
  proposalId: string;
  agent: ProposalAgent;
  summary: string;
  groups: EditProposalGroupResponse[];
  rejected: string[];
  /** T-021: which model answered, and a plain sentence of what was sent to it. */
  modelLabel?: string | null;
  disclosure?: ProposalDisclosure | null;
  /** R11.2: what the helper could see of the footage in range. */
  grounding?: ProposalGrounding | null;
  /** R11.2: true when some clip in range has no analysis yet, so Analyse first can help. */
  analysisAvailable?: boolean;
  /** Recut: what the proposal did, what was asked, and the swaps it refused. */
  did?: ProposalDid | null;
  asked?: { opClass: string | null; target: string | null } | null;
  /** How many mute/remove/trim actions were dropped because the request was to cut. */
  swapsRefused?: number | null;
  /** Some clips in range lack the analysis this request needs (partial coverage). */
  needsAnalysis?: ProposalNeedsAnalysis | null;
}

/** R11.2: how much of the footage the proposal was cut from. `note` is the server's plain sentence. */
export interface ProposalGrounding {
  level: 'transcriptAndShots' | 'transcriptOnly' | 'shotsOnly' | 'namesOnly';
  clipsWithFacts: number;
  clipsTotal: number;
  note: string;
}

/** R11.2: `GET …/proposals/availability`: per helper, whether it can run on the range, and why not. */
export interface ProposalAvailability {
  agents: Partial<Record<ProposalAgent, { available: boolean; grounding: ProposalGrounding | null; reason: string | null }>>;
  analysisAvailable: boolean;
  /** What the footage in range lacks (kinds) and which sources (`file:<id>` / `step_result:<id>`); absent on older servers. */
  analysisHint?: { kinds?: string[]; missingRefs?: string[] } | null;
}

export interface ProposalDisclosure {
  itemCount: number;
  rangeSeconds: number;
  soundFileCount: number;
  briefChars: number;
  transcriptSeconds: number;
  summary: string;
}

/** `POST …/proposals/estimate`: the price, the hourly allowance and the disclosure, before asking. Nothing is charged or counted. */
export interface ProposalEstimate {
  billingEnabled: boolean;
  credits?: number | null;
  creditsLow?: number | null;
  creditsHigh?: number | null;
  balanceCredits?: number | null;
  affordable: boolean;
  unavailableReason?: string | null;
  included?: boolean;
  modelLabel?: string | null;
  disclosure?: ProposalDisclosure | null;
  used: number;
  limit: number;
  retryAfterSeconds?: number | null;
  grounding?: ProposalGrounding | null;
  analysisAvailable?: boolean;
}

export const fetchProposalAvailability = (projectId: string, timelineId: string, range: { inFrame: number; outFrame: number } | null): Promise<ProposalAvailability> =>
  apiFetch<ProposalAvailability>(`${editTimelineKey(projectId, timelineId)}/proposals/availability${range ? `?startFrame=${range.inFrame}&endFrame=${range.outFrame}` : ''}`);

export const estimateEditProposal = (projectId: string, timelineId: string, request: EditProposalRequest): Promise<ProposalEstimate> =>
  apiFetch<ProposalEstimate>(`${editTimelineKey(projectId, timelineId)}/proposals/estimate`, {
    method: 'POST',
    body: JSON.stringify(request),
  });

const PROPOSAL_POLL_MS = 2000;
const PROPOSAL_GIVE_UP_MS = 6 * 60 * 1000;
const PROPOSAL_MAX_POLL_FAILURES = 3;

type ProposalJobTicket = { jobId: string; state: 'running' };
const isRunningTicket = (v: unknown): v is ProposalJobTicket =>
  typeof v === 'object' && v !== null && (v as { state?: unknown }).state === 'running' && typeof (v as { jobId?: unknown }).jobId === 'string';

const proposalPause = (ms: number, signal?: AbortSignal): Promise<void> =>
  new Promise((resolve, reject) => {
    if (signal?.aborted) return reject(new DOMException('Aborted', 'AbortError'));
    const onAbort = () => { clearTimeout(t); reject(new DOMException('Aborted', 'AbortError')); };
    const t = setTimeout(() => { signal?.removeEventListener('abort', onAbort); resolve(); }, ms);
    signal?.addEventListener('abort', onAbort, { once: true });
  });

/**
 * Asks for a proposal as a background job (the model can outlast the proxy limits) and polls until it
 * is done. Resolves/rejects exactly as the synchronous route would; an older server that answers 200
 * with the proposal is resolved directly. Gives up after 6 minutes with code PROPOSAL_TIMEOUT.
 */
export const requestEditProposal = async (projectId: string, timelineId: string, request: EditProposalRequest, signal?: AbortSignal): Promise<EditProposalResponse> => {
  const base = `${editTimelineKey(projectId, timelineId)}/proposals`;
  const first = await apiFetch<EditProposalResponse | ProposalJobTicket>(`${base}?background=true`, {
    method: 'POST',
    body: JSON.stringify(request),
    signal,
  });
  if (!isRunningTicket(first)) return first;

  const startedAt = Date.now();
  let failures = 0;
  for (;;) {
    if (Date.now() - startedAt >= PROPOSAL_GIVE_UP_MS) {
      const body = { code: 'PROPOSAL_TIMEOUT', message: 'The AI took too long to answer. You were not charged; please try again.' };
      throw new ApiError(body.message, 424, [], body);
    }
    await proposalPause(PROPOSAL_POLL_MS, signal);
    try {
      const next = await apiFetch<EditProposalResponse | ProposalJobTicket>(`${base}/jobs/${encodeURIComponent(first.jobId)}`, { signal });
      failures = 0;
      if (!isRunningTicket(next)) return next;
    } catch (e) {
      if (signal?.aborted) throw e;
      if (e instanceof ApiError && e.status < 500) throw e;
      if (++failures >= PROPOSAL_MAX_POLL_FAILURES) throw e;
    }
  }
};
