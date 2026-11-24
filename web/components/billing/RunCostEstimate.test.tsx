// @vitest-environment jsdom

/**
 * The pre-run estimate (B10c-UI). What matters here is not the arithmetic — that is B7's and is
 * tested on the API — but what the screen CLAIMS: a range, honestly labelled as measured or typical,
 * with nothing at all rendered on a deployment that does not bill.
 */
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { RunCostEstimate } from './RunCostEstimate';
import { RunWithInputsModal } from '@/components/workflow-apps/RunWithInputsModal';
import { useWorkflowCostEstimate } from '@/lib/hooks/use-billing';
import type { AppManifest } from '@/lib/types/workflow';
import type { WorkflowCostEstimate } from '@/lib/types/billing';

const briefEstimateMock = vi.hoisted(() => vi.fn());
vi.mock('@/lib/hooks/use-billing', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/hooks/use-billing')>();
  // No resolution cap: the plan's limits are RunStylePicker.test's business, not this file's.
  return { ...actual, useWorkflowCostEstimate: vi.fn(), useWorkflowCostEstimateWithBrief: briefEstimateMock, useOutputHeightCap: () => null };
});

// The confirm dialog's other two hooks; the estimate is what this file is about.
vi.mock('@/lib/hooks/use-styles', () => ({
  useStylePresets: () => ({ data: [] }),
  useEditingStyles: () => ({ data: [] }),
}));

const estimateMock = vi.mocked(useWorkflowCostEstimate);
type EstimateResult = ReturnType<typeof useWorkflowCostEstimate>;

const result = (over: Partial<EstimateResult>) =>
  ({ data: undefined, error: undefined, isLoading: false, mutate: vi.fn(), ...over }) as unknown as EstimateResult;

const estimate = (over: Partial<WorkflowCostEstimate> = {}): WorkflowCostEstimate => ({
  billingEnabled: true,
  hasHistory: true,
  lowCredits: 30,
  highCredits: 52,
  requiredCredits: 52,
  balanceCredits: 400,
  affordable: true,
  steps: [
    { stepOrder: 1, stepType: 'Agent', agentDefinitionId: null, label: 'Writes the script', lowCredits: 10, highCredits: 20, requiredCredits: 20, basis: 'History' },
    { stepOrder: 2, stepType: 'VideoCompile', agentDefinitionId: null, label: null, lowCredits: 20, highCredits: 32, requiredCredits: 32, basis: 'Default' },
  ],
  ...over,
});

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({
      matches: false, media: query, onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });
  // The brief is an autosizing Textarea, which listens for font loading; jsdom has no FontFaceSet.
  Object.defineProperty(document, 'fonts', {
    configurable: true,
    value: { addEventListener: vi.fn(), removeEventListener: vi.fn(), ready: Promise.resolve() },
  });
});

afterEach(() => {
  cleanup();
  estimateMock.mockReset();
});

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);

/** The same run as a newer backend describes it: a typical figure, the basis, and why. */
const rangedEstimate = (over: Partial<WorkflowCostEstimate> = {}): WorkflowCostEstimate =>
  estimate({
    typicalCredits: 40,
    fixedMaxCredits: 0,
    rangeBasis: 'PastRuns',
    pastRunCount: 6,
    spreadReasons: [],
    ...over,
  });

describe('RunCostEstimate (the user own daily video limit)', () => {
  it('shows the limit, what is used and what is left, and where to change it', () => {
    estimateMock.mockReturnValue(result({
      data: rangedEstimate({
        videoSpendLimit: { dailyLimitCredits: 300, alertThresholdCredits: null, usedTodayCredits: 120, remainingTodayCredits: 180, alertReached: false, limitReached: false },
      }),
    }));
    wrap(<RunCostEstimate />);

    expect(screen.getByTestId('run-cost-video-limit').textContent)
      .toBe('Your daily video limit: 120 of 300 credits used today, 180 left. Change it in Settings > Usage.');
  });

  it('says there is no limit when the user set none, and shows nothing for a workflow without video', () => {
    estimateMock.mockReturnValue(result({
      data: rangedEstimate({
        videoSpendLimit: { dailyLimitCredits: null, alertThresholdCredits: null, usedTodayCredits: 0, remainingTodayCredits: null, alertReached: false, limitReached: false },
      }),
    }));
    wrap(<RunCostEstimate />);
    expect(screen.getByTestId('run-cost-video-limit').textContent).toMatch(/No daily video limit set/);

    cleanup();
    estimateMock.mockReturnValue(result({ data: rangedEstimate() }));
    wrap(<RunCostEstimate />);
    expect(screen.queryByTestId('run-cost-video-limit')).toBeNull();
  });
});

describe('RunCostEstimate (typical cost and usual range)', () => {
  it('leads with the typical cost, the usual range and the past runs it comes from', () => {
    estimateMock.mockReturnValue(result({ data: rangedEstimate() }));
    wrap(<RunCostEstimate />);

    expect(screen.getByText('This run should cost ≈ 40 credits (usually 30–52)')).toBeTruthy();
    expect(screen.getByText('Based on 6 past runs of this workflow.')).toBeTruthy();
    expect(screen.queryByTestId('run-cost-fixed')).toBeNull();
    // The balance copy still speaks of what is held, not of the typical figure.
    expect(screen.getByText(/covers the 52 this run holds/)).toBeTruthy();
  });

  it('on a first run, says it is rough, states the video ceiling, and explains a wide range', () => {
    estimateMock.mockReturnValue(result({
      data: rangedEstimate({
        hasHistory: false,
        lowCredits: 12,
        highCredits: 30,
        typicalCredits: 18,
        fixedMaxCredits: 320,
        rangeBasis: 'FirstRun',
        pastRunCount: 0,
        spreadReasons: ['review_loop_repeats', 'video_generation_max_clips', 'video_generation_in_review_loop', 'unknown_code'],
      }),
    }));
    wrap(<RunCostEstimate />);

    expect(screen.getByText('This run should cost ≈ 18 credits (usually 12–30)')).toBeTruthy();
    expect(screen.getByTestId('run-cost-fixed').textContent).toBe(
      'Plus up to 320 credits for generated video, priced at the most clips it can order. It can be ordered again if the review asks for another pass.',
    );
    expect(screen.getByText('First run, so this is a rough estimate from typical figures for these steps.')).toBeTruthy();
    expect(screen.getByText('Why the range is this wide:')).toBeTruthy();
    const why = screen.getByTestId('run-cost-why');
    expect(why.querySelectorAll('li')).toHaveLength(1);
    expect(why.textContent).toContain('The review step can send the edit back for another pass.');
  });

  it('does not explain a range that is not wide', () => {
    estimateMock.mockReturnValue(result({ data: rangedEstimate({ spreadReasons: ['footage_length'] }) }));
    wrap(<RunCostEstimate />);

    expect(screen.queryByText('Why the range is this wide:')).toBeNull();
  });

  it('lists a fixed-price step as a ceiling and a looped step as one that may run again', () => {
    estimateMock.mockReturnValue(result({
      data: rangedEstimate({
        steps: [
          { stepOrder: 1, stepType: 'Agent', agentDefinitionId: null, label: 'Edits the cut', lowCredits: 10, highCredits: 20, requiredCredits: 20, basis: 'AgentTypeHistory', typicalCredits: 14, expectedRuns: 1.5, fixedMaxCredits: 0 },
          { stepOrder: 2, stepType: 'VideoGenerate', agentDefinitionId: null, label: null, lowCredits: 0, highCredits: 0, requiredCredits: 320, basis: 'Default', typicalCredits: 0, expectedRuns: 1, fixedMaxCredits: 320 },
        ],
      }),
    }));
    wrap(<RunCostEstimate />);

    fireEvent.click(screen.getByRole('button', { name: /What each step costs/ }));

    expect(screen.getByText('Edits the cut: ≈ 10–20 credits, may run again after review (from your own past runs of this kind of agent)')).toBeTruthy();
    expect(screen.getByText('VideoGenerate: up to 320 credits')).toBeTruthy();
  });
});

describe('RunCostEstimate (an older backend, without the typical figure)', () => {
  it('shows the range and says it is measured when the workspace has history', () => {
    estimateMock.mockReturnValue(result({ data: estimate() }));
    wrap(<RunCostEstimate />);

    expect(screen.getByText(/≈ 30–52 credits/)).toBeTruthy();
    expect(screen.getByText(/what runs in this workspace have actually cost/)).toBeTruthy();
    expect(screen.getByText(/400 credits available/)).toBeTruthy();
  });

  it('says the figures are typical, not measured, when there is no history to go on', () => {
    estimateMock.mockReturnValue(result({ data: estimate({ hasHistory: false }) }));
    wrap(<RunCostEstimate />);

    expect(screen.getByText(/no finished runs of its own to measure yet/)).toBeTruthy();
  });

  it('breaks the range down per step and labels where each figure came from', () => {
    estimateMock.mockReturnValue(result({ data: estimate() }));
    wrap(<RunCostEstimate />);

    fireEvent.click(screen.getByRole('button', { name: /What each step costs/ }));

    expect(screen.getByText('Writes the script: ≈ 10–20 credits (from your own past runs of this step)')).toBeTruthy();
    // A cold-start step is named as a default rather than dressed up as this workspace's own number.
    expect(screen.getByText('VideoCompile: ≈ 20–32 credits (a typical figure, not your own history)')).toBeTruthy();
  });

  it('renders nothing at all on a deployment without billing', () => {
    estimateMock.mockReturnValue(result({ data: estimate({ billingEnabled: false, balanceCredits: null }) }));
    wrap(<RunCostEstimate />);

    expect(screen.queryByTestId('run-cost-estimate')).toBeNull();
    expect(screen.queryByText(/credits/)).toBeNull();
  });

  it('renders nothing when the estimate could not be read', () => {
    estimateMock.mockReturnValue(result({ error: new Error('offline') }));
    wrap(<RunCostEstimate />);

    expect(screen.queryByTestId('run-cost-estimate')).toBeNull();
    expect(screen.queryByText(/credits/)).toBeNull();
  });

  it('does not ask for an estimate without both ids', () => {
    estimateMock.mockReturnValue(result({}));
    wrap(<RunCostEstimate />);

    expect(estimateMock).toHaveBeenCalledWith(undefined, undefined, undefined);
  });

  it('warns with the shortfall and the way to fix it when the balance cannot start the run', () => {
    estimateMock.mockReturnValue(result({ data: estimate({ balanceCredits: 12, affordable: false }) }));
    wrap(<RunCostEstimate />);

    expect(screen.getByText(/Not enough credits for this run/)).toBeTruthy();
    expect(screen.getByText(/52 credits up front/)).toBeTruthy();
    expect(screen.getByText(/40 short/)).toBeTruthy();
    expect(screen.getByRole('link', { name: /Top up credits/ }).getAttribute('href')).toBe('/settings/billing');
  });

  // QA-EF-1: the figure a run needs to start rounds UP, never to the nearest — 12.4 reads 13, not 12.4.
  it('rounds the up-front figure and the shortfall up when the balance cannot start the run', () => {
    estimateMock.mockReturnValue(
      result({ data: estimate({ requiredCredits: 12.4, highCredits: 12.4, balanceCredits: 4.314, affordable: false }) }),
    );
    wrap(<RunCostEstimate />);

    expect(screen.getByText(/Starting it needs 13 credits up front/)).toBeTruthy();
    // 12.4 - 4.314 = 8.086 short: shown as 9, never 8.09 or 8.
    expect(screen.getByText(/, 9 short/)).toBeTruthy();
  });

  it('says "1 credit" when the up-front figure is one', () => {
    estimateMock.mockReturnValue(
      result({ data: estimate({ requiredCredits: 0.6, highCredits: 0.6, balanceCredits: 0.2, affordable: false }) }),
    );
    wrap(<RunCostEstimate />);

    expect(screen.getByText(/Starting it needs 1 credit up front/)).toBeTruthy();
  });

  it('rounds the figure this run holds up when the balance covers it', () => {
    estimateMock.mockReturnValue(result({ data: estimate({ requiredCredits: 12.4, highCredits: 12.4, balanceCredits: 400 }) }));
    wrap(<RunCostEstimate />);

    expect(screen.getByText(/covers the 13 this run holds/)).toBeTruthy();
  });
});

describe('RunWithInputsModal (the estimate follows the typed inputs)', () => {
  it('prices the chosen inputs, not the default branch', async () => {
    const manifest: AppManifest = { version: 2, inputs: [{ kind: 'input', id: 'lite', type: 'toggle', label: 'Lite mode' }] };
    briefEstimateMock.mockReturnValue({ data: estimate({ lowCredits: 7, highCredits: 9, requiredCredits: 9 }) });
    wrap(<RunWithInputsModal projectId="p1" workflowId="w1" opened onClose={vi.fn()} onConfirm={vi.fn()} manifest={manifest} />);
    fireEvent.click(screen.getByRole('switch', { name: 'Lite mode' }));
    await waitFor(() => {
      const bodies = briefEstimateMock.mock.calls.map((c) => c[2]).filter(Boolean);
      expect(JSON.stringify(bodies[bodies.length - 1])).toContain('"inputs":{"lite":true}');
    });
    expect(screen.getByText(/≈ 7–9 credits/)).toBeTruthy();
  });
});

describe('RunWithInputsModal (the estimate before Execute)', () => {
  it('shows what the run should cost inside the confirmation, before it starts', () => {
    estimateMock.mockReturnValue(result({ data: estimate() }));
    const onConfirm = vi.fn();
    wrap(
      <RunWithInputsModal
        opened
        onClose={vi.fn()}
        onConfirm={onConfirm}
        workflowName="Reel"
        projectId="p1"
        workflowId="w1"
        stepSummaries={['Watches your footage']}
      />,
    );

    expect(screen.getByTestId('run-cost-estimate')).toBeTruthy();
    expect(screen.getByText(/≈ 30–52 credits/)).toBeTruthy();
    // The dialog still starts the run, and still passes exactly what it always did.
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));
    expect(onConfirm).toHaveBeenCalledWith({});
  });

  it('prices the run at the resolution picked, and at Auto when none is', () => {
    estimateMock.mockReturnValue(result({ data: estimate() }));
    wrap(<RunWithInputsModal projectId="p1" workflowId="w1" opened onClose={vi.fn()} onConfirm={vi.fn()} />);
    expect(estimateMock).toHaveBeenLastCalledWith('p1', 'w1', undefined);
    fireEvent.click(screen.getByText('4K'));
    expect(estimateMock).toHaveBeenLastCalledWith('p1', 'w1', '2160p');
  });

  it('shows no cost chrome at all when billing is off', () => {
    estimateMock.mockReturnValue(result({ data: estimate({ billingEnabled: false }) }));
    wrap(<RunWithInputsModal projectId="p1" workflowId="w1" opened onClose={vi.fn()} onConfirm={vi.fn()} />);

    expect(screen.queryByTestId('run-cost-estimate')).toBeNull();
    expect(screen.queryByText(/credits/)).toBeNull();
  });
});

describe('RunCostEstimate (may run out partway)', () => {
  const TITLE = 'You may run out of credits partway through this run.';
  // Typical 40 + up to 20 fixed-price = 60; the run needs 52 to start.
  const risky = (over: Partial<WorkflowCostEstimate> = {}) =>
    rangedEstimate({ fixedMaxCredits: 20, balanceCredits: 55, ...over });

  it('warns when the balance starts the run but is below typical plus the fixed-price part', () => {
    estimateMock.mockReturnValue(result({ data: risky() }));
    wrap(<RunCostEstimate />);

    const alert = screen.getByTestId('run-cost-may-run-out');
    expect(screen.getByText(TITLE)).toBeTruthy();
    expect(alert.textContent).toContain(
      'Runs often cost more than the amount needed to start, so top up first to avoid the run stopping halfway.',
    );
    expect(alert.querySelector('a')?.getAttribute('href')).toBe('/settings/billing');
    expect(alert.textContent).not.toMatch(/p25|hold|low edge/i);
  });

  it('says nothing at or above typical plus the fixed-price part', () => {
    estimateMock.mockReturnValue(result({ data: risky({ balanceCredits: 60 }) }));
    wrap(<RunCostEstimate />);
    expect(screen.queryByText(TITLE)).toBeNull();
    cleanup();

    estimateMock.mockReturnValue(result({ data: risky({ balanceCredits: 400 }) }));
    wrap(<RunCostEstimate />);
    expect(screen.queryByText(TITLE)).toBeNull();
  });

  it('leaves a run that cannot start to the shortfall message', () => {
    estimateMock.mockReturnValue(result({ data: risky({ balanceCredits: 30, affordable: false }) }));
    wrap(<RunCostEstimate />);
    expect(screen.getByText('Not enough credits for this run')).toBeTruthy();
    expect(screen.queryByText(TITLE)).toBeNull();
  });

  it('says nothing when billing is off or the balance is unknown', () => {
    estimateMock.mockReturnValue(result({ data: risky({ billingEnabled: false }) }));
    wrap(<RunCostEstimate />);
    expect(screen.queryByText(TITLE)).toBeNull();
    cleanup();

    estimateMock.mockReturnValue(result({ data: risky({ balanceCredits: null }) }));
    wrap(<RunCostEstimate />);
    expect(screen.queryByText(TITLE)).toBeNull();
    expect(screen.getByTestId('run-cost-estimate')).toBeTruthy();
  });

  it('adds the alert to the cost line once today\'s use reaches the alert level', () => {
    estimateMock.mockReturnValue(result({
      data: rangedEstimate({
        videoSpendLimit: { dailyLimitCredits: 300, alertThresholdCredits: 100, usedTodayCredits: 120, remainingTodayCredits: 180, alertReached: true, limitReached: false },
      }),
    }));
    wrap(<RunCostEstimate />);
    expect(screen.getByTestId('run-cost-video-limit').textContent).toMatch(/reached your alert level/);
  });
});

