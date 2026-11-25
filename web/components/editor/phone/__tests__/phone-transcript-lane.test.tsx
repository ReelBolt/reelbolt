// @vitest-environment jsdom
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createEditorStore, useEditorStore, type EditorStore } from '@/lib/editing/store';
import { caption, doc as makeDoc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline } from '@/lib/editing/types';
import { NO_KIND, type KindStatus, type MediaAnalysisStatus } from '@/lib/editing/api/media-analysis';

const G = '00000000-0000-4000-8000-0000000000aa';
const REF = `step_result:${G}`;
const mocks = vi.hoisted(() => ({ estimate: vi.fn(), start: vi.fn(), statuses: { data: undefined as unknown, mutate: vi.fn() }, words: [] as unknown[] }));
vi.mock('@/lib/editing/api/media-analysis', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/api/media-analysis')>()),
  estimateMediaAnalysis: mocks.estimate,
  startMediaAnalysis: mocks.start,
  useDocumentSourceStatuses: () => mocks.statuses,
}));
vi.mock('@/lib/editing/api/analysis', () => ({
  useTimelineAnalysis: () => ({ isLoading: false, error: undefined, data: { sources: [], artifact: { words: mocks.words } } }),
  sourceMatcher: () => () => true,
}));

import { CaptionsSheet } from '../CaptionsSheet';
import { WordPicker } from '../WordPicker';

const status = (transcript: Partial<KindStatus>): MediaAnalysisStatus => ({
  mediaRef: REF,
  kinds: { silence: NO_KIND, shots: NO_KIND, motion: NO_KIND, transcript: { ...NO_KIND, ...transcript }, beats: NO_KIND, events: NO_KIND },
});
const stepDoc = (): EditTimeline =>
  makeDoc([track(TRACKS.v1, 'video', [media('a', 0, 300, 0, { assetRef: { kind: 'stepResult', stepResultId: G } })]), track('subs', 'video', [caption('k1', 0, 60, 'Hello')])]);
const make = (d: EditTimeline) => createEditorStore({ timelineId: 't', projectId: 'p', doc: d, revision: 1 });

function Sheet({ store }: { store: EditorStore }) {
  const d = useEditorStore(store, (s) => s.doc);
  return (
    <MantineProvider>
      <CaptionsSheet
        doc={d}
        projectId="p"
        seedStepResultId={null}
        readOnly={false}
        playheadFrame={10}
        newItemId={() => 'n'}
        newTrackId={() => 'nt'}
        onApply={(ops: readonly EditOp[], label: string) => store.dispatchBatch(ops, label)}
        onSetProperty={vi.fn()}
        onSeek={vi.fn()}
        onUndo={vi.fn()}
        onRemoveSilence={vi.fn()}
        onCutToBeat={vi.fn()}
      />
    </MantineProvider>
  );
}

beforeEach(() => {
  (globalThis as { ResizeObserver?: unknown }).ResizeObserver ??= class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
  mocks.words = [];
  mocks.estimate.mockReset();
  mocks.start.mockReset();
  mocks.statuses.mutate.mockReset();
  mocks.statuses.data = [status({})];
});
afterEach(cleanup);

describe('phone transcript lane', () => {
  it('Captions sheet with no words offers to find them, with the price, then starts only the transcript', async () => {
    mocks.estimate.mockResolvedValue({ billingEnabled: true, credits: 4, seconds: 20, balanceCredits: 50, affordable: true, cached: false, unavailableReason: null });
    render(<Sheet store={make(stepDoc())} />);
    expect((screen.getByTestId('phone-captions-generate') as HTMLButtonElement).disabled).toBe(true);
    const start = screen.getByTestId('analyse-first');
    expect(start.style.minHeight).toBe('44px');
    fireEvent.click(start);
    await waitFor(() => expect(screen.getByTestId('analyse-first-price').textContent).toContain('4 credits'));
    fireEvent.click(screen.getByTestId('analyse-first-confirm'));
    await waitFor(() => expect(mocks.start).toHaveBeenCalledWith('p', REF, ['transcript']));
    expect(screen.queryByText(/larger screen/i)).toBeNull();
  });

  it('shows progress while the transcript runs', () => {
    mocks.statuses.data = [status({ state: 'running' })];
    render(<Sheet store={make(stepDoc())} />);
    expect(screen.getByTestId('transcript-running')).toBeTruthy();
    expect(screen.queryByTestId('analyse-first')).toBeNull();
  });

  it('Delete words with no words offers the same start', () => {
    render(
      <MantineProvider>
        <WordPicker doc={stepDoc()} projectId="p" seedStepResultId={null} readOnly={false} newItemId={() => 'n'} onApply={vi.fn()} onSeek={vi.fn()} />
      </MantineProvider>,
    );
    expect(screen.getByTestId('analyse-first')).toBeTruthy();
  });

  it('when the words exist, Generate is on and no start is offered', () => {
    mocks.words = [{ id: 'w0', startSec: 0, endSec: 0.5, text: 'One', sourceIndex: 0 }];
    render(<Sheet store={make(stepDoc())} />);
    expect((screen.getByTestId('phone-captions-generate') as HTMLButtonElement).disabled).toBe(false);
    expect(screen.queryByTestId('analyse-first')).toBeNull();
  });

  it('turns the captions lane off and on in one undoable op each', () => {
    const store = make(stepDoc());
    render(<Sheet store={store} />);
    const toggle = screen.getByTestId('phone-captions-lane-subs');
    expect(toggle.getAttribute('aria-pressed')).toBe('true');
    fireEvent.click(toggle);
    expect(store.getState().doc.tracks.find((t) => t.id === 'subs')?.enabled).toBe(false);
    expect(screen.getByTestId('phone-captions-lane-subs').getAttribute('aria-pressed')).toBe('false');
    fireEvent.click(screen.getByTestId('phone-captions-lane-subs'));
    expect(store.getState().doc.tracks.find((t) => t.id === 'subs')?.enabled).toBe(true);
  });
});
