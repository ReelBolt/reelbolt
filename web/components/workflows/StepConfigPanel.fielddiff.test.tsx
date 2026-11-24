// @vitest-environment jsdom
'use client';

/**
 * Field-level highlighting of the assistant's pending proposal inside a step's config panel.
 * (Header of the sibling voiceover test kept below for context.)
 *
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
import type { StepFieldDiff } from './field-diff';
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

function renderPanel(step: StepData, onChange: (updates: Partial<StepData>) => void, fieldDiff?: StepFieldDiff) {
  return render(
    <MantineProvider>
      <StepConfigPanel
        step={step}
        opened
        onClose={vi.fn()}
        onChange={onChange}
        allSteps={[step]}
        previousSteps={[]}
        currentStepIndex={0}
        fieldDiff={fieldDiff}
      />
    </MantineProvider>,
  );
}

describe('StepConfigPanel — assistant proposal field highlighting', () => {
  const reviewStep = () =>
    makeStep({ stepType: 'ReviewLoop', label: 'Review', minScore: 7, reviewLoopConfig: null });

  it('highlights exactly the proposed fields, in nested config and in step columns', () => {
    const diff: StepFieldDiff = new Map([
      ['col.minScore', { before: 7, after: 9 }],
      ['config.enabled', { before: false, after: true }],
    ]);
    renderPanel(reviewStep(), vi.fn(), diff);

    const marked = Array.from(document.querySelectorAll('[data-diff="changed"]')).map((e) => e.getAttribute('data-field-path'));
    expect(marked.sort()).toEqual(['col.minScore', 'config.enabled']);
    expect(screen.getByText(/proposes changes to 2 fields/)).toBeTruthy();
    expect(document.querySelector('[data-field-path="col.minScore"]')?.getAttribute('data-diff-note')).toContain('7');
  });

  it('shows no highlight or banner without a proposal', () => {
    renderPanel(reviewStep(), vi.fn());
    expect(document.querySelector('[data-diff]')).toBeNull();
    expect(screen.queryByText(/proposes changes/)).toBeNull();
  });
});
