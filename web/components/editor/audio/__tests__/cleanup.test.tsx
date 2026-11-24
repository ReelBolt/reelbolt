// @vitest-environment jsdom
/** Audio cleanup: the job hook's state machine and the panel with a mocked API. */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, renderHook, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import { doc, media, track } from '@/lib/editing/__tests__/fixtures';
import { apply, type EditOp } from '@/lib/editing/engine';
import type { CleanupEstimate, CleanupJob } from '@/lib/editing/cleanup/types';
import { CleanupPanel } from '../CleanupPanel';
import { useCleanupJob, type CleanupJobApi } from '../useCleanupJob';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;
Element.prototype.scrollIntoView = vi.fn();
window.HTMLMediaElement.prototype.pause = vi.fn();
window.HTMLMediaElement.prototype.play = vi.fn().mockResolvedValue(undefined);

beforeEach(() => window.localStorage.clear());
afterEach(cleanup);

const FREE: CleanupEstimate = { billingEnabled: true, hosted: false, credits: 0, affordable: true, cached: false };
const PAID: CleanupEstimate = { billingEnabled: true, hosted: true, credits: 3, balanceCredits: 10, affordable: true, cached: false };
const job = (over: Partial<CleanupJob> = {}): CleanupJob => ({ executionId: 'e1', state: 'ready', target: 'Hum', applied: true, outputFileId: 'out-1', ...over });

function mockApi(estimate: CleanupEstimate, result: CleanupJob = job()): CleanupJobApi & { start: ReturnType<typeof vi.fn> } {
  return {
    estimate: vi.fn().mockResolvedValue(estimate),
    start: vi.fn().mockResolvedValue({ executionId: 'e1', target: 'Hum', hosted: estimate.hosted }),
    wait: vi.fn().mockResolvedValue(result),
  };
}

const ctx = { projectId: 'p1', userId: 'u@x', orgId: 'o1', outputKind: 'Audio' as const };

describe('useCleanupJob state machine', () => {
  it('a free target goes idle, estimating, confirm, running, done and never asks for consent', async () => {
    const api = mockApi(FREE);
    const { result } = renderHook(() => useCleanupJob(ctx, api));
    expect(result.current.state.kind).toBe('idle');
    await act(async () => {
      await result.current.begin('hum', 'file:g1');
    });
    expect(result.current.state.kind).toBe('confirm');
    await act(async () => {
      result.current.confirm();
    });
    await waitFor(() => expect(result.current.state.kind).toBe('done'));
    expect(api.start).toHaveBeenCalledWith('p1', 'file:g1', { target: 'Hum', outputKind: 'Audio' });
  });

  it('the first AI run asks for consent, sends the flag only after it, and remembers the answer', async () => {
    const api = mockApi(PAID);
    const { result } = renderHook(() => useCleanupJob(ctx, api));
    await act(async () => {
      await result.current.begin('studio', 'file:g1');
    });
    act(() => result.current.confirm());
    expect(result.current.state.kind).toBe('consent');
    expect(api.start).not.toHaveBeenCalled();
    act(() => result.current.declineConsent());
    expect(result.current.state.kind).toBe('confirm');
    act(() => result.current.confirm());
    await act(async () => {
      result.current.grantConsent();
    });
    await waitFor(() => expect(result.current.state.kind).toBe('done'));
    expect(api.start).toHaveBeenCalledWith('p1', 'file:g1', { target: 'IsolateVoice', outputKind: 'Audio', allowSourceMediaEgress: true });
    // second time: no dialog
    await act(async () => {
      await result.current.begin('music', 'file:g1');
    });
    await act(async () => {
      result.current.confirm();
    });
    await waitFor(() => expect(api.start).toHaveBeenCalledTimes(2));
    expect(result.current.state.kind).not.toBe('consent');
  });

  it('a refused start is an error with the refusal kind; a read-only editor cannot start', async () => {
    const api = mockApi(FREE);
    api.start.mockRejectedValue(Object.assign(new ApiError('no', 402), { body: { code: 'INSUFFICIENT_CREDITS', missing: 2 } }));
    const { result } = renderHook(() => useCleanupJob(ctx, api));
    await act(async () => {
      await result.current.begin('hum', 'file:g1');
    });
    await act(async () => {
      result.current.confirm();
    });
    await waitFor(() => expect(result.current.state.kind).toBe('error'));
    expect(result.current.state).toMatchObject({ refusal: { kind: 'credits', missing: 2 } });

    const ro = mockApi(FREE);
    const r2 = renderHook(() => useCleanupJob({ ...ctx, readOnly: true }, ro));
    await act(async () => {
      await r2.result.current.begin('hum', 'file:g1');
    });
    expect(ro.estimate).not.toHaveBeenCalled();
    expect(r2.result.current.state.kind).toBe('idle');
  });

  it('an unaffordable estimate stops at the credits refusal', async () => {
    const api = mockApi({ ...PAID, affordable: false, balanceCredits: 1 });
    const { result } = renderHook(() => useCleanupJob(ctx, api));
    await act(async () => {
      await result.current.begin('studio', 'file:g1');
    });
    expect(result.current.state).toMatchObject({ kind: 'error', refusal: { kind: 'credits', missing: 2 } });
  });
});

describe('CleanupPanel', () => {
  const d = doc([track('A1', 'audio', [media('a', 0, 100)])]);
  const mount = (api: CleanupJobApi, over: Partial<React.ComponentProps<typeof CleanupPanel>> = {}) => {
    const onApply = vi.fn<(ops: readonly EditOp[], label: string) => void>();
    render(
      <MantineProvider>
        <CleanupPanel doc={d} selection={['a']} projectId="p1" readOnly={false} onApply={onApply} api={api} userId="u@x" orgId="o1" {...over} />
      </MantineProvider>,
    );
    return onApply;
  };

  it('shows exactly the five noises, Basic and Studio', () => {
    mount(mockApi(FREE));
    for (const c of ['basic', 'studio', 'hum', 'wind', 'hiss', 'echo', 'music']) expect(screen.getByTestId(`cleanup-choice-${c}`)).toBeTruthy();
    expect(screen.getByTestId('cleanup-noise-picker').querySelectorAll('[data-testid^="cleanup-choice-"]')).toHaveLength(5);
  });

  it('free flow: estimate says Free, run, audition, Apply is one batch, Use original reverts it', async () => {
    const api = mockApi(FREE);
    let current = d;
    const batches: EditOp[][] = [];
    let rerender = () => {};
    const ui = () => (
      <MantineProvider>
        <CleanupPanel
          doc={current}
          selection={['a']}
          projectId="p1"
          readOnly={false}
          onApply={(batch) => {
            batches.push([...batch]);
            current = batch.reduce((acc, op) => apply(acc, op).doc, current);
            rerender();
          }}
          api={api}
          userId="u@x"
          orgId="o1"
        />
      </MantineProvider>
    );
    const view = render(ui());
    rerender = () => view.rerender(ui());
    fireEvent.click(screen.getByTestId('cleanup-choice-hum'));
    expect((await screen.findByTestId('cleanup-estimate')).textContent).toMatch(/free/i);
    fireEvent.click(screen.getByTestId('cleanup-run'));
    expect(await screen.findByTestId('cleanup-audition')).toBeTruthy();
    fireEvent.click(screen.getByTestId('cleanup-apply'));
    expect(batches).toHaveLength(1);
    expect(batches[0]).toEqual([{ type: 'setProperty', itemId: 'a', key: 'assetRef', value: { kind: 'projectFile', projectFileId: 'out-1' } }]);
    expect(await screen.findByTestId('cleanup-applied')).toBeTruthy();
    fireEvent.click(screen.getByTestId('cleanup-use-original'));
    await waitFor(() => expect(batches).toHaveLength(2));
    expect(batches[1]).toEqual([{ type: 'setProperty', itemId: 'a', key: 'assetRef', value: { kind: 'projectFile', projectFileId: '00000000-0000-4000-8000-000000000001' } }]);
    expect(await screen.findByTestId('cleanup-reverted')).toBeTruthy();
  });

  it('says nothing was changed when no hum was found', async () => {
    mount(mockApi(FREE, job({ applied: false, outputFileId: null })));
    fireEvent.click(screen.getByTestId('cleanup-choice-hum'));
    fireEvent.click(await screen.findByTestId('cleanup-run'));
    expect((await screen.findByTestId('cleanup-nothing')).textContent).toMatch(/no hum found, nothing changed/i);
  });

  it('AI flow: credits estimate, then the consent dialog before anything starts', async () => {
    const api = mockApi(PAID);
    mount(api);
    fireEvent.click(screen.getByTestId('cleanup-choice-studio'));
    expect((await screen.findByTestId('cleanup-estimate')).textContent).toMatch(/about 3 credits/i);
    fireEvent.click(screen.getByTestId('cleanup-run'));
    expect(await screen.findByTestId('cleanup-consent-accept')).toBeTruthy();
    expect(api.start).not.toHaveBeenCalled();
    fireEvent.click(screen.getByTestId('cleanup-consent-accept'));
    await waitFor(() => expect(api.start).toHaveBeenCalledWith('p1', 'file:00000000-0000-4000-8000-000000000001', expect.objectContaining({ allowSourceMediaEgress: true })));
  });

  it('shows the add-credits and plan refusals on the hosted start', async () => {
    const api = mockApi(PAID);
    api.start.mockRejectedValueOnce(Object.assign(new ApiError('no', 402), { body: { code: 'INSUFFICIENT_CREDITS', missing: 2 } }));
    mount(api);
    fireEvent.click(screen.getByTestId('cleanup-choice-studio'));
    fireEvent.click(await screen.findByTestId('cleanup-run'));
    fireEvent.click(await screen.findByTestId('cleanup-consent-accept'));
    expect(await screen.findByTestId('cleanup-add-credits')).toBeTruthy();
    fireEvent.click(screen.getByTestId('cleanup-try-again'));
    api.start.mockRejectedValueOnce(Object.assign(new ApiError('no', 402), { body: { code: 'PLAN_NOT_ENTITLED' } }));
    fireEvent.click(screen.getByTestId('cleanup-choice-studio'));
    fireEvent.click(await screen.findByTestId('cleanup-run'));
    expect(await screen.findByTestId('cleanup-see-plans')).toBeTruthy();
  });

  it('a read-only editor cannot start, and with no clip selected nothing is offered', () => {
    const api = mockApi(FREE);
    mount(api, { readOnly: true });
    expect((screen.getByTestId('cleanup-choice-hum') as HTMLButtonElement).disabled).toBe(true);
    cleanup();
    mount(api, { selection: [] });
    expect((screen.getByTestId('cleanup-choice-basic') as HTMLButtonElement).disabled).toBe(true);
    expect(api.estimate).not.toHaveBeenCalled();
  });
});

describe('CleanupPanel consent gate', () => {
  const d = doc([track('A1', 'audio', [media('a', 0, 100)])]);
  const mount = (api: CleanupJobApi, over: Partial<React.ComponentProps<typeof CleanupPanel>> = {}) => {
    render(
      <MantineProvider>
        <CleanupPanel doc={d} selection={['a']} projectId="p1" readOnly={false} onApply={vi.fn()} api={api} userId="u@x" orgId="o1" {...over} />
      </MantineProvider>,
    );
  };
  const acceptOnce = async (api: ReturnType<typeof mockApi>) => {
    fireEvent.click(screen.getByTestId('cleanup-choice-studio'));
    fireEvent.click(await screen.findByTestId('cleanup-run'));
    fireEvent.click(await screen.findByTestId('cleanup-consent-accept'));
    await waitFor(() => expect(api.start).toHaveBeenCalledTimes(1));
  };
  const expectAsks = async (api: ReturnType<typeof mockApi>) => {
    fireEvent.click(screen.getByTestId('cleanup-choice-studio'));
    fireEvent.click(await screen.findByTestId('cleanup-run'));
    expect(await screen.findByTestId('cleanup-consent-accept')).toBeTruthy();
    expect(api.start).not.toHaveBeenCalled();
  };

  beforeEach(() => {
    document.cookie = 'reelbolt_user=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
  });

  it('remembers consent for this user, org and project only; another project or org asks again', async () => {
    const first = mockApi(PAID);
    mount(first);
    await acceptOnce(first);
    cleanup();

    const same = mockApi(PAID);
    mount(same);
    fireEvent.click(screen.getByTestId('cleanup-choice-studio'));
    fireEvent.click(await screen.findByTestId('cleanup-run'));
    // No accept click: consent was already remembered for this exact scope.
    await waitFor(() => expect(same.start).toHaveBeenCalledTimes(1));
    cleanup();

    const otherProject = mockApi(PAID);
    mount(otherProject, { projectId: 'p2' });
    await expectAsks(otherProject);
    cleanup();

    const otherOrg = mockApi(PAID);
    mount(otherOrg, { orgId: 'o2' });
    await expectAsks(otherOrg);
  });

  it('with no user never stores consent: the dialog shows every time (no shared anonymous key)', async () => {
    const first = mockApi(PAID);
    mount(first, { userId: undefined });
    await acceptOnce(first);
    expect(window.localStorage.length).toBe(0);
    cleanup();

    const again = mockApi(PAID);
    mount(again, { userId: undefined });
    await expectAsks(again);
  });

  it('a hand-set record of the wrong shape does not count: the dialog still shows', async () => {
    const first = mockApi(PAID);
    mount(first);
    await acceptOnce(first);
    for (let i = 0; i < window.localStorage.length; i += 1) {
      const k = window.localStorage.key(i) as string;
      window.localStorage.setItem(k, 'yes');
    }
    cleanup();

    const api = mockApi(PAID);
    mount(api);
    await expectAsks(api);
  });
});
