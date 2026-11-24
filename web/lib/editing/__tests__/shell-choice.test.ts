import { describe, expect, it } from 'vitest';
import { defaultShell, introSeen, markIntroSeen, preferencesCustomised, readStoredShell, resolveShell, SHELL_STORAGE_KEY, storeShell } from '../shell-choice';

function memoryStorage(initial: Record<string, string> = {}) {
  const data = new Map(Object.entries(initial));
  return { getItem: (k: string) => data.get(k) ?? null, setItem: (k: string, v: string) => void data.set(k, v), data };
}
const throwing = { getItem: () => { throw new Error('blocked'); }, setItem: () => { throw new Error('blocked'); } };

describe('the stored shell', () => {
  it('reads only the two known values', () => {
    expect(readStoredShell(memoryStorage({ [SHELL_STORAGE_KEY]: 'simple' }))).toBe('simple');
    expect(readStoredShell(memoryStorage({ [SHELL_STORAGE_KEY]: 'full' }))).toBe('full');
    expect(readStoredShell(memoryStorage({ [SHELL_STORAGE_KEY]: 'pro' }))).toBeNull();
    expect(readStoredShell(memoryStorage())).toBeNull();
    expect(readStoredShell(null)).toBeNull();
  });

  it('survives storage that throws (private mode): nothing stored, nothing broken', () => {
    expect(readStoredShell(throwing)).toBeNull();
    expect(() => storeShell(throwing, 'full')).not.toThrow();
    // An intro that cannot be remembered as seen is not shown on every visit.
    expect(introSeen(throwing)).toBe(true);
    expect(() => markIntroSeen(throwing)).not.toThrow();
  });

  it('stores the choice and the dismissed intro', () => {
    const storage = memoryStorage();
    storeShell(storage, 'full');
    expect(readStoredShell(storage)).toBe('full');
    expect(introSeen(storage)).toBe(false);
    markIntroSeen(storage);
    expect(introSeen(storage)).toBe(true);
  });
});

describe('who starts where', () => {
  it('a remapped key or a saved layout means the full editor was set up', () => {
    expect(preferencesCustomised(null)).toBe(false);
    expect(preferencesCustomised({ keymap: {}, layouts: [] })).toBe(false);
    expect(preferencesCustomised({ keymap: { undo: 'Mod+U' }, layouts: [] })).toBe(true);
    expect(preferencesCustomised({ keymap: {}, layouts: [{}] })).toBe(true);
  });

  it('a tablet starts Simple; a desktop starts Simple unless the full editor was customised', () => {
    expect(defaultShell({ deviceClass: 'tablet', preferences: { keymap: { undo: 'Mod+U' } } })).toBe('simple');
    expect(defaultShell({ deviceClass: 'desktop', preferences: null })).toBe('simple');
    expect(defaultShell({ deviceClass: 'desktop', preferences: { keymap: {}, layouts: [] } })).toBe('simple');
    expect(defaultShell({ deviceClass: 'desktop', preferences: { keymap: { undo: 'Mod+U' } } })).toBe('full');
  });

  it('a stored choice always wins; a desktop without one waits for the preferences', () => {
    expect(resolveShell({ stored: 'full', deviceClass: 'tablet', preferences: null, preferencesSettled: false })).toBe('full');
    expect(resolveShell({ stored: null, deviceClass: 'tablet', preferences: null, preferencesSettled: false })).toBe('simple');
    expect(resolveShell({ stored: null, deviceClass: 'desktop', preferences: null, preferencesSettled: false })).toBeNull();
    expect(resolveShell({ stored: null, deviceClass: 'desktop', preferences: null, preferencesSettled: true })).toBe('simple');
    expect(resolveShell({ stored: null, deviceClass: 'desktop', preferences: { layouts: [{}] }, preferencesSettled: true })).toBe('full');
  });
});
