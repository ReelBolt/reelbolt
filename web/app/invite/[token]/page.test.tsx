// @vitest-environment jsdom
/**
 * A16-UI: the invitation link page (/app/invite/{token}). It is the page the API's invite email
 * points at, so it has to work signed out — and it must not offer an action the API would refuse
 * (accepting as the wrong address is a 403; an unknown, revoked or used token is a 404 and an
 * expired one a 410).
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Suspense } from 'react';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { ApiError } from '@/lib/api/client';
import type { InvitationPreview } from '@/lib/api/orgs';
import type { UserInfo } from '@/lib/types/auth';

const H = vi.hoisted(() => ({
  push: vi.fn(),
  acceptInvite: vi.fn(),
  mutate: vi.fn(),
  preview: undefined as unknown,
  error: undefined as unknown,
  isLoading: false,
}));

vi.mock('next/navigation', () => ({ useRouter: () => ({ push: H.push, refresh: vi.fn(), prefetch: vi.fn() }) }));
vi.mock('@/lib/hooks/use-orgs', () => ({
  useInvitationPreview: () => ({
    data: H.preview,
    error: H.error,
    isLoading: H.isLoading,
    mutate: H.mutate,
  }),
  useAcceptInvite: () => H.acceptInvite,
}));

import InvitePage from './page';

const notifyShow = vi.spyOn(notifications, 'show').mockImplementation(() => 'notification-id');
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Element.prototype.scrollIntoView = vi.fn();

const PREVIEW: InvitationPreview = {
  organizationName: 'Acme Team',
  inviterName: 'Ada Lovelace',
  role: 'Member',
  email: 'invited@example.com',
  expiresAt: '2026-10-08T00:00:00Z',
  accountExists: true,
};

function signInAs(email: string) {
  const full: UserInfo = {
    email, isPlatformAdmin: false, mustChangePassword: false,
    org: 'org-personal', orgName: email, orgKind: 'Personal', orgRole: 'Owner',
  };
  document.cookie = `reelbolt_user=${encodeURIComponent(JSON.stringify(full))}; path=/`;
}

/**
 * The page unwraps `params` with React's `use`, so it suspends on the promise. Rendering inside
 * `await act(...)` flushes that, exactly like the project page's test does.
 */
async function renderPage() {
  await act(async () => {
    render(
      <MantineProvider>
        <Notifications />
        <Suspense fallback={null}>
          <InvitePage params={Promise.resolve({ token: 'tok-123' })} />
        </Suspense>
      </MantineProvider>,
    );
  });
}

beforeEach(() => {
  document.cookie = 'reelbolt_user=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
  H.push.mockReset();
  H.acceptInvite.mockReset();
  H.mutate.mockReset();
  H.preview = undefined;
  H.error = undefined;
  H.isLoading = false;
  H.acceptInvite.mockResolvedValue({ orgName: 'Acme Team', orgRole: 'Member' });
  notifyShow.mockClear();
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

describe('InvitePage', () => {
  it('says it is checking while the preview loads', async () => {
    H.isLoading = true;
    await renderPage();
    expect(await screen.findByText('Checking this invitation…')).toBeTruthy();
  });

  it('explains an expired invitation (410) and keeps the sign-in way out', async () => {
    H.error = new ApiError('invitation has expired', 410);
    await renderPage();
    expect(await screen.findByText('This invitation has expired')).toBeTruthy();
    expect(screen.getByText(/seven days/)).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Go to sign in' })).toBeTruthy();
  });

  it('explains an unknown, revoked or used invitation (404)', async () => {
    H.error = new ApiError('invitation not found', 404);
    await renderPage();
    expect(await screen.findByText('This invitation is no longer valid')).toBeTruthy();
    expect(screen.getByText(/revoked, already used/)).toBeTruthy();
  });

  it('offers a retry for anything else that went wrong', async () => {
    H.error = new Error('Network down');
    await renderPage();
    expect(await screen.findByText('We could not check this invitation')).toBeTruthy();
    expect(screen.getByText('Network down')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(H.mutate).toHaveBeenCalled();
  });

  it('shows what the invite is for and asks a signed-out visitor with an account to sign in', async () => {
    H.preview = PREVIEW;
    await renderPage();
    expect(await screen.findByText('Acme Team')).toBeTruthy();
    expect(screen.getByText(/Ada Lovelace invited/)).toBeTruthy();
    expect(screen.getByText('invited@example.com')).toBeTruthy();
    expect(screen.getByText(/Sign in as invited@example.com/)).toBeTruthy();
    // A Mantine Button rendered as a link is an anchor, so it is addressed by role "link".
    expect(screen.getByRole('link', { name: 'Sign in' })).toBeTruthy();
  });

  it('tells a signed-out visitor without an account to create one for the invited address', async () => {
    H.preview = { ...PREVIEW, accountExists: false };
    await renderPage();
    expect(await screen.findByText('invited@example.com')).toBeTruthy();
    expect(screen.getAllByText(/Create one with that address/).length).toBeGreaterThan(0);
    expect(screen.getByRole('link', { name: 'I already have an account' })).toBeTruthy();
  });

  it('joins the workspace when the signed-in address matches, then lands on the dashboard', async () => {
    H.preview = PREVIEW;
    signInAs('invited@example.com');
    await renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Join Acme Team' }));

    await waitFor(() => expect(H.acceptInvite).toHaveBeenCalledWith('tok-123'));
    await waitFor(() => expect(H.push).toHaveBeenCalledWith('/dashboard'));
    expect(notifyShow).toHaveBeenCalledWith(expect.objectContaining({ color: 'green' }));
  });

  it('refuses to offer joining when another address is signed in', async () => {
    H.preview = PREVIEW;
    signInAs('someone.else@example.com');
    await renderPage();
    expect(await screen.findByText(/you are signed in as someone.else@example.com/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Join Acme Team' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeTruthy();
  });

  it('reports a refused acceptance and stays on the page', async () => {
    H.preview = PREVIEW;
    signInAs('invited@example.com');
    H.acceptInvite.mockRejectedValue(new ApiError('this invitation was sent to a different email address', 403));
    await renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Join Acme Team' }));

    await waitFor(() => expect(notifyShow).toHaveBeenCalledWith({
      title: 'Could not join',
      message: 'this invitation was sent to a different email address',
      color: 'red',
    }));
    expect(H.push).not.toHaveBeenCalled();
  });
});
