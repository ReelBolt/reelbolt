// @vitest-environment jsdom
/**
 * E7-UI: the pairing approval screen.
 *
 * This is the human end of an anti-phishing control, so the assertions that matter are about what
 * the screen TELLS the user before they click: approve only a code shown on a device you own, check
 * the code character for character, and every Owner is emailed afterwards. The other half is that
 * the screen never sharpens an answer the server deliberately blurs (D5) — a 404 says one thing,
 * whatever caused it, and never 'you do not have permission'.
 */
import { Suspense } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { ApiError } from '@/lib/api/client';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

let currentSearch = '';
const replace = vi.fn((url: string) => { currentSearch = url.split('?')[1] ?? ''; });
vi.mock('next/navigation', () => ({
  useRouter: () => ({ replace, push: vi.fn() }),
  useSearchParams: () => new URLSearchParams(currentSearch),
}));

const state = vi.hoisted(() => ({
  pairing: undefined as unknown,
  error: undefined as unknown,
  orgs: [] as unknown[],
}));
const approvePairing = vi.hoisted(() => vi.fn());
const denyPairing = vi.hoisted(() => vi.fn());

vi.mock('@/lib/hooks/use-runners', () => ({
  usePairingRequest: () => ({ data: state.pairing, isLoading: false, error: state.error }),
}));
vi.mock('@/lib/hooks/use-orgs', () => ({ useOrgs: () => ({ data: state.orgs }) }));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ activeOrgId: 'org-team' }) }));
vi.mock('@/lib/api/runners', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api/runners')>();
  return { ...actual, approvePairing, denyPairing };
});

import PairRunnerPage from './page';

const CODE = '7HQD-4M2R';
const NORMALIZED = '7HQD4M2R';

const pairing = {
  userCode: CODE,
  name: 'DESKTOP-7F2K',
  os: 'windows',
  arch: 'amd64',
  version: '0.4.1',
  requestedFromIp: '203.0.113.7',
  requestedAt: new Date(Date.now() - 60_000).toISOString(),
  expiresAt: new Date(Date.now() + 9 * 60_000).toISOString(),
};

const personalOrg = { id: 'org-personal', name: 'Dana', slug: 'dana', kind: 'Personal', role: 'Owner', createdAt: '2026-01-01T00:00:00Z' };
const teamOrg = { id: 'org-team', name: 'Acme Studio', slug: 'acme', kind: 'Team', role: 'Owner', createdAt: '2026-01-01T00:00:00Z' };

beforeEach(() => {
  currentSearch = '';
  replace.mockClear();
  state.pairing = undefined;
  state.error = undefined;
  state.orgs = [personalOrg, teamOrg];
  approvePairing.mockReset();
  denyPairing.mockReset();
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
        <Suspense fallback={null}>
          <PairRunnerPage />
        </Suspense>
      </MantineProvider>
    </SWRConfig>,
  );
}

describe('Pair runner page', () => {
  it('asks for the code when the link carried none', () => {
    renderPage();
    expect(screen.getByLabelText('Pairing code')).toBeTruthy();
  });

  it('refuses a code that is not a code, before any lookup', () => {
    renderPage();
    fireEvent.change(screen.getByLabelText('Pairing code'), { target: { value: 'OOPS' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    expect(screen.getByText(/not a pairing code/)).toBeTruthy();
    expect(replace).not.toHaveBeenCalled();
  });

  it('normalizes what the user typed into the URL', () => {
    renderPage();
    fireEvent.change(screen.getByLabelText('Pairing code'), { target: { value: '7hqd 4m2r' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    expect(replace).toHaveBeenCalledWith('/runners/pair?code=' + NORMALIZED);
  });

  it('warns that approving attaches a machine to the workspace, before showing any approve button', () => {
    currentSearch = 'code=' + CODE;
    state.pairing = pairing;
    renderPage();

    expect(screen.getByText('Only approve a code shown on a device you own')).toBeTruthy();
    expect(screen.getByText(/they are trying to attach their machine to your workspace/)).toBeTruthy();
    // The anti-phishing follow-up: the code has to be checked against the device's own screen.
    expect(screen.getByText(/character for character/)).toBeTruthy();
    expect(screen.getByText(/Every\s+Owner of the workspace is emailed when a device is approved/)).toBeTruthy();
  });

  it('shows the device, the code and the network address it came from — with no invented location', () => {
    currentSearch = 'code=' + CODE;
    state.pairing = pairing;
    renderPage();

    expect(screen.getByText('DESKTOP-7F2K')).toBeTruthy();
    expect(screen.getByText(CODE)).toBeTruthy();
    expect(screen.getByText('Windows (x64)')).toBeTruthy();
    expect(screen.getByText('203.0.113.7')).toBeTruthy();
    expect(screen.getByText(/does not look up a location/)).toBeTruthy();
  });

  it('approves into the workspace the user picked, and says the Owners were emailed', async () => {
    currentSearch = 'code=' + CODE;
    state.pairing = pairing;
    approvePairing.mockResolvedValue({ deviceId: 'd-1', organizationId: 'org-personal' });
    renderPage();

    // The active workspace is the Team org (use-auth mock), so it is the default the screen offers.
    expect(screen.getByDisplayValue('Acme Studio')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Approve this device/ }));
    await waitFor(() => expect(approvePairing).toHaveBeenCalledWith(NORMALIZED, 'org-team'));

    // The title appears twice by design: the page heading and the confirmation alert.
    await waitFor(() => expect(screen.getAllByText('Device approved').length).toBeGreaterThan(0));
    expect(screen.getByText(/Every Owner\s+of that workspace has been emailed/)).toBeTruthy();
  });

  it('denies without naming a workspace, and says nothing was paired', async () => {
    currentSearch = 'code=' + CODE;
    state.pairing = pairing;
    denyPairing.mockResolvedValue(undefined);
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Deny' }));
    await waitFor(() => expect(denyPairing).toHaveBeenCalledWith(NORMALIZED));
    await waitFor(() => expect(screen.getAllByText('Request denied').length).toBeGreaterThan(0));
  });

  // The server refuses to distinguish an unknown code from a malformed one, an expired one, one that
  // already settled, a workspace the caller is not a member of, one that does not exist and the
  // platform's own workspace. The screen says one thing too, and never 'permission'.
  it('gives one answer for every reason a pairing request is not there', () => {
    currentSearch = 'code=' + CODE;
    state.error = new ApiError('pairing request not found', 404, [], { error: 'pairing request not found' });
    renderPage();

    expect(screen.getByText('No matching pairing request')).toBeTruthy();
    expect(screen.getByText(/That pairing request is not available/)).toBeTruthy();
    expect(screen.queryByText(/permission/i)).toBeNull();
  });

  it('offers no workspace to approve into when the caller is only a Member everywhere', () => {
    currentSearch = 'code=' + CODE;
    state.pairing = pairing;
    state.orgs = [{ ...teamOrg, role: 'Member' }];
    renderPage();

    expect(screen.queryByLabelText('Workspace')).toBeNull();
    expect(screen.getByText(/not an Owner or Admin of any workspace/)).toBeTruthy();
    // Denying is never an authority question, so it stays available.
    expect((screen.getByRole('button', { name: 'Deny' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('stops offering approval once the code has expired', () => {
    currentSearch = 'code=' + CODE;
    state.pairing = { ...pairing, expiresAt: new Date(Date.now() - 60_000).toISOString() };
    renderPage();

    expect(screen.getByText(/Expired/)).toBeTruthy();
    expect((screen.getByRole('button', { name: /Approve this device/ }) as HTMLButtonElement).disabled).toBe(true);
  });
});
