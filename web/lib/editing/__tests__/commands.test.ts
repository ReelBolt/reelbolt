import { describe, expect, it, vi } from 'vitest';
import {
  COMMANDS,
  PAGE_COMMANDS,
  commandDescriptor,
  commandUnavailable,
  runCommand,
  type CommandContext,
} from '../commands';
import { KEY_ACTIONS, type KeyAction } from '../keymap';
import { createEditorStore } from '../store';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';

function storeWith(selection: string[] = []) {
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [media('c1', 0, 60, 0)])]), revision: 1 });
  store.setUi({ selection, playheadFrame: 10 });
  return store;
}

function context(store = storeWith()): CommandContext {
  return {
    store,
    endFrame: 100,
    undo: vi.fn(),
    redo: vi.fn(),
    addMarker: vi.fn(),
    liftSelection: vi.fn(),
    extractSelection: vi.fn(),
    nudgeSelection: vi.fn(),
    bladeAtPlayhead: vi.fn(),
    playInToOut: vi.fn(() => true),
    setMarks: vi.fn(),
    editing: {
      copy: vi.fn(), cut: vi.fn(), paste: vi.fn(), duplicate: vi.fn(), linkSelected: vi.fn(), unlinkSelected: vi.fn(), moveToTrack: vi.fn(),
    },
    setWorkspace: vi.fn(),
    openCheatsheet: vi.fn(),
    openPalette: vi.fn(),
  };
}

describe('the one command catalogue (T-007)', () => {
  it('has exactly one descriptor per keymap action, in catalogue order', () => {
    expect(COMMANDS.map((c) => c.id)).toEqual(KEY_ACTIONS.map((a) => a.id));
    expect(new Set(COMMANDS.map((c) => c.id)).size).toBe(KEY_ACTIONS.length);
    for (const action of KEY_ACTIONS) {
      const command = commandDescriptor(action.id);
      expect(command?.group).toBe(action.group);
      expect(command?.label).toBe(action.label);
      expect(typeof command?.run).toBe('function');
    }
    expect(KEY_ACTIONS).toHaveLength(77);
  });

  it('the full-screen action asks the host to toggle the viewer, and does nothing where there is no viewer to enlarge', () => {
    const toggleFullscreen = vi.fn();
    const preventDefault = vi.fn();
    runCommand('viewerFullscreen', { ...context(), toggleFullscreen, event: { preventDefault } });
    expect(toggleFullscreen).toHaveBeenCalledTimes(1);
    expect(preventDefault).toHaveBeenCalledTimes(1);
    expect(() => runCommand('viewerFullscreen', context())).not.toThrow();
  });

  it('routes the Events lane actions to the lane, and does nothing where there is no lane', () => {
    const events = { toggleLane: vi.fn(), toggleSnap: vi.fn(), actOnNearest: vi.fn() };
    const preventDefault = vi.fn();
    runCommand('toggleEventsLane', { ...context(), events });
    runCommand('toggleEventSnap', { ...context(), events });
    runCommand('actOnEvent', { ...context(), events, event: { preventDefault } });
    expect([events.toggleLane, events.toggleSnap, events.actOnNearest].map((f) => f.mock.calls.length)).toEqual([1, 1, 1]);
    expect(preventDefault).toHaveBeenCalledTimes(1);
    expect(() => runCommand('actOnEvent', context())).not.toThrow();
  });

  it('routes the previous actions to the same handlers (undo, marks, nudge, editing)', () => {
    const ctx = context();
    runCommand('undo', { ...ctx, event: { preventDefault: vi.fn() } });
    runCommand('redo', { ...ctx, event: { preventDefault: vi.fn() } });
    runCommand('nudgeBack10', { ...ctx, event: { preventDefault: vi.fn() } });
    runCommand('copyClips', { ...ctx, event: { preventDefault: vi.fn() } });
    runCommand('addMarker', ctx);
    expect(ctx.undo).toHaveBeenCalledTimes(1);
    expect(ctx.redo).toHaveBeenCalledTimes(1);
    expect(ctx.nudgeSelection).toHaveBeenCalledWith(-10);
    expect(ctx.editing.copy).toHaveBeenCalledTimes(1);
    expect(ctx.addMarker).toHaveBeenCalledWith('Marker');
  });

  it('runs the seven page actions through setWorkspace in bar order', () => {
    const ctx = context();
    const pages: [KeyAction, string][] = [
      ['pageMedia', 'media'], ['pageCut', 'cut'], ['pageEdit', 'edit'], ['pageCompose', 'compose'],
      ['pageColour', 'color'], ['pageAudio', 'audio'], ['pageDeliver', 'deliver'],
    ];
    for (const [id] of pages) runCommand(id, ctx);
    expect(ctx.setWorkspace).toHaveBeenNthCalledWith(1, 'media');
    expect(ctx.setWorkspace).toHaveBeenNthCalledWith(2, 'cut');
    expect(ctx.setWorkspace).toHaveBeenNthCalledWith(3, 'edit');
    expect(ctx.setWorkspace).toHaveBeenNthCalledWith(4, 'compose');
    expect(ctx.setWorkspace).toHaveBeenNthCalledWith(5, 'color');
    expect(ctx.setWorkspace).toHaveBeenNthCalledWith(6, 'audio');
    expect(ctx.setWorkspace).toHaveBeenNthCalledWith(7, 'deliver');
    expect(PAGE_COMMANDS).toMatchObject({ pageMedia: 'media', pageColour: 'color', pageDeliver: 'deliver' });
  });

  it('opens the palette from its own command (and the cheatsheet stays the ? path)', () => {
    const ctx = context();
    runCommand('commandPalette', ctx);
    expect(ctx.openPalette).toHaveBeenCalledTimes(1);
  });

  it('reports availability: undo/redo/paste/selection', () => {
    const empty = storeWith();
    const state = { store: empty, canUndo: false, canRedo: false, hasClipboard: false };
    expect(commandUnavailable('undo', state)).toBe('nothingToUndo');
    expect(commandUnavailable('redo', state)).toBe('nothingToRedo');
    expect(commandUnavailable('pasteClips', state)).toBe('nothingToPaste');
    expect(commandUnavailable('deleteSelection', state)).toBe('selectionRequired');
    expect(commandUnavailable('playPause', state)).toBeNull();
    const selected = storeWith(['c1']);
    expect(commandUnavailable('deleteSelection', { ...state, store: selected, canUndo: true, hasClipboard: true })).toBeNull();
  });

  it('a stale id is a no-op', () => {
    const ctx = context();
    expect(() => runCommand('notAnAction' as KeyAction, ctx)).not.toThrow();
  });
});
