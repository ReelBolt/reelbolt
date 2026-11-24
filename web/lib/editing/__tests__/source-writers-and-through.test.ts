// @vitest-environment jsdom
/**
 * Wave 3 fix round A: controls that keep focus after a click let the editor's chords through (keeping only
 * their own keys), and while Source shows every timeline writer does nothing (undo and redo stay).
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { KEY_ACTIONS, shortcutBlocked } from '../keymap';
import { SOURCE_DECISIONS, commandUnavailable, runCommand, showRecordFirst, type CommandContext } from '../commands';
import { createEditorStore } from '../store';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';

afterEach(() => {
  document.body.innerHTML = '';
  vi.restoreAllMocks();
});

const el = (html: string): HTMLElement => {
  const host = document.createElement('div');
  host.innerHTML = html;
  document.body.appendChild(host);
  return host.firstElementChild as HTMLElement;
};
const BARE = ['h', 'r', 'l', 'k', 'j', 'Space', 'H', 'R', 'L', 'K', 'J', 'Mod+Z', 'Delete', 'Escape'];

describe('controls that keep focus after a click', () => {
  const GROUP = '<div role="radiogroup" data-editor-shortcuts="through" data-editor-own-keys="radio"><button role="radio" aria-checked="true">Ripple</button></div>';

  it('a trim radio lets bare chords through, keeps Enter, and keeps arrows only for keyboard focus', () => {
    const radio = el(GROUP).querySelector('button') as HTMLElement;
    for (const chord of BARE) expect(shortcutBlocked(radio, chord), chord).toBe(false);
    expect(shortcutBlocked(radio, 'Enter')).toBe(true);
    const real = Element.prototype.matches;
    const focusVisible = (value: boolean) =>
      vi.spyOn(Element.prototype, 'matches').mockImplementation(function (this: Element, selector: string) {
        return selector === ':focus-visible' ? value : real.call(this, selector);
      });
    // After a mouse click the radio is not focus-visible: the arrows step frames as before.
    focusVisible(false);
    expect(shortcutBlocked(radio, 'ArrowLeft')).toBe(false);
    vi.restoreAllMocks();
    // Focused from the keyboard, the radio keeps its arrows.
    focusVisible(true);
    for (const chord of ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown']) expect(shortcutBlocked(radio, chord), chord).toBe(true);
    expect(shortcutBlocked(radio, 'Shift+ArrowLeft')).toBe(false);
  });

  it('the Source | Record switch input is a radio input: chords go through', () => {
    const input = el('<div data-editor-shortcuts="through" data-editor-own-keys="radio"><input type="radio" /></div>').querySelector('input') as HTMLElement;
    for (const chord of BARE) expect(shortcutBlocked(input, chord), chord).toBe(false);
  });

  it('Watch keeps Enter only', () => {
    const watch = el('<button data-editor-shortcuts="through" data-editor-own-keys="activate">Watch</button>');
    for (const chord of [...BARE, 'ArrowLeft', 'ArrowDown']) expect(shortcutBlocked(watch, chord), chord).toBe(false);
    expect(shortcutBlocked(watch, 'Enter')).toBe(true);
  });

  it('the scrub slider keeps its arrows, Home, End and Page keys', () => {
    const thumb = el('<div data-editor-shortcuts="through" data-editor-own-keys="slider"><div role="slider" tabindex="0"></div></div>').querySelector('[role="slider"]') as HTMLElement;
    for (const chord of ['ArrowLeft', 'ArrowRight', 'Home', 'End', 'PageUp']) expect(shortcutBlocked(thumb, chord), chord).toBe(true);
    for (const chord of BARE) expect(shortcutBlocked(thumb, chord), chord).toBe(false);
  });

  it('still blocks real menus, selects, text fields inside a through host, and dialogs', () => {
    expect(shortcutBlocked(el('<div role="menu"><div role="menuitem">Cut</div></div>').firstElementChild, 'h')).toBe(true);
    expect(shortcutBlocked(el('<select></select>'), 'h')).toBe(true);
    expect(shortcutBlocked(el('<div data-editor-shortcuts="through" data-editor-own-keys="radio"><input type="text" /></div>').querySelector('input'), 'h')).toBe(true);
    expect(shortcutBlocked(el('<div role="dialog"><div data-editor-shortcuts="through"><button>x</button></div></div>').querySelector('button'), 'h')).toBe(true);
  });
});

function setup() {
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [media('c1', 0, 60, 0)])]), revision: 1 });
  store.setUi({ playheadFrame: 10, workspace: 'cut', viewerMode: 'source', viewerSource: 'file:a', selection: ['c1'] });
  const calls: string[] = [];
  const fn = (name: string) => vi.fn(() => { calls.push(name); });
  const ctx: CommandContext = {
    store, endFrame: 100, undo: fn('undo'), redo: fn('redo'), addMarker: fn('addMarker'), liftSelection: fn('lift'), extractSelection: fn('extract'),
    nudgeSelection: fn('nudge'), bladeAtPlayhead: fn('blade'), playInToOut: vi.fn(() => true), setMarks: fn('setMarks'),
    editing: { copy: fn('copy'), cut: fn('cut'), paste: fn('paste'), duplicate: fn('duplicate'), linkSelected: fn('link'), unlinkSelected: fn('unlink'), moveToTrack: fn('moveToTrack') },
    setWorkspace: vi.fn(), openCheatsheet: vi.fn(), openPalette: vi.fn(),
    source: { togglePlay: vi.fn(), shuttleReverse: vi.fn(), stop: vi.fn(), shuttleForward: vi.fn(), step: vi.fn(), goToStart: vi.fn(), goToEnd: vi.fn(), markIn: vi.fn(), markOut: vi.fn(), clearMarks: vi.fn() },
    place: fn('place'),
    compose: { readOnly: false, phone: false, mask: undefined } as never,
  };
  return { store, ctx, calls };
}

describe('Source mode decides every action (a new writer fails here until it is decided)', () => {
  it('has a decision for exactly the keymap actions', () => {
    expect(Object.keys(SOURCE_DECISIONS).sort()).toEqual(KEY_ACTIONS.map((a) => a.id).sort());
  });

  it('every writer does nothing in Source, with the palette reason; undo and redo and placement stay', () => {
    for (const info of KEY_ACTIONS) {
      if (SOURCE_DECISIONS[info.id] !== 'writer') continue;
      const { store, ctx, calls } = setup();
      const revision = store.getState().history.length;
      runCommand(info.id, ctx);
      expect(calls, info.id).toEqual([]);
      expect(store.getState().history.length, info.id).toBe(revision);
      expect(commandUnavailable(info.id, { store, canUndo: true, canRedo: true, hasClipboard: true }), info.id).toBe('switchToRecord');
    }
    const { ctx, calls, store } = setup();
    runCommand('undo', ctx);
    runCommand('redo', ctx);
    runCommand('placeInsert', ctx);
    expect(calls).toEqual(['undo', 'redo', 'place']);
    expect(commandUnavailable('undo', { store, canUndo: true, canRedo: true, hasClipboard: true })).toBeNull();
  });

  it('names the known writers', () => {
    const writers = Object.entries(SOURCE_DECISIONS).filter(([, d]) => d === 'writer').map(([id]) => id);
    for (const id of ['deleteSelection', 'liftSelection', 'bladeAtPlayhead', 'cutClips', 'pasteClips', 'duplicateClips', 'nudgeBack', 'nudgeForward10', 'moveTrackUp', 'linkClips', 'unlinkClips', 'addAdjustmentClip', 'composeRemoveEntry']) expect(writers, id).toContain(id);
  });

  it('writers run as before in Record', () => {
    const { ctx, calls, store } = setup();
    store.setUi({ viewerMode: 'record' });
    runCommand('duplicateClips', ctx);
    expect(calls).toEqual(['duplicate']);
  });
});

describe('showRecordFirst', () => {
  it('a pointer writer returns the viewer to Record; in Record it changes nothing', () => {
    let viewerMode = 'source';
    const store = { getState: () => ({ ui: { viewerMode } }), setUi: vi.fn((patch: { viewerMode: string }) => { viewerMode = patch.viewerMode; }) };
    const asStore = store as unknown as Parameters<typeof showRecordFirst>[0];
    showRecordFirst(asStore);
    expect(viewerMode).toBe('record');
    showRecordFirst(asStore);
    expect(store.setUi).toHaveBeenCalledTimes(1);
  });
});
