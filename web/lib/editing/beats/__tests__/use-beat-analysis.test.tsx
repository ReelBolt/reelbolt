import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import { SWRConfig } from 'swr';
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { KindStatus } from '../../api/media-analysis';
import { useBeatAnalysis } from '../use-beat-analysis';
import { analysis as beatAnalysis, clip } from './fixtures';

const PROJECT = 'p1';
const FILE = '00000000-0000-4000-8000-000000000001';
const MEDIA_REF = `file:${FILE}`;
const musicClip = (fileId = FILE) => clip('m', 0, 240, { assetRef: { kind: 'projectFile', projectFileId: fileId } });

interface Call {
  url: string;
  method: string;
  body?: string;
}

const kind = (extra: Partial<KindStatus> = {}): KindStatus => ({ state: 'none', reason: null, requestId: null, updatedAt: null, summary: null, ...extra });
const statusBody = (beats: Partial<KindStatus>) => ({
  mediaRef: MEDIA_REF,
  kinds: { silence: kind(), shots: kind(), motion: kind(), transcript: kind(), beats: kind(beats) },
});

let calls: Call[] = [];

function mockFetch(handler: (call: Call) => { status: number; body: unknown }) {
  calls = [];
  global.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const call: Call = { url: String(input), method: init?.method ?? 'GET', body: typeof init?.body === 'string' ? init.body : undefined };
    calls.push(call);
    const answer = handler(call);
    return new Response(JSON.stringify(answer.body), { status: answer.status, headers: { 'content-type': 'application/json' } });
  }) as unknown as typeof fetch;
}

const wrapper = ({ children }: { children: ReactNode }) => (
  <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0, shouldRetryOnError: false }}>{children}</SWRConfig>
);

const dataCalls = () => calls.filter((c) => c.url.endsWith('/analysis/beats'));

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe('useBeatAnalysis', () => {
  it('is unsupported for a clip that does not show a project file, and fetches nothing', () => {
    mockFetch(() => ({ status: 200, body: statusBody({ state: 'ready' }) }));
    const step = clip('s', 0, 100, { assetRef: { kind: 'stepResult', stepResultId: 'sr-1' } });
    const { result } = renderHook(() => useBeatAnalysis(PROJECT, step), { wrapper });
    expect(result.current.state).toBe('unsupported');
    expect(result.current.analysis).toBeNull();
    expect(calls).toEqual([]);
  });

  it('reads and parses the analysis once the kind is ready', async () => {
    mockFetch((call) => (call.url.endsWith('/analysis/beats') ? { status: 200, body: beatAnalysis() } : { status: 200, body: statusBody({ state: 'ready', summary: { bpm: 120, tempoWord: 'fast' } }) }));
    const { result } = renderHook(() => useBeatAnalysis(PROJECT, musicClip()), { wrapper });
    await waitFor(() => expect(result.current.state).toBe('ready'));
    await waitFor(() => expect(result.current.analysis?.bpm).toBe(120));
    expect(result.current.analysis?.beatTimesSec).toHaveLength(33);
    expect(result.current.error).toBeNull();
    expect(dataCalls()).toHaveLength(1);
    expect(dataCalls()[0].url).toContain(encodeURIComponent(MEDIA_REF));
  });

  it('shows a failed run and its reason verbatim without fetching data', async () => {
    mockFetch(() => ({ status: 200, body: statusBody({ state: 'failed', reason: 'The audio could not be read.' }) }));
    const { result } = renderHook(() => useBeatAnalysis(PROJECT, musicClip()), { wrapper });
    await waitFor(() => expect(result.current.state).toBe('failed'));
    expect(result.current.reason).toBe('The audio could not be read.');
    expect(result.current.analysis).toBeNull();
    expect(dataCalls()).toHaveLength(0);
  });

  it('start() runs the free beats analysis for the clip file and refreshes the status', async () => {
    let posted = false;
    mockFetch((call) => {
      if (call.method === 'POST') {
        posted = true;
        return { status: 200, body: { requestId: 'r1', mediaRef: MEDIA_REF, states: { beats: { state: 'queued', reason: null } } } };
      }
      return { status: 200, body: statusBody({ state: posted ? 'queued' : 'none' }) };
    });
    const { result } = renderHook(() => useBeatAnalysis(PROJECT, musicClip()), { wrapper });
    await waitFor(() => expect(calls.some((c) => c.method === 'GET')).toBe(true));
    await act(async () => {
      await result.current.start();
    });
    const post = calls.find((c) => c.method === 'POST');
    expect(post?.url).toContain(encodeURIComponent(MEDIA_REF));
    expect(JSON.parse(post?.body ?? '{}')).toEqual({ kinds: ['beats'], language: null });
    expect(result.current.starting).toBe(false);
    await waitFor(() => expect(result.current.state).toBe('queued'));
  });

  it('reports a refused start in plain words', async () => {
    mockFetch((call) =>
      call.method === 'POST' ? { status: 500, body: { message: 'The service is busy.' } } : { status: 200, body: statusBody({ state: 'none' }) },
    );
    const { result } = renderHook(() => useBeatAnalysis(PROJECT, musicClip()), { wrapper });
    await waitFor(() => expect(calls.some((c) => c.method === 'GET')).toBe(true));
    await act(async () => {
      await result.current.start();
    });
    expect(result.current.error).toBe('The service is busy.');
    expect(result.current.starting).toBe(false);
  });
});
