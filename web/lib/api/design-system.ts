import { handleUnauthorized, unauthorizedMessage } from '../auth/session-expiry';
import { ApiError, apiFetch, extractErrorList, extractErrorMessage } from './client';
import type {
  BuildRequest,
  BuildResponse,
  DesignDiff,
  DesignDocument,
  DesignLibrarySummary,
  DesignProposal,
  DesignProposalStatus,
  DesignSource,
  DesignSystemsSummary,
  DesignSystemKind,
  DesignVersion,
  DesignVersionSummary,
  EditorDefaults,
  FigmaImportResponse,
  ImportCapabilitiesResponse,
  ImportResponse,
  IngestResponse,
  PdfImportResponse,
  ZipImportResponse,
} from '../types/design-system';

/**
 * SPEC §17 `GET /api/v1/design-system/importers`: the static capability matrix — which importer
 * reads which part of a profile. It carries no project context, so it hangs off the API root rather
 * than the project base, and the same table serves every workspace. The caller shows a short
 * "unavailable" line when this fails; it never blocks an import.
 */
export function getImportCapabilities(): Promise<ImportCapabilitiesResponse> {
  return apiFetch<ImportCapabilitiesResponse>('/api/v1/design-system/importers');
}

/** SPEC §9: every read hangs off one project-scoped base. No mutation lives here. */
function designSystemBase(projectId: string): string {
  return `/api/v1/projects/${projectId}/design-system`;
}

/** Both kinds' summaries: the design system and the product map, either of which may be absent. */
export function getDesignSystemsSummary(projectId: string): Promise<DesignSystemsSummary> {
  return apiFetch<DesignSystemsSummary>(designSystemBase(projectId));
}

/** Versions of one kind, newest first (the API caps the list at 100 and omits the document). */
export function listDesignVersions(
  projectId: string,
  kind: DesignSystemKind = 'design',
): Promise<DesignVersionSummary[]> {
  return apiFetch<DesignVersionSummary[]>(`${designSystemBase(projectId)}/versions?kind=${kind}`);
}

/** One full version: document, coverage and the sources it was read from. */
export function getDesignVersion(projectId: string, versionId: string): Promise<DesignVersion> {
  return apiFetch<DesignVersion>(`${designSystemBase(projectId)}/versions/${versionId}`);
}

/** The sources recorded for one kind. */
export function listDesignSources(
  projectId: string,
  kind: DesignSystemKind = 'design',
): Promise<DesignSource[]> {
  return apiFetch<DesignSource[]>(`${designSystemBase(projectId)}/sources?kind=${kind}`);
}

/**
 * SPEC §16.3: the current approved design system's text-layer defaults mapped onto the editor's own
 * fonts. `null` when the project has no usable design member — the server answers `204` and the
 * editor keeps exactly its own defaults. Fetched once per project (`useEditorDefaults`).
 */
export async function getEditorDefaults(projectId: string): Promise<EditorDefaults | null> {
  return (await apiFetch<EditorDefaults | null>(`${designSystemBase(projectId)}/editor-defaults`)) ?? null;
}

// --- Mutations (P2-22) ------------------------------------------------------------------------

/**
 * Start the build workflow (SPEC §9). `templateKey` defaults to `product-profile-from-code` on the
 * server; a resume must resend the SAME body plus `acceptEstimateCredits` (SPEC §9.4).
 */
export function buildProfile(projectId: string, body: BuildRequest): Promise<BuildResponse> {
  return apiFetch<BuildResponse>(`${designSystemBase(projectId)}/builds`, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

/**
 * Import chosen project files as design/product drafts (SPEC §9). `kinds` is always sent so the
 * server never has to guess; the component sends the checked set, `['design']` by default. The
 * answer carries the Draft versions it created plus every importer diagnostic.
 */
export function importDesign(
  projectId: string,
  projectFileIds: string[],
  kinds: DesignSystemKind[],
): Promise<ImportResponse> {
  return apiFetch<ImportResponse>(`${designSystemBase(projectId)}/import`, {
    method: 'POST',
    body: JSON.stringify({ projectFileIds, kinds }),
  });
}

/**
 * SPEC §17/P3-14 `POST …/import/figma`: reads a Figma file's variables with a one-shot Personal
 * Access Token (`type=password` in the form, cleared after submit). The token is never stored
 * server-side nor echoed back; a `404 feature_disabled` means the workspace has the import off.
 */
export function importFigmaVariables(
  projectId: string,
  fileKey: string,
  accessToken: string,
): Promise<FigmaImportResponse> {
  return apiFetch<FigmaImportResponse>(`${designSystemBase(projectId)}/import/figma`, {
    method: 'POST',
    body: JSON.stringify({ fileKey, accessToken }),
  });
}

/**
 * SPEC §17/P3-14 `POST …/import/pdf`: a multipart Brand PDF (at most 20 MiB). `apiFetch` always
 * sets a JSON content type, which would strip the multipart boundary, so this one call posts the
 * FormData directly and decodes refusals exactly like `apiFetch` (415 not a PDF, 413 too large,
 * 503 worker unavailable, 422 unreadable).
 */
export async function importBrandPdf(projectId: string, file: File): Promise<PdfImportResponse> {
  const form = new FormData();
  form.append('file', file);

  const res = await fetch(`${designSystemBase(projectId)}/import/pdf`, {
    method: 'POST',
    body: form,
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

  return res.json() as Promise<PdfImportResponse>;
}

/**
 * SPEC §16.4/P4-17 `POST …/import-zip`: a multipart design export — a `.zip`, or a `.sketch` /
 * `.penpot` archive (the route reads the zip magic, not the extension). `apiFetch` would strip the
 * multipart boundary, so this call posts the FormData directly and decodes refusals exactly like
 * `apiFetch` (415 not an archive, 413 over 32 MiB, 503 worker unavailable, 400 `IMPORT_REJECTED`
 * for a hostile archive and 400 `IMPORT_NOTHING_RECOGNIZED` when no entry carried colors or fonts).
 */
export async function importZip(projectId: string, file: File): Promise<ZipImportResponse> {
  const form = new FormData();
  form.append('file', file);

  const res = await fetch(`${designSystemBase(projectId)}/import-zip`, {
    method: 'POST',
    body: form,
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

  return res.json() as Promise<ZipImportResponse>;
}

/**
 * SPEC §9 `POST /ingest`: reads a finished build's Compose artifact and creates the Draft versions
 * (idempotent per execution). The "Save as drafts" action for a build whose automatic save was
 * refused or did not run.
 */
export function ingestDesignSystem(
  projectId: string,
  executionId: string,
  stepOrder: number,
): Promise<IngestResponse> {
  return apiFetch<IngestResponse>(`${designSystemBase(projectId)}/ingest`, {
    method: 'POST',
    body: JSON.stringify({ executionId, stepOrder }),
  });
}

/** Approve the draft (SPEC §9); a non-draft or stale base answers 409. */
export function approveVersion(projectId: string, versionId: string): Promise<unknown> {
  return apiFetch(`${designSystemBase(projectId)}/versions/${versionId}/approve`, { method: 'POST' });
}

/** Reject the draft (SPEC §9), optionally with a note. */
export function rejectVersion(projectId: string, versionId: string, note?: string): Promise<unknown> {
  return apiFetch(`${designSystemBase(projectId)}/versions/${versionId}/reject`, {
    method: 'POST',
    body: JSON.stringify(note === undefined ? {} : { note }),
  });
}

/** Restore an old version as a new draft (SPEC §9). */
export function restoreVersion(projectId: string, versionId: string): Promise<unknown> {
  return apiFetch(`${designSystemBase(projectId)}/versions/${versionId}/restore`, { method: 'POST' });
}

// --- Proposals, locks, checks and the two-version diff (P3-04) --------------------------------

/**
 * SPEC §9 `GET /proposals`: the current system's proposals (newest first), optionally narrowed to
 * `kind` (defaults to `design`) and `status`.
 */
export function listDesignProposals(
  projectId: string,
  kind: DesignSystemKind = 'design',
  status?: DesignProposalStatus,
): Promise<DesignProposal[]> {
  const query = new URLSearchParams({ kind });
  if (status) query.set('status', status);
  return apiFetch<DesignProposal[]>(`${designSystemBase(projectId)}/proposals?${query.toString()}`);
}

/** SPEC §9/§13.4 `POST /proposals/{id}/approve`; a decided proposal or a moved base is a 409. */
export function approveProposal(
  projectId: string,
  proposalId: string,
  note?: string,
): Promise<DesignProposal> {
  return apiFetch<DesignProposal>(`${designSystemBase(projectId)}/proposals/${proposalId}/approve`, {
    method: 'POST',
    body: JSON.stringify(note === undefined ? {} : { note }),
  });
}

/** SPEC §9/§13.4 `POST /proposals/{id}/reject`, optionally with a note. */
export function rejectProposal(
  projectId: string,
  proposalId: string,
  note?: string,
): Promise<DesignProposal> {
  return apiFetch<DesignProposal>(`${designSystemBase(projectId)}/proposals/${proposalId}/reject`, {
    method: 'POST',
    body: JSON.stringify(note === undefined ? {} : { note }),
  });
}

/** SPEC §9 `PATCH /versions/{id}/locks`: lock/unlock token paths on a Draft (409 otherwise). */
export function lockVersion(
  projectId: string,
  versionId: string,
  lock: string[],
  unlock: string[],
): Promise<DesignVersion> {
  return apiFetch<DesignVersion>(`${designSystemBase(projectId)}/versions/${versionId}/locks`, {
    method: 'PATCH',
    body: JSON.stringify({ lock, unlock }),
  });
}

/** SPEC §9 `POST /check`: enqueue a change check now (202; the check never runs in the request). */
export function checkForDrift(projectId: string): Promise<unknown> {
  return apiFetch(`${designSystemBase(projectId)}/check`, { method: 'POST' });
}

/**
 * SPEC §9 `GET /versions/{id}/diff?against={otherId}`: the two-version diff, `against` the older
 * side and `versionId` the newer, so added/removed/changed read oldest → newest.
 */
export function getVersionDiff(
  projectId: string,
  versionId: string,
  againstId: string,
): Promise<DesignDiff> {
  return apiFetch<DesignDiff>(
    `${designSystemBase(projectId)}/versions/${versionId}/diff?against=${againstId}`,
  );
}

// --- Organization brand libraries (P4-06; SPEC §16.1) ------------------------------------------

/**
 * The org libraries base. Singular `org` + plural `design-libraries`, the one shape nginx never
 * sends to the Go API's `/api/v1/orgs/…`. Every action is scoped to the caller's active org.
 */
function orgLibrariesBase(): string {
  return '/api/v1/org/design-libraries';
}

/** `GET /api/v1/org/design-libraries`: every library of the active organization, by name. */
export function listDesignLibraries(): Promise<DesignLibrarySummary[]> {
  return apiFetch<DesignLibrarySummary[]>(orgLibrariesBase());
}

/**
 * `POST …`: create a library, optionally with its first Draft document. An invalid document is a
 * `400` and writes nothing at all; `document` is only sent when the caller supplied one.
 */
export function createDesignLibrary(
  name: string,
  document?: DesignDocument,
): Promise<DesignLibrarySummary> {
  return apiFetch<DesignLibrarySummary>(orgLibrariesBase(), {
    method: 'POST',
    body: JSON.stringify(document === undefined ? { name } : { name, document }),
  });
}

/** `PATCH …/{libraryId}`: rename a library. The container's name follows server-side. */
export function renameDesignLibrary(libraryId: string, name: string): Promise<DesignLibrarySummary> {
  return apiFetch<DesignLibrarySummary>(`${orgLibrariesBase()}/${libraryId}`, {
    method: 'PATCH',
    body: JSON.stringify({ name }),
  });
}

/** `DELETE …/{libraryId}?confirm=true`: delete a library and its versions after an explicit confirm. */
export function deleteDesignLibrary(libraryId: string): Promise<void> {
  return apiFetch<void>(`${orgLibrariesBase()}/${libraryId}?confirm=true`, { method: 'DELETE' });
}

/** `GET …/{libraryId}/versions`: the library's versions, newest first. */
export function listDesignLibraryVersions(libraryId: string): Promise<DesignVersionSummary[]> {
  return apiFetch<DesignVersionSummary[]>(`${orgLibrariesBase()}/${libraryId}/versions`);
}

/**
 * `POST …/{libraryId}/versions/{versionId}/approve`: approve a Draft. Owner/Admin only; the server
 * refuses a Member (403) and a Billing caller (403 on every library action).
 */
export function approveDesignLibraryVersion(libraryId: string, versionId: string): Promise<unknown> {
  return apiFetch(`${orgLibrariesBase()}/${libraryId}/versions/${versionId}/approve`, {
    method: 'POST',
  });
}

/**
 * SPEC §16.1 `PUT /api/v1/projects/{projectId}/design-system/library-link`: set, or clear with a
 * null `libraryId`, the organization library the project's design system inherits from.
 */
export function linkProjectLibrary(projectId: string, libraryId: string | null): Promise<void> {
  return apiFetch<void>(`/api/v1/projects/${projectId}/design-system/library-link`, {
    method: 'PUT',
    body: JSON.stringify({ libraryId }),
  });
}

// --- Export (P4-08; SPEC §16.2) ----------------------------------------------------------------

/** The design formats the export route serves, in menu order; the product map only accepts `json`. */
export const DESIGN_EXPORT_FORMATS = ['dtcg', 'css', 'css-tailwind', 'designmd', 'json'] as const;

export type DesignExportFormat = (typeof DESIGN_EXPORT_FORMATS)[number];

/** The menu labels for the format codes the route expects. */
export const DESIGN_EXPORT_FORMAT_LABELS: Record<DesignExportFormat, string> = {
  dtcg: 'DTCG JSON',
  css: 'CSS variables',
  'css-tailwind': 'Tailwind v4 theme',
  designmd: 'DESIGN.md',
  json: 'JSON',
};

/**
 * SPEC §16.2 `GET …/design-system/export?kind=&format=`: the project's effective current document
 * as a Blob (the server's `Content-Disposition` names the download). A refusal decodes exactly like
 * `apiFetch`, so the caller can surface the API's own message.
 */
export async function exportDesignDocument(
  projectId: string,
  kind: DesignSystemKind,
  format: DesignExportFormat,
): Promise<Blob> {
  const query = new URLSearchParams({ kind, format });
  const res = await fetch(`${designSystemBase(projectId)}/export?${query.toString()}`);

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

  return res.blob();
}

/** The file name the server's `Content-Disposition` uses, mirrored for the client-side anchor. */
export function designExportFileName(kind: DesignSystemKind, format: DesignExportFormat): string {
  if (kind === 'product_map')
    return 'product-map.json';

  switch (format) {
    case 'dtcg':
      return 'design-system.dtcg.json';
    case 'css':
      return 'design-system.css';
    case 'css-tailwind':
      return 'design-system.tailwind.css';
    case 'designmd':
      return 'DESIGN.md';
    default:
      return 'design-system.json';
  }
}

/** Saves a blob through a transient anchor; the only DOM this module touches. */
export function downloadBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  a.rel = 'noopener';
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
