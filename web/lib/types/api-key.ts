/**
 * The workspace API keys contract, mirroring the Inference API's `OrgApiKeysController` DTOs
 * (`inference/src/ReelBolt.Inference.Api/Controllers/OrgApiKeysController.cs`, camelCase JSON).
 *
 * Two shapes are accepted from `GET /api/v1/org/api-keys` while the server work lands: the older
 * bare array and the newer `{items, capsEnforced}` envelope. `listApiKeys` normalises both; the
 * optional `usage` block on an item is only present once the usage ticket ships.
 */

export type ApiKeyScope =
  | 'projects.read'
  | 'projects.write'
  | 'files.read'
  | 'files.write'
  | 'workflows.read'
  | 'workflows.write'
  | 'runs.read'
  | 'runs.execute'
  | 'outputs.read'
  | 'webhooks.manage'
  /** Lets a run this key starts allow sending media to AI services (`inputs.allowMediaEgress`). Off unless ticked. */
  | 'media.egress';

export const API_KEY_SCOPES: readonly ApiKeyScope[] = [
  'projects.read',
  'projects.write',
  'files.read',
  'files.write',
  'workflows.read',
  'workflows.write',
  'runs.read',
  'runs.execute',
  'outputs.read',
  'webhooks.manage',
  'media.egress',
];

/** `active` | `revoked` | `expired`; `inactive` when the key's service account left the workspace. */
export type ApiKeyStatus = 'active' | 'revoked' | 'expired' | 'inactive';

export interface ApiKeyCaps {
  dailyCredits: number | null;
  weeklyCredits: number | null;
  monthlyCredits: number | null;
}

/** The daily limit on a key: `legacy_uncapped` keys predate the limit and need someone to set one. */
export type DailyCapStatus = 'capped' | 'waived' | 'legacy_uncapped';

export interface ApiKeyDailyCap {
  status: DailyCapStatus;
  defaultCredits: number;
}

/** Caps as sent to the server: `noDailyCap` removes the daily limit (needs `confirmCaps`). */
export interface ApiKeyCapsRequest extends Partial<ApiKeyCaps> {
  noDailyCap?: boolean;
}

/** One capped window's live counter, added by the parallel usage ticket. */
export interface ApiKeyUsageWindow {
  capCredits: number | null;
  usedCredits: number;
  resetsAt: string;
}

export interface ApiKeyUsage {
  daily?: ApiKeyUsageWindow | null;
  weekly?: ApiKeyUsageWindow | null;
  monthly?: ApiKeyUsageWindow | null;
}

export interface ApiKeyCreator {
  id: string;
  name: string;
}

export interface ApiKeyProject {
  id: string;
  name?: string | null;
}

export interface ApiKeyItem {
  id: string;
  name: string;
  /** `rbk_<keyId>`, the visible, non-secret half of the key. */
  keyPrefix: string;
  lastFour: string;
  createdBy: ApiKeyCreator;
  scopes: string[];
  /** null/absent means "all projects". */
  projectIds?: string[] | null;
  projects?: ApiKeyProject[] | null;
  caps: ApiKeyCaps;
  expiresAt: string | null;
  neverExpires: boolean;
  createdAt: string;
  lastUsedAt: string | null;
  status: ApiKeyStatus;
  revokedAt: string | null;
  /** Absent until the usage ticket's server-side changes are deployed. */
  usage?: ApiKeyUsage | null;
  /** Absent on servers that predate the daily limit. */
  dailyCap?: ApiKeyDailyCap | null;
}

/** What the list endpoint normalises to. `capsEnforced: null` means the server did not say. */
export interface ApiKeyListResult {
  items: ApiKeyItem[];
  capsEnforced: boolean | null;
  /** The daily limit new keys get; null when the server did not say. */
  defaultDailyCapCredits: number | null;
}

export interface ApiKeyCreated {
  key: ApiKeyItem;
  /** Shown once; never stored or cached. */
  secret: string;
}

export interface CreateApiKeyRequest {
  name: string;
  scopes: ApiKeyScope[];
  projectIds?: string[];
  expiresAt?: string;
  caps?: ApiKeyCapsRequest;
  /** True once the person confirmed raising the daily limit above the default or removing it. */
  confirmCaps?: boolean;
}

export interface UpdateApiKeyRequest {
  name?: string;
  /** A caps object without a daily key leaves the daily limit as it is. */
  caps?: ApiKeyCapsRequest;
  confirmCaps?: boolean;
}

export interface ApiKeyAuditEntry {
  occurredAt: string;
  method: string;
  route: string;
  statusCode: number;
  durationMs: number;
  requestId: string;
}

export interface ApiKeyAuditPage {
  data: ApiKeyAuditEntry[];
  nextCursor: string | null;
}
