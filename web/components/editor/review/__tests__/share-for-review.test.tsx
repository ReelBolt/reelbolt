// @vitest-environment jsdom
/** T-012: the member side of guest links: create shows the URL once, the list never has it, revoke, unread badge, banners. */

import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { ApiError } from '@/lib/api/client';
import type { EditTimelineRenderSummary } from '@/lib/editing/sync/api';
import type { ReviewLinkSummary } from '@/lib/editing/review/guest-links';
import { GuestDecisionBanners, ShareForReview, shareRenderLabel, shareableRenders } from '../ShareForReview';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;

const api = vi.hoisted(() => ({
  list: vi.fn(),
  create: vi.fn(),
  revoke: vi.fn(),
  detail: vi.fn(),
  renders: vi.fn(),
}));
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

const render1 = (over: Partial<EditTimelineRenderSummary> = {}): EditTimelineRenderSummary => ({
  executionId: 'e1', workflowId: 'w', timelineId: 't', status: 'Passed', startedAt: null, completedAt: '2026-10-26T10:30:00Z', revision: 1, preset: 'Vertical9x16',
  outputFileName: 'film.mp4', errorMessage: null, stepResultId: 'sr1', hasOutput: true, regions: null, durationSec: 12, ...over,
});

const link = (over: Partial<ReviewLinkSummary> = {}): ReviewLinkSummary => ({
  id: 'l1', label: 'Client cut', status: 'active', expiresAt: new Date(Date.now() + 6 * 86_400_000 + 3_600_000).toISOString(), createdAt: '2026-10-26T00:00:00Z',
  viewCount: 4, lastViewedAt: null, stale: false, decision: null, guestComments: 2, unreadGuestActivity: false, ...over,
});

function mount() {
  return render(
    <MantineProvider>
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <ShareForReview projectId="p" timelineId="t" />
      </SWRConfig>
    </MantineProvider>,
  );
}

beforeEach(() => {
  api.list.mockReset().mockResolvedValue([]);
  api.create.mockReset();
  api.revoke.mockReset().mockResolvedValue(undefined);
  api.detail.mockReset().mockResolvedValue({});
  api.renders.mockReset().mockResolvedValue([render1(), render1({ executionId: 'e0', completedAt: '2026-10-25T10:30:00Z', outputFileName: 'old.mp4' }), render1({ executionId: 'ep', preset: 'Preview' }), render1({ executionId: 'ea', preset: 'AudioOnly' })]);
});
afterEach(() => cleanup());

describe('shareable renders', () => {
  it('lists passed picture renders, newest first, without previews or audio, labelled by name, date and length', () => {
    const list = shareableRenders([render1(), render1({ executionId: 'ep', preset: 'Preview' }), render1({ executionId: 'ea', preset: 'AudioOnly' }), render1({ executionId: 'f', status: 'Failed' })]);
    expect(list.map((r) => r.executionId)).toEqual(['e1']);
    expect(shareRenderLabel(list[0])).toMatch(/^film\.mp4, .+, 0:12$/);
    expect(shareRenderLabel(list[0])).not.toMatch(/e1|sr1/);
  });
});

describe('ShareForReview', () => {
  it('creates a link with the chosen options and shows the URL once', async () => {
    api.create.mockResolvedValue({ id: 'l1', url: 'https://x.test/app/review/g/SECRETTOKEN', label: 'Client cut', expiresAt: '2026-11-09T00:00:00Z', renderCreatedAt: '2026-10-26T00:00:00Z', stale: false });
    mount();
    fireEvent.click(screen.getByTestId('share-for-review'));
    await screen.findByTestId('share-form');
    await waitFor(() => expect((screen.getByTestId('share-render') as HTMLInputElement).value).toMatch(/^film\.mp4/));
    fireEvent.change(screen.getByTestId('share-label'), { target: { value: 'Client cut' } });
    fireEvent.click(screen.getByTestId('share-create'));
    const url = (await screen.findByTestId('share-url')) as HTMLInputElement;
    expect(url.value).toBe('https://x.test/app/review/g/SECRETTOKEN');
    expect(api.create).toHaveBeenCalledWith('p', 't', { label: 'Client cut', renderExecutionId: 'e1', expiresInDays: 14, allowComments: true });
    expect(screen.getByText(/This link shows only this render|will not be shown again/)).toBeTruthy();
    // The list (re-fetched) has no URL anywhere.
    expect(screen.getByTestId('share-list').textContent).not.toContain('SECRETTOKEN');
  });

  it('hides the URL after the dialog is closed and reopened', async () => {
    api.create.mockResolvedValue({ id: 'l1', url: 'https://x.test/app/review/g/SECRETTOKEN', label: null, expiresAt: '2026-11-09T00:00:00Z', renderCreatedAt: '2026-10-26T00:00:00Z', stale: false });
    mount();
    fireEvent.click(screen.getByTestId('share-for-review'));
    await screen.findByTestId('share-form');
    await waitFor(() => expect((screen.getByTestId('share-render') as HTMLInputElement).value).not.toBe(''));
    fireEvent.click(screen.getByTestId('share-create'));
    await screen.findByTestId('share-url');
    fireEvent.click(screen.getByRole('button', { name: /close/i }));
    await waitFor(() => expect(screen.queryByTestId('share-dialog')).toBeNull());
    expect(document.body.textContent).not.toContain('SECRETTOKEN');
    fireEvent.click(screen.getByTestId('share-for-review'));
    await screen.findByTestId('share-form');
    expect(screen.queryByTestId('share-url')).toBeNull();
    expect(document.body.textContent).not.toContain('SECRETTOKEN');
  });

  it('copies through the clipboard and falls back to a selectable field', async () => {
    api.create.mockResolvedValue({ id: 'l1', url: 'https://x.test/app/review/g/SECRETTOKEN', label: null, expiresAt: '2026-11-09T00:00:00Z', renderCreatedAt: '2026-10-26T00:00:00Z', stale: false });
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    mount();
    fireEvent.click(screen.getByTestId('share-for-review'));
    await waitFor(() => expect((screen.getByTestId('share-render') as HTMLInputElement).value).not.toBe(''));
    fireEvent.click(screen.getByTestId('share-create'));
    await screen.findByTestId('share-url');
    fireEvent.click(screen.getByTestId('share-copy'));
    await screen.findByText('Link copied.');
    expect(writeText).toHaveBeenCalledWith('https://x.test/app/review/g/SECRETTOKEN');
    writeText.mockRejectedValue(new Error('denied'));
    fireEvent.click(screen.getByTestId('share-copy'));
    await screen.findByText(/to copy the selected link/);
  });

  it('says "Make a full render first." for NO_RENDER_TO_SHARE and a plain sentence for the limit', async () => {
    api.create.mockRejectedValueOnce(new ApiError('Render this timeline first, then share the finished video.', 409, [], { code: 'NO_RENDER_TO_SHARE' }));
    mount();
    fireEvent.click(screen.getByTestId('share-for-review'));
    await waitFor(() => expect((screen.getByTestId('share-render') as HTMLInputElement).value).not.toBe(''));
    fireEvent.click(screen.getByTestId('share-create'));
    expect((await screen.findByTestId('share-problem')).textContent).toBe('Make a full render first.');
    api.create.mockRejectedValueOnce(new ApiError('x', 409, [], { code: 'REVIEW_LINK_LIMIT' }));
    fireEvent.click(screen.getByTestId('share-create'));
    await waitFor(() => expect(screen.getByTestId('share-problem').textContent).toMatch(/too many share links/));
  });

  it('lists links with status, expiry in words, views and the latest decision, never a URL', async () => {
    api.list.mockResolvedValue([
      link({ decision: { decision: 'approved', guestName: 'Ana', note: null, at: '2026-10-26T12:00:00Z' }, stale: true }),
      link({ id: 'l2', label: null, status: 'revoked' }),
    ]);
    mount();
    fireEvent.click(screen.getByTestId('share-for-review'));
    const row = await screen.findByTestId('share-link-l1');
    expect(row.textContent).toContain('Client cut');
    expect(row.textContent).toContain('Expires in 6 days');
    expect(row.textContent).toContain('4 views');
    expect(row.textContent).toContain('Approved by Ana');
    expect(row.textContent).toContain('Newer edits exist');
    expect(screen.getByTestId('share-status-l2').textContent).toBe('Turned off');
    expect(screen.queryByTestId('share-revoke-l2')).toBeNull();
    expect(screen.getByTestId('share-list').textContent).not.toMatch(/https?:\/\//);
  });

  it('revokes only after a confirm sentence', async () => {
    api.list.mockResolvedValue([link()]);
    mount();
    fireEvent.click(screen.getByTestId('share-for-review'));
    fireEvent.click(await screen.findByTestId('share-revoke-l1'));
    expect(api.revoke).not.toHaveBeenCalled();
    expect(screen.getByText(/Anyone who has it will see that it expired/)).toBeTruthy();
    fireEvent.click(screen.getByTestId('share-revoke-confirm-l1'));
    await waitFor(() => expect(api.revoke).toHaveBeenCalledWith('p', 't', 'l1'));
  });

  it('shows an unread badge on the button and marks the link seen when the list opens', async () => {
    api.list.mockResolvedValue([link({ unreadGuestActivity: true })]);
    mount();
    expect((await screen.findByTestId('share-unread-badge')).textContent).toBe('1');
    fireEvent.click(screen.getByTestId('share-for-review'));
    await waitFor(() => expect(api.detail).toHaveBeenCalledWith('p', 't', 'l1'));
  });

  it('shows no badge without unread activity', async () => {
    api.list.mockResolvedValue([link()]);
    mount();
    await waitFor(() => expect(api.list).toHaveBeenCalled());
    expect(screen.queryByTestId('share-unread-badge')).toBeNull();
  });
});

describe('GuestDecisionBanners', () => {
  it('shows approved and changes-requested sentences as text, skipping dead links', () => {
    render(
      <MantineProvider>
        <GuestDecisionBanners
          links={[
            link({ decision: { decision: 'approved', guestName: 'Ana', note: null, at: '2026-10-26T12:00:00Z' } }),
            link({ id: 'l2', label: null, decision: { decision: 'changesRequested', guestName: '<b>Bo</b>', note: 'Shorter <i>intro</i>', at: '2026-10-26T12:00:00Z' } }),
            link({ id: 'l3', status: 'revoked', decision: { decision: 'approved', guestName: 'Zed', note: null, at: '2026-10-26T12:00:00Z' } }),
          ]}
        />
      </MantineProvider>,
    );
    expect(screen.getByTestId('guest-decision-l1').textContent).toMatch(/^Approved by Ana, /);
    expect(screen.getByTestId('guest-decision-l2').textContent).toContain('<b>Bo</b> asked for changes: Shorter <i>intro</i>');
    expect(screen.getByTestId('guest-decision-l2').querySelector('b')).toBeNull();
    expect(screen.queryByTestId('guest-decision-l3')).toBeNull();
  });
});
