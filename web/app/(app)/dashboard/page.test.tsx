// @vitest-environment jsdom
/**
 * The dashboard's part of the new-user funnel: the one redirect to the welcome questions, the
 * "your first video" rail with its persona header, the provider checklist kept away from an account
 * that cannot act on it, and the `alpha.first_project` call-site (emitted from the REAL transition,
 * the project form reporting a successful creation, never on a page load).
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { OnboardingContext, type OnboardingContextValue } from '@/components/onboarding/onboarding-context';
import type { FirstVideoRailProps } from '@/components/dashboard/FirstVideoRail';
import type { OnboardingMirror, OnboardingProfile, OnboardingServerState } from '@/lib/onboarding/state';
import type { DashboardOverview } from '@/lib/types/dashboard';
import type { Project } from '@/lib/types/project';
import { userKeyFromEmail } from '@/lib/telemetry';

const mocks = vi.hoisted(() => ({
  emitFunnelEvent: vi.fn(),
  push: vi.fn(),
  replace: vi.fn(),
  mutateProjects: vi.fn(),
  mutateOverview: vi.fn(),
  beginWelcome: vi.fn(),
  clearPickedPlan: vi.fn(async () => {}),
  captured: {
    onSuccess: undefined as ((project?: { id: string }) => void) | undefined,
    rail: undefined as FirstVideoRailProps | undefined,
  },
  auth: { user: { email: 'Ada@Example.com', displayName: 'Ada' }, isPlatformAdmin: false, isOrgAdmin: true },
  projects: { data: [] as Project[] | undefined, isLoading: false, error: undefined as unknown },
  overview: { data: undefined as DashboardOverview | undefined, isLoading: false, error: undefined as unknown },
  billing: { data: undefined as unknown, error: undefined as unknown },
  onboarding: {
    settled: true,
    userKey: 'u1',
    server: null as OnboardingServerState | null,
    mirror: {} as OnboardingMirror,
    profile: null as OnboardingProfile | null,
    clearPickedPlan: undefined as unknown as () => Promise<void>,
  },
}));

// Only the emitter is replaced; `userKeyFromEmail` stays real, so the key asserted is the app's own.
vi.mock('@/lib/telemetry', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/telemetry')>()),
  emitFunnelEvent: mocks.emitFunnelEvent,
}));
vi.mock('next/navigation', () => ({ useRouter: () => ({ push: mocks.push, replace: mocks.replace }) }));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => mocks.auth }));
vi.mock('@/lib/hooks/use-projects', () => ({ useProjects: () => ({ ...mocks.projects, mutate: mocks.mutateProjects }) }));
vi.mock('@/lib/hooks/use-dashboard', () => ({
  useDashboardOverview: () => ({ ...mocks.overview, mutate: mocks.mutateOverview }),
  useWorkflowEvents: () => undefined,
}));
vi.mock('@/lib/hooks/use-billing', () => ({ useBillingSummary: () => mocks.billing }));
vi.mock('@/lib/hooks/use-inference-providers', () => ({
  useInferenceProviders: () => ({ data: [] }),
  useSelectableProviders: () => ({ data: [] }),
}));
// The real checklist helpers, a controllable state.
vi.mock('@/lib/hooks/use-onboarding', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/hooks/use-onboarding')>()),
  useOnboarding: () => mocks.onboarding,
}));
vi.mock('@/components/assistant/AssistantShellProvider', () => ({ useAssistantShell: () => ({ openWithPrompt: vi.fn() }) }));
vi.mock('@/components/billing/FirstVideoGrantCallout', () => ({ FirstVideoGrantCallout: () => <div data-testid="grant-callout" /> }));
vi.mock('@/components/billing/PickedPlanNotice', () => ({
  PickedPlanNotice: ({ planKey, onForget }: { planKey: string; onForget: () => void }) => (
    <button type="button" data-testid="picked-plan" data-plan={planKey} onClick={onForget} />
  ),
}));
vi.mock('@/components/onboarding/GettingStartedCard', () => ({ GettingStartedCard: () => <div data-testid="getting-started" /> }));
vi.mock('@/components/onboarding/TourCard', () => ({ TourCard: () => <div data-testid="tour-card" /> }));
vi.mock('@/components/dashboard/NudgeButton', () => ({ NudgeList: () => null }));
vi.mock('@/components/dashboard/FirstVideoRail', () => ({
  FirstVideoRail: (props: FirstVideoRailProps) => {
    mocks.captured.rail = props;
    return <div data-testid="first-video-rail" />;
  },
}));
vi.mock('@/components/dashboard/sections', () => ({
  ContinueProjects: () => <div data-testid="continue-projects" />,
  LatestRenders: () => <div data-testid="latest-renders" />,
  LiveRuns: () => <div data-testid="live-runs" />,
  NeedsAttention: () => null,
  RecentRuns: () => null,
  SetupChecklist: () => <div data-testid="setup-checklist" />,
}));
// The form is the transition's source: capturing its `onSuccess` is how the funnel test drives it.
vi.mock('@/components/projects/ProjectForm', () => ({
  ProjectForm: (props: { onSuccess: (project?: { id: string }) => void }) => {
    mocks.captured.onSuccess = props.onSuccess;
    return null;
  },
}));

import DashboardPage from './page';

// MantineProvider reads window.matchMedia, which jsdom does not implement.
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (q: string) => ({
    matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {},
    dispatchEvent: () => false, onchange: null,
  }),
});

const project = (id: string, createdAt: string) => ({ id, name: id, createdAt, updatedAt: createdAt }) as Project;
const emptyOverview = (over: Partial<DashboardOverview> = {}): DashboardOverview => ({
  projects: [], activeExecutions: [], recentExecutions: [], needsAttention: [], recentOutputs: [],
  totals: { projects: 0, sourceFiles: 0, renders: 0, passedLast7Days: 0, failedLast7Days: 0 },
  ...over,
});
const blank: OnboardingProfile = { persona: null, material: null, suggestedPersona: null, pickedPlan: null, answeredAt: null, skippedAt: null };
const serverWith = (done: string[], profile: OnboardingProfile | null = blank): OnboardingServerState => ({
  tour: { status: 'pending', version: null, updatedAt: null },
  checklist: {
    dismissed: false,
    items: (['create_project', 'upload_file', 'run_video', 'open_result', 'try_assistant', 'open_editor'] as const).map((key) => ({ key, done: done.includes(key) })),
  },
  profile,
});

const basePlan = { kind: 'Subscription', minSeats: null, includedCredits: 1000, current: false };
const PRO = { ...basePlan, key: 'pro', name: 'Pro', priceMonthlyUsd: 79, priceId: 'pri_pro' };
const TEAM = { ...basePlan, key: 'team', name: 'Team', kind: 'PerSeat', minSeats: 3, priceMonthlyUsd: 49, priceId: 'pri_team' };
const summaryWith = (plans: unknown[]) => ({ canManageBilling: true, subscription: null, plans });

let context: OnboardingContextValue;
const mount = () =>
  render(
    <MantineProvider env="test">
      <OnboardingContext.Provider value={context}>
        <DashboardPage />
      </OnboardingContext.Provider>
    </MantineProvider>,
  );

beforeEach(() => {
  for (const fn of [mocks.emitFunnelEvent, mocks.push, mocks.replace, mocks.mutateProjects, mocks.mutateOverview, mocks.beginWelcome, mocks.clearPickedPlan]) fn.mockClear();
  mocks.captured.onSuccess = undefined;
  mocks.captured.rail = undefined;
  mocks.auth = { user: { email: 'Ada@Example.com', displayName: 'Ada' }, isPlatformAdmin: false, isOrgAdmin: true };
  mocks.projects = { data: [], isLoading: false, error: undefined };
  mocks.billing = { data: undefined, error: undefined };
  mocks.overview = { data: emptyOverview(), isLoading: false, error: undefined };
  mocks.onboarding = { settled: true, userKey: 'u1', server: serverWith([]), mirror: {}, profile: blank, clearPickedPlan: mocks.clearPickedPlan };
  context = {
    startTour() {}, editorEnabled: true, appsEnabled: true, welcomeEnabled: true, welcome: 'stay', beginWelcome: mocks.beginWelcome,
    cardUi: null, setCardUi: null,
  };
});
afterEach(() => {
  cleanup();
});

describe('Dashboard: the redirect to the welcome questions', () => {
  it('goes to /welcome exactly once when the provider says so, recording it before leaving', () => {
    context = { ...context, welcome: 'redirect' };
    const view = mount();
    expect(mocks.beginWelcome).toHaveBeenCalledTimes(1);
    expect(mocks.replace).toHaveBeenCalledTimes(1);
    expect(mocks.replace).toHaveBeenCalledWith('/welcome');
    expect(mocks.beginWelcome.mock.invocationCallOrder[0]).toBeLessThan(mocks.replace.mock.invocationCallOrder[0]);
    view.rerender(
      <MantineProvider env="test">
        <OnboardingContext.Provider value={{ ...context }}>
          <DashboardPage />
        </OnboardingContext.Provider>
      </MantineProvider>,
    );
    expect(mocks.replace).toHaveBeenCalledTimes(1);
    // Nothing of the new-user dashboard flashes on the way out.
    expect(screen.getByTestId('dashboard-holding')).toBeTruthy();
    expect(screen.queryByTestId('first-video-rail')).toBeNull();
  });

  it('draws nothing that could flash while the decision is still open', () => {
    context = { ...context, welcome: 'wait' };
    mount();
    expect(mocks.replace).not.toHaveBeenCalled();
    expect(screen.getByTestId('dashboard-holding')).toBeTruthy();
    expect(screen.queryByTestId('first-video-rail')).toBeNull();
  });

  it('never leaves the page blank for a slow API: it draws itself after a few seconds, and a late redirect waits for the next visit', () => {
    vi.useFakeTimers();
    try {
      context = { ...context, welcome: 'wait' };
      const view = mount();
      expect(screen.getByTestId('dashboard-holding')).toBeTruthy();
      act(() => {
        vi.advanceTimersByTime(5000);
      });
      expect(screen.queryByTestId('dashboard-holding')).toBeNull();
      expect(screen.getByTestId('first-video-rail')).toBeTruthy();

      view.rerender(
        <MantineProvider env="test">
          <OnboardingContext.Provider value={{ ...context, welcome: 'redirect' }}>
            <DashboardPage />
          </OnboardingContext.Provider>
        </MantineProvider>,
      );
      expect(mocks.replace).not.toHaveBeenCalled();
      expect(mocks.beginWelcome).not.toHaveBeenCalled();
      expect(screen.getByTestId('first-video-rail')).toBeTruthy();
    } finally {
      vi.useRealTimers();
    }
  });

  it('keeps holding while it leaves for the welcome questions, however long the navigation takes', () => {
    vi.useFakeTimers();
    try {
      context = { ...context, welcome: 'redirect' };
      mount();
      expect(mocks.replace).toHaveBeenCalledTimes(1);
      act(() => {
        vi.advanceTimersByTime(20000);
      });
      expect(screen.getByTestId('dashboard-holding')).toBeTruthy();
      expect(mocks.replace).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it('stays put when the provider says stay (flag off, answered, skipped, already offered, projects exist)', () => {
    mount();
    expect(mocks.replace).not.toHaveBeenCalled();
    expect(mocks.beginWelcome).not.toHaveBeenCalled();
    expect(screen.queryByTestId('dashboard-holding')).toBeNull();
  });
});

describe('Dashboard: your first video', () => {
  it('shows the rail (not the old generic panel) to an account with no project, with the credit callout kept', () => {
    mount();
    expect(screen.getByTestId('first-video-rail')).toBeTruthy();
    expect(screen.getByTestId('grant-callout')).toBeTruthy();
    expect(screen.getByTestId('getting-started')).toBeTruthy();
    expect(screen.getByTestId('tour-card')).toBeTruthy();
    expect(document.body.textContent).not.toContain('three moves away');
    expect(mocks.captured.rail).toMatchObject({ project: undefined, addDone: false, runDone: false, welcomeEnabled: true });
    expect(screen.queryByTestId('live-runs')).toBeNull();
  });

  it.each([
    ['creator', 'The footage is already on your phone. Get a first cut while you plan the next video.'],
    ['business', 'You did not start this to become a video editor. Your first video starts here.'],
    ['educator', 'Your best lessons deserve a shorter cut. Record the point in a few short takes and get a tight clip.'],
    ['nonprofit', 'You have the stories and the clips. Tell the first one.'],
    ['software', 'You shipped it. Now show it in a video.'],
    ['agency', 'First cuts should not eat the margin. Start one here.'],
    ['other', 'You have a story. Make the video.'],
  ] as const)('greets a %s with their own one-line header', (persona, line) => {
    mocks.onboarding.profile = { ...blank, persona, material: 'footage', answeredAt: '2026-11-11T10:00:00Z' };
    mount();
    expect(screen.getByText(line)).toBeTruthy();
    expect(mocks.captured.rail).toMatchObject({ persona, material: 'footage' });
  });

  it('falls back to the landing page\'s audience, then to the general line, when nothing was answered', () => {
    mocks.onboarding.profile = { ...blank, suggestedPersona: 'educator' };
    mount();
    expect(screen.getByText('Your best lessons deserve a shorter cut. Record the point in a few short takes and get a tight clip.')).toBeTruthy();
    cleanup();
    mocks.onboarding.profile = null;
    mount();
    expect(screen.getByText('You have a story. Make the video.')).toBeTruthy();
  });

  // The header follows what the person starts from as well as who they are. Someone who answered
  // "just an idea" (the rail below tells them to go and film) or "an app I built" is never told
  // that the footage is already on their phone.
  it.each(['creator', 'business', 'educator', 'nonprofit', 'software', 'agency', 'other'] as const)(
    'does not tell a %s who starts from an idea or from code that the footage is already there',
    (persona) => {
      mocks.onboarding.profile = { ...blank, persona, material: 'idea', answeredAt: '2026-11-11T10:00:00Z' };
      mount();
      expect(screen.getByText('Five short clips from your phone are enough for a first cut. Your shot list is ready.')).toBeTruthy();
      expect(screen.queryByText(/already on your phone|long recordings|stories and the clips/)).toBeNull();
      cleanup();
      mocks.onboarding.profile = { ...blank, persona, material: 'code', answeredAt: '2026-11-11T10:00:00Z' };
      mount();
      expect(screen.getByText('You built it. Now show it in a video.')).toBeTruthy();
      expect(screen.queryByText(/already on your phone|long recordings|stories and the clips/)).toBeNull();
    },
  );

  // The public educator page says a long lecture cannot be turned into short clips on its own.
  it('does not tell an educator to turn a long recording into a clip, or promise a clip people finish', () => {
    mocks.onboarding.profile = { ...blank, persona: 'educator', material: 'footage', answeredAt: '2026-11-11T10:00:00Z' };
    mount();
    expect(screen.queryByText(/Turn one into a clip|people finish|long recordings/)).toBeNull();
  });

  const projectRow = (projectId: string, sourceFileCount: number) =>
    ({ projectId, sourceFileCount, videoCount: sourceFileCount, workflowCount: 1, renderCount: 0 }) as DashboardOverview['projects'][number];

  it('keeps the rail after the welcome flow created the project, ticking "add" from that project\'s own files and pointing at the newest project', () => {
    mocks.projects.data = [project('older', '2026-11-01T10:00:00Z'), project('newest', '2026-11-11T10:00:00Z')];
    mocks.overview.data = emptyOverview({ projects: [projectRow('older', 0), projectRow('newest', 3)] });
    mocks.onboarding.server = serverWith(['create_project', 'upload_file']);
    mocks.onboarding.profile = { ...blank, persona: 'creator', material: 'idea', answeredAt: '2026-11-11T10:00:00Z' };
    mount();
    expect(screen.getByTestId('first-video-rail')).toBeTruthy();
    expect(mocks.captured.rail).toMatchObject({ addDone: true, runDone: false, material: 'idea', persona: 'creator' });
    expect(mocks.captured.rail?.project?.id).toBe('newest');
    // More than one project: the list of projects stays reachable under the rail.
    expect(screen.getByTestId('continue-projects')).toBeTruthy();
    expect(screen.queryByTestId('live-runs')).toBeNull();
  });

  // The checklist's upload mark is account-wide and kept for good: a file uploaded to an older
  // project must not tick "add" (and move the button on to "press Run") for a newer, empty one.
  it('does not tick "add" for an empty newest project because a file was once uploaded elsewhere', () => {
    mocks.projects.data = [project('older', '2026-11-01T10:00:00Z'), project('newest', '2026-11-11T10:00:00Z')];
    mocks.overview.data = emptyOverview({ projects: [projectRow('older', 4), projectRow('newest', 0)] });
    mocks.onboarding.server = serverWith(['create_project', 'upload_file']);
    mount();
    expect(mocks.captured.rail?.project?.id).toBe('newest');
    expect(mocks.captured.rail).toMatchObject({ addDone: false, runDone: false });
  });

  it('hands over to the ordinary dashboard once the first run has started', () => {
    mocks.projects.data = [project('p1', '2026-11-11T10:00:00Z')];
    mocks.onboarding.server = serverWith(['create_project', 'upload_file', 'run_video']);
    mount();
    expect(screen.queryByTestId('first-video-rail')).toBeNull();
    expect(screen.getByTestId('live-runs')).toBeTruthy();
    expect(screen.getByText('Here is what is happening across your projects.')).toBeTruthy();
  });

  it('never shows the rail in a workspace that already has runs or renders (a teammate who has not run one)', () => {
    mocks.projects.data = [project('p1', '2026-11-01T10:00:00Z')];
    mocks.overview.data = emptyOverview({ totals: { projects: 1, sourceFiles: 4, renders: 3, passedLast7Days: 1, failedLast7Days: 0 } });
    mount();
    expect(screen.queryByTestId('first-video-rail')).toBeNull();
    expect(screen.getByTestId('live-runs')).toBeTruthy();
  });

  it('does not hold the page for someone this browser has seen run a video, even before the API answers', () => {
    mocks.projects = { data: undefined, isLoading: true, error: undefined };
    mocks.overview = { data: undefined, isLoading: true, error: undefined };
    mocks.onboarding = { settled: false, userKey: 'u1', server: null, mirror: { done: ['run_video'] }, profile: null, clearPickedPlan: mocks.clearPickedPlan };
    mount();
    expect(screen.queryByTestId('dashboard-holding')).toBeNull();
    expect(screen.getByTestId('live-runs')).toBeTruthy();
  });

  it('shows the slow line and Retry after 10 s of loading', () => {
    vi.useFakeTimers();
    try {
      mocks.projects = { data: undefined, isLoading: true, error: undefined };
      mocks.overview = { data: undefined, isLoading: true, error: undefined };
      mocks.onboarding = { settled: false, userKey: 'u1', server: null, mirror: { done: ['run_video'] }, profile: null, clearPickedPlan: mocks.clearPickedPlan };
      mount();
      expect(screen.queryByText(/taking longer than usual/i)).toBeNull();
      act(() => { vi.advanceTimersByTime(10000); });
      expect(screen.getByRole('status').textContent).toMatch(/taking longer than usual/i);
      expect(screen.getAllByRole('button', { name: 'Retry' }).length).toBeGreaterThan(0);
    } finally {
      vi.useRealTimers();
    }
  });

  it('holds briefly, instead of flashing the wrong layout, while an account that may be on its first video loads', () => {
    mocks.projects.data = [project('p1', '2026-11-11T10:00:00Z')];
    mocks.overview = { data: undefined, isLoading: true, error: undefined };
    mount();
    expect(screen.getByTestId('dashboard-holding')).toBeTruthy();
  });

  it('never gets stuck holding when the overview cannot be loaded', () => {
    mocks.projects.data = [project('p1', '2026-11-11T10:00:00Z')];
    mocks.overview = { data: undefined, isLoading: false, error: new Error('offline') };
    mount();
    expect(screen.queryByTestId('dashboard-holding')).toBeNull();
    expect(screen.getByTestId('failure-card')).toBeTruthy();
  });
});

describe('Dashboard: the overview failing', () => {
  const failing = (status: number, message: string) => Object.assign(new Error(message), { status });
  it.each([
    ['a server failure', failing(500, 'Internal Server Error'), 'Something went wrong on our side', true],
    ['a network failure', new TypeError('Failed to fetch'), 'Could not reach ReelBolt', true],
    ['a missing overview', failing(404, ''), 'Not found', false],
    ['a refused overview', failing(403, ''), 'No permission', false],
  ])('says %s plainly instead of showing empty sections, with Retry only when it can help', (_n, err, title, retry) => {
    mocks.projects.data = [project('p1', '2026-11-11T10:00:00Z')];
    mocks.overview = { data: undefined, isLoading: false, error: err };
    mount();
    const card = screen.getByTestId('failure-card');
    expect(card.textContent).toContain(title);
    expect(card.textContent).not.toContain('Internal Server Error');
    expect(screen.queryByTestId('live-runs')).toBeNull();
    expect(screen.queryByTestId('latest-renders')).toBeNull();
    const button = screen.queryByRole('button', { name: 'Retry' });
    expect(Boolean(button)).toBe(retry);
    if (button) {
      fireEvent.click(button);
      expect(mocks.mutateOverview).toHaveBeenCalled();
    }
  });

  it('shows the sections, and no failure, when the overview loads', () => {
    mocks.projects.data = [project('p1', '2026-11-11T10:00:00Z')];
    mocks.overview = {
      data: emptyOverview({ totals: { projects: 1, sourceFiles: 1, renders: 1, passedLast7Days: 1, failedLast7Days: 0 } }),
      isLoading: false, error: undefined,
    };
    mount();
    expect(screen.queryByTestId('failure-card')).toBeNull();
    expect(screen.getByTestId('live-runs')).toBeTruthy();
  });
});

describe('Dashboard: the provider checklist', () => {
  it('is hidden from a workspace owner while the workspace has no project', () => {
    mount();
    expect(screen.queryByTestId('setup-checklist')).toBeNull();
  });

  // The welcome flow creates the first project before the dashboard is ever seen, so "has a
  // project" is every trial owner on the way to a first video: provider wording the trial plan
  // cannot act on stays away until the first run has started.
  it('stays hidden from a workspace owner whose workspace has a project but no run yet (first-video mode)', () => {
    mocks.projects.data = [project('p1', '2026-11-11T10:00:00Z')];
    mount();
    expect(screen.getByTestId('first-video-rail')).toBeTruthy();
    expect(screen.queryByTestId('setup-checklist')).toBeNull();
  });

  it('is shown to a workspace owner once the first run has started', () => {
    mocks.projects.data = [project('p1', '2026-11-11T10:00:00Z')];
    mocks.onboarding.server = serverWith(['create_project', 'upload_file', 'run_video']);
    mount();
    expect(screen.queryByTestId('first-video-rail')).toBeNull();
    expect(screen.getByTestId('setup-checklist')).toBeTruthy();
  });

  it('is always shown to a platform administrator, who can act on it', () => {
    mocks.auth = { ...mocks.auth, isPlatformAdmin: true };
    mount();
    expect(screen.getByTestId('setup-checklist')).toBeTruthy();
  });

  it('is never shown to a member who is neither', () => {
    mocks.auth = { ...mocks.auth, isOrgAdmin: false };
    mocks.projects.data = [project('p1', '2026-11-11T10:00:00Z')];
    mount();
    expect(screen.queryByTestId('setup-checklist')).toBeNull();
  });
});

/**
 * What the person asked for before they had an account (lib/auth/signup-intent.ts): a page they
 * were on their way to, and a plan picked on the pricing page. The provider round trip and the
 * emailed link drop both; the dashboard is where every new session lands, so it honours them here.
 */
describe('Dashboard: the sign-up link\'s plan and return path', () => {
  const who = userKeyFromEmail('ada@example.com');
  const store = (value: Record<string, unknown>) =>
    window.localStorage.setItem('rb-signup-intent:v1', JSON.stringify({ at: Date.now(), who, ...value }));

  beforeEach(() => { window.localStorage.clear(); window.sessionStorage.clear(); });
  afterEach(() => { window.localStorage.clear(); window.sessionStorage.clear(); });

  it('goes on to the page the sign-up link named, once, and before the welcome questions', () => {
    store({ next: '/app/marketplace/recipes/abc' });
    context = { ...context, welcome: 'redirect' };
    mount();
    expect(mocks.push).toHaveBeenCalledWith('/marketplace/recipes/abc');
    // The welcome questions are for the next visit here: not sent to both at once.
    expect(mocks.replace).not.toHaveBeenCalled();
    expect(mocks.beginWelcome).not.toHaveBeenCalled();
    expect(screen.getByTestId('dashboard-holding')).toBeTruthy();
    // Used up: a later visit does not send the person there again.
    expect(JSON.parse(window.localStorage.getItem('rb-signup-intent:v1') ?? '{}').next).toBeUndefined();
    cleanup();
    mocks.push.mockClear();
    mount();
    expect(mocks.push).not.toHaveBeenCalled();
    expect(mocks.replace).toHaveBeenCalledWith('/welcome');
  });

  it('ignores a return path remembered for another address', () => {
    window.localStorage.setItem('rb-signup-intent:v1', JSON.stringify({ at: Date.now(), who: userKeyFromEmail('someone@else.test'), next: '/app/projects/9' }));
    mount();
    expect(mocks.push).not.toHaveBeenCalled();
    expect(window.localStorage.getItem('rb-signup-intent:v1')).toBeNull();
  });

  it('keeps a plan picked on the pricing page as a notice, until it is dismissed', () => {
    store({ plan: 'pro' });
    mocks.billing = { data: summaryWith([{ ...PRO, current: true }]), error: undefined };
    mocks.projects.data = [project('p1', '2026-11-11T10:00:00Z')];
    mount();
    expect(screen.getByTestId('picked-plan').getAttribute('data-plan')).toBe('pro');
    // A notice, never a detour: the first-video rail is still what the page leads with.
    expect(screen.getByTestId('first-video-rail')).toBeTruthy();
    expect(mocks.push).not.toHaveBeenCalled();
    act(() => screen.getByTestId('picked-plan').click());
    expect(screen.queryByTestId('picked-plan')).toBeNull();
    expect(window.localStorage.getItem('rb-signup-intent:v1')).toBeNull();
  });

  it('goes straight to the checkout of the picked plan, once, when the summary lists it', () => {
    store({ plan: 'pro' });
    mocks.billing = { data: summaryWith([PRO]), error: undefined };
    const view = mount();
    expect(mocks.replace).toHaveBeenCalledTimes(1);
    expect(mocks.replace).toHaveBeenCalledWith('/settings/billing?plan=pro&start=1#plans');
    expect(window.sessionStorage.getItem('rb-plan-start:v1')).toBe('pro');
    expect(screen.getByTestId('dashboard-holding')).toBeTruthy();
    view.rerender(
      <MantineProvider env="test">
        <OnboardingContext.Provider value={{ ...context }}>
          <DashboardPage />
        </OnboardingContext.Provider>
      </MantineProvider>,
    );
    expect(mocks.replace).toHaveBeenCalledTimes(1);
  });

  it('lets the return path have the first word over the checkout', () => {
    store({ plan: 'pro', next: '/app/projects/9' });
    mocks.billing = { data: summaryWith([PRO]), error: undefined };
    mount();
    expect(mocks.push).toHaveBeenCalledWith('/projects/9');
    expect(mocks.replace).not.toHaveBeenCalled();
  });

  it('does not send a per-seat plan to the checkout; the notice stays', () => {
    store({ plan: 'team' });
    mocks.billing = { data: summaryWith([TEAM]), error: undefined };
    mocks.projects.data = [project('p1', '2026-11-11T10:00:00Z')];
    mount();
    expect(mocks.replace).not.toHaveBeenCalled();
    expect(screen.getByTestId('picked-plan').getAttribute('data-plan')).toBe('team');
  });

  it('holds the welcome redirect until the summary answers, then goes to the checkout instead', () => {
    store({ plan: 'pro' });
    context = { ...context, welcome: 'redirect' };
    const view = mount();
    expect(mocks.replace).not.toHaveBeenCalled();
    expect(mocks.beginWelcome).not.toHaveBeenCalled();
    expect(screen.getByTestId('dashboard-holding')).toBeTruthy();
    mocks.billing = { data: summaryWith([PRO]), error: undefined };
    view.rerender(
      <MantineProvider env="test">
        <OnboardingContext.Provider value={{ ...context }}>
          <DashboardPage />
        </OnboardingContext.Provider>
      </MantineProvider>,
    );
    expect(mocks.replace).toHaveBeenCalledTimes(1);
    expect(mocks.replace).toHaveBeenCalledWith('/settings/billing?plan=pro&start=1#plans');
    expect(mocks.beginWelcome).not.toHaveBeenCalled();
  });

  it('does not redirect an unverified address, and leaves no token', () => {
    store({ plan: 'pro' });
    mocks.auth = { ...mocks.auth, user: { ...mocks.auth.user, emailVerified: false } as typeof mocks.auth.user };
    mocks.billing = { data: summaryWith([PRO]), error: undefined };
    mocks.projects.data = [project('p1', '2026-11-11T10:00:00Z')];
    mount();
    expect(mocks.replace).not.toHaveBeenCalled();
    expect(window.sessionStorage.getItem('rb-plan-start:v1')).toBeNull();
  });

  it('resumes a plan the server kept, with nothing in this browser, and clears it once used', () => {
    mocks.onboarding.profile = { ...blank, pickedPlan: 'pro' };
    mocks.billing = { data: summaryWith([PRO]), error: undefined };
    mount();
    expect(mocks.replace).toHaveBeenCalledWith('/settings/billing?plan=pro&start=1#plans');
    expect(mocks.clearPickedPlan).toHaveBeenCalledTimes(1);
  });

  it('does not use the server plan before onboarding has settled', () => {
    mocks.onboarding.settled = false;
    mocks.onboarding.profile = { ...blank, pickedPlan: 'pro' };
    mocks.billing = { data: summaryWith([PRO]), error: undefined };
    mount();
    expect(mocks.replace).not.toHaveBeenCalled();
  });

  it('keeps the server plan for an unverified address (nothing cleared)', () => {
    mocks.onboarding.profile = { ...blank, pickedPlan: 'pro' };
    mocks.auth = { ...mocks.auth, user: { ...mocks.auth.user, emailVerified: false } as typeof mocks.auth.user };
    mocks.billing = { data: summaryWith([PRO]), error: undefined };
    mocks.projects.data = [project('p1', '2026-11-11T10:00:00Z')];
    mount();
    expect(mocks.replace).not.toHaveBeenCalled();
    expect(mocks.clearPickedPlan).not.toHaveBeenCalled();
    expect(screen.getByTestId('picked-plan').getAttribute('data-plan')).toBe('pro');
  });

  it('dismissing the notice clears both the local and the server plan', () => {
    store({ plan: 'pro' });
    mocks.onboarding.profile = { ...blank, pickedPlan: 'pro' };
    mocks.billing = { data: summaryWith([{ ...PRO, current: true }]), error: undefined };
    mocks.projects.data = [project('p1', '2026-11-11T10:00:00Z')];
    mount();
    act(() => screen.getByTestId('picked-plan').click());
    expect(mocks.clearPickedPlan).toHaveBeenCalledTimes(1);
    expect(window.localStorage.getItem('rb-signup-intent:v1')).toBeNull();
  });

  it('does not redirect a member who cannot manage billing, and leaves no token', () => {
    store({ plan: 'pro' });
    mocks.billing = { data: { ...summaryWith([PRO]), canManageBilling: false }, error: undefined };
    mocks.projects.data = [project('p1', '2026-11-11T10:00:00Z')];
    mount();
    expect(mocks.replace).not.toHaveBeenCalled();
    expect(window.sessionStorage.getItem('rb-plan-start:v1')).toBeNull();
  });

  it('falls back to the welcome questions when the summary cannot be read', () => {
    store({ plan: 'pro' });
    context = { ...context, welcome: 'redirect' };
    mocks.billing = { data: undefined, error: new Error('down') };
    mount();
    expect(mocks.replace).toHaveBeenCalledWith('/welcome');
  });

  it('shows no plan notice to someone whose link carried none', () => {
    mount();
    expect(screen.queryByTestId('picked-plan')).toBeNull();
  });

  it('still sends a brand-new account to the welcome questions when the link carried nothing', () => {
    context = { ...context, welcome: 'redirect' };
    mount();
    expect(mocks.replace).toHaveBeenCalledWith('/welcome');
  });
});

describe('Dashboard funnel call-site', () => {
  it('does not emit anything merely because the page rendered', () => {
    mount();
    expect(mocks.emitFunnelEvent).not.toHaveBeenCalled();
  });

  it('emits alpha.first_project when the project form reports a successful creation', () => {
    mount();
    expect(mocks.captured.onSuccess).toBeTypeOf('function');
    act(() => mocks.captured.onSuccess?.({ id: 'p1' }));
    expect(mocks.emitFunnelEvent).toHaveBeenCalledTimes(1);
    expect(mocks.emitFunnelEvent).toHaveBeenCalledWith('alpha.first_project', userKeyFromEmail('ada@example.com'));
    expect(mocks.push).toHaveBeenCalledWith('/projects/p1?tab=files');
  });
});
