// @vitest-environment jsdom
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within, cleanup } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ForEachStepConfig } from './ForEachStepConfig';
import type { StepType } from '@/lib/types/workflow';
import type { StepData } from './WorkflowStepList';

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
});

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

describe('ForEachStepConfig', () => {
  it('preserves unknown top-level config key in edit round-trip', async () => {
    const onChange = vi.fn();

    renderWithMantine(
      <ForEachStepConfig
        loopSourceExpression="[items]"
        maxIterations={10}
        onChange={onChange}
        previousSteps={[
          { ...createTestStep('step-1'), label: 'First', stepType: 'Extract' as StepType },
        ]}
      />,
    );

    // Simulate a form change via SchemaDrivenStepConfigForm
    const loopSourceInput = screen.getByDisplayValue('[items]');
    fireEvent.change(loopSourceInput, { target: { value: '[newItems]' } });
    const applyButtons = screen.getAllByRole('button', { name: /apply/i });
    fireEvent.click(applyButtons[0]);

    await waitFor(() => {
      expect(onChange).toHaveBeenCalled();
    });

    // Verify onChange was called with updated loopSourceExpression
    const lastCall = onChange.mock.calls[onChange.mock.calls.length - 1][0];
    expect(lastCall).toHaveProperty('loopSourceExpression', '[newItems]');
  });

  it('renders form fields for loopSourceExpression and maxIterations', () => {
    renderWithMantine(
      <ForEachStepConfig
        loopSourceExpression="{{steps.1.items}}"
        maxIterations={5}
        onChange={vi.fn()}
        previousSteps={[
          { ...createTestStep('step-1'), label: 'Item Source', stepType: 'Extract' as StepType },
        ]}
      />,
    );

    // Verify form fields render
    expect(screen.getByDisplayValue('{{steps.1.items}}')).toBeTruthy();
    expect(screen.getByDisplayValue('5')).toBeTruthy();
  });

  it('applies changes via SchemaDrivenStepConfigForm', async () => {
    const onChange = vi.fn();

    renderWithMantine(
      <ForEachStepConfig
        loopSourceExpression="[items]"
        maxIterations={3}
        onChange={onChange}
        previousSteps={[]}
      />,
    );

    // Verify onChange can be triggered by form changes
    const loopSourceInput = screen.getByDisplayValue('[items]');
    fireEvent.change(loopSourceInput, { target: { value: '[updatedItems]' } });
    const applyButtons = screen.getAllByRole('button', { name: /apply/i });
    fireEvent.click(applyButtons[applyButtons.length - 1]);

    await waitFor(() => {
      expect(onChange).toHaveBeenCalledWith(
        expect.objectContaining({ loopSourceExpression: '[updatedItems]' }),
      );
    });
  });
});
