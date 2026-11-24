import { describe, it, expect } from 'vitest';
import { deriveFlowGraph, type FlowGraphUIState } from './flowchart-layout';
import type { StepData } from './WorkflowStepList';
import type { StepType } from '@/lib/types/workflow';

// Helper to create a minimal StepData for testing
function createTestStep(id: string, stepType: string = 'Agent'): StepData {
  return {
    id,
    label: `Step ${id}`,
    agentDefinitionId: 'agent-123',
    stepType: stepType as StepType,
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
    voiceoverConfig: null,
    videoGenerateConfig: null,
  };
}

describe('deriveFlowGraph', () => {
  it('should create nodes with dagre-computed positions when no positions override', () => {
    const steps = [createTestStep('step-1'), createTestStep('step-2'), createTestStep('step-3')];
    const ui: FlowGraphUIState = {
      expandedStepId: null,
      pinnedStepIds: new Set(),
    };

    const result = deriveFlowGraph(steps, ui);

    expect(result.nodes).toHaveLength(3);
    // Positions should be computed by dagre, not hardcoded
    expect(result.nodes[0].position).toBeDefined();
    expect(result.nodes[1].position).toBeDefined();
    expect(result.nodes[2].position).toBeDefined();
  });

  it('should use ui.positions override when present', () => {
    const steps = [createTestStep('step-1'), createTestStep('step-2')];
    const ui: FlowGraphUIState = {
      expandedStepId: null,
      pinnedStepIds: new Set(),
      positions: {
        'step-1': { x: 100, y: 200 },
        // step-2 has no override, should use dagre layout
      },
    };

    const result = deriveFlowGraph(steps, ui);

    expect(result.nodes[0].position).toEqual({ x: 100, y: 200 }); // overridden
    expect(result.nodes[1].position).toBeDefined(); // dagre-computed
    expect(result.nodes[1].position).not.toEqual({ x: 100, y: 200 }); // should be different from override
  });

  it('should yield identical node positions when changing expandedStepId only', () => {
    const steps = [createTestStep('step-1'), createTestStep('step-2')];

    const ui1: FlowGraphUIState = {
      expandedStepId: 'step-1',
      pinnedStepIds: new Set(),
    };

    const ui2: FlowGraphUIState = {
      expandedStepId: 'step-2',
      pinnedStepIds: new Set(),
    };

    const result1 = deriveFlowGraph(steps, ui1);
    const result2 = deriveFlowGraph(steps, ui2);

    // Positions should be identical even when expandedStepId changes
    // because positions are determined by dagre layout, not expandedStepId
    expect(result1.nodes[0].position).toEqual(result2.nodes[0].position);
    expect(result1.nodes[1].position).toEqual(result2.nodes[1].position);

    // But zIndex should differ based on expandedStepId
    expect(result1.nodes[0].zIndex).toBe(1000); // expanded
    expect(result2.nodes[0].zIndex).toBe(0); // not expanded
    expect(result1.nodes[1].zIndex).toBe(0); // not expanded
    expect(result2.nodes[1].zIndex).toBe(1000); // expanded
  });

  it('should create edges between consecutive steps', () => {
    const steps = [createTestStep('step-1'), createTestStep('step-2'), createTestStep('step-3')];
    const ui: FlowGraphUIState = {
      expandedStepId: null,
      pinnedStepIds: new Set(),
    };

    const result = deriveFlowGraph(steps, ui);

    expect(result.edges).toHaveLength(2);
    expect(result.edges[0]).toMatchObject({
      id: 'estep-1-step-2',
      source: 'step-1',
      target: 'step-2',
      type: 'smoothstep',
      animated: true,
    });
    expect(result.edges[1]).toMatchObject({
      id: 'estep-2-step-3',
      source: 'step-2',
      target: 'step-3',
      type: 'smoothstep',
      animated: true,
    });
  });

  it('should not create edges for the first step', () => {
    const steps = [createTestStep('step-1')];
    const ui: FlowGraphUIState = {
      expandedStepId: null,
      pinnedStepIds: new Set(),
    };

    const result = deriveFlowGraph(steps, ui);

    expect(result.edges).toHaveLength(0);
  });

  it('should set high zIndex for expanded or pinned steps', () => {
    const steps = [createTestStep('step-1'), createTestStep('step-2'), createTestStep('step-3')];
    const ui: FlowGraphUIState = {
      expandedStepId: 'step-1',
      pinnedStepIds: new Set(['step-3']),
    };

    const result = deriveFlowGraph(steps, ui);

    expect(result.nodes[0].zIndex).toBe(1000); // expanded
    expect(result.nodes[1].zIndex).toBe(0); // neither
    expect(result.nodes[2].zIndex).toBe(1000); // pinned
  });

  it('should return the same node positions when called twice with identical inputs', () => {
    const steps = [createTestStep('step-1'), createTestStep('step-2')];
    const ui: FlowGraphUIState = {
      expandedStepId: null,
      pinnedStepIds: new Set(),
    };

    const result1 = deriveFlowGraph(steps, ui);
    const result2 = deriveFlowGraph(steps, ui);

    // Positions should be identical
    expect(result1.nodes[0].position).toEqual(result2.nodes[0].position);
    expect(result1.nodes[1].position).toEqual(result2.nodes[1].position);
  });

  it('should include projectId in node data when provided', () => {
    const steps = [createTestStep('step-1')];
    const ui: FlowGraphUIState = {
      expandedStepId: null,
      pinnedStepIds: new Set(),
    };

    const result = deriveFlowGraph(steps, ui, 'project-123');

    expect(result.nodes[0].data.projectId).toBe('project-123');
  });

  it('should create two edges for Conditional step with both branch fields set', () => {
    const steps = [
      createTestStep('step-1', 'Agent'),
      { ...createTestStep('step-2', 'Conditional'), trueBranchStepOrder: '3', falseBranchStepOrder: '4' },
      createTestStep('step-3', 'Agent'),
      createTestStep('step-4', 'Agent'),
    ];
    const ui: FlowGraphUIState = {
      expandedStepId: null,
      pinnedStepIds: new Set(),
    };

    const result = deriveFlowGraph(steps, ui);

    // Should have edges: step-1 to step-2 (incoming), step-2 to step-3 (true branch), step-2 to step-4 (false branch)
    const edgesFromStep2 = result.edges.filter(e => e.source === 'step-2');
    expect(edgesFromStep2).toHaveLength(2);

    const trueEdge = edgesFromStep2.find(e => e.target === 'step-3');
    const falseEdge = edgesFromStep2.find(e => e.target === 'step-4');

    expect(trueEdge).toBeDefined();
    expect(falseEdge).toBeDefined();
    expect(trueEdge?.label).toBe('True');
    expect(falseEdge?.label).toBe('False');
  });

  it('should create a back-edge for ReviewLoop step with loopTargetStepOrder', () => {
    const steps = [
      createTestStep('step-1', 'Agent'),
      { ...createTestStep('step-2', 'ReviewLoop'), loopTargetStepOrder: 1, maxIterations: 5, minScore: 0.8 },
      createTestStep('step-3', 'Agent'),
    ];
    const ui: FlowGraphUIState = {
      expandedStepId: null,
      pinnedStepIds: new Set(),
    };

    const result = deriveFlowGraph(steps, ui);

    // Should have a back-edge from step-2 to step-1
    const backEdge = result.edges.find(e => e.source === 'step-2' && e.target === 'step-1');
    expect(backEdge).toBeDefined();
    expect(backEdge?.type).not.toBe('smoothstep'); // Should be different type for back-edge
    expect(backEdge?.label).toContain('≤5'); // Should include maxIterations
    expect(backEdge?.label).toContain('score >= 0.8'); // Should include minScore
  });
});
