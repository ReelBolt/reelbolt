import React from 'react';
import { renderHook, waitFor, act } from '@testing-library/react';
import { SWRConfig, useSWRConfig } from 'swr';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useDocumentMediaAnalysis } from '../media-analysis';

const REF = 'step_result:00000000-0000-4000-8000-0000000000aa';
let phase: 'running' | 'ready' = 'running';
const KINDS = ['silence', 'shots', 'motion', 'transcript', 'beats', 'events'];

vi.mock('@/lib/api/client', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api/client')>('@/lib/api/client');
  return {
    ...actual,
    fetcher: async (url: string) => {
      if (url.endsWith('/transcript')) {
        if (words === 'fail') {
          words = 'ok';
          throw new Error('boom');
        }
        return { words: [{ text: 'hello', startSec: 0, endSec: 1, estimated: false }] };
      }
      return { mediaRef: REF, kinds: Object.fromEntries(KINDS.map((k) => [k, { state: k === 'transcript' ? phase : 'none', reason: null, requestId: null, updatedAt: null, summary: null }])) };
    },
  };
});

let words: 'ok' | 'fail' = 'ok';
const wrapper = ({ children }: { children: React.ReactNode }) => <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>{children}</SWRConfig>;

describe('useDocumentMediaAnalysis refresh', () => {
  beforeEach(() => { phase = 'running'; words = 'ok'; });

  it('fetches the words after the status turns ready even when only the status key saw it', async () => {
    const { result } = renderHook(() => ({ analysis: useDocumentMediaAnalysis('p1', [REF]), config: useSWRConfig() }), { wrapper });
    await waitFor(() => expect(result.current.analysis.data?.[0].transcript).toBeNull());
    phase = 'ready';
    await act(async () => { await result.current.config.mutate((key) => Array.isArray(key) && key[0] === 'file-analysis-status'); });
    await waitFor(() => expect(result.current.analysis.data?.[0].transcript?.map((w) => w.text)).toEqual(['hello']));
  });

  it('status ready with a cached empty answer: the words are fetched', async () => {
    phase = 'ready';
    words = 'fail';
    const { result } = renderHook(() => ({ analysis: useDocumentMediaAnalysis('p1', [REF]), config: useSWRConfig() }), { wrapper });
    await waitFor(() => expect(result.current.analysis.data?.[0].transcript?.map((w) => w.text)).toEqual(['hello']));
  });
});
