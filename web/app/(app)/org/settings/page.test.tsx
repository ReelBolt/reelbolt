// @vitest-environment jsdom
/**
 * A16-UI: workspace settings. Rename follows the API's rule (Owner/Admin), delete follows a
 * stricter one (Owner only, Team only), a Personal workspace is never renamed from here because
 * the switcher always labels it "Personal" (decision D1), and both destructive paths move the
 * session off a workspace it can no longer be in.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import type { Org } from '@/lib/api/orgs';
import type { UserInfo } from '@/lib/types/auth';

const H = vi.hoisted(() => ({
  push: vi.fn(),
  switchOrg: vi.fn(),
  mutateOrgs: vi.fn(),
  updateOrg: vi.fn(),
  deleteOrg: vi.fn(),
  createOrg: vi.fn(),
  orgs: [] as unknown[],
  devices: [] as unknown[] | undefined,
}));

vi.mock('next/navigation', () => ({ useRouter: () => ({ push: H.push, refresh: vi.fn(), prefetch: vi.fn() }) }));
vi.mock('@/lib/hooks/use-orgs', () => ({
  useOrgs: () => ({ data: H.orgs, mutate: H.mutateOrgs }),
  useSwitchOrg: () => H.switchOrg,
}));
// E8-UI: the compute card reads the workspace's paired machines so the choice is made with the
// machines it would use in sight. H.devices === undefined is the "list could not be read" case.
vi.mock('@/lib/hooks/use-runners', () => ({
  useRunners: () => ({ data: H.devices }),
}));
vi.mock('@/lib/api/orgs', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/orgs')>()),
  updateOrg: (...a: unknown[]) => H.updateOrg(...a),
  deleteOrg: (...a: unknown[]) => H.deleteOrg(...a),
  createOrg: (...a: unknown[]) => H.createOrg(...a),
}));

import OrgSettingsPage from './page';

const notifyShow = vi.spyOn(notifications, 'show').mockImplementation(() => 'notification-id');
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Element.prototype.scrollIntoView = vi.fn();

const TEAM: Org = {
  id: 'org-acme', name: 'Acme Team', slug: 'acme-team', kind: 'Team', role: 'Owner',
  createdAt: '2026-10-01T00:00:00Z', computePreference: 'Auto',
};
const PERSONAL: Org = {
  id: 'org-personal', name: 'tommaso@example.com', slug: 'personal', kind: 'Personal', role: 'Owner', createdAt: '2026-10-01T00:00:00Z',
};

function signInAs(org: Org) {
  const full: UserInfo = {
    email: 'tommaso@example.com', isPlatformAdmin: false, mustChangePassword: false,
    org: org.id, orgName: org.name, orgKind: org.kind, orgRole: org.role,
  };
  document.cookie = `reelbolt_user=${encodeURIComponent(JSON.stringify(full))}; path=/`;
}

function renderPage(activeOrg: Org = TEAM, orgs: Org[] = [PERSONAL, activeOrg]) {
  H.orgs = orgs;
  signInAs(activeOrg);
  render(
    <MantineProvider>
      <Notifications />
      <OrgSettingsPage />
    </MantineProvider>,
  );
}

const saveButton = () => screen.getByRole('button', { name: 'Save' }) as HTMLButtonElement;

beforeEach(() => {
  document.cookie = 'reelbolt_user=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
  H.push.mockReset();
  H.switchOrg.mockReset();
  H.mutateOrgs.mockReset();
  H.updateOrg.mockReset();
  H.deleteOrg.mockReset();
  H.createOrg.mockReset();
  notifyShow.mockClear();
  H.devices = [];
  H.updateOrg.mockResolvedValue({ ...TEAM, name: 'Acme Two' });
  H.deleteOrg.mockResolvedValue(undefined);
  H.switchOrg.mockResolvedValue({});
  H.createOrg.mockResolvedValue({ ...TEAM, id: 'org-new', name: 'New Team' });
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

describe('OrgSettingsPage', () => {
  it('lets an Owner rename the workspace, and keeps Save off until something changed', async () => {
    renderPage();
    const input = screen.getByLabelText('Name') as HTMLInputElement;
    expect(input.value).toBe('Acme Team');
    expect(saveButton().disabled).toBe(true);

    fireEvent.change(input, { target: { value: 'Acme Two' } });
    expect(saveButton().disabled).toBe(false);
    fireEvent.click(saveButton());

    await waitFor(() => expect(H.updateOrg).toHaveBeenCalledWith('org-acme', { name: 'Acme Two' }));
    expect(H.mutateOrgs).toHaveBeenCalled();
    expect(notifyShow).toHaveBeenCalledWith(expect.objectContaining({ color: 'green' }));
  });

  it('does not send a rename that changes nothing', () => {
    renderPage();
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: '  Acme Team  ' } });
    expect(saveButton().disabled).toBe(true);
    expect(H.updateOrg).not.toHaveBeenCalled();
  });

  it('never offers to rename a Personal workspace, whose label is always "Personal"', () => {
    renderPage({ ...PERSONAL, role: 'Owner' }, [PERSONAL]);
    expect(screen.queryByLabelText('Name')).toBeNull();
    expect(screen.getByText(/personal workspace/i)).toBeTruthy();
    expect(screen.queryByText('Delete workspace')).toBeNull();
  });

  it('lets an Admin rename but reserves deletion for the Owner', () => {
    renderPage({ ...TEAM, role: 'Admin' }, [PERSONAL, { ...TEAM, role: 'Admin' }]);
    expect(screen.getByLabelText('Name')).toBeTruthy();
    expect(screen.getByText('Only the workspace owner can delete it.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Delete this workspace' })).toBeNull();
  });

  it('is read-only for a Member', () => {
    renderPage({ ...TEAM, role: 'Member' }, [PERSONAL, { ...TEAM, role: 'Member' }]);
    expect(screen.queryByLabelText('Name')).toBeNull();
    expect(screen.getByText(/settings are read-only for you/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Delete this workspace' })).toBeNull();
  });

  it('deletes a Team workspace, then moves the session to the personal one', async () => {
    renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Delete this workspace' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete workspace' }));

    await waitFor(() => expect(H.deleteOrg).toHaveBeenCalledWith('org-acme'));
    await waitFor(() => expect(H.switchOrg).toHaveBeenCalledWith('org-personal'));
    expect(H.push).toHaveBeenCalledWith('/dashboard');
  });

  it('creates a team workspace and switches into it', async () => {
    renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'New team workspace' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('Workspace name'), { target: { value: 'New Team' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Create workspace' }));

    await waitFor(() => expect(H.createOrg).toHaveBeenCalledWith({ name: 'New Team' }));
    await waitFor(() => expect(H.switchOrg).toHaveBeenCalledWith('org-new'));
    expect(H.push).toHaveBeenCalledWith('/dashboard');
  });

  it('creates a team workspace from a Personal workspace too', async () => {
    renderPage({ ...PERSONAL, role: 'Owner' }, [PERSONAL]);
    fireEvent.click(screen.getByRole('button', { name: 'New team workspace' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('Workspace name'), { target: { value: 'New Team' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Create workspace' }));
    await waitFor(() => expect(H.createOrg).toHaveBeenCalled());
  });

  // ---- E8-UI: where the workspace's work runs ----

  it('offers the four values the engine switches over, and states how the work is split', () => {
    renderPage();
    for (const label of ['Automatic (the default)', 'Never use my machines', 'Prefer my machines', 'Only my machines']) {
      expect(screen.getByRole('radio', { name: label })).toBeTruthy();
    }
    expect(screen.getByText(/everything else runs on ReelBolt/i)).toBeTruthy();
    expect(screen.queryByText(/not a guarantee|cloud pool is not in service/i)).toBeNull();
  });

  it('preselects the value the workspace actually stores', () => {
    renderPage({ ...TEAM, computePreference: 'PreferRunner' }, [PERSONAL, { ...TEAM, computePreference: 'PreferRunner' }]);
    expect((screen.getByRole('radio', { name: 'Prefer my machines' }) as HTMLInputElement).checked).toBe(true);
  });

  it('saves the choice an Owner makes', async () => {
    renderPage();
    fireEvent.click(screen.getByRole('radio', { name: 'Only my machines' }));
    await waitFor(() => expect(H.updateOrg).toHaveBeenCalledWith('org-acme', { computePreference: 'RunnerOnly' }));
    expect(H.mutateOrgs).toHaveBeenCalled();
    expect(notifyShow).toHaveBeenCalledWith(expect.objectContaining({ color: 'green' }));
  });

  it('warns before the choice that fails a run instead of falling back', () => {
    renderPage();
    expect(screen.getByText(/the step stops with a clear message/)).toBeTruthy();
    expect(screen.getByText(/reported a container runtime/)).toBeTruthy();
  });

  it('states how many machines were actually heard from, never that they are online', () => {
    H.devices = [
      { id: 'd1', name: 'Studio', lastSeenAt: new Date().toISOString() },
      { id: 'd2', name: 'Old box', lastSeenAt: '2026-01-01T00:00:00Z' },
    ];
    renderPage();
    expect(screen.getByText(/2 paired machines, 1 heard from in the last few minutes/)).toBeTruthy();
  });

  it('claims nothing about machines when the runner list could not be read', () => {
    H.devices = undefined;
    renderPage();
    expect(screen.queryByText(/no paired machines/i)).toBeNull();
    expect(screen.getByText(/See this workspace's paired machines/i)).toBeTruthy();
  });

  it('is read-only for a Member, who is told what the workspace is set to instead', () => {
    renderPage({ ...TEAM, role: 'Member', computePreference: 'RunnerOnly' }, [PERSONAL, { ...TEAM, role: 'Member', computePreference: 'RunnerOnly' }]);
    expect(screen.queryByRole('radio', { name: 'Only my machines' })).toBeNull();
    expect(screen.getByText(/set to \u201cOnly my machines\u201d/)).toBeTruthy();
    expect(H.updateOrg).not.toHaveBeenCalled();
  });
});
