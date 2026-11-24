/**
 * VF13 / VF18: how the editor looks on THIS device for THIS user.
 *
 *   - density: how big the controls are. `comfortable` is the default; `compact` packs them tighter on a
 *     fine pointer (a mouse). A coarse pointer (a finger) always gets comfortable controls with 44 px hit areas.
 *   - surround: `match` follows the app's light/dark scheme; `dark` keeps the editor chrome dark while the app is
 *     light (for night grading).
 *
 * Kept next to the user's other device-level editor state (per user, localStorage), not in the server's
 * preferences row: it is a property of the screen in front of the person, and it needs no migration. The reader is a
 * validator: anything unknown or malformed falls back to the default for that field, extra fields are ignored,
 * and a copy written by a later version that adds fields still loads (additive).
 */

import { getUserFromCookie } from '../../hooks/use-auth';
import { DISPLAY_PREFIX, migrateEditorStorageScope, userScopeFromEmail } from '../sync/user-scope';

export type EditorDensity = 'comfortable' | 'compact';
export type EditorSurround = 'match' | 'dark';

export interface EditorDisplayPrefs {
  density: EditorDensity;
  surround: EditorSurround;
}

export const DEFAULT_DISPLAY_PREFS: EditorDisplayPrefs = Object.freeze({ density: 'comfortable', surround: 'match' });

export const DISPLAY_PREFS_PREFIX = DISPLAY_PREFIX;
export const displayPrefsKey = (userId: string = userScopeFromEmail(getUserFromCookie()?.email)): string => `${DISPLAY_PREFS_PREFIX}:${userId}`;

const isDensity = (v: unknown): v is EditorDensity => v === 'comfortable' || v === 'compact';
const isSurround = (v: unknown): v is EditorSurround => v === 'match' || v === 'dark';

/** Turns whatever was stored into valid prefs: each field is kept only when it is one of its known values. */
export function validateDisplayPrefs(raw: unknown): EditorDisplayPrefs {
  const o = raw !== null && typeof raw === 'object' ? (raw as Record<string, unknown>) : {};
  return {
    density: isDensity(o.density) ? o.density : DEFAULT_DISPLAY_PREFS.density,
    surround: isSurround(o.surround) ? o.surround : DEFAULT_DISPLAY_PREFS.surround,
  };
}

/** The density that is really applied: a finger always gets comfortable controls. */
export function effectiveDensity(preferred: EditorDensity, coarsePointer: boolean): EditorDensity {
  return coarsePointer ? 'comfortable' : preferred;
}

export function parseDisplayPrefs(json: string | null): EditorDisplayPrefs {
  if (json === null) return { ...DEFAULT_DISPLAY_PREFS };
  try {
    return validateDisplayPrefs(JSON.parse(json));
  } catch {
    return { ...DEFAULT_DISPLAY_PREFS };
  }
}

export function readDisplayPrefs(): EditorDisplayPrefs {
  if (typeof window === 'undefined') return { ...DEFAULT_DISPLAY_PREFS };
  migrateEditorStorageScope(getUserFromCookie()?.email);
  try {
    return parseDisplayPrefs(window.localStorage.getItem(displayPrefsKey()));
  } catch {
    return { ...DEFAULT_DISPLAY_PREFS };
  }
}

const listeners = new Set<() => void>();
let cache: { key: string; raw: string | null; value: EditorDisplayPrefs } | null = null;

/** The same object until the stored text changes (what `useSyncExternalStore` needs). */
export function getDisplayPrefsSnapshot(): EditorDisplayPrefs {
  if (typeof window === 'undefined') return DEFAULT_DISPLAY_PREFS;
  migrateEditorStorageScope(getUserFromCookie()?.email);
  const key = displayPrefsKey();
  let raw: string | null = null;
  try {
    raw = window.localStorage.getItem(key);
  } catch {
    raw = null;
  }
  if (!cache || cache.key !== key || cache.raw !== raw) cache = { key, raw, value: parseDisplayPrefs(raw) };
  return cache.value;
}

export const getServerDisplayPrefs = (): EditorDisplayPrefs => DEFAULT_DISPLAY_PREFS;

export function subscribeDisplayPrefs(onChange: () => void): () => void {
  listeners.add(onChange);
  const onStorage = (e: StorageEvent) => {
    if (e.key === null || e.key.startsWith(DISPLAY_PREFS_PREFIX)) onChange();
  };
  window.addEventListener('storage', onStorage);
  return () => {
    listeners.delete(onChange);
    window.removeEventListener('storage', onStorage);
  };
}

/** Saves a change on top of what is stored; the screen follows through the subscription. */
export function updateDisplayPrefs(change: Partial<EditorDisplayPrefs>): EditorDisplayPrefs {
  migrateEditorStorageScope(getUserFromCookie()?.email);
  const next = validateDisplayPrefs({ ...readDisplayPrefs(), ...change });
  try {
    window.localStorage.setItem(displayPrefsKey(), JSON.stringify(next));
  } catch {
    /* storage unavailable: the change lasts until the page closes only */
    cache = { key: displayPrefsKey(), raw: JSON.stringify(next), value: next };
  }
  for (const l of listeners) l();
  return next;
}
