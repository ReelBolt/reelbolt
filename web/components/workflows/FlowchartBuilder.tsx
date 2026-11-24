 'use client';

import { useCallback, useEffect, useMemo, useState, useRef } from 'react';
import {
  ReactFlow,
  Controls,
  Background,
  BackgroundVariant,
  Panel,
  useNodesState,
  useEdgesState,
  Edge,
  Node,
  NodeTypes,
  ReactFlowProvider,
  MiniMap,
  MarkerType,
  Handle,
  Position,
  useReactFlow,
  type ReactFlowProps,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { Stack, ActionIcon, Tooltip } from '@mantine/core';
import { useMediaQuery, useReducedMotion } from '@mantine/hooks';
import { IconPlus, IconLayoutGrid } from '@tabler/icons-react';
import { AgentNode } from './nodes/AgentNode';
import { ConditionalNode } from './nodes/ConditionalNode';
import { ForEachNode } from './nodes/ForEachNode';
import { ReviewLoopNode } from './nodes/ReviewLoopNode';
import { ParallelNode } from './nodes/ParallelNode';
import { ExtractNode } from './nodes/ExtractNode';
import { VideoAnalyzeNode } from './nodes/VideoAnalyzeNode';
import { VideoCompileNode } from './nodes/VideoCompileNode';
import { ThumbnailGenerateNode } from './nodes/ThumbnailGenerateNode';
import { TimelineRenderNode } from './nodes/TimelineRenderNode';
import { DesignSystemNode } from './nodes/DesignSystemNode';
import { ExtractFrameNode } from './nodes/ExtractFrameNode';
import { AudioGenerateNode } from './nodes/AudioGenerateNode';
import { ImageGenerateNode } from './nodes/ImageGenerateNode';
import { AudioCleanupNode } from './nodes/AudioCleanupNode';
import { VideoEnhanceNode } from './nodes/VideoEnhanceNode';
import { PinInsertNode } from './nodes/PinInsertNode';
import { VideoGenerateNode } from './nodes/VideoGenerateNode';
import { VoiceoverNode } from './nodes/VoiceoverNode';
import { RoomNode } from './nodes/RoomNode';
import { AddStepModal } from './AddStepModal';
import { StepConfigPanel } from './StepConfigPanel';
import { computeStepFieldDiff } from './field-diff';
import { fieldPathsForMessage } from '@/lib/utils/step-errors';
import { camelizeKeys } from '@/lib/utils/config-case';
import { createDefaultExtractStepConfig } from './ExtractStepConfig';
import { createDefaultDesignSystemStepConfig } from './DesignSystemStepConfig';
import { createDefaultExtractFrameStepConfig, EXTRACT_FRAME_FIRST_STEP_SOURCE } from './ExtractFrameStepConfig';
import { createDefaultAudioGenerateStepConfig } from './AudioGenerateStepConfig';
import { createDefaultImageGenerateStepConfig } from './ImageGenerateStepConfig';
import { createDefaultAudioCleanupStepConfig, AUDIO_CLEANUP_FIRST_STEP_SOURCE } from './AudioCleanupStepConfig';
import { createDefaultVideoEnhanceStepConfig, VIDEO_ENHANCE_FIRST_STEP_SOURCE } from './VideoEnhanceStepConfig';
import { createDefaultPinInsertStepConfig } from './PinInsertStepConfig';
import { createDefaultVideoAnalyzeStepConfig, NEW_CLIP_SOURCE_REF } from './VideoAnalyzeStepConfig';
import { createDefaultVideoCompileStepConfig } from './VideoCompileStepConfig';
import { createDefaultThumbnailGenerateStepConfig } from './ThumbnailGenerateStepConfig';
import { createDefaultTimelineRenderStepConfig } from './TimelineRenderStepConfig';
import { createDefaultVideoGenerateStepConfig } from './VideoGenerateStepConfig';
import { createDefaultVoiceoverStepConfig } from './VoiceoverStepConfig';
import { ROOM_STEP_KINDS, isRoomStepType, createDefaultRoomConfigJson } from './RoomStepConfigEditor';
import { useAgents } from '@/lib/hooks/use-agents';
import { WorkflowStepList } from './WorkflowStepList';
import { deriveFlowGraph, type FlowGraphUIState } from './flowchart-layout';
import type { StepData } from './WorkflowStepList';
import type { StepType } from '@/lib/types/workflow';
import type { WorkflowEditDiff } from '@/lib/types/workflow-assistant';
import { describeDiff, type DiffKind } from '@/components/assistant/diff-model';
import { DiffTag } from '@/components/assistant/DiffTag';
import diffClasses from '@/components/assistant/diff.module.css';
import restyle from './graph-restyle.module.css';
import { useT } from '@/i18n';

type DiffOverlayNodeData = {
  kind: DiffKind;
  /** Badge: optional text after the tag (a changed step's new name). Ghost: the new step's name. */
  name?: string;
  /** Ghost only: "Step 4 · Review Loop". */
  meta?: string;
  variant: 'badge' | 'ghost';
};

/**
 * Canvas half of the assistant's diff: a tag pinned to an existing step (changed/removed), or a
 * dashed "ghost" card for a proposed new step. Uses the same DiffTag and tokens as the review tray
 * in the assistant drawer so both read as one system. Purely decorative for pointer/keyboard: the
 * nodes are created non-selectable and non-focusable, and clicks on them are ignored.
 */
function DiffBadgeNode({ data }: { data: DiffOverlayNodeData }) {
  const tBuild = useT('workflows.builder');
  const { kind, name, meta, variant } = data;
  if (variant === 'ghost') {
    // Handles are required for React Flow to route the dashed edge into (and between) ghosts.
    return (
      <>
        <Handle type="target" position={Position.Top} className={diffClasses.ghostHandle} isConnectable={false} />
        <div className={`${diffClasses.kind} ${diffClasses.ghost}`} data-kind={kind}>
          <DiffTag kind={kind} label={tBuild('proposedStep')} />
          <span className={diffClasses.ghostName}>{name}</span>
          {meta && <span className={diffClasses.ghostMeta}>{meta}</span>}
        </div>
        <Handle type="source" position={Position.Bottom} className={diffClasses.ghostHandle} isConnectable={false} />
      </>
    );
  }
  return (
    <div className={`${diffClasses.kind} ${diffClasses.badge}`} data-kind={kind}>
      <DiffTag kind={kind} />
      {name && <span className={diffClasses.badgeName}>→ {name}</span>}
    </div>
  );
}

const NO_PINNED_STEPS: ReadonlySet<string> = new Set();

/** Canvas fills the viewport below the page header; never shorter than 520px. */
export const CANVAS_HEIGHT = 'max(520px, calc(100dvh - 180px))';

/**
 * Wheel and trackpad scrolling PAN the canvas; zooming needs Ctrl/Cmd + wheel or a pinch. The
 * React Flow default (wheel zooms) fought the page scroll: scrolling down the page over the canvas
 * zoomed it instead, and the last steps of a long workflow were hard to reach. Exported so the
 * test pins the choice.
 */
export const CANVAS_INTERACTION: Pick<ReactFlowProps, 'panOnScroll' | 'zoomOnScroll' | 'zoomOnPinch' | 'zoomActivationKeyCode' | 'minZoom' | 'maxZoom'> = {
  panOnScroll: true,
  zoomOnScroll: false,
  zoomOnPinch: true,
  zoomActivationKeyCode: ['Control', 'Meta'],
  minZoom: 0.3,
  maxZoom: 1.5,
};

const nodeTypes = {
  agent: AgentNode,
  conditional: ConditionalNode,
  forEach: ForEachNode,
  reviewLoop: ReviewLoopNode,
  parallel: ParallelNode,
  extract: ExtractNode,
  videoAnalyze: VideoAnalyzeNode,
  videoCompile: VideoCompileNode,
  videoGenerate: VideoGenerateNode,
  voiceover: VoiceoverNode,
  // All three room step types (EditRoom/GraphicsRoom/ColorGradeRoom) share one node component —
  // they differ only in accent, copy, and which StepData field carries their config, which
  // RoomNode reads from ROOM_STEP_KINDS keyed by the step's own type.
  editRoom: RoomNode,
  graphicsRoom: RoomNode,
  colorGradeRoom: RoomNode,
  thumbnailGenerate: ThumbnailGenerateNode,
  dailiesRoom: RoomNode,
  timelineRender: TimelineRenderNode,
  designSystem: DesignSystemNode,
  extractFrame: ExtractFrameNode,
  audioGenerate: AudioGenerateNode,
  imageGenerate: ImageGenerateNode,
  audioCleanup: AudioCleanupNode,
  videoEnhance: VideoEnhanceNode,
  pinInsert: PinInsertNode,
  diffBadge: DiffBadgeNode,
} satisfies NodeTypes;

interface FlowchartBuilderProps {
  steps: StepData[];
  onChange: (steps: StepData[]) => void;
  /** Threaded into VideoAnalyzeNode's data so its VideoSourceRef picker can list this project's video files. */
  projectId?: string;
  /** Optional pending assistant-proposed workflow edit diff to display as overlay on canvas. */
  pendingAssistantDiff?: WorkflowEditDiff;
  /** Server validation messages from a rejected save, by 1-based step order. */
  stepErrors?: Map<number, string[]>;
}

export function FlowchartBuilder({ steps, onChange, projectId, pendingAssistantDiff, stepErrors }: FlowchartBuilderProps) {
  const tBuild = useT('workflows.builder');
  const tf = useT('workflows.flow');
  const [addModalOpen, setAddModalOpen] = useState(false);
  const [nodes, setNodes, onNodesChange] = useNodesState<Node>([]);
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>([]);
  const { data: agents, isLoading: agentsLoading } = useAgents();

  // Track manually-dragged node positions to persist them across state changes
  const [dragPositions, setDragPositions] = useState<Record<string, { x: number; y: number }>>({});

  // StepConfigPanel state: which step is currently being configured. Step settings ALWAYS open in
  // this side drawer: nodes used to grow a tall inline form that overlapped the next steps and could
  // pan the whole canvas off-screen, so cards are now compact summaries with a Settings button.
  const [selectedStepIndex, setSelectedStepIndex] = useState<number | null>(null);
  const [configPanelOpen, setConfigPanelOpen] = useState(false);
  const selectedNodeRef = useRef<string | null>(null);

  const stepsRef = useRef(steps);
  useEffect(() => {
    stepsRef.current = steps;
  }, [steps]);
  const openSettings = useCallback((stepId: string) => {
    const stepIndex = stepsRef.current.findIndex((s) => s.id === stepId);
    if (stepIndex < 0) return;
    selectedNodeRef.current = stepId;
    setSelectedStepIndex(stepIndex);
    setConfigPanelOpen(true);
  }, []);

  // Keep a stable reference to onChange to avoid useMemo recomputation when parent re-creates callback
  const onChangeRef = useRef(onChange);
  useEffect(() => {
    onChangeRef.current = onChange;
  }, [onChange]);

  // Derive pure nodes and edges from steps and UI state, then inject handlers
  const { nodesWithHandlers, derivedEdges } = useMemo(() => {
    const uiState: FlowGraphUIState = {
      expandedStepId: null,
      pinnedStepIds: NO_PINNED_STEPS,
      positions: dragPositions,
    };

    const { nodes: derivedNodes, edges: derivedEdges } = deriveFlowGraph(steps, uiState, projectId, tf);

    // Inject handlers into node data (must be done after deriving to keep derive pure)
    const nodesWithHandlers = derivedNodes.map((node, index) => ({
      ...node,
      data: {
        ...node.data,
        onOpenSettings: openSettings,
        onChange: (updates: Partial<StepData>) => {
          const newSteps = [...steps];
          newSteps[index] = { ...newSteps[index], ...updates };
          onChangeRef.current(newSteps);
        },
        onRemove: () => {
          onChangeRef.current(steps.filter((_, i) => i !== index));
        },
      },
    }));

    return { nodesWithHandlers, derivedEdges };
  }, [steps, projectId, dragPositions, openSettings, tf]);

  // Apply pending assistant diff overlay if present, on top of the base derivation.
  //
  // Every overlaid node is a NEW object. The overlay used to write `style` straight onto the node
  // objects shared with `nodesWithHandlers`, so the highlight survived Discard: once the diff was
  // cleared this memo returned those same (mutated) objects and the red/orange borders lingered.
  const { finalNodes, finalEdges } = useMemo(() => {
    // A step the server rejected on save gets the same red outline a removal does.
    const markRejected = (list: Node[]): Node[] =>
      !stepErrors || stepErrors.size === 0
        ? list
        : list.map((n) => {
            const i = steps.findIndex((s) => s.id === n.id);
            return i >= 0 && stepErrors.has(i + 1)
              ? { ...n, className: [n.className, diffClasses.nodeRemoved].filter(Boolean).join(' ') }
              : n;
          });

    if (!pendingAssistantDiff) {
      return { finalNodes: markRejected(nodesWithHandlers as Node[]), finalEdges: derivedEdges };
    }

    const nodes: Node[] = markRejected([...nodesWithHandlers]);
    const edges: Edge[] = [...derivedEdges];
    const overlay: Node[] = [];
    const rows = describeDiff(pendingAssistantDiff, steps);

    for (const row of rows) {
      if (row.kind === 'added') continue;
      // Diff step orders are 1-based and refer to the steps as they are now.
      const step = steps[row.stepOrder - 1];
      const nodeIndex = step ? nodes.findIndex((n) => n.id === step.id) : -1;
      if (nodeIndex === -1) continue; // stale proposal: the step it names is gone

      const node = nodes[nodeIndex];
      const outline = row.kind === 'removed' ? diffClasses.nodeRemoved : diffClasses.nodeChanged;
      nodes[nodeIndex] = { ...node, className: [node.className, outline].filter(Boolean).join(' ') };

      overlay.push({
        id: `diff-badge-${row.stepOrder}`,
        type: 'diffBadge',
        // Beside the card (step cards are 320px wide), level with its header: never clipped by the
        // top of the canvas the way a tab above the first step was.
        position: { x: node.position.x + 320 + 16, y: node.position.y + 18 },
        data: { kind: row.kind, variant: 'badge', name: row.previousName ? row.name : undefined },
        zIndex: 999,
        draggable: false,
        selectable: false,
        focusable: false,
        connectable: false,
      });
    }

    // Additions: dashed ghost cards chained after the last current step.
    const lastStep = steps[steps.length - 1];
    const lastNode = lastStep ? nodes.find((n) => n.id === lastStep.id) : undefined;
    if (lastNode) {
      let previousId = lastNode.id;
      rows
        .filter((row) => row.kind === 'added')
        .forEach((row, i) => {
          const ghostId = `diff-ghost-${row.stepOrder}`;
          overlay.push({
            id: ghostId,
            type: 'diffBadge',
            position: { x: lastNode.position.x, y: lastNode.position.y + (i + 1) * 200 },
            data: {
              kind: 'added',
              variant: 'ghost',
              name: row.name,
              meta: tBuild('ghostMeta', { number: row.stepOrder, type: row.stepTypeLabel }),
            },
            zIndex: 1,
            draggable: false,
            selectable: false,
            focusable: false,
            connectable: false,
          });
          edges.push({
            id: `e-${previousId}-${ghostId}`,
            source: previousId,
            target: ghostId,
            type: 'smoothstep',
            className: diffClasses.addedEdge,
            // Marker fill is an SVG attribute, which can't read a CSS variable; this green holds
            // >= 3:1 against both the light and the dark canvas.
            markerEnd: { type: MarkerType.ArrowClosed, color: '#16a34a' },
          });
          previousId = ghostId;
        });
    }

    return { finalNodes: [...nodes, ...overlay], finalEdges: edges };
  }, [nodesWithHandlers, derivedEdges, pendingAssistantDiff, steps, stepErrors, tBuild]);

  // Bring a newly arrived proposal into view: ghost steps are placed below the last step, which is
  // usually below the fold. Once per proposal (keyed on its token), never on unrelated re-renders.
  const { fitView } = useReactFlow();
  const reduceMotion = useReducedMotion();
  const proposalToken = pendingAssistantDiff?.confirmationToken;
  useEffect(() => {
    if (!proposalToken) return;
    const t = setTimeout(() => fitView({ padding: 0.15, duration: reduceMotion ? 0 : 300 }), 50);
    return () => clearTimeout(t);
  }, [proposalToken, fitView, reduceMotion]);

  // Frame the whole workflow once its steps first appear (and whenever steps are added/removed),
  // so the last steps are on screen without hunting for them. Never on unrelated re-renders.
  const stepCount = steps.length;
  useEffect(() => {
    if (stepCount === 0) return;
    const t = setTimeout(() => fitView({ padding: 0.15, maxZoom: 1, minZoom: 0.3, duration: 0 }), 50);
    return () => clearTimeout(t);
  }, [stepCount, fitView]);

  // Apply the (potentially overlaid) nodes and edges to the React Flow state
  useEffect(() => {
    setNodes(finalNodes);
    setEdges(finalEdges);
  }, [finalNodes, finalEdges, setNodes, setEdges]);

  // Capture drag positions to persist them across state changes
  const handleNodeDragStop = useCallback((_: unknown, node: Node) => {
    setDragPositions((prev) => ({
      ...prev,
      [node.id]: node.position,
    }));
  }, []);

  // onConnect is now a no-op: branching is specified via step config fields, not free-hand wiring
  const onConnect = useCallback(() => {
    // Branching is specified via step config fields (conditionExpression, etc.), not by manually wiring edges
  }, []);

  const handleAddStep = (stepType: StepType) => {
    // Extract, VideoAnalyze, VideoCompile and the three room step types don't resolve their model
    // from the step's own agent FK — auto-assign the appropriate built-in placeholder agent so the
    // non-nullable AgentDefinitionId FK is always satisfied without user action. VideoTransform is
    // a single deterministic-placeholder row that serves every video-editing step type, exactly as
    // ExtractTransform serves Extract (a room step's real seats/director are resolved server-side
    // from its own config JSON, never from this FK).
    const extractTransformAgentId = agents?.find((a) => a.agentType === 'ExtractTransform')?.id ?? '';
    const videoTransformAgentId = agents?.find((a) => a.agentType === 'VideoTransform')?.id ?? '';

    let agentDefinitionId = '';
    if (stepType === 'Conditional' || stepType === 'Extract' || stepType === 'Parallel' || stepType === 'DesignSystem') agentDefinitionId = extractTransformAgentId;
    if (stepType === 'VideoAnalyze' || stepType === 'VideoCompile' || stepType === 'VideoGenerate' || stepType === 'Voiceover' || stepType === 'ThumbnailGenerate' || stepType === 'TimelineRender' || stepType === 'ExtractFrame' || stepType === 'AudioGenerate' || stepType === 'ImageGenerate' || stepType === 'AudioCleanup' || stepType === 'VideoEnhance' || stepType === 'PinInsert' || isRoomStepType(stepType)) agentDefinitionId = videoTransformAgentId;

    const newStep: StepData = {
      id: `step-${Date.now()}`,
      label: '',
      agentDefinitionId,
      stepType,
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
      extractConfig: stepType === 'Extract' ? createDefaultExtractStepConfig() : null,
      // A first-step analyze has no earlier step to take a video from, so it starts on an uploaded
      // file (the C# default "previous step output" would be meaningless there).
      videoAnalyzeConfig: stepType === 'VideoAnalyze'
        ? { ...createDefaultVideoAnalyzeStepConfig(), ...(steps.length === 0 ? { source: NEW_CLIP_SOURCE_REF } : {}) }
        : null,
      videoCompileConfig: stepType === 'VideoCompile' ? createDefaultVideoCompileStepConfig() : null,
      videoGenerateConfig: stepType === 'VideoGenerate' ? createDefaultVideoGenerateStepConfig() : null,
      voiceoverConfig: stepType === 'Voiceover' ? createDefaultVoiceoverStepConfig() : null,
      thumbnailGenerateConfig: stepType === 'ThumbnailGenerate' ? createDefaultThumbnailGenerateStepConfig() : null,
      timelineRenderConfig: stepType === 'TimelineRender' ? createDefaultTimelineRenderStepConfig() : null,
      designSystemConfig: stepType === 'DesignSystem' ? createDefaultDesignSystemStepConfig() : null,
      // With nothing before it, "the previous step" names nothing: start from an uploaded file instead.
      extractFrameConfig: stepType === 'ExtractFrame'
        ? { ...createDefaultExtractFrameStepConfig(), ...(steps.length === 0 ? { source: EXTRACT_FRAME_FIRST_STEP_SOURCE } : {}) }
        : null,
      audioGenerateConfig: stepType === 'AudioGenerate' ? createDefaultAudioGenerateStepConfig() : null,
      imageGenerateConfig: stepType === 'ImageGenerate' ? createDefaultImageGenerateStepConfig() : null,
      audioCleanupConfig: stepType === 'AudioCleanup'
        ? { ...createDefaultAudioCleanupStepConfig(), ...(steps.length === 0 ? { source: AUDIO_CLEANUP_FIRST_STEP_SOURCE } : {}) }
        : null,
      videoEnhanceConfig: stepType === 'VideoEnhance'
        ? { ...createDefaultVideoEnhanceStepConfig(), ...(steps.length === 0 ? { source: VIDEO_ENHANCE_FIRST_STEP_SOURCE } : {}) }
        : null,
      pinInsertConfig: stepType === 'PinInsert' ? createDefaultPinInsertStepConfig() : null,
      // A default config is written IMMEDIATELY on add (not lazily on first edit): the executor
      // hard-fails a room step whose config JSON is null/empty, so a freshly-added, untouched step
      // must still carry the runnable default ({view: Previous}).
      ...(isRoomStepType(stepType)
        ? { [ROOM_STEP_KINDS[stepType].configField]: createDefaultRoomConfigJson() }
        : {}),
    };
    onChange([...steps, newStep]);
    setAddModalOpen(false);
  };

  const handleAutoLayout = () => {
    // Re-run deriveFlowGraph's dagre layout and clear manual drag position overrides
    setDragPositions({});
  };

  // Which fields of the open step the server named in a rejected save, for in-place red marks.
  const selectedFieldErrors = useMemo(() => {
    const msgs = stepErrors?.get(selectedStepIndex === null ? -1 : selectedStepIndex + 1);
    const step = selectedStepIndex === null ? undefined : steps[selectedStepIndex];
    if (!msgs || !step) return null;
    const raw = (step.videoGenerateConfig ?? step.videoAnalyzeConfig ?? step.videoCompileConfig ?? step.voiceoverConfig
      ?? step.thumbnailGenerateConfig ?? step.extractFrameConfig ?? step.audioGenerateConfig ?? step.imageGenerateConfig ?? step.audioCleanupConfig ?? step.videoEnhanceConfig ?? step.pinInsertConfig ?? step.extractConfig ?? step.conditionalConfig ?? step.reviewLoopConfig) as unknown;
    const json = raw ? JSON.stringify(camelizeKeys(raw)) : (step.editRoomConfigJson ?? step.graphicsRoomConfigJson ?? step.colorGradeRoomConfigJson ?? step.dailiesRoomConfigJson ?? null);
    const out = new Map<string, string>();
    for (const m of msgs) for (const path of fieldPathsForMessage(m, json)) if (!out.has(path)) out.set(path, m);
    return out;
  }, [stepErrors, selectedStepIndex, steps]);

  // Handle node click to open the config panel
  // Resolve the clicked node against `steps`, never against the React Flow node array: that array
  // also holds the assistant's diff badges/ghosts (appended after the steps), so indexing by node
  // position used to hand StepConfigPanel an out-of-range index and crash on `steps[i]` undefined.
  const handleNodeClick = useCallback((event: React.MouseEvent, node: Node) => {
    const stepIndex = steps.findIndex((s) => s.id === node.id);
    if (stepIndex >= 0) {
      selectedNodeRef.current = node.id;
      setSelectedStepIndex(stepIndex);
      setConfigPanelOpen(true);
    }
  }, [steps]);

  // Handle config panel close and return focus to the node
  const handleConfigPanelClose = useCallback(() => {
    setConfigPanelOpen(false);
  }, []);

  // Keyboard shortcuts
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      const isCanvasFocused = (e.target as HTMLElement)?.closest('.react-flow');
      const isFormField = (e.target as HTMLElement)?.matches('input, textarea, select, [contenteditable]');

      // Zoom shortcuts (canvas focused)
      if (isCanvasFocused && !isFormField) {
        if (e.key === '+' || e.key === '=') {
          e.preventDefault();
          // Zoom in
        } else if (e.key === '-') {
          e.preventDefault();
          // Zoom out
        } else if (e.key === 'f' || e.key === 'F') {
          e.preventDefault();
          // Fit to view
        }
      }

      // Node-focused shortcuts
      if (!isFormField && selectedStepIndex !== null) {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault();
          setConfigPanelOpen(true);
        } else if (e.key === 'Delete' || e.key === 'Backspace') {
          e.preventDefault();
          // Remove focused node with inline confirmation
          if (selectedStepIndex !== null && confirm(tBuild('removeConfirm', { number: selectedStepIndex + 1 }))) {
            onChange(steps.filter((_, i) => i !== selectedStepIndex));
            setSelectedStepIndex(null);
          }
        } else if (e.key.startsWith('Arrow')) {
          e.preventDefault();
          // Nudge focused node by 16px
          const step = steps[selectedStepIndex];
          if (step) {
            const currentPos = dragPositions[step.id] || { x: 0, y: 0 };
            const delta = 16;
            const newPos = { ...currentPos };
            if (e.key === 'ArrowUp') newPos.y -= delta;
            if (e.key === 'ArrowDown') newPos.y += delta;
            if (e.key === 'ArrowLeft') newPos.x -= delta;
            if (e.key === 'ArrowRight') newPos.x += delta;
            setDragPositions((prev) => ({
              ...prev,
              [step.id]: newPos,
            }));
          }
        }
      }

      // Close panel with Escape
      if (e.key === 'Escape' && configPanelOpen) {
        e.preventDefault();
        setConfigPanelOpen(false);
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [selectedStepIndex, configPanelOpen, steps, onChange, dragPositions, tBuild]);

  return (
    <>
      <div className={restyle.canvas} style={{
        width: '100%',
        display: 'flex',
        flexDirection: 'column',
        overflow: 'hidden',
        // Fills the viewport (no 900px cap), so a 6-11 step workflow is reachable by panning inside
        // one tall canvas rather than a small box inside a scrolling page.
        height: CANVAS_HEIGHT,
      }}
        data-testid="workflow-canvas"
      >
        <ReactFlow
          nodes={nodes}
          edges={edges}
          onNodesChange={onNodesChange}
          onEdgesChange={onEdgesChange}
          onConnect={onConnect}
          onNodeDragStop={handleNodeDragStop}
          onNodeClick={handleNodeClick}
          nodeTypes={nodeTypes}
          attributionPosition="bottom-right"
          style={{ flex: 1, minHeight: 0 }}
          {...CANVAS_INTERACTION}
        >
          <Background variant={BackgroundVariant.Dots} gap={16} size={1} color="currentColor" style={{ opacity: 0.18 }} />
          <Controls showInteractive={false} />
          <MiniMap position="bottom-right" />

          <Panel position="top-right">
            <Stack gap="xs" className={restyle.toolbar}>
              <Tooltip label={tBuild('addStep')}>
                <ActionIcon
                  size="lg"
                  variant="filled"
                  color="violet"
                  aria-label={tBuild('addStep')}
                  onClick={() => setAddModalOpen(true)}
                >
                  <IconPlus size={20} />
                </ActionIcon>
              </Tooltip>
              <Tooltip label={tBuild('autoLayout')}>
                <ActionIcon
                  size="lg"
                  variant="light"
                  color="gray"
                  onClick={handleAutoLayout}
                >
                  <IconLayoutGrid size={20} />
                </ActionIcon>
              </Tooltip>
            </Stack>
          </Panel>
        </ReactFlow>
      </div>

      {selectedStepIndex !== null && steps[selectedStepIndex] && (
        <StepConfigPanel
          step={steps[selectedStepIndex]}
          opened={configPanelOpen}
          onClose={handleConfigPanelClose}
          onChange={(updates: Partial<StepData>) => {
            const newSteps = [...steps];
            newSteps[selectedStepIndex] = { ...newSteps[selectedStepIndex], ...updates };
            onChange(newSteps);
          }}
          previousSteps={steps}
          allSteps={steps}
          currentStepIndex={selectedStepIndex}
          projectId={projectId}
          stepErrors={stepErrors?.get(selectedStepIndex + 1)}
          fieldErrors={selectedFieldErrors}
          fieldDiff={
            pendingAssistantDiff?.proposedSteps
              ? computeStepFieldDiff(
                  steps[selectedStepIndex],
                  pendingAssistantDiff.proposedSteps.find((p) => p.stepOrder === selectedStepIndex + 1)
                    ?? pendingAssistantDiff.proposedSteps[selectedStepIndex],
                )
              : null
          }
        />
      )}

      <AddStepModal
        opened={addModalOpen}
        onClose={() => setAddModalOpen(false)}
        onAdd={handleAddStep}
        nonLlmStepsDisabled={agentsLoading}
      />
    </>
  );
}

export function FlowchartBuilderWrapper({ steps, onChange, projectId, pendingAssistantDiff, stepErrors }: FlowchartBuilderProps) {
  // Below Mantine's `sm` breakpoint (768px / 48em), drag-and-drop-a-canvas is a poor fit for a
  // touch viewport — render a linear editable list instead. `useMediaQuery` (rather than
  // visibleFrom/hiddenFrom CSS-only hiding) ensures only one of the two ever actually mounts:
  // React Flow is a heavy canvas and we don't want it mounted-but-hidden on a phone burning render
  // budget. Both branches share the same `steps`/`onChange` from the caller, so resizing/rotating
  // across the breakpoint never loses in-progress edits.
  // Provide initialValue: true (assume desktop on server) to avoid hydration mismatch from
  // the hook returning false on first render before its effect resolves the real media query value.
  const isDesktop = useMediaQuery('(min-width: 48em)', true);

  if (!isDesktop) {
    return <WorkflowStepList steps={steps} onChange={onChange} projectId={projectId} />;
  }

  return (
    <ReactFlowProvider>
      <FlowchartBuilder steps={steps} onChange={onChange} projectId={projectId} pendingAssistantDiff={pendingAssistantDiff} stepErrors={stepErrors} />
    </ReactFlowProvider>
  );
}
