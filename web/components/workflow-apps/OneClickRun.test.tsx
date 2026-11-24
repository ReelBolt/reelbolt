// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { AppManifest } from '@/lib/types/workflow';

const push = vi.hoisted(() => vi.fn());
vi.mock('next/navigation', () => ({ useRouter: () => ({ push, replace: vi.fn() }) }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
const execute = vi.hoisted(() => vi.fn());
const lint = vi.hoisted(() => vi.fn());
vi.mock('@/lib/api/workflows', () => ({ executeWorkflow: execute, lintWorkflow: lint }));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ user: { email: 'a@b.c' } }) }));
const gate = vi.hoisted(() => ({ resend: null as null | ((n: number) => unknown), refuse: false }));
vi.mock('@/components/design-system/EstimateGateModal', () => ({
  useEstimateGate: () => ({
    modal: null,
    intercept: (_e: unknown, resend: (n: number) => unknown) => {
      if (!gate.refuse) return false;
      gate.resend = resend;
      return true;
    },
  }),
}));
const emit = vi.hoisted(() => vi.fn());
vi.mock('@/lib/telemetry', () => ({ emitFunnelEvent: emit, userKeyFromEmail: () => 'k' }));
vi.mock('@/lib/hooks/use-styles', () => ({ useStylePresets: () => ({ data: [{ key: 'punchy' }], error: undefined }) }));
vi.mock('@/components/billing/insufficientCreditsNotification', () => ({ notifyInsufficientCredits: () => false }));

const state = vi.hoisted(() => ({
  manifest: null as unknown as AppManifest,
  requiresUserInput: false,
  files: [] as unknown[],
  estimate: null as unknown,
  lastRun: null as unknown,
}));
vi.mock('@/lib/hooks/use-workflow-apps', () => ({
  useWorkflowApps: () => ({ data: { apps: [{ workflowId: 'w1', source: 'edit-my-video', lastRun: state.lastRun }], recipes: [] } }),
  useWorkflowApp: () => ({ data: { name: 'Edit my video', costClass: 'Some', manifest: state.manifest, mediaEgress: null } }),
}));
vi.mock('@/lib/hooks/use-workflows', () => ({ useWorkflow: () => ({ data: { requiresUserInput: state.requiresUserInput } }) }));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: state.files }) }));
vi.mock('@/lib/hooks/use-billing', () => ({ useWorkflowCostEstimate: () => ({ data: state.estimate, error: undefined }) }));
vi.mock('swr', () => ({ default: (key: unknown) => ({ data: key ? { findings: [] } : undefined, error: undefined }) }));

import { OneClickRun } from './OneClickRun';

const clip = (id: string) => ({ id, originalFileName: `${id}.mp4`, mimeType: 'video/mp4', category: 'userFiles' });
const est = (over: object = {}) => ({ billingEnabled: true, affordable: true, lowCredits: 30, highCredits: 48, typicalCredits: 42, requiredCredits: 48, balanceCredits: 120, ...over });

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  state.manifest = { version: 1, title: 'Edit my video', suggestedStylePreset: 'punchy', inputs: [{ kind: 'files', role: 'video' }] };
  state.requiresUserInput = false;
  state.files = [clip('a')];
  state.estimate = est();
  state.lastRun = null;
  push.mockReset();
  emit.mockReset();
  gate.refuse = false;
  gate.resend = null;
  execute.mockReset();
});
afterEach(cleanup);

const mount = () => render(<MantineProvider><OneClickRun projectId="p1" workflowId="w1" /></MantineProvider>);

describe('OneClickRun', () => {
  it('shows one Run it button with the rounded estimate and balance in one line', () => {
    mount();
    expect(screen.getByRole('button', { name: 'Run it' })).toBeTruthy();
    expect(screen.getByTestId('one-click-price').textContent).toBe('About 40 credits, you have 120');
  });

  it('does not run until clicked, then runs with the footage filled and opens the run page', async () => {
    execute.mockResolvedValue({ id: 'e1' });
    mount();
    expect(execute).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Run it' }));
    await waitFor(() => expect(push).toHaveBeenCalledWith('/projects/p1/workflows/w1'));
    expect(execute).toHaveBeenCalledWith('p1', 'w1', null, false, {}, undefined, undefined, { sourceFiles: [{ role: 'Video', fileIds: ['a'] }], stylePreset: 'punchy' });
  });

  it('becomes Review and run, a link to the run page, when notes are required', () => {
    state.requiresUserInput = true;
    state.manifest = { version: 1, inputs: [{ kind: 'files', role: 'video' }, { kind: 'notes' }] };
    mount();
    expect(screen.queryByRole('button', { name: 'Run it' })).toBeNull();
    expect(screen.getByRole('link', { name: 'Review and run' }).getAttribute('href')).toBe('/projects/p1/workflows/w1');
    expect(screen.getByTestId('one-click-reason').textContent).toMatch(/short description/);
    expect(execute).not.toHaveBeenCalled();
  });

  it('becomes Review and run on a shortfall', () => {
    state.estimate = est({ affordable: false, balanceCredits: 3 });
    mount();
    expect(screen.getByRole('link', { name: 'Review and run' })).toBeTruthy();
    expect(screen.getByTestId('one-click-reason').textContent).toMatch(/not have enough credits/);
  });

  it('becomes Review and run, saying the most it can cost, when the run page would ask first', () => {
    state.estimate = est({ highCredits: 54 });
    mount();
    expect(screen.queryByRole('button', { name: 'Run it' })).toBeNull();
    expect(screen.getByRole('link', { name: 'Review and run' })).toBeTruthy();
    expect(screen.getByTestId('one-click-reason').textContent).toBe('This run can cost up to 60 credits, so check it first.');
  });

  it('is gone once the project has run an app, so no second paid run is offered', () => {
    state.lastRun = { executionId: 'e0', status: 'Passed' };
    mount();
    expect(screen.queryByTestId('one-click-run')).toBeNull();
  });

  it('shows no reason while the price is still pending, only the checking line', () => {
    state.estimate = undefined;
    mount();
    expect(screen.queryByTestId('one-click-reason')).toBeNull();
    expect(screen.getByTestId('one-click-price').textContent).toBe('Checking the price');
  });

  it('disappears after a successful start', async () => {
    execute.mockResolvedValue({ id: 'e1' });
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Run it' }));
    await waitFor(() => expect(screen.queryByTestId('one-click-run')).toBeNull());
  });

  it('fires run_clicked on the Run it click, and run_blocked with a token only when Review and run is pressed', async () => {
    execute.mockResolvedValue({ id: 'e1' });
    mount();
    expect(emit).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Run it' }));
    await waitFor(() => expect(push).toHaveBeenCalled());
    expect(emit).toHaveBeenCalledWith('alpha.run_clicked', 'k');
    cleanup();
    emit.mockReset();
    state.estimate = est({ affordable: false });
    mount();
    expect(emit).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('link', { name: 'Review and run' }));
    expect(emit).toHaveBeenCalledWith('alpha.run_blocked', 'k', 'credits');
  });

  it('counts one run_clicked for one click, not again on the estimate-gate resend', async () => {
    gate.refuse = true;
    execute.mockRejectedValueOnce(new Error('gate')).mockResolvedValue({ id: 'e1' });
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Run it' }));
    await waitFor(() => expect(gate.resend).not.toBeNull());
    gate.refuse = false;
    await gate.resend?.(60);
    await waitFor(() => expect(push).toHaveBeenCalled());
    expect(emit.mock.calls.filter((c) => c[0] === 'alpha.run_clicked')).toHaveLength(1);
    expect(execute).toHaveBeenLastCalledWith('p1', 'w1', null, false, {}, 60, undefined, expect.anything());
  });
});
