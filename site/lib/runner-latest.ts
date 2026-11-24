// Strict parsing of the runner's published latest.json (schema: runner/cmd/reelbolt-manifest
// latest.go). Pure and import-free so both `scripts/fetch-runner-latest.mjs` (Node type stripping)
// and the unit tests can use it. Anything that does not validate yields `null`, and the caller
// treats `null` as "nothing to download": the page then fails closed.

export type RunnerPlatform = 'windows-x64' | 'linux-x64' | 'linux-arm64';

export interface RunnerArtifact {
  platform: RunnerPlatform;
  label: string;
  url: string;
  sha256: string;
  /** Bytes. */
  size: number;
  recommended: boolean;
}

export interface RunnerRelease {
  version: string;
  /** ISO timestamp from latest.json, or '' when the document carries none. */
  publishedAt: string;
  artifacts: RunnerArtifact[];
}

export const RUNNER_DOWNLOAD_HOST = 'downloads.reelbolt.ai';
export const RUNNER_LATEST_URL = `https://${RUNNER_DOWNLOAD_HOST}/runner/stable/latest.json`;

const PLATFORMS: readonly string[] = ['windows-x64', 'linux-x64', 'linux-arm64'];

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function nonEmpty(v: unknown): v is string {
  return typeof v === 'string' && v.trim().length > 0;
}

const FILE_NAME = /^[A-Za-z0-9._+-]+$/;

/** The last path segment, only if it is a plain file name (no decoding, no shell metacharacters). */
function fileNameOf(u: URL): string | null {
  const last = u.pathname.split('/').pop() ?? '';
  return FILE_NAME.test(last) && last !== '.' && last !== '..' ? last : null;
}

/** The file name of an already-validated download URL. */
export function runnerFileName(url: string): string {
  return fileNameOf(new URL(url)) ?? 'download';
}

/** True for an https URL on the production download host. */
export function isDownloadUrl(value: string): boolean {
  try {
    const u = new URL(value);
    return u.protocol === 'https:' && u.hostname === RUNNER_DOWNLOAD_HOST && !u.username && !u.password && u.port === '' && fileNameOf(u) !== null;
  } catch {
    return false;
  }
}

/** The override is only honoured for localhost (test fixtures) or the production URL. */
export function resolveLatestUrl(override: string | undefined): string {
  if (!override) return RUNNER_LATEST_URL;
  try {
    const u = new URL(override);
    if (u.protocol === 'http:' && (u.hostname === 'localhost' || u.hostname === '127.0.0.1')) return override;
    if (u.protocol === 'https:' && u.hostname === RUNNER_DOWNLOAD_HOST) return override;
  } catch {
    // fall through
  }
  throw new Error(`RUNNER_LATEST_URL must be http://localhost or https://${RUNNER_DOWNLOAD_HOST}: ${override}`);
}

/**
 * Validate a latest.json document. Installers only. Returns null if the document is malformed in
 * any way that matters, or contains no installer: a half-trusted list is worse than none.
 */
export function parseRunnerLatest(doc: unknown): RunnerRelease | null {
  if (!isRecord(doc) || !nonEmpty(doc.version) || !Array.isArray(doc.artifacts)) return null;
  let publishedAt = '';
  if (doc.publishedAt !== undefined && doc.publishedAt !== '') {
    if (typeof doc.publishedAt !== 'string' || Number.isNaN(Date.parse(doc.publishedAt))) return null;
    publishedAt = doc.publishedAt;
  }
  const artifacts: RunnerArtifact[] = [];
  for (const a of doc.artifacts) {
    if (!isRecord(a)) return null;
    if (a.kind !== 'installer') continue;
    if (
      typeof a.platform !== 'string' ||
      !PLATFORMS.includes(a.platform) ||
      !nonEmpty(a.label) ||
      typeof a.url !== 'string' ||
      !isDownloadUrl(a.url) ||
      typeof a.sha256 !== 'string' ||
      !/^[0-9a-f]{64}$/.test(a.sha256) ||
      typeof a.size !== 'number' ||
      !Number.isInteger(a.size) ||
      a.size <= 0
    ) {
      return null;
    }
    artifacts.push({
      platform: a.platform as RunnerPlatform,
      label: a.label.trim(),
      url: a.url,
      sha256: a.sha256,
      size: a.size,
      recommended: a.recommended === true,
    });
  }
  if (artifacts.length === 0) return null;
  return { version: doc.version.trim(), publishedAt, artifacts };
}
