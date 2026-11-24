'use client';

import React from 'react';
import { Box, Button, Text } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { IconCheck } from '@tabler/icons-react';
import type { MediaClip } from '@/lib/editing/types';
import type { EditorStore } from '@/lib/editing/store/editor-store';
import { useEditorStore } from '@/lib/editing/store/use-editor-store';
import { leafPlayheadFrame } from '@/lib/editing/store/coarse-ui';
import { PANEL_PLAYHEAD_QUANTUM_SEC } from '@/components/editor/shell/LivePlayhead';
import { PHONE_MEDIA_QUERY } from '@/lib/editing/device-class';
import { commitFrameOps, selectedFrameItem } from '@/lib/editing/viewer-transform';
import { DEFAULT_REFRAME_POINT } from '../panels/MaskReframeFields';
import { TOOL_ROW_STYLE, ToolDock } from './tool-dock';
import { useT } from '@/i18n';

/**
 * The reframe tool's bar, docked above the picture like the mask tool's. It exists so the tool is never a dead
 * end: on a clip with no reframe point it says so and offers Add (the write happens on the click, never on
 * entering the tool). Desktop and tablet only; the phone never enters a viewer tool.
 */
export function ReframeToolBar({ store, readOnly }: { store: EditorStore; readOnly: boolean }) {
  const phone = useMediaQuery(PHONE_MEDIA_QUERY) ?? false;
  const active = useEditorStore(store, (s) => s.ui.viewerTool === 'reframe');
  if (!active || phone) return null;
  return <ReframeToolView store={store} readOnly={readOnly} />;
}

function ReframeToolView({ store, readOnly }: { store: EditorStore; readOnly: boolean }) {
  const tx = useT('editor.viewer.reframeTool');
  const coarse = useMediaQuery('(pointer: coarse)') ?? false;
  const doc = useEditorStore(store, (s) => s.doc);
  const selection = useEditorStore(store, (s) => s.ui.selection);
  const frame = useEditorStore(store, (s) => leafPlayheadFrame(s, PANEL_PLAYHEAD_QUANTUM_SEC));
  const item = selectedFrameItem(doc, selection, frame);
  const clip = item && item.type === 'mediaClip' ? (item as MediaClip) : null;
  const size = coarse ? 'md' : 'compact-sm';
  const minHeight = coarse ? 44 : undefined;
  const message = !clip ? tx('noClip') : clip.reframe ? tx('hintEdit') : tx('hintNone');
  return (
    <ToolDock>
      <Box role="toolbar" aria-label={tx('toolbarLabel')} data-testid="viewer-reframe-toolbar" style={TOOL_ROW_STYLE}>
        <Text size="xs" c="dimmed" data-testid="viewer-reframe-hint" style={{ flex: '1 1 200px', minWidth: 0 }}>
          {message}
        </Text>
        {clip && !clip.reframe && (
          <Button
            data-testid="viewer-reframe-add"
            size={size}
            fz={12}
            style={{ minHeight }}
            variant="light"
            disabled={readOnly}
            onClick={() => commitFrameOps(store, [{ type: 'setProperty', itemId: clip.id, key: 'reframe', value: { ...DEFAULT_REFRAME_POINT } }], tx('labelAdd'))}
          >
            {tx('add')}
          </Button>
        )}
        <Button data-testid="viewer-reframe-done" size={size} fz={12} style={{ minHeight }} variant="default" leftSection={<IconCheck size={14} />} onClick={() => store.setUi({ viewerTool: 'select' })}>
          {tx('done')}
        </Button>
      </Box>
    </ToolDock>
  );
}
