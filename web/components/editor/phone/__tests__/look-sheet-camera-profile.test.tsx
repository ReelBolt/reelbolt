// @vitest-environment jsdom
import React, { useCallback } from 'react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen, act } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createEditorStore, undoStep, useEditorStore, type EditorStore } from '@/lib/editing/store';
import { doc as makeDoc, media, track } from '@/lib/editing/__tests__/fixtures';
import type { MediaClip } from '@/lib/editing/types';
import { useTimelineEditing } from '../../timeline/useTimelineEditing';
import { LookSheetBody } from '../LookSheet';
import { usePhoneActions } from '../usePhoneActions';
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
afterEach(cleanup);

function Harness({ store }: { store: EditorStore }) {
  const doc = useEditorStore(store, (s) => s.doc);
  const ui = useEditorStore(store, (s) => s.ui);
  const split = useCallback(() => {}, []);
  const nudge = useCallback(() => {}, []);
  const editing = useTimelineEditing({ store, readOnly: false, doc, pool: undefined, split, openInspector: () => {}, openReplace: () => {} });
  const actions = usePhoneActions({ store, doc, ui, readOnly: false, editing, split, nudge, extractAudio: () => {}, undo: () => void undoStep(store), addMarker: () => {}, setInOut: () => {}, selectMode: false, setSelectMode: () => {}, sourceSizeFor: () => ({ width: 1920, height: 1080 }) });
  return (
    <MantineProvider>
      <LookSheetBody actions={actions} />
    </MantineProvider>
  );
}

describe('phone Look sheet: camera profile row', () => {
  it('shows the profile row (badge and Turn off) and no viewing LUT; Turn off is one undo step', () => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: makeDoc([track('v1', 'video', [media('v', 0, 60, 0, { inputProfile: 'appleLog' })])]), revision: 1 });
    render(<Harness store={store} />);
    act(() => store.setUi({ selection: ['v'] }));
    expect(screen.getByTestId('look-camera-profile-badge').textContent).toContain('Camera profile: ');
    expect(screen.queryByTestId('viewing-lut-button')).toBeNull();
    fireEvent.click(screen.getByTestId('look-camera-profile-turn-off'));
    const clip = () => store.getState().doc.tracks[0].items[0] as MediaClip;
    expect(clip().inputProfile).toBeNull();
    act(() => void undoStep(store));
    expect(clip().inputProfile).toBe('appleLog');
  });
});
