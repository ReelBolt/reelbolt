// @vitest-environment jsdom
'use client';

/**
 * T-071 / T-070 regression coverage — a Voiceover node clicked on the workflow page must open a
 * drawer that actually contains the Voiceover editor, and editing one of its fields must flow back
 * out through the panel's onChange. Proven RED against the pre-T-070 StepConfigPanel, whose
 * renderStepTypeConfig() had no Voiceover branch at all.
 *
 * Deliberately NOT mocked: VoiceoverStepConfig is rendered for real, so these assertions can only
 * pass if the wired editor is on screen — mocking the module away would make them tautological.
 */

import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { StepConfigPanel } from './StepConfigPanel';
import { createDefaultVoiceoverStepConfig } from './VoiceoverStepConfig';
import { createDefaultVideoAnalyzeStepConfig } from './VideoAnalyzeStepConfig';
import { NARRATION_PLAN_STEP_REQUIRED_ERROR } from '@/lib/utils/voiceover-validation';
import type { StepData } from './WorkflowStepList';

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

afterEach(() => cleanup());

function makeStep(overrides: Partial<StepData> = {}): StepData {
  return {
    id: 's1',
    label: 'Narration pass',
    agentDefinitionId: 'vt-agent',
    stepType: 'Voiceover',
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

function renderPanel(
  step: StepData,
  onChange: (updates: Partial<StepData>) => void,
  allSteps: StepData[] = [step],
  currentStepIndex = 0,
) {
  return render(
    <MantineProvider>
      <StepConfigPanel
        step={step}
        opened
        onClose={vi.fn()}
        onChange={onChange}
        allSteps={allSteps}
        previousSteps={[]}
        currentStepIndex={currentStepIndex}
      />
    </MantineProvider>,
  );
}

describe('StepConfigPanel — Voiceover steps get their type-specific editor', () => {
  it('renders the Voiceover config editor instead of an empty drawer', async () => {
    renderPanel(makeStep({ voiceoverConfig: createDefaultVoiceoverStepConfig() }), vi.fn());

    // Pre-T-070 renderStepTypeConfig() returned null for Voiceover: the drawer opened with nothing
    // but the step label. Every assertion below is a control VoiceoverStepConfig itself renders.
    expect(await screen.findByText('Voice service')).toBeTruthy();
    expect(screen.getAllByLabelText(/Speech service/).length).toBeGreaterThan(0);
    expect(screen.getAllByLabelText(/Model \(optional\)/).length).toBeGreaterThan(0);
    expect(screen.getByLabelText(/Default voice ID/)).toBeTruthy();
    expect(screen.getByLabelText(/Audio size limit/)).toBeTruthy();
  });

  it('routes an edit of a rendered field out through onChange as a voiceoverConfig patch', async () => {
    const onChange = vi.fn();
    renderPanel(makeStep({ voiceoverConfig: createDefaultVoiceoverStepConfig() }), onChange);

    const voiceInput = await screen.findByLabelText(/Default voice ID/);
    fireEvent.change(voiceInput, { target: { value: 'aria' } });

    // Proves the drawer holds the *wired* editor rather than a static rendering of one: a field
    // that renders but never calls back is the same defect one layer down.
    expect(onChange).toHaveBeenCalledWith({
      voiceoverConfig: expect.objectContaining({ defaultVoiceId: 'aria' }),
    });
  });

  it('still renders the VideoAnalyze editor for a VideoAnalyze step (sibling control)', async () => {
    renderPanel(
      makeStep({ stepType: 'VideoAnalyze', videoAnalyzeConfig: createDefaultVideoAnalyzeStepConfig() }),
      vi.fn(),
    );

    // Control: this branch existed before T-070 and must keep working after it. A red assertion
    // here means the Voiceover branch was inserted somewhere it shadows a sibling.
    expect(await screen.findByText('Silence detection')).toBeTruthy();
    expect(screen.getByRole('switch', { name: /^Analyze visuals/ })).toBeTruthy();
    expect(screen.queryByText('Voice service')).toBeNull();
  });

  /**
   * Wave 1 phase 2 (T-003) — the NarrationPlan mode through the drawer that actually hosts it.
   *
   * The editor's own suite renders it in isolation; these two assert the wiring between the panel
   * and that editor, which is where a mode can be present but unreachable.
   */
  it('exposes the NarrationPlan source mode, with its unconfigured warning, in the drawer', async () => {
    renderPanel(
      makeStep({
        voiceoverConfig: { source: { kind: 'NarrationPlan', narrationWriterStepOrder: null } },
      }),
      vi.fn(),
    );

    expect(await screen.findByRole('radio', { name: 'From Narration plan' })).toBeTruthy();
    expect(screen.getByRole('combobox', { name: 'Narration writer step' })).toBeTruthy();
    // The unconfigured state has to reach the drawer too: a warning the user never sees is the
    // same defect as no warning at all.
    expect(screen.getByText(NARRATION_PLAN_STEP_REQUIRED_ERROR)).toBeTruthy();
  });

  it('routes a switch into the NarrationPlan mode out through onChange as a voiceoverConfig patch', async () => {
    const onChange = vi.fn();

    renderPanel(makeStep({ voiceoverConfig: createDefaultVoiceoverStepConfig() }), onChange);

    fireEvent.click(await screen.findByRole('radio', { name: 'From Narration plan' }));

    // A mode switch the drawer swallows would leave the user staring at an editor that never
    // changes; the patch must carry the new kind AND the null step order the editor emits.
    expect(onChange).toHaveBeenCalledWith({
      voiceoverConfig: expect.objectContaining({
        source: expect.objectContaining({
          kind: 'NarrationPlan',
          narrationWriterStepOrder: null,
        }),
      }),
    });
  });

  it('shows an already-chosen NarrationWriter step in the picker, with no warning', async () => {
    const writer = makeStep({ id: 's0', label: 'Write narration', stepType: 'Agent' });
    const node = makeStep({
      voiceoverConfig: { source: { kind: 'NarrationPlan', narrationWriterStepOrder: 1 } },
    });

    renderPanel(node, vi.fn(), [writer, node], 1);

    // The picker reflects the persisted order, so reopening a configured step does not silently
    // read as unchosen.
    const picker = (await screen.findByRole('combobox', {
      name: 'Narration writer step',
    })) as HTMLInputElement;
    expect(picker.value).toBe('1. Write narration');
    expect(screen.queryByText(NARRATION_PLAN_STEP_REQUIRED_ERROR)).toBeNull();
  });

  it('leaves an untouched Voiceover step on the Inline mode (control)', async () => {
    // Control: the NarrationPlan branch must not shadow the default rendering, and the alternate
    // mode's picker must not be reachable from an Inline step.
    renderPanel(makeStep({ voiceoverConfig: createDefaultVoiceoverStepConfig() }), vi.fn());

    expect(await screen.findByRole('radio', { name: 'Type lines here' })).toBeTruthy();
    expect(screen.queryByRole('combobox', { name: 'Narration writer step' })).toBeNull();
    expect(screen.queryByText(NARRATION_PLAN_STEP_REQUIRED_ERROR)).toBeNull();
  });
});
