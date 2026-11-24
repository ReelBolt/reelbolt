// @vitest-environment jsdom
import React, { useCallback } from 'react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createEditorStore, undoStep, useEditorStore, type EditorStore } from '@/lib/editing/store';
import { doc as makeDoc, media, standardDoc, track } from '@/lib/editing/__tests__/fixtures';
import { linkedOpsForNudge } from '@/lib/editing/link';
import { currentLook, fixValuesOf } from '@/lib/editing/color/look-gallery';
import type { EditTimeline, MediaClip, TitleClip } from '@/lib/editing/types';
import { useTimelineEditing } from '../../timeline/useTimelineEditing';
import { ClipSheetBody } from '../ClipSheet';
import { LookSheetBody } from '../LookSheet';
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

function Harness({ store, which }: { store: EditorStore; which: 'look' | 'clip' }) {
  const doc = useEditorStore(store, (s) => s.doc);
  const ui = useEditorStore(store, (s) => s.ui);
  const split = useCallback(() => {}, []);
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
    selectMode: false,
    setSelectMode: () => {},
    sourceSizeFor: () => ({ width: 1920, height: 1080 }),
  });
  return (
    <MantineProvider>
      {which === 'look' ? <LookSheetBody actions={actions} /> : <ClipSheetBody actions={actions} />}
      <UndoSnackbar />
    </MantineProvider>
  );
}

const make = (d: EditTimeline = standardDoc()) => createEditorStore({ timelineId: 't', projectId: 'p', doc: d, revision: 1 });
const item = (store: EditorStore, id: string) => store.getState().doc.tracks.flatMap((t) => t.items).find((i) => i.id === id);
const clipOf = (store: EditorStore, id: string) => item(store, id) as MediaClip;
const tap = (id: string) => fireEvent.click(screen.getByTestId(id));
const select = (store: EditorStore, ids: string[]) => act(() => store.setUi({ selection: ids }));
/** The numeric fallback of a slider field: type, then leave the box. */
const type = (testId: string, value: string) => {
  const box = screen.getByTestId(`${testId}-number`);
  fireEvent.change(box, { target: { value } });
  fireEvent.blur(box);
};

describe('RM.10 Look and fix sheet', () => {
  it('a look word is stored on the selected clip as one undo step; strength and a fix follow', () => {
    const store = make();
    render(<Harness store={store} which="look" />);
    select(store, ['c1']);
    const before = store.getState().cursor;
    tap('look-warm');
    expect(store.getState().cursor).toBe(before + 1);
    expect(currentLook(clipOf(store, 'c1').grade)?.look).toBe('Warm');
    type('look-strength', '60');
    expect(currentLook(clipOf(store, 'c1').grade)?.strength).toBe(60);
    type('fix-brightness', '0.5');
    expect(fixValuesOf(clipOf(store, 'c1').grade).brightness).toBe(0.5);
    expect(store.getState().cursor).toBe(before + 3);
  });

  it('refuses a fix out of range in one plain sentence and changes nothing', () => {
    const store = make();
    render(<Harness store={store} which="look" />);
    select(store, ['c1']);
    const before = store.getState().cursor;
    type('fix-contrast', '9');
    expect(store.getState().cursor).toBe(before);
    expect(screen.getByTestId('fix-contrast-problem').textContent).toContain('Contrast is between');
  });

  it('Apply to all clips grades every picture clip in ONE undo step, keeping their other adjustments', () => {
    const store = make();
    render(<Harness store={store} which="look" />);
    select(store, ['c1']);
    tap('look-warm');
    type('fix-warmth', '0.4');
    const before = store.getState().cursor;
    tap('look-apply-all');
    expect(store.getState().cursor).toBe(before + 1);
    for (const id of ['c1', 'c2', 'c3']) {
      expect(currentLook(clipOf(store, id).grade)?.look).toBe('Warm');
      expect(fixValuesOf(clipOf(store, id).grade).warmth).toBe(0.4);
    }
    expect(clipOf(store, 'a1').grade ?? null).toBeNull();
    act(() => void undoStep(store));
    expect(store.getState().cursor).toBe(before);
    expect(clipOf(store, 'c2').grade ?? null).toBeNull();
  });

  it('Apply to all with nothing chosen says so and writes nothing', () => {
    const store = make();
    render(<Harness store={store} which="look" />);
    select(store, ['c1']);
    const before = store.getState().cursor;
    tap('look-apply-all');
    expect(store.getState().cursor).toBe(before);
    expect(screen.getByTestId('look-problem').textContent).toContain('Pick a look');
  });

  it('Compare toggles the before and after, and the preview goes back to the grade when the sheet closes', () => {
    const store = make();
    const view = render(<Harness store={store} which="look" />);
    select(store, ['c1']);
    expect(store.getState().ui.gradeBypass).toBe(false);
    tap('look-compare-toggle');
    expect(store.getState().ui.gradeBypass).toBe(true);
    tap('look-compare-toggle');
    expect(store.getState().ui.gradeBypass).toBe(false);
    fireEvent.pointerDown(screen.getByTestId('look-compare-hold'));
    expect(store.getState().ui.gradeBypass).toBe(true);
    fireEvent.pointerUp(screen.getByTestId('look-compare-hold'));
    expect(store.getState().ui.gradeBypass).toBe(false);
    tap('look-compare-toggle');
    view.unmount();
    expect(store.getState().ui.gradeBypass).toBe(false);
  });

  it('colour work made on desktop is kept and named', () => {
    const grade = { nodes: [{ id: 'n1', name: 'Wheels', enabled: true, primaries: { exposure: 0.3, contrast: 1, saturation: 1, temperature: 0, tint: 0 }, curves: null, lut: null }] };
    const store = make(makeDoc([track('v1', 'video', [media('v', 0, 60, 0, { grade })])]));
    render(<Harness store={store} which="look" />);
    select(store, ['v']);
    expect(screen.getByTestId('look-extra').textContent).toContain('made on desktop');
    tap('look-warm');
    expect(clipOf(store, 'v').grade?.nodes?.some((n) => n.name === 'Wheels')).toBe(true);
  });

  it('adds a title at the playhead with a template, selects it, and edits its words', () => {
    const store = make();
    render(<Harness store={store} which="look" />);
    act(() => store.setUi({ playheadFrame: 130 }));
    tap('look-title-add');
    expect(screen.getByTestId('look-title-problem').textContent).toContain('Type the words');
    fireEvent.change(screen.getByTestId('look-title-text'), { target: { value: 'Hello there' } });
    tap('look-title-style-lowerThird');
    tap('look-title-add');
    const id = store.getState().ui.selection[0];
    const t = item(store, id) as TitleClip;
    expect(t.type).toBe('titleClip');
    expect(t.text).toBe('Hello there');
    expect(t.template).toBe('lowerThird');
    expect(t.recordIn).toBe(130);
    // the selected title can be edited from the same sheet
    const field = screen.getByTestId('look-title-edit-text');
    fireEvent.change(field, { target: { value: 'Goodbye' } });
    fireEvent.blur(field);
    expect((item(store, id) as TitleClip).text).toBe('Goodbye');
    tap('look-title-edit-style-boxed');
    expect((item(store, id) as TitleClip).template).toBe('boxed');
  });
});

describe('R07.7 phone clip sheet', () => {
  it('Fill covers the frame and Fit shows it all, each one undo step', () => {
    const d = makeDoc([track('v1', 'video', [media('v', 0, 60)])]);
    // a vertical frame with a 16:9 source: Fit keeps the width, Fill has to grow the picture
    const store = make({ ...d, settings: { ...d.settings, width: 1080, height: 1920 } });
    render(<Harness store={store} which="clip" />);
    select(store, ['v']);
    const before = store.getState().cursor;
    tap('clip-fill');
    expect(store.getState().cursor).toBe(before + 1);
    expect(clipOf(store, 'v').transform?.scale?.value).toBeGreaterThan(3);
    tap('clip-fit');
    expect(store.getState().cursor).toBe(before + 2);
    expect(clipOf(store, 'v').transform?.scale?.value ?? 1).toBeCloseTo(1);
  });

  it('the 3x3 pad and the numeric X and Y place the picture', () => {
    const store = make(makeDoc([track('v1', 'video', [media('v', 0, 60, 0, { transform: { scale: { value: 0.5, keys: null } } })])]));
    render(<Harness store={store} which="clip" />);
    select(store, ['v']);
    tap('clip-pad-1--1');
    const t = clipOf(store, 'v').transform;
    expect(t?.positionX?.value).toBeCloseTo(0.25);
    expect(t?.positionY?.value).toBeCloseTo(-0.25);
    type('clip-x', '-10');
    expect(clipOf(store, 'v').transform?.positionX?.value).toBeCloseTo(-0.1);
    type('clip-scale', '80');
    expect(clipOf(store, 'v').transform?.scale?.value).toBeCloseTo(0.8);
  });

  it('opacity is stored as 0..1 and 100% removes it; out of range is refused', () => {
    const store = make(makeDoc([track('v1', 'video', [media('v', 0, 60)])]));
    render(<Harness store={store} which="clip" />);
    select(store, ['v']);
    type('clip-opacity', '40');
    expect(clipOf(store, 'v').transform?.opacity?.value).toBe(0.4);
    type('clip-opacity', '100');
    expect(clipOf(store, 'v').transform ?? null).toBeNull();
  });

  it('speed chips and the numeric speed retime the clip; Mute writes the floor', () => {
    const store = make();
    render(<Harness store={store} which="clip" />);
    select(store, ['c1']);
    tap('phone-speed-2');
    expect(clipOf(store, 'c1').playbackRate).toBe(2);
    expect(clipOf(store, 'c1').recordOut).toBe(30);
    type('phone-speed-custom', '1');
    expect(clipOf(store, 'c1').playbackRate).toBe(1);
    select(store, ['a1']);
    tap('phone-volume-mute');
    expect(clipOf(store, 'a1').gainDb?.value).toBe(-60);
  });

  it('fade in and out are stored in frames; a fade longer than the clip is refused', () => {
    const store = make(makeDoc([track('v1', 'video', [media('v', 0, 90)])]));
    render(<Harness store={store} which="clip" />);
    select(store, ['v']);
    const fps = store.getState().doc.settings.rate.num / store.getState().doc.settings.rate.den;
    type('clip-fade-in', '1');
    expect(clipOf(store, 'v').fadeInFrames).toBe(Math.round(fps));
    type('clip-fade-out', '2');
    expect(clipOf(store, 'v').fadeOutFrames).toBe(Math.round(2 * fps));
    const before = store.getState().cursor;
    type('clip-fade-out', '3');
    expect(store.getState().cursor).toBe(before);
  });

  it('an animated channel is shown read-only ("animated on desktop") and is never written', () => {
    const keyed = { value: 0, keys: [{ frame: 0, value: 0, interp: 'linear' as const }, { frame: 30, value: 0.2, interp: 'linear' as const }] };
    const store = make(makeDoc([track('v1', 'video', [media('v', 0, 60, 0, { transform: { positionX: keyed } })])]));
    render(<Harness store={store} which="clip" />);
    select(store, ['v']);
    expect(screen.getByTestId('clip-x-readonly').textContent).toContain('Animated on desktop');
    expect(screen.queryByTestId('clip-x-slider')).toBeNull();
    const before = store.getState().cursor;
    tap('clip-pad-1-1');
    expect(store.getState().cursor).toBe(before);
    expect(screen.getByTestId('phone-sheet-problem').textContent).toContain('animated');
  });

  it('rotation, crop and reverse are not offered; an existing value is told in words', () => {
    const store = make(makeDoc([track('v1', 'video', [media('v', 0, 60, 0, { reverse: true, crop: { left: 0.1 }, transform: { rotation: { value: 15, keys: null } } })])]));
    render(<Harness store={store} which="clip" />);
    select(store, ['v']);
    const notes = screen.getByTestId('clip-readonly-notes').textContent ?? '';
    expect(notes).toContain('Rotated 15 degrees');
    expect(notes).toContain('Cropped');
    expect(notes).toContain('Plays backwards');
    expect(screen.queryByText(/rotation/i)).toBeNull();
  });
});
