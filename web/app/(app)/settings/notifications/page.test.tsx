// @vitest-environment jsdom
/**
 * The personal notification settings page.
 *
 * What these tests are actually protecting:
 *
 *   - the four switches are present, named in plain language, and reflect the caller's stored
 *     values (a missing row resolves to "all on");
 *   - flipping a switch calls the API with ONLY that switch (a partial patch), so the other
 *     three keep their stored values;
 *   - the recent-activity list is mounted and renders the delivered/failed rows the API returns;
 *   - a settings that fail to load surface an error rather than a blank page.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const H = vi.hoisted(() => ({
  prefs: null as null | { jobSucceeded: boolean; jobFailed: boolean; fileCreated: boolean; graphicsEdited: boolean; reviewActivity?: boolean; avatarRetention?: boolean },
  isLoading: false,
  error: null as null | Error,
  mutate: vi.fn(),
  recent: [] as unknown[],
  recentLoading: false,
  update: vi.fn(),
}));

vi.mock('@/lib/hooks/use-notifications', () => ({
  useNotificationPreferences: () => ({
    data: H.prefs,
    isLoading: H.isLoading,
    error: H.error,
    mutate: H.mutate,
  }),
  useRecentNotifications: () => ({ data: H.recent, isLoading: H.recentLoading }),
}));
vi.mock('@/lib/api/notifications', () => ({
  updateNotificationPreferences: (...args: unknown[]) => H.update(...args),
}));

import NotificationsPage from './page';

Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (q: string) => ({
    matches: false,
    media: q,
    addEventListener() {},
    removeEventListener() {},
    addListener() {},
    removeListener() {},
    dispatchEvent: () => false,
    onchange: null,
  }),
});

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);

const renderPage = () =>
  render(
    <MantineProvider defaultColorScheme="auto">
      <NotificationsPage />
    </MantineProvider>,
  );

beforeEach(() => {
  H.prefs = { jobSucceeded: true, jobFailed: true, fileCreated: true, graphicsEdited: true };
  H.isLoading = false;
  H.error = null;
  H.mutate = vi.fn();
  H.recent = [];
  H.recentLoading = false;
  H.update = vi.fn().mockResolvedValue({ jobSucceeded: true, jobFailed: true, fileCreated: true, graphicsEdited: true });
});

afterEach(cleanup);

describe('notifications: the four switches', () => {
  it('renders a page title and one card per switch, named in plain language', () => {
    renderPage();
    expect(screen.getByRole('heading', { level: 1, name: 'Notifications' })).toBeTruthy();
    const h3 = Array.from(document.querySelectorAll('h3')).map((h) => h.textContent);
    for (const title of ['Job finished', 'Job failed', 'File added', 'Thumbnail graphic edited']) {
      expect(h3).toContain(title);
    }
  });

  // Mantine renders its Switch as <input type="checkbox" role="switch">, so the accessible
  // role is "switch" (the explicit ARIA role overrides the implicit "checkbox"). Each switch
  // carries a distinct aria-label ("Email me: <card title>"), so they are addressable by name.
  const switchBy = (title: string) =>
    screen.getByRole('switch', { name: `Email me: ${title}` }) as HTMLInputElement;

  it('shows every switch on when the stored values are all on', () => {
    renderPage();
    for (const title of ['Job finished', 'Job failed', 'File added', 'Thumbnail graphic edited']) {
      expect(switchBy(title).checked).toBe(true);
    }
  });

  it('shows a switch off when its stored value is off', () => {
    H.prefs = { jobSucceeded: true, jobFailed: false, fileCreated: true, graphicsEdited: true };
    renderPage();
    expect(switchBy('Job failed').checked).toBe(false);
  });

  it('flips only the touched switch, leaving the other three untouched', async () => {
    renderPage();
    fireEvent.click(switchBy('File added'));
    await waitFor(() => expect(H.update).toHaveBeenCalledTimes(1));
    expect(H.update).toHaveBeenCalledWith({ fileCreated: false });
  });
});

describe('notifications: recent activity', () => {
  it('mounts the recent-activity card and renders the rows the API returns', () => {
    H.recent = [
      {
        id: 'a',
        organizationId: 'org',
        category: 'job_succeeded',
        title: 'Your video job finished: Q3 Launch',
        delivered: true,
        createdAt: '2026-10-11T02:00:00Z',
      },
      {
        id: 'b',
        organizationId: 'org',
        category: 'job_failed',
        title: 'Your video job failed: Q3 Launch',
        delivered: false,
        error: 'Render failed',
        createdAt: '2026-10-11T02:05:00Z',
      },
    ];
    renderPage();
    expect(screen.getByTestId('recent-activity')).toBeTruthy();
    expect(screen.getByText('Your video job finished: Q3 Launch')).toBeTruthy();
    expect(screen.getByText('Your video job failed: Q3 Launch')).toBeTruthy();
  });

  it('says nothing yet when there are no rows', () => {
    renderPage();
    expect(screen.getByText(/Nothing here yet/)).toBeTruthy();
  });
});

describe('notifications: failure states', () => {
  it('shows an error when the settings fail to load, not a blank page', () => {
    H.prefs = null;
    H.error = new Error('boom');
    renderPage();
    expect(screen.getByText(/Could not load your notification settings/i)).toBeTruthy();
  });
});

describe('notifications: review activity', () => {
  const sw = () => screen.getByRole('switch', { name: 'Email me: Review activity' }) as HTMLInputElement;

  it('is on by default when the field is absent, with a plain description', () => {
    renderPage();
    expect(sw().checked).toBe(true);
    expect(screen.getByText('Email me when someone leaves a note or a decision on a review link I shared.')).toBeTruthy();
  });

  it('sends reviewActivity false when toggled off', async () => {
    renderPage();
    fireEvent.click(sw());
    await waitFor(() => expect(H.update).toHaveBeenCalledWith({ reviewActivity: false }));
  });

  it('shows a plain message when the save fails', async () => {
    H.update = vi.fn().mockRejectedValue(new Error('boom'));
    renderPage();
    fireEvent.click(sw());
    await waitFor(() => expect(screen.getByTestId('save-error').textContent).toContain('could not be saved'));
  });
});

describe('notifications: avatar retention', () => {
  const sw = () => screen.getByRole('switch', { name: 'Email me: Avatar about to be deleted' }) as HTMLInputElement;

  it('is on by default when the field is absent', () => {
    renderPage();
    expect(sw().checked).toBe(true);
  });

  it('sends avatarRetention false when toggled off', async () => {
    renderPage();
    fireEvent.click(sw());
    await waitFor(() => expect(H.update).toHaveBeenCalledWith({ avatarRetention: false }));
  });
});
