// @vitest-environment jsdom

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, cleanup, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import DecisionCalibrationPage from './page';
import { useDecisionCalibration } from '@/lib/hooks/use-decision-calibration';
import { summarizeAgents } from '@/lib/calibration-summary';
import type {
  DecisionCalibrationReport,
  DecisionCalibrationSite,
  ReliabilityBucket,
} from '@/lib/types/decision-calibration';

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  }
);
Element.prototype.scrollIntoView = vi.fn();

const openWithPrompt = vi.fn();
vi.mock('@/components/assistant/AssistantShellProvider', () => ({
  useAssistantShell: () => ({ openWithPrompt }),
}));
vi.mock('@/lib/hooks/use-decision-calibration', () => ({ useDecisionCalibration: vi.fn() }));
const mockHook = vi.mocked(useDecisionCalibration);
type HookResult = ReturnType<typeof useDecisionCalibration>;
const hookResult = (over: Partial<HookResult>) =>
  ({ data: undefined, error: undefined, isLoading: false, isValidating: false, ...over }) as unknown as HookResult;

const buckets = (): ReliabilityBucket[] =>
  Array.from({ length: 10 }, (_, i) => ({
    lowerBound: i / 10,
    count: i === 8 ? 6 : 0,
    predictedMean: i === 8 ? 0.85 : 0,
    observedAgreementRate: i === 8 ? 0.8 : 0,
    confidenceIntervalLow: i === 8 ? 0.4 : 0,
    confidenceIntervalHigh: i === 8 ? 0.95 : 0,
  }));

const sweep = () =>
  Array.from({ length: 11 }, (_, i) => ({
    acceptAt: Number((0.5 + i * 0.05).toFixed(2)),
    acceptedCount: 11 - i,
    acceptRate: (11 - i) / 11,
    acceptedAgreementRate: 0.9,
    acceptedWithOutcomeCount: 11 - i,
    tokensSaved: 100 * (11 - i),
  }));

const site = (name: string, question: string, verdict: 'NotEnoughData' | 'TryGate' | 'KeepShadow'): DecisionCalibrationSite => ({
  site: name,
  question,
  observationCount: 10,
  agreementCount: 8,
  agreementRate: 0.8,
  meanConfidence: 0.7,
  reliabilityBuckets: buckets(),
  escalationCount: 4,
  escalationRate: 0.4,
  thresholdSweep: sweep(),
  recommendation: { verdict, suggestedAcceptAt: verdict === 'TryGate' ? 0.9 : null, reason: `reason-${name}` },
});

const report = (over: Partial<DecisionCalibrationReport> = {}): DecisionCalibrationReport => ({
  sites: [site('Colorist.Look', 'Look', 'NotEnoughData'), site('Colorist.Strength', 'Strength', 'TryGate'), site('MusicSupervisor.Fit', 'Fit', 'KeepShadow')],
  providers: [],
  totals: { observationCount: 30, agreementRate: 0.8, escalationRate: 0.4, tokensSaved: 1234, tokensSavedIsProjected: true, acceptAt: 0.85, minMargin: 0.25 },
  sampleSizeWarning: null,
  ...over,
});

const renderPage = () =>
  render(
    <MantineProvider>
      <DecisionCalibrationPage />
    </MantineProvider>
  );

describe('DecisionCalibrationPage', () => {
  beforeEach(() => {
    global.matchMedia = ((q: string) => ({
      matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
      addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
    })) as unknown as typeof matchMedia;
    mockHook.mockReturnValue(hookResult({}));
  });
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it('shows a loader while loading and claims nothing', () => {
    mockHook.mockReturnValue(hookResult({ isLoading: true }));
    const { container } = renderPage();
    expect(container.querySelector('.mantine-Loader-root')).not.toBeNull();
    expect(screen.queryByText(/PROJECTED/)).toBeNull();
  });

  it('explains the zero-observation state instead of drawing a chart', () => {
    mockHook.mockReturnValue(hookResult({ data: report({ sites: [], totals: { ...report().totals, observationCount: 0 } }) }));
    renderPage();
    expect(screen.getByText('No decision observations recorded')).toBeTruthy();
    expect(screen.queryByRole('img', { name: /reliability diagram/i })).toBeNull();
  });

  it('opens with the plain-language explainer', () => {
    renderPage();
    expect(screen.getByText('How this page works')).toBeTruthy();
    expect(screen.getByText(/Shadow/, { selector: 'b' })).toBeTruthy();
  });

  it('renders one card per agent with the worst verdict and the projected label', () => {
    mockHook.mockReturnValue(hookResult({ data: report() }));
    renderPage();
    expect(screen.getByText('Colorist')).toBeTruthy();
    expect(screen.getByText('MusicSupervisor')).toBeTruthy();
    expect(screen.getByText('Not enough data')).toBeTruthy(); // Colorist: worst of NotEnoughData + TryGate
    expect(screen.getByText('Keep Shadow')).toBeTruthy();
    expect(screen.getByText('PROJECTED')).toBeTruthy();
  });

  it('draws both charts for the selected question', () => {
    mockHook.mockReturnValue(hookResult({ data: report() }));
    renderPage();
    expect(screen.getByRole('img', { name: /reliability diagram/i })).toBeTruthy();
    expect(screen.getByRole('img', { name: /threshold sweep/i })).toBeTruthy();
  });

  it('asks the assistant per agent and from the header', () => {
    mockHook.mockReturnValue(hookResult({ data: report() }));
    renderPage();
    const buttons = screen.getAllByRole('button', { name: /Ask assistant/i });
    expect(buttons.length).toBe(3); // header + 2 agents
    fireEvent.click(buttons[1]);
    expect(openWithPrompt).toHaveBeenCalledWith(expect.stringContaining('Should I enable Gate for Colorist?'));
  });

  it('passes the supplied warning through verbatim', () => {
    mockHook.mockReturnValue(hookResult({ data: report({ sampleSizeWarning: 'Only 5 rows.' }) }));
    renderPage();
    expect(screen.getByText('Only 5 rows.')).toBeTruthy();
  });
});

describe('summarizeAgents', () => {
  it('keeps the most cautious verdict and weights agreement by observations', () => {
    const [colorist] = summarizeAgents([site('Colorist.A', 'A', 'TryGate'), site('Colorist.B', 'B', 'KeepShadow')], 0.85);
    expect(colorist.verdict).toBe('KeepShadow');
    expect(colorist.observationCount).toBe(20);
    expect(colorist.agreementRate).toBeCloseTo(0.8);
    expect(colorist.tokensSaved).toBe(2 * 100 * 4); // 0.85 is sweep index 7, 4 accepted
  });
});
