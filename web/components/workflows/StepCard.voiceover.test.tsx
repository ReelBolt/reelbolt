// @vitest-environment jsdom
'use client';

/**
 * T-071 / T-070 regression coverage — switching an existing step's type to Voiceover (or
 * VideoAnalyze) must fill in the built-in placeholder agent id when it is still empty, and must
 * NOT overwrite an id the step already has. Proven RED against the pre-T-070 StepCard.
 *
 * A NEW file rather than an edit to StepCard.test.tsx: that suite's StepTypeSelector mock only
 * offers Agent/Conditional, so the Voiceover branch of handleStepTypeChange is unreachable there.
 */

import { describe, it, expect, vi, beforeAll, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { StepCard } from './StepCard';
import type { StepData } from './WorkflowStepList';

// The default StepTypeSelector renders every type inside a Mantine Select, which is awkward to
// drive. This mock swaps in a plain <select> that exposes the two step types under test.
vi.mock('./StepTypeSelector', () => ({
  StepTypeSelector: ({ value, onChange }: { value: string; onChange: (value: string) => void }) => (
    <select data-testid="step-type-selector" value={value} onChange={(e) => onChange(e.target.value)}>
      <option>Agent</option>
      <option>Voiceover</option>
      <option>VideoAnalyze</option>
    </select>
  ),
}));

vi.mock('./StepTypeBadge', () => ({
  StepTypeBadge: ({ stepType }: { stepType: string }) => <div data-testid="step-badge">{stepType}</div>,
}));

vi.mock('./AgentPicker', () => ({
  AgentPicker: ({ value, onChange }: { value: string; onChange: (value: string) => void }) => (
    <input data-testid="agent-picker" value={value} onChange={(e) => onChange(e.target.value)} />
  ),
}));

vi.mock('./StepConfigSheet', () => ({ StepConfigSheet: () => null }));

vi.mock('@/lib/hooks/use-agents', () => ({
  useAgents: () => ({
    data: [
      { id: 'vt-agent', agentType: 'VideoTransform' },
      { id: 'et-agent', agentType: 'ExtractTransform' },
    ],
    isLoading: false,
  }),
}));

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({
      matches: false,
      media: query,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  });
  // A `function`, not an arrow: vitest 4+ refuses `new` on an arrow-function mock.
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });
});

function makeStep(overrides: Partial<StepData> = {}): StepData {
  return {
    id: 's1',
    label: 'Narrate',
    agentDefinitionId: '',
    stepType: 'Agent',
    conditionExpression: null,
    loopSourceExpression: null,
    loopTargetStepOrder: null,
    maxIterations: 3,
    minScore: null,
    inputMappingJson: null,
    agentInputContextMode: null,
    cacheMode: null,
    selectedPriorStepOrders: [],
    trueBranchStepOrder: null,
    falseBranchStepOrder: null,
    parallelAgentIds: [],
    extractConfig: null,
    videoAnalyzeConfig: null,
    videoCompileConfig: null,
    thumbnailGenerateConfig: null,
    videoGenerateConfig: null,
    voiceoverConfig: null,
    ...overrides,
  } as StepData;
}

function renderCard(step: StepData, onChange: (updates: Partial<StepData>) => void) {
  return render(
    <MantineProvider>
      <StepCard
        step={step}
        stepNumber={1}
        allSteps={[step]}
        currentStepIndex={0}
        onChange={onChange}
        onRemove={vi.fn()}
        onMoveUp={vi.fn()}
        onMoveDown={vi.fn()}
        canMoveUp={false}
        canMoveDown={false}
      />
    </MantineProvider>,
  );
}

/** Drives the mocked type selector and returns the single `onChange` patch it produced. */
function switchTypeTo(stepType: 'Voiceover' | 'VideoAnalyze'): { patch: Partial<StepData>; onChange: ReturnType<typeof vi.fn> } {
  const onChange = vi.fn();
  renderCard(makeStep({ agentDefinitionId: '' }), onChange);

  fireEvent.change(screen.getByTestId('step-type-selector'), { target: { value: stepType } });

  expect(onChange).toHaveBeenCalledTimes(1);
  return { patch: onChange.mock.calls[0][0] as Partial<StepData>, onChange };
}

describe('StepCard — switching a step type to Voiceover assigns the placeholder agent', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    cleanup();
  });

  it('assigns the VideoTransform placeholder agent when an agent-less step is switched to Voiceover', () => {
    const { patch } = switchTypeTo('Voiceover');

    // Pre-T-070 the patch was `{ stepType: 'Voiceover' }` alone, so the save guard rejected the
    // workflow with "Step 1 requires an agent".
    expect(patch).toEqual({ stepType: 'Voiceover', agentDefinitionId: 'vt-agent' });
  });

  it('still assigns the VideoTransform placeholder agent when switched to VideoAnalyze (sibling control)', () => {
    const { patch } = switchTypeTo('VideoAnalyze');

    expect(patch).toEqual({ stepType: 'VideoAnalyze', agentDefinitionId: 'vt-agent' });
  });

  it('does not overwrite an agentDefinitionId the step already has', () => {
    const onChange = vi.fn();
    renderCard(makeStep({ stepType: 'Agent', agentDefinitionId: 'existing-agent' }), onChange);

    fireEvent.change(screen.getByTestId('step-type-selector'), { target: { value: 'Voiceover' } });

    // The handler's `step.agentDefinitionId === ''` guard: a step that already points at a real
    // agent keeps it, so switching type never silently re-points a configured step.
    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0][0]).toEqual({ stepType: 'Voiceover' });
  });
});
