import { describe, expect, it, vi } from 'vitest';
import { commandUnavailable, runCommand, type CommandContext, type SourceTransport } from '../commands';
import { createEditorStore } from '../store';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';

function setup(ui: Partial<Parameters<ReturnType<typeof createEditorStore>['setUi']>[0]> = {}) {
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [media('c1', 0, 60, 0)])]), revision: 1 });
  store.setUi({ playheadFrame: 10, workspace: 'cut', viewerMode: 'source', viewerSource: 'file:a', ...ui });
  const source: SourceTransport = {
    togglePlay: vi.fn(), shuttleReverse: vi.fn(), stop: vi.fn(), shuttleForward: vi.fn(), step: vi.fn(),
    goToStart: vi.fn(), goToEnd: vi.fn(), markIn: vi.fn(), markOut: vi.fn(), clearMarks: vi.fn(),
  };
  const ctx: CommandContext = {
    store, endFrame: 100, undo: vi.fn(), redo: vi.fn(), addMarker: vi.fn(), liftSelection: vi.fn(), extractSelection: vi.fn(),
    nudgeSelection: vi.fn(), bladeAtPlayhead: vi.fn(), playInToOut: vi.fn(() => true), setMarks: vi.fn(),
    editing: { copy: vi.fn(), cut: vi.fn(), paste: vi.fn(), duplicate: vi.fn(), linkSelected: vi.fn(), unlinkSelected: vi.fn(), moveToTrack: vi.fn() },
    setWorkspace: vi.fn(), openCheatsheet: vi.fn(), openPalette: vi.fn(), source,
  };
  return { store, ctx, source };
}

describe('transport commands while Source shows (T-017)', () => {
  it('acts on the source and leaves the timeline playhead, speed, marks and document alone', () => {
    const { store, ctx, source } = setup();
    const revision = store.getState().history.length;
    for (const id of ['playPause', 'shuttleReverse', 'stop', 'shuttleForward', 'frameBack', 'frameForward10', 'goToStart', 'goToEnd', 'markIn', 'markOut', 'clearInOut'] as const) runCommand(id, ctx);
    expect(source.togglePlay).toHaveBeenCalledTimes(1);
    expect(source.shuttleReverse).toHaveBeenCalledTimes(1);
    expect(source.stop).toHaveBeenCalledTimes(1);
    expect(source.shuttleForward).toHaveBeenCalledTimes(1);
    expect(source.step).toHaveBeenNthCalledWith(1, -1);
    expect(source.step).toHaveBeenNthCalledWith(2, 10);
    expect(source.markIn).toHaveBeenCalledTimes(1);
    expect(source.markOut).toHaveBeenCalledTimes(1);
    expect(source.clearMarks).toHaveBeenCalledTimes(1);
    expect(ctx.setMarks).not.toHaveBeenCalled();
    expect(store.getState().ui.playheadFrame).toBe(10);
    expect(store.getState().ui.playbackSpeed).toBe(0);
    expect(store.getState().history.length).toBe(revision);
  });

  it('timeline-only jumps and markers do nothing in Source', () => {
    const { store, ctx } = setup();
    for (const id of ['nextEdit', 'previousEdit', 'nextMarker', 'previousMarker', 'addMarker'] as const) runCommand(id, ctx);
    expect(ctx.addMarker).not.toHaveBeenCalled();
    expect(store.getState().ui.playheadFrame).toBe(10);
  });

  it('behaves exactly as before in Record', () => {
    const { store, ctx, source } = setup({ viewerMode: 'record' });
    runCommand('playPause', ctx);
    runCommand('markIn', ctx);
    expect(source.togglePlay).not.toHaveBeenCalled();
    expect(store.getState().ui.playbackSpeed).toBe(1);
    expect(ctx.setMarks).toHaveBeenCalledWith({ inFrame: 10, outFrame: null });
  });

  it('entering a picture tool returns to Record, then runs the tool', () => {
    const { store, ctx } = setup({ workspace: 'compose' });
    runCommand('toolMask', ctx);
    expect(store.getState().ui.viewerMode).toBe('record');
  });

  it('the switch chord toggles on Cut and Edit only, and the palette says why it is off elsewhere or on a phone', () => {
    const { store, ctx } = setup({ viewerMode: 'record' });
    runCommand('toggleViewerMode', ctx);
    expect(store.getState().ui.viewerMode).toBe('source');
    runCommand('toggleViewerMode', ctx);
    expect(store.getState().ui.viewerMode).toBe('record');
    store.setUi({ workspace: 'color' });
    runCommand('toggleViewerMode', ctx);
    expect(store.getState().ui.viewerMode).toBe('record');
    const state = { store, canUndo: false, canRedo: false, hasClipboard: false };
    expect(commandUnavailable('toggleViewerMode', state)).toBe('viewerNeedsPage');
    expect(commandUnavailable('toggleViewerMode', { ...state, phone: true })).toBe('desktopOnly');
    store.setUi({ workspace: 'edit' });
    expect(commandUnavailable('toggleViewerMode', state)).toBeNull();
  });
});
