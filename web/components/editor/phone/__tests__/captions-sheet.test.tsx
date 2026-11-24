// @vitest-environment jsdom
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createEditorStore, undoStep, useEditorStore, type EditorStore } from '@/lib/editing/store';
import { caption, doc as makeDoc, media, track } from '@/lib/editing/__tests__/fixtures';
import type { EditOp, PropertyKey, PropertyValue } from '@/lib/editing/engine';
import type { Caption, EditTimeline } from '@/lib/editing/types';
import { CaptionsSheet } from '../CaptionsSheet';
import { WordPicker, WORD_LONG_PRESS_MS } from '../WordPicker';
import { moveEdgeTo, orderedRange, phoneMoreToolTabs, stepRange } from '../captions-logic';

// jsdom has no PointerEvent; a MouseEvent carries the coordinates the picker reads.
if (typeof window !== 'undefined' && !('PointerEvent' in window)) {
  class PointerEventShim extends MouseEvent {
    pointerId: number;
    constructor(type: string, init: MouseEventInit & { pointerId?: number } = {}) {
      super(type, init);
      this.pointerId = init.pointerId ?? 1;
    }
  }
  Object.defineProperty(window, 'PointerEvent', { value: PointerEventShim, configurable: true });
}

// Six words, half a second each, on a 30 fps clip that starts at source second 0.
vi.mock('@/lib/editing/api/analysis', () => ({
  useTimelineAnalysis: () => ({
    isLoading: false,
    error: undefined,
    data: {
      sources: [],
      artifact: {
        words: ['One', 'two', 'three', 'four', 'five', 'six'].map((text, i) => ({ id: `w${i}`, startSec: i * 0.5, endSec: i * 0.5 + 0.5, text, sourceIndex: 0 })),
      },
    },
  }),
  sourceMatcher: () => () => true,
}));

beforeEach(() => {
  (globalThis as { ResizeObserver?: unknown }).ResizeObserver ??= class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
  Element.prototype.scrollIntoView = vi.fn();
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

const captionsDoc = (): EditTimeline =>
  makeDoc([
    track('v1', 'video', [media('c1', 0, 300, 0)]),
    track('subs', 'video', [caption('k1', 0, 60, 'Hello there my friend'), caption('k2', 60, 120, 'Second one')]),
  ]);

let counter = 0;
const newId = () => `new-${++counter}`;

function SheetHarness({ store, playhead = 30, onRemoveSilence = vi.fn(), onCutToBeat = vi.fn(), moreTools }: {onCutToBeat?: () => void;  store: EditorStore; playhead?: number; onRemoveSilence?: () => void; moreTools?: React.ReactNode }) {
  const d = useEditorStore(store, (s) => s.doc);
  return (
    <MantineProvider>
      <CaptionsSheet
        doc={d}
        projectId="p"
        seedStepResultId={null}
        readOnly={false}
        playheadFrame={playhead}
        newItemId={newId}
        newTrackId={newId}
        onApply={(ops: readonly EditOp[], label: string) => store.dispatchBatch(ops, label)}
        onSetProperty={(itemId: string, key: PropertyKey, value: PropertyValue, label?: string) => store.dispatch({ type: 'setProperty', itemId, key, value } as EditOp, label ?? 'Set')}
        onSeek={vi.fn()}
        onUndo={() => void undoStep(store)}
        onRemoveSilence={onRemoveSilence}
        onCutToBeat={onCutToBeat}
        moreTools={moreTools}
      />
    </MantineProvider>
  );
}
const make = (d: EditTimeline) => createEditorStore({ timelineId: 't', projectId: 'p', doc: d, revision: 1 });
const captionsOf = (store: EditorStore): Caption[] => store.getState().doc.tracks.flatMap((t) => t.items).filter((i): i is Caption => i.type === 'caption');

describe('Captions sheet', () => {
  it('has a 44px Cut to the music button that opens the dialog', () => {
    const open = vi.fn();
    render(<SheetHarness store={make(captionsDoc())} onCutToBeat={open} />);
    const button = screen.getByTestId('phone-captions-cut-to-beat');
    fireEvent.click(button);
    expect(open).toHaveBeenCalledTimes(1);
    expect(button.outerHTML).toContain('2.75rem');
  });

  it('lists the captions on the timeline with their times', () => {
    render(<SheetHarness store={make(captionsDoc())} />);
    expect(screen.getByTestId('phone-caption-text-k1').textContent).toBe('Hello there my friend');
    expect(screen.getByTestId('phone-caption-text-k2').textContent).toBe('Second one');
  });

  it('Edit opens a full-width field and Done saves the text into the document', () => {
    const store = make(captionsDoc());
    render(<SheetHarness store={store} />);
    fireEvent.click(screen.getByTestId('phone-caption-edit-k2'));
    const field = screen.getByTestId('caption-edit-text') as HTMLTextAreaElement;
    expect(field.value).toBe('Second one');
    fireEvent.change(field, { target: { value: 'Brand new words' } });
    expect(captionsOf(store).find((c) => c.id === 'k2')?.text).toBe('Second one');
    fireEvent.click(screen.getByTestId('caption-edit-done'));
    expect(captionsOf(store).find((c) => c.id === 'k2')?.text).toBe('Brand new words');
    expect(screen.queryByTestId('caption-edit-sheet')).toBeNull();
  });

  it('Cancel leaves the text alone and an empty caption is refused with a sentence', () => {
    const store = make(captionsDoc());
    render(<SheetHarness store={store} />);
    fireEvent.click(screen.getByTestId('phone-caption-edit-k1'));
    fireEvent.change(screen.getByTestId('caption-edit-text'), { target: { value: '   ' } });
    fireEvent.click(screen.getByTestId('caption-edit-done'));
    expect(screen.getByTestId('caption-edit-problem').textContent).toMatch(/cannot be empty/);
    fireEvent.click(screen.getByTestId('caption-edit-cancel'));
    expect(captionsOf(store).find((c) => c.id === 'k1')?.text).toBe('Hello there my friend');
  });

  it('Split cuts a caption at the playhead and Merge joins it with the next, each one undo step', () => {
    const store = make(captionsDoc());
    render(<SheetHarness store={store} playhead={30} />);
    fireEvent.click(screen.getByTestId('phone-caption-split-k1'));
    expect(captionsOf(store)).toHaveLength(3);
    act(() => void undoStep(store));
    expect(captionsOf(store)).toHaveLength(2);
    fireEvent.click(screen.getByTestId('phone-caption-merge-k1'));
    expect(captionsOf(store)).toHaveLength(1);
    expect(captionsOf(store)[0].text).toContain('Second one');
    act(() => void undoStep(store));
    expect(captionsOf(store)).toHaveLength(2);
  });

  it('Split is off when the playhead is outside the caption, and the last caption cannot merge', () => {
    render(<SheetHarness store={make(captionsDoc())} playhead={200} />);
    expect((screen.getByTestId('phone-caption-split-k1') as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByTestId('phone-caption-merge-k2') as HTMLButtonElement).disabled).toBe(true);
  });

  it('Delete removes the caption without moving the others, and one undo brings it back', () => {
    const store = make(captionsDoc());
    render(<SheetHarness store={store} />);
    fireEvent.click(screen.getByTestId('phone-caption-delete-k1'));
    expect(captionsOf(store).map((c) => c.id)).toEqual(['k2']);
    expect(captionsOf(store)[0].recordIn).toBe(60);
    act(() => void undoStep(store));
    expect(captionsOf(store).map((c) => c.id)).toEqual(['k1', 'k2']);
  });

  it('Add at the playhead puts a new caption on the timeline', () => {
    const store = make(makeDoc([track('v1', 'video', [media('c1', 0, 300, 0)])]));
    render(<SheetHarness store={store} playhead={30} />);
    expect(screen.getByTestId('phone-captions-none')).toBeTruthy();
    fireEvent.click(screen.getByTestId('phone-captions-add'));
    fireEvent.change(screen.getByTestId('caption-edit-text'), { target: { value: 'Typed by hand' } });
    fireEvent.click(screen.getByTestId('caption-edit-done'));
    expect(captionsOf(store).map((c) => c.text)).toEqual(['Typed by hand']);
    expect(captionsOf(store)[0].recordIn).toBe(30);
  });

  it('Generate from speech builds captions from the transcript in one undo step', () => {
    const store = make(makeDoc([track('v1', 'video', [media('c1', 0, 300, 0)])]));
    render(<SheetHarness store={store} />);
    fireEvent.click(screen.getByTestId('phone-captions-generate'));
    expect(captionsOf(store).length).toBeGreaterThan(0);
    expect(captionsOf(store).map((c) => c.text).join(' ')).toContain('One');
    act(() => void undoStep(store));
    expect(captionsOf(store)).toHaveLength(0);
  });

  it('Remove silence is one tap away and Delete words opens the word list', () => {
    const open = vi.fn();
    render(<SheetHarness store={make(captionsDoc())} onRemoveSilence={open} />);
    fireEvent.click(screen.getByTestId('phone-captions-remove-silence'));
    expect(open).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByTestId('phone-captions-delete-words'));
    expect(screen.getByTestId('word-picker')).toBeTruthy();
    expect(screen.getByTestId('word-0').textContent).toBe('One');
  });
});

describe('More tools on a phone (x1)', () => {
  it('never lists Takes or Compare', () => {
    const tabs = ['media', 'markers', 'analyse', 'takes', 'transcript', 'compare', 'subtitles'].map((value) => ({ value }));
    expect(phoneMoreToolTabs(tabs).map((t) => t.value)).toEqual(['media', 'markers', 'analyse', 'transcript', 'subtitles']);
  });
});

describe('More tools', () => {
  it('keeps the desktop panels one tap away, closed until asked for', () => {
    render(<SheetHarness store={make(captionsDoc())} moreTools={<div data-testid="desktop-panels">panels</div>} />);
    expect(screen.queryByTestId('desktop-panels')).toBeNull();
    fireEvent.click(screen.getByTestId('phone-captions-more'));
    expect(screen.getByTestId('desktop-panels')).toBeTruthy();
    fireEvent.click(screen.getByTestId('phone-captions-more'));
    expect(screen.queryByTestId('desktop-panels')).toBeNull();
  });
});

describe('word range helpers', () => {
  it('orders, steps and drags the ends without crossing', () => {
    expect(orderedRange(4, 1)).toEqual({ start: 1, end: 4 });
    expect(stepRange({ start: 2, end: 2 }, 'start', 1, 6)).toEqual({ start: 2, end: 2 });
    expect(stepRange({ start: 2, end: 2 }, 'end', 1, 6)).toEqual({ start: 2, end: 3 });
    expect(stepRange({ start: 0, end: 3 }, 'start', -1, 6)).toEqual({ start: 0, end: 3 });
    expect(moveEdgeTo({ start: 2, end: 4 }, 'start', 5, 6)).toEqual({ start: 4, end: 4 });
    expect(moveEdgeTo({ start: 2, end: 4 }, 'end', 99, 6)).toEqual({ start: 2, end: 5 });
  });
});

function PickerHarness({ store }: { store: EditorStore }) {
  const d = useEditorStore(store, (s) => s.doc);
  return (
    <MantineProvider>
      <WordPicker doc={d} projectId="p" seedStepResultId={null} readOnly={false} newItemId={newId} onApply={(ops, label) => store.dispatchBatch(ops, label)} onSeek={vi.fn()} />
    </MantineProvider>
  );
}
const clipEnd = (store: EditorStore) => store.getState().doc.tracks.find((t) => t.id === 'v1')!.items.reduce((m, i) => Math.max(m, i.recordOut), 0);
const soloDoc = () => makeDoc([track('v1', 'video', [media('c1', 0, 300, 0)])]);

describe('Delete words', () => {
  it('Select words, then tap the first and last word, selects the range between them', () => {
    render(<PickerHarness store={make(soloDoc())} />);
    fireEvent.click(screen.getByTestId('word-picker-select'));
    fireEvent.click(screen.getByTestId('word-1'));
    fireEvent.click(screen.getByTestId('word-3'));
    expect(screen.getByTestId('word-picker-summary').textContent).toContain('3 words selected');
    expect(screen.getByTestId('word-handle-start')).toBeTruthy();
    expect(screen.getByTestId('word-handle-end')).toBeTruthy();
  });

  it('the arrow buttons move the ends, and Delete ripple-cuts those words in one undo step', () => {
    const store = make(soloDoc());
    render(<PickerHarness store={store} />);
    const before = clipEnd(store);
    fireEvent.click(screen.getByTestId('word-picker-select'));
    fireEvent.click(screen.getByTestId('word-1'));
    fireEvent.click(screen.getByTestId('word-end-later'));
    expect(screen.getByTestId('word-picker-summary').textContent).toContain('2 words selected');
    fireEvent.click(screen.getByTestId('word-picker-delete'));
    expect(clipEnd(store)).toBeLessThan(before);
    expect(screen.getByTestId('word-picker-message').textContent).toMatch(/Deleted 2 words/);
    act(() => void undoStep(store));
    expect(clipEnd(store)).toBe(before);
    expect(store.getState().doc.tracks.find((t) => t.id === 'v1')!.items).toHaveLength(1);
  });

  it('a 500 ms press on a word starts a selection with handles; releasing early only moves the playhead', () => {
    vi.useFakeTimers();
    render(<PickerHarness store={make(soloDoc())} />);
    const word = screen.getByTestId('word-2');
    fireEvent.pointerDown(word, { clientX: 10, clientY: 10 });
    act(() => void vi.advanceTimersByTime(WORD_LONG_PRESS_MS - 50));
    fireEvent.pointerUp(word);
    fireEvent.click(word);
    expect(screen.queryByTestId('word-handle-start')).toBeNull();

    fireEvent.pointerDown(word, { clientX: 10, clientY: 10 });
    act(() => void vi.advanceTimersByTime(WORD_LONG_PRESS_MS + 10));
    expect(screen.getByTestId('word-picker-summary').textContent).toContain('1 word selected');
    fireEvent.pointerUp(word);
    fireEvent.click(word); // the click that ends a long press is swallowed: it must not clear the selection
    expect(screen.getByTestId('word-picker-summary').textContent).toContain('1 word selected');
  });

  it('moving the finger cancels the long press', () => {
    vi.useFakeTimers();
    render(<PickerHarness store={make(soloDoc())} />);
    const word = screen.getByTestId('word-2');
    fireEvent.pointerDown(word, { clientX: 10, clientY: 10 });
    fireEvent.pointerMove(word, { clientX: 60, clientY: 10 });
    act(() => void vi.advanceTimersByTime(WORD_LONG_PRESS_MS + 10));
    expect(screen.queryByTestId('word-picker-summary')).toBeNull();
  });

  it('dragging the end handle onto a later word extends the selection', () => {
    const proto = HTMLElement.prototype as unknown as { setPointerCapture: () => void; hasPointerCapture: () => boolean };
    proto.setPointerCapture = () => undefined;
    proto.hasPointerCapture = () => true;
    render(<PickerHarness store={make(soloDoc())} />);
    fireEvent.click(screen.getByTestId('word-picker-select'));
    fireEvent.click(screen.getByTestId('word-1'));
    const target = screen.getByTestId('word-4');
    const original = document.elementFromPoint;
    document.elementFromPoint = () => target;
    const handle = screen.getByTestId('word-handle-end');
    fireEvent.pointerDown(handle, { pointerId: 1, clientX: 5, clientY: 5 });
    fireEvent.pointerMove(handle, { pointerId: 1, clientX: 200, clientY: 5 });
    document.elementFromPoint = original;
    expect(screen.getByTestId('word-picker-summary').textContent).toContain('4 words selected');
  });
});
