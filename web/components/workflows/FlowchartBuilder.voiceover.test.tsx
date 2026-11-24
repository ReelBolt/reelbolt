// @vitest-environment jsdom
'use client';

/**
 * T-071 / T-070 regression coverage — the "a newly added Voiceover step still carries an empty
 * agentDefinitionId" defect, proven RED against the pre-T-070 FlowchartBuilder.
 *
 * A NEW file rather than an edit to FlowchartBuilder.test.tsx: that suite mocks `useAgents` to `[]`,
 * so it can never exercise the placeholder-agent assignment this file is about, and rewriting its
 * mocks would put the 281-test baseline at risk. The mocking pattern below mirrors it otherwise
 * (react-flow mock keeping Panel/Controls real, AddStepModal mocked, node components mocked,
 * StepConfigPanel mocked).
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { Mock } from 'vitest';
import { render, screen, waitFor, cleanup, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ReactFlowProvider } from '@xyflow/react';
import { FlowchartBuilder } from './FlowchartBuilder';
import type { StepData } from './WorkflowStepList';

// Keep Controls/MiniMap/Panel REAL (unmocked), exactly like FlowchartBuilder.test.tsx — the "Add
// Step" button lives inside a real @xyflow/react <Panel>.
vi.mock('@xyflow/react', async () => {
  const actual = await vi.importActual('@xyflow/react');
  return {
    ...actual,
    ReactFlow: ({ children }: { children: React.ReactNode }) => <div data-testid="react-flow">{children}</div>,
    Background: () => <div data-testid="background">Background</div>,
    useNodesState: () => [[], vi.fn(), vi.fn()],
    useEdgesState: () => [[], vi.fn(), vi.fn()],
    useReactFlow: () => ({ fitView: vi.fn() }),
  };
});

// The two built-in deterministic placeholder agent rows T-070 relies on. `vi.hoisted` because the
// vi.mock factory below is hoisted above every module-scope binding.
const { MOCK_AGENTS } = vi.hoisted(() => ({
  MOCK_AGENTS: [
    { id: 'vt-agent', agentType: 'VideoTransform' },
    { id: 'et-agent', agentType: 'ExtractTransform' },
  ],
}));

vi.mock('@/lib/hooks/use-agents', () => ({
  useAgents: () => ({ data: MOCK_AGENTS, isLoading: false }),
}));

vi.mock('./flowchart-layout', () => ({
  deriveFlowGraph: () => ({
    nodes: [],
    edges: [],
    selectedNodeId: null,
    expandedStepId: null,
    pinnedStepIds: new Set(),
  }),
}));

// The real AddStepModal renders the full type list inside a Mantine Modal; this file only needs the
// two entry points handleAddStep is being tested through.
vi.mock('./AddStepModal', () => ({
  AddStepModal: ({ opened, onClose, onAdd }: { opened: boolean; onClose: () => void; onAdd: (type: string) => void }) =>
    opened ? (
      <div data-testid="add-step-modal">
        <button data-testid="add-voiceover" onClick={() => onAdd('Voiceover')}>Add Voiceover</button>
        <button data-testid="add-videoanalyze" onClick={() => onAdd('VideoAnalyze')}>Add VideoAnalyze</button>
        <button onClick={onClose}>Close</button>
      </div>
    ) : null,
}));

// Node components are irrelevant here and expensive to render.
vi.mock('./nodes/AgentNode', () => ({ AgentNode: () => null }));
vi.mock('./nodes/ConditionalNode', () => ({ ConditionalNode: () => null }));
vi.mock('./nodes/ForEachNode', () => ({ ForEachNode: () => null }));
vi.mock('./nodes/ReviewLoopNode', () => ({ ReviewLoopNode: () => null }));
vi.mock('./nodes/ParallelNode', () => ({ ParallelNode: () => null }));
vi.mock('./nodes/ExtractNode', () => ({ ExtractNode: () => null }));
vi.mock('./nodes/VideoAnalyzeNode', () => ({ VideoAnalyzeNode: () => null }));
vi.mock('./nodes/VideoCompileNode', () => ({ VideoCompileNode: () => null }));
vi.mock('./nodes/VideoGenerateNode', () => ({ VideoGenerateNode: () => null }));
vi.mock('./nodes/VoiceoverNode', () => ({ VoiceoverNode: () => null }));
vi.mock('./nodes/RoomNode', () => ({ RoomNode: () => null }));

vi.mock('./StepConfigPanel', () => ({ StepConfigPanel: () => null }));
vi.mock('./WorkflowStepList', () => ({ WorkflowStepList: () => null }));

describe('FlowchartBuilder — adding a Voiceover step assigns the placeholder agent', () => {
  beforeEach(() => {
    cleanup();
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
    Object.defineProperty(global, 'ResizeObserver', {
      writable: true,
      value: class ResizeObserver {
        observe = vi.fn();
        unobserve = vi.fn();
        disconnect = vi.fn();
      },
    });
  });

  afterEach(() => {
    cleanup();
  });

  function renderBuilder(onChange: Mock) {
    return render(
      <MantineProvider>
        <ReactFlowProvider>
          <FlowchartBuilder steps={[]} onChange={onChange} />
        </ReactFlowProvider>
      </MantineProvider>,
    );
  }

  async function addStepThroughModal(onChange: Mock, buttonTestId: string) {
    renderBuilder(onChange);
    fireEvent.click(screen.getByRole('button', { name: 'Add Step' }));
    await waitFor(() => {
      expect(screen.getByTestId('add-step-modal')).toBeTruthy();
    });
    fireEvent.click(screen.getByTestId(buttonTestId));
    await waitFor(() => {
      expect(onChange).toHaveBeenCalledTimes(1);
    });
    return onChange.mock.calls[0][0] as StepData[];
  }

  it('assigns the VideoTransform placeholder agent when a Voiceover step is added', async () => {
    const onChange = vi.fn();

    const nextSteps = await addStepThroughModal(onChange, 'add-voiceover');

    // Pre-T-070 this is the whole defect: agentDefinitionId was '' and the create-workflow guard
    // rejected the saved workflow with "Step 1 requires an agent".
    expect(nextSteps).toHaveLength(1);
    expect(nextSteps[0].stepType).toBe('Voiceover');
    expect(nextSteps[0].agentDefinitionId).toBe('vt-agent');
  });

  it('assigns the VideoTransform placeholder agent when a VideoAnalyze step is added (sibling control)', async () => {
    const onChange = vi.fn();

    const nextSteps = await addStepThroughModal(onChange, 'add-videoanalyze');

    // Control: this path was already correct before T-070 and must stay correct after it. If this
    // assertion goes red, the Voiceover fix broke a sibling video step type — not a test bug.
    expect(nextSteps).toHaveLength(1);
    expect(nextSteps[0].stepType).toBe('VideoAnalyze');
    expect(nextSteps[0].agentDefinitionId).toBe('vt-agent');
  });
});
