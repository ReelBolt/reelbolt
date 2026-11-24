// @vitest-environment jsdom
/**
 * Wave 5: guest review links beside the film on the Deliver page. Create a link for THIS render (the URL is shown
 * once, then gone), copy it, list the links without any URL or token, turn one off after a confirm; a person who
 * cannot change the timeline sees the list only.
 */

import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { ApiError } from '@/lib/api/client';
import type { EditTimelineRenderSummary } from '@/lib/editing/sync/api';
import type { ReviewLinkSummary } from '@/lib/editing/review/guest-links';
import { ReviewLinksBody, type ReviewLinksBodyProps } from '../ShareForReview';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;

const api = vi.hoisted(() => ({ list: vi.fn(), create: vi.fn(), revoke: vi.fn(), detail: vi.fn(), renders: vi.fn() }));
vi.mock('@/lib/editing/review/guest-links', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/review/guest-links')>()),
  listReviewLinks: (...a: unknown[]) => api.list(...a),
  createReviewLink: (...a: unknown[]) => api.create(...a),
  revokeReviewLink: (...a: unknown[]) => api.revoke(...a),
  getReviewLink: (...a: unknown[]) => api.detail(...a),
}));
vi.mock('@/lib/editing/sync/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/sync/api')>()),
  listEditTimelineRenders: (...a: unknown[]) => api.renders(...a),
}));

const film = (over: Partial<EditTimelineRenderSummary> = {}): EditTimelineRenderSummary => ({
  executionId: 'e-new', workflowId: 'w', timelineId: 't', status: 'Passed', startedAt: null, completedAt: '2026-10-26T10:30:00Z', revision: 1, preset: 'Hd1080p16x9',
  outputFileName: 'film.mp4', errorMessage: null, stepResultId: 'sr1', hasOutput: true, regions: null, durationSec: 12, ...over,
});

const link = (over: Partial<ReviewLinkSummary> = {}): ReviewLinkSummary => ({
  id: 'l1', label: 'Client cut', status: 'active', expiresAt: new Date(Date.now() + 6 * 86_400_000 + 3_600_000).toISOString(), createdAt: '2026-10-26T00:00:00Z',
  viewCount: 4, lastViewedAt: null, stale: false, decision: null, guestComments: 2, unreadGuestActivity: false, ...over,
});

const RENDERS = [film(), film({ executionId: 'e-old', completedAt: '2026-10-25T10:30:00Z', outputFileName: 'old.mp4' })];

function mount(props: Partial<ReviewLinksBodyProps> = {}) {
  return render(
    <MantineProvider>
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <ReviewLinksBody projectId="p" timelineId="t" renders={RENDERS} defaultExecutionId="e-new" inline {...props} />
      </SWRConfig>
    </MantineProvider>,
  );
}

const CREATED = { id: 'l9', url: 'https://x.test/app/review/g/SECRETTOKEN', label: 'Client cut', expiresAt: '2026-11-09T00:00:00Z', renderCreatedAt: '2026-10-26T00:00:00Z', stale: false };

beforeEach(() => {
  api.list.mockReset().mockResolvedValue([]);
  api.create.mockReset().mockResolvedValue(CREATED);
  api.revoke.mockReset().mockResolvedValue(undefined);
  api.detail.mockReset().mockResolvedValue({});
  api.renders.mockReset().mockResolvedValue([]);
});
afterEach(() => cleanup());

describe('review links beside the film', () => {
  it('opens the form from a labelled button and creates the link for the film on screen', async () => {
    mount();
    expect(screen.queryByTestId('share-form')).toBeNull();
    const open = screen.getByTestId('share-open-form');
    expect(open.textContent).toContain('Create a review link');
    fireEvent.click(open);
    await screen.findByTestId('share-form');
    await waitFor(() => expect((screen.getByTestId('share-render') as HTMLInputElement).value).toMatch(/^film\.mp4/));
    fireEvent.change(screen.getByTestId('share-label'), { target: { value: 'Client cut' } });
    fireEvent.click(screen.getByTestId('share-create'));
    await screen.findByTestId('share-url');
    expect(api.create).toHaveBeenCalledWith('p', 't', { label: 'Client cut', renderExecutionId: 'e-new', expiresInDays: 14, allowComments: true });
    // The page already holds the queue: the card does not ask the server for it again.
    expect(api.renders).not.toHaveBeenCalled();
  });

  it('follows the film on screen when a newer one finishes, until the person picks one themselves', async () => {
    const { rerender } = mount({ defaultExecutionId: 'e-old' });
    fireEvent.click(screen.getByTestId('share-open-form'));
    await waitFor(() => expect((screen.getByTestId('share-render') as HTMLInputElement).value).toMatch(/^old\.mp4/));
    rerender(
      <MantineProvider>
        <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
          <ReviewLinksBody projectId="p" timelineId="t" renders={RENDERS} defaultExecutionId="e-new" inline />
        </SWRConfig>
      </MantineProvider>,
    );
    await waitFor(() => expect((screen.getByTestId('share-render') as HTMLInputElement).value).toMatch(/^film\.mp4/));
  });

  it('shows the URL once: copy it, press Done, and it is gone from the page', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    api.list.mockResolvedValueOnce([]).mockResolvedValue([link({ id: 'l9' })]);
    mount();
    fireEvent.click(screen.getByTestId('share-open-form'));
    await screen.findByTestId('share-form');
    fireEvent.click(screen.getByTestId('share-create'));
    expect(((await screen.findByTestId('share-url')) as HTMLInputElement).value).toBe(CREATED.url);
    fireEvent.click(screen.getByTestId('share-copy'));
    await screen.findByText('Link copied.');
    expect(writeText).toHaveBeenCalledWith(CREATED.url);
    // The list that came back with the new link carries no URL and no token.
    await screen.findByTestId('share-link-l9');
    expect(screen.getByTestId('share-list').textContent).not.toMatch(/SECRETTOKEN|https?:\/\//);
    fireEvent.click(screen.getByTestId('share-done'));
    expect(screen.queryByTestId('share-url')).toBeNull();
    expect(document.body.textContent).not.toContain('SECRETTOKEN');
    expect(screen.getByTestId('share-open-form')).toBeTruthy();
  });

  it('lists the links with status, views and notes, and turns one off only after a confirm', async () => {
    api.list.mockResolvedValue([link(), link({ id: 'l2', label: null, status: 'revoked' })]);
    mount();
    const row = await screen.findByTestId('share-link-l1');
    expect(row.textContent).toContain('Client cut');
    expect(row.textContent).toContain('4 views');
    expect(row.textContent).toContain('2 guest notes');
    expect(screen.getByTestId('share-status-l2').textContent).toBe('Turned off');
    fireEvent.click(screen.getByTestId('share-revoke-l1'));
    expect(api.revoke).not.toHaveBeenCalled();
    fireEvent.click(screen.getByTestId('share-revoke-confirm-l1'));
    await waitFor(() => expect(api.revoke).toHaveBeenCalledWith('p', 't', 'l1'));
  });

  it('does not mark a link as seen just because the page lists it; the person says so', async () => {
    api.list.mockResolvedValue([link({ unreadGuestActivity: true })]);
    mount();
    const seen = await screen.findByTestId('share-mark-seen');
    expect(api.detail).not.toHaveBeenCalled();
    fireEvent.click(seen);
    await waitFor(() => expect(api.detail).toHaveBeenCalledWith('p', 't', 'l1'));
  });

  it('a person who cannot change the timeline sees the list, with no way to create or turn off', async () => {
    api.list.mockResolvedValue([link()]);
    mount({ readOnly: true });
    await screen.findByTestId('share-link-l1');
    expect(screen.getByTestId('share-read-only').textContent).toContain('cannot be created or turned off');
    expect(screen.queryByTestId('share-open-form')).toBeNull();
    expect(screen.queryByTestId('share-revoke-l1')).toBeNull();
  });

  it('says what is missing when there is no full film to share', () => {
    mount({ renders: [film({ preset: 'AudioOnly' })], defaultExecutionId: null });
    expect((screen.getByTestId('share-open-form') as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByTestId('share-no-render').textContent).toContain('Make a full render first.');
  });

  it('has a sentence while the list loads, when it is empty and when it cannot be read', async () => {
    let release: (links: ReviewLinkSummary[]) => void = () => {};
    api.list.mockReturnValue(new Promise<ReviewLinkSummary[]>((resolve) => { release = resolve; }));
    const first = mount();
    expect(screen.getByTestId('share-list-loading')).toBeTruthy();
    release([]);
    expect((await screen.findByTestId('share-list-empty')).textContent).toContain('No links yet.');
    first.unmount();
    api.list.mockReset().mockRejectedValue(new Error('offline'));
    mount();
    expect((await screen.findByTestId('share-list-error')).textContent).toContain('could not be loaded');
  });

  it('says why when the link could not be created', async () => {
    api.create.mockRejectedValue(new ApiError('x', 500, []));
    mount();
    fireEvent.click(screen.getByTestId('share-open-form'));
    await screen.findByTestId('share-form');
    fireEvent.click(screen.getByTestId('share-create'));
    expect((await screen.findByTestId('share-problem')).textContent).not.toBe('');
    expect(screen.queryByTestId('share-url')).toBeNull();
  });
});
