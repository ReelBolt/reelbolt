/**
 * R12.4: preferences that save safely.
 *
 *   - Saves are serialized: two quick saves never overlap, so the later one cannot be overwritten
 *     by the earlier one arriving second.
 *   - The server keeps one row per user and a PUT replaces it, so a save first reads the stored
 *     copy; if another tab or device changed it since this one loaded, only what THIS save changed
 *     is applied on top of the stored copy (`mergePreferences`) rather than dropping their edit.
 *   - What was stored before every save is kept on this device (the last few), so
 *     "Restore previous settings" can put it back.
 */

import { getUserFromCookie } from '../../hooks/use-auth';
import { ANONYMOUS_SCOPE, PREFS_HISTORY_PREFIX, migrateEditorStorageScope, userScopeFromEmail } from '../sync/user-scope';
import { getEditorPreferences, saveEditorPreferences, type EditorPreferences, type SavedLayout } from '../api/editor-p9';

export type PrefsBody = Pick<EditorPreferences, 'keymap' | 'layouts'>;

export interface PrefsSnapshot extends PrefsBody {
  /** When the copy was replaced (ISO, local clock). */
  replacedAt: string;
}

/** Legacy unscoped key: never adopted, dropped on first read. */
export const LEGACY_HISTORY_KEY = PREFS_HISTORY_PREFIX;
export const currentUser = (): string => userScopeFromEmail(getUserFromCookie()?.email);
/** Per user: another account on this browser never reaches this key. */
export const historyKey = (userId: string = currentUser()): string => `${LEGACY_HISTORY_KEY}:${userId}`;
export const HISTORY_LIMIT = 5;

const layoutKey = (l: SavedLayout): string => `${l.workspace}\u0000${l.name}`;
const same = (a: unknown, b: unknown): boolean => JSON.stringify(a) === JSON.stringify(b);

/** `next` is what the user wants, `base` what they started from, `latest` what is stored now. */
export function mergePreferences(base: PrefsBody, latest: PrefsBody, next: PrefsBody): PrefsBody {
  const keymap: Record<string, string> = { ...latest.keymap };
  for (const id of new Set([...Object.keys(base.keymap), ...Object.keys(next.keymap)])) {
    if (same(base.keymap[id], next.keymap[id])) continue;
    if (next.keymap[id] === undefined) delete keymap[id];
    else keymap[id] = next.keymap[id];
  }
  const baseL = new Map(base.layouts.map((l) => [layoutKey(l), l]));
  const nextL = new Map(next.layouts.map((l) => [layoutKey(l), l]));
  let layouts = [...latest.layouts];
  for (const k of baseL.keys()) if (!nextL.has(k)) layouts = layouts.filter((x) => layoutKey(x) !== k);
  for (const [k, l] of nextL) {
    if (same(baseL.get(k), l)) continue;
    const at = layouts.findIndex((x) => layoutKey(x) === k);
    if (at >= 0) layouts[at] = l;
    else layouts.push(l);
  }
  return { keymap, layouts: layouts.slice(-50) };
}

export function readHistory(): PrefsSnapshot[] {
  if (typeof window === 'undefined') return [];
  migrateEditorStorageScope(getUserFromCookie()?.email);
  const user = currentUser();
  // Nobody is signed in: there is no copy that could be theirs.
  if (user === ANONYMOUS_SCOPE) return [];
  try {
    window.localStorage.removeItem(LEGACY_HISTORY_KEY);
    const parsed: unknown = JSON.parse(window.localStorage.getItem(historyKey(user)) ?? '[]');
    return Array.isArray(parsed) ? (parsed as PrefsSnapshot[]).filter((s) => s && typeof s === 'object' && s.keymap && Array.isArray(s.layouts)).slice(0, HISTORY_LIMIT) : [];
  } catch {
    return [];
  }
}

function pushHistory(body: PrefsBody): void {
  if (typeof window === 'undefined') return;
  migrateEditorStorageScope(getUserFromCookie()?.email);
  if (currentUser() === ANONYMOUS_SCOPE) return;
  const history = readHistory();
  if (history[0] && same({ keymap: history[0].keymap, layouts: history[0].layouts }, body)) return;
  try {
    window.localStorage.setItem(historyKey(), JSON.stringify([{ keymap: body.keymap, layouts: body.layouts, replacedAt: new Date().toISOString() }, ...history].slice(0, HISTORY_LIMIT)));
  } catch {
    /* storage unavailable: the save still goes through, only "restore" has nothing to offer */
  }
}

let queue: Promise<unknown> = Promise.resolve();

/** Runs `job` after every earlier save has settled (a failed one does not block the next). */
export function enqueueSave<T>(job: () => Promise<T>): Promise<T> {
  const run = queue.then(job, job);
  queue = run.catch(() => undefined);
  return run;
}

/** Saves `next` (started from `base`); resolves with what the server now holds. */
export function savePreferencesSafely(base: EditorPreferences, next: PrefsBody): Promise<EditorPreferences> {
  return enqueueSave(async () => {
    const latest = await getEditorPreferences();
    const changedElsewhere = latest.updatedAt !== base.updatedAt;
    const body = changedElsewhere ? mergePreferences(base, latest, next) : next;
    pushHistory({ keymap: latest.keymap, layouts: latest.layouts });
    return saveEditorPreferences(body);
  });
}

/** Puts the most recent replaced copy back; null when there is nothing to restore. */
export function restorePreviousPreferences(): Promise<EditorPreferences | null> {
  return enqueueSave(async () => {
    const [previous, ...rest] = readHistory();
    if (!previous) return null;
    const current = await getEditorPreferences();
    const saved = await saveEditorPreferences({ keymap: previous.keymap, layouts: previous.layouts });
    try {
      window.localStorage.setItem(historyKey(), JSON.stringify([{ keymap: current.keymap, layouts: current.layouts, replacedAt: new Date().toISOString() }, ...rest].slice(0, HISTORY_LIMIT)));
    } catch {
      /* see pushHistory */
    }
    return saved;
  });
}
