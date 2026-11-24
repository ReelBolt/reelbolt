// @vitest-environment jsdom
/**
 * A16-UI: the members table's role-conditional controls. What each row offers is the server's own
 * rule set — an Admin can never touch an Owner, nobody can demote or remove the last Owner, and a
 * Member sees the list read-only with the one action the API grants them (leaving).
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { OrgMember } from '@/lib/api/orgs';
import { MembersTable } from './MembersTable';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Element.prototype.scrollIntoView = vi.fn();

const member = (over: Partial<OrgMember>): OrgMember => ({
  userId: 'u1',
  email: 'person@example.com',
  displayName: 'Person',
  role: 'Member',
  joinedAt: '2026-10-01T00:00:00Z',
  ...over,
});

const OWNER = member({ userId: 'u-owner', email: 'owner@example.com', displayName: 'Owner One', role: 'Owner' });
const OTHER_OWNER = member({ userId: 'u-owner2', email: 'owner2@example.com', displayName: 'Owner Two', role: 'Owner' });
const PLAIN = member({ userId: 'u-member', email: 'member@example.com', displayName: 'Member One', role: 'Member' });

function renderTable(props: Partial<Parameters<typeof MembersTable>[0]> = {}) {
  const onRoleChange = vi.fn();
  const onRemove = vi.fn();
  render(
    <MantineProvider>
      <MembersTable
        members={[OWNER, OTHER_OWNER, PLAIN]}
        callerRole="Owner"
        callerEmail="owner@example.com"
        onRoleChange={onRoleChange}
        onRemove={onRemove}
        {...props}
      />
    </MantineProvider>,
  );
  return { onRoleChange, onRemove };
}

/**
 * Opens the role menu of one row and returns its items. Mantine mounts the dropdown
 * asynchronously into a portal, so the items are captured once and addressed by their own text
 * (the same approach OrgSwitcher.test.tsx uses for its workspace menu).
 */
async function openRoleMenu(email: string) {
  fireEvent.click(screen.getByRole('button', { name: `Change role for ${email}` }));
  const items = await screen.findAllByRole('menuitem');
  return {
    labels: items.map((item) => item.textContent),
    item(label: string) {
      const found = items.find((candidate) => candidate.textContent === label);
      if (!found) throw new Error(`No role menu item labelled "${label}"`);
      return found;
    },
  };
}

beforeEach(() => {
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

describe('MembersTable', () => {
  it('marks the caller and offers no self-removal control to a manager', () => {
    renderTable();
    expect(screen.getByText('You')).toBeTruthy();
    // A manager's own row offers "Leave", never "Remove"; other rows offer "Remove".
    expect(screen.queryByRole('button', { name: 'Remove owner@example.com' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Leave workspace' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Remove member@example.com' })).toBeTruthy();
  });

  it('lets an Owner grant Owner but an Admin not even see it', async () => {
    renderTable();
    const asOwner = await openRoleMenu('member@example.com');
    expect(asOwner.labels).toEqual(['Owner', 'Admin', 'Member', 'Billing']);
    // The row's own current role is offered but disabled: a no-op role change is not a change.
    expect(asOwner.item('Member').getAttribute('data-disabled')).not.toBeNull();
    cleanup();

    renderTable({ callerRole: 'Admin', callerEmail: 'admin@example.com' });
    const asAdmin = await openRoleMenu('member@example.com');
    expect(asAdmin.labels).toEqual(['Admin', 'Member', 'Billing']);
    expect(() => asAdmin.item('Owner')).toThrow();
  });

  it('offers an Admin no control on an Owner row at all', () => {
    renderTable({ callerRole: 'Admin', callerEmail: 'admin@example.com' });
    expect(screen.queryByRole('button', { name: 'Change role for owner@example.com' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Remove owner@example.com' })).toBeNull();
  });

  it('says "Last owner" and offers nothing that would demote or remove them', () => {
    renderTable({ members: [OWNER, PLAIN], callerRole: 'Owner', callerEmail: 'owner@example.com' });
    expect(screen.getByText('Last owner')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Change role for owner@example.com' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Remove owner@example.com' })).toBeNull();
  });

  it('reports a chosen role to the caller', async () => {
    const { onRoleChange } = renderTable();
    const menu = await openRoleMenu('member@example.com');
    fireEvent.click(menu.item('Billing'));
    expect(onRoleChange).toHaveBeenCalledWith(PLAIN, 'Billing');
  });

  it('reports a removal to the caller', () => {
    const { onRemove } = renderTable();
    fireEvent.click(screen.getByRole('button', { name: 'Remove member@example.com' }));
    expect(onRemove).toHaveBeenCalledWith(PLAIN);
  });

  it('gives a Member a read-only list with only the leave action on their own row', () => {
    renderTable({ callerRole: 'Member', callerEmail: 'member@example.com', members: [OWNER, PLAIN] });
    expect(screen.queryByRole('button', { name: 'Change role for member@example.com' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Remove owner@example.com' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Leave workspace' })).toBeTruthy();
  });

  it('gives a Member no leave action on someone else’s row', () => {
    renderTable({ callerRole: 'Member', callerEmail: 'someone-else@example.com', members: [OWNER, PLAIN] });
    expect(screen.queryByRole('button', { name: 'Leave workspace' })).toBeNull();
    expect(screen.queryByRole('button', { name: /Change role/ })).toBeNull();
  });
});
