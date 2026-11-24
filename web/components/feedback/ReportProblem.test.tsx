// @vitest-environment jsdom

/** "Report a problem" on a failed run: when it shows, the "on us" line, and the dialog opening with the run attached. */
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.mock('@/lib/api/feedback', () => ({ submitFeedback: vi.fn() }));

import { ReportProblem, canReportRun, isOnUs } from './ReportProblem';
import type { WorkflowExecution } from '@/lib/types/workflow';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => false })
  });
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(cleanup);

const EXEC = '11111111-1111-4111-8111-111111111111';
const PROJ = '22222222-2222-4222-8222-222222222222';
const step = (failureClass: string | null) => ({ status: 'Failed', failureClass }) as unknown as WorkflowExecution['stepResults'][number];
const run = (status: string, classes: Array<string | null> = []) =>
  ({ status, stepResults: classes.map(step) }) as Pick<WorkflowExecution, 'status' | 'stepResults'>;

const show = (r: Pick<WorkflowExecution, 'status' | 'stepResults'>) =>
  render(
    <MantineProvider>
      <ReportProblem run={r} executionId={EXEC} projectId={PROJ} />
    </MantineProvider>
  );

describe('visibility', () => {
  it('shows for Failed only', () => {
    expect(canReportRun(run('Failed'))).toBe(true);
    expect(canReportRun(run('Cancelled'))).toBe(false);
    expect(canReportRun(run('Stopped'))).toBe(false);
    expect(canReportRun(run('Passed'))).toBe(false);
    expect(canReportRun(run('Running'))).toBe(false);
  });
  it('hides when the failure was the person stopping it', () => {
    expect(canReportRun(run('Failed', ['UserCancelled']))).toBe(false);
    expect(canReportRun(run('Failed', ['UserInput']))).toBe(true);
  });
  it('says "on us" only for the platform class', () => {
    expect(isOnUs(run('Failed', ['PlatformInternal']))).toBe(true);
    expect(isOnUs(run('Failed', ['PlatformProviderError']))).toBe(true);
    expect(isOnUs(run('Failed', ['UserInput']))).toBe(false);
    expect(isOnUs(run('Failed', ['InsufficientCredits']))).toBe(false);
    expect(isOnUs(run('Failed', ['EntitlementDenied']))).toBe(false);
    expect(isOnUs(run('Failed'))).toBe(false);
    expect(isOnUs(run('Passed', ['PlatformInternal']))).toBe(false);
  });
});

describe('ReportProblem', () => {
  it('renders nothing for a Cancelled or Passed run', () => {
    show(run('Cancelled'));
    show(run('Passed'));
    expect(screen.queryByTestId('report-problem')).toBeNull();
  });
  it('shows the button, at least 44px tall, and the on-us line for a platform failure', () => {
    show(run('Failed', ['PlatformInternal']));
    expect(screen.getByTestId('report-problem-on-us').textContent).toBe('This one is on us. You were not charged for this step.');
    expect(screen.getByTestId('report-problem-button').style.minHeight).toBe('44px');
  });
  it('omits the on-us line for a user-class failure', () => {
    show(run('Failed', ['UserInput']));
    expect(screen.queryByTestId('report-problem-on-us')).toBeNull();
    expect(screen.getByText('Report a problem')).toBeTruthy();
  });
  it('opens the feedback dialog as a bug with the run attached', () => {
    show(run('Failed', ['UserInput']));
    fireEvent.click(screen.getByTestId('report-problem-button'));
    expect(screen.getByRole('dialog')).toBeTruthy();
    const payload = JSON.parse(screen.getByTestId('feedback-payload').textContent ?? '{}');
    expect(payload.kind).toBe('bug');
    expect(payload.context.executionId).toBe(EXEC);
    expect(payload.context.projectId).toBe(PROJ);
  });
});
