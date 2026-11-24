import { apiFetch, ApiError } from './client';
import { readableServerMessage } from './describe-error';

/**
 * CSAM scan administration (csam-hash-matching). The list is the audit trail of scans for project
 * files, and the re-scan action runs the matcher again for one file.
 *
 * Privacy rule: nothing here carries file content, a storage key or a playable URL. A row is a
 * name, a status and the provider's classification, and that is all the admin surface may see.
 */

export const ADMIN_CSAM_SCANS_PATH = '/api/v1/csam/admin/scans';

/** The status filter the API accepts; `all` is the default view. */
export type CsamStatusFilter = 'all' | 'match' | 'not-scanned' | 'disabled' | 'clean';

/**
 * One scan row. `status` is the audit outcome (`match`, `not-scanned`, `clean`), while the
 * project file's own state is the enum (`Quarantined`, `Clean`, `NotScanned`, `Pending`) — both are
 * treated case-insensitively by the page because the API's serializer may PascalCase them.
 */
export interface CsamScanItem {
  id: string;
  /** Null after the file or project was deleted; the audit row and its name snapshot remain. */
  projectFileId: string | null;
  projectId: string | null;
  fileName: string | null;
  status: string;
  classification: string | null;
  matchType: string | null;
  nearMatchCount: number;
  quarantined: boolean;
  quarantineSucceeded: boolean;
  errorCode: string | null;
  providerVersion: string | null;
  createdAt: string;
}

export interface CsamScanListResponse {
  /** False when the deployment has the control switched off; the list is empty in that case. */
  enabled: boolean;
  items: CsamScanItem[];
  nextBefore: string | null;
}

/** The outcome of a re-scan: the new project-file status plus the matcher's own details. */
export interface CsamRescanResult {
  status: string;
  classification?: string | null;
  matchType?: string | null;
  quarantined: boolean;
  errorCode?: string | null;
}

export function csamRescanPath(fileId: string): string {
  return `/api/v1/csam/admin/files/${encodeURIComponent(fileId)}/rescan`;
}

/** The scan list key. `all` is the API default, so the parameter is omitted for it. */
export function adminScansPath(
  status: CsamStatusFilter = 'all',
  limit = 100,
  before?: string | null,
): string {
  const params = new URLSearchParams();
  if (status !== 'all') params.set('status', status);
  params.set('limit', String(limit));
  if (before) params.set('before', before);
  return `${ADMIN_CSAM_SCANS_PATH}?${params.toString()}`;
}

/** One page of scan rows. A missing `items` array reads as empty rather than throwing. */
export async function listAdminScans(params: {
  status?: CsamStatusFilter;
  limit?: number;
  before?: string | null;
} = {}): Promise<CsamScanListResponse> {
  const res = await apiFetch<CsamScanListResponse>(
    adminScansPath(params.status ?? 'all', params.limit ?? 100, params.before),
  );
  return {
    enabled: res.enabled !== false,
    items: Array.isArray(res.items) ? res.items : [],
    nextBefore: res.nextBefore ?? null,
  };
}

/** Run the matcher again for one file. Never un-quarantines by itself. */
export function rescanCsamFile(fileId: string): Promise<CsamRescanResult> {
  return apiFetch<CsamRescanResult>(csamRescanPath(fileId), { method: 'POST' });
}

/** Enum strings without case: `"NotScanned"` and `"not-scanned"` are one status. */
export function sameCsamStatus(value: string | null | undefined, expected: string): boolean {
  if (typeof value !== 'string') return false;
  const normalize = (s: string) => s.toLowerCase().replace(/[-_\s]/g, '');
  return normalize(value) === normalize(expected);
}

/** The admin-facing sentence for a failed scan-list load or re-scan. */
export function csamAdminMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 403) return 'Only platform administrators can do this.';
    if (error.status === 404) return 'CSAM scanning is not available on this deployment.';
    if (error.errors.length > 0) return error.errors.join(' ');
    const own = readableServerMessage(error);
    if (own) return own;
  }
  const own = readableServerMessage(error);
  if (own) return own;
  return 'Something went wrong. Nothing was changed.';
}

/** One factual sentence describing what a re-scan produced. No provider detail beyond the outcome. */
export function csamRescanMessage(result: CsamRescanResult): string {
  if (result.quarantined) {
    return 'The file matched and is quarantined. Follow the review and reporting steps in docs/csam.md.';
  }
  if (sameCsamStatus(result.errorCode ?? '', 'disabled')) {
    return 'The CSAM control is disabled on this deployment; nothing was scanned.';
  }
  if (sameCsamStatus(result.status, 'not-scanned')) {
    const suffix = result.errorCode ? ` Error code: ${result.errorCode}.` : '';
    return `The file was not scanned and stays available.${suffix}`;
  }
  if (sameCsamStatus(result.status, 'clean')) return 'The file was scanned and no known match was found.';
  return `Scan finished with status ${result.status}.`;
}
