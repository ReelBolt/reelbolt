// @vitest-environment jsdom
import { useState } from 'react';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { VideoGenerateStepConfig, createDefaultVideoGenerateStepConfig } from './VideoGenerateStepConfig';
import { PLAN_SOURCE_STEP_REF_REQUIRED_ERROR } from '@/lib/utils/video-generate-validation';
import type { VideoGenerateStepConfig as VideoGenerateStepConfigValue, StepType } from '@/lib/types/workflow';
import type { StepData } from './WorkflowStepList';
import type { Mock } from 'vitest';

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

  // Mantine's Combobox scrolls the selected option into view; jsdom lacks it.
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  cleanup();
});

const renderWithMantine = (component: React.ReactElement) => {
  return render(<MantineProvider>{component}</MantineProvider>);
};

function lastConfig(handleChange: Mock): VideoGenerateStepConfigValue {
  const calls = handleChange.mock.calls;
  return calls[calls.length - 1][0] as VideoGenerateStepConfigValue;
}

/** Minimal StepData (same shape VideoCompileStepConfig.test.tsx builds). */
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
    videoGenerateConfig: null,
    voiceoverConfig: null,
  };
}

/**
 * Stateful harness: the plan-source selector swaps whole sub-trees, so the config must actually
 * round-trip through onChange for the follow-up assertions to mean anything.
 */
function Controlled({
  initial = {},
  allSteps = [],
  currentStepIndex = 0,
}: {
  initial?: Partial<VideoGenerateStepConfigValue>;
  allSteps?: StepData[];
  currentStepIndex?: number;
}) {
  const [config, setConfig] = useState<VideoGenerateStepConfigValue>({
    ...createDefaultVideoGenerateStepConfig(),
    ...initial,
  });
  return (
    <VideoGenerateStepConfig
      config={config}
      onChange={setConfig}
      allSteps={allSteps}
      currentStepIndex={currentStepIndex}
    />
  );
}

/** Opens a Mantine Select and clicks one of its options. */
function chooseOption(comboboxName: string, optionName: string) {
  fireEvent.click(screen.getByRole('combobox', { name: comboboxName }));
  fireEvent.click(screen.getByRole('option', { name: optionName }));
}

describe('VideoGenerateStepConfig', () => {
  it('routes a typed inline prompt through onChange', () => {
    const handleChange = vi.fn();
    const config = createDefaultVideoGenerateStepConfig();

    renderWithMantine(<VideoGenerateStepConfig config={config} onChange={handleChange} />);

    const prompt = screen.getByLabelText(/Prompt/) as HTMLTextAreaElement;
    fireEvent.change(prompt, { target: { value: 'A drone shot over a harbour' } });

    expect(handleChange).toHaveBeenCalled();
    const updated = lastConfig(handleChange);
    expect(updated.inlinePrompt).toBe('A drone shot over a harbour');
    expect(updated.maxSpendCredits).toBe(config.maxSpendCredits);
    expect(updated.model).toBe(config.model);
  });

  it('routes a typed max spend through onChange', () => {
    const handleChange = vi.fn();
    const config = createDefaultVideoGenerateStepConfig();

    renderWithMantine(<VideoGenerateStepConfig config={config} onChange={handleChange} />);

    const maxSpend = screen.getByLabelText(/Max spend \(credits\)/) as HTMLInputElement;
    fireEvent.change(maxSpend, { target: { value: '2.5' } });

    expect(handleChange).toHaveBeenCalled();
    const updated = lastConfig(handleChange);
    expect(updated.maxSpendCredits).toBe(2.5);
    expect(updated.inlinePrompt).toBe(config.inlinePrompt);
  });

  it('resets a 2K resolution to one H3-Max offers when switching model to MiniMax-H3-Max', () => {
    const handleChange = vi.fn();
    const config: VideoGenerateStepConfigValue = {
      ...createDefaultVideoGenerateStepConfig(),
      model: 'MiniMax-H3',
      resolution: '2K',
    };

    renderWithMantine(<VideoGenerateStepConfig config={config} onChange={handleChange} />);

    fireEvent.click(screen.getByRole('combobox', { name: 'Model' }));
    fireEvent.click(screen.getByRole('option', { name: 'MiniMax-H3-Max' }));

    expect(handleChange).toHaveBeenCalled();
    const updated = lastConfig(handleChange);
    expect(updated.model).toBe('MiniMax-H3-Max');
    expect(['480P', '768P']).toContain(updated.resolution);
    expect(updated.resolution).not.toBe('2K');
  });

  describe('Max Clips / Takes', () => {
    it("renders a 'Max Clips' input next to Takes", () => {
      renderWithMantine(
        <VideoGenerateStepConfig config={createDefaultVideoGenerateStepConfig()} onChange={vi.fn()} />,
      );

      const maxClips = screen.getByRole('textbox', { name: 'Max Clips' }) as HTMLInputElement;
      expect(maxClips.value).toBe('1');
      expect(screen.getByText(/Caps paid clips/)).toBeTruthy();
    });

    it('caps typed takes at maxClips and never changes maxClips', () => {
      const handleChange = vi.fn();
      const config: VideoGenerateStepConfigValue = {
        ...createDefaultVideoGenerateStepConfig(),
        takes: 1,
        maxClips: 2,
      };

      renderWithMantine(<VideoGenerateStepConfig config={config} onChange={handleChange} />);

      expect(screen.getByText('Number of generation attempts (1-2)')).toBeTruthy();
      fireEvent.change(screen.getByRole('textbox', { name: 'Takes' }), { target: { value: '70' } });

      expect(handleChange).toHaveBeenCalled();
      const updated = lastConfig(handleChange);
      expect(updated.takes).toBe(2);
      expect(updated.maxClips).toBe(2);
    });

    it('clamps takes in the same onChange when maxClips is lowered below it', () => {
      const handleChange = vi.fn();
      const config: VideoGenerateStepConfigValue = {
        ...createDefaultVideoGenerateStepConfig(),
        takes: 4,
        maxClips: 4,
      };

      renderWithMantine(<VideoGenerateStepConfig config={config} onChange={handleChange} />);

      fireEvent.change(screen.getByRole('textbox', { name: 'Max Clips' }), { target: { value: '2' } });

      expect(handleChange).toHaveBeenCalledTimes(1);
      const updated = lastConfig(handleChange);
      expect(updated.maxClips).toBe(2);
      expect(updated.takes).toBe(2);
    });

    it('clamps maxClips to 1..64', () => {
      const handleChange = vi.fn();
      renderWithMantine(
        <VideoGenerateStepConfig config={createDefaultVideoGenerateStepConfig()} onChange={handleChange} />,
      );

      fireEvent.change(screen.getByRole('textbox', { name: 'Max Clips' }), { target: { value: '99' } });

      expect(lastConfig(handleChange).maxClips).toBe(64);
      expect(lastConfig(handleChange).takes).toBe(1);
    });

    it('flags a loaded config whose takes exceed maxClips without mutating it', () => {
      const handleChange = vi.fn();
      const config: VideoGenerateStepConfigValue = {
        ...createDefaultVideoGenerateStepConfig(),
        takes: 3,
        maxClips: 1,
      };

      renderWithMantine(<VideoGenerateStepConfig config={config} onChange={handleChange} />);

      expect(screen.getByText('Takes cannot exceed Max Clips')).toBeTruthy();
      expect(handleChange).not.toHaveBeenCalled();
    });
  });

  describe('Max Spend (credits) field error', () => {
    const MAX_SPEND_ERROR = 'Max Spend (credits) must be greater than 0';

    it('shows the error at the default maxSpendCredits of 0', () => {
      renderWithMantine(
        <VideoGenerateStepConfig config={createDefaultVideoGenerateStepConfig()} onChange={vi.fn()} />,
      );
      expect(screen.getByText(MAX_SPEND_ERROR)).toBeTruthy();
    });

    it('shows the error for a negative maxSpendCredits', () => {
      renderWithMantine(
        <VideoGenerateStepConfig
          config={{ ...createDefaultVideoGenerateStepConfig(), maxSpendCredits: -2 }}
          onChange={vi.fn()}
        />,
      );
      expect(screen.getByText(MAX_SPEND_ERROR)).toBeTruthy();
    });

    it('shows no error once maxSpendCredits is > 0', () => {
      renderWithMantine(
        <VideoGenerateStepConfig
          config={{ ...createDefaultVideoGenerateStepConfig(), maxSpendCredits: 0.01 }}
          onChange={vi.fn()}
        />,
      );
      expect(screen.queryByText(MAX_SPEND_ERROR)).toBeNull();
    });
  });

  describe('Poll Interval / Timeout field errors', () => {
    const POLL = 'Poll Interval must be between 1 and 300 seconds';
    const TIMEOUT = 'Timeout must be between 60 and 7200 seconds';
    const CROSS = 'Timeout must be at least the Poll Interval';

    const renderWith = (overrides: Partial<VideoGenerateStepConfigValue>) =>
      renderWithMantine(
        <VideoGenerateStepConfig
          config={{ ...createDefaultVideoGenerateStepConfig(), ...overrides }}
          onChange={vi.fn()}
        />,
      );

    function inputFor(label: RegExp): HTMLInputElement {
      return screen.getByRole('textbox', { name: label, hidden: true }) as HTMLInputElement;
    }

    it('shows no poll/timeout errors for the defaults', () => {
      renderWith({});
      expect(screen.queryByText(POLL)).toBeNull();
      expect(screen.queryByText(TIMEOUT)).toBeNull();
      expect(screen.queryByText(CROSS)).toBeNull();
    });

    it('shows the poll range error on the Poll Interval field', () => {
      renderWith({ pollIntervalSeconds: 301 });
      expect(inputFor(/Poll Interval/).getAttribute('aria-invalid')).toBe('true');
      expect(inputFor(/Timeout/).getAttribute('aria-invalid')).not.toBe('true');
      expect(screen.getByText(POLL)).toBeTruthy();
    });

    it('shows the timeout range error on the Timeout field', () => {
      renderWith({ timeoutSeconds: 59 });
      expect(inputFor(/Timeout/).getAttribute('aria-invalid')).toBe('true');
      expect(inputFor(/Poll Interval/).getAttribute('aria-invalid')).not.toBe('true');
      expect(screen.getByText(TIMEOUT)).toBeTruthy();
    });

    it('shows the cross-field error on the Timeout field', () => {
      renderWith({ pollIntervalSeconds: 200, timeoutSeconds: 100 });
      expect(inputFor(/Timeout/).getAttribute('aria-invalid')).toBe('true');
      expect(inputFor(/Poll Interval/).getAttribute('aria-invalid')).not.toBe('true');
      expect(screen.getByText(CROSS)).toBeTruthy();
    });

    it('starts the Advanced section collapsed for valid defaults', () => {
      renderWith({});
      expect(screen.queryByRole('textbox', { name: /Poll Interval/ })).toBeNull();
    });

    it.each([
      { pollIntervalSeconds: 0 },
      { timeoutSeconds: 7201 },
      { pollIntervalSeconds: 200, timeoutSeconds: 100 },
    ])('starts the Advanced section open when a field is invalid (%o)', (overrides) => {
      renderWith(overrides);
      expect(screen.getByRole('textbox', { name: /Poll Interval/ })).toBeTruthy();
      expect(screen.getByRole('textbox', { name: /Timeout/ })).toBeTruthy();
    });

    it('describes each field range', () => {
      renderWith({});
      expect(screen.getByText('1-300 seconds')).toBeTruthy();
      expect(screen.getByText('60-7200 seconds')).toBeTruthy();
    });
  });

  describe('What to generate', () => {
    it('renders the selector with Inline selected by default', () => {
      renderWithMantine(<Controlled />);

      const selector = screen.getByRole('combobox', { name: 'What to generate' }) as HTMLInputElement;
      expect(selector.value).toBe('Inline prompt');
    });

    it('routes the chosen plan source through onChange', () => {
      const handleChange = vi.fn();
      renderWithMantine(
        <VideoGenerateStepConfig config={createDefaultVideoGenerateStepConfig()} onChange={handleChange} />,
      );

      chooseOption('What to generate', 'Plan from a planner step');

      expect(handleChange).toHaveBeenCalled();
      expect(lastConfig(handleChange).planSource).toBe('StepRef');
    });

    it('leaves the Inline editor unchanged: no reference pickers, prompt still required', () => {
      renderWithMantine(<Controlled />);

      expect(screen.queryByRole('combobox', { name: 'Plan step' })).toBeNull();
      expect(screen.queryByRole('combobox', { name: 'Analysis step (optional)' })).toBeNull();
      expect((screen.getByLabelText(/Prompt/) as HTMLTextAreaElement).required).toBe(true);
      expect(screen.getByText('Max Spend (credits) must be greater than 0')).toBeTruthy();
    });

    it('shows both reference pickers once the step is plan-sourced', () => {
      renderWithMantine(<Controlled />);

      chooseOption('What to generate', 'Plan from a planner step');

      expect(screen.getByRole('combobox', { name: 'Plan step' })).toBeTruthy();
      expect(screen.getByRole('combobox', { name: 'Analysis step (optional)' })).toBeTruthy();
    });

    it('drops the required marker from Inline Prompt in plan-sourced mode', () => {
      renderWithMantine(<Controlled initial={{ planSource: 'StepRef' }} />);

      expect((screen.getByLabelText(/Prompt/) as HTMLTextAreaElement).required).toBe(false);
      expect(screen.getByText('Unused while the prompt comes from a planner step')).toBeTruthy();
    });

    it('shows the required-reference error while the plan is unset', () => {
      renderWithMantine(<Controlled initial={{ planSource: 'StepRef', plan: null }} />);

      expect(screen.getByText(PLAN_SOURCE_STEP_REF_REQUIRED_ERROR)).toBeTruthy();
    });

    it('shows no required-reference error once a plan reference is set', () => {
      renderWithMantine(
        <Controlled initial={{ planSource: 'StepRef', plan: { from: 'Previous' } }} />,
      );

      expect(screen.queryByText(PLAN_SOURCE_STEP_REF_REQUIRED_ERROR)).toBeNull();
    });

    it('flags a specific-step plan that has no step order', () => {
      renderWithMantine(
        <Controlled initial={{ planSource: 'StepRef', plan: { from: 'Step', stepOrder: null } }} />,
      );

      expect(screen.getByText('Plan with a specific step requires a step order')).toBeTruthy();
    });

    it('routes a picked plan reference through onChange', () => {
      const handleChange = vi.fn();
      renderWithMantine(
        <VideoGenerateStepConfig
          config={{ ...createDefaultVideoGenerateStepConfig(), planSource: 'StepRef' }}
          onChange={handleChange}
        />,
      );

      chooseOption('Plan step', 'Specific step');

      expect(lastConfig(handleChange).plan).toEqual({ from: 'Step', stepOrder: null });
    });

    it('offers the prior steps when a specific plan step is requested', () => {
      renderWithMantine(
        <Controlled
          initial={{ planSource: 'StepRef', plan: { from: 'Step', stepOrder: null } }}
          allSteps={[createTestStep('step-1', 'Agent'), createTestStep('step-2', 'VideoGenerate')]}
          currentStepIndex={1}
        />,
      );

      chooseOption('Plan step', 'Specific step');
      fireEvent.click(screen.getByRole('combobox', { name: 'Step' }));

      // Only the single step BEFORE this one (index 1) is offered.
      expect(screen.getByRole('option', { name: '1. Step step-1' })).toBeTruthy();
      expect(screen.queryByRole('option', { name: /step-2/ })).toBeNull();
    });
  });
});
