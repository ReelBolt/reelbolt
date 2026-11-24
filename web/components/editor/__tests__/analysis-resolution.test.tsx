// @vitest-environment jsdom
/**
 * Top-10 #4 at the component level, with the REAL resolver and a stubbed `fetch`: the seed step
 * result's artifact is the compile EDL (no silence, words or shots), exactly as the API serves it,
 * and the panels must still find the VideoAnalyze artifact through the EDL's pointer — the d4
 * "what-if" without route substitution. Also: a two-file track maps each file's silence onto that
 * file only, captions generated twice do not add a second track, and Remove silence has a phone entry.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { createEditorStore } from '@/lib/editing/store';
import type { EditOp } from '@/lib/editing/engine';
import type { MediaClip } from '@/lib/editing/types';
import { assetRef, doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { RemoveSilenceButton, RemoveSilenceDialog } from '../panels/RemoveSilenceDialog';
import { SubtitlesPanel } from '../panels/SubtitlesPanel';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

const P = '11111111-1111-4111-8111-111111111111';
const SEED = '22222222-2222-4222-8222-222222222222';
const ANALYSIS = '33333333-3333-4333-8333-333333333333';
const EXEC = '44444444-4444-4444-8444-444444444444';
const KEY = `projects/${P}/agentFiles/video-analysis/${EXEC}/step-1-analysis.json`;

const routes: Record<string, unknown> = {
  [`/api/v1/projects/${P}/step-results/${SEED}/artifact`]: { version: 1, analysisArtifactStorageKey: KEY, analysisStepOrder: 1, analysisStepResultId: null, segments: [{ index: 0, snappedStartSec: 0, snappedEndSec: 10, sourceIndex: 0 }] },
  [`/api/v1/projects/${P}/executions/${EXEC}`]: { id: EXEC, stepResults: [{ id: ANALYSIS, artifactStorageKey: KEY }, { id: SEED, artifactStorageKey: `projects/${P}/agentFiles/video-analysis/${EXEC}/step-2-edl.json` }] },
  [`/api/v1/projects/${P}/step-results/${ANALYSIS}/artifact`]: {
    sources: [{ sourceIndex: 0, storageKey: 'a.mp4' }, { sourceIndex: 1, storageKey: 'b.mp4' }],
    shots: [{ id: 's0', startSec: 0, endSec: 10, sourceIndex: 0 }, { id: 's1', startSec: 0, endSec: 10, sourceIndex: 1 }],
    // File A is silent at 3.0-4.5 s, file B at 8.0-9.5 s.
    silenceSpans: [{ id: 'g0', startSec: 3, endSec: 4.5, sourceIndex: 0 }, { id: 'g1', startSec: 8, endSec: 9.5, sourceIndex: 1 }],
    words: [{ id: 'w0', startSec: 0, endSec: 0.5, text: 'Hello', sourceIndex: 0 }, { id: 'w1', startSec: 0.5, endSec: 1, text: 'there.', sourceIndex: 0 }],
  },
  [`/api/v1/projects/${P}/files`]: [{ id: assetRef(1).projectFileId, storageKey: 'a.mp4' }, { id: assetRef(2).projectFileId, storageKey: 'b.mp4' }],
};

const fetchStub = vi.fn(async (url: string) => {
  if (!(url in routes)) return { ok: false, status: 404, statusText: 'Not Found', json: async () => ({}) };
  return { ok: true, status: 200, statusText: 'OK', json: async () => routes[url] };
});

beforeEach(() => {
  fetchStub.mockClear();
  vi.stubGlobal('fetch', fetchStub);
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const wrap = (ui: React.ReactElement) =>
  render(
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <MantineProvider>{ui}</MantineProvider>
    </SWRConfig>,
  );

// One track: 10 s of file A, then 10 s of file B, both from source second 0.
const twoFileDoc = () => doc([track(TRACKS.v1, 'video', [media('a', 0, 300, 0, { assetRef: assetRef(1) }), media('b', 300, 600, 0, { assetRef: assetRef(2) })])]);

describe('RemoveSilenceDialog reads the analysis, not the compile EDL', () => {
  it("lists each file's silence on that file's clip only, and cuts exactly those", async () => {
    const store = createEditorStore({ timelineId: 't', projectId: P, doc: twoFileDoc(), revision: 1 });
    let n = 0;
    const onApply = vi.fn((ops: readonly EditOp[], label: string) => store.dispatchBatch(ops, label));
    wrap(<RemoveSilenceDialog opened onClose={() => {}} doc={store.getState().doc} projectId={P} seedStepResultId={SEED} newItemId={() => `n${++n}`} onApply={onApply} />);

    await waitFor(() => expect(screen.getByTestId('remove-silence-span-g1')).toBeTruthy());
    // g0: file A at 3.0 s = record frame 90. g1: file B at 8.0 s = record 300 + 240 = 540 —
    // never file A's frame 240, which is where the same seconds sit on the first clip.
    expect(document.body.textContent).toContain('00:00:03:00');
    expect(document.body.textContent).toContain('00:00:18:00');
    expect(document.body.textContent).not.toContain('00:00:08:00');
    expect(fetchStub.mock.calls.map((c) => c[0])).toContain(`/api/v1/projects/${P}/step-results/${ANALYSIS}/artifact`);

    fireEvent.click(screen.getByTestId('remove-silence-apply'));
    expect(onApply).toHaveBeenCalledTimes(1);
    const items = store.getState().doc.tracks[0].items as MediaClip[];
    // Default padding 2 f: A loses frames 92..133, B loses 542..583 (shifted by A's 41 f ripple).
    expect(items.map((i) => [i.assetRef.projectFileId === assetRef(1).projectFileId ? 'A' : 'B', i.recordIn, i.recordOut, i.sourceIn])).toEqual([
      ['A', 0, 92, 0],
      ['A', 92, 259, 133],
      ['B', 259, 501, 0],
      ['B', 501, 518, 283],
    ]);
    expect(store.getState().history).toHaveLength(1);
  });

  it('says plainly when the run kept no analysis', async () => {
    const OTHER = '55555555-5555-4555-8555-555555555555';
    routes[`/api/v1/projects/${P}/step-results/${OTHER}/artifact`] = { version: 1, segments: [] };
    wrap(<RemoveSilenceDialog opened onClose={() => {}} doc={twoFileDoc()} projectId={P} seedStepResultId={OTHER} newItemId={() => 'x'} onApply={() => {}} />);
    await waitFor(() => expect(screen.getByTestId('remove-silence-no-analysis')).toBeTruthy());
    expect(screen.getByTestId('remove-silence-no-analysis').textContent).not.toMatch(/[0-9a-f]{8}-/);
  });
});

describe('SubtitlesPanel', () => {
  it('generating twice keeps one captions track (d4 N2)', async () => {
    const store = createEditorStore({ timelineId: 't', projectId: P, doc: twoFileDoc(), revision: 1 });
    let n = 0;
    let t = 0;
    const onApply = (ops: readonly EditOp[], label: string) => store.dispatchBatch(ops, label);
    const panel = () => (
      <SubtitlesPanel doc={store.getState().doc} projectId={P} timelineId="t" seedStepResultId={SEED} readOnly={false} playheadFrame={0} newItemId={() => `cap${++n}`} newTrackId={() => `subs${++t}`} onApply={onApply} onSetProperty={() => {}} onSeek={() => {}} />
    );
    const view = wrap(panel());
    await waitFor(() => expect(screen.getByTestId('subtitles-preview-count').textContent).toMatch(/from 2 words/));
    fireEvent.click(screen.getByTestId('subtitles-generate'));
    expect(store.getState().doc.tracks).toHaveLength(2);

    view.rerender(
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <MantineProvider>{panel()}</MantineProvider>
      </SWRConfig>,
    );
    await waitFor(() => expect(screen.getByTestId('subtitles-preview-count').textContent).toMatch(/from 2 words/));
    fireEvent.click(screen.getByTestId('subtitles-generate'));
    expect(store.getState().doc.tracks).toHaveLength(2);
    expect(store.getState().doc.tracks[1].items).toHaveLength(1);
    expect(screen.getByText(/The captions are already there/)).toBeTruthy();
  });
});

describe('RemoveSilenceButton', () => {
  it('is a named, reachable button on the timeline toolbar', () => {
    const onOpen = vi.fn();
    wrap(<RemoveSilenceButton disabled={false} onOpen={onOpen} />);
    const button = screen.getByTestId('remove-silence-open');
    expect(button.textContent).toBe('Remove silence');
    // Not hidden below a breakpoint (Mantine's visibleFrom adds this class).
    expect(button.className).not.toMatch(/visible-from|hidden-from/);
    fireEvent.click(button);
    expect(onOpen).toHaveBeenCalledTimes(1);
  });
});
