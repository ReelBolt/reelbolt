import type { DeviceClass } from './device-class';

/**
 * Which shell the editor shows on a desktop or a tablet: the Simple one (a viewer, a timeline, one panel that follows
 * the selection, three places) or the Full one (the seven pages). View state only: it is stored per device, never in
 * the timeline, a review link, an export or a hand-off. The phone has its own surface and no choice.
 */
export type EditorShell = 'simple' | 'full';

/** The three places of the Simple shell. They change the side panel; the viewer and the timeline stay. */
export type SimplePlace = 'edit' | 'look' | 'export';
export const SIMPLE_PLACES: readonly SimplePlace[] = ['edit', 'look', 'export'];

export const SHELL_STORAGE_KEY = 'reelbolt:editor:shell';
/** Set once the one-line note about the Simple shell was dismissed on this device. */
export const SHELL_INTRO_STORAGE_KEY = 'reelbolt:editor:shell-intro-seen';

type StorageLike = Pick<Storage, 'getItem' | 'setItem'>;

/** The shell the user chose on this device, or null when they never chose. Storage can throw (private mode). */
export function readStoredShell(storage: StorageLike | null | undefined): EditorShell | null {
  try {
    const value = storage?.getItem(SHELL_STORAGE_KEY);
    return value === 'simple' || value === 'full' ? value : null;
  } catch {
    return null;
  }
}

export function storeShell(storage: StorageLike | null | undefined, shell: EditorShell): void {
  try {
    storage?.setItem(SHELL_STORAGE_KEY, shell);
  } catch {
    // Remembered for this page only.
  }
}

export function introSeen(storage: StorageLike | null | undefined): boolean {
  try {
    return storage?.getItem(SHELL_INTRO_STORAGE_KEY) === '1';
  } catch {
    return true;
  }
}

export function markIntroSeen(storage: StorageLike | null | undefined): void {
  try {
    storage?.setItem(SHELL_INTRO_STORAGE_KEY, '1');
  } catch {
    // Shown again next time.
  }
}

/** What the saved editor preferences say about how the person works: a remapped key or a saved layout means they set the full editor up. */
export interface PreferenceFacts {
  keymap?: Record<string, string> | null;
  layouts?: readonly unknown[] | null;
}

export function preferencesCustomised(preferences: PreferenceFacts | null | undefined): boolean {
  if (!preferences) return false;
  return Object.keys(preferences.keymap ?? {}).length > 0 || (preferences.layouts?.length ?? 0) > 0;
}

/**
 * The shell with no stored choice: a tablet starts Simple; a desktop starts Simple unless the saved preferences
 * show the full editor was customised. `preferences` null = not known (never loaded, or the read failed), which
 * counts as not customised.
 */
export function defaultShell(facts: { deviceClass: DeviceClass; preferences: PreferenceFacts | null }): EditorShell {
  if (facts.deviceClass === 'tablet') return 'simple';
  return preferencesCustomised(facts.preferences) ? 'full' : 'simple';
}

/**
 * Resolves the shell. Returns null while it cannot be decided yet: a desktop with no stored choice whose
 * preferences have not answered. `preferencesSettled` is true once they loaded, failed, or were waited for long enough.
 */
export function resolveShell(input: { stored: EditorShell | null; deviceClass: DeviceClass; preferences: PreferenceFacts | null; preferencesSettled: boolean }): EditorShell | null {
  if (input.stored) return input.stored;
  if (input.deviceClass === 'tablet') return 'simple';
  if (!input.preferencesSettled) return null;
  return defaultShell({ deviceClass: input.deviceClass, preferences: input.preferences });
}
