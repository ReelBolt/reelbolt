// @vitest-environment jsdom
/**
 * A17-UI: the workspace providers page's role gating — the acceptance criterion for this slice.
 *
 * The page decides what to render — and what to FETCH — from the caller's role in the active
 * workspace, mirroring the API (`OrgInferenceProvidersController`): Owner/Admin manage the
 * workspace's own providers, a Member sees neither the list nor the create control (the endpoint
 * answers 403), the Billing role has no provider access at all, and a session with no workspace
 * role gets nothing.
 *
 * A Member is not left without a choice: the step editors offer them the member-facing `selectable`
 * list. What they may not do is MANAGE a provider, which is what this page is.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import type { Org } from '@/lib/api/orgs';
import type { InferenceProvider } from '@/lib/types/inference-provider';
import type { UserInfo } from '@/lib/types/auth';

const H = vi.hoisted(() => ({
  listCalls: [] as boolean[],
  mutate: vi.fn(),
  deleteProvider: vi.fn(),
  providers: [] as unknown[],
  orgs: [] as unknown[],
}));

vi.mock('@/lib/hooks/use-orgs', () => ({ useOrgs: () => ({ data: H.orgs, mutate: vi.fn() }) }));
vi.mock('@/lib/hooks/use-inference-providers', () => ({
  useProviders: (scope: string, enabled: boolean) => {
    // The workspace page must only ever read the workspace's own providers.
    if (scope !== 'org') throw new Error(`unexpected scope ${scope}`);
    H.listCalls.push(enabled);
    return { data: enabled ? H.providers : undefined, isLoading: false, error: undefined, mutate: H.mutate };
  },
}));
vi.mock('@/lib/api/inference-providers', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/inference-providers')>()),
  deleteProvider: (...a: unknown[]) => H.deleteProvider(...a),
}));

import OrgInferenceProvidersPage from './page';

const notifyShow = vi.spyOn(notifications, 'show').mockImplementation(() => 'notification-id');
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Element.prototype.scrollIntoView = vi.fn();

const TEAM: Org = {
  id: 'org-acme', name: 'Acme Team', slug: 'acme-team', kind: 'Team', role: 'Owner', createdAt: '2026-10-01T00:00:00Z',
};

const PROVIDER: InferenceProvider = {
  id: 'p-1', organizationId: 'org-acme', name: 'Acme vLLM', kind: 'OpenAICompatible',
  endpoint: 'https://vllm.acme.example/v1',
  hasApiKey: true, apiKeyLastFour: 'abcd', isEnabled: true, isSystem: false,
  timeoutSeconds: 30, createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
  personalOAuthMode: false,
  models: [
    {
      id: 'm-1', modelCode: 'llama-3-8b', displayName: 'Llama 3', isEnabled: true,
      capabilities: [{
        id: 'c-1', capability: 'Chat', isDefault: true, isEnabled: true, priceSource: 'Table',
        lastTestAt: null, lastTestOk: null, lastTestError: null, prices: null,
      }],
    },
  ],
};

/** The readable cookie the Go API writes on login and on every switch-org. */
function signInAs(org: Org | null, over: Partial<UserInfo> = {}) {
  const full: UserInfo = org
    ? { email: 'tommaso@example.com', isPlatformAdmin: false, mustChangePassword: false,
        org: org.id, orgName: org.name, orgKind: org.kind, orgRole: org.role, ...over }
    : { email: 'tommaso@example.com', isPlatformAdmin: false, mustChangePassword: false, ...over };
  document.cookie = `reelbolt_user=${encodeURIComponent(JSON.stringify(full))}; path=/`;
}

function renderPage(activeOrg: Org | null = TEAM) {
  H.orgs = activeOrg ? [activeOrg] : [];
  signInAs(activeOrg);
  render(
    <MantineProvider>
      <Notifications />
      <OrgInferenceProvidersPage />
    </MantineProvider>,
  );
}

beforeEach(() => {
  document.cookie = 'reelbolt_user=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
  H.listCalls.length = 0;
  H.mutate.mockReset();
  H.deleteProvider.mockReset();
  H.deleteProvider.mockResolvedValue(undefined);
  H.providers = [PROVIDER];
  notifyShow.mockClear();
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

describe('OrgInferenceProvidersPage', () => {
  it('gives an Owner the workspace list and the create control', () => {
    renderPage();
    expect(screen.getByText('Acme vLLM')).toBeTruthy();
    // One row per account, with what its models serve.
    expect(screen.getByText('Chat')).toBeTruthy();
    expect(screen.getByText('•••• abcd')).toBeTruthy();
    expect(screen.getByRole('button', { name: /New provider/ })).toBeTruthy();
    // The row link is the workspace's detail page, not the platform admin's.
    expect(screen.getByRole('link', { name: 'Edit Acme vLLM' }).getAttribute('href')).toBe('/org/inference-providers/p-1');
    expect(H.listCalls).toEqual([true]);
  });

  it('gives an Admin the same controls', () => {
    renderPage({ ...TEAM, role: 'Admin' });
    expect(screen.getByText('Acme vLLM')).toBeTruthy();
    expect(screen.getByRole('button', { name: /New provider/ })).toBeTruthy();
    expect(H.listCalls).toEqual([true]);
  });

  it('gives a Member no list and no create control, and asks the API for nothing', () => {
    renderPage({ ...TEAM, role: 'Member' });
    expect(screen.getByText(/managed by an Owner or Admin/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /New provider/ })).toBeNull();
    expect(screen.queryByText('Acme vLLM')).toBeNull();
    // The endpoint answers 403 for a Member: the page must not fire the request it knows fails.
    expect(H.listCalls).toEqual([false]);
  });

  it('tells a Billing caller it has no provider access, and asks the API for nothing', () => {
    renderPage({ ...TEAM, role: 'Billing' });
    expect(screen.getByText(/Billing role has no provider access/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /New provider/ })).toBeNull();
    expect(H.listCalls).toEqual([false]);
  });

  it('shows nothing to a session with no workspace role', () => {
    renderPage(null);
    expect(screen.getByText('No workspace role')).toBeTruthy();
    expect(H.listCalls).toEqual([false]);
  });

  it('deletes a workspace provider through the org endpoint after a confirmation', async () => {
    renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Delete Acme vLLM' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete' }));

    // The scope argument is the whole point: the same call must not go to the platform endpoint.
    await waitFor(() => expect(H.deleteProvider).toHaveBeenCalledWith('p-1', 'org'));
    expect(H.mutate).toHaveBeenCalled();
  });

  it('says the workspace uses ReelBolt providers when it has none of its own', () => {
    H.providers = [];
    renderPage();
    expect(screen.getByText('No providers of your own')).toBeTruthy();
  });
});
