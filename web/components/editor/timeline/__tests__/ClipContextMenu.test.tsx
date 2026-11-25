// @vitest-environment jsdom
/**
 * T-018: the right-click menu shows each item's LIVE chord. Remap Copy and the chord in the menu changes; an
 * unbound action, or a default another remap took, shows no chord at all. The long tail sits in submenus.
 */

import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ClipContextMenu, type ClipMenuItem } from '../ClipContextMenu';
import { LiveChordsProvider } from '../../prefs/live-chords';
import { liveChords } from '@/lib/editing/keymap';
import { useTimelineEditing } from '../useTimelineEditing';
import { createEditorStore } from '@/lib/editing/store';
import { renderHook } from '@testing-library/react';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;
afterEach(cleanup);

const ITEMS: ClipMenuItem[] = [
  { id: 'copy', label: 'Copy', action: 'copyClips', run: vi.fn() },
  { id: 'cut', label: 'Cut', action: 'cutClips', run: vi.fn() },
  { id: 'speed', label: 'Speed...', sub: 'arrange', dividerBefore: true, run: vi.fn() },
  { id: 'join', label: 'Join', sub: 'arrange', run: vi.fn() },
  { id: 'properties', label: 'Properties', dividerBefore: true, run: vi.fn() },
];

function show(overrides: Record<string, string>) {
  return render(
    <MantineProvider env="test">
      <LiveChordsProvider chords={liveChords(overrides)}>
        <ClipContextMenu anchor={{ x: 10, y: 10 }} items={ITEMS} onClose={() => {}} />
      </LiveChordsProvider>
    </MantineProvider>,
  );
}

describe('keyboard in a submenu', () => {
  it('ArrowLeft closes the submenu and returns focus to its entry', async () => {
    show({});
    const entry = screen.getByTestId('ctx-sub-arrange');
    fireEvent.mouseEnter(entry);
    fireEvent.keyDown(entry, { key: 'ArrowRight' });
    const inside = await screen.findByTestId('ctx-speed');
    act(() => inside.focus());
    fireEvent.keyDown(inside, { key: 'ArrowLeft' });
    expect(document.activeElement).toBe(entry);
  });
});

describe('the context menu reads the live keymap', () => {
  it('shows the default chord, then the remapped one', () => {
    const view = show({});
    expect(screen.getByTestId('ctx-copy-chord').textContent).toBe('Ctrl+C');
    view.unmount();
    show({ copyClips: 'Mod+Shift+K' });
    expect(screen.getByTestId('ctx-copy-chord').textContent).toBe('Ctrl+Shift+K');
  });

  it('shows nothing for an unbound action, or one whose default another action took', () => {
    show({ copyClips: '', cutClips: 'Mod+C' });
    expect(screen.queryByTestId('ctx-copy-chord')).toBeNull();
    // Cut now owns Mod+C; Copy has no chord left to show.
    expect(screen.getByTestId('ctx-cut-chord').textContent).toBe('Ctrl+C');
  });

  it('folds the long tail into a submenu and keeps the top level short', async () => {
    show({});
    expect(screen.getByTestId('ctx-sub-arrange')).toBeTruthy();
    expect(screen.queryByTestId('ctx-speed')).toBeNull();
    expect(screen.getByTestId('ctx-properties')).toBeTruthy();
    expect(screen.getByTestId('clip-context-menu').style.overflowY, 'scrolling is the fallback for a short window').toBe('auto');
    fireEvent.mouseEnter(screen.getByTestId('ctx-sub-arrange'));
    expect(await screen.findByTestId('ctx-speed')).toBeTruthy();
  });
});

describe('the shared clip list carries actions, not baked chords', () => {
  it('every item with a catalogue action names it, and none carries a baked shortcut string', () => {
    const document = doc([track(TRACKS.v1, 'video', [media('c1', 0, 100, 0, {})])]);
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: document, revision: 1 });
    store.setUi({ selection: ['c1'], workspace: 'edit' });
    const { result } = renderHook(() => useTimelineEditing({ store, readOnly: false, doc: document, pool: undefined, split: () => {}, openInspector: () => {}, openReplace: () => {} }));
    const items = result.current.items();
    const byId = Object.fromEntries(items.map((i) => [i.id, i]));
    expect(byId.split.action).toBe('bladeAtPlayhead');
    expect(byId.copy.action).toBe('copyClips');
    expect(byId.paste.action).toBe('pasteClips');
    expect(byId['select-forward'].action).toBe('selectForward');
    for (const item of items) expect('shortcut' in item).toBe(false);
  });

  it('the empty-lane and ruler menus list marks, a marker and (with a source) the four placements', () => {
    const document = doc([track(TRACKS.v1, 'video', [media('c1', 0, 100, 0, {})])]);
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: document, revision: 1 });
    const place = vi.fn();
    const setMarks = vi.fn();
    const addMarkerAt = vi.fn();
    const { result } = renderHook(() => useTimelineEditing({ store, readOnly: false, doc: document, pool: undefined, split: () => {}, openInspector: () => {}, openReplace: () => {}, setMarks, addMarkerAt, getPlace: () => place }));
    act(() => result.current.openMenu(0, 0, { area: 'ruler', frame: 42 }));
    const ids = result.current.pointItems().map((i) => i.id);
    expect(ids).toEqual(['point-mark-in', 'point-mark-out', 'point-add-marker', 'point-insert', 'point-overwrite', 'point-append', 'point-placeOnTop']);
    const items = result.current.pointItems();
    act(() => items.find((i) => i.id === 'point-overwrite')!.run());
    expect(place).toHaveBeenCalledWith('overwrite');
    expect(store.getState().ui.playheadFrame).toBe(42);
    act(() => items.find((i) => i.id === 'point-mark-in')!.run());
    expect(setMarks).toHaveBeenCalledWith(expect.objectContaining({ inFrame: 42 }));
    act(() => items.find((i) => i.id === 'point-add-marker')!.run());
    expect(addMarkerAt).toHaveBeenCalledWith(42);
    act(() => result.current.openMenu(0, 0, { area: 'lane', frame: 42 }));
    expect(result.current.pointItems()[0].id).toBe('point-paste');
  });

  it('with no source loaded the placements are absent', () => {
    const document = doc([track(TRACKS.v1, 'video', [media('c1', 0, 100, 0, {})])]);
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: document, revision: 1 });
    const { result } = renderHook(() => useTimelineEditing({ store, readOnly: false, doc: document, pool: undefined, split: () => {}, openInspector: () => {}, openReplace: () => {} }));
    act(() => result.current.openMenu(0, 0, { area: 'ruler', frame: 5 }));
    expect(result.current.pointItems().some((i) => i.id.startsWith('point-insert'))).toBe(false);
  });
});
