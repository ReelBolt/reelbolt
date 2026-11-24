// @vitest-environment jsdom
/**
 * Magic blur on the phone: the Objects entry in More and the section in the clip sheet. A media clip on a picture
 * track with a project-file asset shows the section; a sound clip is told why not; a full run (mocked objects
 * module) registers with the editor, and the result the watcher lands writes exactly one undoable regionEffects op.
 */
import React, { useCallback } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createEditorStore, undoStep, useEditorStore, type EditorStore } from '@/lib/editing/store';
import { doc as makeDoc, media, standardDoc, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import type { EditTimeline, MediaClip } from '@/lib/editing/types';
import { getResultsStore, resetResultsStores, setActiveTimeline } from '@/lib/editing/background/results-store';
import { landObjectResult } from '@/lib/editing/background/object-effect';
import type { ObjectRequest } from '@/lib/editing/api/objects';
import { useTimelineEditing } from '../../timeline/useTimelineEditing';
import { ClipSheetBody } from '../ClipSheet';
import { MoreSheetBody } from '../ActionSheets';
import { usePhoneActions } from '../usePhoneActions';
import { UndoSnackbar, resetUndoSnackbar } from '../UndoSnackbar';
import { resetSheetHistory } from '../sheet-history';

const STEP_RESULT = '00000000-0000-4000-8000-0000000000aa';
const api = vi.hoisted(() => ({
  estimateObject: vi.fn(),
  startObject: vi.fn(),
}));
vi.mock('@/lib/editing/api/objects', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/api/objects')>()),
  estimateObject: api.estimateObject,
  startObject: api.startObject,
  useObjectList: () => ({ data: undefined }),
  useObjectConsent: () => ({ data: undefined }),
}));

beforeEach(() => {
  resetSheetHistory();
  api.estimateObject.mockReset().mockResolvedValue({ billingEnabled: true, credits: 2, frames: 10, seconds: 5, balanceCredits: 9, affordable: true, cached: false, consented: true, unavailableReason: null });
  api.startObject.mockReset().mockResolvedValue({ requestId: 'r1', executionId: 'e1', status: 'queued' });
  window.localStorage.clear();
  resetResultsStores();
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

function Harness({ store, which }: { store: EditorStore; which: 'clip' | 'more' }) {
  const doc = useEditorStore(store, (s) => s.doc);
  const ui = useEditorStore(store, (s) => s.ui);
  const split = useCallback(() => {}, []);
  const editing = useTimelineEditing({ store, readOnly: false, doc, pool: undefined, split, openInspector: () => {}, openReplace: () => {} });
  const actions = usePhoneActions({
    store, doc, ui, readOnly: false, editing, split, nudge: () => {}, extractAudio: () => {}, undo: () => void undoStep(store), addMarker: () => {},
    setInOut: (m) => store.setInOutMarks(m, { persist: false }), selectMode: false, setSelectMode: () => {}, sourceSizeFor: () => ({ width: 1920, height: 1080 }),
  });
  return (
    <MantineProvider>
      {which === 'clip' ? <ClipSheetBody actions={actions} focus="objects" /> : <MoreSheetBody actions={actions} onClose={() => {}} onOpen={() => {}} onEnterSelect={() => {}} />}
      <UndoSnackbar />
    </MantineProvider>
  );
}
const make = (d: EditTimeline = standardDoc()) => createEditorStore({ timelineId: 't', projectId: 'p', doc: d, revision: 1 });
const select = (store: EditorStore, ids: string[]) => act(() => store.setUi({ selection: ids }));
const clipOf = (store: EditorStore, id: string) => store.getState().doc.tracks.flatMap((t) => t.items).find((i) => i.id === id) as MediaClip;

describe('phone Objects', () => {
  it('shows the section for a picture clip from a project file', () => {
    const store = make();
    render(<Harness store={store} which="clip" />);
    select(store, ['c1']);
    expect(screen.getByTestId('objects-sheet')).toBeTruthy();
    expect(screen.getByTestId('object-effects-section')).toBeTruthy();
    expect(screen.getByTestId('object-run')).toBeTruthy();
  });

  it('explains why a sound clip has none', () => {
    const store = make(makeDoc([track(TRACKS.a1, 'audio', [media('a1', 0, 60, 0)])]));
    render(<Harness store={store} which="clip" />);
    select(store, ['a1']);
    expect(screen.getByTestId('objects-needs-picture').textContent).toContain('picture track');
    expect(screen.queryByTestId('object-effects-section')).toBeNull();
  });

  it('More offers the entry, enabled for a picture clip and off for a sound clip', () => {
    const store = make(makeDoc([track(TRACKS.v1, 'video', [media('c1', 0, 60, 0)]), track(TRACKS.a1, 'audio', [media('a1', 0, 60, 0)])]));
    render(<Harness store={store} which="more" />);
    select(store, ['c1']);
    expect((screen.getByTestId('phone-more-objects') as HTMLButtonElement).disabled).toBe(false);
    expect(screen.getByTestId('phone-more-objects').textContent).toBe('Blur or mask an object');
    select(store, ['a1']);
    expect((screen.getByTestId('phone-more-objects') as HTMLButtonElement).disabled).toBe(true);
  });

  it('a full run registers with the editor; the landed result writes exactly one undoable regionEffects op', async () => {
    const request: ObjectRequest = { requestId: 'r1', executionId: 'e1', status: 'succeeded', prompt: 'face', matchScope: null, effect: 'blur', strength: 0.7, invert: false, featherPx: 0, analysisStepResultId: STEP_RESULT, trackIds: ['g1'], coverageEndSec: null, failureCode: null, message: null, createdAt: 'x' };
    const store = make();
    setActiveTimeline('t', () => store.getState().doc);
    render(<Harness store={store} which="clip" />);
    select(store, ['c1']);
    const before = store.getState().cursor;
    fireEvent.change(screen.getByTestId('object-prompt'), { target: { value: 'face' } });
    await waitFor(() => expect((screen.getByTestId('object-run') as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByTestId('object-run'));
    await waitFor(() => expect(api.startObject).toHaveBeenCalled());
    await screen.findByTestId('object-progress');
    expect(store.getState().cursor).toBe(before);
    const results = getResultsStore('t');
    expect(results.getEntries()[0].anchor.clipIds).toEqual(['c1']);
    act(() => landObjectResult({ results, editor: store, entry: results.getEntries()[0], request, label: 'Add' }));
    expect(clipOf(store, 'c1').regionEffects).toHaveLength(1);
    expect(store.getState().cursor).toBe(before + 1);
    expect(clipOf(store, 'c1').regionEffects?.[0]).toEqual({ effect: 'blur', strength: 0.7, enabled: true, mask: { kind: 'segmentation', analysisStepResultId: STEP_RESULT, trackIds: ['g1'], invert: false, featherPx: 0 } });
    await act(async () => void (await undoStep(store)));
    expect(clipOf(store, 'c1').regionEffects ?? null).toBeNull();
  });
});
