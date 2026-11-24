import { describe, expect, it, vi } from 'vitest';
import { SIMPLE_DECISIONS, commandUnavailable, runCommand, type CommandContext } from '../commands';
import { KEY_ACTIONS, type KeyAction } from '../keymap';
import { createEditorStore } from '../store';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';

function storeWith(selection: string[] = []) {
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [media('c1', 0, 60, 0)])]), revision: 1 });
  store.setUi({ selection, playheadFrame: 10, workspace: 'edit' });
  return store;
}

function simpleContext(store = storeWith(), event?: Pick<KeyboardEvent, 'preventDefault'>) {
  const simple = { setPlace: vi.fn(), openFull: vi.fn() };
  const ctx: CommandContext = {
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
    editing: { copy: vi.fn(), cut: vi.fn(), paste: vi.fn(), duplicate: vi.fn(), linkSelected: vi.fn(), unlinkSelected: vi.fn(), moveToTrack: vi.fn() },
    setWorkspace: vi.fn(),
    openCheatsheet: vi.fn(),
    openPalette: vi.fn(),
    simple,
    event,
  };
  return { ctx, simple };
}

describe('what each action does in the Simple shell', () => {
  it('every keymap action has a decision', () => {
    for (const info of KEY_ACTIONS) expect(SIMPLE_DECISIONS[info.id], info.id).toMatch(/^(works|opensFull|off)$/);
    expect(Object.keys(SIMPLE_DECISIONS).sort()).toEqual(KEY_ACTIONS.map((a) => a.id).sort());
  });

  it('an action that works runs as in the full editor', () => {
    const { ctx, simple } = simpleContext(storeWith(['c1']));
    runCommand('bladeAtPlayhead', ctx);
    expect(ctx.bladeAtPlayhead).toHaveBeenCalledTimes(1);
    runCommand('undo', ctx);
    expect(ctx.undo).toHaveBeenCalledTimes(1);
    expect(simple.openFull).not.toHaveBeenCalled();
  });

  it('an action that needs the full editor asks the host, saying whether a key was pressed, and changes nothing itself', () => {
    const store = storeWith(['c1']);
    const event = { preventDefault: vi.fn() };
    const { ctx, simple } = simpleContext(store, event);
    runCommand('pageColour', ctx);
    expect(simple.openFull).toHaveBeenCalledWith('pageColour', true);
    expect(ctx.setWorkspace).not.toHaveBeenCalled();
    expect(event.preventDefault).toHaveBeenCalled();
    const fromPalette = simpleContext(store);
    runCommand('toggleEventsLane', fromPalette.ctx);
    expect(fromPalette.simple.openFull).toHaveBeenCalledWith('toggleEventsLane', false);
  });

  it('the two page actions the Simple shell has are its own places', () => {
    const { ctx, simple } = simpleContext();
    runCommand('pageDeliver', ctx);
    expect(simple.setPlace).toHaveBeenCalledWith('export');
    runCommand('pageEdit', ctx);
    expect(simple.setPlace).toHaveBeenCalledWith('edit');
    expect(ctx.setWorkspace).not.toHaveBeenCalled();
  });

  it('an action that is off does nothing, and the palette says why', () => {
    const store = storeWith();
    const { ctx, simple } = simpleContext(store);
    runCommand('toggleViewerMode', ctx);
    expect(store.getState().ui.viewerMode).not.toBe('source');
    expect(simple.openFull).not.toHaveBeenCalled();
    const state = { store, canUndo: true, canRedo: true, hasClipboard: true, simple: true };
    expect(commandUnavailable('toggleViewerMode', state)).toBe('fullEditorOnly');
    expect(commandUnavailable('placeInsert', state)).toBe('fullEditorOnly');
    // One that opens the full editor is always available: picking it is how you get there.
    expect(commandUnavailable('toolMask', state)).toBeNull();
    expect(commandUnavailable('pageAudio', state)).toBeNull();
  });

  it('outside the Simple shell nothing changes', () => {
    const { ctx } = simpleContext();
    const full: CommandContext = { ...ctx, simple: undefined };
    runCommand('pageDeliver', full);
    expect(full.setWorkspace).toHaveBeenCalledWith('deliver');
    const off: KeyAction[] = (Object.keys(SIMPLE_DECISIONS) as KeyAction[]).filter((id) => SIMPLE_DECISIONS[id] === 'off');
    expect(off.sort()).toEqual(['placeAppend', 'placeInsert', 'placeOnTop', 'placeOver', 'placeOverwrite', 'toggleViewerMode']);
  });
});
