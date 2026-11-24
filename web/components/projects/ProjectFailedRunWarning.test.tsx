// @vitest-environment jsdom

/**
 * The project overview's "Last run failed" panel shares the dashboard's dismissal memory, so closing
 * the same failure on either page closes it on both — while a newer failure still shows up.
 */
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ProjectOverviewTab } from './ProjectSections';
import type { DashboardExecution } from '@/lib/types/dashboard';
import type { Project } from '@/lib/types/project';
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

const PROJECT: Project = {
  id: 'project-1',
  name: 'Launch',
  description: '',
  status: 'Active',
  userId: 'user-1',
  createdAt: '2026-10-01T09:00:00Z',
  updatedAt: '2026-10-18T09:00:00Z',
};

function run(over: Partial<DashboardExecution>): DashboardExecution {
  return {
    id: 'run-a',
    projectId: PROJECT.id,
    projectName: PROJECT.name,
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

const renderTab = (attention: DashboardExecution[]) =>
  render(
    <MantineProvider>
      <ProjectOverviewTab
        project={PROJECT}
        active={[]}
        attention={attention}
        outputs={[]}
        workflows={[]}
        onOpenTab={vi.fn()}
      />
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

describe('ProjectOverviewTab failed-run warning', () => {
  it('shows the failure with a way to open the run', () => {
    renderTab([run({})]);

    expect(screen.getByText('Last run failed')).toBeTruthy();
    expect(screen.getByText('The render timed out.')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Open run' }).getAttribute('href')).toBe(
      '/projects/project-1/workflows/workflow-1/executions/run-a',
    );
  });

  it('hides on X and stays hidden after navigating away and back', () => {
    renderTab([run({ id: 'run-a' })]);

    fireEvent.click(screen.getByRole('button', { name: 'Dismiss this failed run warning' }));
    expect(screen.queryByText('Last run failed')).toBeNull();
    expect(JSON.parse(window.localStorage.getItem(DISMISSED_RUN_FAILURES_KEY) ?? '[]')).toEqual(['run-a']);

    cleanup();
    renderTab([run({ id: 'run-a' })]);
    expect(screen.queryByText('Last run failed')).toBeNull();
  });

  it('surfaces a NEW failure after an older one was dismissed', () => {
    dismissFailedRuns(['run-a']);
    renderTab([run({ id: 'run-a' })]);
    expect(screen.queryByText('Last run failed')).toBeNull();

    cleanup();
    renderTab([run({ id: 'run-a' }), run({ id: 'run-b', workflowName: 'Teaser' })]);
    expect(screen.getByText('Last run failed')).toBeTruthy();
    expect(screen.getByText('Teaser')).toBeTruthy();
    expect(screen.queryByText('Product promo')).toBeNull();
  });
});
