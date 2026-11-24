// @vitest-environment jsdom
/**
 * Wave 5: one cost statement before a render. Every figure on it comes from the estimate endpoint; the statement
 * has a sentence for each state (working it out, billing off, a figure, not enough credits, a refusal, a failure)
 * and Render follows it.
 */

import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { ApiError } from '@/lib/api/client';
import type { RenderEditTimelineRequest } from '@/lib/editing/sync/api';
import { CostStatement, type CostState } from '../CostStatement';
import { DeliverWorkspace, programEndFrame, timelineIsEmpty, type DeliverWorkspaceProps } from '../DeliverWorkspace';

// Plain functions, not spies: a spy tracks the rejected promise it returns and reports it as unhandled.
let estimateImpl: (request: RenderEditTimelineRequest) => Promise<unknown> = async () => ({});
const estimateCalls: RenderEditTimelineRequest[] = [];
vi.mock('@/lib/editing/sync/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/sync/api')>()),
  estimateEditTimelineRender: (_p: string, _t: string, request: RenderEditTimelineRequest) => {
    estimateCalls.push(request);
    return estimateImpl(request);
  },
  getExportScope: async () => ({ format: 'otio', carries: ['Clips'], dropped: [], mediaMode: 'none' }),
}));
vi.mock('@/lib/editing/review/guest-links', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/review/guest-links')>()),
  listReviewLinks: async () => [],
}));
vi.mock('@/lib/editing/use-device-class', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/use-device-class')>()),
  useDeviceClass: () => 'desktop',
}));
vi.mock('@/lib/hooks/use-execution-stream', () => ({ useExecutionStream: () => ({ events: [] }) }));
vi.mock('@/lib/hooks/use-billing', () => ({ useOutputHeightCap: () => null }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.mock('../../presets/PresetPicker', () => ({ PresetPicker: () => null }));
vi.mock('next/link', () => ({ default: ({ children, href, ...rest }: { children: React.ReactNode; href: string }) => <a href={href} {...rest}>{children}</a> }));

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;

beforeEach(() => {
  estimateImpl = async () => ({});
  estimateCalls.length = 0;
  window.localStorage.clear();
});
afterEach(() => cleanup());

const summary = { format: 'Full HD landscape', size: '1920 × 1080', range: 'Whole timeline', captions: null };
const statement = (cost: CostState) => render(<MantineProvider><CostStatement summary={summary} cost={cost} /></MantineProvider>);

describe('CostStatement', () => {
  it('always says what will be rendered', () => {
    statement({ kind: 'idle' });
    expect(screen.getByTestId('render-summary-format').textContent).toBe('Full HD landscape · 1920 × 1080');
    expect(screen.getByTestId('render-summary-range').textContent).toBe('Whole timeline');
    expect(screen.queryByTestId('render-summary-captions')).toBeNull();
  });

  it('says it is working the cost out, and shows no figure meanwhile', () => {
    statement({ kind: 'loading' });
    expect(screen.getByTestId('render-cost-loading').textContent).toContain('Working out the cost');
    expect(screen.queryByTestId('render-estimate')).toBeNull();
  });

  it('billing off: no cost line at all, only "Ready to render"', () => {
    statement({ kind: 'billingOff', estimate: { billingEnabled: false, affordable: true } });
    expect(screen.getByTestId('render-ready').textContent).toBe('Ready to render.');
    expect(screen.queryByTestId('render-estimate')).toBeNull();
    expect(screen.getByTestId('render-cost').textContent).not.toMatch(/credit/i);
  });

  it('a figure: the estimate, its usual range, the balance and the file size, all as the endpoint answered', () => {
    statement({ kind: 'ready', estimate: { billingEnabled: true, credits: 12, creditsLow: 10, creditsHigh: 14.25, estimateSeconds: 190, balanceCredits: 100, affordable: true, estimatedSizeBytes: 12_500_000 } });
    expect(screen.getByTestId('render-estimate').textContent).toBe('About 12 credits, about 3 min');
    expect(screen.getByTestId('render-estimate-range').textContent).toBe('Usually between 10 and 14.3 credits');
    expect(screen.getByTestId('render-balance').textContent).toBe('You have 100 credits');
    expect(screen.getByTestId('render-summary-size').textContent).toBe('About 12.5 MB');
  });

  it('shows no range when the endpoint gave none, and no invented figure when it gave no credits', () => {
    const { unmount } = statement({ kind: 'ready', estimate: { billingEnabled: true, credits: 3, estimateSeconds: 20, affordable: true } });
    expect(screen.getByTestId('render-estimate').textContent).toBe('About 3 credits, under 1 min');
    expect(screen.queryByTestId('render-estimate-range')).toBeNull();
    expect(screen.queryByTestId('render-balance')).toBeNull();
    unmount();
    statement({ kind: 'ready', estimate: { billingEnabled: true, affordable: true } });
    expect(screen.queryByTestId('render-estimate')).toBeNull();
    expect(screen.getByTestId('render-cost-unknown').textContent).toBe('There is no cost figure for these settings yet.');
  });

  it('a refusal says why and links to the way out', () => {
    const { unmount } = statement({ kind: 'refused', message: 'You need 4 more credits to render this.', link: 'credits' });
    expect(screen.getByTestId('render-cost-refused').textContent).toContain('You need 4 more credits to render this.');
    expect(screen.getByTestId('render-cost-link').getAttribute('href')).toBe('/settings/billing');
    unmount();
    statement({ kind: 'refused', message: 'Your plan does not include this render format.', link: 'plans' });
    expect(screen.getByTestId('render-cost-link').getAttribute('href')).toBe('/settings/billing#plans');
  });

  it('a failure says so and offers to try again', () => {
    const onRetry = vi.fn();
    statement({ kind: 'error', onRetry });
    expect(screen.getByTestId('render-cost-error').textContent).toContain('could not be worked out');
    fireEvent.click(screen.getByTestId('render-cost-retry'));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });
});

const props: DeliverWorkspaceProps = {
  projectId: 'p', timelineId: 't', revision: 3, dirty: false, readOnly: false, inOut: { inFrame: null, outFrame: null }, renders: [], rendersLoading: false, refreshRenders: () => {},
  timelineSize: { width: 1920, height: 1080, rate: { num: 30, den: 1 } },
  doc: { tracks: [{ items: [{ id: 'i1', name: 'Clip', type: 'mediaClip', recordOut: 900 }] }] } as unknown as DeliverWorkspaceProps['doc'],
};

const page = (over: Partial<DeliverWorkspaceProps> = {}) =>
  render(
    <MantineProvider>
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <DeliverWorkspace {...props} {...over} />
      </SWRConfig>
    </MantineProvider>,
  );

const start = () => screen.getByTestId('render-start') as HTMLButtonElement;

describe('the Deliver page states the cost before Render', () => {
  it('asks the estimate endpoint with the preset by name, and shows its answer above the Render button', async () => {
    estimateImpl = async () => ({ billingEnabled: true, credits: 12, creditsLow: 10, creditsHigh: 14, estimateSeconds: 190, balanceCredits: 100, affordable: true });
    page();
    expect(screen.getByTestId('render-cost-loading')).toBeTruthy();
    await waitFor(() => expect(screen.getByTestId('render-estimate').textContent).toBe('About 12 credits, about 3 min'));
    expect(estimateCalls[0].preset).toBe('Hd1080p16x9');
    expect(screen.getByTestId('render-balance').textContent).toBe('You have 100 credits');
    expect(screen.getByTestId('render-summary-range').textContent).toBe('Whole timeline, 00:00:00:00 to 00:00:30:00');
    expect(start().disabled).toBe(false);
    // The statement sits in the same panel as the button, before it.
    const panel = screen.getByTestId('render-cost-panel');
    expect(panel.contains(start())).toBe(true);
    expect(screen.getByTestId('render-cost').compareDocumentPosition(start()) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('asks again when the preset changes, and never shows the old figure for the new settings', async () => {
    let credits = 12;
    estimateImpl = async () => ({ billingEnabled: true, credits, estimateSeconds: 190, balanceCredits: 100, affordable: true });
    page();
    await waitFor(() => expect(screen.getByTestId('render-estimate').textContent).toContain('About 12 credits'));
    credits = 40;
    fireEvent.click(screen.getByTestId('render-preset-Uhd4k16x9'));
    expect(screen.queryByTestId('render-estimate')).toBeNull();
    expect(screen.getByTestId('render-cost-loading')).toBeTruthy();
    await waitFor(() => expect(screen.getByTestId('render-estimate').textContent).toContain('About 40 credits'));
    expect(estimateCalls[estimateCalls.length - 1].preset).toBe('Uhd4k16x9');
  });

  it('billing off: "Ready to render", no credits anywhere, Render on', async () => {
    estimateImpl = async () => ({ billingEnabled: false, affordable: true });
    page();
    await waitFor(() => expect(screen.getByTestId('render-ready')).toBeTruthy());
    expect(screen.queryByTestId('render-estimate')).toBeNull();
    expect(screen.getByTestId('deliver-controls').textContent).not.toContain('0 credits');
    expect(start().disabled).toBe(false);
  });

  it('not enough credits: says how many are missing, links to add credits, Render off', async () => {
    estimateImpl = async () => ({ billingEnabled: true, credits: 12, estimateSeconds: 190, balanceCredits: 2, affordable: false });
    page();
    await waitFor(() => expect(screen.getByTestId('render-need-credits').textContent).toContain('You need 10 more credits'));
    expect(screen.getByTestId('render-add-credits').getAttribute('href')).toBe('/settings/billing');
    expect(screen.getByTestId('render-balance').textContent).toBe('You have 2 credits');
    expect(start().disabled).toBe(true);
  });

  it('a shortfall for other settings never blocks the settings now on screen', async () => {
    let affordable = false;
    estimateImpl = async () => (affordable ? { billingEnabled: true, credits: 1, estimateSeconds: 30, balanceCredits: 2, affordable: true } : { billingEnabled: true, credits: 12, estimateSeconds: 190, balanceCredits: 2, affordable: false });
    page();
    await waitFor(() => expect(screen.getByTestId('render-need-credits')).toBeTruthy());
    affordable = true;
    fireEvent.click(screen.getByTestId('render-preset-AudioOnly'));
    // The old answer was for another preset: it is gone at once, and the statement says it is asking again.
    expect(screen.queryByTestId('render-need-credits')).toBeNull();
    expect(screen.getByTestId('render-cost-loading')).toBeTruthy();
    await waitFor(() => expect(screen.getByTestId('render-estimate').textContent).toContain('About 1 credit'));
    expect(start().disabled).toBe(false);
  });

  it('a 402 from the estimate is said in plain words and turns Render off', async () => {
    estimateImpl = async () => {
      throw new ApiError('x', 402, [], { code: 'INSUFFICIENT_CREDITS', missing: 4 });
    };
    page();
    await waitFor(() => expect(screen.getByTestId('render-cost-refused').textContent).toContain('You need 4 more credits to render this.'));
    expect(screen.getByTestId('render-cost-link').getAttribute('href')).toBe('/settings/billing');
    expect(start().disabled).toBe(true);
    expect(screen.getByTestId('render-disabled-reason').getAttribute('data-reason')).toBe('cost');
  });

  it('a failed estimate offers to try again and asks the endpoint once more', async () => {
    let fail = true;
    estimateImpl = async () => {
      if (fail) throw new Error('network');
      return { billingEnabled: true, credits: 5, estimateSeconds: 90, balanceCredits: 50, affordable: true };
    };
    page();
    await waitFor(() => expect(screen.getByTestId('render-cost-error')).toBeTruthy());
    const before = estimateCalls.length;
    fail = false;
    fireEvent.click(screen.getByTestId('render-cost-retry'));
    await waitFor(() => expect(screen.getByTestId('render-estimate').textContent).toContain('About 5 credits'));
    expect(estimateCalls.length).toBe(before + 1);
  });

  it('a read-only timeline asks for no estimate and cannot render', async () => {
    page({ readOnly: true });
    expect(start().disabled).toBe(true);
    expect(screen.getByTestId('render-disabled-reason').getAttribute('data-reason')).toBe('readOnly');
    await new Promise((resolve) => setTimeout(resolve, 500));
    expect(estimateCalls).toHaveLength(0);
    expect(screen.getByTestId('render-cost').getAttribute('data-cost')).toBe('idle');
  });

  it('an empty timeline cannot render and says what to do first', () => {
    const onOpenEdit = vi.fn();
    page({ doc: { tracks: [{ items: [] }] } as DeliverWorkspaceProps['doc'], onOpenEdit });
    expect(start().disabled).toBe(true);
    expect(screen.getByTestId('render-disabled-reason').getAttribute('data-reason')).toBe('empty');
    fireEvent.click(screen.getByTestId('render-open-edit'));
    expect(onOpenEdit).toHaveBeenCalledTimes(1);
  });
});

describe('what the timeline holds', () => {
  it('finds the last frame and an empty timeline', () => {
    expect(programEndFrame({ tracks: [{ items: [{ recordOut: 120 }, { recordOut: 90 }] }, { items: [{ recordOut: 300 }] }] })).toBe(300);
    expect(programEndFrame({ tracks: [] })).toBe(0);
    expect(timelineIsEmpty({ tracks: [{ items: [] }, { items: [] }] })).toBe(true);
    expect(timelineIsEmpty({ tracks: [{ items: [] }, { items: [{ recordOut: 1 }] }] })).toBe(false);
  });
});
