/**
 * The media API client — **the only module in this feature that knows the wire shape.**
 *
 * Every call routes through `apiFetch`, so base URL handling, error-message extraction and the
 * `204` no-body case live in exactly one place. No component imports `fetch`; that is asserted by
 * `web/lib/editing/media/__tests__/api-boundary.test.ts` rather than left to review.
 *
 * ## What this module knows, and what it deliberately refuses to know
 *
 *   - A source is addressed by an **opaque `mediaRef` token** (`file:<guid>` / `step_result:<guid>`).
 *     The token is interpolated verbatim into the path: its character set is URL-safe and the
 *     server's route constraint matches the decoded literal. Nothing here parses a GUID out of it or
 *     branches on the prefix — a GUID cannot tell a `ProjectFile` from a `WorkflowStepResult`, which
 *     is the entire reason the token exists.
 *   - Derivative bytes stream by **row id**, from a path built only by `derivativeContentUrl`.
 *   - No response is expected to contain a storage key, a bucket or an object URL. If one ever
 *     arrives it is ignored: the UI never reads a field like that.
 */

import { apiFetch, fetcher } from '../../api/client';
import type { AssetRef } from '../types';
import { isMediaRef } from '../media/types';
import type {
  AddBinItemRequest,
  CreateBinRequest,
  MediaAnnotation,
  UpsertMediaAnnotationRequest,
  FilmstripSheet,
  MediaBin,
  MediaBinItem,
  MediaBinItemDetail,
  MediaDerivativeRow,
  MediaFilmstripResponse,
  MoveBinItemRequest,
  MediaPoolItem,
  MediaRef,
  RequestMediaDerivativesRequest,
  RequestMediaDerivativesResponse,
  UpdateBinRequest,
} from '../media/types';

// ─────────────────────────────────────────────────────────────────────────────
// Paths
// ─────────────────────────────────────────────────────────────────────────────

export const mediaPoolKey = (projectId: string): string =>
  `/api/v1/projects/${projectId}/media`;

export const mediaBinsKey = (projectId: string): string =>
  `/api/v1/projects/${projectId}/media/bins`;

/** P6: every annotation of the project. */
export const mediaAnnotationsKey = (projectId: string): string =>
  `/api/v1/projects/${projectId}/media/annotations`;

export const mediaDerivativesKey = (projectId: string, mediaRef: MediaRef): string =>
  `/api/v1/projects/${projectId}/media/${mediaRef}/derivatives`;

/**
 * The filmstrip-index route of one source.
 *
 * Keyed by the source's own opaque token, which is interpolated verbatim — the same rule the
 * derivative route follows, and the reason a pool item carries `filmstripUrl` at all.
 */
export const mediaFilmstripKey = (projectId: string, mediaRef: MediaRef): string =>
  `/api/v1/projects/${projectId}/media/${mediaRef}/filmstrip`;

/**
 * The route that lists one bin's items.
 *
 * Keyed by `binId` alone, not by the pair: a bin lives in exactly one project.
 */
export const mediaBinItemsKey = (projectId: string, binId: string): string =>
  `${mediaBinsKey(projectId)}/${binId}/items`;

const binPath = (projectId: string, binId: string): string =>
  `/api/v1/projects/${projectId}/media/bins/${binId}`;

/**
 * The URL for a derivative row's bytes.
 *
 * The row's own `contentUrl` is preferred when the server sent one, and the canonical path is built
 * as the fallback — so a row with a missing or empty `contentUrl` still yields a usable URL, and
 * **no other module ever hardcodes a derivative path**. The result is always non-empty.
 */
export function derivativeContentUrl(
  projectId: string,
  row: Pick<MediaDerivativeRow, 'id' | 'contentUrl'>,
): string {
  const fromRow = typeof row?.contentUrl === 'string' ? row.contentUrl.trim() : '';
  if (fromRow.length > 0) return fromRow;
  return `/api/v1/projects/${projectId}/media/derivatives/${row.id}/content`;
}

/**
 * The route a filmstrip sheet's bytes stream from: the OWNING derivative row plus the sheet's own
 * 0-based index.
 *
 * This mirrors the path the server builds into `MediaFilmstripSheetResponse.ContentUrl` exactly.
 * It exists for two reasons: the fixtures need a real URL without hand-spelling a route (the
 * boundary test forbids that outside this module), and it documents WHY a sheet cannot be resolved
 * the way a derivative row is — sheets 1..N-1 have no `media_derivatives` row of their own, so there
 * is no row id to build a per-sheet path from.
 */
export const filmstripSheetContentUrl = (
  projectId: string,
  derivativeId: string,
  sheetIndex: number,
): string =>
  `/api/v1/projects/${projectId}/media/derivatives/${derivativeId}/sheet/${sheetIndex}/content`;

/**
 * The URL for a filmstrip sprite sheet, or `null` when the body carries none.
 *
 * Unlike a derivative row, a sheet's path **cannot be rebuilt from the body**: the sheet is
 * addressed through the derivative row that owns the density plus the sheet's own index, and the
 * index body carries neither the owning row id nor the sheet's position on the wire. The landed
 * `MediaFilmstripSheetResponse.ContentUrl` is therefore always present, and this helper's whole job
 * is to be the ONE place that decides a body's URL is unusable — so a component renders a stated
 * "sheet image unavailable" instead of a broken background.
 */
export function filmstripSheetUrl(sheet: Pick<FilmstripSheet, 'contentUrl'>): string | null {
  const url = typeof sheet?.contentUrl === 'string' ? sheet.contentUrl.trim() : '';
  return url.length > 0 ? url : null;
}

/**
 * The URL of a source's **original** bytes, or `null` when the token names no known source kind.
 *
 * - `file:<id>` streams from the project-files **media** route (`files/{id}/media`): inline,
 *   Range-aware, project-scoped — the same route the file manager's player uses. It is NOT the
 *   `files/{id}/content` route: that one is the text editor's and answers 409 for any media file,
 *   which silenced every audio clip and blacked out every video without a proxy.
 * - `step_result:<id>` streams from the outputs **download** route (`outputs/{id}/download`),
 *   inline and Range-aware as well, scoped to the project's own `outputFiles/` keys.
 *
 * Both routes answer 404 for a source outside the project, never 403.
 *
 * ## Why the mediaRef is read here, and nowhere else
 *
 * The token is opaque to every component: a pool item is addressed by `file:<guid>` /
 * `step_result:<guid>` and nothing in the UI may look inside it. This function is the ONE place
 * that turns a token into a route, and it exists here — beside {@link derivativeContentUrl} — for
 * the same reason that helper does: `api/media.ts` is the only module that knows a wire path, and
 * T-202's `api-boundary.test.ts` asserts that no other module in the feature spells one. A
 * component that built this path for itself would be the exception that test exists to prevent.
 *
 * The prefix is compared literally, exactly once, and the body is passed through as an opaque
 * string: nothing here parses it, validates it as a GUID, or uses it as an identity.
 */
export function originalContentUrl(projectId: string, mediaRef: MediaRef): string | null {
  if (typeof mediaRef !== 'string') return null;

  const separator = mediaRef.indexOf(':');
  if (separator <= 0) return null;

  const prefix = mediaRef.slice(0, separator);
  const sourceId = mediaRef.slice(separator + 1);
  if (sourceId.length === 0) return null;
  if (prefix === 'file') return `/api/v1/projects/${projectId}/files/${sourceId}/media`;
  if (prefix === 'step_result') return `/api/v1/projects/${projectId}/outputs/${sourceId}/download`;
  return null;
}

/**
 * P6: the one place a pool `mediaRef` becomes a document `AssetRef` (the Cut page inserts pool
 * sources, the inspector relinks a clip to one) — the inverse of `preview/scheduler.ts#mediaRefOf`,
 * and here for the same reason `originalContentUrl` is: this module is the only one allowed to
 * look inside the token. Split on the first `:` exactly once; the id is passed through opaque.
 */
export function assetRefFromMediaRef(mediaRef: MediaRef): AssetRef | null {
  if (!isMediaRef(mediaRef)) return null;
  const separator = mediaRef.indexOf(':');
  const prefix = mediaRef.slice(0, separator);
  const id = mediaRef.slice(separator + 1);
  if (prefix === 'file') return { kind: 'projectFile', projectFileId: id };
  if (prefix === 'step_result') return { kind: 'stepResult', stepResultId: id };
  return null;
}

// ─────────────────────────────────────────────────────────────────────────────
// Reads
// ─────────────────────────────────────────────────────────────────────────────

/** The project media pool: project files with media MIME types plus unregistered step renders. */
export const fetchMediaPool = (projectId: string): Promise<MediaPoolItem[]> =>
  fetcher<MediaPoolItem[]>(mediaPoolKey(projectId));

/**
 * One source's derivatives, as a FLAT LIST of rows.
 *
 * An empty list is a normal answer meaning "no derivative has been generated yet" — never an error,
 * and never evidence that generation failed: the contract has no `state` field, so the client cannot
 * tell "not yet" from "failed" from "unsupported" and must not claim any of them.
 */
export const fetchMediaDerivatives = (
  projectId: string,
  mediaRef: MediaRef,
): Promise<MediaDerivativeRow[]> =>
  fetcher<MediaDerivativeRow[]>(mediaDerivativesKey(projectId, mediaRef));

/**
 * One source's filmstrip index, in the exact nesting `resolveFilmstripThumb` consumes.
 *
 * **`densities: []` is a normal answer, not an error.** It means this source has no filmstrip index
 * yet, and the strip says so rather than inferring a density from a sheet count or a duration. The
 * index describes what the WRITER measured; whether a given sheet's bytes have been uploaded is
 * that sheet's own URL's answer (a per-sheet `404`), which is a degradation the strip states
 * per-cell rather than a hole in the index.
 *
 * A source that no longer resolves — deleted, foreign, never media — is a `404`, the same single
 * answer every other route on this controller gives.
 */
export const fetchMediaFilmstrip = (
  projectId: string,
  mediaRef: MediaRef,
): Promise<MediaFilmstripResponse> =>
  fetcher<MediaFilmstripResponse>(mediaFilmstripKey(projectId, mediaRef));

/** Every non-deleted bin of the project, flat, ordered deterministically by the server. */
export const fetchMediaBins = (projectId: string): Promise<MediaBin[]> =>
  fetcher<MediaBin[]>(mediaBinsKey(projectId));

/**
 * One bin's items — the real listing, ordered by `sortOrder`.
 *
 * Each row carries the four fields of the source the pin names, and **all four are null for a
 * dangling pin** (the source row was deleted or moved out of this project). The row is still
 * returned, because a pin the client cannot see is a pin the client cannot remove: a listing that
 * filtered dangling pins out would strand exactly the entries a user most needs to clean up.
 */
export const fetchBinItems = (
  projectId: string,
  binId: string,
): Promise<MediaBinItemDetail[]> =>
  fetcher<MediaBinItemDetail[]>(mediaBinItemsKey(projectId, binId));

// ─────────────────────────────────────────────────────────────────────────────
// Bin writes
// ─────────────────────────────────────────────────────────────────────────────

export const createBin = (projectId: string, request: CreateBinRequest): Promise<MediaBin> =>
  apiFetch<MediaBin>(mediaBinsKey(projectId), {
    method: 'POST',
    body: JSON.stringify(request),
  });

/** Renames and/or reparents a bin. Omitted fields are left unchanged. */
export const updateBin = (
  projectId: string,
  binId: string,
  request: UpdateBinRequest,
): Promise<MediaBin> =>
  apiFetch<MediaBin>(binPath(projectId, binId), {
    method: 'PATCH',
    body: JSON.stringify(request),
  });

/** Soft-deletes a bin and its whole subtree plus its items. */
export const deleteBin = (projectId: string, binId: string): Promise<void> =>
  apiFetch<void>(binPath(projectId, binId), { method: 'DELETE' });

/**
 * Adds a pool clip to a bin.
 *
 * Adding the same mediaRef twice is not a duplicate-row error: the server returns the existing item.
 */
export const addBinItem = (
  projectId: string,
  binId: string,
  mediaRef: MediaRef,
): Promise<MediaBinItem> => {
  const body: AddBinItemRequest = { mediaRef };
  return apiFetch<MediaBinItem>(`${binPath(projectId, binId)}/items`, {
    method: 'POST',
    body: JSON.stringify(body),
  });
};

/**
 * Moves an existing bin item to another bin.
 *
 * The body field is **`binId`** — the bin to move the item INTO — as the landed
 * `MoveMediaBinItemRequest` declares.
 *
 * The `itemId` argument is reachable from two places now: `MediaPoolItem.bins[].itemId` and
 * `GET .../media/bins/{binId}/items`. Nothing in this feature calls it yet — the UI's bin
 * interactions are add and remove, and a move is expressed as remove-then-add — but it is
 * implemented because the write surface is part of the contract.
 */
export const moveBinItem = (
  projectId: string,
  binId: string,
  itemId: string,
  targetBinId: string,
): Promise<MediaBinItem> => {
  const body: MoveBinItemRequest = { binId: targetBinId };
  return apiFetch<MediaBinItem>(`${binPath(projectId, binId)}/items/${itemId}`, {
    method: 'PATCH',
    body: JSON.stringify(body),
  });
};

/**
 * Removes a bin item — the write that makes a clip leave a bin.
 *
 * `itemId` comes from `MediaPoolItem.bins[].itemId` or from the bin's own item listing. Removing a
 * pin does not delete the source; it only detaches it from this one bin.
 */
export const removeBinItem = (
  projectId: string,
  binId: string,
  itemId: string,
): Promise<void> =>
  apiFetch<void>(`${binPath(projectId, binId)}/items/${itemId}`, { method: 'DELETE' });

// ─────────────────────────────────────────────────────────────────────────────
// Derivative production — the ONE write in this feature that asks for work
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Asks the service to produce a source's derivative set, and returns what was asked for.
 *
 * ## When this may be called — a contract decision, not a style preference
 *
 * **On explicit user action only.** The sanctioned trigger is the user opening/selecting an asset
 * in the media workspace's detail pane, which is this initiative's implementation of issue #123
 * §3.6's "lazy derivation on first open". It must **never** be called from a hover and never from a
 * list render: a pointer crossing a grid would otherwise queue an unbounded amount of encoding
 * work, and the user would have asked for none of it. A dedicated "Prepare proxy"/"Generate
 * derivatives" affordance is the other sanctioned caller, because pressing a button is as explicit
 * as opening a clip.
 *
 * ## What the answer means
 *
 * A `202 Accepted` means the request was **published**, nothing more. This API has no ffmpeg, so
 * nothing is generated synchronously and no derivative row exists because of this call — a client
 * must never present it as "the proxy is ready now". The returned `kinds` are what was asked for;
 * deciding what is worth producing is the consumer's job.
 *
 * `kinds` is omitted from the default call on purpose: the server then requests **every member of
 * the closed enum**, which is enumerated from the enum itself so a kind added later is requested by
 * the very next editor-open instead of being silently skipped until someone updates a list here.
 */
export const requestMediaDerivatives = (
  projectId: string,
  request: RequestMediaDerivativesRequest,
): Promise<RequestMediaDerivativesResponse> =>
  apiFetch<RequestMediaDerivativesResponse>(
    `/api/v1/projects/${projectId}/media/derivatives/request`,
    { method: 'POST', body: JSON.stringify(request) },
  );

/**
 * Asks for the CONVERTED proxy of one source: the plain proxy run through a camera profile, so the viewer plays it with
 * the input stage already baked in (8-bit log in an unconverted proxy bands badly). Like every request here: `202` means
 * published, not ready. The caller dedupes per (source, profile); the viewer calls this at most once per session each.
 */
export const requestConvertedProxy = (
  projectId: string,
  mediaRef: MediaRef,
  cameraProfile: string,
): Promise<RequestMediaDerivativesResponse> =>
  requestMediaDerivatives(projectId, { mediaRef, kinds: ['proxy'], cameraProfile });

// ─────────────────────────────────────────────────────────────────────────────
// P6: annotations (keywords, note, colour label, favourite, flag)
// ─────────────────────────────────────────────────────────────────────────────

/** Every annotated source of the project; a source with no row simply has no entry. */
export const fetchMediaAnnotations = (projectId: string): Promise<MediaAnnotation[]> =>
  fetcher<MediaAnnotation[]>(mediaAnnotationsKey(projectId));

/**
 * Writes one source's annotation. The mediaRef is interpolated verbatim, like every other route
 * here; the server creates the row on first write and merges on later ones (omitted fields keep
 * their stored value — see `UpsertMediaAnnotationRequest`).
 */
export const upsertMediaAnnotation = (
  projectId: string,
  mediaRef: MediaRef,
  request: UpsertMediaAnnotationRequest,
): Promise<MediaAnnotation> =>
  apiFetch<MediaAnnotation>(`${mediaAnnotationsKey(projectId)}/${mediaRef}`, {
    method: 'PUT',
    body: JSON.stringify(request),
  });
