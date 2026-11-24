// @vitest-environment jsdom
/**
 * Public API keys page: flag gating, role gating, list badges, revoke confirmation and the
 * secret-shown-once step of the create flow.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { Org } from '@/lib/api/orgs';
import type { ApiKeyItem } from '@/lib/types/api-key';
import { notifications } from '@mantine/notifications';
import { ApiError } from '@/lib/api/client';

const H = vi.hoisted(() => ({
  flag: true,
  notFound: vi.fn(() => {
    throw new Error('NEXT_NOT_FOUND');
  }),
  orgs: [] as unknown[],
  items: [] as unknown[],
  keyCalls: [] as unknown[],
  mutate: vi.fn(),
  createApiKey: vi.fn(),
  revokeApiKey: vi.fn(),
}));

vi.mock('next/navigation', () => ({
  notFound: H.notFound,
  useRouter: () => ({ push: vi.fn(), replace: vi.fn(), refresh: vi.fn(), prefetch: vi.fn() }),
  usePathname: () => '/org/api-keys',
  useSearchParams: () => new URLSearchParams(),
}));
vi.mock('@/lib/hooks/use-webhooks', () => ({ useWebhooks: () => ({ data: [], error: undefined, mutate: vi.fn() }) }));
vi.mock('@/components/shell/PublicApiFlag', () => ({ usePublicApiEnabled: () => H.flag }));
vi.mock('@/lib/hooks/use-orgs', () => ({ useOrgs: () => ({ data: H.orgs, mutate: vi.fn() }) }));
vi.mock('@/lib/hooks/use-projects', () => ({ useProjects: () => ({ data: [] }) }));
vi.mock('@/lib/hooks/use-api-keys', () => ({
  useApiKeys: (orgId: string | null) => {
    H.keyCalls.push(orgId);
    return { data: orgId ? { items: H.items, capsEnforced: true } : undefined, error: undefined, mutate: H.mutate };
  },
}));
vi.mock('@/lib/api/api-keys', () => ({
  createApiKey: (...a: unknown[]) => H.createApiKey(...a),
  revokeApiKey: (...a: unknown[]) => H.revokeApiKey(...a),
  updateApiKey: vi.fn(),
  getApiKeyAudit: vi.fn().mockResolvedValue({ data: [], nextCursor: null }),
  listApiKeys: vi.fn(),
}));

import OrgApiKeysPage from './page';

const TEAM: Org = { id: 'org-acme', name: 'Acme Team', slug: 'acme', kind: 'Team', role: 'Owner', createdAt: '2026-10-01T00:00:00Z' };

function key(over: Partial<ApiKeyItem> = {}): ApiKeyItem {
  return {
    id: 'k1',
    name: 'CI pipeline',
    keyPrefix: 'rbk_abc123',
    lastFour: 'wxyz',
    createdBy: { id: 'u1', name: 'Tommaso' },
    scopes: ['projects.read'],
    projectIds: null,
    projects: null,
    caps: { dailyCredits: null, weeklyCredits: null, monthlyCredits: null },
    expiresAt: null,
    neverExpires: true,
    createdAt: new Date().toISOString(),
    lastUsedAt: new Date().toISOString(),
    status: 'active',
    revokedAt: null,
    ...over,
  };
}

function renderPage(org: Org = TEAM) {
  H.orgs = [org];
  document.cookie = `reelbolt_user=${encodeURIComponent(
    JSON.stringify({
      email: 'tommaso@example.com', isPlatformAdmin: false, mustChangePassword: false,
      org: org.id, orgName: org.name, orgKind: org.kind, orgRole: org.role,
    }),
  )}; path=/`;
  return render(
    <MantineProvider>
      <OrgApiKeysPage />
    </MantineProvider>,
  );
}

beforeEach(() => {
  H.flag = true;
  H.items = [key()];
  H.keyCalls.length = 0;
  H.mutate.mockReset();
  H.createApiKey.mockReset();
  H.revokeApiKey.mockReset().mockResolvedValue(undefined);
});
afterEach(() => cleanup());
beforeEach(() => {
  global.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null, addEventListener: vi.fn(), removeEventListener: vi.fn(),
    addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn(),
  })) as unknown as typeof matchMedia;
});

describe('OrgApiKeysPage', () => {
  it('is a 404 when the flag is off', () => {
    H.flag = false;
    expect(() => renderPage()).toThrow('NEXT_NOT_FOUND');
  });

  it('explains instead of requesting for a Member', () => {
    renderPage({ ...TEAM, role: 'Member' });
    expect(screen.getByText('Only an Owner or Admin can manage API keys')).toBeTruthy();
    expect(H.keyCalls.every((c) => c === null)).toBe(true);
  });

  it('lists keys with the no-expiry warning badge', () => {
    renderPage();
    expect(screen.getAllByText('CI pipeline').length).toBeGreaterThan(0);
    expect(screen.getAllByText('No expiry').length).toBeGreaterThan(0);
  });

  it('revokes only after the confirmation', async () => {
    renderPage();
    fireEvent.click(screen.getAllByRole('button', { name: /Revoke/ })[0]);
    expect(H.revokeApiKey).not.toHaveBeenCalled();
    fireEvent.click(await screen.findByRole('button', { name: 'Revoke key' }));
    await waitFor(() => expect(H.revokeApiKey).toHaveBeenCalledWith('k1'));
  });

  it('shows an error toast, not the success title, when the revoke fails', async () => {
    H.revokeApiKey.mockRejectedValueOnce(new Error('boom'));
    const show = vi.spyOn(notifications, 'show');
    renderPage();
    fireEvent.click(screen.getAllByRole('button', { name: /Revoke/ })[0]);
    fireEvent.click(await screen.findByRole('button', { name: 'Revoke key' }));
    await waitFor(() => expect(show).toHaveBeenCalled());
    const arg = show.mock.calls[0][0];
    expect(arg.title).toBe('Revoke failed');
    expect(arg.color).toBe('red');
    expect(arg.message).toBe('Could not revoke the key. It is still active.');
    show.mockRestore();
  });

  it('shows the secret once and clears it on close', async () => {
    H.createApiKey.mockResolvedValue({ key: key({ id: 'k2' }), secret: 'rbk_secret_value_123' });
    renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'New API key' }));
    fireEvent.change(await screen.findByLabelText(/Name/), { target: { value: 'Deploy' } });
    fireEvent.click(screen.getByRole('button', { name: 'Select all read' }));
    fireEvent.click(screen.getByRole('button', { name: 'Create key' }));
    expect(await screen.findByText(/rbk_secret_value_123/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'I have stored it' }));
    await waitFor(() => expect(screen.queryByText(/rbk_secret_value_123/)).toBeNull());
  });

  it('shows the paid-plans notice with a billing link on 402', async () => {
    H.createApiKey.mockRejectedValue(new ApiError('PLAN_NOT_ENTITLED', 402));
    renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'New API key' }));
    fireEvent.change(await screen.findByLabelText(/Name/), { target: { value: 'Deploy' } });
    fireEvent.click(screen.getByRole('button', { name: 'Select all read' }));
    fireEvent.click(screen.getByRole('button', { name: 'Create key' }));
    await waitFor(() => expect(document.querySelector('a[href="/settings/billing"]')).not.toBeNull());
  });
});
