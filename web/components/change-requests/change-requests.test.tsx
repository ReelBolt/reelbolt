// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import { StepResultPanel } from '@/components/workflows/StepResultPanel';
import type { WorkflowStepResult } from '@/lib/types/workflow';
import { RequestStepChangeModal } from './RequestStepChangeModal';
import { describeChangeRequestError as describeCr } from './change-request-errors';
import { buildChangeRequest as buildCr, emptyForm } from './change-request-form';
import { capability, changeRequest } from './change-request-fixtures';
import { createTranslator } from 'use-intl/core';
import { loadMessages } from '@/i18n/messages';

const tCr = createTranslator({ locale: 'en', messages: loadMessages('en'), namespace: 'changeRequests' }) as unknown as (k: string, v?: Record<string, string | number>) => string;
const buildChangeRequest = (cap: Parameters<typeof buildCr>[0], form: Parameters<typeof buildCr>[1]) => buildCr(cap, form, tCr);
const describeChangeRequestError = (e: unknown) => describeCr(e, tCr);


const push = vi.fn();
vi.mock('next/navigation', () => ({ useRouter: () => ({ push }) }));
vi.mock('next/link', () => ({ default: ({ children, href }: { children: React.ReactNode; href: string }) => <a href={href}>{children}</a> }));
const apiFetch = vi.fn();
vi.mock('@/lib/api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api/client')>();
  return { ...actual, apiFetch: (...a: unknown[]) => apiFetch(...a) };
});

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((q) => ({
      matches: false, media: q, onchange: null, addListener: vi.fn(), removeListener: vi.fn(),
      addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  Object.defineProperty(document, 'fonts', { configurable: true, value: { addEventListener: vi.fn(), removeEventListener: vi.fn() } });
});
beforeEach(() => { apiFetch.mockReset(); push.mockReset(); });
afterEach(cleanup);

const stepResult = { id: 'sr3', workflowStepId: 'ws', output: '', tokensUsed: 0, durationMs: 1, executedAt: '2026-10-25T10:00:00Z', status: 'Completed' } as WorkflowStepResult;

function panel(cap: ReturnType<typeof capability> | undefined, onRequest = vi.fn()) {
  render(
    <MantineProvider>
      <StepResultPanel
        projectId="p1" stepResult={stepResult} stepEvents={[]} liveTokenMetrics={{ inputTokens: 0, outputTokens: 0 }}
        artifactJson={null} artifactError={null} artifactLoading={false} changeCapability={cap} onRequestChanges={onRequest}
      />
    </MantineProvider>,
  );
  return onRequest;
}

describe('Request changes button', () => {
  it('shows for a supported step and passes the capability', () => {
    const cap = capability();
    const cb = panel(cap);
    fireEvent.click(screen.getByTestId('step-request-changes'));
    expect(cb).toHaveBeenCalledWith(cap);
  });
  it('is hidden for an unsupported step and when capabilities have not loaded', () => {
    panel(capability({ supported: false, notSupportedReason: 'not built yet' }));
    expect(screen.queryByTestId('step-request-changes')).toBeNull();
    cleanup();
    panel(undefined);
    expect(screen.queryByTestId('step-request-changes')).toBeNull();
  });
  it('labels the film step as a change to the video', () => {
    panel(capability({ route: 'composition-revision', items: [] }));
    expect(screen.getByTestId('step-request-changes').textContent).toBe('Request changes to the video');
  });
});

describe('buildChangeRequest', () => {
  const cap = capability();
  it('needs a chosen line and a real change', () => {
    expect(buildChangeRequest(cap, emptyForm()).request).toBeNull();
    const f = { ...emptyForm(), selected: ['vo1'] };
    expect(buildChangeRequest(cap, f).request).toBeNull();
  });
  it('does not accept a note alone', () => {
    const f = { ...emptyForm(), selected: ['vo1'], instruction: 'make it calmer' };
    expect(buildChangeRequest(cap, f).request).toBeNull();
    const all = { ...emptyForm(), scope: 'all' as const, instruction: 'x' };
    expect(buildChangeRequest(cap, all).request).toBeNull();
  });
  it('keeps the note beside a real change', () => {
    const f = { ...emptyForm(), selected: ['vo1'], instruction: 'calmer', drafts: { vo1: { text: '', voiceId: 'v', speed: null, pronunciation: [] } } };
    expect(buildChangeRequest(cap, f).request?.instruction).toBe('calmer');
  });
  it('refuses a speed outside the catalogue range', () => {
    const f = { ...emptyForm(), selected: ['vo1'], drafts: { vo1: { text: '', voiceId: '', speed: 3, pronunciation: [] } } };
    const r = buildChangeRequest(cap, f);
    expect(r.request).toBeNull();
    expect(r.errors[0]).toContain('between 0.5 and 2');
  });
  it('refuses a half-filled pronunciation entry and over-long text', () => {
    const f = { ...emptyForm(), selected: ['vo1'], drafts: { vo1: { text: 'x'.repeat(501), voiceId: '', speed: null, pronunciation: [{ term: 'SQL', spelling: '' }] } } };
    expect(buildChangeRequest(cap, f).errors.length).toBe(2);
  });
  it('builds per-line and all-lines requests, skipping unchanged text', () => {
    const f = { ...emptyForm(), selected: ['vo1'], drafts: { vo1: { text: 'Hello there.', voiceId: 'v', speed: 1.2, pronunciation: [] } } };
    expect(buildChangeRequest(cap, f).request).toEqual({ stepResultId: 'sr3', itemIds: ['vo1'], overrides: { vo1: { voiceId: 'v', speed: 1.2 } } });
    const all = { ...emptyForm(), scope: 'all' as const, all: { text: '', voiceId: '', speed: 0.8, pronunciation: [] } };
    expect(buildChangeRequest(cap, all).request).toEqual({ stepResultId: 'sr3', overrides: { '*': { speed: 0.8 } } });
  });
});

function modal() {
  return render(
    <MantineProvider>
      <RequestStepChangeModal projectId="p1" executionId="ex1" capability={capability()} opened onClose={vi.fn()} onCreated={vi.fn()} />
    </MantineProvider>,
  );
}

const preview = (over = {}) => ({
  estimate: { billingEnabled: true, lowCredits: 1, highCredits: 3, requiredCredits: 3, balanceCredits: 10, affordable: true, ...over },
  replay: { steps: [
    { stepOrder: 1, label: 'Script', disposition: 'Reuse', reason: '' },
    { stepOrder: 3, label: 'Narration', disposition: 'Revise', reason: '' },
  ] },
});

async function pickSpeed() {
  fireEvent.click(await screen.findByLabelText(/Hello there/));
  fireEvent.change(await screen.findByLabelText('Speed'), { target: { value: '1.5' } });
}

describe('RequestStepChangeModal', () => {
  it('shows the exact price of the redone part only when the API sends it', async () => {
    apiFetch.mockResolvedValue(preview({ targetExactCredits: 1.5, targetExactNote: 'Priced from the re-made lines.' }));
    modal();
    await pickSpeed();
    expect((await screen.findByTestId('change-request-exact', undefined, { timeout: 3000 })).textContent).toBe('This part: 1.50 credits');
  });

  it('shows no exact line when the API sends none', async () => {
    apiFetch.mockResolvedValue(preview());
    modal();
    await pickSpeed();
    await screen.findByTestId('change-request-estimate', undefined, { timeout: 3000 });
    expect(screen.queryByTestId('change-request-exact')).toBeNull();
  });

  it('shows the price, the redone and the reused list', async () => {
    apiFetch.mockResolvedValue(preview());
    modal();
    await pickSpeed();
    expect(await screen.findByTestId('change-request-estimate', undefined, { timeout: 3000 })).toBeTruthy();
    expect(screen.getByTestId('change-request-estimate').textContent).toContain('About 1.00 to 3.00');
    expect(screen.getByText('Will be redone')).toBeTruthy();
    expect(screen.getByText('Reused (free)')).toBeTruthy();
    expect(screen.queryByTestId('change-request-shortfall')).toBeNull();
  });
  it('shows the shortfall with a top-up link', async () => {
    apiFetch.mockResolvedValue(preview({ affordable: false }));
    modal();
    await pickSpeed();
    expect(await screen.findByTestId('change-request-shortfall', undefined, { timeout: 3000 })).toBeTruthy();
    expect(screen.getAllByText('Top up credits').length).toBeGreaterThan(0);
  });
  it('confirms, posts and goes to the new run', async () => {
    apiFetch.mockImplementation((url: string) => Promise.resolve(url.endsWith('/estimate') ? preview() : changeRequest({ executionId: 'ex9', workflowDefinitionId: 'wf9' })));
    modal();
    await pickSpeed();
    await screen.findByTestId('change-request-estimate', undefined, { timeout: 3000 });
    fireEvent.click(screen.getByText('Confirm and redo'));
    await waitFor(() => expect(push).toHaveBeenCalledWith('/projects/p1/workflows/wf9/executions/ex9'));
  });
  it('shows a 402 refusal from the create call', async () => {
    apiFetch.mockImplementation((url: string) => (url.endsWith('/estimate')
      ? Promise.resolve(preview())
      : Promise.reject(new ApiError('Not enough', 402, [], { code: 'INSUFFICIENT_CREDITS' }))));
    modal();
    await pickSpeed();
    await screen.findByTestId('change-request-estimate', undefined, { timeout: 3000 });
    fireEvent.click(screen.getByText('Confirm and redo'));
    expect((await screen.findByTestId('change-request-error')).textContent).toContain('Not enough');
  });
});

describe('RequestStepChangeModal refusals and races', () => {
  it('shows a 409 and a 429 from the create call', async () => {
    for (const [err, text] of [
      [new ApiError('', 409, [], { code: 'STEP_NOT_COMPLETED' }), 'Only a step that finished'],
      [new ApiError('Slow down', 429, [], { code: 'REVISION_RATE_LIMITED', retryAfterSeconds: 120 }), 'about 2 minutes'],
    ] as const) {
      apiFetch.mockReset();
      apiFetch.mockImplementation((url: string) => (url.endsWith('/estimate') ? Promise.resolve(preview()) : Promise.reject(err)));
      modal();
      await pickSpeed();
      await screen.findByTestId('change-request-estimate', undefined, { timeout: 3000 });
      fireEvent.click(screen.getByText('Confirm and redo'));
      expect((await screen.findByTestId('change-request-error')).textContent).toContain(text);
      cleanup();
    }
  });
  it('shows the first server message of a 400', async () => {
    apiFetch.mockRejectedValue(new ApiError('The change request is not valid.', 400, [], { errors: [{ path: 'overrides.vo1.speed', message: 'Speed is too fast.' }] }));
    modal();
    await pickSpeed();
    expect((await screen.findByTestId('change-request-error', undefined, { timeout: 3000 })).textContent).toContain('Speed is too fast.');
  });
  it('a slower earlier estimate does not overwrite a newer one', async () => {
    const resolvers: Array<(v: unknown) => void> = [];
    apiFetch.mockImplementation(() => new Promise((r) => { resolvers.push(r); }));
    modal();
    await pickSpeed();
    await waitFor(() => expect(resolvers.length).toBe(1), { timeout: 3000 });
    fireEvent.change(screen.getByLabelText('Speed'), { target: { value: '1.8' } });
    await waitFor(() => expect(resolvers.length).toBe(2), { timeout: 3000 });
    resolvers[1](preview({ lowCredits: 5, highCredits: 5 }));
    await screen.findByText(/About 5.00 credits/, undefined, { timeout: 3000 });
    resolvers[0](preview({ lowCredits: 1, highCredits: 1 }));
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.getByTestId('change-request-estimate').textContent).toContain('About 5.00 credits');
  });
});

describe('describeChangeRequestError', () => {
  it('maps the refusals', () => {
    expect(describeChangeRequestError(new ApiError('', 409, [], { code: 'RUN_NOT_FINISHED' })).title).toBe('This run has not finished yet');
    expect(describeChangeRequestError(new ApiError('', 409, [], { code: 'STEP_NOT_COMPLETED' })).title).toBe('This step did not finish');
    expect(describeChangeRequestError(new ApiError('', 409, [], { code: 'USE_COMPOSITION_REVISION' })).title).toBe('Change the video instead');
    expect(describeChangeRequestError(new ApiError('Slow down', 429, [], { code: 'REVISION_RATE_LIMITED', retryAfterSeconds: 120 })).message).toContain('about 2 minutes');
    const r = describeChangeRequestError(new ApiError('', 402, [], { code: 'INSUFFICIENT_CREDITS' }));
    expect(r.topUp).toBe(true);
    expect(describeChangeRequestError(new ApiError('', 403, [], { code: 'PLAN_NOT_ENTITLED' })).title).toBe('Not part of your plan');
    expect(describeChangeRequestError(new ApiError('', 403, [], { code: 'PLAN_NOT_ENTITLED' })).message).toMatch(/single steps/);
  });
});
