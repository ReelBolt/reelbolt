'use client';

import React from 'react';
import { Background, ReactFlow, type Edge, type Node } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import type { AdjustmentClip, EditTimeline, MediaClip } from '@/lib/editing/types';
import { colorGroupOf } from '@/lib/editing/color/grade';
import { msg, useT, type Msg } from '@/i18n';

/** The stages of a clip's picture chain, in the order the renderer applies them (exported for the test). */
export function stackStages(doc: EditTimeline, item: MediaClip | AdjustmentClip): { id: string; label: Msg; active: boolean }[] {
  if (item.type === 'adjustmentClip') {
    return [
      { id: 'below', label: msg('editor-misc.stages.below'), active: true },
      { id: 'grade', label: msg('editor-misc.stages.grade', { count: item.grade?.nodes?.filter((n) => n.enabled !== false).length ?? 0 }), active: !!item.grade && item.grade.enabled !== false && (item.grade.nodes?.length ?? 0) > 0 },
      { id: 'effects', label: msg('editor-misc.stages.effects', { count: item.effects?.length ?? 0 }), active: (item.effects?.length ?? 0) > 0 },
      { id: 'out', label: msg('editor-misc.stages.program'), active: true },
    ];
  }
  const group = colorGroupOf(doc, item.id);
  return [
    { id: 'source', label: msg('editor-misc.stages.source'), active: true },
    { id: 'stabilize', label: msg('editor-misc.stages.stabilise'), active: item.stabilize === true },
    { id: 'reframe', label: msg('editor-misc.stages.reframe'), active: !!item.reframe },
    { id: 'keyer', label: msg('editor-misc.stages.keyer'), active: !!item.keyer },
    { id: 'crop', label: msg('editor-misc.stages.crop'), active: !!item.crop },
    { id: 'transform', label: msg('editor-misc.stages.transform'), active: !!item.transform },
    { id: 'group', label: group ? msg('editor-misc.stages.groupGradeNamed', { name: group.name }) : msg('editor-misc.stages.groupGrade'), active: !!group?.grade && group.grade.enabled !== false },
    { id: 'grade', label: msg('editor-misc.stages.grade', { count: item.grade?.nodes?.filter((n) => n.enabled !== false).length ?? 0 }), active: !!item.grade && item.grade.enabled !== false && (item.grade.nodes?.length ?? 0) > 0 },
    { id: 'effects', label: msg('editor-misc.stages.effects', { count: item.effects?.filter((e) => e.enabled !== false).length ?? 0 }), active: (item.effects?.length ?? 0) > 0 },
    { id: 'mask', label: msg('editor-misc.stages.mask'), active: !!item.mask },
    { id: 'pin', label: msg('editor-misc.stages.pin'), active: !!item.cornerPin },
    { id: 'overlay', label: msg('editor-misc.stages.overlay'), active: true },
  ];
}

export interface NodeViewProps {
  doc: EditTimeline;
  item: MediaClip | AdjustmentClip;
}

/** A read-only node view (P7) of the stack the renderer builds for the item: the stages in order, inactive ones dimmed. */
export function NodeView({ doc, item }: NodeViewProps) {
  const t = useT();
  const stages = stackStages(doc, item);
  const nodes: Node[] = stages.map((s, i) => ({
    id: s.id,
    position: { x: 20 + (i % 4) * 150, y: 20 + Math.floor(i / 4) * 70 },
    data: { label: t.msg(s.label) },
    draggable: false,
    selectable: false,
    style: { fontSize: 12, padding: 6, width: 130, opacity: s.active ? 1 : 0.4, borderStyle: s.active ? 'solid' : 'dashed' },
  }));
  const edges: Edge[] = stages.slice(1).map((s, i) => ({ id: `${stages[i].id}-${s.id}`, source: stages[i].id, target: s.id, animated: s.active }));
  return (
    <div style={{ height: 230, border: '1px solid var(--mantine-color-default-border)', borderRadius: 4 }} data-testid="node-view">
      <ReactFlow nodes={nodes} edges={edges} fitView nodesDraggable={false} nodesConnectable={false} elementsSelectable={false} panOnDrag zoomOnScroll={false} proOptions={{ hideAttribution: true }}>
        <Background gap={16} />
      </ReactFlow>
    </div>
  );
}
