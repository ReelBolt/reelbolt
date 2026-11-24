import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiFetch = vi.hoisted(() => vi.fn(async (..._args: unknown[]) => ({})));
vi.mock('./client', () => ({ apiFetch }));

import { getExecution, getStepResult } from './executions';

describe('execution reads', () => {
  beforeEach(() => apiFetch.mockClear());

  it('reads the full run by default', async () => {
    await getExecution('p1', 'e1');
    expect(apiFetch).toHaveBeenCalledWith('/api/v1/projects/p1/executions/e1');
  });

  it('asks for the summary when told to', async () => {
    await getExecution('p1', 'e1', { detail: 'summary' });
    expect(apiFetch).toHaveBeenCalledWith('/api/v1/projects/p1/executions/e1?detail=summary');
  });

  it('reads one step result', async () => {
    await getStepResult('p1', 'e1', 'r1');
    expect(apiFetch).toHaveBeenCalledWith('/api/v1/projects/p1/executions/e1/step-results/r1');
  });
});
