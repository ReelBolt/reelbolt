// @vitest-environment jsdom
/**
 * A16-UI: the members page's role gating, which is the acceptance criterion for this slice.
 *
 * The page decides what to render — and what to FETCH — from the caller's role in the active
 * workspace, mirroring the API's rules (see lib/org-permissions): Owner/Admin manage members and
 * invitations, a Member sees the same list read-only and may leave, Billing may not read the
 * member list at all (403), and a Personal workspace hides the member and invite controls.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import type { Org, OrgInvite, OrgMember } from '@/lib/api/orgs';
import type { UserInfo } from '@/lib/types/auth';

const H = vi.hoisted(() => ({
  push: vi.fn(),
  switchOrg: vi.fn(),
  mutateMembers: vi.fn(),
  mutateInvites: vi.fn(),
  updateMember: vi.fn(),
  removeMember: vi.fn(),
  revokeInvite: vi.fn(),
  createInvite: vi.fn(),
  orgs: [] as unknown[],
  members: [] as unknown[],
  invites: [] as unknown[],
  memberCalls: [] as unknown[],
  inviteCalls: [] as unknown[],
}));

vi.mock('next/navigation', () => ({ useRouter: () => ({ push: H.push, refresh: vi.fn(), prefetch: vi.fn() }) }));
vi.mock('@/lib/hooks/use-orgs', () => ({
  useOrgs: () => ({ data: H.orgs, mutate: vi.fn() }),
  useOrgMembers: (orgId: string | null) => {
    H.memberCalls.push(orgId);
    return { data: orgId ? H.members : undefined, mutate: H.mutateMembers };
  },
  useOrgInvites: (orgId: string | null) => {
    H.inviteCalls.push(orgId);
    return { data: orgId ? H.invites : undefined, mutate: H.mutateInvites };
  },
  useSwitchOrg: () => H.switchOrg,
}));
vi.mock('@/lib/api/orgs', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/orgs')>()),
  updateMember: (...a: unknown[]) => H.updateMember(...a),
  removeMember: (...a: unknown[]) => H.removeMember(...a),
  revokeInvite: (...a: unknown[]) => H.revokeInvite(...a),
  createInvite: (...a: unknown[]) => H.createInvite(...a),
}));

import OrgMembersPage from './page';

const notifyShow = vi.spyOn(notifications, 'show').mockImplementation(() => 'notification-id');
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Element.prototype.scrollIntoView = vi.fn();

const MEMBER_ORG: Org = {
  id: 'org-acme', name: 'Acme Team', slug: 'acme-team', kind: 'Team', role: 'Owner', createdAt: '2026-10-01T00:00:00Z',
};
const PERSONAL_ORG: Org = {
  id: 'org-personal', name: 'tommaso@example.com', slug: 'personal', kind: 'Personal', role: 'Owner', createdAt: '2026-10-01T00:00:00Z',
};
const OWNER: OrgMember = { userId: 'u-owner', email: 'tommaso@example.com', displayName: 'Tommaso', role: 'Owner', joinedAt: '2026-10-01T00:00:00Z' };
const PLAIN: OrgMember = { userId: 'u-member', email: 'member@example.com', displayName: 'Member One', role: 'Member', joinedAt: '2026-10-02T00:00:00Z' };
const INVITE: OrgInvite = { id: 'inv-1', email: 'teammate@example.com', role: 'Member', createdAt: '2026-10-01T00:00:00Z', expiresAt: '2026-10-08T00:00:00Z', emailSent: true };

/** The readable cookie the Go API writes on login and on every switch-org. */
function signInAs(org: Org, over: Partial<UserInfo> = {}) {
  const full: UserInfo = {
    email: 'tommaso@example.com', isPlatformAdmin: false, mustChangePassword: false,
    org: org.id, orgName: org.name, orgKind: org.kind, orgRole: org.role,
    ...over,
  };
  document.cookie = `reelbolt_user=${encodeURIComponent(JSON.stringify(full))}; path=/`;
}

function renderPage(
  activeOrg: Org = MEMBER_ORG,
  orgs: Org[] = [PERSONAL_ORG, activeOrg],
  email = 'tommaso@example.com',
) {
  H.orgs = orgs;
  signInAs(activeOrg, { email });
  render(
    <MantineProvider>
      <Notifications />
      <OrgMembersPage />
    </MantineProvider>,
  );
}

beforeEach(() => {
  document.cookie = 'reelbolt_user=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
  H.push.mockReset();
  H.switchOrg.mockReset();
  H.mutateMembers.mockReset();
  H.mutateInvites.mockReset();
  H.updateMember.mockReset();
  H.removeMember.mockReset();
  H.revokeInvite.mockReset();
  H.createInvite.mockReset();
  H.memberCalls.length = 0;
  H.inviteCalls.length = 0;
  notifyShow.mockClear();
  H.members = [OWNER, PLAIN];
  H.invites = [INVITE];
  H.updateMember.mockResolvedValue(undefined);
  H.removeMember.mockResolvedValue(undefined);
  H.revokeInvite.mockResolvedValue(undefined);
  H.createInvite.mockResolvedValue({ ...INVITE, id: 'inv-2', emailSent: true });
  H.switchOrg.mockResolvedValue({});
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

describe('OrgMembersPage', () => {
  it('gives an Owner the member list, the invite form and the pending invitations', () => {
    renderPage();
    expect(screen.getByText('Acme Team')).toBeTruthy();
    expect(screen.getByText('member@example.com')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Invite someone' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Pending invitations' })).toBeTruthy();
    expect(screen.getByText('teammate@example.com')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Change role for member@example.com' })).toBeTruthy();
    expect(H.memberCalls).toEqual(['org-acme']);
    expect(H.inviteCalls).toEqual(['org-acme']);
  });

  it('gives an Admin the same controls but not over an Owner', () => {
    renderPage({ ...MEMBER_ORG, role: 'Admin' });
    expect(screen.getByRole('heading', { name: 'Invite someone' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Change role for tommaso@example.com' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Change role for member@example.com' })).toBeTruthy();
  });

  it('gives a Member a read-only list, no invites, and a way to leave', () => {
    renderPage(
      { ...MEMBER_ORG, role: 'Member' },
      [PERSONAL_ORG, { ...MEMBER_ORG, role: 'Member' }],
      'member@example.com',
    );
    expect(screen.getByText('member@example.com')).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Invite someone' })).toBeNull();
    expect(screen.queryByRole('heading', { name: 'Pending invitations' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Change role for member@example.com' })).toBeNull();
    // The Member may still leave — the one member action the API grants them.
    expect(screen.getByRole('button', { name: 'Leave workspace' })).toBeTruthy();
    expect(screen.getByText(/only an Owner or Admin can change roles/)).toBeTruthy();
    // Members may list members, but never invitations.
    expect(H.memberCalls).toEqual(['org-acme']);
    expect(H.inviteCalls).toEqual([null]);
  });

  it('tells a Billing caller the members are not visible, and asks the API for nothing', () => {
    renderPage({ ...MEMBER_ORG, role: 'Billing' }, [PERSONAL_ORG, { ...MEMBER_ORG, role: 'Billing' }]);
    expect(screen.getByText('Members are not visible to your role')).toBeTruthy();
    expect(H.memberCalls).toEqual([null]);
    expect(H.inviteCalls).toEqual([null]);
    expect(screen.queryByRole('heading', { name: 'Invite someone' })).toBeNull();
  });

  it('hides the member and invite controls in a Personal workspace', () => {
    renderPage({ ...PERSONAL_ORG, role: 'Owner' }, [PERSONAL_ORG]);
    expect(screen.getByText('Your personal workspace')).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Invite someone' })).toBeNull();
    expect(screen.queryByRole('heading', { name: 'Pending invitations' })).toBeNull();
    expect(H.memberCalls).toEqual([null]);
    expect(H.inviteCalls).toEqual([null]);
  });

  it('changes a role through the API and refreshes the list', async () => {
    renderPage({ ...MEMBER_ORG, role: 'Admin' });
    fireEvent.click(screen.getByRole('button', { name: 'Change role for member@example.com' }));
    const items = await screen.findAllByRole('menuitem');
    fireEvent.click(items.find((item) => item.textContent === 'Billing')!);

    await waitFor(() => expect(H.updateMember).toHaveBeenCalledWith('org-acme', 'u-member', { role: 'Billing' }));
    expect(H.mutateMembers).toHaveBeenCalled();
    expect(H.switchOrg).not.toHaveBeenCalled(); // not the caller's own row
    expect(notifyShow).toHaveBeenCalledWith(expect.objectContaining({ color: 'green' }));
  });

  it('re-issues the session when the caller changes their own role', async () => {
    // Two Owners, so demoting the caller is allowed (the last Owner may not be demoted at all).
    H.members = [OWNER, { ...PLAIN, userId: 'u-owner2', email: 'owner2@example.com', role: 'Owner' }];
    renderPage({ ...MEMBER_ORG, role: 'Owner' }, [PERSONAL_ORG, MEMBER_ORG]);
    fireEvent.click(screen.getByRole('button', { name: 'Change role for tommaso@example.com' }));
    const items = await screen.findAllByRole('menuitem');
    fireEvent.click(items.find((item) => item.textContent === 'Admin')!);

    await waitFor(() => expect(H.updateMember).toHaveBeenCalledWith('org-acme', 'u-owner', { role: 'Admin' }));
    // The readable session cookie would otherwise keep claiming Owner.
    await waitFor(() => expect(H.switchOrg).toHaveBeenCalledWith('org-acme'));
  });

  it('removes another member after a confirmation', async () => {
    renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Remove member@example.com' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Remove' }));

    await waitFor(() => expect(H.removeMember).toHaveBeenCalledWith('org-acme', 'u-member'));
    expect(H.mutateMembers).toHaveBeenCalled();
    expect(H.push).not.toHaveBeenCalled();
  });

  it('leaves the workspace and lands on the personal one', async () => {
    renderPage(
      { ...MEMBER_ORG, role: 'Member' },
      [PERSONAL_ORG, { ...MEMBER_ORG, role: 'Member' }],
      'member@example.com',
    );
    fireEvent.click(screen.getByRole('button', { name: 'Leave workspace' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Leave' }));

    await waitFor(() => expect(H.removeMember).toHaveBeenCalledWith('org-acme', 'u-member'));
    await waitFor(() => expect(H.switchOrg).toHaveBeenCalledWith('org-personal'));
    expect(H.push).toHaveBeenCalledWith('/dashboard');
  });

  it('revokes an invitation after a confirmation', async () => {
    renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Revoke invitation for teammate@example.com' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Revoke' }));

    await waitFor(() => expect(H.revokeInvite).toHaveBeenCalledWith('org-acme', 'inv-1'));
    expect(H.mutateInvites).toHaveBeenCalled();
  });

  it('resends an invitation by inviting the same address and role again (A27)', async () => {
    renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Resend invitation to teammate@example.com' }));
    await waitFor(() =>
      expect(H.createInvite).toHaveBeenCalledWith('org-acme', { email: 'teammate@example.com', role: 'Member' }),
    );
    expect(H.mutateInvites).toHaveBeenCalled();
  });

  it('gives the personal workspace a way forward and names the list once (A27)', () => {
    renderPage(PERSONAL_ORG, [PERSONAL_ORG]);
    expect(screen.getByRole('button', { name: 'Create a team workspace' })).toBeTruthy();
  });

  it('a failed resend says the earlier link may already be dead', async () => {
    H.createInvite.mockRejectedValue(new Error('network down'));
    renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Resend invitation to teammate@example.com' }));
    await waitFor(() =>
      expect(notifyShow).toHaveBeenCalledWith(expect.objectContaining({ message: expect.stringMatching(/previous link may no longer work/) })),
    );
  });
});
