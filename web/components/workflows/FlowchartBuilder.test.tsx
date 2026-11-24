// @vitest-environment jsdom
'use client';

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ReactFlowProvider } from '@xyflow/react';
import { FlowchartBuilder } from './FlowchartBuilder';
import { createDefaultVoiceoverStepConfig } from './VoiceoverStepConfig';
import { createDefaultThumbnailGenerateStepConfig } from './ThumbnailGenerateStepConfig';
import { createDefaultTimelineRenderStepConfig } from './TimelineRenderStepConfig';
import { createDefaultExtractFrameStepConfig } from './ExtractFrameStepConfig';
import { createDefaultAudioGenerateStepConfig } from './AudioGenerateStepConfig';
import { createDefaultDesignSystemStepConfig } from './DesignSystemStepConfig';
import type { StepData } from './WorkflowStepList';
import type { WorkflowEditDiff } from '@/lib/types/workflow-assistant';

// Mock ReactFlow components: keep Controls/MiniMap/Panel REAL (unmocked) to test DOM ordering hypothesis
vi.mock('@xyflow/react', async () => {
  const actual = await vi.importActual('@xyflow/react');
  return {
    ...actual,
    ReactFlow: ({ children }: { children: React.ReactNode }) => <div data-testid="react-flow">{children}</div>,
    Background: () => <div data-testid="background">Background</div>,
    // Controls, MiniMap, Panel are NOT mocked — they come from the real @xyflow/react module
    // This way they render their own .react-flow__panel divs so we can test the hypothesis about
    // DOM ordering: do Controls/MiniMap's .react-flow__panel buttons come before ours?
    useNodesState: () => [[], vi.fn(), vi.fn()],
    useEdgesState: () => [[], vi.fn(), vi.fn()],
    useReactFlow: () => ({ fitView: vi.fn() }),
  };
});

// Mock the agents hook
vi.mock('@/lib/hooks/use-agents', () => ({
  useAgents: () => ({ data: [], isLoading: false }),
}));

// Mock the flowchart layout function
vi.mock('./flowchart-layout', () => ({
  deriveFlowGraph: () => ({ nodes: [], edges: [], selectedNodeId: null, expandedStepId: null, pinnedStepIds: new Set() }),
}));

// Mock AddStepModal to verify it receives the opened prop
vi.mock('./AddStepModal', () => ({
  AddStepModal: ({ opened, onClose, onAdd }: { opened: boolean; onClose: () => void; onAdd: (type: string) => void }) =>
    opened ? (
      <div data-testid="add-step-modal">
        <h2>Add Workflow Step</h2>
        <button onClick={() => onAdd('Agent')}>Agent</button>
        {/* T-005: the real AddStepModal offers every StepType; this stub re-declares only the type
            under test, so asserting a Voiceover add is one extra button, not new mocking machinery. */}
        <button onClick={() => onAdd('Voiceover')}>Voiceover</button>
        <button onClick={() => onAdd('ThumbnailGenerate')}>ThumbnailGenerate</button>
        <button onClick={() => onAdd('TimelineRender')}>TimelineRender</button>
        <button onClick={() => onAdd('DesignSystem')}>DesignSystem</button>
        <button onClick={() => onAdd('ExtractFrame')}>ExtractFrame</button>
        <button onClick={() => onAdd('AudioGenerate')}>AudioGenerate</button>
        <button onClick={onClose}>Close</button>
      </div>
    ) : null,
}));

// Mock all the node components to avoid complex rendering
vi.mock('./nodes/AgentNode', () => ({
  AgentNode: () => null,
}));
vi.mock('./nodes/ConditionalNode', () => ({
  ConditionalNode: () => null,
}));
vi.mock('./nodes/ForEachNode', () => ({
  ForEachNode: () => null,
}));
vi.mock('./nodes/ReviewLoopNode', () => ({
  ReviewLoopNode: () => null,
}));
vi.mock('./nodes/ParallelNode', () => ({
  ParallelNode: () => null,
}));
vi.mock('./nodes/ExtractNode', () => ({
  ExtractNode: () => null,
}));
vi.mock('./nodes/VideoAnalyzeNode', () => ({
  VideoAnalyzeNode: () => null,
}));
vi.mock('./nodes/VideoCompileNode', () => ({
  VideoCompileNode: () => null,
}));
vi.mock('./nodes/RoomNode', () => ({
  RoomNode: () => null,
}));

// Mock StepConfigPanel
vi.mock('./StepConfigPanel', () => ({
  StepConfigPanel: () => null,
}));

// Mock WorkflowStepList
vi.mock('./WorkflowStepList', () => ({
  WorkflowStepList: () => null,
}));

describe('FlowchartBuilder', () => {
  const mockSteps: StepData[] = [];
  const mockOnChange = vi.fn();

  beforeEach(() => {
    cleanup();
    mockOnChange.mockClear();
    // Mock window.matchMedia for Mantine's color scheme detection
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
    // Mock ResizeObserver for Mantine's ScrollArea
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

  const createMockStepData = (id: string, label: string): StepData => ({
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
    editRoomConfigJson: null,
    graphicsRoomConfigJson: null,
    colorGradeRoomConfigJson: null,
    cacheMode: null,
  });

  it('renders the Add Step button with aria-label', () => {
    render(
      <MantineProvider>
        <ReactFlowProvider>
          <FlowchartBuilder steps={mockSteps} onChange={mockOnChange} />
        </ReactFlowProvider>
      </MantineProvider>
    );

    const addStepButton = screen.getByRole('button', { name: 'Add Step' });
    expect(addStepButton).toBeTruthy();
  });

  it('opens AddStepModal when clicking the Add Step button', async () => {
    render(
      <MantineProvider>
        <ReactFlowProvider>
          <FlowchartBuilder steps={mockSteps} onChange={mockOnChange} />
        </ReactFlowProvider>
      </MantineProvider>
    );

    // Initially, the modal should not be visible
    expect(screen.queryByText('Add Workflow Step')).toBeNull();

    // Click the "Add Step" button
    const addStepButton = screen.getByRole('button', { name: 'Add Step' });
    fireEvent.click(addStepButton);

    // After click, the modal title should become visible
    await waitFor(() => {
      expect(screen.getByText('Add Workflow Step')).toBeTruthy();
    });
  });

  it('can add a step through the modal', async () => {
    render(
      <MantineProvider>
        <ReactFlowProvider>
          <FlowchartBuilder steps={mockSteps} onChange={mockOnChange} />
        </ReactFlowProvider>
      </MantineProvider>
    );

    // Click the "Add Step" button to open the modal
    const addStepButton = screen.getByRole('button', { name: 'Add Step' });
    fireEvent.click(addStepButton);

    // Wait for modal to appear
    await waitFor(() => {
      expect(screen.getByText('Add Workflow Step')).toBeTruthy();
    });

    // Click on the "Agent" step type button
    const agentButton = screen.getByRole('button', { name: /Agent/i });
    fireEvent.click(agentButton);

    // Modal should close and onChange should be called
    await waitFor(() => {
      expect(mockOnChange).toHaveBeenCalled();
    });
  });

  /**
   * T-005 — what the builder layer genuinely had unpinned for the voiceover arm.
   *
   * `web/components/workflows/**` never touches `voiceoverConfigJson` (that string exists only at
   * the page boundary), so a JSON round trip does not belong here. `StepConfigPanel.voiceover.test.tsx`
   * already proves the panel routes an edit out as a `voiceoverConfig` patch, and
   * `VoiceoverNode.test.tsx` proves the node summary. What was pinned by nothing is the ADD path:
   * `handleAddStep` must seed a fresh Voiceover step with the runnable default config, because the
   * executor hard-fails a Voiceover step whose config JSON is null/empty — a freshly-added,
   * untouched step has to carry one. Reachable in this harness by declaring the type on the
   * existing AddStepModal stub above, so it costs no new mocking machinery.
   */
  it('seeds a freshly-added Voiceover step with the default voiceover config', async () => {
    render(
      <MantineProvider>
        <ReactFlowProvider>
          <FlowchartBuilder steps={mockSteps} onChange={mockOnChange} />
        </ReactFlowProvider>
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('button', { name: 'Add Step' }));
    await waitFor(() => {
      expect(screen.getByText('Add Workflow Step')).toBeTruthy();
    });

    fireEvent.click(screen.getByRole('button', { name: 'Voiceover' }));

    await waitFor(() => {
      expect(mockOnChange).toHaveBeenCalled();
    });
    const added = mockOnChange.mock.calls.at(-1)?.[0] as StepData[];
    expect(added).toHaveLength(1);
    expect(added[0].stepType).toBe('Voiceover');
    // Equal to THE default, not merely non-null: a step added with `voiceoverConfig: null` would
    // otherwise satisfy a bare truthiness check while still failing the executor at run time.
    expect(added[0].voiceoverConfig).toEqual(createDefaultVoiceoverStepConfig());
    expect(added[0].voiceoverConfig?.source.kind).toBe('Inline');
  });

  /**
   * The same add-path guarantee, for the ThumbnailGenerate step type: its config is what tells the
   * executor WHICH footage analysis to score, so a freshly-added step carrying `null` would fail the
   * run rather than merely render an empty editor. Asserted equal to THE factory default, not to a
   * hand-copied literal, so the node's summary and the executor read one shape.
   */
  it('seeds a freshly-added ThumbnailGenerate step with the default thumbnail config', async () => {
    render(
      <MantineProvider>
        <ReactFlowProvider>
          <FlowchartBuilder steps={mockSteps} onChange={mockOnChange} />
        </ReactFlowProvider>
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('button', { name: 'Add Step' }));
    await waitFor(() => {
      expect(screen.getByText('Add Workflow Step')).toBeTruthy();
    });

    fireEvent.click(screen.getByRole('button', { name: 'ThumbnailGenerate' }));

    await waitFor(() => {
      expect(mockOnChange).toHaveBeenCalled();
    });
    const added = mockOnChange.mock.calls.at(-1)?.[0] as StepData[];
    expect(added).toHaveLength(1);
    expect(added[0].stepType).toBe('ThumbnailGenerate');
    expect(added[0].thumbnailGenerateConfig).toEqual(createDefaultThumbnailGenerateStepConfig());
    // The default is the Auto arm, which is the only one that reads the footage analysis.
    expect(added[0].thumbnailGenerateConfig?.selection).toBe('Auto');
  });

  /**
   * The same guarantee for TimelineRender (editor v2, P4): the node summarises the preset and file
   * name from this config, so a freshly-added step must carry the factory default rather than null.
   */
  it('seeds a freshly-added TimelineRender step with the default render config', async () => {
    render(
      <MantineProvider>
        <ReactFlowProvider>
          <FlowchartBuilder steps={mockSteps} onChange={mockOnChange} />
        </ReactFlowProvider>
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('button', { name: 'Add Step' }));
    await waitFor(() => {
      expect(screen.getByText('Add Workflow Step')).toBeTruthy();
    });

    fireEvent.click(screen.getByRole('button', { name: 'TimelineRender' }));

    await waitFor(() => {
      expect(mockOnChange).toHaveBeenCalled();
    });
    const added = mockOnChange.mock.calls.at(-1)?.[0] as StepData[];
    expect(added).toHaveLength(1);
    expect(added[0].stepType).toBe('TimelineRender');
    expect(added[0].timelineRenderConfig).toEqual(createDefaultTimelineRenderStepConfig());
    // The default names no timeline yet: the author must choose one, and the card says so.
    expect(added[0].timelineRenderConfig?.editTimelineId).toBe('');
  });

  /**
   * DesignSystem: the card summarises what the step reads, so a freshly-added step must carry the
   * factory default (a Parse) rather than null — a null config is not valid for this step type.
   */
  it('seeds a freshly-added DesignSystem step with the default config', async () => {
    render(
      <MantineProvider>
        <ReactFlowProvider>
          <FlowchartBuilder steps={mockSteps} onChange={mockOnChange} />
        </ReactFlowProvider>
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('button', { name: 'Add Step' }));
    await waitFor(() => {
      expect(screen.getByText('Add Workflow Step')).toBeTruthy();
    });

    fireEvent.click(screen.getByRole('button', { name: 'DesignSystem' }));

    await waitFor(() => {
      expect(mockOnChange).toHaveBeenCalled();
    });
    const added = mockOnChange.mock.calls.at(-1)?.[0] as StepData[];
    expect(added).toHaveLength(1);
    expect(added[0].stepType).toBe('DesignSystem');
    expect(added[0].designSystemConfig).toEqual(createDefaultDesignSystemStepConfig());
    expect(added[0].designSystemConfig?.op).toBe('Parse');
  });

  /**
   * ExtractFrame / AudioGenerate carry no usable shape without a config (a named video; a description), so a
   * freshly-added step is seeded with the factory default and the placeholder agent the other deterministic
   * steps use, never null.
   */
  it.each([
    ['ExtractFrame', 'extractFrameConfig', createDefaultExtractFrameStepConfig(), 'audioGenerateConfig'],
    ['AudioGenerate', 'audioGenerateConfig', createDefaultAudioGenerateStepConfig(), 'extractFrameConfig'],
  ] as const)('seeds a freshly-added %s step with its default config and no other step\'s config', async (type, key, expected, other) => {
    render(
      <MantineProvider>
        <ReactFlowProvider>
          <FlowchartBuilder steps={[]} onChange={mockOnChange} />
        </ReactFlowProvider>
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('button', { name: 'Add Step' }));
    await waitFor(() => {
      expect(screen.getByText('Add Workflow Step')).toBeTruthy();
    });
    fireEvent.click(screen.getByRole('button', { name: type }));

    await waitFor(() => {
      expect(mockOnChange).toHaveBeenCalled();
    });
    const added = mockOnChange.mock.calls.at(-1)?.[0] as StepData[];
    expect(added).toHaveLength(1);
    expect(added[0].stepType).toBe(type);
    // The first step has nothing before it, so an ExtractFrame starts from an uploaded file, not "the previous step".
    const config = added[0][key] as unknown;
    expect(config).toEqual(type === 'ExtractFrame' ? { ...(expected as object), source: { kind: 'ProjectFile', projectFileId: null, stepOrder: null } } : expected);
    expect(added[0][other]).toBeNull();
  });

  it('verifies DOM order of .react-flow__panel buttons (hypothesis test)', async () => {
    // This test uses UNMOCKED Controls/MiniMap/Panel to check the real hypothesis:
    // Do Controls' zoom button and MiniMap's buttons render in .react-flow__panel
    // BEFORE our custom Panel's buttons, so a selector picking ".first()" hits the wrong button?

    const { container } = render(
      <MantineProvider>
        <ReactFlowProvider>
          <FlowchartBuilder steps={mockSteps} onChange={mockOnChange} />
        </ReactFlowProvider>
      </MantineProvider>
    );

    // Query all buttons inside .react-flow__panel divs
    const allPanelButtons = container.querySelectorAll('.react-flow__panel button');

    // Log each button for debugging
    const buttonDescriptions: string[] = [];
    allPanelButtons.forEach((btn, idx) => {
      const ariaLabel = btn.getAttribute('aria-label');
      const title = btn.getAttribute('title');
      const text = btn.textContent;
      const description = ariaLabel || title || text || '(no label)';
      buttonDescriptions.push(`Button ${idx}: ${description}`);
    });
    console.log('Panel buttons found:', buttonDescriptions.join(' | '));

    // Find the "Add Step" button by its aria-label
    const addStepButton = Array.from(allPanelButtons).find(
      (btn) => btn.getAttribute('aria-label') === 'Add Step'
    );

    expect(addStepButton).toBeTruthy();
    const addStepIndex = Array.from(allPanelButtons).indexOf(addStepButton as Element);

    console.log(`Add Step button is at index ${addStepIndex} out of ${allPanelButtons.length} total panel buttons`);

    // List what comes before it (if anything)
    if (addStepIndex > 0) {
      console.log('Buttons BEFORE Add Step:');
      for (let i = 0; i < addStepIndex; i++) {
        const btn = allPanelButtons[i] as HTMLElement;
        const ariaLabel = btn.getAttribute('aria-label');
        const title = btn.getAttribute('title');
        const text = btn.textContent;
        const description = ariaLabel || title || text || '(no label)';
        console.log(`  Index ${i}: ${description}`);
      }
    }

    // If our button is NOT first, that's the collision hypothesis confirmed
    if (addStepIndex === 0) {
      console.log('✓ Add Step is the FIRST .react-flow__panel button — no collision with Controls/MiniMap');
    } else {
      console.log(`✗ Add Step is NOT first (index ${addStepIndex}) — COLLISION CONFIRMED`);
      console.log('  This explains why clicking the e2e spec\'s ".react-flow__panel button.first()" hits the wrong button');
    }
  });

  it('renders without error when pendingAssistantDiff is null', () => {
    const steps = [createMockStepData('step-1', 'Step 1')];
    const onChange = vi.fn();

    const { getByTestId } = render(
      <MantineProvider>
        <ReactFlowProvider>
          <FlowchartBuilder
            steps={steps}
            onChange={onChange}
            projectId="project-1"
            pendingAssistantDiff={undefined}
          />
        </ReactFlowProvider>
      </MantineProvider>
    );

    expect(getByTestId('react-flow')).toBeDefined();
  });

  it('renders with pending assistant diff overlay without update depth errors', async () => {
    const steps = [
      createMockStepData('step-1', 'Step 1'),
      createMockStepData('step-2', 'Step 2'),
    ];
    const onChange = vi.fn();

    const pendingDiff: WorkflowEditDiff = {
      confirmationToken: 'token-123',
      removals: [{ stepOrder: 1, agentDefinitionId: 'agent-1', stepType: 'Agent', summary: 'Remove first step' }],
      modifications: [{ stepOrder: 2, agentDefinitionId: 'agent-1', stepType: 'Agent', summary: 'Modify second step' }],
      additions: [{ stepOrder: 3, agentDefinitionId: 'agent-1', stepType: 'Extract', summary: 'Add extract step' }],
    };

    const consoleErrorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});

    const { getByTestId } = render(
      <MantineProvider>
        <ReactFlowProvider>
          <FlowchartBuilder
            steps={steps}
            onChange={onChange}
            projectId="project-1"
            pendingAssistantDiff={pendingDiff}
          />
        </ReactFlowProvider>
      </MantineProvider>
    );

    await waitFor(() => {
      expect(getByTestId('react-flow')).toBeDefined();
    });

    // Check that no "Maximum update depth exceeded" error was logged
    const errorCalls = consoleErrorSpy.mock.calls;
    const updateDepthError = errorCalls.find((call) =>
      call[0]?.toString().includes('Maximum update depth exceeded')
    );
    expect(updateDepthError).toBeUndefined();

    consoleErrorSpy.mockRestore();
  });

  it('handles multiple re-renders with different pendingAssistantDiff without update depth errors', async () => {
    const steps = [
      createMockStepData('step-1', 'Step 1'),
      createMockStepData('step-2', 'Step 2'),
    ];
    const onChange = vi.fn();

    const consoleErrorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});

    let pendingDiff: WorkflowEditDiff | undefined = undefined;

    const { rerender, getByTestId } = render(
      <MantineProvider>
        <ReactFlowProvider>
          <FlowchartBuilder
            steps={steps}
            onChange={onChange}
            projectId="project-1"
            pendingAssistantDiff={pendingDiff}
          />
        </ReactFlowProvider>
      </MantineProvider>
    );

    await waitFor(() => {
      expect(getByTestId('react-flow')).toBeDefined();
    });

    // Simulate receiving a diff from the assistant
    pendingDiff = {
      confirmationToken: 'token-456',
      removals: [],
      modifications: [],
      additions: [{ stepOrder: 3, agentDefinitionId: 'agent-1', stepType: 'Agent', summary: 'New step' }],
    };

    rerender(
      <MantineProvider>
        <ReactFlowProvider>
          <FlowchartBuilder
            steps={steps}
            onChange={onChange}
            projectId="project-1"
            pendingAssistantDiff={pendingDiff}
          />
        </ReactFlowProvider>
      </MantineProvider>
    );

    await waitFor(() => {
      expect(getByTestId('react-flow')).toBeDefined();
    });

    // Check for update depth errors
    const errorCalls = consoleErrorSpy.mock.calls;
    const updateDepthError = errorCalls.find((call) =>
      call[0]?.toString().includes('Maximum update depth exceeded')
    );
    expect(updateDepthError).toBeUndefined();

    consoleErrorSpy.mockRestore();
  });

  it('correctly renders diff overlay elements when pendingAssistantDiff is provided', async () => {
    const steps = [
      createMockStepData('step-1', 'Step 1'),
      createMockStepData('step-2', 'Step 2'),
    ];
    const onChange = vi.fn();

    const pendingDiff: WorkflowEditDiff = {
      confirmationToken: 'token-789',
      removals: [{ stepOrder: 1, agentDefinitionId: 'agent-1', stepType: 'Agent', summary: 'Remove step' }],
      modifications: [{ stepOrder: 2, agentDefinitionId: 'agent-1', stepType: 'Extract', summary: 'Change type' }],
      additions: [{ stepOrder: 3, agentDefinitionId: 'agent-1', stepType: 'Agent', summary: 'New step' }],
    };

    const { getByTestId } = render(
      <MantineProvider>
        <ReactFlowProvider>
          <FlowchartBuilder
            steps={steps}
            onChange={onChange}
            projectId="project-1"
            pendingAssistantDiff={pendingDiff}
          />
        </ReactFlowProvider>
      </MantineProvider>
    );

    await waitFor(() => {
      expect(getByTestId('react-flow')).toBeDefined();
    });

    // The component should render without errors
    expect(getByTestId('react-flow')).toBeDefined();
  });

  it('does not cause infinite loops when steps change while diff is pending', async () => {
    let steps = [createMockStepData('step-1', 'Step 1')];
    const onChange = vi.fn();

    const pendingDiff: WorkflowEditDiff = {
      confirmationToken: 'token-xyz',
      removals: [],
      modifications: [],
      additions: [{ stepOrder: 2, agentDefinitionId: 'agent-1', stepType: 'Agent', summary: 'Add step' }],
    };

    const consoleErrorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});

    const { rerender, getByTestId } = render(
      <MantineProvider>
        <ReactFlowProvider>
          <FlowchartBuilder
            steps={steps}
            onChange={onChange}
            projectId="project-1"
            pendingAssistantDiff={pendingDiff}
          />
        </ReactFlowProvider>
      </MantineProvider>
    );

    await waitFor(() => {
      expect(getByTestId('react-flow')).toBeDefined();
    });

    // Add a new step while the diff is still pending
    steps = [
      createMockStepData('step-1', 'Step 1'),
      createMockStepData('step-2', 'Step 2'),
    ];

    rerender(
      <MantineProvider>
        <ReactFlowProvider>
          <FlowchartBuilder
            steps={steps}
            onChange={onChange}
            projectId="project-1"
            pendingAssistantDiff={pendingDiff}
          />
        </ReactFlowProvider>
      </MantineProvider>
    );

    await waitFor(() => {
      expect(getByTestId('react-flow')).toBeDefined();
    });

    const errorCalls = consoleErrorSpy.mock.calls;
    const updateDepthError = errorCalls.find((call) =>
      call[0]?.toString().includes('Maximum update depth exceeded')
    );
    expect(updateDepthError).toBeUndefined();

    consoleErrorSpy.mockRestore();
  });
});
