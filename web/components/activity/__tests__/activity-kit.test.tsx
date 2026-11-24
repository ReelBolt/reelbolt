import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ReactNode } from 'react';
import type { ActivityJob } from '@/lib/activity/types';
import { getJobType, registerJobType } from '@/lib/activity/registry';
import { IconActivity } from '@tabler/icons-react';

const act = {
  jobs: [] as ActivityJob[],
  activeCount: 0,
  failedUnseenCount: 0,
  dismiss: vi.fn(),
  clearFinished: vi.fn(),
  markFailuresSeen: vi.fn(),
  resolveTrackedJob: vi.fn(async () => undefined),
  cancel: vi.fn(async () => undefined),
};
vi.mock('@/lib/activity/ActivityProvider', () => ({ useActivity: () => act }));
let phone = false;
vi.mock('@mantine/hooks', async (orig) => ({ ...(await orig<typeof import('@mantine/hooks')>()), useMediaQuery: () => phone }));
vi.mock('next/link', () => ({ default: ({ href, children, ...r }: { href: string; children: ReactNode }) => <a href={href} {...r}>{children}</a> }));

import { ActivityIndicator, ActivityTray, InlineJobStatus, JobCost, JobFailure, JobProgress, JobStatusBadge } from '..';

Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (q: string) => ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false, onchange: null }),
});
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

const job = (over: Partial<ActivityJob> = {}): ActivityJob => ({
  id: 'j1', type: 'render', state: 'running', projectId: 'p', projectName: 'Proj', title: 'My film', executionId: null, workflowId: null,
  subject: {}, step: null, progress: null, credits: { heldCredits: null, chargedCredits: null }, failure: null, startedByYou: true,
  cancellable: true, createdAt: '2026-01-01T00:00:00Z', startedAt: null, endedAt: null, origin: 'server', ...over,
});
const wrap = (ui: ReactNode) => render(<MantineProvider>{ui}</MantineProvider>);

beforeEach(() => {
  Object.assign(act, { jobs: [], activeCount: 0, failedUnseenCount: 0 });
  phone = false;
  vi.clearAllMocks();
});
afterEach(cleanup);

describe('JobStatusBadge', () => {
  it.each([
    ['waiting', 'Waiting for a free slot'], ['estimating', 'Getting a cost estimate'], ['running', 'Running'], ['done', 'Done'],
    ['failed', 'Failed'], ['cancelled', 'Cancelled'], ['needsReview', 'Finished, needs your OK'],
  ])('%s', (state, label) => {
    wrap(<JobStatusBadge state={state} />);
    expect(screen.getByText(label)).toBeTruthy();
  });
});

describe('JobProgress', () => {
  it('is determinate with a known value and shows the live stage', () => {
    const { container } = wrap(<JobProgress job={job({ progress: 0.2, liveProgress: 0.5, liveStage: 'Encoding' })} />);
    expect(container.querySelector('[data-indeterminate]')).toBeNull();
    expect(screen.getByRole('progressbar').getAttribute('aria-valuenow')).toBe('50');
    expect(screen.getByText('Encoding the video')).toBeTruthy();
  });
  it('is indeterminate while running without progress, with the step line', () => {
    const { container } = wrap(<JobProgress job={job({ step: { order: 2, count: 5, stepType: null } })} />);
    expect(container.querySelector('[data-indeterminate="true"]')).not.toBeNull();
    expect(screen.getByText('Step 2 of 5')).toBeTruthy();
  });
});

describe('JobFailure and JobCost', () => {
  it('maps the code to plain copy, never the raw code, and says credits came back', () => {
    const { container } = wrap(<JobFailure job={job({ state: 'failed', failure: { code: 'INSUFFICIENT_CREDITS', refunded: true } })} />);
    expect(screen.getByText('You do not have enough credits for this.')).toBeTruthy();
    expect(screen.getByText('Your credits were given back.')).toBeTruthy();
    expect(container.textContent).not.toContain('INSUFFICIENT_CREDITS');
  });
  it('falls back to generic for an unknown code and says credits were kept', () => {
    const { container } = wrap(<JobFailure job={job({ state: 'failed', failure: { code: 'WEIRD_X', refunded: false } })} />);
    expect(screen.getByText('Something went wrong.')).toBeTruthy();
    expect(screen.getByText('Credits used up to the failure were kept.')).toBeTruthy();
    expect(container.textContent).not.toContain('WEIRD_X');
  });
  it('says nothing about credits when refunded is null', () => {
    wrap(<JobFailure job={job({ state: 'failed', failure: { code: null, refunded: null } })} />);
    expect(screen.queryByText(/credits/)).toBeNull();
  });
  it('cost shows held, used, and hides when null', () => {
    const { rerender } = wrap(<JobCost job={job({ credits: { heldCredits: 2.5, chargedCredits: null } })} />);
    expect(screen.getByText('About 2.5 credits held')).toBeTruthy();
    rerender(<MantineProvider><JobCost job={job({ state: 'done', credits: { heldCredits: null, chargedCredits: 1 } })} /></MantineProvider>);
    expect(screen.getByText('1 credit used')).toBeTruthy();
    rerender(<MantineProvider><JobCost job={job()} /></MantineProvider>);
    expect(screen.queryByText(/credit/)).toBeNull();
  });
});

describe('InlineJobStatus', () => {
  it('confirms before calling the registry cancel', async () => {
    const cancelFn = vi.fn(async () => undefined);
    registerJobType({ type: 'cancelable', icon: IconActivity, labelKey: 'types.generic', cancel: cancelFn });
    const registerCancel = (j: ActivityJob) => getJobType(j.type).cancel?.(j);
    wrap(<InlineJobStatus job={job({ type: 'cancelable' })} onCancel={(j) => registerCancel(j)} />);
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(cancelFn).not.toHaveBeenCalled();
    fireEvent.click(await screen.findByRole('button', { name: 'Yes, stop it' }));
    expect(cancelFn).toHaveBeenCalledTimes(1);
  });
  it('uses the activity model cancel by default and has no button when the type cannot cancel', async () => {
    registerJobType({ type: 'cancelable', icon: IconActivity, labelKey: 'types.generic', cancel: async () => undefined });
    const { unmount } = wrap(<InlineJobStatus job={job({ type: 'cancelable' })} />);
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Yes, stop it' }));
    await waitFor(() => expect(act.cancel).toHaveBeenCalled());
    unmount();
    wrap(<InlineJobStatus job={job({ type: 'nope' })} />);
    expect(screen.queryByRole('button', { name: 'Cancel' })).toBeNull();
    expect(screen.getByRole('status')).toBeTruthy();
  });
});

describe('ActivityTray and ActivityIndicator', () => {
  const finished = job({ id: 'f1', state: 'done', endedAt: '2026-01-01T00:01:00Z' });
  const review = job({ id: 'r1', state: 'needsReview', review: { summaryKey: 'states.needsReview' } });

  it('shows the empty state and the switch', () => {
    wrap(<ActivityTray opened onChange={() => {}}><button>x</button></ActivityTray>);
    expect(screen.getByText(/Nothing is running/)).toBeTruthy();
    expect(screen.getByLabelText('Tell me when a background job finishes')).toBeTruthy();
  });
  it('lists the sections, clears finished and dismisses a row', () => {
    act.jobs = [finished, job({ id: 'a' }), review];
    wrap(<ActivityTray opened onChange={() => {}}><button>x</button></ActivityTray>);
    const titles = screen.getAllByRole('region').map((r) => r.getAttribute('aria-label'));
    expect(titles).toEqual(['Needs your OK', 'Running now', 'Recently finished']);
    fireEvent.click(screen.getByRole('button', { name: 'Clear finished' }));
    expect(act.clearFinished).toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }));
    expect(act.dismiss).toHaveBeenCalledWith('f1');
  });
  it('Apply and Discard resolve the review job', () => {
    act.jobs = [review];
    wrap(<ActivityTray opened onChange={() => {}}><button>x</button></ActivityTray>);
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    expect(act.resolveTrackedJob).toHaveBeenCalledWith('r1', 'applied');
    fireEvent.click(screen.getByRole('button', { name: 'Discard' }));
    expect(act.resolveTrackedJob).toHaveBeenCalledWith('r1', 'discarded');
  });
  it('uses a Drawer on a phone', () => {
    phone = true;
    wrap(<ActivityTray opened onChange={() => {}}><button>x</button></ActivityTray>);
    expect(document.querySelector('.mantine-Drawer-content')).not.toBeNull();
  });
  it('indicator: accessible name, count, failed dot, marks seen on open', () => {
    Object.assign(act, { jobs: [job()], activeCount: 3, failedUnseenCount: 1 });
    wrap(<ActivityIndicator />);
    const btn = screen.getByRole('button', { name: 'Background activity, 3 running' });
    expect(screen.getByTestId('activity-count').textContent).toBe('3');
    expect(document.querySelector('[data-dot="true"]')).not.toBeNull();
    fireEvent.click(btn);
    expect(act.markFailuresSeen).toHaveBeenCalled();
  });
  it('indicator: no dot and no count when idle; dot for a pending OK', () => {
    const { unmount } = wrap(<ActivityIndicator />);
    expect(document.querySelector('[data-dot="true"]')).toBeNull();
    expect(screen.queryByTestId('activity-count')).toBeNull();
    unmount();
    act.jobs = [review];
    wrap(<ActivityIndicator />);
    expect(document.querySelector('[data-dot="true"]')).not.toBeNull();
  });
});
