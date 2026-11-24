// @vitest-environment jsdom
/**
 * A16-UI: the invite form. It offers only the roles the caller's own role may hand out, and it
 * never claims mail was sent — the API reports `emailSent: false` when SMTP is unconfigured or
 * the send failed, and the invitation exists either way.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import type { OrgInvite } from '@/lib/api/orgs';

const { createInvite } = vi.hoisted(() => ({ createInvite: vi.fn() }));
const notifyShow = vi.spyOn(notifications, 'show').mockImplementation(() => 'notification-id');

vi.mock('@/lib/api/orgs', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/orgs')>()),
  createInvite: (...a: unknown[]) => createInvite(...a),
}));

import { InviteForm } from './InviteForm';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Element.prototype.scrollIntoView = vi.fn();

const invite = (over: Partial<OrgInvite> = {}): OrgInvite => ({
  id: 'inv-1',
  email: 'teammate@example.com',
  role: 'Member',
  createdAt: '2026-10-01T00:00:00Z',
  expiresAt: '2026-10-08T00:00:00Z',
  emailSent: true,
  ...over,
});

function renderForm(callerRole = 'Owner') {
  const onCreated = vi.fn();
  render(
    <MantineProvider>
      <Notifications />
      <InviteForm orgId="org-acme" callerRole={callerRole} onCreated={onCreated} />
    </MantineProvider>,
  );
  return onCreated;
}

/** Opens a Mantine Select and clicks one of its options. */
function chooseOption(comboboxName: string, optionName: string) {
  fireEvent.click(screen.getByRole('combobox', { name: comboboxName }));
  fireEvent.click(screen.getByRole('option', { name: optionName }));
}

beforeEach(() => {
  createInvite.mockReset();
  notifyShow.mockClear();
  createInvite.mockResolvedValue(invite());
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

describe('InviteForm', () => {
  it('invites the typed address as a Member by default and refreshes the list', async () => {
    const onCreated = renderForm();
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: '  teammate@example.com  ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send invite' }));

    await waitFor(() => expect(createInvite).toHaveBeenCalledWith('org-acme', {
      email: 'teammate@example.com',
      role: 'Member',
    }));
    expect(onCreated).toHaveBeenCalled();
    expect(notifyShow).toHaveBeenCalledWith(expect.objectContaining({ color: 'green' }));
  });

  it('offers only the roles an Admin may grant', () => {
    renderForm('Admin');
    fireEvent.click(screen.getByRole('combobox', { name: 'Role' }));
    const options = screen.getAllByRole('option').map((option) => option.textContent);
    expect(options).toEqual(['Admin', 'Member', 'Billing']);
  });

  it('sends the chosen role', async () => {
    renderForm('Owner');
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'teammate@example.com' } });
    chooseOption('Role', 'Billing');
    fireEvent.click(screen.getByRole('button', { name: 'Send invite' }));
    await waitFor(() => expect(createInvite).toHaveBeenCalledWith('org-acme', {
      email: 'teammate@example.com',
      role: 'Billing',
    }));
  });

  it('says the email was not sent rather than claiming it was', async () => {
    createInvite.mockResolvedValue(invite({ emailSent: false }));
    renderForm();
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'teammate@example.com' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send invite' }));

    expect(await screen.findByText(/this server did not send the email/i)).toBeTruthy();
    expect(screen.getByText(/teammate@example.com/)).toBeTruthy();
    expect(notifyShow).not.toHaveBeenCalled();
  });

  it('reports a refused invitation with the API message', async () => {
    createInvite.mockRejectedValue(new Error('that person is already a member'));
    const onCreated = renderForm();
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'teammate@example.com' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send invite' }));

    expect(await screen.findByText('That person is already a member of this workspace.')).toBeTruthy();
    expect(notifyShow).not.toHaveBeenCalled();
    expect(onCreated).not.toHaveBeenCalled();
  });

  it('renders nothing for a role that may not invite', () => {
    const { container } = render(
      <MantineProvider>
        <InviteForm orgId="org-acme" callerRole="Member" onCreated={vi.fn()} />
      </MantineProvider>,
    );
    expect(container.querySelector('form')).toBeNull();
  });
});
