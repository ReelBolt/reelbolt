/**
 * R04.1 / R04.2 / R04.5 / VF12 in the viewer: a hole in the picture says why with its progress and Try again,
 * the badge opens a list whose clip lines jump to the clip, the simple preview is labelled, sustained drops
 * offer it, and the frame has its size and a neutral skeleton before the session exists.
 */

import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { MissingLayer, PlaybackStats, PreviewSession, PreviewSessionKind, ProxyStatus } from '@/lib/editing/preview';
import { ReadinessStore, ZERO_STATS } from '@/lib/editing/preview';
import { assetRef, doc, media, track } from '@/lib/editing/__tests__/fixtures';
import type { EditTimeline } from '@/lib/editing/types';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
global.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as unknown as typeof ResizeObserver;

interface Scripted extends PreviewSession {
  setMissing(next: MissingLayer[]): void;
  setStats(next: Partial<PlaybackStats>, playing: boolean): void;
}
let current: Scripted | null = null;
let nextKind: PreviewSessionKind = 'live';

function scripted(): Scripted {
  let missing: MissingLayer[] = [];
  let stats: PlaybackStats = ZERO_STATS;
  let playing = false;
  const listeners = new Set<() => void>();
  const notify = () => listeners.forEach((l) => l());
  return {
    kind: nextKind,
    reason: nextKind === 'fallback' ? 'One clip at a time.' : null,
    seek: () => notify(),
    play: () => undefined,
    pause: () => undefined,
    currentFrame: () => 0,
    isPlaying: () => playing,
    stats: () => stats,
    missingLayers: () => missing,
    subscribe: (l) => (listeners.add(l), () => listeners.delete(l)),
    invalidate: () => undefined,
    dispose: () => listeners.clear(),
    setMissing(next) {
      missing = next;
      notify();
    },
    setStats(next, isPlaying) {
      stats = { ...stats, ...next };
      playing = isPlaying;
      notify();
    },
  };
}

vi.mock('@/lib/editing/preview', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/editing/preview')>();
  return {
    ...actual,
    hasWebCodecs: () => true,
    createPreviewSession: () => {
      current = scripted();
      return current;
    },
  };
});

import { ProgramViewer, type ProgramViewerProps } from '../ProgramViewer';

const REF = `file:${assetRef(1).projectFileId}`;
const timeline: EditTimeline = doc([
  track('v1', 'video', [media('c1', 0, 300, 0, { name: 'Interview', playbackRate: 0.5 })]),
  track('v2', 'video', [media('c2', 400, 600, 0, { name: 'Broll', playbackRate: 2, assetRef: assetRef(2) })]),
]);
const flags = () => ({ locked: false, enabled: true, mute: false, solo: false });
const pending: ProxyStatus = { state: 'pending' };

function Harness(props: Partial<ProgramViewerProps>) {
  return (
    <MantineProvider env="test">
      <ProgramViewer
        doc={timeline}
        getDoc={() => timeline}
        flags={flags}
        proxyUrlFor={() => '/p'}
        playheadFrame={0}
        speed={0}
        playbackResolution="half"
        forceFallback={false}
        onFrame={() => undefined}
        onEnded={() => undefined}
        onStats={() => undefined}
        proxyStatusFor={() => ({ state: 'proxy', url: '/p' })}
        {...props}
      />
    </MantineProvider>
  );
}

describe('viewer readiness (R04.1)', () => {
  beforeEach(() => {
    current = null;
    nextKind = 'live';
  });
  afterEach(() => cleanup());

  it('a hole shows the reason with its percent and a progress bar, then clears when the picture is there', () => {
    const readiness = new ReadinessStore();
    render(<Harness readiness={readiness} proxyStatusFor={() => pending} />);
    act(() => current!.setMissing([{ itemId: 'c1', trackId: 'v1', cause: 'noUrl' }]));
    expect(screen.getByTestId('preview-notice').textContent).toBe('Preparing preview…');
    act(() => readiness.apply({ mediaRef: REF, stage: 'Proxy', percentComplete: 42 }));
    expect(screen.getByTestId('preview-notice').textContent).toBe('Preparing preview 42%');
    expect(screen.getByTestId('preview-progress')).toBeTruthy();
    expect(screen.getByTestId('readiness-label').textContent).toBe('Preparing preview 42%');
    act(() => current!.setMissing([]));
    expect(screen.queryByTestId('preview-notice')).toBeNull();
  });

  it('a failed preparation says why and Try again asks for that source', () => {
    const readiness = new ReadinessStore();
    const onRetry = vi.fn();
    render(<Harness readiness={readiness} proxyStatusFor={() => pending} onRetryPreview={onRetry} />);
    act(() => readiness.apply({ mediaRef: REF, stage: 'Failed', percentComplete: null }));
    act(() => current!.setMissing([{ itemId: 'c1', trackId: 'v1', cause: 'noUrl' }]));
    expect(screen.getByTestId('preview-notice').textContent).toMatch(/^Preview failed: the file could not be converted/);
    fireEvent.click(screen.getByTestId('preview-retry'));
    expect(onRetry).toHaveBeenCalledWith([REF]);
  });

  it('the reason sits above the picture-pick layer (z-index 2), so Try again can be clicked in the Select tool', () => {
    const readiness = new ReadinessStore();
    render(<Harness readiness={readiness} proxyStatusFor={() => pending} onRetryPreview={vi.fn()} />);
    act(() => readiness.apply({ mediaRef: REF, stage: 'Failed', percentComplete: null }));
    act(() => current!.setMissing([{ itemId: 'c1', trackId: 'v1', cause: 'noUrl' }]));
    const frame = screen.getByTestId('preview-reason').parentElement as HTMLElement;
    expect(Number(frame.style.zIndex)).toBeGreaterThan(2);
  });

  it('the chip lists every picture source and Prepare all is offered', () => {
    const readiness = new ReadinessStore();
    const onPrepareAll = vi.fn();
    render(<Harness readiness={readiness} proxyStatusFor={() => pending} onPrepareAll={onPrepareAll} />);
    fireEvent.click(screen.getByTestId('readiness-chip'));
    expect(screen.getAllByTestId('readiness-row')).toHaveLength(2);
    fireEvent.click(screen.getByTestId('prepare-all'));
    expect(onPrepareAll).toHaveBeenCalled();
  });

  it('no chip when every source is ready', () => {
    render(<Harness readiness={new ReadinessStore()} />);
    expect(screen.queryByTestId('readiness-chip')).toBeNull();
  });
});

describe('badge reasons (R04.2)', () => {
  beforeEach(() => {
    nextKind = 'live';
  });
  afterEach(() => cleanup());

  it('tapping the badge lists the reasons; a clip line jumps to the clip', () => {
    const onJump = vi.fn();
    render(<Harness onJumpToClip={onJump} />);
    const badge = screen.getByTestId('parity-badge');
    expect(badge.getAttribute('data-parity')).toBe('approximate');
    fireEvent.click(badge);
    const lines = screen.getAllByTestId('parity-reason');
    expect(lines[0].textContent).toMatch(/^Clip "Interview": Slow motion/);
    fireEvent.click(lines[0]);
    expect(onJump).toHaveBeenCalledWith('c1');
  });

  it('clips elsewhere in the timeline are listed too', () => {
    const onJump = vi.fn();
    render(<Harness onJumpToClip={onJump} />);
    fireEvent.click(screen.getByTestId('parity-badge'));
    const elsewhere = screen.getByTestId('parity-elsewhere');
    expect(elsewhere.textContent).toMatch(/Clip "Broll": Fast motion/);
    fireEvent.click(elsewhere.querySelector('[data-testid="parity-reason"]')!);
    expect(onJump).toHaveBeenCalledWith('c2');
  });
});

describe('simple preview (R04.5)', () => {
  beforeEach(() => {
    nextKind = 'fallback';
  });
  afterEach(() => cleanup());

  it('is labelled, and the badge is never faithful', () => {
    render(<Harness forceFallback simpleReason="Started on the simple preview because this is a phone." />);
    expect(screen.getByTestId('simple-preview-label').textContent).toBe('Simple preview');
    expect(screen.getByTestId('parity-badge').getAttribute('data-parity')).not.toBe('faithful');
  });
});

describe('simple preview label tooltip', () => {
  beforeEach(() => {
    nextKind = 'fallback';
  });
  afterEach(() => cleanup());

  it("shows the session's own reason, not the Auto sentence", () => {
    render(<Harness forceFallback simpleReason="Started on the simple preview because this is a phone." />);
    expect(screen.getByTestId('simple-preview-label').getAttribute('title')).toBe('One clip at a time.');
  });
});

describe('dropped frames offer (R04.5)', () => {
  beforeEach(() => {
    nextKind = 'live';
  });
  afterEach(() => {
    vi.restoreAllMocks();
    cleanup();
  });

  it('sustained drops show the chip and the button switches to the simple preview', () => {
    let clock = 0;
    vi.spyOn(performance, 'now').mockImplementation(() => clock);
    const onSwitch = vi.fn();
    render(<Harness onSwitchToSimple={onSwitch} />);
    expect(screen.queryByTestId('dropped-frames-chip')).toBeNull();
    act(() => current!.setStats({ deadlines: 0, droppedFrames: 0 }, true));
    clock = 2500;
    act(() => current!.setStats({ deadlines: 75, droppedFrames: 40 }, true));
    expect(screen.getByTestId('dropped-frames-chip')).toBeTruthy();
    fireEvent.click(screen.getByTestId('switch-simple-preview'));
    expect(onSwitch).toHaveBeenCalled();
    // paused: the warning goes away
    act(() => current!.setStats({}, false));
    expect(screen.queryByTestId('dropped-frames-chip')).toBeNull();
  });

  it('healthy playback shows nothing', () => {
    let clock = 0;
    vi.spyOn(performance, 'now').mockImplementation(() => clock);
    render(<Harness onSwitchToSimple={vi.fn()} />);
    act(() => current!.setStats({ deadlines: 0, droppedFrames: 0 }, true));
    clock = 2500;
    act(() => current!.setStats({ deadlines: 75, droppedFrames: 2 }, true));
    expect(screen.queryByTestId('dropped-frames-chip')).toBeNull();
  });
});

describe('viewer skeleton (VF12)', () => {
  afterEach(() => cleanup());

  it('the frame carries the program aspect and a booted flag; no "No preview" alert before a session exists', () => {
    render(<Harness />);
    const frame = screen.getByTestId('program-frame');
    expect(Number.parseFloat(frame.style.aspectRatio)).toBeCloseTo(timeline.settings.width / timeline.settings.height, 4);
    expect(screen.queryByTestId('preview-unavailable')).toBeNull();
  });
});
