import type { Format } from '@/i18n/format';
import type { T } from '@/i18n/translator';
import { enT } from '@/lib/i18n-default';
import { formatBytes } from '../utils/bytes';

/**
 * Storage-usage types. These mirror the response records on
 * `ReelBolt.Inference.Api.Controllers.StorageUsageController` and the `storage` object of
 * `BillingController`'s summary exactly — ASP.NET Core serializes records with the default camelCase
 * policy, so the field names below are the wire names.
 *
 * Part 2 of docs/design/local-folder-sync-and-storage.md: the limit comes from the plan catalog
 * (Trial 1 GB, Creator 15 GB, Pro 50 GB, Team 100 GB, Enterprise unlimited) and `limitBytes` is null
 * when the plan has no storage limit.
 */

/** One project's share of the workspace's storage, largest first in `byProject`. */
export interface ProjectStorageUsage {
  projectId: string;
  name: string;
  usedBytes: number;
}

/** `GET /api/v1/storage/usage` — the workspace's storage, its limit, and the largest projects. */
export interface StorageUsage {
  usedBytes: number;
  /** Null = unlimited (a plan with no storage limit, or a deployment with billing off). */
  limitBytes: number | null;
  /** At most 20 projects, largest first. */
  byProject: ProjectStorageUsage[];
  /** The biggest video the platform can edit (whole GB and minutes); absent from an older server. */
  videoSourceLimits?: { maxGb: number; maxMinutes: number } | null;
  /** The biggest zip ReelBolt unpacks, in whole MB (the size of the zip as uploaded); absent from an older server. */
  zipUnpackLimits?: { maxMb: number } | null;
}

/** The `storage` object on `GET /api/v1/billing/summary` — the same figures, without the projects. */
export interface BillingStorage {
  usedBytes: number;
  /** Null = unlimited. */
  limitBytes: number | null;
}

/** The fraction of the limit used, 0 when unlimited; clamped to [0, 1]. */
export function storageFraction(usage: Pick<StorageUsage, 'usedBytes' | 'limitBytes'>): number {
  if (!usage.limitBytes || usage.limitBytes <= 0) return 0;
  return Math.min(1, Math.max(0, usage.usedBytes / usage.limitBytes));
}

/** True at or above the warning threshold the design document sets for project pages (90 %). */
export function isStorageNearlyFull(usage: Pick<StorageUsage, 'usedBytes' | 'limitBytes'> | null | undefined): boolean {
  if (!usage || usage.limitBytes === null || usage.limitBytes <= 0) return false;
  return usage.usedBytes / usage.limitBytes >= 0.9;
}

/**
 * "4.2 GB of 15 GB used", or "4.2 GB used · Unlimited" when the plan has no limit. The one sentence
 * the Billing page, the Settings page and every quota error share.
 */
export function storageUsageSentence(usage: Pick<StorageUsage, 'usedBytes' | 'limitBytes'>, t: T = enT, f?: Format): string {
  const used = formatBytes(usage.usedBytes, f);
  if (usage.limitBytes === null) return t('libLabels.storage.usageUnlimited', { used });
  return t('libLabels.storage.usageOf', { used, limit: formatBytes(usage.limitBytes, f) });
}
