// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Suspense } from 'react';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import type { WorkflowDefinition, WorkflowExecution } from '@/lib/types/workflow';
import type { ExecutionStreamEvent } from '@/lib/hooks/use-execution-stream';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

// The flow canvas is irrelevant here (and needs real layout); render each node's label inline.
vi.mock('@xyflow/react', async () => {
  const { useState } = await import('react');
  return {
    ReactFlow: ({ nodes, onNodeClick }: { nodes: { id: string; data: { label: React.ReactNode } }[]; onNodeClick?: (e: unknown, n: unknown) => void }) => (
      <div data-testid="flow">{nodes.map((n) => <div key={n.id} data-testid={`node-${n.id}`} onClick={(e) => onNodeClick?.(e, n)}>{n.data.label}</div>)}</div>
    ),
    ReactFlowProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
    Background: () => null,
    BackgroundVariant: { Dots: 'dots' },
    Controls: () => null,
    MarkerType: { ArrowClosed: 'arrowclosed' },
    useNodesState: (init: unknown[]) => { const [n, setN] = useState(init); return [n, setN, vi.fn()]; },
    useEdgesState: (init: unknown[]) => { const [e, setE] = useState(init); return [e, setE, vi.fn()]; },
  };
});
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.mock('next/navigation', () => ({ useRouter: () => ({ push: vi.fn() }) }));
vi.mock('next/link', () => ({ default: ({ children, href }: { children: React.ReactNode; href: string }) => <a href={href}>{children}</a> }));

const workflow: WorkflowDefinition = {
  id: 'wf', name: 'Narrated story', createdAt: '', updatedAt: '', requiresUserInput: false,
  steps: [
    { id: 's1', agentDefinitionId: 'a-va', stepOrder: 1, label: '', stepType: 'VideoAnalyze' },
    { id: 's2', agentDefinitionId: 'a-nw', stepOrder: 2, label: '', stepType: 'Agent' },
    { id: 's3', agentDefinitionId: 'a-vt', stepOrder: 3, label: '', stepType: 'VideoCompile' },
  ],
};
const result = (id: string, stepId: string, status: 'Completed' | 'Running', durationMs: number, executedAt: string, outputJson: string | null = null) => ({
  id, workflowStepId: stepId, output: '', tokensUsed: 1234, durationMs, executedAt, status, outputJson,
});
let execution: WorkflowExecution;
let events: ExecutionStreamEvent[] = [];

vi.mock('@/lib/api/executions', () => ({
  getExecution: vi.fn(async () => execution),
  getStepResult: vi.fn(async (_p: string, _e: string, id: string) => ({ ...execution.stepResults.find((r) => r.id === id)!, detailOmitted: false })),
  stopExecution: vi.fn(), retryExecution: vi.fn(), resumeExecution: vi.fn(),
}));
// `fetcher` is what the SWR-backed hooks call; the page now reads the workspace's runners (E8-UI)
// to name the machine a step ran on, so the client mock has to provide it too.
vi.mock('@/lib/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/api/client')>()),
  apiFetch: vi.fn(async () => workflow),
  fetcher: vi.fn(async () => []),
}));
const metrics = { totalEvents: 7, totalInputTokens: 1000, totalOutputTokens: 234, totalTokens: 1234, averageDurationMs: 0 };
vi.mock('@/lib/hooks/use-execution-stream', () => ({
  useExecutionStream: () => ({ events, connectionState: 'connected', lastError: null, metrics }),
}));
const artifactResult = { data: undefined, error: undefined, isLoading: false };
vi.mock('@/lib/hooks/use-step-result-artifact', () => ({ useStepResultArtifact: () => artifactResult }));
// SWR returns referentially stable data between renders; the mocks must too.
const agentsResult = {
  data: [
    { id: 'a-va', name: 'VideoTransform', agentType: 'VideoTransform', isBuiltIn: true },
    { id: 'a-nw', name: 'NarrationWriter', agentType: 'NarrationWriter', isBuiltIn: true },
    { id: 'a-vt', name: 'VideoTransform', agentType: 'VideoTransform', isBuiltIn: true },
  ],
};
const executionsResult = { data: [{ id: 'other', startedAt: '2026-10-03T09:00:00Z' }, { id: 'ex1', startedAt: '2026-10-04T14:02:00Z' }] };
const projectResult = { data: { id: 'p1', name: 'Launch reel' } };
// The run's credits come from the usage ledger; `undefined` is both "still loading" and "billing is
// not enabled on this deployment", and the tile must be absent in both cases rather than read 0.
let runCredits: number | undefined = 12.5;
vi.mock('@/lib/hooks/use-billing', () => ({ useExecutionCredits: () => runCredits }));
vi.mock('@/lib/hooks/use-agents', () => ({ useAgents: () => agentsResult }));
vi.mock('@/lib/hooks/use-executions', () => ({ useExecutions: () => executionsResult }));
vi.mock('@/lib/hooks/use-projects', () => ({ useProject: () => projectResult }));
// "Change this video" has its own tests (components/change-requests); here its data is set per test and never loads by default.
let revisionsResult: { data: RevisionHistory | undefined; error: unknown; mutate: ReturnType<typeof vi.fn> } = { data: undefined, error: undefined, mutate: vi.fn() };
vi.mock('@/lib/hooks/use-revisions', () => ({ useRevisions: () => revisionsResult }));
let changeCapsResult: { data: StepChangeCapabilities | undefined; error: unknown } = { data: undefined, error: undefined };
vi.mock('@/lib/hooks/use-change-requests', () => ({
  useChangeCapabilities: () => changeCapsResult,
  useChangeRequests: () => ({ data: undefined, error: undefined, mutate: vi.fn() }),
}));

// The first-video moment has its own tests (components/workflow-apps); here it is off unless a test turns it on.
let firstResultState = { active: false, dismiss: vi.fn(), failedFirst: false };
vi.mock('@/components/workflow-apps/use-first-result', () => ({ useFirstResult: () => firstResultState }));

const openWithPrompt = vi.fn();
vi.mock('@/components/assistant/AssistantShellProvider', () => ({ useOptionalAssistant: () => ({ openWithPrompt }) }));
import ExecutionDetailPage from './page';
import { history as revisionHistory } from '@/components/change-requests/revision-fixtures';
import type { StepChangeCapabilities } from '@/lib/types/change-requests';
import type { RevisionHistory } from '@/lib/types/revisions';
import { getExecution, getStepResult, retryExecution, resumeExecution } from '@/lib/api/executions';
import { ApiError, fetcher } from '@/lib/api/client';
import { enT } from '@/lib/i18n-default';
import { notifications } from '@mantine/notifications';

function setViewport(width: number) {
  global.matchMedia = ((q: string) => {
    const min = /min-width:\s*([\d.]+)em/.exec(q);
    return {
      matches: min ? width >= Number(min[1]) * 16 : false,
      media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
    };
  }) as unknown as typeof matchMedia;
}

async function renderPage() {
  const params = Promise.resolve({ id: 'p1', workflowId: 'wf', executionId: 'ex1' });
  await act(async () => {
    render(
      <SWRConfig value={{ provider: () => new Map() }}>
        <MantineProvider>
          <Suspense fallback={null}>
            <ExecutionDetailPage params={params} />
          </Suspense>
        </MantineProvider>
      </SWRConfig>,
    );
  });
  await act(async () => {});
}

async function showDiagram() {
  await act(async () => {
    fireEvent.click(screen.getByRole('radio', { name: 'Diagram' }));
  });
}

beforeEach(() => {
  setViewport(1280);
  events = [];
  runCredits = 12.5;
  execution = {
    id: 'ex1', workflowDefinitionId: 'wf', status: 'Running', startedAt: '2026-10-04T14:02:00Z', completedAt: null,
    iterationCount: 0, resultJson: null, reviewScores: [],
    stepResults: [
      result('r1', 's1', 'Completed', 120_000, '2026-10-04T14:02:00Z'),
      // Started a minute ago (real clock: faking Date freezes the render scheduler in jsdom).
      result('r2', 's2', 'Running', 0, new Date(Date.now() - 60_000).toISOString()),
    ],
  };
});
// jsdom has no FontFaceSet; Mantine's autosize Textarea listens on it.
Object.defineProperty(document, 'fonts', { configurable: true, value: { addEventListener: vi.fn(), removeEventListener: vi.fn() } });
afterEach(() => {
  cleanup();
  firstResultState = { active: false, dismiss: vi.fn(), failedFirst: false };
  revisionsResult = { data: undefined, error: undefined, mutate: vi.fn() };
  changeCapsResult = { data: undefined, error: undefined };
});

describe('Execution page', () => {
  it('names the run, says what is happening in plain language and estimates the time left', async () => {
    await renderPage();
    const summary = screen.getByTestId('run-summary');
    expect(screen.getAllByText(/^Run 2 · 4 Oct, \d\d:\d\d$/).length).toBeGreaterThan(0);
    expect(screen.queryByText(/Execution ex1/)).toBeNull();
    expect(within(summary).getByText('Writing the narration…')).toBeTruthy();
    expect(within(summary).getByText(/Writes the narration/)).toBeTruthy();
    // avg 2 min x 2 remaining steps, minus the 1 min the running step already spent → ~3 min.
    expect(within(summary).getByText('About 3 min left (estimate)')).toBeTruthy();
  });

  it('shows what the run was charged, and nothing at all when billing is unavailable', async () => {
    await renderPage();
    expect(within(screen.getByTestId('run-summary')).getByText('12.50')).toBeTruthy();

    cleanup();
    runCredits = undefined; // 404 from /billing/usage, i.e. billing is off on this deployment
    await renderPage();
    const stats = within(screen.getByTestId('run-summary')).getByTestId('run-stats');
    expect(stats.textContent).not.toContain('Credits');
  });

  it('shows what each finished step was charged beside its duration, and nothing for a step with no recorded charge', async () => {
    execution.stepResults = [
      { ...result('r1', 's1', 'Completed', 120_000, '2026-10-04T14:02:00Z'), creditsCharged: 3.25 },
      { ...result('r3', 's3', 'Completed', 1_000, '2026-10-04T14:05:00Z'), creditsCharged: null },
    ];
    await renderPage();
    await showDiagram();
    const badges = screen.getAllByTestId('step-credits-charged');
    expect(badges).toHaveLength(1);
    expect(badges[0].textContent).toBe('3.25 credits');
  });

  it('shows a failed step\'s failure code behind the Details disclosure only', async () => {
    execution.stepResults = [
      { ...result('r1', 's1', 'Completed', 120_000, '2026-10-04T14:02:00Z'), status: 'Failed' as const, failureCode: 'COMPOSITION_SOURCE_NOT_FOUND', failureClass: 'UserInput' },
    ];
    await renderPage();
    await showDiagram();
    expect(screen.queryByTestId('step-failure-code')).toBeNull();
    fireEvent.click(within(screen.getByTestId('run-summary')).getByRole('button', { name: 'Details' }));
    expect(screen.getAllByTestId('step-failure-code')[0].textContent).toBe('COMPOSITION_SOURCE_NOT_FOUND');
  });

  it('keeps telemetry behind the Details disclosure', async () => {
    await renderPage();
    const summary = screen.getByTestId('run-summary');
    expect(within(summary).queryByText(/Stream connected/i)).toBeNull();
    expect(within(summary).queryByText(/Tokens/)).toBeNull();
    expect(within(summary).queryByText(/ms\/step/)).toBeNull();
    expect(within(summary).queryByText(/Iter 0/)).toBeNull();
    expect(screen.queryByText(/1,234 tok/)).toBeNull();
    fireEvent.click(within(summary).getByRole('button', { name: 'Details' }));
    expect(within(summary).getByText(/Stream connected/i)).toBeTruthy();
    expect(within(summary).getByText(/Tokens in \/ out \/ total/)).toBeTruthy();
    expect(within(summary).getByText(/Iter 0/)).toBeTruthy();
  });

  it('shows run warnings (contract C1) prominently at the top', async () => {
    execution = {
      ...execution, status: 'Passed', completedAt: '2026-10-04T14:40:00Z',
      stepResults: [
        result('r1', 's1', 'Completed', 1000, '2026-10-04T14:02:00Z'),
        result('r3', 's3', 'Completed', 1000, '2026-10-04T14:30:00Z', JSON.stringify({
          status: 'completed',
          warnings: [{ code: 'narration_mostly_dropped', message: '7 of 8 narration lines could not be placed in the video.' }],
        })),
      ],
    };
    await renderPage();
    const alerts = screen.getAllByRole('alert');
    expect(alerts[0].textContent).toContain('Most of the narration is missing');
    expect(alerts[0].textContent).toContain('7 of 8 narration lines could not be placed in the video.');
    // The alert precedes the status card in document order.
    expect(alerts[0].compareDocumentPosition(screen.getByTestId('run-summary')) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(within(screen.getByTestId('run-summary')).getAllByText('Done')[0]).toBeTruthy();
  });

  it('shows an amber Needs attention badge and the server message for a passed run below the bar', async () => {
    execution = {
      ...execution, status: 'Passed', completedAt: '2026-10-04T14:40:00Z',
      needsAttention: { reason: 'review_below_threshold', stepOrder: 3, score: 6, minScore: 8, iterations: 3, message: 'The review stopped after 3 rounds at 6/10; the target was 8/10.' },
    };
    await renderPage();
    const alert = screen.getByTestId('needs-attention-alert');
    expect(alert.textContent).toContain('The review stopped after 3 rounds at 6/10; the target was 8/10.');
    expect(within(screen.getByTestId('run-summary')).getByText('Needs attention')).toBeTruthy();
    expect(screen.queryByText('Finished')).toBeNull();
  });

  it('shows friendly agent names and no raw payload buttons in the activity feed', async () => {
    events = [
      { id: 'e1', type: 'step.started', executionId: 'ex1', timestamp: '2026-10-04T14:19:00Z', payload: { stepId: 's2', agentName: 'NARRATIONWRITER' } },
      { id: 'e2', type: 'step.completed', executionId: 'ex1', timestamp: '2026-10-04T14:18:00Z', payload: { stepId: 's1', agentName: 'VIDEOTRANSFORM', durationMs: 120000, tokensUsed: 50 } },
      { id: 'e3', type: 'step.tool-called', executionId: 'ex1', timestamp: '2026-10-04T14:19:30Z', payload: { stepId: 's2', toolName: 'ReadProjectFile' } },
    ];
    await renderPage();
    expect(screen.queryByText('NARRATIONWRITER')).toBeNull();
    expect(screen.queryByText('VIDEOTRANSFORM')).toBeNull();
    expect(screen.getAllByText('Writes the narration').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Processes the video').length).toBeGreaterThan(0);
    expect(screen.queryByText(/Show raw payload/)).toBeNull();
    expect(screen.queryByText(/Tool: ReadProjectFile/)).toBeNull();
    expect(screen.queryByText('step.completed')).toBeNull();
  });

  it('at 375px keeps short stat labels in a two-column grid and never dims the summary card', async () => {
    setViewport(375);
    await renderPage();
    const summary = screen.getByTestId('run-summary');
    // Nothing on the card (or its ancestors) is left at a partial opacity by an entrance animation.
    for (let el: HTMLElement | null = summary; el; el = el.parentElement) {
      expect(el.style.opacity === '' || el.style.opacity === '1').toBe(true);
    }
    const stats = within(summary).getByTestId('run-stats');
    expect(stats.className).toMatch(/SimpleGrid/);
    const labels = Array.from(stats.children).map((tile) => tile.firstElementChild?.textContent ?? '');
    expect(labels).toEqual(['Steps done', 'Started', 'Elapsed', 'Credits', 'Time left']);
    for (const label of labels) {
      expect(label.length).toBeLessThanOrEqual(12);
      expect((stats.children[labels.indexOf(label)].firstElementChild as HTMLElement).style.whiteSpace).toBe('nowrap');
    }
  });

  it('shows only the brand/product versions the run was pinned to', async () => {
    const member = (versionId: string, versionNumber: number) => ({
      versionId, versionNumber, digest: 'a'.repeat(64), libraryVersionId: null,
    });
    execution = {
      ...execution,
      designSystemPin: { v: 1, design: member('d', 7), productMap: member('p', 3) },
    };
    await renderPage();
    expect(within(screen.getByTestId('run-summary')).getByText('Used brand v7 / product v3')).toBeTruthy();

    cleanup();
    execution = { ...execution, designSystemPin: { v: 1, design: member('d', 7) } };
    await renderPage();
    const onlyBrand = within(screen.getByTestId('run-summary'));
    expect(onlyBrand.getByText('Used brand v7')).toBeTruthy();
    expect(onlyBrand.queryByText(/\/ product/)).toBeNull();

    cleanup();
    execution = { ...execution, designSystemPin: null };
    await renderPage();
    expect(screen.queryByTestId('design-system-pin')).toBeNull();
  });

  it('shows the server\'s warning when the run was pinned to an empty or outdated design', async () => {
    execution = {
      ...execution,
      designSystemWarnings: ['This run used design version 3, which has no colours, fonts or other design tokens, so your brand was not applied.'],
    };
    await renderPage();
    const alert = screen.getAllByRole('alert').find((el) => el.textContent?.includes('brand was not applied'));
    expect(alert).toBeTruthy();
    expect(alert!.textContent).toContain('Your brand design may not have been used');
  });

  it('says the run is still stopping when Run again is refused', async () => {
    execution = { ...execution, status: 'Cancelled', completedAt: '2026-10-04T14:10:00Z' };
    vi.mocked(retryExecution).mockRejectedValueOnce(new Error('This run is still stopping. Try again in a moment.'));
    await renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Run again' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Run every step again' }));
    expect(await screen.findByText('This run is still stopping. Try again in a moment.')).toBeTruthy();
  });

  it('a Run again or Resume failing with a 502 shows the generic server sentence, not "Bad Gateway"', async () => {
    const sentence = enT('errors.generic.server');
    for (const err of [new ApiError('Bad Gateway', 502), new ApiError('<!DOCTYPE html><html><body>502 Bad Gateway</body></html>', 502)]) {
      execution = { ...execution, status: 'Failed', completedAt: '2026-10-04T14:10:00Z', canResume: true };
      vi.mocked(resumeExecution).mockRejectedValueOnce(err);
      await renderPage();
      fireEvent.click(screen.getByRole('button', { name: 'Resume' }));
      expect(await screen.findByText(sentence)).toBeTruthy();
      expect(screen.queryByText(/Bad Gateway|DOCTYPE/)).toBeNull();
      cleanup();
    }
    execution = { ...execution, status: 'Cancelled', completedAt: '2026-10-04T14:10:00Z', canResume: false };
    vi.mocked(retryExecution).mockRejectedValueOnce(new ApiError('Bad Gateway', 502));
    await renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Run again' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Run every step again' }));
    expect(await screen.findByText(sentence)).toBeTruthy();
    expect(screen.queryByText(/Bad Gateway/)).toBeNull();
  });

  it('shows why Run again was refused when the run is already active', async () => {
    execution = { ...execution, status: 'Cancelled', completedAt: '2026-10-04T14:10:00Z' };
    vi.mocked(retryExecution).mockRejectedValueOnce(new Error('This run is already running or queued.'));
    await renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Run again' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Run every step again' }));
    expect(await screen.findByText('This run is already running or queued.')).toBeTruthy();
  });

  it('offers Resume first when the run can continue, and Run again asks before replacing results', async () => {
    execution = { ...execution, status: 'Cancelled', completedAt: '2026-10-04T14:10:00Z', canResume: true };
    vi.mocked(resumeExecution).mockResolvedValueOnce(execution);
    await renderPage();
    expect(screen.getByRole('button', { name: 'Run again' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Resume' }));
    await act(async () => {});
    // No estimate figure on a first click: it is sent only after a 409 ESTIMATE_NOT_ACCEPTED.
    expect(resumeExecution).toHaveBeenCalledWith('p1', 'wf', 'ex1', undefined);

    vi.mocked(retryExecution).mockClear();
    fireEvent.click(screen.getByRole('button', { name: 'Run again' }));
    expect(await screen.findByText(/Results from this attempt are replaced/)).toBeTruthy();
    expect(retryExecution).not.toHaveBeenCalled();
  });

  it('a Resume refused with 409 ESTIMATE_NOT_ACCEPTED opens the estimate modal; Confirm resends it with the high figure', async () => {
    execution = { ...execution, status: 'Failed', completedAt: '2026-10-04T14:10:00Z', canResume: true };
    vi.mocked(resumeExecution).mockReset();
    vi.mocked(resumeExecution)
      .mockRejectedValueOnce(new ApiError('Accept an estimate of at least 231 credits to start this build.', 409, [], {
        code: 'ESTIMATE_NOT_ACCEPTED',
        message: 'Accept an estimate of at least 231 credits to start this build.',
        estimate: { low: 12, high: 231 },
        capCredits: 300,
      }))
      .mockResolvedValueOnce(execution);
    await renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Resume' }));

    expect(await screen.findByText(/between 12 and 231 credits/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }));

    await waitFor(() => expect(resumeExecution).toHaveBeenCalledTimes(2));
    expect(vi.mocked(resumeExecution).mock.calls[0]).toEqual(['p1', 'wf', 'ex1', undefined]);
    expect(vi.mocked(resumeExecution).mock.calls[1]).toEqual(['p1', 'wf', 'ex1', 231]);
  });

  it('shows only Run again for a run that cannot be resumed', async () => {
    execution = { ...execution, status: 'Failed', completedAt: '2026-10-04T14:10:00Z', canResume: false };
    await renderPage();
    expect(screen.queryByRole('button', { name: 'Resume' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Run again' })).toBeTruthy();
  });

  it('maps step results onto the run own steps, not the workflow current ones', async () => {
    execution = {
      ...execution,
      steps: [{ id: 'n1', agentDefinitionId: 'a-nw', stepOrder: 1, label: 'Snapshot step', stepType: 'Agent' }],
      stepResults: [result('r9', 'n1', 'Completed', 1000, '2026-10-04T14:02:00Z')],
    };
    await renderPage();
    expect(screen.getByText('Snapshot step')).toBeTruthy();
    expect(within(screen.getByTestId('run-summary')).getByText('1 / 1')).toBeTruthy();
  });

  describe('lighter run reads', () => {
    it('loads the run as a summary and reads no step in full until one is opened', async () => {
      await renderPage();
      await showDiagram();
      expect(getExecution).toHaveBeenCalledWith('p1', 'ex1', { detail: 'summary' });
      expect(getStepResult).not.toHaveBeenCalled();

      fireEvent.click(screen.getByTestId('node-s1'));
      await act(async () => {});
      expect(getStepResult).toHaveBeenCalledWith('p1', 'ex1', 'r1');
    });

    it('says the step could not be read and offers Try again when the full read fails', async () => {
      vi.spyOn(console, 'error').mockImplementation(() => {});
      const original = vi.mocked(getStepResult).getMockImplementation()!;
      vi.mocked(getStepResult).mockRejectedValue(new Error('boom'));
      await renderPage();
      await showDiagram();
      fireEvent.click(screen.getByTestId('node-s1'));
      await act(async () => {});

      const retry = await screen.findByRole('button', { name: 'Try again' }, { timeout: 4000 });
      vi.mocked(getStepResult).mockClear();
      vi.mocked(getStepResult).mockImplementation(original);
      fireEvent.click(retry);
      await act(async () => {});
      expect(getStepResult).toHaveBeenCalledWith('p1', 'ex1', 'r1');
      vi.mocked(console.error).mockRestore?.();
    });

    it('reads the summary exactly once when the tab becomes visible again', async () => {
      await renderPage();
      vi.mocked(getExecution).mockClear();
      Object.defineProperty(document, 'hidden', { configurable: true, get: () => false });
      await act(async () => { document.dispatchEvent(new Event('visibilitychange')); });
      await act(async () => { await new Promise((r) => setTimeout(r, 2200)); });
      expect(getExecution).toHaveBeenCalledTimes(1);
      expect(getExecution).toHaveBeenCalledWith('p1', 'ex1', { detail: 'summary' });
    });
  });

  describe('refused Resume and Run again', () => {
    const refusal = (status: number, body: Record<string, unknown>) => new ApiError(String(body.message ?? 'refused'), status, [], body);
    const creditsRefusal = () => refusal(402, { code: 'INSUFFICIENT_CREDITS', message: 'Not enough credits.', required: 10, balance: 2 });

    async function clickResume() {
      execution = { ...execution, status: 'Cancelled', completedAt: '2026-10-04T14:10:00Z', canResume: true };
      await renderPage();
      vi.mocked(getExecution).mockClear();
      vi.mocked(notifications.show).mockClear();
      fireEvent.click(screen.getByRole('button', { name: 'Resume' }));
      await act(async () => {});
    }
    async function clickRetry() {
      execution = { ...execution, status: 'Failed', completedAt: '2026-10-04T14:10:00Z', canResume: false };
      await renderPage();
      vi.mocked(getExecution).mockClear();
      vi.mocked(notifications.show).mockClear();
      fireEvent.click(screen.getByRole('button', { name: 'Run again' }));
      fireEvent.click(await screen.findByRole('button', { name: 'Run every step again' }));
      await act(async () => {});
    }

    beforeEach(() => { vi.spyOn(console, 'error').mockImplementation(() => {}); });
    afterEach(() => { vi.mocked(console.error).mockRestore?.(); });

    it('shows the not-enough-credits notification when Resume is refused with 402 INSUFFICIENT_CREDITS', async () => {
      vi.mocked(resumeExecution).mockRejectedValueOnce(creditsRefusal());
      await clickResume();
      expect(notifications.show).toHaveBeenCalledTimes(1);
      expect(notifications.show).toHaveBeenCalledWith(expect.objectContaining({ id: 'insufficient-credits', title: expect.anything() }));
      expect(getExecution).not.toHaveBeenCalled();
    });

    it('shows the not-enough-credits notification when Run again is refused with 402 INSUFFICIENT_CREDITS', async () => {
      vi.mocked(retryExecution).mockRejectedValueOnce(creditsRefusal());
      await clickRetry();
      expect(notifications.show).toHaveBeenCalledWith(expect.objectContaining({ id: 'insufficient-credits', title: expect.anything() }));
      expect(getExecution).not.toHaveBeenCalled();
    });

    it('shows the server message in a red notification when Resume is refused with 402 PLAN_NOT_ENTITLED', async () => {
      vi.mocked(resumeExecution).mockRejectedValueOnce(refusal(402, { code: 'PLAN_NOT_ENTITLED', message: 'Your plan does not include this.' }));
      await clickResume();
      expect(notifications.show).toHaveBeenCalledTimes(1);
      expect(notifications.show).toHaveBeenCalledWith({ title: 'Error', message: 'Your plan does not include this.', color: 'red' });
      expect(getExecution).not.toHaveBeenCalled();
    });

    it('shows the server message in a red notification when Run again is refused with 402 PLAN_NOT_ENTITLED', async () => {
      vi.mocked(retryExecution).mockRejectedValueOnce(refusal(402, { code: 'PLAN_NOT_ENTITLED', message: 'Your plan does not include this.' }));
      await clickRetry();
      expect(notifications.show).toHaveBeenCalledWith({ title: 'Error', message: 'Your plan does not include this.', color: 'red' });
      expect(getExecution).not.toHaveBeenCalled();
    });

    it.each([
      ['RESUME_UNAVAILABLE', 'This run cannot be continued. Run it again from the start instead.'],
      ['RUN_STILL_STOPPING', 'This run is still stopping. Try again in a moment.'],
    ])('says the plain message on the card when Resume is refused with 409 %s', async (code, message) => {
      vi.mocked(resumeExecution).mockRejectedValueOnce(refusal(409, { code, message: 'raw server text' }));
      await clickResume();
      expect(screen.getByText(message)).toBeTruthy();
      expect(screen.queryByText('raw server text')).toBeNull();
      expect(notifications.show).not.toHaveBeenCalled();
      expect(getExecution).not.toHaveBeenCalled();
    });

    it.each([
      ['RUN_NOT_RESUMABLE', 'This run cannot be continued. Run it again from the start instead.'],
      ['RUN_CHANGED', 'This run changed while you were acting on it. Reload the page and try again.'],
    ])('shows the server message for 409 %s and the fixed text when none is sent', async (code, fallback) => {
      vi.mocked(resumeExecution).mockRejectedValueOnce(refusal(409, { code, message: 'It is already queued again.' }));
      await clickResume();
      expect(screen.getByText('It is already queued again.')).toBeTruthy();
      expect(screen.queryByText(fallback)).toBeNull();
      cleanup();
      vi.mocked(resumeExecution).mockRejectedValueOnce(refusal(409, { code, message: '' }));
      await clickResume();
      expect(screen.getByText(fallback)).toBeTruthy();
    });

    it.each([
      ['RETRY_UNAVAILABLE', 'This run cannot be run again.'],
      ['RUN_STILL_STOPPING', 'This run is still stopping. Try again in a moment.'],
    ])('says the plain message on the card when Run again is refused with 409 %s', async (code, message) => {
      vi.mocked(retryExecution).mockRejectedValueOnce(refusal(409, { code, message: 'raw server text' }));
      await clickRetry();
      expect(screen.getByText(message)).toBeTruthy();
      expect(screen.queryByText('raw server text')).toBeNull();
      expect(notifications.show).not.toHaveBeenCalled();
      expect(getExecution).not.toHaveBeenCalled();
    });
  });
});

describe('Execution page: Change this video', () => {
  const filmCaps = (): StepChangeCapabilities => ({
    executionId: 'ex1', runFinished: true, maxTargets: 6,
    steps: [{
      stepOrder: 1, stepResultId: 'r1', stepType: 'VideoCompile', label: 'Video', supported: true, notSupportedReason: null,
      route: 'composition-revision', itemKind: 'None', items: [], overrides: [], combinable: true,
    }],
  });

  it('shows no change panel for a run with nothing to change', async () => {
    await renderPage();
    expect(screen.queryByTestId('change-video-panel')).toBeNull();
  });

  it('shows one panel for the video, and the film step\'s Request changes goes to its note field', async () => {
    revisionsResult = { data: revisionHistory(), error: undefined, mutate: vi.fn() };
    changeCapsResult = { data: filmCaps(), error: undefined };
    await renderPage();
    expect(screen.getAllByRole('region', { name: 'Change this video' })).toHaveLength(1);

    await showDiagram();
    fireEvent.click(screen.getByTestId('node-s1'));
    await act(async () => {});
    fireEvent.click(await screen.findByTestId('step-request-changes'));
    expect(document.activeElement).toBe(screen.getByLabelText('What should change?'));
  });
});

describe('Execution page: plain step list', () => {
  it('shows the list by default and labels an untaken step Skipped on a failed run', async () => {
    execution = { ...execution, status: 'Failed', errorMessage: 'Step 2 (X) failed after 1 attempts. Last error: boom' } as typeof execution;
    await renderPage();
    expect(screen.getByTestId('run-step-list')).toBeTruthy();
    expect(screen.queryByText('Waiting')).toBeNull();
    expect(screen.queryByText('Skipped')).toBeNull();
    expect(screen.queryByText('Time left')).toBeNull();
    const failure = screen.getByTestId('run-failure');
    expect(screen.getByTestId('run-failure').firstElementChild?.textContent ?? '').toBeTruthy();
    expect(within(failure).getByRole('button', { name: 'Details' }).getAttribute('aria-expanded')).toBe('false');
    expect(failure.textContent).toContain('Something went wrong while making your video.');
  });

  it('shows Waiting, not Skipped, for a step that has not run yet on a running run', async () => {
    await renderPage();
    expect(screen.queryByText('Skipped')).toBeNull();
    expect(screen.getAllByText('Waiting').length).toBeGreaterThan(0);
  });
});

describe('Execution page: the first result', () => {
  const OUTPUT = { stepResultId: 'sr1', workflowExecutionId: 'ex1', fileName: 'Story.mp4', producedAt: '2026-10-04T14:40:00Z', isFinal: true, durationSec: 47 };
  const withOutputs = () =>
    vi.mocked(fetcher).mockImplementation((async (key: string) => (key.endsWith('/outputs') ? [OUTPUT] : [])) as never);
  afterEach(() => { vi.mocked(fetcher).mockImplementation((async () => []) as never); });

  it('shows the one-time moment above the player of a first finished video, with the run\'s own length and three next steps', async () => {
    withOutputs();
    firstResultState = { active: true, dismiss: vi.fn(), failedFirst: false };
    execution = { ...execution, status: 'Passed', completedAt: '2026-10-04T14:40:00Z' };
    await renderPage();
    const moment = await screen.findByRole('region', { name: 'Your first video is ready' });
    const player = screen.getByTestId('run-result-player');
    expect(moment.compareDocumentPosition(player) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    // The page's own player is the video: the moment adds none.
    expect(moment.querySelector('video')).toBeNull();
    expect(within(moment).getByTestId('first-result-facts').textContent).toBe('Your video runs 0:47.');
    expect(within(moment).getByRole('link', { name: 'Download' }).getAttribute('href')).toContain('saveAs=Story.mp4');
    expect(within(moment).getByRole('button', { name: 'Change something' })).toBeTruthy();
    expect(within(moment).getByRole('link', { name: 'Try another recipe' }).getAttribute('href')).toBe('/projects/p1?tab=workflows');
    fireEvent.click(within(moment).getByRole('button', { name: 'Close' }));
    expect(firstResultState.dismiss).toHaveBeenCalledTimes(1);
  });

  it('"Change something" goes to this page\'s own way to change the video', async () => {
    withOutputs();
    firstResultState = { active: true, dismiss: vi.fn(), failedFirst: false };
    execution = { ...execution, status: 'Passed', completedAt: '2026-10-04T14:40:00Z' };
    revisionsResult = { data: revisionHistory(), error: undefined, mutate: vi.fn() };
    changeCapsResult = {
      data: {
        executionId: 'ex1', runFinished: true, maxTargets: 6,
        steps: [{ stepOrder: 1, stepResultId: 'r1', stepType: 'VideoCompile', label: 'Video', supported: true, notSupportedReason: null,
          route: 'composition-revision', itemKind: 'None', items: [], overrides: [], combinable: true }],
      },
      error: undefined,
    };
    await renderPage();
    const moment = await screen.findByRole('region', { name: 'Your first video is ready' });
    fireEvent.click(within(moment).getByRole('button', { name: 'Change something' }));
    expect(document.activeElement).toBe(screen.getByLabelText('What should change?'));
  });

  it('shows no moment for any other finished run', async () => {
    withOutputs();
    execution = { ...execution, status: 'Passed', completedAt: '2026-10-04T14:40:00Z' };
    await renderPage();
    expect(await screen.findByTestId('run-result-player')).toBeTruthy();
    expect(screen.queryByTestId('first-result')).toBeNull();
  });

  it('a first run that failed on our side says so kindly, with the refund; an ordinary failure is unchanged', async () => {
    const failed = {
      ...execution, status: 'Failed' as const, completedAt: '2026-10-04T14:10:00Z', errorMessage: 'Something broke',
      stepResults: [{ ...result('r1', 's1', 'Completed', 1000, '2026-10-04T14:02:00Z'), status: 'Failed' as const, failureCode: 'UNEXPECTED_ERROR', failureClass: 'PlatformInternal' }],
    };
    execution = failed as typeof execution;
    firstResultState = { active: false, dismiss: vi.fn(), failedFirst: true };
    await renderPage();
    const failure = screen.getByTestId('run-failure');
    expect(within(failure).getByTestId('first-run-failed').textContent).toBe('This was your first run, and it did not finish. Here is what happened.');
    expect(within(failure).getByTestId('first-run-failed-next').textContent).toContain('This step failed on our side, so the credits held for it are refunded.');
    cleanup();

    firstResultState = { active: false, dismiss: vi.fn(), failedFirst: false };
    await renderPage();
    expect(screen.queryByTestId('first-run-failed')).toBeNull();
    expect(screen.getByTestId('run-failure').textContent).not.toMatch(/refund/i);
  });
  it('a failed screen insert shows what to do next and what the run cost, on any run (not only the first)', async () => {
    const failed = {
      ...execution, status: 'Failed' as const, completedAt: '2026-10-04T14:10:00Z', errorMessage: 'Scanning your clip for the screen did not finish.',
      stepResults: [
        { ...result('r1', 's1', 'Completed', 1000, '2026-10-04T14:02:00Z'), creditsCharged: 0.326 },
        { ...result('r2', 's2', 'Completed', 1000, '2026-10-04T14:03:00Z'), status: 'Failed' as const, failureCode: 'INSERT_TRACKING_FAILED', failureClass: 'PlatformInternal', creditsCharged: 0 },
      ],
    };
    execution = failed as typeof execution;
    await renderPage();
    const failure = screen.getByTestId('run-failure');
    expect(within(failure).getByTestId('failed-run-credits').textContent).toBe('This run used 0.326 credits for the steps that finished (1 step); the step that failed charged nothing.');
    const next = within(failure).getByTestId('insert-next-step');
    expect(within(next).getByRole('link', { name: 'What clips work' }).getAttribute('href')).toContain('tracked-screen-insert#which-clip-works');
    fireEvent.click(within(next).getByRole('button', { name: 'Ask the assistant to put it in a corner instead' }));
    expect(openWithPrompt).toHaveBeenCalledWith(expect.stringContaining('corner'), expect.objectContaining({ projectId: 'p1', executionId: 'ex1' }));
    expect(within(next).getByRole('button', { name: 'Run again' })).toBeTruthy();
    cleanup();

    runCredits = undefined;
    await renderPage();
    expect(screen.queryByTestId('failed-run-credits')).toBeNull();
  });
});

describe('ExecutionDetailPage load failures', () => {
  it('500 and network failure: plain sentence, Retry loads again, way back', async () => {
    for (const err of [new ApiError('raw boom', 500), new TypeError('Failed to fetch')]) {
      vi.mocked(getExecution).mockRejectedValueOnce(err);
      await renderPage();
      expect(screen.getByTestId('failure-card')).toBeTruthy();
      expect(screen.getByRole('link', { name: 'Back to the app' }).getAttribute('href')).toBe('/projects/p1/workflows/wf');
      await act(async () => {
        fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
      });
      await act(async () => {});
      expect(screen.queryByTestId('failure-card')).toBeNull();
      cleanup();
    }
  });
  it('404 says not found and 403 says no permission; neither offers Retry', async () => {
    for (const status of [404, 403]) {
      vi.mocked(getExecution).mockRejectedValueOnce(new ApiError('x', status));
      await renderPage();
      expect(screen.getByTestId('failure-card').getAttribute('data-kind')).toBe(status === 404 ? 'notFound' : 'forbidden');
      expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
      expect(screen.getByRole('link', { name: 'Back to the app' })).toBeTruthy();
      cleanup();
    }
  });
});
