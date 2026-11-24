// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { WorkflowExecution } from '@/lib/types/workflow';
import { FailedRunCredits } from './FirstRunFailed';
import { InsertFailureNext } from './InsertFailureNext';

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
});
afterEach(cleanup);
const run = (rs: Array<Record<string, unknown>>) => ({ stepResults: rs }) as unknown as WorkflowExecution;
const wrap = (ui: React.ReactNode) => render(<MantineProvider>{ui}</MantineProvider>);

describe('FailedRunCredits', () => {
  it('says what finished steps cost and that the failed step charged nothing', () => {
    wrap(<FailedRunCredits billingEnabled execution={run([{ status: 'Completed', creditsCharged: 0.326 }, { status: 'Failed', creditsCharged: 0, failureClass: 'PlatformInternal' }])} />);
    expect(screen.getByTestId('failed-run-credits').textContent).toBe('This run used 0.326 credits for the steps that finished (1 step); the step that failed charged nothing.');
  });
  it('says nothing was charged when every step is zero', () => {
    wrap(<FailedRunCredits billingEnabled execution={run([{ status: 'Failed', creditsCharged: 0 }])} />);
    expect(screen.getByTestId('failed-run-credits').textContent).toBe('Nothing was charged for this run.');
  });
  it('claims a refund only for a charged failed step on our side', () => {
    wrap(<FailedRunCredits billingEnabled execution={run([{ status: 'Failed', creditsCharged: 2, failureClass: 'PlatformInternal' }])} />);
    expect(screen.getByTestId('failed-run-credits').textContent).toMatch(/refunded/);
    cleanup();
    wrap(<FailedRunCredits billingEnabled execution={run([{ status: 'Failed', creditsCharged: 2, failureClass: 'UserInput' }])} />);
    expect(screen.getByTestId('failed-run-credits').textContent).not.toMatch(/refund/);
  });
  it('renders nothing without billing or recorded figures', () => {
    wrap(<FailedRunCredits billingEnabled={false} execution={run([{ status: 'Failed', creditsCharged: 0 }])} />);
    wrap(<FailedRunCredits billingEnabled execution={run([{ status: 'Failed' }])} />);
    expect(screen.queryByTestId('failed-run-credits')).toBeNull();
  });
});

describe('InsertFailureNext', () => {
  it('leads with Run again for a tracking failure and asks the assistant with a prompt', () => {
    const again = vi.fn();
    const ask = vi.fn();
    wrap(<InsertFailureNext code="INSERT_TRACKING_FAILED" onRunAgain={again} onAskAssistant={ask} />);
    const buttons = screen.getAllByRole('button').map((b) => b.textContent);
    expect(buttons[0]).toBe('Run again');
    fireEvent.click(screen.getByRole('button', { name: 'Run again' }));
    expect(again).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: 'Ask the assistant to put it in a corner instead' }));
    expect(ask).toHaveBeenCalledWith(expect.stringContaining('corner'));
  });
  it('renders nothing for other failures', () => {
    wrap(<InsertFailureNext code="VOICE_NOT_FOUND" onRunAgain={vi.fn()} />);
    expect(screen.queryByTestId('insert-next-step')).toBeNull();
  });
});

describe('FailedRunCredits honesty', () => {
  it('does not say the failed step charged nothing when its figure is unknown', () => {
    wrap(<FailedRunCredits billingEnabled execution={run([{ status: 'Completed', creditsCharged: 0.3 }, { status: 'Failed', creditsCharged: null }])} />);
    expect(screen.getByTestId('failed-run-credits').textContent).toBe('This run used 0.300 credits for the steps that finished (1 step).');
  });
});
describe('InsertFailureNext corner button', () => {
  it('is hidden for content failures', () => {
    wrap(<InsertFailureNext code="INSERT_CONTENT_MISSING" onRunAgain={vi.fn()} onAskAssistant={vi.fn()} />);
    expect(screen.queryByRole('button', { name: /corner/ })).toBeNull();
  });
});
