/**
 * The viewer's effects against a scripted session: proxies arriving re-present the paused frame
 * (PA-6), a user seek during playback reaches the session (PA-8), Loop wraps and Play in to out
 * stops at Out (PA-7), a hole in the picture says why (PA-13), and the badge is honest (PA-18).
 */

import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { MissingLayer, PreviewSession, ProxyStatus } from '@/lib/editing/preview';
import { ZERO_STATS } from '@/lib/editing/preview';
import type { PlayRange } from '@/lib/editing/transport';
import { doc, media, track } from '@/lib/editing/__tests__/fixtures';
import type { EditTimeline } from '@/lib/editing/types';

interface ScriptedSession extends PreviewSession {
  seeks: number[];
  plays: number[];
  invalidations: number;
  /** The session presents `frame` while playing (the render tick). */
  tick(frame: number): void;
  /** The session pauses itself at the end of the timeline. */
  end(): void;
  setMissing(next: MissingLayer[]): void;
}

// Mantine's color-scheme manager reads `window.matchMedia`, which jsdom does not implement.
global.matchMedia = (query: string): MediaQueryList =>
  ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => true,
  }) as MediaQueryList;

let current: ScriptedSession | null = null;

function scriptedSession(): ScriptedSession {
  let frame = 0;
  let speed = 0;
  let missing: MissingLayer[] = [];
  const listeners = new Set<() => void>();
  const notify = () => listeners.forEach((l) => l());
  const session: ScriptedSession = {
    kind: 'live',
    reason: null,
    seeks: [],
    plays: [],
    invalidations: 0,
    seek(next) {
      session.seeks.push(next);
      frame = next;
      notify();
    },
    play(next) {
      session.plays.push(next);
      speed = next;
      notify();
    },
    pause() {
      speed = 0;
      notify();
    },
    currentFrame: () => frame,
    isPlaying: () => speed !== 0,
    stats: () => ZERO_STATS,
    missingLayers: () => missing,
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    invalidate() {
      session.invalidations += 1;
    },
    dispose() {
      listeners.clear();
    },
    tick(next) {
      frame = next;
      notify();
    },
    end() {
      speed = 0;
      notify();
    },
    setMissing(next) {
      missing = next;
      notify();
    },
  };
  return session;
}

vi.mock('@/lib/editing/preview', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/editing/preview')>();
  return {
    ...actual,
    hasWebCodecs: () => true,
    createPreviewSession: () => {
      current = scriptedSession();
      return current;
    },
  };
});

import { ProgramViewer, type ProgramViewerProps } from '../ProgramViewer';

const timeline: EditTimeline = doc([track('v1', 'video', [media('c1', 0, 300)])]);
const flags = () => ({ locked: false, enabled: true, mute: false, solo: false });

function Harness(props: Partial<ProgramViewerProps> & { onFrame: (f: number) => void; onEnded: () => void }) {
  return (
    <MantineProvider>
      <ProgramViewer
        doc={timeline}
        getDoc={() => timeline}
        flags={flags}
        proxyUrlFor={() => '/p'}
        playheadFrame={0}
        speed={0}
        playbackResolution="half"
        forceFallback={false}
        onStats={() => undefined}
        {...props}
      />
    </MantineProvider>
  );
}

describe('ProgramViewer', () => {
  beforeEach(() => {
    current = null;
  });
  afterEach(() => cleanup());

  it('PA-6: re-presents the paused frame when a proxy URL arrives', () => {
    const onFrame = vi.fn();
    const onEnded = vi.fn();
    const view = render(<Harness onFrame={onFrame} onEnded={onEnded} proxyVersion={0} />);
    const before = current!.invalidations;
    view.rerender(<Harness onFrame={onFrame} onEnded={onEnded} proxyVersion={1} />);
    expect(current!.invalidations).toBeGreaterThan(before);
  });

  it('PA-8: a seek while playing reaches the session; the session\'s own frames do not', () => {
    const onFrame = vi.fn();
    const onEnded = vi.fn();
    const view = render(<Harness onFrame={onFrame} onEnded={onEnded} playheadFrame={0} speed={0} />);
    view.rerender(<Harness onFrame={onFrame} onEnded={onEnded} playheadFrame={0} speed={1} />);
    act(() => current!.tick(10));
    expect(onFrame).toHaveBeenLastCalledWith(10);
    const seeksBefore = current!.seeks.length;
    // The store echoes the session's frame back as the playhead: not a seek.
    view.rerender(<Harness onFrame={onFrame} onEnded={onEnded} playheadFrame={10} speed={1} />);
    expect(current!.seeks.length).toBe(seeksBefore);
    // Home / a marker click / an edit jump while playing: the session must go there.
    view.rerender(<Harness onFrame={onFrame} onEnded={onEnded} playheadFrame={200} speed={1} />);
    expect(current!.seeks.at(-1)).toBe(200);
    expect(current!.isPlaying()).toBe(true);
  });

  it('PA-7: Loop wraps at Out back to In and keeps playing', () => {
    const onFrame = vi.fn();
    const onEnded = vi.fn();
    const range: PlayRange = { inFrame: 30, outFrame: 60, loop: true };
    const view = render(<Harness onFrame={onFrame} onEnded={onEnded} getPlayRange={() => range} playheadFrame={30} speed={0} />);
    view.rerender(<Harness onFrame={onFrame} onEnded={onEnded} getPlayRange={() => range} playheadFrame={30} speed={1} />);
    const seeksBefore = current!.seeks.length;
    act(() => current!.tick(60));
    expect(current!.seeks.length).toBe(seeksBefore + 1);
    expect(current!.seeks.at(-1)).toBe(30);
    expect(current!.isPlaying()).toBe(true);
    expect(onEnded).not.toHaveBeenCalled();
  });

  it('PA-7: Loop with the timeline end as Out restarts when the session stops itself', () => {
    const onFrame = vi.fn();
    const onEnded = vi.fn();
    const range: PlayRange = { inFrame: 0, outFrame: 300, loop: true };
    const view = render(<Harness onFrame={onFrame} onEnded={onEnded} getPlayRange={() => range} playheadFrame={290} speed={0} />);
    view.rerender(<Harness onFrame={onFrame} onEnded={onEnded} getPlayRange={() => range} playheadFrame={290} speed={1} />);
    act(() => current!.end());
    expect(current!.seeks.at(-1)).toBe(0);
    expect(current!.isPlaying()).toBe(true);
    expect(onEnded).not.toHaveBeenCalled();
  });

  it('PA-7: Play in to out stops on the last frame before Out and reports ended', () => {
    const onFrame = vi.fn();
    const onEnded = vi.fn();
    const range: PlayRange = { inFrame: 30, outFrame: 60, loop: false };
    const view = render(<Harness onFrame={onFrame} onEnded={onEnded} getPlayRange={() => range} playheadFrame={30} speed={0} />);
    view.rerender(<Harness onFrame={onFrame} onEnded={onEnded} getPlayRange={() => range} playheadFrame={30} speed={1} />);
    act(() => current!.tick(61));
    expect(current!.isPlaying()).toBe(false);
    expect(current!.seeks.at(-1)).toBe(59);
    expect(onFrame).toHaveBeenLastCalledWith(59);
    expect(onEnded).toHaveBeenCalledTimes(1);
  });

  it('PA-13: a clip with no URL yet says "Preparing preview…" instead of showing black', () => {
    const status: ProxyStatus = { state: 'pending' };
    render(<Harness onFrame={vi.fn()} onEnded={vi.fn()} proxyStatusFor={() => status} />);
    expect(screen.queryByTestId('preview-notice')).toBeNull();
    act(() => current!.setMissing([{ itemId: 'c1', trackId: 'v1', cause: 'noUrl' }]));
    expect(screen.getByTestId('preview-notice').textContent).toBe('Preparing preview…');
  });

  it('PA-18: the badge reads "not ready" on a clip with no proxy', () => {
    render(<Harness onFrame={vi.fn()} onEnded={vi.fn()} proxyStatusFor={() => ({ state: 'original', url: '/o' })} />);
    const badge = screen.getByTestId('parity-badge');
    expect(badge.getAttribute('data-parity')).toBe('notReady');
    expect(badge.textContent).toMatch(/not ready/i);
  });

  it('PA-18: the badge says faithful only when the clip plays from its proxy', () => {
    render(<Harness onFrame={vi.fn()} onEnded={vi.fn()} proxyStatusFor={() => ({ state: 'proxy', url: '/p' })} />);
    expect(screen.getByTestId('parity-badge').getAttribute('data-parity')).toBe('faithful');
  });
});
