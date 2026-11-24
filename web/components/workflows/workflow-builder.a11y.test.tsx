// @vitest-environment jsdom
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ReactFlowProvider } from '@xyflow/react';
import { ConditionalNode } from './nodes/ConditionalNode';
import { StepCard } from './StepCard';
import type { StepData } from './WorkflowStepList';

// Vitest runs without `globals`, so Testing Library's auto-cleanup never registers: unmount
// explicitly. Otherwise the Modal opened by the Enter/Space tests stays mounted past the test and
// Mantine's scroll-lock timer can fire after jsdom is torn down ("window is not defined").
afterEach(() => cleanup());

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
    label: 'Check score',
    agentDefinitionId: '',
    stepType: 'Conditional',
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

function renderNode(step: StepData, stepNumber = 3) {
  const onExpandChange = vi.fn();
  const onTogglePin = vi.fn();
  const onChange = vi.fn();
  const onRemove = vi.fn();
  const utils = render(
    <MantineProvider>
      <ReactFlowProvider>
        <ConditionalNode
          data={{
            step,
            stepNumber,
            allSteps: [],
            currentStepIndex: 0,
            expanded: false,
            pinned: false,
            onExpandChange,
            onTogglePin,
            onChange,
            onRemove,
          }}
        />
      </ReactFlowProvider>
    </MantineProvider>,
  );
  return { ...utils, onExpandChange, onTogglePin, onChange, onRemove };
}

describe('canvas node accessibility (RED — target behavior from interaction-spec.md §4, not yet implemented)', () => {
  it('exposes an accessible name via role="button" + aria-label "Step {n}, {StepType}: {label}"', () => {
    renderNode(makeStep({ label: 'Check score', stepType: 'Conditional' }), 3);
    const button = screen.getByRole('button', { name: 'Step 3, Conditional Branch: Check score' });
    expect(button).toBeTruthy();
  });

  it('outer node element is keyboard-focusable (tabIndex 0)', () => {
    const { container } = renderNode(makeStep({ label: 'Check score', stepType: 'Conditional' }), 3);
    // The outer node Card has role="button" and tabIndex={0}, making it keyboard-focusable
    const outerCard = container.querySelector('[role="button"]') as HTMLElement;
    expect(outerCard).toBeTruthy();
    expect(outerCard?.getAttribute('tabindex')).toBe('0');
  });

  it('pressing Enter on the node fires a click (target behavior — currently fails, no keydown handler exists)', () => {
    const { container } = renderNode(makeStep({ label: 'Check score', stepType: 'Conditional' }), 3);
    const clickSpy = vi.fn();
    container.addEventListener('click', clickSpy);
    const outerCard = container.querySelector('[role="button"]') as HTMLElement;
    expect(outerCard).toBeTruthy();
    fireEvent.keyDown(outerCard, { key: 'Enter', code: 'Enter' });
    expect(clickSpy).toHaveBeenCalled();
  });

  it('pressing Space on the node fires a click (target behavior — currently fails, no keydown handler exists)', () => {
    const { container } = renderNode(makeStep({ label: 'Check score', stepType: 'Conditional' }), 3);
    const clickSpy = vi.fn();
    container.addEventListener('click', clickSpy);
    const outerCard = container.querySelector('[role="button"]') as HTMLElement;
    expect(outerCard).toBeTruthy();
    fireEvent.keyDown(outerCard, { key: ' ', code: 'Space' });
    expect(clickSpy).toHaveBeenCalled();
  });
});

describe('StepCard accessibility (native button semantics — expected to already pass, kept as regression coverage)', () => {
  function renderCard(step: StepData, stepNumber = 2) {
    return render(
      <MantineProvider>
        <StepCard
          step={step}
          stepNumber={stepNumber}
          allSteps={[]}
          currentStepIndex={0}
          onChange={vi.fn()}
          onRemove={vi.fn()}
          onMoveUp={vi.fn()}
          onMoveDown={vi.fn()}
          canMoveUp
          canMoveDown
        />
      </MantineProvider>,
    );
  }

  it('Configure button has aria-label "Step {n}, {StepType}: {label}" matching the canvas node pattern', () => {
    renderCard(makeStep({ label: 'Check score', stepType: 'Conditional' }), 2);
    const button = screen.getByRole('button', { name: 'Step 2, Conditional Branch: Check score' });
    expect(button.tagName).toBe('BUTTON');
  });

  it('Configure button, move-up, move-down and delete controls are real natively-keyboard-operable buttons', () => {
    const { container } = renderCard(makeStep({ label: 'Check score', stepType: 'Conditional' }), 2);
    const card = container.querySelector('[data-variant="subtle"][size="lg"]')?.closest('[class*="Card"]') || container.querySelector('div');
    const allButtons = container.querySelectorAll('button');
    let foundMoveUp = false;
    let foundMoveDown = false;
    let foundDelete = false;
    let foundConfigure = false;

    allButtons.forEach((btn) => {
      const ariaLabel = btn.getAttribute('aria-label');
      if (ariaLabel === 'Move step up') foundMoveUp = true;
      if (ariaLabel === 'Move step down') foundMoveDown = true;
      if (ariaLabel === 'Delete step') foundDelete = true;
      if (ariaLabel?.includes('Step 2, Conditional Branch: Check score')) foundConfigure = true;
    });

    expect(foundMoveUp).toBe(true);
    expect(foundMoveDown).toBe(true);
    expect(foundDelete).toBe(true);
    expect(foundConfigure).toBe(true);
  });
});
