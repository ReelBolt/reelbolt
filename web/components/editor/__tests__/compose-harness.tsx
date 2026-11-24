import React, { useState } from 'react';
import type { EditOp, PropertyKey, PropertyValue } from '@/lib/editing/engine';
import type { EditTimeline } from '@/lib/editing/types';
import type { ComposeEntryId } from '@/lib/editing/compose/entries';
import type { MaskReframeTool } from '../panels/MaskReframeFields';
import { ComposeList } from '../compose/ComposeList';
import { ComposeProperties } from '../compose/ComposeProperties';

export interface ComposeHarnessProps {
  doc: EditTimeline;
  projectId: string;
  readOnly: boolean;
  selection: readonly string[];
  playheadFrame: number;
  timelineId?: string;
  inOut: { inFrame: number | null; outFrame: number | null };
  newId: () => string;
  onSetProperty: (itemId: string, key: PropertyKey, value: PropertyValue, label?: string) => void;
  onApply: (ops: readonly EditOp[], label: string) => void;
  onSelect: (itemId: string) => void;
  onSeek: (frame: number) => void;
  onActivateViewerTool?: (tool: MaskReframeTool | 'pin') => void;
  onDrawMask?: (redraw: boolean) => void;
  /** Replaces the default tool hook, to see the options a list entry passes (e.g. `focusPicture: false`). */
  onViewerTool?: (tool: 'select' | 'mask' | 'reframe' | 'pin', options?: { focusPicture?: boolean }) => void;
}

/** Test host: the editor's two Compose columns side by side, holding the entry the store holds in the app. */
export function ComposeHarness(props: ComposeHarnessProps) {
  const [entry, setEntry] = useState<ComposeEntryId | null>(null);
  const { onActivateViewerTool, onViewerTool, ...rest } = props;
  return (
    <div>
      <ComposeList {...rest} composeEntry={entry} onComposeEntry={setEntry} onViewerTool={onViewerTool ?? ((tool) => tool !== 'select' && onActivateViewerTool?.(tool))} />
      <ComposeProperties {...rest} composeEntry={entry} onActivateViewerTool={onActivateViewerTool} />
    </div>
  );
}
