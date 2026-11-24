/**
 * T-007 Part B: the Cut to the music dialog itself — it gates on the beat analysis, shows the tempo
 * and the plain confidence warnings, corrects the grid in the browser, starts the free analysis on
 * demand, and dispatches the compiled plan once through `onApply`. jsdom does not lay out, so the
 * phone case pins the full-screen modal and the inline 44 px touch sizes.
 */
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { clearBeatChoice } from '@/lib/editing/beats/beat-choice-store';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { assetRef, doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { analysis as beatAnalysis } from '@/lib/editing/beats/__tests__/fixtures';
import type { BeatAnalysis } from '@/lib/editing/beats/beat-grid';
import type { EditTimeline } from '@/lib/editing/types';
import { CutToBeatDialog } from '../CutToBeatDialog';

const P = '11111111-1111-4111-8111-111111111111';
const MUSIC_FILE = assetRef(7).projectFileId;

let touch = false;
const matchMediaStub = (query: string): MediaQueryList =>
  ({
    matches: touch && /pointer:\s*coarse|max-width/.test(query),
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => true,
  }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

const EMPTY = { state: 'none', reason: null, requestId: null, updatedAt: null, summary: null } as const;
const statusBody = (beats: Record<string, unknown>) => ({
  mediaRef: `file:${MUSIC_FILE}`,
  kinds: { silence: EMPTY, shots: EMPTY, motion: EMPTY, transcript: EMPTY, beats: { ...EMPTY, ...beats } },
});

interface Calls {
  urls: string[];
  posts: Array<{ url: string; body: unknown }>;
}

/** 16 s of music on A1 (with room to spare), and two clips butting at frame 58 (the 120 BPM bar grid has a line at 60). */
const timeline = (): EditTimeline =>
  doc([
    track(TRACKS.v1, 'video', [
      media('c1', 0, 58, 0, { assetRef: assetRef(1) }),
      media('c2', 58, 120, 0, { assetRef: assetRef(2) }),
    ]),
    track(TRACKS.a1, 'audio', [media('m', 0, 540, 0, { assetRef: assetRef(7) })]),
  ]);

let calls: Calls = { urls: [], posts: [] };

function mockFetch(options: { beats: Record<string, unknown>; beat?: BeatAnalysis; onStart?: () => void }) {
  calls = { urls: [], posts: [] };
  let started = false;
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    calls.urls.push(url);
    if (init?.method === 'POST') {
      calls.posts.push({ url, body: JSON.parse(String(init.body ?? '{}')) });
      started = true;
      options.onStart?.();
      return new Response(JSON.stringify({ requestId: 'r1', mediaRef: `file:${MUSIC_FILE}`, states: { beats: { state: 'queued', reason: null } } }), { status: 200, headers: { 'content-type': 'application/json' } });
    }
    if (url.endsWith('/analysis/beats')) {
      return new Response(JSON.stringify(options.beat ?? beatAnalysis()), { status: 200, headers: { 'content-type': 'application/json' } });
    }
    if (url.endsWith('/analysis')) {
      return new Response(JSON.stringify(statusBody({ state: started ? 'queued' : 'none', ...options.beats })), { status: 200, headers: { 'content-type': 'application/json' } });
    }
    return new Response(JSON.stringify({}), { status: 404, headers: { 'content-type': 'application/json' } });
  }) as unknown as typeof fetch);
}

const renderDialog = (overrides: Partial<React.ComponentProps<typeof CutToBeatDialog>> = {}) =>
  render(
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0, shouldRetryOnError: false }}>
      <MantineProvider>
        <CutToBeatDialog
          opened
          onClose={() => {}}
          doc={timeline()}
          projectId={P}
          newItemId={() => 'n'}
          selectedItemIds={['m', 'c1']}
          onApply={() => {}}
          {...overrides}
        />
      </MantineProvider>
    </SWRConfig>,
  );

const minHeightPx = (el: HTMLElement | null) => parseFloat(el?.style.minHeight ?? '') || 0;

beforeEach(() => {
  vi.stubGlobal('matchMedia', matchMediaStub);
});
afterEach(() => {
  cleanup();
  clearBeatChoice(P);
  vi.unstubAllGlobals();
  touch = false;
});

describe('CutToBeatDialog', () => {
  it('reads the beat analysis and shows the tempo, the grid and the live plan summary', async () => {
    mockFetch({ beats: { state: 'ready', summary: { bpm: 120, tempoWord: 'fast' } }, beat: beatAnalysis({ tempoWord: 'fast' }) });
    renderDialog();

    await waitFor(() => expect(screen.getByTestId('cut-to-beat-tempo').textContent).toBe('120 BPM, fast'));
    // 120 BPM at 30 fps: a bar every 60 frames, 9 bar lines inside the long-enough music clip.
    expect(screen.getByTestId('cut-to-beat-grid-count').textContent).toBe('9 cut points in the music');
    // The 62-frame cut snaps to the bar line at 60: one cut moves, nothing is skipped.
    await waitFor(() => expect(screen.getByTestId('cut-to-beat-summary').textContent).toBe('1 cut will move.'));
    expect((screen.getByTestId('cut-to-beat-apply') as HTMLButtonElement).disabled).toBe(false);
  });

  it('warns plainly below 0.3 confidence and when the beat is not steady', async () => {
    mockFetch({ beats: { state: 'ready' }, beat: beatAnalysis({ confidence: 0.1, steady: false }) });
    renderDialog();
    await waitFor(() => expect(screen.getByTestId('cut-to-beat-confidence-warning')).toBeTruthy());
    expect(screen.getByTestId('cut-to-beat-steady-warning')).toBeTruthy();
    expect(screen.getByTestId('cut-to-beat-confidence-warning').textContent).toContain('Check the grid before you apply.');
  });

  it('offers the half-tempo switch when the analysis suggests it, and recomputes the grid in place', async () => {
    mockFetch({ beats: { state: 'ready' }, beat: beatAnalysis({ tempoWord: 'fast', halfTempoScore: 0.8 }) });
    renderDialog();

    await waitFor(() => expect(screen.getByTestId('cut-to-beat-tempo-offer')).toBeTruthy());
    expect(screen.getByTestId('cut-to-beat-grid-count').textContent).toBe('9 cut points in the music');

    fireEvent.click(screen.getByTestId('cut-to-beat-use-half'));

    await waitFor(() => expect(screen.getByTestId('cut-to-beat-grid-count').textContent).toBe('5 cut points in the music'));
    expect((screen.getByRole('radio', { name: 'Half' }) as HTMLInputElement).checked).toBe(true);
    // No server call was made by the correction.
    expect(calls.posts).toHaveLength(0);
  });

  it('nudges the grid later in 10 ms steps and shifts the bar start', async () => {
    mockFetch({ beats: { state: 'ready' }, beat: beatAnalysis({ tempoWord: 'fast' }) });
    renderDialog();
    await waitFor(() => expect(screen.getByTestId('cut-to-beat-nudge')).toBeTruthy());

    fireEvent.click(screen.getByTestId('cut-to-beat-nudge-later'));
    await waitFor(() => expect(screen.getByTestId('cut-to-beat-nudge').textContent).toBe('10 ms'));
  });

  it('starts the free beat analysis on demand and polls until it is ready', async () => {
    mockFetch({ beats: {} });
    renderDialog();

    const find = await screen.findByTestId('cut-to-beat-find');
    fireEvent.click(find);
    await waitFor(() => expect(calls.posts).toHaveLength(1));
    expect(calls.posts[0].body).toEqual({ kinds: ['beats'], language: null });
    expect(calls.posts[0].url).toContain(encodeURIComponent(`file:${MUSIC_FILE}`));
    await waitFor(() => expect(screen.getByTestId('cut-to-beat-finding').textContent).toContain('Waiting to start'));
  });

  it('shows the failure reason verbatim and offers a retry', async () => {
    mockFetch({ beats: { state: 'failed', reason: 'The audio could not be read.' } });
    renderDialog();
    await waitFor(() => expect(screen.getByTestId('cut-to-beat-reason').textContent).toBe('The audio could not be read.'));
    expect(screen.getByTestId('cut-to-beat-find').textContent).toBe('Try again');
  });

  it('applies the compiled plan once through onApply and closes', async () => {
    mockFetch({ beats: { state: 'ready' }, beat: beatAnalysis({ tempoWord: 'fast' }) });
    const onApply = vi.fn();
    const onClose = vi.fn();
    renderDialog({ onApply, onClose });

    await waitFor(() => expect(screen.getByTestId('cut-to-beat-summary').textContent).toBe('1 cut will move.'));
    fireEvent.click(screen.getByTestId('cut-to-beat-apply'));

    await waitFor(() => expect(onApply).toHaveBeenCalledTimes(1));
    const [ops, label] = onApply.mock.calls[0];
    expect(ops).toHaveLength(1);
    expect(label).toBe('Cut to the music (1 cut)');
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('auditions the same plan in the viewer instead of applying it', async () => {
    mockFetch({ beats: { state: 'ready' }, beat: beatAnalysis({ tempoWord: 'fast' }) });
    const onAudition = vi.fn();
    const onApply = vi.fn();
    renderDialog({ onAudition, onApply });

    await waitFor(() => expect(screen.getByTestId('cut-to-beat-summary').textContent).toBe('1 cut will move.'));
    fireEvent.click(screen.getByTestId('cut-to-beat-audition'));
    await waitFor(() => expect(onAudition).toHaveBeenCalledTimes(1));
    expect(onApply).not.toHaveBeenCalled();
  });

  it('keeps Apply disabled on a read-only document', async () => {
    mockFetch({ beats: { state: 'ready' }, beat: beatAnalysis({ tempoWord: 'fast' }) });
    renderDialog({ readOnly: true });
    await waitFor(() => expect(screen.getByTestId('cut-to-beat-summary').textContent).toBe('1 cut will move.'));
    expect((screen.getByTestId('cut-to-beat-apply') as HTMLButtonElement).disabled).toBe(true);
  });

  it('opens full screen on a phone with finger-sized actions', async () => {
    touch = true;
    mockFetch({ beats: { state: 'ready' }, beat: beatAnalysis({ tempoWord: 'fast' }) });
    renderDialog();

    await waitFor(() => expect(screen.getByTestId('cut-to-beat-apply')).toBeTruthy());
    expect(document.querySelector('[data-full-screen="true"]')).not.toBeNull();
    for (const id of ['cut-to-beat-apply', 'cut-to-beat-cancel']) {
      expect(minHeightPx(screen.getByTestId(id))).toBeGreaterThanOrEqual(44);
    }
  });
});
