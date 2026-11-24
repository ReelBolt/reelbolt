// @vitest-environment jsdom
import React, { useCallback, useState } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createEditorStore, undoStep, useEditorStore, type EditorStore } from '@/lib/editing/store';
import { doc as makeDoc, media, standardDoc, track } from '@/lib/editing/__tests__/fixtures';
import { linkedOpsForNudge } from '@/lib/editing/link';
import type { EditTimeline, MediaClip } from '@/lib/editing/types';
import { useTimelineEditing } from '../../timeline/useTimelineEditing';
import { PhoneActionBar } from '../PhoneActionBar';
import { selectModeTap } from '../phone-actions';
import { usePhoneActions } from '../usePhoneActions';
import { UndoSnackbar, resetUndoSnackbar } from '../UndoSnackbar';
import { resetSheetHistory } from '../sheet-history';

beforeEach(() => {
  resetSheetHistory();
  (globalThis as { ResizeObserver?: unknown }).ResizeObserver ??= class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
});
afterEach(async () => {
  cleanup();
  await new Promise((r) => setTimeout(r, 60));
  resetUndoSnackbar();
});

function Harness({ store, onAddMedia = () => {} }: { store: EditorStore; onAddMedia?: () => void }) {
  const doc = useEditorStore(store, (s) => s.doc);
  const ui = useEditorStore(store, (s) => s.ui);
  const [selectMode, setSelectMode] = useState(false);
  const split = useCallback(() => {
    const s = store.getState();
    for (const id of s.ui.selection) store.dispatch({ type: 'blade', itemId: id, atFrame: s.ui.playheadFrame, newItemId: `${id}-right` }, 'Split');
  }, [store]);
  const nudge = useCallback((d: number) => store.dispatchBatch(linkedOpsForNudge(store.getState().doc, store.getState().ui.selection, d), 'Nudge'), [store]);
  const editing = useTimelineEditing({ store, readOnly: false, doc, pool: undefined, split, openInspector: () => {}, openReplace: () => {} });
  const actions = usePhoneActions({
    store,
    doc,
    ui,
    readOnly: false,
    editing,
    split,
    nudge,
    extractAudio: () => {},
    undo: () => void undoStep(store),
    addMarker: () => {},
    setInOut: (m) => store.setInOutMarks(m, { persist: false }),
    selectMode,
    setSelectMode,
  });
  return (
    <MantineProvider>
      <PhoneActionBar actions={actions} onAddMedia={onAddMedia} />
      <UndoSnackbar />
    </MantineProvider>
  );
}

const make = (d: EditTimeline = standardDoc()) => createEditorStore({ timelineId: 't', projectId: 'p', doc: d, revision: 1 });
const item = (store: EditorStore, id: string) => store.getState().doc.tracks.flatMap((t) => t.items).find((i) => i.id === id) as MediaClip | undefined;
const tap = (id: string) => fireEvent.click(screen.getByTestId(id));
const select = (store: EditorStore, ids: string[]) => act(() => store.setUi({ selection: ids }));

describe('RM.5 action bar states and layout', () => {
  it('nothing selected: Add media, Add text, Marker, Select; the bar is 52 px and each button 44 px', () => {
    const onAddMedia = vi.fn();
    render(<Harness store={make()} onAddMedia={onAddMedia} />);
    const bar = screen.getByTestId('phone-action-bar');
    expect(bar.style.height).toBe('52px');
    expect(bar.dataset.mode).toBe('none');
    for (const id of ['add-media', 'add-text', 'marker', 'select']) {
      const b = screen.getByTestId(`phone-action-${id}`);
      expect(b.style.height).toBe('44px');
      expect(b.style.minWidth).toBe('44px');
    }
    tap('phone-action-add-media');
    expect(onAddMedia).toHaveBeenCalled();
  });
  it('a clip selected: Split, Duplicate, Trim, Nudge, More, then Delete after a gap', () => {
    const store = make();
    render(<Harness store={store} />);
    select(store, ['c1']);
    const ids = Array.from(screen.getByTestId('phone-action-bar').querySelectorAll('[data-testid^="phone-action-"]')).map((e) => e.getAttribute('data-testid'));
    expect(ids).toEqual(['phone-action-split', 'phone-action-duplicate', 'phone-action-trim', 'phone-action-nudge', 'phone-action-more', 'phone-action-delete']);
    expect(screen.getByTestId('phone-action-bar').children[5].getAttribute('aria-hidden')).toBe('true'); // the spacer before Delete
  });
  it('Add text puts a title at the playhead and selects it', () => {
    const store = make();
    render(<Harness store={store} />);
    act(() => store.setUi({ playheadFrame: 100 }));
    tap('phone-action-add-text');
    const sel = store.getState().ui.selection;
    expect(sel.length).toBe(1);
    expect(item(store, sel[0])?.type).toBe('titleClip');
  });
});

describe('RM.5 actions change the document (one undo step each)', () => {
  it('Delete is a ripple delete and shows the 6 second Undo that restores the clip', () => {
    const store = make();
    render(<Harness store={store} />);
    select(store, ['c1']);
    tap('phone-action-delete');
    expect(item(store, 'c1')).toBeUndefined();
    expect(item(store, 'c2')!.recordIn).toBe(0);
    expect(screen.getByTestId('undo-snackbar').textContent).toContain('Clip deleted');
    tap('undo-snackbar-undo');
    expect(item(store, 'c1')).toBeDefined();
    expect(item(store, 'c2')!.recordIn).toBe(60);
  });
  it('Split cuts at the playhead', () => {
    const store = make();
    render(<Harness store={store} />);
    act(() => store.setUi({ playheadFrame: 30 }));
    select(store, ['c1']);
    tap('phone-action-split');
    expect(item(store, 'c1')!.recordOut).toBe(30);
    expect(item(store, 'c1-right')!.recordIn).toBe(30);
  });
  it('Duplicate adds a copy and selects it', () => {
    const store = make();
    render(<Harness store={store} />);
    select(store, ['c3']);
    const before = store.getState().doc.tracks.flatMap((t) => t.items).length;
    tap('phone-action-duplicate');
    expect(store.getState().doc.tracks.flatMap((t) => t.items).length).toBe(before + 1);
    expect(store.getState().ui.selection).not.toContain('c3');
  });
  it('Nudge +10 moves the clip ten frames later', () => {
    const store = make();
    render(<Harness store={store} />);
    select(store, ['c3']);
    tap('phone-action-nudge');
    tap('phone-nudge+10');
    expect(item(store, 'c3')!.recordIn).toBe(160);
  });
  it('More: Lift leaves a gap and offers Undo; Move later trades places with the next clip', () => {
    const store = make();
    render(<Harness store={store} />);
    select(store, ['c1']);
    tap('phone-action-more');
    tap('phone-more-move-later');
    expect(item(store, 'c2')!.recordIn).toBe(0);
    expect(item(store, 'c1')!.recordIn).toBe(60);
    select(store, ['c3']);
    tap('phone-action-more');
    tap('phone-more-lift');
    expect(item(store, 'c3')).toBeUndefined();
    expect(screen.getByTestId('undo-snackbar').textContent).toContain('lifted');
  });
  it('Move earlier on the first clip says so in plain words and changes nothing', () => {
    const store = make();
    render(<Harness store={store} />);
    select(store, ['c1']);
    const cursor = store.getState().cursor;
    tap('phone-action-more');
    tap('phone-more-move-earlier');
    expect(store.getState().cursor).toBe(cursor);
  });
});

describe('RM.5 trim sheet', () => {
  it('steppers apply live and the whole session is ONE undo step', async () => {
    const store = make();
    render(<Harness store={store} />);
    select(store, ['c2']);
    const start = store.getState().cursor;
    tap('phone-action-trim');
    tap('trim-end-10');
    tap('trim-end-1');
    expect(item(store, 'c2')!.recordOut).toBe(109);
    expect(store.getState().cursor - start).toBe(2);
    tap('trim-done');
    expect(store.getState().cursor - start).toBe(1);
    expect(item(store, 'c2')!.recordOut).toBe(109);
    act(() => void undoStep(store));
    expect(item(store, 'c2')!.recordOut).toBe(120);
  });
  it('mode chips set the trim mode (ripple by default)', () => {
    const store = make();
    render(<Harness store={store} />);
    select(store, ['c2']);
    tap('phone-action-trim');
    expect(screen.getByTestId('trim-mode-chip-ripple').getAttribute('aria-pressed')).toBe('true');
    tap('trim-mode-chip-slip');
    expect(store.getState().ui.trimMode).toBe('slip');
  });
  it('a refused step says why in the sheet and changes nothing', () => {
    const store = make();
    render(<Harness store={store} />);
    select(store, ['c1']);
    tap('phone-action-trim');
    const cursor = store.getState().cursor;
    tap('trim-start-10');
    expect(store.getState().cursor).toBe(cursor);
    expect(screen.getByTestId('phone-sheet-problem').textContent).toContain('cannot move');
  });
});

describe('RM.5 speed and volume chips', () => {
  it('2x halves the clip', () => {
    const store = make();
    render(<Harness store={store} />);
    select(store, ['c1']);
    tap('phone-action-more');
    tap('phone-more-speed');
    tap('phone-speed-2');
    expect(item(store, 'c1')!.playbackRate).toBe(2);
    expect(item(store, 'c1')!.recordOut).toBe(30);
  });
  it('Mute writes the muted floor on a sound clip', () => {
    const store = make();
    render(<Harness store={store} />);
    select(store, ['a1']);
    tap('phone-action-more');
    tap('phone-more-volume');
    tap('phone-volume-mute');
    expect(item(store, 'a1')!.gainDb?.value).toBe(-60);
  });
});

describe('R07.7 More menu and the one clip sheet', () => {
  it('More offers no transition, and Speed, Volume and the picture entry all open the same clip sheet', () => {
    const store = make();
    render(<Harness store={store} />);
    select(store, ['c1']);
    tap('phone-action-more');
    expect(screen.queryByText(/transition/i)).toBeNull();
    tap('phone-more-clip');
    expect(screen.getByTestId('clip-sheet')).toBeTruthy();
    expect(screen.getByTestId('phone-action-sheet-clip')).toBeTruthy();
    expect(screen.getByTestId('clip-fit')).toBeTruthy();
    expect(screen.getByTestId('speed-sheet')).toBeTruthy();
    expect(screen.getByTestId('volume-sheet')).toBeTruthy();
  });
});

describe('RM.5 Select mode', () => {
  it('taps build a group; Delete removes a linked pair together; Clear empties it', () => {
    const d = makeDoc([track('v1', 'video', [media('v', 0, 60, 0, { linkId: 'L' }), media('w', 60, 120)]), track('a1', 'audio', [media('a', 0, 60, 0, { linkId: 'L' })])]);
    const store = make(d);
    render(<Harness store={store} />);
    tap('phone-action-select');
    expect(screen.getByTestId('phone-action-bar').dataset.mode).toBe('select');
    act(() => store.setUi({ selection: selectModeTap(store.getState().doc, store.getState().ui.selection, 'v') ?? [] }));
    expect(store.getState().ui.selection.sort()).toEqual(['a', 'v']); // the sound comes with the picture
    act(() => store.setUi({ selection: selectModeTap(store.getState().doc, store.getState().ui.selection, 'w') ?? [] }));
    expect(store.getState().ui.selection.length).toBe(3);
    expect(screen.getByTestId('phone-action-select-done').textContent).toContain('3');
    tap('phone-action-select-clear');
    expect(store.getState().ui.selection).toEqual([]);
    expect(selectModeTap(store.getState().doc, [], null)).toBeNull(); // a tap on empty space keeps the group
    act(() => store.setUi({ selection: ['v', 'a', 'w'] }));
    tap('phone-action-delete');
    expect(item(store, 'v')).toBeUndefined();
    expect(item(store, 'a')).toBeUndefined();
    expect(item(store, 'w')).toBeUndefined();
  });
  it('a tap takes a selected clip out again', () => {
    const store = make();
    const once = selectModeTap(store.getState().doc, [], 'c1')!;
    expect(once).toEqual(['c1']);
    expect(selectModeTap(store.getState().doc, once, 'c1')).toEqual([]);
  });
});
