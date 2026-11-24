// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { SWRConfig } from 'swr';
import { TELEMETRY_CONSENT_STORAGE_KEY } from '@/lib/telemetry/consent';
import { OnboardingProvider, useTourLauncher } from './OnboardingProvider';
import { useOnboardingContext } from './onboarding-context';

let pathname = '/dashboard';
vi.mock('next/navigation', () => ({ usePathname: () => pathname }));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ user: { email: 'ada@example.com' } }) }));
vi.mock('./OnboardingTour', () => ({ default: () => null }));
vi.mock('next/dynamic', () => ({
  default: () =>
    function TourStub({ onClose }: { onClose: (r: 'completed' | 'skipped') => void }) {
      return (
        <div role="dialog" data-onboarding-tour="card">
          <button type="button" onClick={() => onClose('skipped')}>stub skip</button>
        </div>
      );
    },
}));

let onboardingStatus = 200;
let onboardingBody: unknown;
let projectsBody: unknown;
let orgsBody: unknown;
let calls: { url: string; method: string; body?: unknown }[] = [];

beforeEach(() => {
  pathname = '/dashboard';
  window.localStorage.clear();
  calls = [];
  onboardingStatus = 200;
  onboardingBody = { tour: { status: 'pending', version: null, updatedAt: null }, checklist: { dismissed: false, items: [] } };
  projectsBody = [];
  orgsBody = [{ id: 'o1', name: 'Personal', kind: 'Personal', role: 'Owner' }];
  vi.stubEnv('NEXT_PUBLIC_TELEMETRY_ENABLED', 'true');
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, method: init?.method ?? 'GET', body: init?.body ? JSON.parse(String(init.body)) : undefined });
      if (url === '/api/v1/onboarding') return new Response(JSON.stringify(onboardingBody), { status: onboardingStatus });
      if (url === '/api/v1/projects') return new Response(JSON.stringify(projectsBody), { status: 200 });
      if (url === '/api/v1/orgs') return new Response(JSON.stringify(orgsBody), { status: 200 });
      return new Response(null, { status: 204 });
    }),
  );
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  document.body.innerHTML = '';
});

function Launcher() {
  const { startTour } = useTourLauncher();
  return <button type="button" onClick={startTour}>Take the tour</button>;
}

const mount = () =>
  render(
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <OnboardingProvider>
        <Launcher />
      </OnboardingProvider>
    </SWRConfig>,
  );

describe('OnboardingProvider', () => {
  it('waits for the telemetry consent card to be answered before auto-starting', async () => {
    mount();
    await new Promise((r) => setTimeout(r, 1000));
    expect(screen.queryByRole('dialog')).toBeNull();
    act(() => window.localStorage.setItem(TELEMETRY_CONSENT_STORAGE_KEY, 'denied'));
    act(() => window.dispatchEvent(new Event('storage')));
    await waitFor(() => expect(screen.getByRole('dialog')).toBeTruthy());
  });

  it('auto-starts at once when telemetry is off', async () => {
    vi.stubEnv('NEXT_PUBLIC_TELEMETRY_ENABLED', 'false');
    mount();
    await waitFor(() => expect(screen.getByRole('dialog')).toBeTruthy());
  });

  it('does not start over another dialog', async () => {
    vi.stubEnv('NEXT_PUBLIC_TELEMETRY_ENABLED', 'false');
    const other = document.createElement('div');
    other.setAttribute('role', 'dialog');
    document.body.appendChild(other);
    mount();
    await new Promise((r) => setTimeout(r, 1100));
    expect(document.querySelectorAll('[data-onboarding-tour]')).toHaveLength(0);
    other.remove();
    await waitFor(() => expect(document.querySelectorAll('[data-onboarding-tour]')).toHaveLength(1), { timeout: 2500 });
  });

  it('ignores the docked assistant panel and popovers (aria-modal=false)', async () => {
    vi.stubEnv('NEXT_PUBLIC_TELEMETRY_ENABLED', 'false');
    const docked = document.createElement('div');
    docked.setAttribute('role', 'dialog');
    docked.setAttribute('aria-modal', 'false');
    document.body.appendChild(docked);
    mount();
    await waitFor(() => expect(document.querySelectorAll('[data-onboarding-tour]')).toHaveLength(1), { timeout: 2500 });
  });

  it('only auto-starts on the dashboard', async () => {
    vi.stubEnv('NEXT_PUBLIC_TELEMETRY_ENABLED', 'false');
    pathname = '/projects';
    mount();
    await new Promise((r) => setTimeout(r, 1000));
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('never re-shows after a skip, but "Take the tour" opens it on request', async () => {
    vi.stubEnv('NEXT_PUBLIC_TELEMETRY_ENABLED', 'false');
    const view = mount();
    await waitFor(() => expect(screen.getByRole('dialog')).toBeTruthy());
    fireEvent.click(screen.getByText('stub skip'));
    expect(screen.queryByRole('dialog')).toBeNull();
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true));
    await new Promise((r) => setTimeout(r, 1000));
    expect(screen.queryByRole('dialog')).toBeNull();
    fireEvent.click(screen.getByText('Take the tour'));
    expect(screen.getByRole('dialog')).toBeTruthy();
    view.unmount();
  });

  it('does not auto-start for an account that already finished (server says completed)', async () => {
    vi.stubEnv('NEXT_PUBLIC_TELEMETRY_ENABLED', 'false');
    onboardingBody = { tour: { status: 'completed', version: 1, updatedAt: null }, checklist: { dismissed: false, items: [] } };
    mount();
    await waitFor(() => expect(calls.length).toBeGreaterThan(0));
    await new Promise((r) => setTimeout(r, 1000));
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('reports open_editor when the editor is opened', async () => {
    pathname = '/editor/abc/timeline/def';
    mount();
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ marks: ['open_editor'] }));
  });
});

/** Shows what the provider decided about the welcome questions, as the dashboard reads it. */
function WelcomeProbe() {
  const { welcome, beginWelcome } = useOnboardingContext();
  return (
    <>
      <output data-testid="welcome">{welcome}</output>
      <button type="button" onClick={beginWelcome}>begin welcome</button>
    </>
  );
}

const tree = (welcomeEnabled?: boolean) => (
  <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
    <OnboardingProvider welcomeEnabled={welcomeEnabled}>
      <Launcher />
      <WelcomeProbe />
    </OnboardingProvider>
  </SWRConfig>
);

const blankProfile = { persona: null, material: null, suggestedPersona: null, answeredAt: null, skippedAt: null };
const pendingWith = (profile: unknown) => ({
  tour: { status: 'pending', version: null, updatedAt: null },
  checklist: { dismissed: false, items: [] },
  profile,
});
const tourDialogs = () => document.querySelectorAll('[data-onboarding-tour]');

describe('OnboardingProvider and the welcome questions', () => {
  beforeEach(() => {
    vi.stubEnv('NEXT_PUBLIC_TELEMETRY_ENABLED', 'false');
  });

  it('says redirect for a brand-new account with no project, and holds the tour meanwhile', async () => {
    onboardingBody = pendingWith(blankProfile);
    render(tree());
    await waitFor(() => expect(screen.getByTestId('welcome').textContent).toBe('redirect'));
    expect(calls.some((c) => c.url === '/api/v1/projects')).toBe(true);
    await new Promise((r) => setTimeout(r, 1000));
    expect(tourDialogs()).toHaveLength(0);
  });

  // An invited teammate's own Personal workspace is empty, but they are not a lone new user.
  it('says stay for a member of a team whose Personal workspace has no project, and the tour opens as before', async () => {
    onboardingBody = pendingWith(blankProfile);
    orgsBody = [
      { id: 'o1', name: 'Personal', kind: 'Personal', role: 'Owner' },
      { id: 'o2', name: 'Studio', kind: 'Team', role: 'Member' },
    ];
    render(tree());
    await waitFor(() => expect(calls.some((c) => c.url === '/api/v1/orgs')).toBe(true));
    await waitFor(() => expect(screen.getByTestId('welcome').textContent).toBe('stay'));
    await waitFor(() => expect(tourDialogs()).toHaveLength(1));
  });

  it('redirects once: after the dashboard begins the welcome, coming back neither redirects nor loops', async () => {
    onboardingBody = pendingWith(blankProfile);
    const view = render(tree());
    await waitFor(() => expect(screen.getByTestId('welcome').textContent).toBe('redirect'));
    fireEvent.click(screen.getByText('begin welcome'));
    // Still "redirect" while the dashboard is on its way out, and nothing opens over it.
    expect(screen.getByTestId('welcome').textContent).toBe('redirect');
    await new Promise((r) => setTimeout(r, 1000));
    expect(tourDialogs()).toHaveLength(0);

    pathname = '/welcome';
    view.rerender(tree());
    expect(screen.getByTestId('welcome').textContent).toBe('stay');
    await new Promise((r) => setTimeout(r, 1000));
    expect(tourDialogs()).toHaveLength(0);

    // Back on the dashboard without answering: no second redirect; the tour opens as it always did.
    pathname = '/dashboard';
    view.rerender(tree());
    expect(screen.getByTestId('welcome').textContent).toBe('stay');
    await waitFor(() => expect(tourDialogs()).toHaveLength(1));
  });

  it('does not redirect again on a later page load in the same browser', async () => {
    onboardingBody = pendingWith(blankProfile);
    const first = render(tree());
    await waitFor(() => expect(screen.getByTestId('welcome').textContent).toBe('redirect'));
    fireEvent.click(screen.getByText('begin welcome'));
    first.unmount();
    render(tree());
    await waitFor(() => expect(calls.filter((c) => c.url === '/api/v1/onboarding').length).toBeGreaterThan(1));
    expect(screen.getByTestId('welcome').textContent).toBe('stay');
  });

  it('never auto-starts the tour for someone who answered the questions, but opens it on request', async () => {
    onboardingBody = pendingWith({ ...blankProfile, persona: 'creator', material: 'footage', answeredAt: '2026-11-11T10:00:00Z' });
    projectsBody = [{ id: 'p1', name: 'My first video' }];
    render(tree());
    await waitFor(() => expect(calls.some((c) => c.url === '/api/v1/onboarding')).toBe(true));
    await new Promise((r) => setTimeout(r, 1100));
    expect(screen.getByTestId('welcome').textContent).toBe('stay');
    expect(tourDialogs()).toHaveLength(0);
    fireEvent.click(screen.getByText('Take the tour'));
    expect(tourDialogs()).toHaveLength(1);
  });

  it('auto-starts the tour as before for someone who skipped the questions', async () => {
    onboardingBody = pendingWith({ ...blankProfile, skippedAt: '2026-11-11T10:00:00Z' });
    render(tree());
    await waitFor(() => expect(tourDialogs()).toHaveLength(1));
    expect(screen.getByTestId('welcome').textContent).toBe('stay');
    expect(calls.some((c) => c.url === '/api/v1/projects')).toBe(false);
  });

  it('auto-starts the tour as before in a workspace that already has projects (an invited member)', async () => {
    onboardingBody = pendingWith(blankProfile);
    projectsBody = [{ id: 'p1', name: 'Team launch' }];
    render(tree());
    await waitFor(() => expect(tourDialogs()).toHaveLength(1));
    expect(screen.getByTestId('welcome').textContent).toBe('stay');
  });

  it('with the flag off nobody is redirected and the tour auto-starts as before', async () => {
    onboardingBody = pendingWith(blankProfile);
    render(tree(false));
    await waitFor(() => expect(tourDialogs()).toHaveLength(1));
    expect(screen.getByTestId('welcome').textContent).toBe('stay');
    expect(calls.some((c) => c.url === '/api/v1/projects')).toBe(false);
  });

  it('never decides anything away from the dashboard', async () => {
    onboardingBody = pendingWith(blankProfile);
    pathname = '/projects';
    render(tree());
    await waitFor(() => expect(calls.some((c) => c.url === '/api/v1/onboarding')).toBe(true));
    await new Promise((r) => setTimeout(r, 300));
    expect(screen.getByTestId('welcome').textContent).toBe('stay');
    expect(calls.some((c) => c.url === '/api/v1/projects')).toBe(false);
  });
});
