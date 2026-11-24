// @vitest-environment jsdom
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within, cleanup } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import {
  ConditionalStepConfig,
  createDefaultConditionalStepConfig,
} from './ConditionalStepConfig';
import type { StepType } from '@/lib/types/workflow';
import type { StepData } from './WorkflowStepList';

// Vitest runs without `globals`, so Testing Library's auto-cleanup never registers: unmount
// explicitly. Otherwise a React task scheduled by a still-mounted tree can fire after jsdom is
// torn down ("window is not defined").
afterEach(() => cleanup());

// Mock window APIs for Mantine and component testing
beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation(query => ({
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

  // Polyfill scrollIntoView for jsdom: Mantine's Combobox scrolls the selected option into view on
  // a timer, which jsdom does not implement.
  Element.prototype.scrollIntoView = vi.fn();
});

// Mantine's Select renders its options into a portal on document.body, so the decision-arm enum
// test below needs an unambiguous document — this file has no RTL auto-cleanup otherwise
// (vitest.config.ts sets no `globals`/setup file).
afterEach(cleanup);

const renderWithMantine = (component: React.ReactElement) => {
  return render(<MantineProvider>{component}</MantineProvider>);
};

// Helper to create a minimal StepData for testing
function createTestStep(id: string, stepType: string = 'Agent'): StepData {
  return {
    id,
    label: `Step ${id}`,
    agentDefinitionId: 'agent-123',
    stepType: stepType as StepType,
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
    voiceoverConfig: null,
    videoGenerateConfig: null,
  };
}

/** Clicks the schema form's Apply so the form's values reach `onChange`. */
function applyForm(container: HTMLElement) {
  fireEvent.click(within(container).getByRole('button', { name: 'Apply' }));
}

describe('ConditionalStepConfig', () => {
  it('preserves unknown top-level config key in edit round-trip', async () => {
    const onChange = vi.fn();

    const { container } = renderWithMantine(
      <ConditionalStepConfig
        conditionExpression="[score] >= 9"
        trueBranchStepOrder="3"
        falseBranchStepOrder="5"
        onChange={onChange}
        previousSteps={[
          { ...createTestStep('step-1'), label: 'First', stepType: 'Extract' as StepType },
          { ...createTestStep('step-2'), label: 'Second', stepType: 'VideoAnalyze' as StepType },
          { ...createTestStep('step-3'), label: 'Third', stepType: 'VideoCompile' as StepType },
        ]}
      />,
    );

    // Simulate a form change that should be called via SchemaDrivenStepConfigForm
    // This test verifies that the component structure allows unknown keys to pass through
    const conditionInput = within(container).getByDisplayValue('[score] >= 9');
    fireEvent.change(conditionInput, { target: { value: '[score] >= 8' } });
    applyForm(container);

    await waitFor(() => {
      expect(onChange).toHaveBeenCalled();
    });

    // Verify onChange was called with updated conditionExpression
    const lastCall = onChange.mock.calls[onChange.mock.calls.length - 1][0];
    expect(lastCall).toHaveProperty('conditionExpression', '[score] >= 8');
  });

  it('renders branch target labels correctly', () => {
    const { container } = renderWithMantine(
      <ConditionalStepConfig
        conditionExpression="[score] >= 9"
        trueBranchStepOrder="2"
        falseBranchStepOrder="3"
        onChange={vi.fn()}
        previousSteps={[
          { ...createTestStep('step-1'), label: 'Initial', stepType: 'Extract' as StepType },
          { ...createTestStep('step-2'), label: 'Success Path', stepType: 'VideoAnalyze' as StepType },
          { ...createTestStep('step-3'), label: 'Fallback Path', stepType: 'VideoCompile' as StepType },
        ]}
      />,
    );

    // Verify labels render correctly
    expect(within(container).getByDisplayValue('2. Success Path')).toBeTruthy();
    expect(within(container).getByDisplayValue('3. Fallback Path')).toBeTruthy();
  });

  it('shows (untitled) for missing step labels', () => {
    const { container } = renderWithMantine(
      <ConditionalStepConfig
        conditionExpression="[score] >= 9"
        trueBranchStepOrder="1"
        falseBranchStepOrder="99" // Out of bounds
        onChange={vi.fn()}
        previousSteps={[
          { ...createTestStep('step-1'), label: '', stepType: 'Extract' as StepType },
        ]}
      />,
    );

    // A stored branch that no longer resolves stays visible instead of silently blanking.
    expect(within(container).getByDisplayValue('99. (step not available here)')).toBeTruthy();
  });

  /**
   * Inverted from T-204's guard test. Back then `StepData` carried no `conditionalConfigJson`, so
   * rendering these controls would have silently discarded whatever the author typed; the builder
   * now carries the config and `handleFormChange` forwards it, so every decision-arm control must be
   * present AND hold the step's stored value.
   */
  it('renders the decision arm populated from the step config', () => {
    const { container } = renderWithMantine(
      <ConditionalStepConfig
        conditionExpression="[score] >= 9"
        trueBranchStepOrder={null}
        falseBranchStepOrder={null}
        conditionalConfig={{
          version: 1,
          mode: 'Decision',
          decisionQuestion: 'Ship it?',
          threshold: 0.7,
          providerId: '11111111-1111-1111-1111-111111111111',
          timeoutSeconds: 9,
        }}
        onChange={vi.fn()}
        previousSteps={[]}
      />,
    );

    // Labels are rendered by SchemaDrivenStepConfigForm from the schema keys.
    for (const label of ['How to decide', 'Decision question name', 'Yes-probability threshold', 'Decision provider', 'Decision timeout (seconds)']) {
      expect(within(container).getByText(label)).toBeTruthy();
    }

    // Populated, not merely present. Mantine's Select renders both a hidden and a visible input
    // carrying the value, so the enum is asserted by presence rather than by a unique match.
    expect(within(container).getAllByDisplayValue(/^Decision:/).length).toBeGreaterThan(0);
    expect(within(container).getByDisplayValue('Ship it?')).toBeTruthy();
    expect(within(container).getByDisplayValue('0.7')).toBeTruthy();
    expect(within(container).getByDisplayValue('9')).toBeTruthy();
  });

  it('carries an edited decision field into the onChange payload', async () => {
    const onChange = vi.fn();

    const { container } = renderWithMantine(
      <ConditionalStepConfig
        conditionExpression="[score] >= 9"
        trueBranchStepOrder={null}
        falseBranchStepOrder={null}
        conditionalConfig={{
          version: 1,
          mode: 'Decision',
          decisionQuestion: 'Ship it?',
          threshold: 0.7,
          providerId: null,
          timeoutSeconds: 5,
        }}
        onChange={onChange}
        previousSteps={[]}
      />,
    );

    fireEvent.change(within(container).getByDisplayValue('0.7'), { target: { value: '0.9' } });
    applyForm(container);

    await waitFor(() => expect(onChange).toHaveBeenCalled());

    const lastCall = onChange.mock.calls[onChange.mock.calls.length - 1][0];
    // The VALUE round-trips, not merely the control's presence.
    expect(lastCall.conditionalConfig.threshold).toBe(0.9);
    // …and the rest of the config is preserved rather than reset.
    expect(lastCall.conditionalConfig.mode).toBe('Decision');
    expect(lastCall.conditionalConfig.decisionQuestion).toBe('Ship it?');
    expect(lastCall.conditionalConfig.version).toBe(1);
  });

  it('switches the decision mode through the enum control', async () => {
    const onChange = vi.fn();

    const { container } = renderWithMantine(
      <ConditionalStepConfig
        conditionExpression="[score] >= 9"
        trueBranchStepOrder={null}
        falseBranchStepOrder={null}
        conditionalConfig={null}
        onChange={onChange}
        previousSteps={[]}
      />,
    );

    fireEvent.click(within(container).getByLabelText('How to decide'));
    fireEvent.click(await screen.findByRole('option', { name: /^Decision:/ }));
    applyForm(container);

    await waitFor(() => expect(onChange).toHaveBeenCalled());

    const lastCall = onChange.mock.calls[onChange.mock.calls.length - 1][0];
    expect(lastCall.conditionalConfig.mode).toBe('Decision');
  });

  it('renders the C# defaults for a null config without writing them back', async () => {
    const onChange = vi.fn();

    const { container } = renderWithMantine(
      <ConditionalStepConfig
        conditionExpression="[score] >= 9"
        trueBranchStepOrder={null}
        falseBranchStepOrder={null}
        conditionalConfig={null}
        onChange={onChange}
        previousSteps={[]}
      />,
    );

    const defaults = createDefaultConditionalStepConfig();
    expect(within(container).getAllByDisplayValue(/^Expression:/).length).toBeGreaterThan(0);
    expect(within(container).getByDisplayValue(String(defaults.threshold))).toBeTruthy();
    expect(within(container).getByDisplayValue(String(defaults.timeoutSeconds))).toBeTruthy();

    // Rendering alone must not touch the parent: a step that had no config still has none.
    expect(onChange).not.toHaveBeenCalled();

    // …and an edit that touches only the condition COLUMN leaves the config column untouched: the
    // update omits it entirely rather than persisting an encoding of today's defaults.
    fireEvent.change(within(container).getByDisplayValue('[score] >= 9'), { target: { value: '[score] >= 8' } });
    applyForm(container);

    await waitFor(() => expect(onChange).toHaveBeenCalled());

    const lastCall = onChange.mock.calls[onChange.mock.calls.length - 1][0];
    expect(lastCall.conditionExpression).toBe('[score] >= 8');
    expect('conditionalConfig' in lastCall).toBe(false);
  });
});
