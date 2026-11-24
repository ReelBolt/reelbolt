// @vitest-environment jsdom
/* eslint @typescript-eslint/no-unused-vars: ["warn", { "argsIgnorePattern": "^_" }] */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { VoiceoverStepConfig, createDefaultVoiceoverStepConfig } from './VoiceoverStepConfig';
import { NARRATION_PLAN_STEP_REQUIRED_ERROR } from '@/lib/utils/voiceover-validation';
import type { StepData } from './WorkflowStepList';

/** The config value the editor edits — named for the type, since the component owns that name. */
type VoiceoverConfigValue = ReturnType<typeof createDefaultVoiceoverStepConfig>;

// Polyfill ResizeObserver for jsdom
vi.stubGlobal(
  'ResizeObserver',
  class ResizeObserver {
    constructor(_callback: ResizeObserverCallback) {}
    observe() {}
    unobserve() {}
    disconnect() {}
  }
);

// Polyfill scrollIntoView for jsdom
Element.prototype.scrollIntoView = vi.fn();

/** A prior step, as the picker sees it: only `label` is read to build the option text. */
function priorStep(label: string): StepData {
  return { label } as StepData;
}

/** A `NarrationPlan` config with the given chosen step order. */
function narrationPlanConfig(narrationWriterStepOrder: number | null): VoiceoverConfigValue {
  return {
    source: { kind: 'NarrationPlan', narrationWriterStepOrder },
    maxBytes: 2_000_000,
  };
}

/** Renders the editor and hands back the `onChange` spy so the emitted config can be inspected. */
function renderEditor(
  config: VoiceoverConfigValue,
  allSteps: StepData[] = [],
  currentStepIndex = 0,
) {
  const onChange = vi.fn();
  render(
    <MantineProvider>
      <VoiceoverStepConfig
        config={config}
        onChange={onChange}
        allSteps={allSteps}
        currentStepIndex={currentStepIndex}
      />
    </MantineProvider>
  );
  return onChange;
}

/** Opens a Mantine Select and clicks one of its options. */
function chooseOption(comboboxName: string, optionName: string) {
  fireEvent.click(screen.getByRole('combobox', { name: comboboxName }));
  fireEvent.click(screen.getByRole('option', { name: optionName }));
}

describe('VoiceoverStepConfig', () => {
  beforeEach(() => {
    global.matchMedia = (query: string): MediaQueryList =>
      ({
        matches: false,
        media: query,
        onchange: null,
        addListener: () => {},
        removeListener: () => {},
        addEventListener: () => {},
        removeEventListener: () => {},
        dispatchEvent: () => true,
      }) as MediaQueryList;
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it('renders the Inline/ScriptScenes toggle', () => {
    const onChange = vi.fn();
    const config = createDefaultVoiceoverStepConfig();

    render(
      <MantineProvider>
        <VoiceoverStepConfig config={config} onChange={onChange} allSteps={[]} currentStepIndex={0} />
      </MantineProvider>
    );

    // Check that both toggle options are rendered
    expect(screen.getByText('Type lines here')).toBeDefined();
    expect(screen.getByText('From Scriptwriter scenes')).toBeDefined();
  });

  it('renders the Source label', () => {
    const onChange = vi.fn();
    const config = createDefaultVoiceoverStepConfig();

    render(
      <MantineProvider>
        <VoiceoverStepConfig config={config} onChange={onChange} allSteps={[]} currentStepIndex={0} />
      </MantineProvider>
    );

    expect(screen.getByText('Source')).toBeDefined();
  });

  it('calls onChange with source.lines containing new entry when adding an Inline line', async () => {
    const onChange = vi.fn();
    const config = createDefaultVoiceoverStepConfig();

    render(
      <MantineProvider>
        <VoiceoverStepConfig config={config} onChange={onChange} allSteps={[]} currentStepIndex={0} />
      </MantineProvider>
    );

    const addButton = screen.getByRole('button', { name: /Add line/i });
    fireEvent.click(addButton);

    await waitFor(() => {
      expect(onChange).toHaveBeenCalled();
      const lastCall = onChange.mock.calls[onChange.mock.calls.length - 1];
      const updatedConfig = lastCall[0];
      expect(updatedConfig.source.lines).toBeDefined();
      expect(Array.isArray(updatedConfig.source.lines)).toBe(true);
      expect(updatedConfig.source.lines.length).toBe(1);
      expect(updatedConfig.source.lines[0]).toEqual({
        text: '',
        startSec: 0,
        voiceId: null,
      });
    });
  });

  it('calls onChange with source.kind === "ScriptScenes" when switching source kind', async () => {
    const onChange = vi.fn();
    const config = createDefaultVoiceoverStepConfig();

    render(
      <MantineProvider>
        <VoiceoverStepConfig config={config} onChange={onChange} allSteps={[]} currentStepIndex={0} />
      </MantineProvider>
    );

    // Find and click the SegmentedControl for ScriptScenes option
    const scriptScenesOption = screen.getByRole('radio', { name: 'From Scriptwriter scenes' });
    fireEvent.click(scriptScenesOption);

    await waitFor(() => {
      expect(onChange).toHaveBeenCalled();
      const lastCall = onChange.mock.calls[onChange.mock.calls.length - 1];
      const updatedConfig = lastCall[0];
      expect(updatedConfig.source.kind).toBe('ScriptScenes');
    });
  });

  /**
   * Wave 1 phase 2 (T-003) — the third source kind, from the editor's side.
   *
   * The state that must be observable here is `NarrationPlan` with no step chosen: the editor has
   * to say so in text, because the failure it prevents is a step that reads as configured in the
   * builder and only fails once executed.
   */
  describe('NarrationPlan source', () => {
    it('offers NarrationPlan as a third source option', () => {
      renderEditor(createDefaultVoiceoverStepConfig());

      expect(screen.getByRole('radio', { name: 'From Narration plan' })).toBeTruthy();
    });

    it('emits kind NarrationPlan with no step and no inline lines when selected', async () => {
      const onChange = renderEditor(createDefaultVoiceoverStepConfig());

      fireEvent.click(screen.getByRole('radio', { name: 'From Narration plan' }));

      await waitFor(() => {
        expect(onChange).toHaveBeenCalled();
        const updatedConfig = onChange.mock.calls[onChange.mock.calls.length - 1][0];

        expect(updatedConfig.source.kind).toBe('NarrationPlan');
        // No step chosen yet — the state getVoiceoverSourceError reports as unconfigured.
        expect(updatedConfig.source.narrationWriterStepOrder).toBeNull();
        // A NarrationPlan never carries lines of its own: they come from the chosen step, and the
        // fields the other two modes own are cleared so no stale state survives the switch.
        expect(updatedConfig.source.lines).toBeUndefined();
        expect(updatedConfig.source.scriptwriterStepOrder).toBeUndefined();
      });
    });

    it('renders the NarrationWriter step picker instead of the Inline line-list controls', () => {
      renderEditor(narrationPlanConfig(null), [priorStep('Script'), priorStep('Compose')], 2);

      // The picker is a labelled control — reached by role + accessible name, not by colour.
      expect(screen.getByRole('combobox', { name: 'Narration writer step' })).toBeTruthy();

      // No Inline control is reachable in this mode: no add button, no empty-state hint, and none
      // of the per-line inputs a stray click could edit.
      expect(screen.queryByRole('button', { name: /Add line/i })).toBeNull();
      expect(screen.queryByText(/No voiceover lines/i)).toBeNull();
      expect(screen.queryByLabelText('Text')).toBeNull();
    });

    it('still renders the picker, disabled, when there are no earlier steps', () => {
      renderEditor(narrationPlanConfig(null), [], 0);

      const picker = screen.getByRole('combobox', { name: 'Narration writer step' }) as HTMLInputElement;
      expect(picker.disabled).toBe(true);
    });

    it('reports the source as unconfigured while no NarrationWriter step is chosen', () => {
      renderEditor(narrationPlanConfig(null), [priorStep('Script'), priorStep('Compose')], 2);

      expect(screen.getByText(NARRATION_PLAN_STEP_REQUIRED_ERROR)).toBeTruthy();
    });

    it('stops reporting the source as unconfigured once a step is chosen', () => {
      renderEditor(narrationPlanConfig(2), [priorStep('Script'), priorStep('Compose')], 2);

      expect(screen.queryByText(NARRATION_PLAN_STEP_REQUIRED_ERROR)).toBeNull();
    });

    it('emits the chosen step order as a 1-based number', async () => {
      const onChange = renderEditor(
        narrationPlanConfig(null),
        [priorStep('Script'), priorStep('Compose')],
        2,
      );

      chooseOption('Narration writer step', '2. Compose');

      await waitFor(() => {
        expect(onChange).toHaveBeenCalled();
        const updatedConfig = onChange.mock.calls[onChange.mock.calls.length - 1][0];
        expect(updatedConfig.source.kind).toBe('NarrationPlan');
        expect(updatedConfig.source.narrationWriterStepOrder).toBe(2);
      });
    });
  });
});

describe('VoiceoverStepConfig keeps pronunciationOverrides', () => {
  afterEach(cleanup);
  it('carries a saved override list through an edit', () => {
    const overrides = [{ term: 'Novara', ipa: 'noˈvara', caseSensitive: false }];
    const config = { ...narrationPlanConfig(1), pronunciationOverrides: overrides } as VoiceoverConfigValue;
    const onChange = renderEditor(config, [priorStep('Script')], 1);
    fireEvent.change(screen.getByLabelText('Default voice ID'), { target: { value: 'voice-9' } });
    const next = onChange.mock.calls[onChange.mock.calls.length - 1][0] as Record<string, unknown>;
    expect(next.defaultVoiceId).toBe('voice-9');
    expect(next.pronunciationOverrides).toEqual(overrides);
  });
});
