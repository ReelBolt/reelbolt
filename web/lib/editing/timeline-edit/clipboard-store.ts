import { getUserFromCookie } from '../../hooks/use-auth';
import { ANONYMOUS_SCOPE, CLIPBOARD_PREFIX, LEGACY_CLIPBOARD_KEY, migrateEditorStorageScope, userScopeFromEmail } from '../sync/user-scope';
import { parseClipboard, type ClipboardPayload } from './clipboard';

/**
 * Where the copied clips live between a copy and a paste: in memory, mirrored to localStorage so a
 * second editor tab (another timeline) can paste them, and offered to the system clipboard as JSON
 * text when the browser allows it. Reading prefers localStorage (the latest copy from any tab), then memory.
 */
/** Per-user key, like the journal and prefs history; wipeEditorLocalData removes every key under this prefix. */
export const CLIPBOARD_KEY_PREFIX = CLIPBOARD_PREFIX;
const LEGACY_KEY = LEGACY_CLIPBOARD_KEY;
let memory: { owner: string; payload: ClipboardPayload } | null = null;

const currentOwner = (): string => userScopeFromEmail(getUserFromCookie()?.email);
export const clipboardKey = (userId: string): string => `${CLIPBOARD_KEY_PREFIX}${userId}`;

/** Drops the old unscoped key without reading it: it has no owner, so it is never adopted. */
function dropLegacy(): void {
  try {
    if (typeof localStorage !== 'undefined') localStorage.removeItem(LEGACY_KEY);
  } catch {
    // blocked storage
  }
}

/** The clipboard text with its owner stamped in, so a paste of the system-clipboard text can be checked. */
function stamp(payload: ClipboardPayload, owner: string): string {
  return JSON.stringify({ ...payload, owner });
}

/** Parses clipboard text (localStorage or the system clipboard); only text stamped with `owner` is accepted, and 'anonymous' never matches. */
export function parseOwnedClipboard(text: string | null | undefined, owner: string = currentOwner()): ClipboardPayload | null {
  if (!text || owner === ANONYMOUS_SCOPE) return null;
  const payload = parseClipboard(text);
  if (!payload) return null;
  const stampedBy = (JSON.parse(text) as { owner?: unknown }).owner;
  if (stampedBy !== owner) return null;
  const { owner: _owner, ...bare } = payload as ClipboardPayload & { owner?: string };
  return bare;
}

export function writeClipClipboard(payload: ClipboardPayload): void {
  migrateEditorStorageScope(getUserFromCookie()?.email);
  dropLegacy();
  const owner = currentOwner();
  if (owner === ANONYMOUS_SCOPE) return;
  memory = { owner, payload };
  const text = stamp(payload, owner);
  try {
    if (typeof localStorage !== 'undefined') localStorage.setItem(clipboardKey(owner), text);
  } catch {
    // Storage full or blocked: the in-memory copy still serves this tab.
  }
  try {
    void navigator.clipboard?.writeText(text).catch(() => undefined);
  } catch {
    // No clipboard permission: the internal clipboard is the source of truth.
  }
}

export function readClipClipboard(): ClipboardPayload | null {
  migrateEditorStorageScope(getUserFromCookie()?.email);
  dropLegacy();
  const owner = currentOwner();
  if (owner === ANONYMOUS_SCOPE) return null;
  try {
    // Storage first: another tab may have copied since this one did.
    const stored = typeof localStorage !== 'undefined' ? parseOwnedClipboard(localStorage.getItem(clipboardKey(owner)), owner) : null;
    if (stored) return stored;
  } catch {
    // fall through to the in-memory copy
  }
  return memory && memory.owner === owner ? memory.payload : null;
}

/** Forgets the in-memory copy (sign-out / user switch). */
export function clearClipClipboardMemory(): void {
  memory = null;
}

export function clearClipClipboardForTests(): void {
  memory = null;
  try {
    if (typeof localStorage !== 'undefined') {
      localStorage.removeItem(LEGACY_KEY);
      for (let i = localStorage.length - 1; i >= 0; i--) {
        const key = localStorage.key(i);
        if (key?.startsWith(CLIPBOARD_KEY_PREFIX)) localStorage.removeItem(key);
      }
    }
  } catch {
    // ignore
  }
}
