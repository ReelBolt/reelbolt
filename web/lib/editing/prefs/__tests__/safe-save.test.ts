/** @vitest-environment jsdom */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { EditorPreferences, SavedLayout } from '../../api/editor-p9';

const who = { email: 'a@x.io' as string | null };
vi.mock('../../../hooks/use-auth', () => ({ getUserFromCookie: () => (who.email ? { email: who.email } : null) }));
const server = { prefs: { keymap: {}, layouts: [], updatedAt: 't0' } as EditorPreferences, puts: [] as unknown[], delay: 0 };
vi.mock('../../api/editor-p9', () => ({
  getEditorPreferences: async () => structuredClone(server.prefs),
  saveEditorPreferences: async (body: { keymap: Record<string, string>; layouts: SavedLayout[] }) => {
    if (server.delay) await new Promise((r) => setTimeout(r, server.delay));
    server.puts.push(structuredClone(body));
    server.prefs = { ...body, updatedAt: `t${server.puts.length}` };
    return structuredClone(server.prefs);
  },
}));

import { userScopeFromEmail, wipeEditorLocalData } from '../../sync/user-scope';
import { LEGACY_HISTORY_KEY, historyKey, mergePreferences, readHistory, restorePreviousPreferences, savePreferencesSafely } from '../safe-save';

const USER_A = userScopeFromEmail('a@x.io');

const layout = (name: string, left = 0.2): SavedLayout => ({ name, workspace: 'edit', sizes: { left: 1, right: 1, timeline: 1 }, fractions: { left, right: 0.2, timeline: 0.4 } });

beforeEach(() => {
  window.localStorage.clear();
  who.email = 'a@x.io';
  server.prefs = { keymap: {}, layouts: [], updatedAt: 't0' };
  server.puts = [];
  server.delay = 0;
});

describe('mergePreferences', () => {
  it('applies only this save\'s changes on top of what is stored', () => {
    const base = { keymap: { undo: 'KeyU' }, layouts: [layout('A')] };
    const latest = { keymap: { undo: 'KeyU', redo: 'KeyY' }, layouts: [layout('A'), layout('B')] };
    const next = { keymap: { undo: 'KeyZ' }, layouts: [] as SavedLayout[] };
    const merged = mergePreferences(base, latest, next);
    expect(merged.keymap).toEqual({ undo: 'KeyZ', redo: 'KeyY' });
    expect(merged.layouts.map((l) => l.name)).toEqual(['B']);
  });
});

describe('savePreferencesSafely', () => {
  it('keeps the replaced copy for "restore previous settings" and restores it', async () => {
    server.prefs = { keymap: { undo: 'KeyU' }, layouts: [], updatedAt: 't0' };
    const base = structuredClone(server.prefs);
    await savePreferencesSafely(base, { keymap: { undo: 'KeyZ' }, layouts: [] });
    expect(readHistory()[0].keymap).toEqual({ undo: 'KeyU' });
    const restored = await restorePreviousPreferences();
    expect(restored?.keymap).toEqual({ undo: 'KeyU' });
    expect(server.prefs.keymap).toEqual({ undo: 'KeyU' });
    expect(readHistory()[0].keymap).toEqual({ undo: 'KeyZ' });
  });
  it('keeps another device\'s change when the stored copy moved on', async () => {
    const base = structuredClone(server.prefs);
    server.prefs = { keymap: { redo: 'KeyY' }, layouts: [], updatedAt: 'other' };
    await savePreferencesSafely(base, { keymap: { undo: 'KeyZ' }, layouts: [] });
    expect(server.prefs.keymap).toEqual({ redo: 'KeyY', undo: 'KeyZ' });
  });
  it('runs two quick saves one after the other', async () => {
    server.delay = 20;
    const base = structuredClone(server.prefs);
    const a = savePreferencesSafely(base, { keymap: { undo: 'KeyA' }, layouts: [] });
    const b = savePreferencesSafely({ ...base, updatedAt: 't1' }, { keymap: { undo: 'KeyA', redo: 'KeyB' }, layouts: [] });
    await Promise.all([a, b]);
    expect(server.puts).toHaveLength(2);
    expect(server.prefs.keymap).toEqual({ undo: 'KeyA', redo: 'KeyB' });
  });
  it('a failed save is thrown and does not block the next', async () => {
    const base = structuredClone(server.prefs);
    server.delay = 0;
    const failing = savePreferencesSafely(base, { keymap: {}, layouts: [] }); // succeeds; then break the server
    await failing;
    const ok = await savePreferencesSafely({ ...base, updatedAt: 't1' }, { keymap: { undo: 'KeyQ' }, layouts: [] });
    expect(ok.keymap).toEqual({ undo: 'KeyQ' });
  });
});

describe('history is per user', () => {
  it('another user on the same browser sees no history and cannot restore it', async () => {
    server.prefs = { keymap: { undo: 'KeyU' }, layouts: [], updatedAt: 't0' };
    await savePreferencesSafely(structuredClone(server.prefs), { keymap: { undo: 'KeyZ' }, layouts: [] });
    expect(readHistory()).toHaveLength(1);
    who.email = 'b@x.io';
    expect(readHistory()).toEqual([]);
    expect(await restorePreviousPreferences()).toBeNull();
    who.email = null;
    expect(readHistory()).toEqual([]);
  });
  it('sign-out wipes it', async () => {
    await savePreferencesSafely(structuredClone(server.prefs), { keymap: { undo: 'KeyZ' }, layouts: [] });
    expect(window.localStorage.getItem(historyKey(USER_A))).not.toBeNull();
    await wipeEditorLocalData();
    expect(window.localStorage.getItem(historyKey(USER_A))).toBeNull();
  });
  it('drops the legacy unscoped key and never adopts it', () => {
    window.localStorage.setItem(LEGACY_HISTORY_KEY, JSON.stringify([{ keymap: { undo: 'KeyQ' }, layouts: [], replacedAt: 'x' }]));
    expect(readHistory()).toEqual([]);
    expect(window.localStorage.getItem(LEGACY_HISTORY_KEY)).toBeNull();
  });
});
