// @vitest-environment jsdom
'use client';

/**
 * T-071 / T-070 regression coverage — the Voiceover canvas node must expose the same keyboard
 * affordances as its nine sibling nodes (role="button", tabIndex 0, an accessible name, and
 * Enter/Space firing a click). Proven RED against the pre-T-070 VoiceoverNode.
 *
 * Mirrors the ConditionalNode block of workflow-builder.a11y.test.tsx, including its
 * matchMedia + ResizeObserver stubs and afterEach(cleanup): without them this node's Mantine
 * render throws under Vitest.
 */

import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ReactFlowProvider } from '@xyflow/react';
import { VoiceoverNode, VOICEOVER_UNCONFIGURED_LABEL, VOICEOVER_UNCONFIGURED_SUMMARY } from './VoiceoverNode';
import { getVoiceoverSourceError } from '@/lib/utils/voiceover-validation';
import type { StepData } from '../WorkflowStepList';

// Vitest runs without `globals`, so Testing Library's auto-cleanup never registers: unmount
// explicitly, otherwise Mantine's scroll-lock timer can fire after jsdom is torn down.
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
    label: 'Narrate',
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

/** Renders the node the way the canvas does: data for `{stepNumber: 4, label: 'Narrate'}`. */
function renderNode(step: StepData, stepNumber = 4, allSteps: StepData[] = [step]) {
  return render(
    <MantineProvider>
      <ReactFlowProvider>
        <VoiceoverNode
          data={{
            step,
            stepNumber,
            allSteps,
            currentStepIndex: 0,
            expanded: false,
            pinned: false,
            onExpandChange: vi.fn(),
            onTogglePin: vi.fn(),
            onChange: vi.fn(),
            onRemove: vi.fn(),
          }}
        />
      </ReactFlowProvider>
    </MantineProvider>,
  );
}

/** The node's outer card — the element carrying role="button" and the keydown handler. */
function outerButton(step: StepData, stepNumber = 4) {
  renderNode(step, stepNumber);
  return screen.getByRole('button', { name: `Step ${stepNumber}, Voiceover: Narrate` });
}

describe('VoiceoverNode keyboard accessibility', () => {
  it('exposes an accessible name via role="button" + aria-label "Step {n}, {StepType}: {label}"', () => {
    const button = outerButton(makeStep({ label: 'Narrate' }), 4);

    // Pre-T-070 the card carried no role and no aria-label, so this query found nothing at all.
    expect(button).toBeTruthy();
  });

  it('makes the node keyboard-focusable (tabIndex 0)', () => {
    const button = outerButton(makeStep({ label: 'Narrate' }), 4);

    // Queried through role="button" and re-checked here so the assertion cannot be satisfied by
    // some unrelated element that merely happens to have tabindex="0".
    expect(button.getAttribute('tabindex')).toBe('0');
  });

  it('fires a click when Enter is pressed on the node', () => {
    const button = outerButton(makeStep({ label: 'Narrate' }), 4);
    const clickSpy = vi.fn();
    button.addEventListener('click', clickSpy);

    fireEvent.keyDown(button, { key: 'Enter', code: 'Enter' });

    expect(clickSpy).toHaveBeenCalled();
  });

  it('fires a click when Space is pressed on the node', () => {
    const button = outerButton(makeStep({ label: 'Narrate' }), 4);
    const clickSpy = vi.fn();
    button.addEventListener('click', clickSpy);

    fireEvent.keyDown(button, { key: ' ', code: 'Space' });

    expect(clickSpy).toHaveBeenCalled();
  });
});

/**
 * Wave 1 phase 2 (T-003) — the source summary badge.
 *
 * Pre-T-002 the node fell through to `'ScriptScenes'` for every source that was not Inline, so a
 * `NarrationPlan` step — including one with no step chosen at all — read as a finished ScriptScenes
 * source. The badge now names the chosen step, and the unconfigured state is stated in text.
 */
describe('VoiceoverNode NarrationPlan summary', () => {
  /** A NarrationPlan step whose source carries the given (possibly invalid) step order. */
  function narrationStep(narrationWriterStepOrder: number | null | undefined, label = 'Narrate'): StepData {
    return makeStep({
      label,
      voiceoverConfig: { source: { kind: 'NarrationPlan', narrationWriterStepOrder } },
    });
  }

  it('summarises a configured NarrationPlan as its chosen step, never as ScriptScenes', () => {
    renderNode(narrationStep(1), 4);

    expect(
      screen.getByLabelText('Voiceover source: narration written in step 1 (Narrate)').textContent,
    ).toBe('Narration from step 1');

    // The pre-T-002 node reported every non-Inline kind as ScriptScenes, whose badge accessible
    // name is exactly this. Queried by accessible name rather than by text, because the collapsed
    // editor stays mounted in the DOM and its own `ScriptScenes` toggle would satisfy a page-wide
    // text query regardless of what the summary says.
    expect(screen.queryByLabelText('Voiceover source: From the script')).toBeNull();
  });

  it('names the chosen step with the label the builder shows for it', () => {
    const writer = makeStep({ id: 's0', label: 'Write narration', stepType: 'Agent' });

    renderNode(narrationStep(1), 4, [writer]);

    expect(
      screen.getByLabelText('Voiceover source: narration written in step 1 (Write narration)'),
    ).toBeTruthy();
  });

  it('falls back to "Step N" only for a VALID order that has no label to show', () => {
    // Order 5 is 1-based and therefore configured; `allSteps` simply has no entry for it. This is
    // the fallback that caused T-004 — it must apply to a valid order and to nothing else.
    renderNode(narrationStep(5), 4);

    expect(screen.getByText('Narration from step 5')).toBeTruthy();
    expect(screen.getByLabelText('Voiceover source: narration written in step 5 (Step 5)')).toBeTruthy();
  });

  /** The badge's accessible name and its visible text in the unconfigured state. */
  // Plain-language copy since the customer e2e findings (the old "NarrationPlan · not configured"
  // badge read as engineer jargon); the constants are the node's own exports so they cannot drift.
  const UNCONFIGURED_LABEL = VOICEOVER_UNCONFIGURED_LABEL;
  const UNCONFIGURED_SUMMARY = VOICEOVER_UNCONFIGURED_SUMMARY;

  it('states the unconfigured state in text while no NarrationWriter step is chosen', () => {
    renderNode(narrationStep(null), 4);

    // Asserted on the visible text, never on a colour.
    expect(screen.getByLabelText(UNCONFIGURED_LABEL).textContent).toBe(UNCONFIGURED_SUMMARY);
    expect(screen.queryByLabelText('Voiceover source: From the script')).toBeNull();
  });

  it('reports it as unconfigured when the step field is absent entirely', () => {
    renderNode(narrationStep(undefined), 4);

    expect(screen.getByLabelText(UNCONFIGURED_LABEL).textContent).toBe(UNCONFIGURED_SUMMARY);
  });

  it('renders step order 0 as unconfigured, never as "step 0" (T-004 regression)', () => {
    // T-004: the summary derived configured-ness from whether a display label could be produced,
    // and the `Step N` fallback always could — so order 0 read as a configured "step 0" while the
    // editor called the very same source unconfigured.
    renderNode(narrationStep(0), 4);

    const badge = screen.getByLabelText(UNCONFIGURED_LABEL);
    expect(badge.textContent).toBe(UNCONFIGURED_SUMMARY);
    // The `Step N` fallback that caused T-004 would have produced exactly this text.
    expect(badge.textContent).not.toMatch(/step 0/i);
  });

  it.each([0, -1, 1.5, Number.NaN])(
    'agrees with getVoiceoverSourceError on the invalid step order %j',
    (order) => {
      // The node's verdict must come from the validator alone — never from a display fallback — so
      // the two layers cannot disagree about which sources are configured.
      expect(
        getVoiceoverSourceError({ kind: 'NarrationPlan', narrationWriterStepOrder: order }),
      ).not.toBeNull();

      renderNode(narrationStep(order), 4);

      expect(screen.getByLabelText(UNCONFIGURED_LABEL).textContent).toBe(UNCONFIGURED_SUMMARY);
    },
  );
});
