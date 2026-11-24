// @vitest-environment jsdom
/**
 * E8-UI: the run-level "Ran on your machine" chip. It exists to answer "did this run use my
 * computer?" from the run list, so it must appear for a run whose steps name OrgRunner — and
 * never for one that stayed on the platform or used the cloud pool, where it would be a claim
 * about a machine that was not involved.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { WorkflowExecution, WorkflowStepResult } from '@/lib/types/workflow';

const H = vi.hoisted(() => ({ executions: [] as unknown[] }));
vi.mock('@/lib/hooks/use-executions', () => ({
  useExecutions: () => ({ data: H.executions, isLoading: false }),
}));

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

import { ExecutionHistory } from './ExecutionHistory';

beforeEach(() => {
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null,
    addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {},
    dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

function step(overrides: Partial<WorkflowStepResult> = {}): WorkflowStepResult {
  return {
    id: 'r', workflowStepId: 's', output: '', tokensUsed: 0, durationMs: 1,
    executedAt: '2026-10-08T10:00:00Z', status: 'Completed', ...overrides,
  };
}

function execution(overrides: Partial<WorkflowExecution> = {}): WorkflowExecution {
  return {
    id: 'e1', workflowDefinitionId: 'w1', status: 'Passed',
    startedAt: '2026-10-08T10:00:00Z', completedAt: '2026-10-08T10:05:00Z',
    iterationCount: 0, resultJson: null, stepResults: [], reviewScores: [], ...overrides,
  };
}

function renderHistory(executions: WorkflowExecution[]) {
  H.executions = executions;
  render(
    <MantineProvider>
      <ExecutionHistory projectId="p1" workflowId="w1" />
    </MantineProvider>,
  );
}

describe('ExecutionHistory executed-by chip', () => {
  it('chips a run that used a machine from the workspace', () => {
    renderHistory([execution({ stepResults: [step({ computeTarget: 'Local' }), step({ computeTarget: 'OrgRunner', fromRunner: true })] })]);
    expect(screen.getByText('Ran on your machine')).toBeTruthy();
  });

  it('leaves a platform-only run unmarked', () => {
    renderHistory([execution({ stepResults: [step({ computeTarget: 'Local' })] })]);
    expect(screen.queryByText('Ran on your machine')).toBeNull();
  });

  it('leaves a cloud-pool run unmarked, because no machine of the workspace was involved', () => {
    renderHistory([execution({ stepResults: [step({ computeTarget: 'CloudPool' })] })]);
    expect(screen.queryByText('Ran on your machine')).toBeNull();
  });

  it('chips only the runs that used a machine, run by run', () => {
    renderHistory([
      execution({ id: 'e1', stepResults: [step({ computeTarget: 'OrgRunner' })] }),
      execution({ id: 'e2', stepResults: [step({ computeTarget: 'Local' })] }),
    ]);
    expect(screen.getAllByText('Ran on your machine')).toHaveLength(1);
  });
});
