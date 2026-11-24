// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import ContentModerationPage from './page';
import {
  acknowledgeModerationItemAndRevalidate,
  useModerationQueue,
  useModerationSettings,
} from '@/lib/hooks/use-moderation-queue';
import type {
  ModerationReviewQueue,
  ModerationReviewQueueItem,
  ModerationSettings,
} from '@/lib/types/moderation';

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
Element.prototype.scrollIntoView = vi.fn();

vi.mock('@/lib/hooks/use-moderation-queue', () => ({
  useModerationQueue: vi.fn(),
  useModerationSettings: vi.fn(),
  acknowledgeModerationItemAndRevalidate: vi.fn(),
}));

const authState = vi.hoisted(() => ({ isPlatformAdmin: true }));
vi.mock('@/lib/hooks/use-auth', () => ({
  useAuth: () => ({ isPlatformAdmin: authState.isPlatformAdmin }),
}));

const queueHook = vi.mocked(useModerationQueue);
const settingsHook = vi.mocked(useModerationSettings);
const acknowledgeMock = vi.mocked(acknowledgeModerationItemAndRevalidate);

type QueueResult = ReturnType<typeof useModerationQueue>;
type SettingsResult = ReturnType<typeof useModerationSettings>;

const queueResult = (over: Partial<QueueResult> = {}): QueueResult =>
  ({
    data: undefined,
    error: undefined,
    isLoading: false,
    isValidating: false,
    mutate: vi.fn(),
    ...over,
  }) as unknown as QueueResult;

const settingsResult = (over: Partial<SettingsResult> = {}): SettingsResult =>
  ({
    data: undefined,
    error: undefined,
    isLoading: false,
    isValidating: false,
    mutate: vi.fn(),
    ...over,
  }) as unknown as SettingsResult;

const item = (over: Partial<ModerationReviewQueueItem> = {}): ModerationReviewQueueItem => ({
  id: 'item-1',
  surface: 'project_upload',
  action: 'upload',
  contentKind: 'image',
  topCategory: 'violence',
  topScore: 0.82,
  reasonCode: 'above_review',
  reviewState: 'pending',
  projectId: 'project-1',
  projectName: 'Launch film',
  createdAt: '2026-10-01T10:00:00.000Z',
  expiresAt: null,
  ...over,
});

const queue = (items: ModerationReviewQueueItem[]): ModerationReviewQueue => ({
  items,
  total: items.length,
  limit: 100,
});

const settings: ModerationSettings = {
  enabled: true,
  model: 'omni-moderation-latest',
  blockThreshold: 0.9,
  reviewThreshold: 0.7,
  hardBlockCategories: ['sexual/minors'],
  timeoutSeconds: 10,
  auditRetentionDays: 30,
  failClosedAtPublish: true,
};

const renderPage = () =>
  render(
    <MantineProvider>
      <ContentModerationPage />
    </MantineProvider>,
  );

describe('ContentModerationPage', () => {
  beforeEach(() => {
    global.matchMedia = ((q: string) => ({
      matches: false,
      media: q,
      onchange: null,
      addListener() {},
      removeListener() {},
      addEventListener() {},
      removeEventListener() {},
      dispatchEvent: () => true,
    })) as unknown as typeof matchMedia;
    vi.spyOn(notifications, 'show').mockImplementation(() => 'notification-id');
    acknowledgeMock.mockResolvedValue({ id: 'item-1', reviewState: 'acknowledged' });
    settingsHook.mockReturnValue(settingsResult({ data: settings }));
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
    authState.isPlatformAdmin = true;
  });

  it('shows the platform-admin notice and makes no request for a non-admin', () => {
    authState.isPlatformAdmin = false;
    queueHook.mockReturnValue(queueResult({ data: queue([]) }));
    renderPage();

    expect(screen.getByText('Platform administrators only')).toBeTruthy();
    expect(queueHook).toHaveBeenCalledWith(false);
  });

  it('always shows the review-only banner', () => {
    queueHook.mockReturnValue(queueResult({ data: queue([]) }));
    renderPage();

    expect(
      screen.getByText(
        'Review only — acknowledging records that you have seen this item. It changes no user content.',
      ),
    ).toBeTruthy();
  });

  it('renders the read-only settings strip', () => {
    queueHook.mockReturnValue(queueResult({ data: queue([]) }));
    renderPage();

    expect(screen.getByText('omni-moderation-latest')).toBeTruthy();
    expect(screen.getByText('0.90')).toBeTruthy();
    expect(screen.getByText('0.70')).toBeTruthy();
    expect(screen.getByText('Enabled', { selector: 'span' })).toBeTruthy();
  });

  it('renders one row per item with every column the acceptance names', () => {
    queueHook.mockReturnValue(queueResult({ data: queue([item()]) }));
    renderPage();

    for (const column of ['Created', 'Surface', 'Project', 'Kind', 'Category', 'Score', 'Action', 'State']) {
      expect(screen.getByRole('columnheader', { name: column })).toBeTruthy();
    }
    expect(screen.getByText('project_upload')).toBeTruthy();
    expect(screen.getByText('Launch film')).toBeTruthy();
    expect(screen.getByText('image')).toBeTruthy();
    expect(screen.getByText('violence')).toBeTruthy();
    expect(screen.getByText('82.0%')).toBeTruthy();
    expect(screen.getByText('pending')).toBeTruthy();
    expect(screen.getByText('Showing 1 of 1 pending decisions.')).toBeTruthy();
  });

  it('offers no block, dismiss or delete action', () => {
    queueHook.mockReturnValue(queueResult({ data: queue([item()]) }));
    renderPage();

    expect(screen.queryByRole('button', { name: /block/i })).toBeNull();
    expect(screen.queryByRole('button', { name: /dismiss/i })).toBeNull();
    expect(screen.queryByRole('button', { name: /delete/i })).toBeNull();
  });

  it('shows the empty state when there is nothing pending', () => {
    queueHook.mockReturnValue(queueResult({ data: queue([]) }));
    renderPage();

    expect(screen.getByText('Nothing to review')).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('acknowledges through the API helper after confirmation', async () => {
    const user = userEvent.setup();
    queueHook.mockReturnValue(queueResult({ data: queue([item()]) }));
    renderPage();

    await user.click(screen.getByRole('button', { name: 'Acknowledge project_upload item' }));

    const dialog = within(await screen.findByRole('dialog'));
    expect(dialog.getByText('Acknowledge this item?')).toBeTruthy();
    expect(dialog.getByText(/changes no user content/i)).toBeTruthy();

    await user.click(dialog.getByRole('button', { name: 'Acknowledge' }));

    await waitFor(() => expect(acknowledgeMock).toHaveBeenCalledTimes(1));
    expect(acknowledgeMock).toHaveBeenCalledWith('item-1');
  });

  it('reports a failed acknowledgement instead of claiming success', async () => {
    const user = userEvent.setup();
    acknowledgeMock.mockRejectedValue(new Error('Request failed'));
    queueHook.mockReturnValue(queueResult({ data: queue([item()]) }));
    renderPage();

    await user.click(screen.getByRole('button', { name: 'Acknowledge project_upload item' }));
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Acknowledge' }));

    await waitFor(() =>
      expect(notifications.show).toHaveBeenCalledWith(
        expect.objectContaining({ title: 'Error', message: 'Request failed', color: 'red' }),
      ),
    );
  });
});
