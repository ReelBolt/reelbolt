// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { AdminRecipe, AdminReport } from '@/lib/types/marketplace';

const auth = vi.hoisted(() => ({ admin: true }));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ isPlatformAdmin: auth.admin }) }));
const data = vi.hoisted(() => ({ lists: {} as Record<string, unknown[]>, enabled: [] as string[] }));
vi.mock('@/lib/hooks/use-marketplace-owner', () => ({
  useAdminRecipes: (tab: string, enabled: boolean) => {
    if (enabled) data.enabled.push(tab);
    return { items: enabled ? data.lists[tab] : undefined, isLoading: false, error: undefined };
  },
  useAdminReports: (enabled: boolean) => {
    if (enabled) data.enabled.push('reports');
    return { items: enabled ? data.lists.reports : undefined, isLoading: false, error: undefined };
  },
}));
const api = vi.hoisted(() => ({ action: vi.fn(), dismiss: vi.fn() }));
vi.mock('@/lib/api/marketplace', async (orig) => ({ ...(await orig<typeof import('@/lib/api/marketplace')>()), adminRecipeAction: api.action, adminDismissReport: api.dismiss }));

import { AdminMarketplace } from './AdminMarketplace';

const recipe = (over: Partial<AdminRecipe> = {}): AdminRecipe => ({
  id: 'r1', versionId: 'v7', digest: 'd7', name: 'Teaser', authorLabel: 'Ada', orgName: 'Acme', version: 2, status: 'pending',
  previewStatus: 'ready', previewSha256: 'abc123', checks: { text: 'review', preview: 'pass', lintCodes: ['L001'] }, prompts: [{ stepOrder: 1, field: 'prompt', text: '<script>x</script>' }], ...over,
});
const report: AdminReport = { id: 'rp1', recipeId: 'r1', recipeName: 'Teaser', reason: 'spam', note: 'looks like an ad', count: 3 };

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  auth.admin = true;
  data.enabled = [];
  data.lists = { pending: [recipe()], listed: [recipe({ id: 'r2', name: 'Live one', status: 'listed' })], removed: [recipe({ id: 'r3', name: 'Gone', status: 'removed' })], reports: [report] };
  api.action.mockReset().mockResolvedValue(undefined);
  api.dismiss.mockReset().mockResolvedValue(undefined);
});
afterEach(cleanup);
const mount = () => render(<MantineProvider><AdminMarketplace /></MantineProvider>);

describe('AdminMarketplace', () => {
  it('shows nothing and asks nothing for a non-admin', () => {
    auth.admin = false;
    mount();
    expect(screen.getByText('Only platform admins can see this page.')).toBeTruthy();
    expect(data.enabled).toEqual([]);
  });

  it('shows the pending row with checks, prompts as text and the preview player', () => {
    const { container } = mount();
    expect(screen.getByText('Organization: Acme')).toBeTruthy();
    expect(screen.getByText('Text check: review')).toBeTruthy();
    expect(screen.getByText('Problems found: L001')).toBeTruthy();
    expect(container.querySelector('pre')?.textContent).toBe('<script>x</script>');
    expect(container.querySelector('script')).toBeNull();
    expect(container.querySelector('video')?.getAttribute('src')).toBe('/api/v1/marketplace/admin/versions/v7/preview');
    expect(container.querySelector('video')?.getAttribute('poster')).toBe('/api/v1/marketplace/admin/versions/v7/poster');
    expect(screen.getByText('Preview under review')).toBeTruthy();
    expect(screen.getByText('Preview file: Preview ready. SHA-256: abc123')).toBeTruthy();
  });

  it('shows an error state when the version preview cannot be loaded', () => {
    const { container } = mount();
    fireEvent.error(container.querySelector('video') as HTMLVideoElement);
    expect(screen.getByRole('alert').textContent).toContain('could not be loaded');
    expect(container.querySelector('video')).toBeNull();
  });

  it('approves from the pending tab', async () => {
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Approve' }));
    await waitFor(() => expect(api.action).toHaveBeenCalledWith('r1', 'approve', { versionId: 'v7', digest: 'd7' }));
  });

  it('rejects with a closed reason and a note', async () => {
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Reject' }));
    const d = await screen.findByRole('dialog');
    fireEvent.change(within(d).getByLabelText('Reason'), { target: { value: 'unsafe' } });
    fireEvent.change(within(d).getByLabelText('Note (optional)'), { target: { value: ' bad ' } });
    fireEvent.click(within(d).getByRole('button', { name: 'Reject' }));
    await waitFor(() => expect(api.action).toHaveBeenCalledWith('r1', 'reject', { versionId: 'v7', digest: 'd7', reason: 'unsafe', note: 'bad' }));
  });

  it('says the recipe changed on STALE_VERSION', async () => {
    api.action.mockRejectedValueOnce(Object.assign(new Error('x'), { code: 'STALE_VERSION', status: 409 }));
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Approve' }));
    expect((await screen.findByRole('alert')).textContent).toContain('changed while you were reviewing');
  });

  it('removes a listed recipe only after a confirmation, and restores a removed one', async () => {
    mount();
    fireEvent.click(screen.getByRole('tab', { name: 'Listed' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Remove' }));
    expect(api.action).not.toHaveBeenCalled();
    const d = await screen.findByRole('dialog');
    fireEvent.change(within(d).getByLabelText('Reason'), { target: { value: 'unsafe' } });
    fireEvent.change(within(d).getByLabelText('Note (optional)'), { target: { value: ' gone ' } });
    fireEvent.click(within(d).getByRole('button', { name: 'Remove' }));
    await waitFor(() => expect(api.action).toHaveBeenCalledWith('r2', 'remove', { reason: 'unsafe', note: 'gone' }));
    fireEvent.click(screen.getByRole('tab', { name: 'Removed' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Restore' }));
    await waitFor(() => expect(api.action).toHaveBeenCalledWith('r3', 'restore'));
  });

  it('shows reports with reason, note and count; dismiss and remove recipe', async () => {
    mount();
    fireEvent.click(screen.getByRole('tab', { name: 'Reports' }));
    expect(await screen.findByText('Reason: It is spam or an advert')).toBeTruthy();
    expect(screen.getByText('looks like an ad')).toBeTruthy();
    expect(screen.getByText('3 open reports')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }));
    await waitFor(() => expect(api.dismiss).toHaveBeenCalledWith('rp1'));
    fireEvent.click(screen.getByRole('button', { name: 'Remove recipe' }));
    const d = await screen.findByRole('dialog');
    fireEvent.click(within(d).getByRole('button', { name: 'Remove' }));
    await waitFor(() => expect(api.action).toHaveBeenCalledWith('r1', 'remove', { reason: 'content', note: undefined }));
  });
});
