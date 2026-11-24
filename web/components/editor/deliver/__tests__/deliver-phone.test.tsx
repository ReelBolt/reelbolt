// @vitest-environment jsdom
/** T-006: the phone Deliver sheet: sticky Render, estimate 402 text, share branches, foreground refetch, completion badge. */

import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, renderHook, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import type { EditTimelineRenderSummary } from '@/lib/editing/sync/api';
import { DeliverWorkspace } from '../DeliverWorkspace';
import { FilmCard } from '../FilmCard';
import { newlyFinished, phoneFormatOrder, shareModeOf, useForegroundRefresh, useRenderCompletion } from '../deliver-phone';
import { PhoneHeader } from '../../phone/PhoneHeader';

// A plain function, not a spy: a spy tracks the rejected promise it returns and reports it as unhandled.
let estimateImpl: () => Promise<unknown> = async () => ({});
vi.mock('@/lib/editing/sync/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/sync/api')>()),
  estimateEditTimelineRender: () => estimateImpl(),
}));
vi.mock('@/lib/editing/use-device-class', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/use-device-class')>()),
  useDeviceClass: () => 'phone',
}));
vi.mock('@/lib/hooks/use-execution-stream', () => ({ useExecutionStream: () => ({ events: [] }) }));
vi.mock('@/lib/hooks/use-billing', () => ({ useOutputHeightCap: () => null }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.mock('../../presets/PresetPicker', () => ({ PresetPicker: () => null }));
vi.mock('next/link', () => ({ default: ({ children, href }: { children: React.ReactNode; href: string }) => <a href={href}>{children}</a> }));

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;

const row = (over: Partial<EditTimelineRenderSummary>): EditTimelineRenderSummary => ({
  executionId: 'e1', workflowId: 'w', timelineId: 't', status: 'Passed', startedAt: null, completedAt: null, revision: 1, preset: 'Vertical9x16',
  outputFileName: 'film.mp4', errorMessage: null, stepResultId: 'sr', hasOutput: true, regions: null, outputFileId: 'f1', ...over,
});

beforeEach(() => {
  estimateImpl = async () => ({});
});
afterEach(() => cleanup());

const wrap = (node: React.ReactNode) => render(<MantineProvider>{node}</MantineProvider>);

describe('phone Deliver sheet', () => {
  it('puts Render in a sticky footer and lists the vertical format first', async () => {
    estimateImpl = async () => ({ billingEnabled: true, credits: 3, estimateSeconds: 30, affordable: true });
    wrap(<DeliverWorkspace projectId="p" timelineId="t" revision={1} dirty={false} readOnly={false} inOut={{ inFrame: null, outFrame: null }} renders={[]} rendersLoading={false} refreshRenders={() => {}} />);
    const footer = await screen.findByTestId('deliver-sticky-footer');
    expect(footer.contains(screen.getByTestId('render-start'))).toBe(true);
    expect(screen.getByTestId('render-start').closest('[data-testid="deliver-phone-scroll"]')).toBeNull();
    await waitFor(() => expect(footer.textContent).toContain('About 3 credits'));
    expect((screen.getByTestId('render-preset') as HTMLInputElement).value).toBe('Vertical for phones');
    expect(screen.getByTestId('deliver-parity').textContent).toMatch(/Preview is paused/);
  });

  it('says a 402 estimate in plain words next to Render', async () => {
    const refused = new ApiError('x', 402, [], { code: 'INSUFFICIENT_CREDITS', missing: 4 });
    estimateImpl = async () => {
      throw refused;
    };
    wrap(<DeliverWorkspace projectId="p" timelineId="t" revision={1} dirty={false} readOnly={false} inOut={{ inFrame: null, outFrame: null }} renders={[]} rendersLoading={false} refreshRenders={() => {}} />);
    const footer = await screen.findByTestId('deliver-sticky-footer');
    await waitFor(() => expect(footer.textContent).toContain('You need 4 more credits to render this.'));
  });
});

describe('share capability', () => {
  const probe = new File(['x'], 'a.mp4', { type: 'video/mp4' });
  it('shares the file when the device can', () => {
    expect(shareModeOf({ share: async () => {}, canShare: () => true }, probe, true)).toBe('files');
  });
  it('falls back to the link, then to nothing', () => {
    expect(shareModeOf({ share: async () => {}, canShare: () => false }, probe, true)).toBe('link');
    expect(shareModeOf({ share: async () => {} }, probe, true)).toBe('link');
    expect(shareModeOf({ share: async () => {}, canShare: () => false }, probe, false)).toBe('none');
    expect(shareModeOf({}, probe, true)).toBe('none');
    expect(shareModeOf(undefined, probe, true)).toBe('none');
  });
  const film = () => wrap(<FilmCard projectId="p" render={row({})} revision={1} playUrl="/v" downloadUrl="/d" onDelete={async () => true} onCopyLink={() => {}} share />);
  it('shows no Share button when the device cannot share', () => {
    Object.defineProperty(navigator, 'share', { value: undefined, configurable: true });
    film();
    expect(screen.queryByTestId('film-share')).toBeNull();
    expect(screen.getByTestId('film-download').getAttribute('download')).toBe('film.mp4');
    expect((screen.getByTestId('film-player') as HTMLVideoElement).hasAttribute('playsinline')).toBe(true);
  });
  it('shows Share film when files can be shared', async () => {
    Object.defineProperty(navigator, 'share', { value: vi.fn().mockResolvedValue(undefined), configurable: true });
    Object.defineProperty(navigator, 'canShare', { value: () => true, configurable: true });
    film();
    expect((await screen.findByTestId('film-share')).getAttribute('data-share-mode')).toBe('files');
    Object.defineProperty(navigator, 'share', { value: undefined, configurable: true });
    Object.defineProperty(navigator, 'canShare', { value: undefined, configurable: true });
  });
});

describe('foreground and completion', () => {
  it('refetches each time the page becomes visible again', () => {
    const refetch = vi.fn();
    renderHook(() => useForegroundRefresh(refetch));
    Object.defineProperty(document, 'visibilityState', { value: 'hidden', configurable: true });
    document.dispatchEvent(new Event('visibilitychange'));
    expect(refetch).not.toHaveBeenCalled();
    Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });
    document.dispatchEvent(new Event('visibilitychange'));
    expect(refetch).toHaveBeenCalledTimes(1);
  });

  it('announces only renders that finish after the first list', () => {
    const done = vi.fn();
    const { rerender } = renderHook(({ r }) => useRenderCompletion(r, done), { initialProps: { r: [row({ executionId: 'old' }), row({ executionId: 'a', status: 'Running', hasOutput: false })] } });
    expect(done).not.toHaveBeenCalled();
    rerender({ r: [row({ executionId: 'old' }), row({ executionId: 'a' })] });
    expect(done).toHaveBeenCalledTimes(1);
    expect(done.mock.calls[0][0].map((r: EditTimelineRenderSummary) => r.executionId)).toEqual(['a']);
    rerender({ r: [row({ executionId: 'old' }), row({ executionId: 'a' })] });
    expect(done).toHaveBeenCalledTimes(1);
    expect(newlyFinished(new Set(['a']), [row({ executionId: 'a' })])).toEqual([]);
  });

  it('shows the Ready badge with text on the Export button', () => {
    const header = (badge: boolean) => (
      <MantineProvider>
        <PhoneHeader name="n" save={{ status: 'saved' } as never} backHref="/" reviewHref="/r" canUndo={false} canRedo={false} onUndo={() => {}} onRedo={() => {}} onExport={() => {}} exportBadge={badge} onOpenSheet={() => {}} touchDrag={false} onToggleTouchDrag={() => {}} />
      </MantineProvider>
    );
    const { rerender } = render(header(false));
    expect(screen.queryByTestId('phone-export-badge')).toBeNull();
    rerender(header(true));
    expect(screen.getByTestId('phone-export-badge').textContent).toBe('Ready');
  });

  it('orders formats vertical first', () => {
    const order = phoneFormatOrder([{ value: 'Hd1080p16x9' as const }, { value: 'Square1x1' as const }, { value: 'Vertical9x16' as const }]).map((o) => o.value);
    expect(order).toEqual(['Vertical9x16', 'Square1x1', 'Hd1080p16x9']);
  });
});
