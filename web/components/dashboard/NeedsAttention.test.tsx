// @vitest-environment jsdom

/**
 * The dashboard "N runs need your attention" panel is the loudest thing on the page, so it has to be
 * closable. The cases that matter: clicking X hides it and remembers that, and a NEW failure still
 * appears rather than being swept up by the last dismissal.
 *
 * The "Recent runs" list below it is fed the same server-provided executions, so it shares the
 * dismissal: a failure closed in the warning must not be listed there (nor after a reload), or the
 * viewer sees the very alert they just dismissed come straight back.
 */
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { NeedsAttention, RecentRuns } from './sections';
import type { DashboardExecution } from '@/lib/types/dashboard';
import {
  __resetDismissedRunFailuresForTests,
  DISMISSED_RUN_FAILURES_KEY,
  dismissFailedRuns,
} from '@/lib/hooks/use-dismissed-runs';

vi.mock('next/link', () => ({
  default: ({ children, href }: { children: React.ReactNode; href: string }) => <a href={href}>{children}</a>,
}));
vi.mock('@/components/assistant/AssistantShellProvider', () => ({
  useAssistantShell: () => ({ openWithPrompt: vi.fn() }),
}));

function run(over: Partial<DashboardExecution>): DashboardExecution {
  return {
    id: 'run-a',
    projectId: 'project-1',
    projectName: 'Launch',
    workflowDefinitionId: 'workflow-1',
    workflowName: 'Product promo',
    status: 'Failed',
    startedAt: '2026-10-18T09:00:00Z',
    completedAt: '2026-10-18T09:04:00Z',
    errorMessage: 'The render timed out.',
    userRequest: null,
    outputStepResultId: null,
    outputFileName: null,
    ...over,
  };
}

const renderPanel = (runs: DashboardExecution[]) =>
  render(
    <MantineProvider>
      <NeedsAttention runs={runs} />
    </MantineProvider>,
  );

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({
      matches: false, media: query, onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
});

beforeEach(() => {
  window.localStorage.clear();
  __resetDismissedRunFailuresForTests();
});

afterEach(() => {
  cleanup();
  window.localStorage.clear();
  __resetDismissedRunFailuresForTests();
});

describe('NeedsAttention', () => {
  it('renders nothing when no run needs attention', () => {
    renderPanel([]);
    expect(screen.queryByText(/needs your attention/)).toBeNull();
  });

  it('lists a failed run and opens it', () => {
    renderPanel([run({})]);

    expect(screen.getByText('A run needs your attention')).toBeTruthy();
    expect(screen.getByText('The render timed out.')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Open run' }).getAttribute('href')).toBe(
      '/projects/project-1/workflows/workflow-1/executions/run-a',
    );
  });

  it('hides the whole warning on X and remembers which runs were dismissed', () => {
    renderPanel([run({ id: 'run-a' }), run({ id: 'run-b', workflowName: 'Teaser' })]);
    expect(screen.getByText('2 runs need your attention')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Dismiss these failed run warnings' }));

    expect(screen.queryByText(/runs? need your attention/)).toBeNull();
    expect(JSON.parse(window.localStorage.getItem(DISMISSED_RUN_FAILURES_KEY) ?? '[]').sort()).toEqual(['run-a', 'run-b']);
  });

  it('keeps a dismissed warning hidden across a full reload, and surfaces a NEW failure on both surfaces', () => {
    const renderSurfaces = (runs: DashboardExecution[]) =>
      render(
        <MantineProvider>
          <NeedsAttention runs={runs} />
          <RecentRuns runs={runs} loading={false} />
        </MantineProvider>,
      );
    const history = () => screen.getByText('Recent runs').closest('section') as HTMLElement;

    // The failed run shows on both the warning and the history list below it.
    renderSurfaces([run({ id: 'run-a' })]);
    expect(screen.getByText('A run needs your attention')).toBeTruthy();
    expect(within(history()).getByText('Product promo')).toBeTruthy();

    // Dismiss it, then reload: the warning stays hidden AND the run is gone from the history list,
    // which is the surface that made the viewer report "the alert came back".
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss these failed run warnings' }));
    cleanup();
    __resetDismissedRunFailuresForTests();
    renderSurfaces([run({ id: 'run-a' })]);
    expect(screen.queryByText(/needs your attention/)).toBeNull();
    expect(screen.queryByText('Product promo')).toBeNull();
    cleanup();

    // A run the viewer has NOT dismissed still surfaces on both.
    renderSurfaces([run({ id: 'run-a' }), run({ id: 'run-b', workflowName: 'Teaser' })]);
    expect(screen.getByText('A run needs your attention')).toBeTruthy();
    expect(within(history()).getByText('Teaser')).toBeTruthy();
  });

  it('does not resurrect a dismissed run in the Recent runs list, and keeps the rest', () => {
    dismissFailedRuns(['run-a']);
    render(
      <MantineProvider>
        <RecentRuns runs={[run({ id: 'run-a' }), run({ id: 'run-b', workflowName: 'Teaser' })]} loading={false} />
      </MantineProvider>,
    );

    expect(screen.queryByText('Product promo')).toBeNull();
    expect(screen.getByText('Teaser')).toBeTruthy();
  });

  it('drops the Recent runs panel once every run in it is dismissed, without claiming none finished', () => {
    dismissFailedRuns(['run-a']);
    render(
      <MantineProvider>
        <RecentRuns runs={[run({ id: 'run-a' })]} loading={false} />
      </MantineProvider>,
    );

    expect(screen.queryByText('Recent runs')).toBeNull();
    expect(screen.queryByText('No finished runs yet.')).toBeNull();
  });

  it('still says nothing has finished when there really are no recent runs', () => {
    render(<MantineProvider><RecentRuns runs={[]} loading={false} /></MantineProvider>);
    expect(screen.getByText('No finished runs yet.')).toBeTruthy();
  });

  it('still surfaces a failure the viewer has not dismissed, counting only the visible ones', () => {
    dismissFailedRuns(['run-a']);
    renderPanel([
      run({ id: 'run-a', errorMessage: 'The first failure.' }),
      run({ id: 'run-b', errorMessage: 'The second failure.' }),
    ]);

    // run-a's row is gone; the title already says one run, not two, and run-b still shows.
    expect(screen.getByText('A run needs your attention')).toBeTruthy();
    expect(screen.queryByText('The first failure.')).toBeNull();
    expect(screen.getByText('The second failure.')).toBeTruthy();
    expect(screen.queryByText('2 runs need your attention')).toBeNull();
  });
});
