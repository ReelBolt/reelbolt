/**
 * "Send my audio to an outside audio service for this project", remembered per user, organisation and
 * project on this device. Local options never ask.
 *
 * The consent is scoped: one click for one project in one organisation says nothing about any other
 * project or organisation. With no resolvable user/org/project nothing is stored or read (there is no
 * shared "anonymous" key), so the dialog simply asks again. The stored record is validated, so a
 * hand-set value of the wrong shape or for another scope does not count.
 *
 * The user is stored as the opaque {@link userScope} (the same hash the journal, queue and clipboard keys
 * use), never the raw email: the key and the record carry no account identity. Consent written by the old
 * raw-email code lives under a different key and is never read; the sign-out wipe still removes it.
 */

import { CLEANUP_CONSENT_PREFIX, userScopeFromEmail } from '../sync/user-scope';

export const CLEANUP_CONSENT_VERSION = 1 as const;

export interface CleanupConsentScope {
  /** The signed-in user (the session email); hashed before it reaches the key or the record. Null when there is no session. */
  user: string | null | undefined;
  /** The active organisation id; null when it cannot be resolved. */
  org: string | null | undefined;
  /** The project the consent covers; null when no project is known. */
  project: string | null | undefined;
}

interface ConsentRecord {
  version: typeof CLEANUP_CONSENT_VERSION;
  user: string;
  org: string;
  project: string;
}

interface ResolvedScope {
  /** The opaque user scope, never the email. */
  user: string;
  org: string;
  project: string;
}

const key = (scope: ResolvedScope): string => `${CLEANUP_CONSENT_PREFIX}v${CLEANUP_CONSENT_VERSION}:${JSON.stringify([scope.user, scope.org, scope.project])}`;

const safeStorage = (): Pick<Storage, 'getItem' | 'setItem'> | null => {
  try {
    return typeof window === 'undefined' ? null : window.localStorage;
  } catch {
    return null;
  }
};

/** Every part must be a non-blank string: a missing user/org/project means "never remember". */
function resolveScope(scope: CleanupConsentScope): ResolvedScope | null {
  const parts = [scope.user, scope.org, scope.project];
  if (parts.some((p) => typeof p !== 'string' || p.trim() === '')) return null;
  return { user: userScopeFromEmail(scope.user), org: scope.org as string, project: scope.project as string };
}

/** Only our own record, for exactly this scope, counts. Anything else is ignored. */
function parseRecord(raw: string | null): ConsentRecord | null {
  if (!raw) return null;
  try {
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return null;
    const record = parsed as Record<string, unknown>;
    if (record.version !== CLEANUP_CONSENT_VERSION) return null;
    if (typeof record.user !== 'string' || typeof record.org !== 'string' || typeof record.project !== 'string') return null;
    return { version: CLEANUP_CONSENT_VERSION, user: record.user, org: record.org, project: record.project };
  } catch {
    return null;
  }
}

export function hasCleanupConsent(scope: CleanupConsentScope, storage: Pick<Storage, 'getItem'> | null = safeStorage()): boolean {
  const resolved = resolveScope(scope);
  if (!resolved) return false;
  try {
    const record = parseRecord(storage?.getItem(key(resolved)) ?? null);
    return !!record && record.user === resolved.user && record.org === resolved.org && record.project === resolved.project;
  } catch {
    return false;
  }
}

export function rememberCleanupConsent(scope: CleanupConsentScope, storage: Pick<Storage, 'setItem'> | null = safeStorage()): void {
  const resolved = resolveScope(scope);
  if (!resolved) return;
  try {
    const record: ConsentRecord = { version: CLEANUP_CONSENT_VERSION, ...resolved };
    storage?.setItem(key(resolved), JSON.stringify(record));
  } catch {
    /* private mode: the dialog simply asks again next time */
  }
}
