// @vitest-environment jsdom

/**
 * Regression tests for the assistant's diff overlay on the canvas:
 *  1. clicking a diff badge/ghost must never open the step config panel with an undefined step;
 *  2. the overlay's node highlight must disappear once the proposal is discarded.
 *
 * React Flow itself is replaced by a stub that renders each node as a button carrying its
 * className and forwards clicks to `onNodeClick`, exactly as the real canvas would.
 */

import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest';
import { render, screen, cleanup, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ReactFlowProvider } from '@xyflow/react';
import type { Node } from '@xyflow/react';
import { FlowchartBuilder } from './FlowchartBuilder';
import type { StepData } from './WorkflowStepList';
import type { WorkflowEditDiff } from '@/lib/types/workflow-assistant';
import diffClasses from '@/components/assistant/diff.module.css';

vi.mock('@xyflow/react', async () => {
  const actual = await vi.importActual<typeof import('@xyflow/react')>('@xyflow/react');
  return {
    ...actual,
    ReactFlow: ({
      nodes,
      onNodeClick,
    }: {
      nodes: Node[];
      onNodeClick?: (e: unknown, node: Node) => void;
    }) => (
      <div data-testid="react-flow">
        {nodes.map((n) => (
          <button key={n.id} data-testid={`node-${n.id}`} className={n.className} onClick={(e) => onNodeClick?.(e, n)}>
            {n.id}
          </button>
        ))}
      </div>
    ),
    Background: () => null,
    Controls: () => null,
    MiniMap: () => null,
    Panel: () => null,
  };
});

vi.mock('@/lib/hooks/use-agents', () => ({ useAgents: () => ({ data: [], isLoading: false }) }));

vi.mock('./flowchart-layout', () => ({
  deriveFlowGraph: (steps: StepData[]) => ({
    nodes: steps.map((s, i) => ({ id: s.id, type: 'agent', position: { x: 0, y: i * 200 }, data: { step: s } })),
    edges: [],
  }),
}));

vi.mock('./AddStepModal', () => ({ AddStepModal: () => null }));

// Reads a field off `step` the way the real panel does, so an undefined step throws here too.
vi.mock('./StepConfigPanel', () => ({
  StepConfigPanel: ({ step, opened }: { step: StepData; opened: boolean }) =>
    opened ? <div data-testid="step-config-panel">{step.label}</div> : null,
}));

const step = (id: string, label: string): StepData => ({
  id,
  label,
  agentDefinitionId: 'agent-1',
  stepType: 'Agent',
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
});

const steps = [step('step-1', 'Analyze'), step('step-2', 'Script')];

const diff: WorkflowEditDiff = {
  confirmationToken: 'tok',
  removals: [{ stepOrder: 1, agentDefinitionId: 'agent-1', stepType: 'Agent', summary: 'Analyze' }],
  modifications: [{ stepOrder: 2, agentDefinitionId: 'agent-1', stepType: 'Agent', summary: 'Script v2' }],
  additions: [{ stepOrder: 3, agentDefinitionId: 'agent-1', stepType: 'ReviewLoop', summary: 'Review' }],
};

function renderBuilder(pendingAssistantDiff?: WorkflowEditDiff) {
  return render(
    <MantineProvider>
      <ReactFlowProvider>
        <FlowchartBuilder steps={steps} onChange={vi.fn()} projectId="p" pendingAssistantDiff={pendingAssistantDiff} />
      </ReactFlowProvider>
    </MantineProvider>,
  );
}

describe('FlowchartBuilder assistant diff overlay', () => {
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
  afterEach(() => cleanup());

  it('outlines changed/removed steps and adds badge + ghost nodes', () => {
    renderBuilder(diff);
    expect(screen.getByTestId('node-step-1').className).toContain(diffClasses.nodeRemoved);
    expect(screen.getByTestId('node-step-2').className).toContain(diffClasses.nodeChanged);
    expect(screen.getByTestId('node-diff-badge-1')).toBeDefined();
    expect(screen.getByTestId('node-diff-badge-2')).toBeDefined();
    expect(screen.getByTestId('node-diff-ghost-3')).toBeDefined();
  });

  it('ignores clicks on diff badges and ghosts instead of opening the config panel on a missing step', () => {
    renderBuilder(diff);

    // These nodes sit after the steps in the node array; indexing by node position used to
    // resolve them to steps[2..4] === undefined and crash the panel.
    fireEvent.click(screen.getByTestId('node-diff-badge-1'));
    fireEvent.click(screen.getByTestId('node-diff-badge-2'));
    fireEvent.click(screen.getByTestId('node-diff-ghost-3'));
    expect(screen.queryByTestId('step-config-panel')).toBeNull();

    // A real step still opens its own panel.
    fireEvent.click(screen.getByTestId('node-step-2'));
    expect(screen.getByTestId('step-config-panel').textContent).toBe('Script');
  });

  it('clears the node highlight and overlay nodes when the proposal is discarded', () => {
    const { rerender } = renderBuilder(diff);
    expect(screen.getByTestId('node-step-2').className).toContain(diffClasses.nodeChanged);

    rerender(
      <MantineProvider>
        <ReactFlowProvider>
          <FlowchartBuilder steps={steps} onChange={vi.fn()} projectId="p" pendingAssistantDiff={undefined} />
        </ReactFlowProvider>
      </MantineProvider>,
    );

    for (const id of ['step-1', 'step-2']) {
      const cls = screen.getByTestId(`node-${id}`).className;
      expect(cls).not.toContain(diffClasses.nodeChanged);
      expect(cls).not.toContain(diffClasses.nodeRemoved);
    }
    expect(screen.queryByTestId('node-diff-badge-1')).toBeNull();
    expect(screen.queryByTestId('node-diff-ghost-3')).toBeNull();
  });
});
