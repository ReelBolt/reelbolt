import { ApiError } from './client';
import type { Format } from '@/i18n/format';
import type { T } from '@/i18n/translator';
import { enT } from '../i18n-default';
import { formatBytes } from '../utils/bytes';
import { readableServerMessage } from './describe-error';

/**
 * The one storage refusal, read the same way everywhere it can appear: the browser upload routes
 * (`POST …/files/upload-url`, the multipart form route), the assistant's attach control, and a
 * project folder link's `lastError` (docs/design/local-folder-sync-and-storage.md Part 2).
 *
 * There are two body shapes on the server and both are read here:
 *
 * - the plan gate's `{ code: "STORAGE_QUOTA_EXCEEDED", feature: "storage", message, limit, current }`
 *   (`ProjectFilesController.Refusal`), which is what a browser upload gets — note `limit`/`current`;
 * - the device/folder-sync `{ error: "STORAGE_QUOTA_EXCEEDED", usedBytes, limitBytes }`, which is what
 *   the desktop app gets and therefore what can end up in a link's `lastError` prose.
 *
 * `ApiError.code` already normalizes `code`/`error`, and `isStorageQuotaError`/`storageQuotaMessage`
 * are the only things a caller needs.
 */

/** The plan gate's storage code, as the Inference API's `PlanGate.StorageCode` spells it. */
export const STORAGE_QUOTA_EXCEEDED = 'STORAGE_QUOTA_EXCEEDED';

/** The storage figures a refusal body carries; either may be null when that body omitted it. */
export interface StorageQuotaInfo {
  usedBytes: number | null;
  limitBytes: number | null;
  /** Set when one file is bigger than the whole plan's storage: its size, and the plan's key ("trial"). */
  incomingBytes?: number | null;
  plan?: string | null;
}

function byteField(body: Record<string, unknown>, ...keys: string[]): number | null {
  for (const key of keys) {
    const value = body[key];
    if (typeof value === 'number' && Number.isFinite(value) && value >= 0) return value;
  }
  return null;
}

/** The used/limit bytes of a storage refusal, or null when the error is not one. */
export function storageQuotaInfo(error: unknown): StorageQuotaInfo | null {
  if (!(error instanceof ApiError) || error.code !== STORAGE_QUOTA_EXCEEDED) return null;
  const body = typeof error.body === 'object' && error.body !== null
    ? (error.body as Record<string, unknown>)
    : {};
  return {
    usedBytes: byteField(body, 'usedBytes', 'current'),
    limitBytes: byteField(body, 'limitBytes', 'limit'),
    // Only a file bigger than the whole plan carries these.
    ...(byteField(body, 'incoming') !== null ? { incomingBytes: byteField(body, 'incoming') } : {}),
    ...(typeof body.plan === 'string' && body.plan ? { plan: body.plan } : {}),
  };
}

/** True when a refusal is the workspace being out of storage, on any route that reports it. */
export function isStorageQuotaError(error: unknown): boolean {
  return error instanceof ApiError && error.code === STORAGE_QUOTA_EXCEEDED;
}

/**
 * The sentence the design document prescribes: "Your workspace is out of storage (X of Y used).
 * Delete files or upgrade your plan." The figures are the server's own; a body without them gets the
 * same sentence without numbers rather than a guess.
 */
export function storageQuotaMessage(info: StorageQuotaInfo | null, t: T = enT, f?: Format): string {
  // One file bigger than the whole plan: deleting files cannot help, so say what can.
  if (info && info.incomingBytes != null && info.limitBytes !== null && info.incomingBytes > info.limitBytes) {
    const plan = info.plan ?? '';
    return t('errors.fileBiggerThanPlan', {
      size: formatBytes(info.incomingBytes, f),
      limit: formatBytes(info.limitBytes, f),
      plan: plan.toLowerCase(),
      planName: plan ? plan.charAt(0).toUpperCase() + plan.slice(1) : '',
    });
  }
  if (!info || info.usedBytes === null || info.limitBytes === null) {
    return t('errors.STORAGE_QUOTA_EXCEEDED');
  }
  return t('errors.storageUsed', { used: formatBytes(info.usedBytes, f), limit: formatBytes(info.limitBytes, f) });
}

/**
 * The plain sentence for any refusal, with a storage refusal mapped to the prescribed one. Used by the
 * upload tray and the assistant's attach control, which must never show a raw backend sentence for a
 * full workspace.
 */
export function uploadErrorMessage(error: unknown, fallback: string, t: T = enT, f?: Format): string {
  if (isStorageQuotaError(error)) return storageQuotaMessage(storageQuotaInfo(error), t, f);
  return readableServerMessage(error) ?? fallback;
}
