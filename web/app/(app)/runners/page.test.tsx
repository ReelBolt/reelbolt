// @vitest-environment jsdom
/**
 * E7-UI: the workspace's runner list.
 *
 * The three things worth pinning: the empty state routes a user somewhere useful (including to the
 * marketing site, which the dashboard links to with a PLAIN anchor because next/link would prepend
 * the '/app' basePath), the list renders what each device says it can run, and revoke asks first.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { Notifications } from '@mantine/notifications';
import { ApiError } from '@/lib/api/client';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

const state = vi.hoisted(() => ({
  devices: [] as unknown[],
  error: undefined as unknown,
  mutate: vi.fn(),
  role: 'Owner',
}));
const revokeRunner = vi.hoisted(() => vi.fn());

vi.mock('@/lib/hooks/use-runners', () => ({
  useRunners: () => ({ data: state.devices, isLoading: false, error: state.error, mutate: state.mutate }),
}));
vi.mock('@/lib/hooks/use-orgs', () => ({
  useOrgMembers: () => ({ data: [] }),
}));
vi.mock('@/lib/hooks/use-auth', () => ({
  useAuth: () => ({ user: { orgRole: state.role }, activeOrgId: 'org-1' }),
}));
vi.mock('@/lib/api/runners', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api/runners')>();
  return { ...actual, revokeRunner };
});

import RunnersPage from './page';

beforeEach(() => {
  state.devices = [];
  state.error = undefined;
  state.role = 'Owner';
  state.mutate.mockReset();
  revokeRunner.mockReset();
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null,
    addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {},
    dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

function renderPage() {
  render(
    <SWRConfig value={{ provider: () => new Map() }}>
      <MantineProvider>
        <Notifications />
        <RunnersPage />
      </MantineProvider>
    </SWRConfig>,
  );
}

const oneDevice = {
  id: '11111111-1111-1111-1111-111111111111',
  name: 'Studio workstation',
  os: 'windows',
  arch: 'amd64',
  runnerVersion: '0.4.1',
  createdAt: '2026-10-01T09:00:00Z',
  lastSeenAt: new Date().toISOString(),
  pairedByUserId: '22222222-2222-2222-2222-222222222222',
  capabilities: { container: 'docker', jobTypes: ['remotion.workspace-session'] },
};

describe('Runners page', () => {
  it('offers both ways forward on an empty workspace, and links to the marketing site without the /app basePath', () => {
    renderPage();

    expect(screen.getByText('No runners yet')).toBeTruthy();
    expect(screen.getByRole('link', { name: /I have a pairing code/ }).getAttribute('href')).toBe('/runners/pair');

    // A plain anchor: next/link would emit '/app/runner', a route that does not exist.
    const download = screen.getByRole('link', { name: /Desktop runner/ });
    expect(download.getAttribute('href')).toBe('/runner');
  });

  it('says which platforms a runner is available for, so a macOS user is not left guessing (D26)', () => {
    renderPage();
    expect(screen.getByText(/Windows \(x64\) and Linux \(x64 or arm64\)\. macOS is not supported\./)).toBeTruthy();
  });

  it('lists the paired machines of the active workspace', () => {
    state.devices = [oneDevice];
    renderPage();

    expect(screen.queryByText('No runners yet')).toBeNull();
    expect(screen.getByText('Studio workstation')).toBeTruthy();
    expect(screen.getByText('Windows (x64)')).toBeTruthy();
    expect(screen.getByText('Video renders')).toBeTruthy();
  });

  it('asks before revoking, and revokes only on confirm', async () => {
    state.devices = [oneDevice];
    revokeRunner.mockResolvedValue({
      id: oneDevice.id,
      revokedAt: new Date().toISOString(),
      alreadyRevoked: false,
    });
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Revoke Studio workstation' }));

    // The confirmation names the machine and says what actually happens to it. Mantine mounts the
    // modal behind a transition, so it arrives a tick after the click.
    expect(await screen.findByText(/Revoke Studio workstation\?/)).toBeTruthy();
    expect(screen.getByText(/disappears from this list/)).toBeTruthy();
    expect(revokeRunner).not.toHaveBeenCalled();

    fireEvent.click(await screen.findByRole('button', { name: 'Revoke' }));
    await waitFor(() => expect(revokeRunner).toHaveBeenCalledWith(oneDevice.id));
    await waitFor(() => expect(state.mutate).toHaveBeenCalled());
  });

  it('reports a refusal from the server rather than hiding the control', async () => {
    state.devices = [oneDevice];
    revokeRunner.mockRejectedValue(new ApiError('insufficient_role', 403, [], { error: 'insufficient_role' }));
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Revoke Studio workstation' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Revoke' }));

    // The message names both routes to authority, because the client cannot tell which the caller has.
    await waitFor(() => expect(screen.getByText(/Only an Owner or Admin of this workspace/)).toBeTruthy());
    expect(state.mutate).not.toHaveBeenCalled();
  });
});
