// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { GettingStartedCard } from './GettingStartedCard';
import { OnboardingContext } from './onboarding-context';
import { updateMirror } from '@/lib/onboarding/state';
import { userKeyFromEmail } from '@/lib/telemetry/identity';
import { CHECKLIST_ITEM_GUIDES, guideHref } from '@/lib/onboarding/guide-links';
import type { DashboardOverview } from '@/lib/types/dashboard';
import type { Project } from '@/lib/types/project';

vi.mock('next/link', () => ({
  default: ({ children, href, className }: { children: React.ReactNode; href: string; className?: string }) => (
    <a href={href} className={className}>{children}</a>
  ),
}));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ user: { email: 'ada@example.com' } }) }));

let onboarding: { status: number; body: unknown };
let billing: unknown;
let calls: { url: string; method: string; body?: unknown }[];

const state = (done: string[] = [], dismissed = false) => ({
  tour: { status: 'completed', version: 1, updatedAt: null },
  checklist: {
    dismissed,
    items: ['create_project', 'upload_file', 'run_video', 'open_result', 'try_assistant', 'open_editor'].map((key) => ({ key, done: done.includes(key) })),
  },
});

const project = { id: 'p1', name: 'Promo' } as Project;
const overview = (over: Partial<DashboardOverview['totals']> = {}, runs = 0): DashboardOverview => ({
  projects: [], activeExecutions: [], needsAttention: [], recentOutputs: [],
  recentExecutions: Array.from({ length: runs }, (_, i) => ({ id: `e${i}`, projectId: 'p1', workflowDefinitionId: 'w1', status: 'Passed' }) as never),
  totals: { projects: 1, sourceFiles: 0, renders: 0, passedLast7Days: 0, failedLast7Days: 0, ...over },
});

let phoneClass = false;
vi.mock('@/lib/hooks/use-device-class', () => ({ useIsPhoneClass: () => phoneClass }));

beforeEach(() => {
  phoneClass = false;
  window.localStorage.clear();
  calls = [];
  onboarding = { status: 200, body: state() };
  billing = { firstVideoGrant: null };
  window.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null,
    addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, method: init?.method ?? 'GET', body: init?.body ? JSON.parse(String(init.body)) : undefined });
      if (url === '/api/v1/onboarding') return new Response(JSON.stringify(onboarding.body), { status: onboarding.status });
      if (url.includes('/billing/summary')) return new Response(JSON.stringify(billing), { status: 200 });
      return new Response(null, { status: 204 });
    }),
  );
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const mount = (props: Partial<Parameters<typeof GettingStartedCard>[0]> = {}) =>
  render(
    <MantineProvider>
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <GettingStartedCard projects={[project]} overview={overview()} onAskAssistant={vi.fn()} {...props} />
      </SWRConfig>
    </MantineProvider>,
  );

const rows = () => screen.getAllByRole('listitem');

describe('GettingStartedCard', () => {
  it('lists the six items in order, each with a place to go and a "How" guide link', async () => {
    mount({ projects: [], overview: overview({ projects: 0 }) });
    await screen.findByText('Getting started');
    expect(rows().map((r) => r.textContent?.replace(/How$/, '').trim())).toEqual([
      'Create a project', 'Upload a file', 'Run your first video', 'Open the result', 'Try the assistant', 'Open the editor (Early preview)',
    ]);
    const how = screen.getAllByRole('link', { name: /^How:/ });
    expect(how.map((a) => a.getAttribute('href'))).toEqual(Object.values(CHECKLIST_ITEM_GUIDES).map(guideHref));
    expect(how.every((a) => a.getAttribute('target') === '_blank')).toBe(true);
    expect(screen.getByRole('link', { name: /^Open the editor/ }).getAttribute('href')).toBe('/editor');
  });

  it('ticks from the onboarding API', async () => {
    onboarding.body = state(['create_project', 'open_editor']);
    mount({ projects: [], overview: overview({ projects: 0 }) });
    await waitFor(() => expect(screen.getByTestId('getting-started-progress').textContent).toBe('2 of 6 done'));
    expect(rows().filter((r) => r.getAttribute('data-done') === 'true')).toHaveLength(2);
  });

  it('never lets the workspace-wide fallback tick items once the server has answered', async () => {
    // Teammates' projects, files and runs are in the dashboard data; the per-user answer says none are this member's.
    mount({ overview: overview({ sourceFiles: 9 }, 3) });
    await waitFor(() => expect(screen.getByTestId('getting-started-progress').textContent).toBe('0 of 6 done'));
  });

  it('drops the editor item when the editor is gated, and still reaches All done', async () => {
    onboarding.body = state(['create_project', 'upload_file', 'run_video', 'open_result', 'try_assistant', 'open_editor']);
    const gated = { startTour() {}, editorEnabled: false, appsEnabled: true, welcomeEnabled: true, welcome: 'stay' as const, beginWelcome() {}, cardUi: null, setCardUi: null };
    const view = render(
      <MantineProvider>
        <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
          <OnboardingContext.Provider value={gated}>
            <GettingStartedCard projects={[project]} overview={overview()} onAskAssistant={vi.fn()} />
          </OnboardingContext.Provider>
        </SWRConfig>
      </MantineProvider>,
    );
    view.unmount();
    onboarding.body = state(['create_project', 'upload_file', 'run_video', 'open_result']);
    render(
      <MantineProvider>
        <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
          <OnboardingContext.Provider value={gated}>
            <GettingStartedCard projects={[project]} overview={overview()} onAskAssistant={vi.fn()} />
          </OnboardingContext.Provider>
        </SWRConfig>
      </MantineProvider>,
    );
    await waitFor(() => expect(screen.getByTestId('getting-started-progress').textContent).toBe('4 of 5 done'));
    expect(screen.queryByText(/Open the editor/)).toBeNull();
    act(() => updateMirror(userKeyFromEmail('ada@example.com'), (m) => ({ ...m, done: ['try_assistant'] })));
    expect(await screen.findByText('All done')).toBeTruthy();
  });

  it('falls back to what the dashboard loaded when the endpoint is missing (404)', async () => {
    onboarding = { status: 404, body: {} };
    mount({ overview: overview({ sourceFiles: 3 }, 1) });
    await waitFor(() => expect(screen.getByTestId('getting-started-progress').textContent).toBe('3 of 6 done'));
  });

  it('is dismissible with a 44px close button and stays dismissed', async () => {
    const view = mount();
    const close = await screen.findByRole('button', { name: 'Dismiss Getting started' });
    expect(close.style.minWidth).toBe('44px');
    expect(close.style.minHeight).toBe('44px');
    fireEvent.click(close);
    expect(screen.queryByTestId('getting-started')).toBeNull();
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT' && JSON.stringify(c.body) === '{"checklistDismissed":true}')).toBe(true));
    view.unmount();
    mount();
    await waitFor(() => expect(calls.filter((c) => c.url === '/api/v1/onboarding' && c.method === 'GET').length).toBeGreaterThan(1));
    expect(screen.queryByTestId('getting-started')).toBeNull();
  });

  it('shows a short "All done" when the last item ticks, then gets out of the way', async () => {
    onboarding.body = state(['create_project', 'upload_file', 'run_video', 'open_result', 'try_assistant']);
    mount();
    await screen.findByText('Getting started');
    // The editor is opened elsewhere in the app: the mirror learns it and the card completes.
    act(() => updateMirror(userKeyFromEmail('ada@example.com'), (m) => ({ ...m, done: ['open_editor'] })));
    expect(await screen.findByText('All done')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Close Getting started' }));
    expect(screen.queryByTestId('getting-started')).toBeNull();
  });

  it('does not show for an account that already finished everything', async () => {
    onboarding.body = state(['create_project', 'upload_file', 'run_video', 'open_result', 'try_assistant', 'open_editor']);
    mount();
    await waitFor(() => expect(calls.some((c) => c.url === '/api/v1/onboarding')).toBe(true));
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.queryByTestId('getting-started')).toBeNull();
  });

  it('mentions the free first video only when the plan really grants one', async () => {
    mount({ projects: [], overview: overview({ projects: 0 }) });
    await screen.findByText('Getting started');
    expect(screen.queryByText(/first one is free/)).toBeNull();
    cleanup();
    billing = { firstVideoGrant: { remaining: 500, expiresAt: null } };
    mount({ projects: [], overview: overview({ projects: 0 }) });
    expect(await screen.findByText(/first one is free/)).toBeTruthy();
  });

  it('is collapsed to one line plus progress on a phone', async () => {
    phoneClass = true;
    mount();
    await screen.findByText('Getting started');
    expect(screen.queryAllByRole('listitem')).toHaveLength(0);
    expect(screen.getByTestId('getting-started-progress')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Show steps' }));
    expect(rows()).toHaveLength(6);
  });
});
