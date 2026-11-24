import { apiFetch } from './client';
import type { StorageUsage } from '../types/storage';

/**
 * Workspace storage (docs/design/local-folder-sync-and-storage.md Part 2). `GET /api/v1/storage/usage`
 * is readable by any member — unlike the billing summary it is not a billing document, and the person
 * who hits the quota is the one who needs to see why. It works with billing off too, where
 * `limitBytes` is null.
 */

export const STORAGE_USAGE_PATH = '/api/v1/storage/usage';

/** `GET /api/v1/storage/usage` — bytes used, the plan's limit (null = unlimited), top 20 projects. */
export function getStorageUsage(): Promise<StorageUsage> {
  return apiFetch<StorageUsage>(STORAGE_USAGE_PATH);
}
