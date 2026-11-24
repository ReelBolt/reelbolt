import { handleUnauthorized, unauthorizedMessage } from '../auth/session-expiry';
import { toWireContent } from '../editor/text-content';
import { apiFetch, ApiError, extractErrorList, extractErrorMessage } from './client';
import { parseChargedHeader } from '../editor/credit-estimate';
import {
  IMAGE_EDITOR_ERROR_CODES,
  IMAGE_GRAPHIC_ERROR_CODES,
  parseLayerKind,
  type AssetUploadRequest,
  type AssetUploadTicket,
  type CreateImageDocumentRequest,
  type CreateImageExportRequest,
  type CreateImageExportResult,
  type CreateImageGraphicRequest,
  type CreateLayerFromFileRequest,
  type CreateTextLayerRequest,
  type DetectSubjectRequest,
  type EditImageDocument,
  type EditImageDocumentSummary,
  type EditImageExport,
  type EditImageLayer,
  type EditImageOperation,
  type ImageOperationCapabilities,
  type ImageGraphicJob,
  type ImageGraphicsResponse,
  type ImageOperationRequest,
  type ImageOperationResult,
  type SegmentSubjectsRequest,
  type SourceTooLargeBody,
  type UpdateImageDocumentRequest,
  type UpdateImageLayerRequest,
} from '../types/image-document';
import { readableServerMessage } from './describe-error';

/**
 * The image editor's layer documents — one picture decomposed into ordered, own-pixel layers.
 *
 * House style follows `lib/api/files.ts`: one thin typed function per route, no state, and the
 * two routes that answer PNG BYTES exposed as URL builders rather than fetchers, because their
 * consumer is an `<img src>` and not a JSON call site. A document's `imageUrl`/`matteUrl` already
 * point at those same two routes, so a caller holding a document should use those; the builders are
 * for the caller that holds only ids.
 *
 * Every layer that leaves this module has crossed {@link normalizeLayer}: the server writes the
 * `kind` enum member as it is spelled in C# (`Cutout`), and the web union is lower-cased. This file
 * and `lib/types/image-document.ts` are the only two places the thumbnail editor's wire shapes are
 * decided, so a change on the server is a change to those two files.
 *
 * Money is spent by `detectSubjects`, `segmentSubjects` and `runImageOperation` — vendor calls —
 * so those are the ones whose failure a caller must surface rather than swallow.
 */

const base = (projectId: string) => `/api/v1/projects/${projectId}/image-documents`;

/** Reconciles a layer's wire shape with the web types (kind casing). Pure; safe to apply twice. */
export function normalizeLayer(layer: EditImageLayer): EditImageLayer {
  return { ...layer, kind: parseLayerKind(layer.kind as string) };
}

export function normalizeDocument(doc: EditImageDocument): EditImageDocument {
  return { ...doc, layers: (doc.layers ?? []).map(normalizeLayer) };
}

/**
 * The error `code` of a non-2xx answer (`{ code, message }`), or null. The thumbnail editor branches
 * on these codes (`REVISION_CONFLICT`, `OPERATION_UNSUPPORTED`, `*_PROVIDER_UNAVAILABLE`, …).
 */
export function errorCodeOf(cause: unknown): string | null {
  if (!(cause instanceof ApiError)) return null;
  const body = cause.body;
  if (typeof body === 'object' && body !== null) {
    const code = (body as Record<string, unknown>).code;
    if (typeof code === 'string') return code;
  }
  return null;
}

/** True for a 409 `REVISION_CONFLICT`: someone else wrote the document since this client read it. */
export function isRevisionConflict(cause: unknown): boolean {
  if (!(cause instanceof ApiError) || cause.status !== 409) return false;
  const code = errorCodeOf(cause);
  return code === null || code === IMAGE_EDITOR_ERROR_CODES.revisionConflict;
}

/**
 * True when the operation cannot run on this deployment — no provider row for the capability
 * (`*_PROVIDER_UNAVAILABLE`) or a provider kind that cannot perform it (`OPERATION_UNSUPPORTED`).
 * The AI panel disables the card with the server's own reason when this holds.
 */
export function isOperationUnavailable(cause: unknown): boolean {
  const code = errorCodeOf(cause);
  if (!code) return false;
  return (
    code === IMAGE_EDITOR_ERROR_CODES.operationUnsupported ||
    code.endsWith(IMAGE_EDITOR_ERROR_CODES.providerUnavailableSuffix)
  );
}

/**
 * True for a `424` answer: the AI service failed or timed out. The server may still have written
 * something (a layer that landed late), so the editor reads the document again after one.
 */
export function isUpstreamFailure(cause: unknown): boolean {
  return cause instanceof ApiError && cause.status === 424;
}

/**
 * How a write is sent. `keepalive` lets it outlive the page: the editor uses it for the last edits
 * when the user leaves (another page, the tab closed), which a plain request would lose.
 */
export interface WriteDelivery {
  keepalive?: boolean;
}

/**
 * Every layer document in the project, newest first and WITHOUT layers — the route answers summaries
 * (`EditImageDocumentSummaryDto`), so a caller that needs the layers reads one document through
 * {@link getImageDocument}. The project scope is the backend's own ownership check.
 */
export function getImageDocuments(projectId: string): Promise<EditImageDocumentSummary[]> {
  return apiFetch<EditImageDocumentSummary[]>(base(projectId));
}

export async function getImageDocument(
  projectId: string,
  documentId: string,
): Promise<EditImageDocument> {
  return normalizeDocument(await apiFetch<EditImageDocument>(`${base(projectId)}/${documentId}`));
}

/**
 * Opens a document on one project image. The server adds the source layer itself (the whole picture,
 * role `background`) so a fresh document is never empty, and makes that picture the layer's own source.
 */
export async function createImageDocument(
  projectId: string,
  payload: CreateImageDocumentRequest,
): Promise<EditImageDocument> {
  return normalizeDocument(
    await apiFetch<EditImageDocument>(base(projectId), {
      method: 'POST',
      body: JSON.stringify(payload),
    }),
  );
}

/** A copy of a picture with all its layers (201, the same document shape as a read). */
export async function duplicateImageDocument(projectId: string, documentId: string): Promise<EditImageDocument> {
  return normalizeDocument(await apiFetch<EditImageDocument>(`${base(projectId)}/${documentId}/duplicate`, { method: 'POST' }));
}

export function deleteImageDocument(projectId: string, documentId: string): Promise<void> {
  return apiFetch<void>(`${base(projectId)}/${documentId}`, { method: 'DELETE' });
}

/**
 * The wire form of a document patch. The server's clearing rules: `backgroundColor: ""` and
 * `name: ""` clear (so a `null` colour, this module's word for "transparent", goes out as `""`),
 * and the canvas size is two numbers that travel together, each 16..8192 (or `0` and `0` to go back
 * to the source image's size) — a patch naming only one side is completed from `current`.
 */
export function documentPatchBody(
  payload: UpdateImageDocumentRequest,
  current?: { canvasWidth: number; canvasHeight: number },
): Record<string, unknown> {
  const { backgroundColor, canvasWidth, canvasHeight, ...rest } = payload;
  const body: Record<string, unknown> = { ...rest };
  if (backgroundColor !== undefined) body.backgroundColor = backgroundColor ?? '';
  if (canvasWidth !== undefined || canvasHeight !== undefined) {
    body.canvasWidth = canvasWidth ?? current?.canvasWidth;
    body.canvasHeight = canvasHeight ?? current?.canvasHeight;
  }
  return body;
}

/**
 * Changes the document itself: its name, canvas size and background colour. A stale `baseRevision`
 * is a 409 `REVISION_CONFLICT` with nothing written.
 */
export async function updateImageDocument(
  projectId: string,
  documentId: string,
  payload: UpdateImageDocumentRequest,
  current?: { canvasWidth: number; canvasHeight: number },
  delivery?: WriteDelivery,
): Promise<EditImageDocument> {
  return normalizeDocument(
    await apiFetch<EditImageDocument>(`${base(projectId)}/${documentId}`, {
      method: 'PATCH',
      body: JSON.stringify(documentPatchBody(payload, current)),
      ...(delivery?.keepalive ? { keepalive: true } : {}),
    }),
  );
}

/**
 * Asks the segmentation provider to cut a subject out and answers with the NEW layers — `cutout`
 * layers whose pixels are RGBA and whose `segmentationModel`/`segmentationPrompt` record what
 * produced them. The route answers an ARRAY, one layer per mask: exactly one unless
 * `returnMultipleMasks` was asked for. The document's other layers are untouched.
 *
 * The text-only request type; {@link segmentSubjects} is the same call with point and box prompts.
 */
export function detectSubjects(
  projectId: string,
  documentId: string,
  payload: DetectSubjectRequest,
): Promise<EditImageLayer[]> {
  return segmentSubjects(projectId, documentId, payload);
}

/**
 * The extended subjects route: text, point and box prompts, optionally several masks back. Answers
 * one `cutout` layer per mask (a bare single layer, which an older server sent, is read as a
 * one-element list). With `layerId` the cut-out is made from that picture layer (any layer with pixels)
 * and lands over it; coordinates in `pointPrompts`/`boxPrompts` are then that layer's pixels. Without it,
 * the document's original image, as before.
 */
export async function segmentSubjects(
  projectId: string,
  documentId: string,
  payload: SegmentSubjectsRequest,
  /** Told what the run cost (the `X-Charged-Credits` header; null when billing is off). */
  onCharged?: (credits: number | null) => void,
): Promise<EditImageLayer[]> {
  const url = `${base(projectId)}/${documentId}/subjects`;
  if (!onCharged) {
    const result = await apiFetch<EditImageLayer | EditImageLayer[]>(url, { method: 'POST', body: JSON.stringify(payload) });
    return (Array.isArray(result) ? result : [result]).map(normalizeLayer);
  }
  // The settled amount rides in a response header, which `apiFetch` does not expose.
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  });
  if (!res.ok) {
    let message = res.statusText;
    let errors: string[] = [];
    let body: unknown;
    try {
      body = await res.json();
      message = extractErrorMessage(body) || message;
      errors = extractErrorList(body);
    } catch {}
    handleUnauthorized(res.status);
    throw new ApiError(unauthorizedMessage(res.status) ?? message, res.status, errors, body);
  }
  onCharged(parseChargedHeader(res.headers.get('X-Charged-Credits')));
  const result = (await res.json()) as EditImageLayer | EditImageLayer[];
  return (Array.isArray(result) ? result : [result]).map(normalizeLayer);
}

/** Changes a layer's role, place in the stack, opacity, transform, visibility, lock, blend or text. */
export async function updateImageLayer(
  projectId: string,
  documentId: string,
  layerId: string,
  payload: UpdateImageLayerRequest,
  delivery?: WriteDelivery,
): Promise<EditImageLayer> {
  const body = payload.content ? { ...payload, content: toWireContent(payload.content) } : payload;
  return normalizeLayer(
    await apiFetch<EditImageLayer>(`${base(projectId)}/${documentId}/layers/${layerId}`, {
      method: 'PATCH',
      body: JSON.stringify(body),
      ...(delivery?.keepalive ? { keepalive: true } : {}),
    }),
  );
}

export function deleteImageLayer(
  projectId: string,
  documentId: string,
  layerId: string,
  delivery?: WriteDelivery,
): Promise<void> {
  return apiFetch<void>(`${base(projectId)}/${documentId}/layers/${layerId}`, {
    method: 'DELETE',
    ...(delivery?.keepalive ? { keepalive: true } : {}),
  });
}

/** Adds a vector `text` layer. The layer has no bitmap; the browser renders it. */
export async function createTextLayer(
  projectId: string,
  documentId: string,
  payload: CreateTextLayerRequest,
): Promise<EditImageLayer> {
  return normalizeLayer(
    await apiFetch<EditImageLayer>(`${base(projectId)}/${documentId}/layers/text`, {
      method: 'POST',
      body: JSON.stringify({ ...payload, content: toWireContent(payload.content) }),
    }),
  );
}

/** Places a project image as an `image` layer (409 `IMAGE_SOURCE_UNSUPPORTED` for a non-image). */
export async function createLayerFromFile(
  projectId: string,
  documentId: string,
  payload: CreateLayerFromFileRequest,
): Promise<EditImageLayer> {
  return normalizeLayer(
    await apiFetch<EditImageLayer>(`${base(projectId)}/${documentId}/layers/from-file`, {
      method: 'POST',
      body: JSON.stringify(payload),
    }),
  );
}

/** Step 1 of a mask or export upload: a presigned PUT under the document's `image-editor/` prefix. */
export function requestAssetUpload(
  projectId: string,
  documentId: string,
  payload: AssetUploadRequest,
): Promise<AssetUploadTicket> {
  return apiFetch<AssetUploadTicket>(`${base(projectId)}/${documentId}/assets/upload-url`, {
    method: 'POST',
    body: JSON.stringify(payload),
  });
}

/**
 * Step 2: PUT the PNG straight to storage — never through nginx. No credentials are sent (the signed
 * URL is the authorization) and the ticket's headers go out unchanged, because the signature covers
 * them. Resolves on a 2xx.
 */
export async function putBlobToStorage(ticket: AssetUploadTicket, blob: Blob): Promise<void> {
  let response: Response;
  try {
    response = await fetch(ticket.url, {
      method: 'PUT',
      headers: ticket.headers,
      body: blob,
      credentials: 'omit',
    });
  } catch {
    throw new Error(
      'The upload could not reach storage. Check your connection and try again. If it keeps failing on a self-hosted install, the storage address or its browser (CORS) setting needs fixing by whoever runs it.',
    );
  }
  if (!response.ok) throw new Error(`Storage refused the upload (HTTP ${response.status}).`);
}

/**
 * Uploads a `mask` (always a PNG) or an `export` (any of the export types) — ticket, then PUT — and
 * answers the storage key to hand to `runImageOperation` / `createImageExport`. The key's extension
 * follows `contentType`, and the export route records the file under that type.
 */
export async function uploadEditorAsset(
  projectId: string,
  documentId: string,
  purpose: AssetUploadRequest['purpose'],
  blob: Blob,
  contentType: AssetUploadRequest['contentType'] = 'image/png',
): Promise<string> {
  const ticket = await requestAssetUpload(projectId, documentId, {
    purpose,
    contentType,
    contentLength: blob.size,
  });
  await putBlobToStorage(ticket, blob);
  return ticket.key;
}

/**
 * Runs one AI operation. Synchronous: the server caps the vendor wait at 90 s and answers 424
 * `OPERATION_TIMEOUT` past it (424, not 504, so the JSON reason survives Cloudflare; a vendor failure is
 * 424 `OPERATION_FAILED`). The result carries the NEW layers plus the provider that served it. An expand of a
 * picture larger than the service takes answers 422 `SOURCE_TOO_LARGE` ({@link sourceTooLarge}) and nothing
 * is spent: send the copy it names as `sourceKey`.
 */
export async function runImageOperation(
  projectId: string,
  documentId: string,
  payload: ImageOperationRequest,
): Promise<ImageOperationResult> {
  const result = await apiFetch<ImageOperationResult>(
    `${base(projectId)}/${documentId}/operations`,
    { method: 'POST', body: JSON.stringify(payload) },
  );
  return { ...result, layers: (result.layers ?? []).map(normalizeLayer) };
}

/**
 * What the AI service set up for a tool can take (prompt or not; for expand its geometry and limits). Read
 * from the service's catalogue, so it costs nothing. A 409 means no service is set up for the tool.
 */
export function getOperationCapabilities(
  projectId: string,
  operation: EditImageOperation,
  /** For a tool priced by output size: the document and the size the run would use. */
  size?: { documentId: string; width: number; height: number },
  /**
   * `subjects: true` asks about a run WITH avatars or objects. For "create a picture" that run goes to the
   * picture-edit service (the subjects' own pictures are its input), so the server answers that route's
   * availability, prompt limits and price; for every other tool the answer is the same as without it.
   */
  options?: { subjects?: boolean },
): Promise<ImageOperationCapabilities> {
  const params: string[] = [];
  if (size) {
    params.push(
      `documentId=${encodeURIComponent(size.documentId)}`,
      `width=${Math.round(size.width)}`,
      `height=${Math.round(size.height)}`,
    );
  }
  if (options?.subjects === true) params.push('subjects=true');
  const query = params.length > 0 ? `?${params.join('&')}` : '';
  return apiFetch<ImageOperationCapabilities>(
    `${base(projectId)}/operations/${encodeURIComponent(operation)}/capabilities${query}`,
  );
}

/** True for the `409 { reason: 'NO_PROVIDER' }` refusal: no AI service is set up for the tool at all. */
export function isNoProviderRefusal(cause: unknown): boolean {
  return (
    cause instanceof ApiError &&
    typeof cause.body === 'object' &&
    cause.body !== null &&
    (cause.body as Record<string, unknown>).reason === 'NO_PROVIDER'
  );
}

/** `canConfigure` of a no-provider refusal (`409 { reason: 'NO_PROVIDER', canConfigure }`), or false. */
export function canConfigureOf(cause: unknown): boolean {
  if (!(cause instanceof ApiError) || typeof cause.body !== 'object' || cause.body === null) return false;
  return (cause.body as Record<string, unknown>).canConfigure === true;
}

/**
 * The body of a `422 SOURCE_TOO_LARGE` answer - the size of the copy of the layer to send instead of its
 * original - or null for any other failure.
 */
export function sourceTooLarge(cause: unknown): SourceTooLargeBody | null {
  if (!(cause instanceof ApiError) || cause.status !== 422) return null;
  if (errorCodeOf(cause) !== IMAGE_EDITOR_ERROR_CODES.sourceTooLarge) return null;
  const body = cause.body as Record<string, unknown>;
  const width = body.width;
  const height = body.height;
  if (typeof width !== 'number' || typeof height !== 'number' || width < 1 || height < 1) return null;
  return {
    code: 'SOURCE_TOO_LARGE',
    message: typeof body.message === 'string' ? body.message : '',
    width: Math.round(width),
    height: Math.round(height),
    source: body.source === 'smaller' ? 'smaller' : 'displayed',
  };
}

/** Registers an uploaded export (flat or layered) as a project file and an export row. */
export function createImageExport(
  projectId: string,
  documentId: string,
  payload: CreateImageExportRequest,
): Promise<CreateImageExportResult> {
  return apiFetch<CreateImageExportResult>(`${base(projectId)}/${documentId}/exports`, {
    method: 'POST',
    body: JSON.stringify(payload),
  });
}

export function getImageExports(projectId: string, documentId: string): Promise<EditImageExport[]> {
  return apiFetch<EditImageExport[]>(`${base(projectId)}/${documentId}/exports`);
}

/** The layer's own pixels as a PNG — an `<img src>`, not a JSON call. */
export function imageLayerUrl(projectId: string, documentId: string, layerId: string): string {
  return `${base(projectId)}/${documentId}/layers/${layerId}/image`;
}

/**
 * The layer's binary matte as a PNG — an `<img src>`, not a JSON call. Answers 404 when the layer
 * has none, which is exactly when the DTO's `matteUrl` is null: check that before pointing here.
 */
export function imageLayerMatteUrl(projectId: string, documentId: string, layerId: string): string {
  return `${base(projectId)}/${documentId}/layers/${layerId}/matte`;
}

/** The result of writing or clearing a layer mask: the layer (PUT only) and the document's new revision. */
export interface MatteWriteResult {
  layer: EditImageLayer | null;
  revision: number | null;
}

async function matteFailure(res: Response): Promise<ApiError> {
  let message = res.statusText;
  let errors: string[] = [];
  let body: unknown;
  try {
    body = await res.json();
    message = extractErrorMessage(body) || message;
    errors = extractErrorList(body);
  } catch {}
  handleUnauthorized(res.status);
  return new ApiError(unauthorizedMessage(res.status) ?? message, res.status, errors, body);
}

function revisionOf(res: Response): number | null {
  const raw = res.headers.get('X-Image-Document-Revision');
  const n = raw === null ? NaN : Number(raw);
  return Number.isFinite(n) ? n : null;
}

const matteQuery = (baseRevision?: number) => (baseRevision === undefined ? '' : `?baseRevision=${baseRevision}`);

/**
 * Stores a layer's mask: raw PNG bytes (the PNG's size must equal the layer's pixel size). Visible
 * amount: 255 shows, 0 hides. Answers the layer and, in `X-Image-Document-Revision`, the new revision.
 */
export async function putImageLayerMatte(
  projectId: string,
  documentId: string,
  layerId: string,
  png: Blob,
  baseRevision?: number,
): Promise<MatteWriteResult> {
  const res = await fetch(`${imageLayerMatteUrl(projectId, documentId, layerId)}${matteQuery(baseRevision)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'image/png' },
    body: png,
  });
  if (!res.ok) throw await matteFailure(res);
  return { layer: normalizeLayer((await res.json()) as EditImageLayer), revision: revisionOf(res) };
}

/** Removes a layer's mask (the layer's own pixels were never changed). */
export async function deleteImageLayerMatte(
  projectId: string,
  documentId: string,
  layerId: string,
  baseRevision?: number,
): Promise<MatteWriteResult> {
  const res = await fetch(`${imageLayerMatteUrl(projectId, documentId, layerId)}${matteQuery(baseRevision)}`, {
    method: 'DELETE',
  });
  if (!res.ok) throw await matteFailure(res);
  return { layer: null, revision: revisionOf(res) };
}

// ── "Design a graphic" ──────────────────────────────────────────────────────────────────────────

/** A job with its `layer` normalised (kind casing), like every other layer that leaves this module. */
export function normalizeGraphicJob(job: ImageGraphicJob): ImageGraphicJob {
  return { ...job, layer: job.layer ? normalizeLayer(job.layer) : null };
}

/**
 * The document's design jobs, newest first, plus the cost of another one and whether this
 * installation can design at all. Reading it also SETTLES finished jobs on the server, so a job that
 * turned `Passed` comes back with the `layer` it inserted.
 */
export async function getImageGraphics(projectId: string, documentId: string): Promise<ImageGraphicsResponse> {
  const result = await apiFetch<ImageGraphicsResponse>(imageGraphicsUrl(projectId, documentId));
  return {
    graphics: (result.graphics ?? []).map(normalizeGraphicJob),
    estimate: result.estimate ?? null,
    available: result.available !== false,
    unavailableReason: result.unavailableReason ?? null,
  };
}

/** The `GET …/graphics` URL, which is also the SWR key of `useImageGraphics`. */
export function imageGraphicsUrl(projectId: string, documentId: string): string {
  return `${base(projectId)}/${documentId}/graphics`;
}

/**
 * Starts a design job (answers 201 with the job, which then runs for a few minutes). Spends credits.
 * A refusal throws an `ApiError` whose message is the server's own sentence; {@link graphicErrorOf}
 * reads the structured parts (the shortfall of a 402, the wait of a 429).
 */
export async function createImageGraphic(
  projectId: string,
  documentId: string,
  payload: CreateImageGraphicRequest,
): Promise<ImageGraphicJob> {
  return normalizeGraphicJob(
    await apiFetch<ImageGraphicJob>(imageGraphicsUrl(projectId, documentId), {
      method: 'POST',
      body: JSON.stringify(payload),
    }),
  );
}

/** One design job (reading it settles it too). */
export async function getImageGraphic(
  projectId: string,
  documentId: string,
  graphicId: string,
): Promise<ImageGraphicJob> {
  return normalizeGraphicJob(await apiFetch<ImageGraphicJob>(`${imageGraphicsUrl(projectId, documentId)}/${graphicId}`));
}

/** What a refused `POST …/graphics` said, read off the error. */
export interface GraphicError {
  status: number | null;
  code: string | null;
  /** The server's own sentence, shown verbatim. */
  message: string;
  /** 429: how long to wait before asking again. */
  retryAfterSeconds: number | null;
  /** 402: the credits the run needs and the balance. */
  required: number | null;
  balance: number | null;
  /** True when this installation cannot design graphics at all (503, or 409 `GRAPHIC_UNAVAILABLE`). */
  unavailable: boolean;
}

const numberField = (body: unknown, key: string): number | null => {
  if (typeof body !== 'object' || body === null) return null;
  const value = (body as Record<string, unknown>)[key];
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
};

/** Reads a design refusal; anything that is not an `ApiError` keeps its message and nothing else. */
export function graphicErrorOf(cause: unknown, fallback = 'The graphic could not be started.'): GraphicError {
  const message = readableServerMessage(cause) ?? fallback;
  if (!(cause instanceof ApiError)) {
    return { status: null, code: null, message, retryAfterSeconds: null, required: null, balance: null, unavailable: false };
  }
  const code = errorCodeOf(cause);
  return {
    status: cause.status,
    code,
    message,
    retryAfterSeconds: numberField(cause.body, 'retryAfterSeconds'),
    required: numberField(cause.body, 'required'),
    balance: numberField(cause.body, 'balance'),
    unavailable:
      code === IMAGE_GRAPHIC_ERROR_CODES.agentUnavailable ||
      code === IMAGE_GRAPHIC_ERROR_CODES.unavailable ||
      (cause.status === 503 && code === null),
  };
}
