import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { renderWithI18n } from '@/test/i18n';
import type { SocialAccount, SocialPost } from '@/lib/types/social';

const state = vi.hoisted(() => ({
  providers: [] as { provider: string; displayName: string }[],
  accounts: [] as unknown[],
  posts: [] as unknown[],
  summary: null as unknown,
}));
const api = vi.hoisted(() => ({ preflight: vi.fn(), create: vi.fn(), retry: vi.fn() }));

vi.mock('next/navigation', () => ({ useSearchParams: () => new URLSearchParams() }));
vi.mock('next/link', () => ({ default: ({ href, children }: { href: string; children: React.ReactNode }) => <a href={href}>{children}</a> }));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ isOrgAdmin: true }) }));
vi.mock('@/lib/hooks/use-post-texts', () => ({
  useSavedPostTexts: () => ({ data: { items: [{ id: 't1', platform: 'youtube_shorts', title: 'Saved title', body: 'Saved body', hashtags: ['a'] }] } }),
}));
vi.mock('@/lib/hooks/use-social', () => ({
  useSocialProviders: () => ({ data: state.providers, isLoading: false }),
  useSocialAccounts: () => ({ data: state.accounts, isLoading: false, mutate: vi.fn() }),
  useSocialPosts: () => ({ data: { items: state.posts, total: state.posts.length }, isLoading: false, mutate: vi.fn() }),
  useSocialMetricsSummary: () => ({ data: state.summary, isLoading: false }),
}));
vi.mock('@/lib/api/social', async (orig) => {
  const real = await orig<typeof import('@/lib/api/social')>();
  return { ...real, preflightSocialPost: api.preflight, createSocialPost: api.create, retrySocialPost: api.retry };
});

import { AccountsTab } from './AccountsTab';
import { PerformanceTab } from './PerformanceTab';
import { PublishButton } from './PublishDialog';
import { QueueTab } from './QueueTab';

const account = (over: Partial<SocialAccount> = {}): SocialAccount => ({
  id: 'a1', provider: 'youtube', displayName: 'My channel', avatarUrl: null, status: 'active', lastError: null, createdAt: '2026-11-01T00:00:00Z', ...over,
});

beforeEach(() => {
  vi.clearAllMocks();
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} };
  Object.defineProperty(document, 'fonts', { configurable: true, value: { addEventListener: vi.fn(), removeEventListener: vi.fn() } });
  state.providers = [{ provider: 'youtube', displayName: 'YouTube' }];
  state.accounts = [account()];
  state.posts = [];
  state.summary = null;
});

describe('Accounts tab', () => {
  it('says not available when the server lists no platform', () => {
    state.providers = [];
    state.accounts = [];
    renderWithI18n(<AccountsTab />, { locale: 'en' });
    expect(screen.getByText('Publishing is not available yet')).toBeTruthy();
  });
  it('shows the private-until-approved note on the YouTube card and the account status', () => {
    renderWithI18n(<AccountsTab />, { locale: 'en' });
    expect(screen.getByTestId('youtube-private-note').textContent).toMatch(/private/);
    expect(screen.getByText('Connected')).toBeTruthy();
  });
  it('offers reconnect for an account that needs it', () => {
    state.accounts = [account({ status: 'reconnect_needed' })];
    renderWithI18n(<AccountsTab />, { locale: 'en' });
    expect(screen.getByText('Reconnect needed')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Reconnect' })).toBeTruthy();
  });
});

describe('Queue tab', () => {
  const post = (over: Partial<SocialPost>): SocialPost => ({
    id: 'p1', projectId: 'pr', outputKind: 'step_result', outputId: 'o', accountId: 'a1', platform: 'youtube', format: 'short', title: 'T', body: 'B',
    hashtags: [], postTextId: null, options: {}, scheduledAt: '2026-11-20T09:00:00Z', scheduledLocal: '2026-11-20T09:00:00', timeZone: 'UTC',
    status: 'scheduled', attempts: 0, nextAttemptAt: null, remotePostUrl: null, failureCode: null, failureMessage: null, createdByUserId: 'u',
    createdAt: '2026-11-01T00:00:00Z', updatedAt: '2026-11-01T00:00:00Z', publishedAt: null, canEdit: true, canCancel: true, canRetry: false, ...over,
  });
  it('shows the check-the-channel prompt for an unknown post and gates retry on a confirmation', async () => {
    state.posts = [post({ status: 'unknown', canEdit: false, canCancel: false, canRetry: true })];
    renderWithI18n(<QueueTab />, { locale: 'en' });
    expect(screen.getByTestId('unknown-prompt')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    const dialog = await screen.findByRole('dialog');
    const confirmBtn = within(dialog).getByRole('button', { name: 'Try again' });
    expect((confirmBtn as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByLabelText('I checked the channel and the post is not there'));
    api.retry.mockResolvedValue({});
    fireEvent.click(confirmBtn);
    await waitFor(() => expect(api.retry).toHaveBeenCalledWith('p1', true));
  });
  it('switches to the month view', () => {
    state.posts = [post({})];
    renderWithI18n(<QueueTab />, { locale: 'en' });
    fireEvent.click(screen.getByText('Month'));
    expect(screen.getByTestId('publishing-month')).toBeTruthy();
  });
});

describe('Performance tab', () => {
  it('shows not available for a null number, never 0', () => {
    state.summary = {
      days: 30, publishedPosts: 1,
      totals: { views: 10, likes: null, comments: null, shares: null, saves: null },
      platforms: [{ platform: 'linkedin', posts: 1, totals: { views: null, likes: null, comments: null, shares: null, saves: null } }],
      posts: [],
    };
    renderWithI18n(<PerformanceTab />, { locale: 'en' });
    expect(screen.getByTestId('total-likes').textContent).toContain('Not available');
    expect(screen.getByTestId('total-views').textContent).toContain('10');
    expect(screen.getAllByTestId('bar-chart').length).toBeGreaterThan(0);
  });
});

describe('Publish dialog', () => {
  it('prefills the saved text, runs preflight, and schedules after review', async () => {
    api.preflight.mockResolvedValue({ platform: 'youtube', limitsVersion: 1, limitsLastChecked: '2026-11-10', ok: true, issues: [] });
    api.create.mockResolvedValue({});
    renderWithI18n(<PublishButton projectId="pr" outputId="o" outputKind="step_result" />, { locale: 'en' });
    fireEvent.click(screen.getByTestId('publish-open'));
    fireEvent.click(await screen.findByLabelText('My channel (YouTube)'));
    expect((screen.getByLabelText('Post text') as HTMLTextAreaElement).value).toBe('Saved body');
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    fireEvent.click(await screen.findByTestId('publish-confirm'));
    await waitFor(() => expect(api.create).toHaveBeenCalled());
    expect(api.preflight).toHaveBeenCalledWith(expect.objectContaining({ accountId: 'a1', body: 'Saved body' }));
    expect(api.create.mock.calls[0][0]).toEqual(expect.objectContaining({ publishNow: true, postTextId: 't1', outputKind: 'step_result' }));
  });
  it('tells the person when no platform is available', async () => {
    state.providers = [];
    state.accounts = [];
    renderWithI18n(<PublishButton projectId="pr" outputId="o" outputKind="project_file" />, { locale: 'en' });
    fireEvent.click(screen.getByTestId('publish-open'));
    expect(await screen.findByTestId('publish-not-available')).toBeTruthy();
  });
});
