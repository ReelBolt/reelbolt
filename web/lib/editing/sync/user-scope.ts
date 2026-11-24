/**
 * Whose edits are on this device. The journal and the offline queue hold full unsaved timeline documents,
 * so they are per user: every entry carries `userId` (the opaque {@link userScope}, never the email),
 * another user's entries are never offered or sent, and an explicit logout wipes them all.
 *
 * The email never appears in a storage key. {@link userScope} hashes the raw id (SHA-256, truncated), so
 * a shared device's key names reveal nothing about who was signed in. Keys written before this change
 * carry the raw id; {@link migrateEditorStorageScope} moves them over once, per page.
 */

import { sha256Hex } from './scope-hash';

/** Prefixes of the per-user keys. They live here so every surface that builds or wipes a key agrees. */
export const JOURNAL_PREFIX = 'reelbolt:editor:journal:';
export const QUEUE_PREFIX = 'reelbolt:editor:offline-queue:';
/** Background results (prompts, briefs, AI answers): per user and timeline, wiped on sign-out. Keys: `reelbolt.editor.bg.<user>.<timeline>`. */
export const BACKGROUND_RESULTS_PREFIX = 'reelbolt.editor.bg.';
export const PREFS_HISTORY_PREFIX = 'reelbolt:editor:prefs-history';
export const CLIPBOARD_PREFIX = 'reelbolt:editor:clipboard:';
/** Picture-editor results kept hidden and waiting for the user's OK: `<prefix><userScope>.<documentId>`. */
export const PICREVIEW_PREFIX = 'reelbolt.editor.picreview.';
/** Remembered audio-cleanup AI consent (`<prefix>v<N>:[<scope>,<org>,<project>]`). Wiped on sign-out. */
export const CLEANUP_CONSENT_PREFIX = 'rb.audioCleanup.consent.';
export const DISPLAY_PREFIX = 'reelbolt:editor:display';
/** Follow subject: the id of a clip's running subject search, in sessionStorage, per user. */
export const FOLLOW_RUN_PREFIX = 'reelbolt:editor:follow-run:';
export const LEGACY_CLIPBOARD_KEY = 'reelforge-editor-clip-clipboard';
const DB_NAME = 'reelbolt-editor';

/** The channel / storage key a sign-out is announced on, so every tab of the browser hears it. */
export const SIGN_OUT_CHANNEL = 'reelbolt:editor:signout';

/** Durable cross-tab record of the last deliberate sign-out (ms since epoch). Holds no user data; never wiped. */
export const SIGN_OUT_AT_KEY = 'reelbolt:editor:signed-out-at';

/** When the browser last signed out deliberately (0 = never / unreadable). A 401 / expiry never sets it. */
export function lastEditorSignOutAt(): number {
  try {
    const raw = typeof window === 'undefined' ? null : window.localStorage.getItem(SIGN_OUT_AT_KEY);
    const n = raw === null ? 0 : Number(raw);
    return Number.isFinite(n) ? n : 0;
  } catch {
    return 0;
  }
}

/** Tells the other tabs of this browser that the user signed out (BroadcastChannel, with a storage event as the fallback). */
export function announceEditorSignOut(): void {
  try {
    if (typeof window !== 'undefined' && window.localStorage) window.localStorage.setItem(SIGN_OUT_AT_KEY, String(Date.now()));
  } catch {
    // blocked storage: the broadcast below still reaches live tabs
  }
  try {
    if (typeof BroadcastChannel !== 'undefined') {
      const channel = new BroadcastChannel(SIGN_OUT_CHANNEL);
      channel.postMessage({ t: 'signout' });
      channel.close();
    }
  } catch {
    // no BroadcastChannel: the storage event below still reaches the other tabs
  }
  try {
    if (typeof window !== 'undefined' && window.localStorage) {
      // The set fires a `storage` event in the other tabs; the key is removed again so nothing lingers on the device.
      window.localStorage.setItem(SIGN_OUT_CHANNEL, String(Date.now()));
      window.localStorage.removeItem(SIGN_OUT_CHANNEL);
    }
  } catch {
    // blocked storage: nothing to journal there either
  }
}

/** Calls `handler` when ANOTHER tab of this browser signs out. Returns the unsubscribe. */
export function onEditorSignOut(handler: () => void): () => void {
  let channel: BroadcastChannel | null = null;
  try {
    if (typeof BroadcastChannel !== 'undefined') {
      channel = new BroadcastChannel(SIGN_OUT_CHANNEL);
      channel.addEventListener('message', () => handler());
    }
  } catch {
    channel = null;
  }
  const onStorage = (event: StorageEvent) => {
    if (event.key === SIGN_OUT_CHANNEL) handler();
  };
  if (typeof window !== 'undefined') window.addEventListener('storage', onStorage);
  return () => {
    channel?.close();
    if (typeof window !== 'undefined') window.removeEventListener('storage', onStorage);
  };
}

/** The raw id a signed-out browser used to be tagged with. It is now only a migration source (see below). */
export const ANONYMOUS_USER = 'anonymous';

/**
 * The LEGACY raw id: lower-cased trimmed email, else {@link ANONYMOUS_USER}. Used only by
 * {@link migrateEditorStorageScope} to find keys written before the scope hash; no key is built from it.
 */
export const userIdFromEmail = (email: string | null | undefined): string => (email ? email.trim().toLowerCase() : ANONYMOUS_USER);

/** The opaque per-user storage scope: SHA-256 of the raw id, truncated. No email (or its parts) survives. */
export function userScope(rawUserId: string): string {
  return sha256Hex(rawUserId).slice(0, 16);
}

/** The scope for the signed-in email (or the anonymous scope when there is no readable account). */
export function userScopeFromEmail(email: string | null | undefined): string {
  return userScope(userIdFromEmail(email));
}

/** The scope of a signed-out session. Hashed like every other id, so every key has the same shape. */
export const ANONYMOUS_SCOPE = userScope(ANONYMOUS_USER);

const migratedLegacyIds = new Set<string>();

type JsonRecord = Record<string, unknown>;

function parseRecord(raw: string): JsonRecord | null {
  try {
    const value: unknown = JSON.parse(raw);
    return value !== null && typeof value === 'object' && !Array.isArray(value) ? (value as JsonRecord) : null;
  } catch {
    return null;
  }
}

/** Rewrites the `userId` of a journal / queue record (including a `{kind:'entry', entry}` wrapper). */
function withUserId(raw: string, scope: string): string {
  const record = parseRecord(raw);
  if (!record) return raw;
  const entry = record.kind === 'entry' ? record.entry : null;
  if (entry !== null && typeof entry === 'object') (entry as JsonRecord).userId = scope;
  else if (record.kind !== 'pointer') record.userId = scope;
  return JSON.stringify(record);
}

/** Rewrites the `owner` stamp of a clipboard record. */
function withOwner(raw: string, scope: string): string {
  const record = parseRecord(raw);
  if (!record) return raw;
  record.owner = scope;
  return JSON.stringify(record);
}

/** Moves one key, rewriting its value; when the scoped key already exists the new one wins and the old is dropped. */
function moveKey(storage: Storage, from: string, to: string, rewrite?: (raw: string) => string): void {
  const raw = storage.getItem(from);
  if (raw === null) return;
  if (storage.getItem(to) !== null) {
    storage.removeItem(from);
    return;
  }
  storage.setItem(to, rewrite ? rewrite(raw) : raw);
  storage.removeItem(from);
}

/**
 * Moves keys written before the scope hash (`reelbolt:editor:display:<email>`, `...prefs-history:<email>`,
 * `...clipboard:<email>`, `...journal:<email>:<timelineId>`, `...offline-queue:<email>:<timelineId>`) to
 * their scoped form, rewriting the `userId` / `owner` inside the value.
 *
 * Idempotent and memoised per legacy id: the second call in the same page does nothing. Never throws
 * (blocked storage is a no-op), and a first run with nothing to migrate performs no writes. When both
 * forms exist the scoped key is kept (the current version already wrote it) and the legacy key is dropped.
 */
export function migrateEditorStorageScope(email: string | null | undefined): void {
  if (typeof window === 'undefined' || !window.localStorage) return;
  const legacy = userIdFromEmail(email);
  if (migratedLegacyIds.has(legacy)) return;
  migratedLegacyIds.add(legacy);
  const scope = userScope(legacy);
  try {
    const storage = window.localStorage;
    const keys: string[] = [];
    for (let i = 0; i < storage.length; i++) {
      const key = storage.key(i);
      if (key !== null) keys.push(key);
    }
    for (const key of keys) {
      try {
        if (key === `${DISPLAY_PREFIX}:${legacy}`) moveKey(storage, key, `${DISPLAY_PREFIX}:${scope}`);
        else if (key === `${PREFS_HISTORY_PREFIX}:${legacy}`) moveKey(storage, key, `${PREFS_HISTORY_PREFIX}:${scope}`);
        else if (key === `${CLIPBOARD_PREFIX}${legacy}`) moveKey(storage, key, `${CLIPBOARD_PREFIX}${scope}`, (raw) => withOwner(raw, scope));
        else if (key.startsWith(`${JOURNAL_PREFIX}${legacy}:`)) {
          const timelineId = key.slice(`${JOURNAL_PREFIX}${legacy}:`.length);
          moveKey(storage, key, `${JOURNAL_PREFIX}${scope}:${timelineId}`, (raw) => withUserId(raw, scope));
        } else if (key.startsWith(`${QUEUE_PREFIX}${legacy}:`)) {
          const timelineId = key.slice(`${QUEUE_PREFIX}${legacy}:`.length);
          moveKey(storage, key, `${QUEUE_PREFIX}${scope}:${timelineId}`, (raw) => withUserId(raw, scope));
        }
      } catch {
        // One blocked / full key must not stop the others; the legacy key simply stays for now.
      }
    }
  } catch {
    // Blocked storage: there is nothing this browser lets us migrate.
  }
}

/** Background-activity keys (dismissals, seen marks) are per user; the toast switch is a device preference and stays. */
const ACTIVITY_PREFIX = 'reelbolt.activity.';
const ACTIVITY_TOASTS_PREF = 'reelbolt.activity.toasts';

/**
 * Removes every journal, offline-queue and audio-cleanup-consent key from localStorage and deletes the
 * IndexedDB database. The prefixes cover BOTH key shapes: the new scoped keys and any legacy email-form
 * key not yet migrated, so a sign-out never leaves either behind. Display prefs are deliberately not wiped.
 * Best effort and never throws: the localStorage part is done synchronously, the returned promise settles
 * when IndexedDB answered (or could not be reached).
 */
export function wipeEditorLocalData(options: { broadcast?: boolean } = {}): Promise<void> {
  // Every other tab of this browser must stop journalling and wipe too (the cookie is shared, their memory is not).
  if (options.broadcast !== false) announceEditorSignOut();
  try {
    if (typeof window !== 'undefined' && window.localStorage) {
      const storage = window.localStorage;
      const doomed: string[] = [];
      for (let i = 0; i < storage.length; i++) {
        const key = storage.key(i);
        if (key && (key.startsWith(JOURNAL_PREFIX) || key.startsWith(QUEUE_PREFIX) || key.startsWith(PREFS_HISTORY_PREFIX) || key.startsWith(CLIPBOARD_PREFIX) || key.startsWith(PICREVIEW_PREFIX) || key.startsWith(BACKGROUND_RESULTS_PREFIX) || key.startsWith(CLEANUP_CONSENT_PREFIX) || key === LEGACY_CLIPBOARD_KEY || (key.startsWith(ACTIVITY_PREFIX) && key !== ACTIVITY_TOASTS_PREF))) doomed.push(key);
      }
      for (const key of doomed) storage.removeItem(key);
    }
  } catch {
    // blocked storage: nothing to wipe
  }
  try {
    if (typeof window !== 'undefined' && window.sessionStorage) {
      const session = window.sessionStorage;
      const doomed: string[] = [];
      for (let i = 0; i < session.length; i++) {
        const key = session.key(i);
        if (key && key.startsWith(FOLLOW_RUN_PREFIX)) doomed.push(key);
      }
      for (const key of doomed) session.removeItem(key);
    }
  } catch {
    // blocked storage: nothing to wipe
  }
  return new Promise<void>((resolve) => {
    try {
      if (typeof indexedDB === 'undefined') return resolve();
      const req = indexedDB.deleteDatabase(DB_NAME);
      req.onsuccess = () => resolve();
      req.onerror = () => resolve();
      // Another tab still holds it open: the delete completes when that connection closes; do not wait.
      req.onblocked = () => resolve();
    } catch {
      resolve();
    }
  });
}
