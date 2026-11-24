/**
 * WebGL honesty and recovery in the viewer: a lost graphics context is shown over the frame and clears on
 * restore; "Try live preview again" rebuilds the session (and probes afresh); "Use live preview" lifts a
 * chosen Simple preview; the Simple preview's grade reaches the <video> as an SVG filter.
 */

import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ContextLoss, CreatePreviewSessionOptions, PreviewSession, PreviewFault } from '@/lib/editing/preview';
import { REASON_CONTEXT_FAILED, REASON_CONTEXT_LOST, REASON_NO_WEBGL2, REASON_USER_CHOICE, ZERO_STATS } from '@/lib/editing/preview';
import { svgGradeFilterOf } from '@/lib/editing/color/svg-grade';
import { IDENTITY_PREVIEW_GRADE } from '@/lib/editing/color/preview-grade';
import type { SvgGradeFilter } from '@/lib/editing/color/svg-grade';
import { doc, media, track } from '@/lib/editing/__tests__/fixtures';
import type { EditTimeline } from '@/lib/editing/types';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;

interface Script {
  kind: 'live' | 'fallback';
  fault: PreviewFault | null;
  reason: string | null;
  liveAvailable: boolean;
  contextLoss: ContextLoss | null;
  filter: SvgGradeFilter | null;
}

let script: Script;
let current: PreviewSession & { notify(): void };
const created: CreatePreviewSessionOptions[] = [];

function makeSession(): PreviewSession & { notify(): void } {
  const listeners = new Set<() => void>();
  const s = {
    get kind() { return script.kind; },
    get reason() { return script.reason; },
    get fault() { return script.fault; },
    get liveAvailable() { return script.liveAvailable; },
    get contextLoss() { return script.contextLoss; },
    gradeFilter: () => script.filter,
    seek: () => undefined,
    play: () => undefined,
    pause: () => undefined,
    currentFrame: () => 0,
    isPlaying: () => false,
    stats: () => ZERO_STATS,
    subscribe: (l: () => void) => (listeners.add(l), () => listeners.delete(l)),
    invalidate: () => undefined,
    dispose: () => listeners.clear(),
    notify: () => listeners.forEach((l) => l()),
  };
  return s as unknown as PreviewSession & { notify(): void };
}

vi.mock('@/lib/editing/preview', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/editing/preview')>();
  return {
    ...actual,
    hasWebCodecs: () => true,
    createPreviewSession: (options: CreatePreviewSessionOptions) => {
      created.push(options);
      current = makeSession();
      return current;
    },
  };
});

import { ProgramViewer } from '../ProgramViewer';

const timeline: EditTimeline = doc([track('v1', 'video', [media('c1', 0, 300)])]);
const flags = () => ({ locked: false, enabled: true, mute: false, solo: false });

function View({ forceFallback = false, simpleReason = null }: { forceFallback?: boolean; simpleReason?: string | null }) {
  return (
    <MantineProvider>
      <ProgramViewer doc={timeline} getDoc={() => timeline} flags={flags} proxyUrlFor={() => '/p'} playheadFrame={0} speed={0} playbackResolution="half" forceFallback={forceFallback} simpleReason={simpleReason} onFrame={() => undefined} onStats={() => undefined} onEnded={() => undefined} />
    </MantineProvider>
  );
}

beforeEach(() => {
  created.length = 0;
  script = { kind: 'live', fault: null, reason: null, liveAvailable: false, contextLoss: null, filter: null };
});
afterEach(() => cleanup());

describe('ProgramViewer: graphics recovery', () => {
  it('shows the lost-context message over the frame and clears it on restore', () => {
    render(<View />);
    expect(screen.queryByTestId('preview-context-lost')).toBeNull();
    script.contextLoss = { state: 'lost', message: REASON_CONTEXT_LOST };
    act(() => current.notify());
    expect(screen.getByTestId('preview-context-lost').textContent).toContain(REASON_CONTEXT_LOST);
    expect(screen.queryByTestId('preview-live-retry')).toBeNull(); // the browser may still restore it
    script.contextLoss = null;
    act(() => current.notify());
    expect(screen.queryByTestId('preview-context-lost')).toBeNull();
  });

  it('a failed rebuild offers "Try live preview again", which recreates the session and probes afresh', () => {
    render(<View />);
    script.contextLoss = { state: 'failed', message: REASON_CONTEXT_FAILED };
    act(() => current.notify());
    const before = created.length;
    expect(created.at(-1)!.reprobe).toBe(false);
    script.contextLoss = null;
    fireEvent.click(screen.getByTestId('preview-live-retry'));
    expect(created.length).toBe(before + 1);
    expect(created.at(-1)!.reprobe).toBe(true);
    expect(screen.queryByTestId('preview-context-lost')).toBeNull();
  });

  it('no WebGL2: the honest reason shows with a retry; no "Use live preview"', () => {
    script = { ...script, kind: 'fallback', fault: 'noWebGl2', reason: REASON_NO_WEBGL2, liveAvailable: false };
    render(<View />);
    expect(screen.getByTestId('preview-degraded-note').textContent).toContain('graphics acceleration');
    expect(screen.getByTestId('preview-live-retry')).toBeTruthy();
    expect(screen.queryByTestId('preview-use-live')).toBeNull();
  });

  it('a chosen Simple preview with live available offers "Use live preview", which starts live', () => {
    script = { ...script, kind: 'fallback', fault: 'userChoice', reason: REASON_USER_CHOICE, liveAvailable: true };
    render(<View forceFallback />);
    expect(created.at(-1)!.forceKind).toBe('fallback');
    expect(screen.queryByTestId('preview-live-retry')).toBeNull();
    script.kind = 'live';
    script.fault = null;
    fireEvent.click(screen.getByTestId('preview-use-live'));
    expect(created.at(-1)!.forceKind).toBeUndefined();
    expect(screen.queryByTestId('preview-use-live')).toBeNull();
  });

  it('Auto on a weak desktop: live at quarter, the plan\'s sentence is the reduced-quality line', () => {
    render(<View simpleReason="Started at quarter size because it has 4 processor cores." />);
    expect(screen.getByTestId('preview-reduced-note').textContent).toContain('Started at quarter size because it has 4 processor cores.');
  });

  it('the Simple preview wears the grade as an SVG filter, and drops it when the grade goes', () => {
    const filter = svgGradeFilterOf({ ...IDENTITY_PREVIEW_GRADE, saturation: 1.4 })!;
    script = { ...script, kind: 'fallback', fault: 'userChoice', reason: REASON_USER_CHOICE, liveAvailable: true, filter };
    render(<View forceFallback />);
    expect(screen.getByTestId('grade-filter-defs').querySelector(`filter#${filter.id}`)).toBeTruthy();
    expect((screen.getByTestId('program-video') as HTMLVideoElement).style.filter).toBe(`url(#${filter.id})`);
    script.filter = null;
    act(() => current.notify());
    expect(screen.queryByTestId('grade-filter-defs')).toBeNull();
    expect((screen.getByTestId('program-video') as HTMLVideoElement).style.filter).toBe('');
  });
});
