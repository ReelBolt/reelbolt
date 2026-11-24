import { describe, expect, it, vi } from 'vitest';
import { COMPOSE_ACTIONS, composeUnavailable, runComposeCommand, type ComposeCommandEnv } from './commands';
import { commandUnavailable, runCommand, type CommandContext } from '../commands';
import { KEY_ACTIONS, buildKeymap, conflicts, effectiveChords, type KeyAction } from '../keymap';
import { TOUCH_PARITY } from '../keymap-touch';
import { createEditorStore } from '../store';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';

const env = (over: Partial<ComposeCommandEnv> = {}): ComposeCommandEnv => ({
  readOnly: false,
  phone: false,
  newId: (() => { let n = 0; return () => `id${++n}`; })(),
  labels: { maskShape: 'shape', maskInvert: 'invert', removeMask: 'rm mask', removeReframe: 'rm reframe', removeKeyer: 'rm keyer', removePin: 'rm pin', addAdjustment: 'add adj' },
  adjustmentNames: { clip: 'Adjustment', track: 'Adjustments' },
  ...over,
});

function storeWith(extra: Parameters<typeof media>[4] = {}, workspace: 'edit' | 'compose' = 'edit', selection: string[] = ['c1']) {
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [media('c1', 0, 60, 0, extra)])]), revision: 1 });
  store.setUi({ selection, workspace, playheadFrame: 10 });
  return store;
}
const rect = { kind: 'rect', x: 0.5, y: 0.5, w: 0.4, h: 0.4, feather: 0, invert: false } as never;
const clipOf = (store: ReturnType<typeof storeWith>) => store.getState().doc.tracks[0].items[0] as { mask?: { kind: string; invert?: boolean } | null };

describe('Compose chords (T-014)', () => {
  it('every Compose action has a default chord, no conflict, a touch-parity entry and a catalogue row', () => {
    const compose = KEY_ACTIONS.filter((a) => a.group === 'Compose').map((a) => a.id);
    expect(new Set(compose)).toEqual(COMPOSE_ACTIONS);
    for (const a of KEY_ACTIONS.filter((x) => x.group === 'Compose')) expect(a.defaults.length, a.id).toBeGreaterThan(0);
    expect(conflicts({})).toEqual([]);
    for (const id of compose) expect(TOUCH_PARITY[id].kind).toBe('keyboard-only');
  });
  it('a remap moves the chord a control shows (tooltip and aria follow the keymap)', () => {
    expect(effectiveChords({}).toolMask).toEqual(['Shift+M']);
    expect(effectiveChords({ toolMask: 'Shift+9' }).toolMask).toEqual(['Shift+9']);
    expect(buildKeymap({ toolMask: 'Shift+9' }).get('Shift+9')).toBe('toolMask');
    expect(buildKeymap({ toolMask: 'Shift+9' }).get('Shift+M')).toBeUndefined();
  });
  it('a tool chord outside Compose opens Compose and enters the tool, paused', () => {
    const store = storeWith();
    store.setUi({ playbackSpeed: 1 });
    const setWorkspace = vi.fn();
    runComposeCommand('toolMask', store, env(), setWorkspace);
    expect(setWorkspace).toHaveBeenCalledWith('compose');
    expect(store.getState().ui.viewerTool).toBe('mask');
    expect(store.getState().ui.playbackSpeed).toBe(0);
    expect(store.getState().ui.composeEntry).toBe('mask');
  });
  it('tools need one media clip, and say why otherwise', () => {
    expect(composeUnavailable('toolReframe', storeWith({}, 'edit', []), env())).toBe('selectionRequired');
    expect(composeUnavailable('toolCornerPin', storeWith(), env())).toBeNull();
    const setWorkspace = vi.fn();
    runComposeCommand('toolReframe', storeWith({}, 'edit', []), env(), setWorkspace);
    expect(setWorkspace).not.toHaveBeenCalled();
  });
  it('mask shape and invert write one undo step and need a box or oval mask', () => {
    expect(composeUnavailable('maskInvert', storeWith(), env())).toBe('composeNeedsMask');
    const store = storeWith({ mask: rect });
    runComposeCommand('maskShapeNext', store, env(), vi.fn());
    expect(clipOf(store).mask?.kind).toBe('ellipse');
    runComposeCommand('maskInvert', store, env(), vi.fn());
    expect(clipOf(store).mask?.invert).toBe(true);
  });
  it('read-only blocks every writing command with a reason and writes nothing', () => {
    const store = storeWith({ mask: rect });
    for (const id of ['maskShapeNext', 'maskInvert', 'addAdjustmentClip'] as KeyAction[]) expect(composeUnavailable(id, store, env({ readOnly: true })), id).toBe('readOnly');
    expect(composeUnavailable('toolMask', store, env({ readOnly: true }))).toBeNull();
    runComposeCommand('maskInvert', store, env({ readOnly: true }), vi.fn());
    expect(clipOf(store).mask?.invert).toBe(false);
  });
  it('nothing fires on a phone', () => {
    const store = storeWith({ mask: rect });
    for (const id of COMPOSE_ACTIONS) expect(composeUnavailable(id, store, env({ phone: true })), id).toBe('desktopOnly');
    const setWorkspace = vi.fn();
    runComposeCommand('toolMask', store, env({ phone: true }), setWorkspace);
    expect(setWorkspace).not.toHaveBeenCalled();
    expect(store.getState().ui.viewerTool).toBe('select');
  });
  it('entry navigation and removal act only on the Compose page', () => {
    const away = storeWith({ mask: rect });
    expect(composeUnavailable('composeEntryNext', away, env())).toBe('composeNeedsPage');
    expect(composeUnavailable('composeRemoveEntry', away, env())).toBe('composeNeedsPage');
    const store = storeWith({ mask: rect }, 'compose');
    store.setUi({ composeEntry: 'mask' });
    runComposeCommand('composeEntryNext', store, env(), vi.fn());
    expect(store.getState().ui.composeEntry).toBe('reframe');
    runComposeCommand('composeEntryPrevious', store, env(), vi.fn());
    expect(store.getState().ui.composeEntry).toBe('mask');
    expect(composeUnavailable('composeRemoveEntry', store, env())).toBeNull();
    runComposeCommand('composeRemoveEntry', store, env(), vi.fn());
    expect(clipOf(store).mask ?? null).toBeNull();
    expect(composeUnavailable('composeRemoveEntry', store, env())).toBe('composeNothingToRemove');
  });
  it('Add adjustment clip adds a clip on a new top track and selects it', () => {
    const store = storeWith();
    runComposeCommand('addAdjustmentClip', store, env(), vi.fn());
    const tracks = store.getState().doc.tracks;
    expect(tracks).toHaveLength(2);
    expect(tracks[1].items[0].type).toBe('adjustmentClip');
    expect(store.getState().ui.selection).toEqual([tracks[1].items[0].id]);
  });
  it('goes through runCommand and the palette availability', () => {
    const store = storeWith({ mask: rect });
    const ctx = { store, setWorkspace: vi.fn(), compose: env() } as unknown as CommandContext;
    runCommand('maskInvert', ctx);
    expect(clipOf(store).mask?.invert).toBe(true);
    const state = { store, canUndo: false, canRedo: false, hasClipboard: false };
    expect(commandUnavailable('maskInvert', { ...state, readOnly: true })).toBe('readOnly');
    expect(commandUnavailable('toolMask', { ...state, phone: true })).toBe('desktopOnly');
  });
});
