// @vitest-environment jsdom
import { render, screen, fireEvent, within, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import {
  ReviewLoopStepConfig,
  createDefaultReviewLoopStepConfig,
} from './ReviewLoopStepConfig';
import type { Mock } from 'vitest';

afterEach(() => cleanup());

beforeAll(() => {
  // Mock matchMedia
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

  // Mock ResizeObserver
  // A `function`, not an arrow: vitest 4+ refuses `new` on an arrow-function mock.
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });
});

// Mantine's Select renders its options into a portal on document.body, so the early-stop tests need
// an unambiguous document — this file has no RTL auto-cleanup otherwise.
afterEach(cleanup);

const renderWithMantine = (component: React.ReactElement) => {
  return render(<MantineProvider>{component}</MantineProvider>);
};

/** Clicks the schema form's Apply so the form's values reach `onChange`. */
function applyForm(container: HTMLElement) {
  fireEvent.click(within(container).getByRole('button', { name: 'Apply' }));
}

describe('ReviewLoopStepConfig', () => {
  it('routes an edited field through onChange while leaving sibling fields untouched', () => {
    const handleChange = vi.fn();

    const { container } = renderWithMantine(
      <ReviewLoopStepConfig
        minScore={8}
        maxIterations={3}
        loopTargetStepOrder={null}
        onChange={handleChange}
        previousSteps={[]}
        currentStepIndex={0}
      />,
    );

    // Verify the form renders
    expect(within(container).getByText(/review agent expects/)).toBeTruthy();

    // Find and interact with the minScore NumberInput
    const minScoreInput = within(container).getByDisplayValue('8');
    expect(minScoreInput).toBeTruthy();

    // Fire change event to update minScore
    fireEvent.change(minScoreInput, { target: { value: '9' } });

    // Blur to trigger validation
    fireEvent.blur(minScoreInput);

    // Find and click the "Apply" button to submit the form
    applyForm(container);

    // Verify onChange was called with the updated minScore. No `reviewLoopConfig` prop was passed,
    // so the early-stop defaults are pristine and the update omits the config key entirely: the
    // caller's partial merge then leaves `reviewLoopConfigJson` as it was (null) rather than
    // rewriting it with an encoding of today's defaults.
    expect(handleChange).toHaveBeenCalledWith({
      minScore: 9,
      maxIterations: 3,
      loopTargetStepOrder: null,
    });

    // Verify that only the expected fields are in the call args.
    const callArg = (handleChange as Mock).mock.calls[0][0] as Record<string, unknown>;
    expect(Object.keys(callArg).sort()).toEqual(['loopTargetStepOrder', 'maxIterations', 'minScore']);
  });

  /**
   * Inverted from T-204's guard test. Back then `StepData` carried no `reviewLoopConfigJson`, so
   * rendering the gate controls would have silently discarded whatever the author typed; the builder
   * now carries the config and `handleFormChange` forwards it, so the controls must be present AND
   * hold the step's stored value.
   */
  it('renders the calibrated early stop populated from the step config', () => {
    const { container } = renderWithMantine(
      <ReviewLoopStepConfig
        minScore={8}
        maxIterations={3}
        loopTargetStepOrder={null}
        reviewLoopConfig={{
          version: 1,
          enabled: true,
          acceptAt: 0.9,
          bimodalMargin: 0.2,
          providerId: '22222222-2222-2222-2222-222222222222',
          timeoutSeconds: 12,
        }}
        onChange={vi.fn()}
        previousSteps={[]}
        currentStepIndex={0}
      />,
    );

    for (const label of ['Early stop with a decision model', 'Early-stop confidence', 'Early-stop margin', 'Decision provider', 'Decision timeout (seconds)']) {
      expect(within(container).getByText(label)).toBeTruthy();
    }

    expect((within(container).getByLabelText(/Early stop with a decision model/) as HTMLInputElement).checked).toBe(true);
    expect(within(container).getByDisplayValue('0.9')).toBeTruthy();
    expect(within(container).getByDisplayValue('0.2')).toBeTruthy();
    expect(within(container).getByDisplayValue('12')).toBeTruthy();
  });

  it('carries an edited early-stop field into the onChange payload', () => {
    const handleChange = vi.fn();

    const { container } = renderWithMantine(
      <ReviewLoopStepConfig
        minScore={8}
        maxIterations={3}
        loopTargetStepOrder={null}
        reviewLoopConfig={{
          version: 1,
          enabled: false,
          acceptAt: 0.85,
          bimodalMargin: 0.15,
          providerId: null,
          timeoutSeconds: 5,
        }}
        onChange={handleChange}
        previousSteps={[]}
        currentStepIndex={0}
      />,
    );

    fireEvent.change(within(container).getByDisplayValue('0.85'), { target: { value: '0.6' } });
    applyForm(container);

    const callArg = (handleChange as Mock).mock.calls[0][0] as Record<string, unknown>;
    // The VALUE round-trips, not merely the control's presence…
    expect((callArg.reviewLoopConfig as Record<string, unknown>).acceptAt).toBe(0.6);
    // …and the remaining config members survive the edit.
    expect((callArg.reviewLoopConfig as Record<string, unknown>).enabled).toBe(false);
    expect((callArg.reviewLoopConfig as Record<string, unknown>).bimodalMargin).toBe(0.15);
    expect((callArg.reviewLoopConfig as Record<string, unknown>).version).toBe(1);
    // The step columns are still carried alongside it.
    expect(callArg.minScore).toBe(8);
    expect(callArg.maxIterations).toBe(3);
  });

  it('enables the early stop through the boolean control', () => {
    const handleChange = vi.fn();

    const { container } = renderWithMantine(
      <ReviewLoopStepConfig
        minScore={8}
        maxIterations={3}
        loopTargetStepOrder={null}
        reviewLoopConfig={null}
        onChange={handleChange}
        previousSteps={[]}
        currentStepIndex={0}
      />,
    );

    fireEvent.click(within(container).getByLabelText(/Early stop with a decision model/));
    applyForm(container);

    const callArg = (handleChange as Mock).mock.calls[0][0] as Record<string, unknown>;
    expect((callArg.reviewLoopConfig as Record<string, unknown>).enabled).toBe(true);
  });

  it('renders the C# defaults for a null config without writing them back', () => {
    const handleChange = vi.fn();

    const { container } = renderWithMantine(
      <ReviewLoopStepConfig
        minScore={8}
        maxIterations={3}
        loopTargetStepOrder={null}
        reviewLoopConfig={null}
        onChange={handleChange}
        previousSteps={[]}
        currentStepIndex={0}
      />,
    );

    const defaults = createDefaultReviewLoopStepConfig();
    // `enabled: false` is the whole point of the default — a null config is today's reviewer path.
    expect((within(container).getByLabelText(/Early stop with a decision model/) as HTMLInputElement).checked).toBe(false);
    expect(within(container).getByDisplayValue(String(defaults.acceptAt))).toBeTruthy();

    // Rendering alone must not touch the parent.
    expect(handleChange).not.toHaveBeenCalled();

    // Editing only a step COLUMN leaves the config column untouched: the key is omitted.
    fireEvent.change(within(container).getByDisplayValue('8'), { target: { value: '9' } });
    applyForm(container);

    const callArg = (handleChange as Mock).mock.calls[0][0] as Record<string, unknown>;
    expect(callArg.minScore).toBe(9);
    expect('reviewLoopConfig' in callArg).toBe(false);
  });
});
