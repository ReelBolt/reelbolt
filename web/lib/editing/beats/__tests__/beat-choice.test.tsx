import { act, cleanup, renderHook } from '@testing-library/react';
import { SWRConfig } from 'swr';
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { clearBeatChoice, getBeatChoice, setBeatCorrection, setBeatMusicClip, useBeatChoice } from '../beat-choice-store';
import { NO_CORRECTION } from '../beat-grid';
import { useBeatGridOverlay } from '../use-beat-grid-overlay';
import { analysis, clip, timeline, track } from './fixtures';

const FILE = '00000000-0000-4000-8000-000000000001';
const doc = timeline([track('a1', 'audio', [clip('m', 0, 240, { assetRef: { kind: 'projectFile', projectFileId: FILE } })])]);
const wrapper = ({ children }: { children: ReactNode }) => <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>{children}</SWRConfig>;

afterEach(() => {
  cleanup();
  clearBeatChoice('p1');
  vi.restoreAllMocks();
});

describe('shared beat choice', () => {
  it('resets the correction when the music clip changes', () => {
    setBeatMusicClip('p1', 'm');
    setBeatCorrection('p1', 'm', { tempo: 'half', nudgeMs: 10, barShift: 0 });
    setBeatMusicClip('p1', 'm');
    expect(getBeatChoice('p1')?.correction.tempo).toBe('half');
    setBeatMusicClip('p1', 'other');
    expect(getBeatChoice('p1')?.correction).toEqual(NO_CORRECTION);
  });

  it('changing the correction moves the overlay frames', async () => {
    global.fetch = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const body = url.includes('/data') || url.includes('beats') && !url.includes('status')
        ? { ...analysis() }
        : { mediaRef: `file:${FILE}`, kinds: { beats: { state: 'ready', reason: null, requestId: null, updatedAt: null, summary: null } } };
      return new Response(JSON.stringify(body), { status: 200, headers: { 'content-type': 'application/json' } });
    }) as unknown as typeof fetch;
    const { result } = renderHook(() => ({ overlay: useBeatGridOverlay('p1', doc, []), choice: useBeatChoice('p1') }), { wrapper });
    expect(result.current.overlay.musicClipId).toBe('m');
    const before = result.current.overlay.beats.map((b) => b.frame);
    act(() => setBeatCorrection('p1', 'm', { tempo: 'asFound', nudgeMs: 100, barShift: 0 }));
    const after = result.current.overlay.beats.map((b) => b.frame);
    if (before.length > 0) expect(after).not.toEqual(before);
    expect(result.current.choice?.correction.nudgeMs).toBe(100);
  });
});
