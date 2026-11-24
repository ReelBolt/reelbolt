import { Node, Edge, MarkerType } from '@xyflow/react';
import * as dagre from '@dagrejs/dagre';
import { englishWorkflowsT } from './english-t';
import type { StepData } from './WorkflowStepList';
import type { StepType } from '@/lib/types/workflow';

/** Maps a workflow StepType to its React Flow node type key. */
const STEP_TYPE_TO_NODE_TYPE: Record<StepType, string> = {
  Agent: 'agent',
  Conditional: 'conditional',
  ForEach: 'forEach',
  ReviewLoop: 'reviewLoop',
  Parallel: 'parallel',
  Extract: 'extract',
  VideoAnalyze: 'videoAnalyze',
  VideoCompile: 'videoCompile',
  VideoGenerate: 'videoGenerate',
  Voiceover: 'voiceover',
  EditRoom: 'editRoom',
  GraphicsRoom: 'graphicsRoom',
  ColorGradeRoom: 'colorGradeRoom',
  ThumbnailGenerate: 'thumbnailGenerate',
  // A system step (one agent making the requested changes): the plain agent card renders it.
  Revise: 'agent',
  // A system step too (one agent designing one graphic for the picture editor).
  DesignGraphic: 'agent',
  DailiesRoom: 'dailiesRoom',
  TimelineRender: 'timelineRender',
  DesignSystem: 'designSystem',
  ExtractFrame: 'extractFrame',
  AudioGenerate: 'audioGenerate',
  ImageGenerate: 'imageGenerate',
  AudioCleanup: 'audioCleanup',
  VideoEnhance: 'videoEnhance',
  PinInsert: 'pinInsert',
};

/** Layout box per step card (StepNodeShell is 300px wide; the tallest card is about 230px). */
export const NODE_LAYOUT_WIDTH = 300;
export const NODE_LAYOUT_HEIGHT = 230;

export interface FlowGraphUIState {
  expandedStepId: string | null;
  pinnedStepIds: ReadonlySet<string>;
  positions?: Record<string, { x: number; y: number }>;
}

export interface FlowGraphResult {
  nodes: Node[];
  edges: Edge[];
}

/**
 * Resolves a step-order number (1-based, as string or number) to a step id.
 * Returns null if unresolvable or out of range.
 */
function resolveStepOrder(orderValue: string | number | null | undefined, steps: StepData[]): string | null {
  if (orderValue == null) return null;

  const order = typeof orderValue === 'string' ? parseInt(orderValue, 10) : orderValue;
  if (isNaN(order) || order < 1 || order > steps.length) return null;

  const targetStep = steps[order - 1];
  return targetStep?.id ?? null;
}

/**
 * Pure function that derives React Flow nodes and edges from workflow steps.
 * No side effects: returns the same nodes/edges for the same inputs.
 *
 * Positions are calculated using:
 * 1. ui.positions[stepId] if present
 * 2. Otherwise: dagre layered top-to-bottom auto-layout
 */
export function deriveFlowGraph(
  steps: StepData[],
  ui: FlowGraphUIState,
  projectId?: string,
  /** Translator for the edge labels (`workflows.flow`); English when omitted. */
  translate: (key: string, values?: Record<string, string | number>) => string = englishWorkflowsT('flow'),
): FlowGraphResult {
  const newNodes: Node[] = [];
  const newEdges: Edge[] = [];

  // First pass: create all nodes
  steps.forEach((step, index) => {
    const nodeType = STEP_TYPE_TO_NODE_TYPE[step.stepType] ?? 'agent';
    const isExpanded = ui.expandedStepId === step.id;
    const isPinned = ui.pinnedStepIds.has(step.id);

    newNodes.push({
      id: step.id,
      type: nodeType,
      position: { x: 0, y: 0 }, // Will be set by dagre or ui.positions override
      // React Flow v12 honors a node's own zIndex on its wrapper element — the only thing that
      // can actually outrank a later-in-DOM sibling node's paint order once a card expands.
      zIndex: isExpanded || isPinned ? 1000 : 0,
      data: {
        step,
        stepNumber: index + 1,
        allSteps: steps,
        currentStepIndex: index,
        projectId,
        expanded: isExpanded,
        pinned: isPinned,
        // Note: handlers (onExpandChange, onTogglePin, onChange, onRemove) are NOT included here
        // because they must be injected by the component that uses this function.
        // This keeps the derived graph pure and testable.
      },
    });
  });

  // Second pass: create edges based on step type and branch fields
  steps.forEach((step, index) => {
    const stepType = step.stepType;

    if (stepType === 'Conditional') {
      // Conditional: two outgoing edges for true/false branches
      const trueBranchId = resolveStepOrder(step.trueBranchStepOrder, steps);
      const falseBranchId = resolveStepOrder(step.falseBranchStepOrder, steps);

      if (trueBranchId) {
        newEdges.push({
          id: `e${step.id}-${trueBranchId}-true`,
          source: step.id,
          target: trueBranchId,
          label: translate('true'),
          type: 'smoothstep',
          animated: true,
          markerEnd: {
            type: MarkerType.ArrowClosed,
            color: '#8b5cf6',
          },
          style: {
            stroke: '#8b5cf6',
            strokeWidth: 2,
          },
        });
      }

      if (falseBranchId) {
        newEdges.push({
          id: `e${step.id}-${falseBranchId}-false`,
          source: step.id,
          target: falseBranchId,
          label: translate('false'),
          type: 'smoothstep',
          animated: true,
          markerEnd: {
            type: MarkerType.ArrowClosed,
            color: '#8b5cf6',
          },
          style: {
            stroke: '#8b5cf6',
            strokeWidth: 2,
          },
        });
      }
    } else if (stepType === 'ForEach') {
      // ForEach: dashed "reads from" edges + outgoing to next
      // Dashed "reads from" edges from selectedPriorStepOrders
      for (const priorOrder of step.selectedPriorStepOrders || []) {
        const priorId = resolveStepOrder(priorOrder, steps);
        if (priorId) {
          newEdges.push({
            id: `e${priorId}-${step.id}-reads`,
            source: priorId,
            target: step.id,
            type: 'smoothstep',
            animated: true,
            style: {
              stroke: '#8b5cf6',
              strokeWidth: 2,
              strokeDasharray: '5,5',
            },
            markerEnd: {
              type: MarkerType.ArrowClosed,
              color: '#8b5cf6',
            },
          });
        }
      }

      // Add outgoing edge to next step
      if (index < steps.length - 1) {
        newEdges.push({
          id: `e${step.id}-${steps[index + 1].id}`,
          source: step.id,
          target: steps[index + 1].id,
          type: 'smoothstep',
          animated: true,
          markerEnd: {
            type: MarkerType.ArrowClosed,
            color: '#8b5cf6',
          },
          style: {
            stroke: '#8b5cf6',
            strokeWidth: 2,
          },
        });
      }
    } else if (stepType === 'ReviewLoop') {
      // ReviewLoop: back-edge to loopTargetStepOrder + forward edge to next
      const loopTargetId = resolveStepOrder(step.loopTargetStepOrder, steps);
      if (loopTargetId) {
        const loopLabel = step.minScore != null
          ? translate('loopWithScore', { max: step.maxIterations, score: step.minScore })
          : translate('loop', { max: step.maxIterations });

        newEdges.push({
          id: `e${step.id}-${loopTargetId}-loop`,
          source: step.id,
          target: loopTargetId,
          label: loopLabel,
          type: 'bezier', // Different type for visual distinction
          animated: true,
          markerEnd: {
            type: MarkerType.ArrowClosed,
            color: '#d97706', // Different color for back-edge
          },
          style: {
            stroke: '#d97706',
            strokeWidth: 2,
          },
        });
      }

      // Add outgoing edge to next step
      if (index < steps.length - 1) {
        newEdges.push({
          id: `e${step.id}-${steps[index + 1].id}`,
          source: step.id,
          target: steps[index + 1].id,
          type: 'smoothstep',
          animated: true,
          markerEnd: {
            type: MarkerType.ArrowClosed,
            color: '#8b5cf6',
          },
          style: {
            stroke: '#8b5cf6',
            strokeWidth: 2,
          },
        });
      }
    } else {
      // Default: Parallel, Agent, Extract, VideoAnalyze, VideoCompile, VideoGenerate, Room steps
      // Outgoing edge to next step
      if (index < steps.length - 1) {
        newEdges.push({
          id: `e${step.id}-${steps[index + 1].id}`,
          source: step.id,
          target: steps[index + 1].id,
          type: 'smoothstep',
          animated: true,
          markerEnd: {
            type: MarkerType.ArrowClosed,
            color: '#8b5cf6',
          },
          style: {
            stroke: '#8b5cf6',
            strokeWidth: 2,
          },
        });
      }
    }
  });

  // Calculate layout using dagre
  const graph = new dagre.graphlib.Graph({ directed: true });
  // Sized to the compact step cards (StepNodeShell: 300px wide, ~200px tall with summary chips).
  // Settings live in a side drawer, so a card never grows past this and never overlaps the next.
  graph.setGraph({ rankdir: 'TB', nodesep: 50, ranksep: 60 });
  graph.setDefaultEdgeLabel(() => ({}));

  // Add nodes to graph
  newNodes.forEach((node) => {
    graph.setNode(node.id, { width: NODE_LAYOUT_WIDTH, height: NODE_LAYOUT_HEIGHT });
  });

  // Add edges to graph
  newEdges.forEach((edge) => {
    graph.setEdge(edge.source, edge.target);
  });

  // Run layout
  dagre.layout(graph);

  // Apply layout positions, respecting ui.positions overrides
  newNodes.forEach((node) => {
    if (ui.positions?.[node.id]) {
      // Use override position
      node.position = ui.positions[node.id];
    } else {
      // Use dagre-computed position
      const dagreNode = graph.node(node.id);
      if (dagreNode) {
        node.position = { x: dagreNode.x - NODE_LAYOUT_WIDTH / 2, y: dagreNode.y - NODE_LAYOUT_HEIGHT / 2 };
      }
    }
  });

  return { nodes: newNodes, edges: newEdges };
}
