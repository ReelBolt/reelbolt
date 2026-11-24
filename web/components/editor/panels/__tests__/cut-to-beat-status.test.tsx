/** In-place status: while the beat analysis runs, the dialog shows the matching activity job (InlineJobStatus). */
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { assetRef, doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import type { EditTimeline } from '@/lib/editing/types';
import type { ActivityJob } from '@/lib/activity/types';
import { CutToBeatDialog } from '../CutToBeatDialog';

const P = '11111111-1111-4111-8111-111111111111';
const MUSIC_FILE = assetRef(7).projectFileId;

const job = {
  id: 'j1', type: 'mediaAnalysis', state: 'running', title: 'Beats', origin: 'server', cancellable: false, progress: null,
  subject: { mediaRef: `file:${MUSIC_FILE}`, analysisKind: 'beats' },
} as unknown as ActivityJob;
let jobs: ActivityJob[] = [];
vi.mock('@/lib/activity/ActivityProvider', () => ({
  useActivity: () => ({ cancel: vi.fn(), allJobs: jobs }),
  useJobFor: (p: (j: ActivityJob) => boolean) => jobs.find(p),
}));

Element.prototype.scrollIntoView = vi.fn();
class ResizeObserverStub { observe() {} unobserve() {} disconnect() {} }
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

const EMPTY = { state: 'none', reason: null, requestId: null, updatedAt: null, summary: null };
const timeline = (): EditTimeline =>
  doc([
    track(TRACKS.v1, 'video', [media('c1', 0, 58, 0, { assetRef: assetRef(1) })]),
    track(TRACKS.a1, 'audio', [media('m', 0, 540, 0, { assetRef: assetRef(7) })]),
  ]);

const renderDialog = () =>
  render(
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0, shouldRetryOnError: false }}>
      <MantineProvider>
        <CutToBeatDialog opened onClose={() => {}} doc={timeline()} projectId={P} newItemId={() => 'n'} selectedItemIds={['m']} onApply={() => {}} />
      </MantineProvider>
    </SWRConfig>,
  );

beforeEach(() => {
  vi.stubGlobal('matchMedia', (q: string) => ({ matches: false, media: q, onchange: null, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true }));
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({
    mediaRef: `file:${MUSIC_FILE}`,
    kinds: { silence: EMPTY, shots: EMPTY, motion: EMPTY, transcript: EMPTY, beats: { ...EMPTY, state: 'running' } },
  }), { status: 200, headers: { 'content-type': 'application/json' } })) as unknown as typeof fetch);
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); jobs = []; });

describe('CutToBeatDialog in-place status', () => {
  it('shows InlineJobStatus for the matching analysis job', async () => {
    jobs = [job];
    renderDialog();
    await waitFor(() => expect(screen.getByTestId('inline-job-status')).toBeTruthy());
  });
  it('falls back to the plain text with no job', async () => {
    renderDialog();
    await waitFor(() => expect(screen.getByTestId('cut-to-beat-finding')).toBeTruthy());
    expect(screen.queryByTestId('inline-job-status')).toBeNull();
  });
});
