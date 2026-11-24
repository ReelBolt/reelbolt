// @vitest-environment jsdom
/**
 * The DesignSystem step form must only ever emit a config the server's `DesignSystemStepConfig.Validate()`
 * accepts: exactly the options object matching `op`, the other two absent. The harness is controlled
 * (real state round trip) and starts from the config a freshly-added step really has.
 */
import { render, screen, fireEvent, cleanup, within } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { useState } from 'react';
import { MantineProvider } from '@mantine/core';
import {
  DesignSystemStepConfig,
  createDefaultDesignSystemStepConfig,
} from './DesignSystemStepConfig';
import type { DesignSystemStepConfig as Config, StepType } from '@/lib/types/workflow';
import type { StepData } from './WorkflowStepList';

afterEach(() => cleanup());

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query: string) => ({
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
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });
  Element.prototype.scrollIntoView = vi.fn();
});

function step(id: string, stepType: StepType, label: string): StepData {
  return {
    id, label, agentDefinitionId: 'agent-123', stepType,
    conditionExpression: null, loopSourceExpression: null, loopTargetStepOrder: null,
    maxIterations: 3, minScore: null, inputMappingJson: null, agentInputContextMode: null,
    cacheMode: null, selectedPriorStepOrders: [], trueBranchStepOrder: null,
    falseBranchStepOrder: null, parallelAgentIds: [], extractConfig: null,
    videoAnalyzeConfig: null, videoCompileConfig: null, thumbnailGenerateConfig: null,
    videoGenerateConfig: null, voiceoverConfig: null,
  };
}

const STEPS: StepData[] = [
  step('s1', 'DesignSystem', 'Read files'),
  step('s2', 'DesignSystem', 'The step under edit'),
];

/** The server's rule (DesignSystemStepConfig.Validate), reduced to the shape part the form controls. */
function serverShapeErrors(c: Config): string[] {
  const errors: string[] = [];
  const has = { Parse: c.parse != null, Compose: c.compose != null, Require: c.require != null };
  (['Parse', 'Compose', 'Require'] as const).forEach((op) => {
    if (op === c.op && !has[op]) errors.push(`${op} options required`);
    if (op !== c.op && has[op]) errors.push(`${op} options not allowed on ${c.op}`);
  });
  if (c.compose && c.compose.parseStepOrder < 1) errors.push('parseStepOrder < 1');
  if (c.require && !c.require.requireDesign && !c.require.requireProductMap) errors.push('require both false');
  return errors;
}

function renderControlled(initial: Config = createDefaultDesignSystemStepConfig()) {
  const seen: Config[] = [];
  function Harness() {
    const [config, setConfig] = useState<Config>(initial);
    return (
      <DesignSystemStepConfig
        config={config}
        onChange={(c) => { seen.push(c); setConfig(c); }}
        allSteps={STEPS}
        currentStepIndex={1}
      />
    );
  }
  render(<MantineProvider><Harness /></MantineProvider>);
  return seen;
}

async function chooseOp(label: string) {
  fireEvent.click(screen.getByRole('combobox', { name: 'What this step does' }));
  fireEvent.click(await screen.findByRole('option', { name: label, hidden: true }));
}

describe('DesignSystemStepConfig', () => {
  it('a new step is a valid Parse config', () => {
    const c = createDefaultDesignSystemStepConfig();
    expect(serverShapeErrors(c)).toEqual([]);
  });

  it('switching to Compose emits exactly {op, version, compose}', async () => {
    const seen = renderControlled();
    await chooseOp('Combine and save the profile');

    const last = seen[seen.length - 1];
    expect(serverShapeErrors(last)).toEqual([]);
    expect(last).toEqual({
      op: 'Compose',
      version: 1,
      compose: { parseStepOrder: 1, designFallbackStepOrders: null, productFallbackStepOrders: null, ingest: true },
    });
    expect('parse' in last && last.parse != null).toBe(false);
  });

  it('switching to Require emits exactly {op, version, require}', async () => {
    const seen = renderControlled();
    await chooseOp('Require an approved profile');

    const last = seen[seen.length - 1];
    expect(serverShapeErrors(last)).toEqual([]);
    expect(last).toEqual({
      op: 'Require',
      version: 1,
      require: { requireDesign: true, requireProductMap: true },
    });
  });

  it('editing a Compose option after switching keeps the config valid', async () => {
    const seen = renderControlled();
    await chooseOp('Combine and save the profile');
    fireEvent.click(screen.getByRole('switch', { name: 'Save the profile for this project' }));

    const last = seen[seen.length - 1];
    expect(serverShapeErrors(last)).toEqual([]);
    expect(last.compose?.ingest).toBe(false);
    expect(last.parse ?? null).toBeNull();
  });

  it('editing a Require option keeps the config valid', async () => {
    const seen = renderControlled();
    await chooseOp('Require an approved profile');
    fireEvent.click(screen.getByRole('switch', { name: 'Needs an approved product profile' }));

    const last = seen[seen.length - 1];
    expect(serverShapeErrors(last)).toEqual([]);
    expect(last.require).toEqual({ requireDesign: true, requireProductMap: false });
  });

  it('round trip Parse -> Compose -> Parse -> Require stays valid at every emission', async () => {
    const seen = renderControlled();
    await chooseOp('Combine and save the profile');
    await chooseOp('Read my design files');
    await chooseOp('Require an approved profile');

    expect(seen.length).toBe(3);
    for (const c of seen) expect(serverShapeErrors(c)).toEqual([]);
    expect(seen.map((c) => c.op)).toEqual(['Compose', 'Parse', 'Require']);
  });

  it('Parse edits keep the Parse shape', () => {
    const seen = renderControlled();
    fireEvent.click(screen.getByRole('switch', { name: 'Read the product structure (pages, parts, libraries)' }));

    const last = seen[seen.length - 1];
    expect(serverShapeErrors(last)).toEqual([]);
    expect(last.parse?.includeProductMap).toBe(false);
    expect(within(document.body).queryByText('Choose at least one thing to read.')).toBeNull();
  });
});
