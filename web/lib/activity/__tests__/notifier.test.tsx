// @vitest-environment jsdom
import { cleanup, render } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const show = vi.fn();
let pathname = '/dashboard';
vi.mock('next/navigation', () => ({ usePathname: () => pathname, useRouter: () => ({ push: vi.fn() }) }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: (...a: unknown[]) => show(...a) } }));
vi.mock('@/i18n', () => ({ useT: () => Object.assign((k: string, v?: Record<string, string>) => `${k}${v?.label ? `:${v.label}` : ''}`, { has: () => true }) }));

import '../builtins';
import { FinishNotifier, isOnJobPage, setActivityToasts } from '../notifier';
import { item } from './helpers';
import type { ActivityJob } from '../types';

const job = (over: Partial<ActivityJob> = {}): ActivityJob => ({ ...item(), origin: 'server', ...over }) as ActivityJob;
const old = '2020-01-01T00:00:00Z';

beforeEach(() => {
  show.mockClear();
  pathname = '/dashboard';
  window.localStorage.clear();
});
afterEach(cleanup);

describe('FinishNotifier', () => {
  it('stays silent for jobs already finished at first load', () => {
    render(<FinishNotifier jobs={[job({ state: 'done', createdAt: old })]} loaded />);
    expect(show).not.toHaveBeenCalled();
  });

  it('notifies once for an observed running -> failed transition, with the refund note', () => {
    const { rerender } = render(<FinishNotifier jobs={[job({ createdAt: old })]} loaded />);
    const failed = job({ createdAt: old, state: 'failed', failure: { code: 'x', refunded: true } });
    rerender(<FinishNotifier jobs={[failed]} loaded />);
    rerender(<FinishNotifier jobs={[failed]} loaded />);
    expect(show).toHaveBeenCalledTimes(1);
    const message = (show.mock.calls[0][0] as { message: { props: { children: unknown[] } } }).message;
    expect(String(message.props.children[0])).toContain('toast.refunded');
  });

  it('does not notify while the user is on the job page, for other people runs, or quiet media successes', () => {
    pathname = '/editor/p1/timeline/t1';
    const { rerender } = render(
      <FinishNotifier jobs={[job({ createdAt: old }), job({ id: 'run:2', createdAt: old, startedByYou: false }), job({ id: 'run:3', type: 'mediaPrep', createdAt: old })]} loaded />,
    );
    rerender(
      <FinishNotifier
        jobs={[
          job({ createdAt: old, state: 'done' }),
          job({ id: 'run:2', createdAt: old, startedByYou: false, state: 'done' }),
          job({ id: 'run:3', type: 'mediaPrep', createdAt: old, state: 'done' }),
        ]}
        loaded
      />,
    );
    expect(show).not.toHaveBeenCalled();
  });

  it('respects the per-device switch', () => {
    setActivityToasts(false);
    const { rerender } = render(<FinishNotifier jobs={[job({ createdAt: old })]} loaded />);
    rerender(<FinishNotifier jobs={[job({ createdAt: old, state: 'done' })]} loaded />);
    expect(show).not.toHaveBeenCalled();
  });

  it('toasts a transition into needsReview even on the job page', () => {
    pathname = '/editor/p1/timeline/t1';
    const { rerender } = render(<FinishNotifier jobs={[job({ createdAt: old })]} loaded />);
    rerender(<FinishNotifier jobs={[job({ createdAt: old, state: 'needsReview' })]} loaded />);
    expect(show).toHaveBeenCalledTimes(1);
  });

  it('a magic blur success is quiet on any timeline of its project, but a failure there still toasts', () => {
    pathname = '/editor/p1/timeline/other';
    const blur = (over: Partial<ActivityJob>) => job({ type: 'objectAnalysis', projectId: 'p1', createdAt: old, ...over });
    const { rerender } = render(<FinishNotifier jobs={[blur({}), blur({ id: 'run:2' })]} loaded />);
    rerender(<FinishNotifier jobs={[blur({ state: 'done' }), blur({ id: 'run:2', state: 'failed', failure: { code: null, refunded: null } })]} loaded />);
    expect(show).toHaveBeenCalledTimes(1);
  });

  it('compares pathnames ignoring the query', () => {
    expect(isOnJobPage('/editor', '/editor?project=p')).toBe(true);
    expect(isOnJobPage('/editor/p', '/editor?project=p')).toBe(false);
  });
});
