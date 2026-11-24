// @vitest-environment jsdom
/**
 * Customer e2e findings 23/30: step settings open in the side drawer (nodes get an
 * `onOpenSettings` handler, no inline expansion), the canvas fills the viewport, and the mouse
 * wheel pans rather than zooms.
 */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, cleanup, act } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ReactFlowProvider } from '@xyflow/react';
import { FlowchartBuilder, CANVAS_INTERACTION, CANVAS_HEIGHT } from './FlowchartBuilder';
import type { StepData } from './WorkflowStepList';

const { captured } = vi.hoisted(() => ({
  captured: { flowProps: null as Record<string, unknown> | null, setNodes: null as null | ((n: unknown) => void), panelProps: null as Record<string, unknown> | null },
}));

vi.mock('@xyflow/react', async () => {
  const actual = await vi.importActual('@xyflow/react');
  return {
    ...actual,
    ReactFlow: (props: Record<string, unknown> & { children: React.ReactNode }) => {
      captured.flowProps = props;
      return <div data-testid="react-flow">{props.children}</div>;
    },
    Background: () => null,
    useNodesState: () => [[], (n: unknown) => captured.setNodes?.(n), vi.fn()],
    useEdgesState: () => [[], vi.fn(), vi.fn()],
    useReactFlow: () => ({ fitView: vi.fn() }),
  };
});

vi.mock('@/lib/hooks/use-agents', () => ({ useAgents: () => ({ data: [], isLoading: false }) }));
vi.mock('./StepConfigPanel', () => ({
  StepConfigPanel: (props: Record<string, unknown>) => {
    captured.panelProps = props;
    return props.opened ? <div data-testid="config-drawer">drawer</div> : null;
  },
}));
vi.mock('./WorkflowStepList', () => ({ WorkflowStepList: () => null }));

function makeStep(id: string): StepData {
  return {
    id, label: id, agentDefinitionId: 'a', stepType: 'Agent',
    conditionExpression: null, loopSourceExpression: null, loopTargetStepOrder: null, maxIterations: 3, minScore: null,
    inputMappingJson: null, agentInputContextMode: null, cacheMode: null, selectedPriorStepOrders: [],
    trueBranchStepOrder: null, falseBranchStepOrder: null, parallelAgentIds: [],
    extractConfig: null, videoAnalyzeConfig: null, videoCompileConfig: null, videoGenerateConfig: null, voiceoverConfig: null,
    thumbnailGenerateConfig: null,
  } as StepData;
}

describe('FlowchartBuilder canvas', () => {
  beforeEach(() => {
    captured.flowProps = null;
    captured.panelProps = null;
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      value: vi.fn().mockImplementation((query) => ({
        matches: false, media: query, onchange: null,
        addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
      })),
    });
  });
  afterEach(() => cleanup());

  it('pans on mouse wheel and only zooms with Ctrl/Cmd or a pinch', () => {
    expect(CANVAS_INTERACTION.panOnScroll).toBe(true);
    expect(CANVAS_INTERACTION.zoomOnScroll).toBe(false);
    expect(CANVAS_INTERACTION.zoomOnPinch).toBe(true);
    expect(CANVAS_INTERACTION.zoomActivationKeyCode).toEqual(['Control', 'Meta']);

    render(<MantineProvider><ReactFlowProvider><FlowchartBuilder steps={[]} onChange={vi.fn()} /></ReactFlowProvider></MantineProvider>);
    expect(captured.flowProps?.panOnScroll).toBe(true);
    expect(captured.flowProps?.zoomOnScroll).toBe(false);
  });

  it('fills the viewport rather than a fixed 900px box', () => {
    render(<MantineProvider><ReactFlowProvider><FlowchartBuilder steps={[]} onChange={vi.fn()} /></ReactFlowProvider></MantineProvider>);
    expect(CANVAS_HEIGHT).toContain('100dvh');
    expect(CANVAS_HEIGHT).not.toContain('900px');
    expect((screen.getByTestId('workflow-canvas') as HTMLElement).style.height).toContain('100dvh');
  });

  it("hands every node an onOpenSettings that opens the side drawer for that step", () => {
    const nodeBatches: { id: string; data: Record<string, unknown> }[][] = [];
    captured.setNodes = (n) => nodeBatches.push(n as never);
    const steps = [makeStep('a'), makeStep('b'), makeStep('c')];
    render(<MantineProvider><ReactFlowProvider><FlowchartBuilder steps={steps} onChange={vi.fn()} /></ReactFlowProvider></MantineProvider>);

    const nodes = nodeBatches.at(-1)!;
    expect(nodes.map((n) => n.id)).toEqual(['a', 'b', 'c']);
    for (const n of nodes) {
      expect(typeof n.data.onOpenSettings).toBe('function');
      // No inline expansion: nothing is ever marked expanded or pinned.
      expect(n.data.expanded).toBe(false);
      expect(n.data.pinned).toBe(false);
    }
    expect(screen.queryByTestId('config-drawer')).toBeNull();

    act(() => (nodes[2].data.onOpenSettings as (id: string) => void)('c'));
    expect(screen.getByTestId('config-drawer')).toBeTruthy();
    expect(captured.panelProps?.currentStepIndex).toBe(2);
  });
});
