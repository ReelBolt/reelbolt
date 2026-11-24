'use client';

import React, { useEffect, useState } from 'react';
import { Box, Button, Group, SegmentedControl, Text } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { IconContrast, IconPencil, IconTrash } from '@tabler/icons-react';
import type { EditorStore } from '@/lib/editing/store/editor-store';
import { useEditorStore } from '@/lib/editing/store/use-editor-store';
import { leafPlayheadFrame } from '@/lib/editing/store/coarse-ui';
import { PANEL_PLAYHEAD_QUANTUM_SEC } from '@/components/editor/shell/LivePlayhead';
import { PHONE_MEDIA_QUERY } from '@/lib/editing/device-class';
import { commitFrameOps, maskOps, maskRedrawFor, maskToolState, toggleInvertOps, useMaskRedraw, FEATHER_MAX, type MaskToolState } from '@/lib/editing/viewer-transform';
import type { MediaClip, ShapeMask } from '@/lib/editing/types';
import { DEFAULT_SHAPE_MASK } from '../panels/MaskReframeFields';
import { LiveSlider } from '../shell/LiveSlider';
import { MaskDrawLayer } from './MaskDrawLayer';
import { useT } from '@/i18n';
import { useLiveChord } from '../prefs/live-chords';
import { composeRemoveRunsOn } from '@/lib/editing/compose/commands';
import { TOOL_ROW_STYLE, ToolDock } from './tool-dock';

type Kind = 'rect' | 'ellipse';

/**
 * T-012: the mask tool on the picture. It exists only while the mask tool is active, on desktop and tablet
 * (the phone never enters a viewer tool). It holds the two bits of tool-local state (the shape a new mask
 * takes, and whether Redraw is armed) and renders the draw layer over the picture and the bar in the viewer's
 * tool dock above it, so nothing but the drawn box ever covers the picture; the handles of an existing
 * mask stay with `FrameHandlesOverlay`.
 */
export function MaskTool({ store, readOnly }: { store: EditorStore; readOnly: boolean }) {
  const phone = useMediaQuery(PHONE_MEDIA_QUERY) ?? false;
  const active = useEditorStore(store, (s) => s.ui.viewerTool === 'mask');
  if (!active || phone) return null;
  return <MaskToolView store={store} readOnly={readOnly} />;
}

function MaskToolView({ store, readOnly }: { store: EditorStore; readOnly: boolean }) {
  const doc = useEditorStore(store, (s) => s.doc);
  const selection = useEditorStore(store, (s) => s.ui.selection);
  const frame = useEditorStore(store, (s) => leafPlayheadFrame(s, PANEL_PLAYHEAD_QUANTUM_SEC));
  const state = maskToolState(doc, selection, frame);
  const [drawKind, setDrawKind] = useState<Kind>('rect');
  // Redraw belongs to one clip's mask: another selection or a removed mask drops it. The armed clip lives in view
  // state (not here) so Compose's own Redraw can arm it, and it is cleared when the tool is left.
  const redrawStore = maskRedrawFor(store);
  const armedFor = useMaskRedraw(store);
  useEffect(
    () => () => {
      if (store.getState().ui.viewerTool !== 'mask') redrawStore.set(null);
    },
    [store, redrawStore],
  );
  const clipId = state.kind === 'edit' ? state.clip.id : null;
  const redrawing = clipId !== null && armedFor === clipId;

  const drawable = !readOnly && (state.kind === 'draw' || (state.kind === 'edit' && redrawing));
  const base: ShapeMask = state.kind === 'edit' ? state.shape : { ...DEFAULT_SHAPE_MASK, kind: drawKind };
  return (
    <>
      {drawable && (state.kind === 'draw' || state.kind === 'edit') && <MaskDrawLayer key={`${state.clip.id}:${redrawing ? 'redraw' : 'new'}`} store={store} clip={state.clip} base={base} replace={state.kind === 'edit'} onDone={() => redrawStore.set(null)} />}
      <ToolDock>
        <MaskToolBar
          store={store}
          state={state}
          readOnly={readOnly}
          drawKind={drawKind}
          onDrawKind={setDrawKind}
          redrawing={redrawing}
          onRedraw={() => redrawStore.set(redrawing ? null : clipId)}
        />
      </ToolDock>
    </>
  );
}

export interface MaskToolBarProps {
  store: EditorStore;
  state: MaskToolState;
  readOnly: boolean;
  drawKind: Kind;
  onDrawKind: (kind: Kind) => void;
  redrawing: boolean;
  onRedraw: () => void;
}

export function MaskToolBar({ store, state, readOnly, drawKind, onDrawKind, redrawing, onRedraw }: MaskToolBarProps) {
  const tx = useT('editor.viewer.maskTool');
  const invertChord = useLiveChord('maskInvert');
  const shapeChord = useLiveChord('maskShapeNext');
  const removeChord = useLiveChord('composeRemoveEntry');
  const removeRuns = useEditorStore(store, (s) => composeRemoveRunsOn(s, 'mask'));
  const coarse = useMediaQuery('(pointer: coarse)') ?? false;
  const size = coarse ? 'md' : 'compact-sm';
  const minHeight = coarse ? 44 : undefined;
  const clip: MediaClip | null = state.kind === 'edit' || state.kind === 'draw' || state.kind === 'object' ? state.clip : null;
  const shape = state.kind === 'edit' ? state.shape : null;
  const kind: Kind = shape ? (shape.kind === 'ellipse' ? 'ellipse' : 'rect') : drawKind;
  const done = () => store.setUi({ viewerTool: 'select' });
  // The shape, invert and remove chords run only on an existing box or oval mask; the draw state advertises none.
  const shapeTip = shape && shapeChord.display ? shapeChord.tip(tx('shapeLabel')) : undefined;
  const removeLive = removeRuns ? removeChord : null;

  const message =
    state.kind === 'no-clip' ? tx('noClip')
    : state.kind === 'off-screen' ? tx('offScreen')
    : state.kind === 'object' ? tx('object')
    : state.kind === 'draw' ? tx('hintDraw')
    : redrawing ? tx('hintRedraw')
    : tx('hintEdit');

  const edit = (next: ShapeMask, label: string) => {
    if (clip) commitFrameOps(store, maskOps(clip, next), label);
  };

  return (
    <Box role="toolbar" aria-label={tx('toolbarLabel')} data-testid="viewer-mask-toolbar" style={TOOL_ROW_STYLE}>
      {(state.kind === 'draw' || state.kind === 'edit') && (
        <>
          <SegmentedControl
            aria-keyshortcuts={shape ? shapeChord.aria : undefined}
            title={shapeTip}
            size={coarse ? 'md' : 'xs'}
            styles={coarse ? { label: { minHeight: 44, display: 'flex', alignItems: 'center', justifyContent: 'center' } } : undefined}
            aria-label={tx('shapeLabel')}
            data-testid="viewer-mask-kind"
            disabled={readOnly}
            value={kind}
            onChange={(v) => {
              const next = v as Kind;
              if (shape) edit({ ...shape, kind: next }, tx('labelShape'));
              else onDrawKind(next);
            }}
            data={[
              { value: 'rect', label: tx('rectangle') },
              { value: 'ellipse', label: tx('ellipse') },
            ]}
          />
          {shape ? (
            <>
              <Group gap={6} wrap="nowrap" style={{ minWidth: 140 }}>
                <Text size="xs">{tx('feather')}</Text>
                <LiveSlider
                  size="xs"
                  style={{ flex: 1, minWidth: 80 }}
                  thumbLabel={tx('feather')}
                  min={0}
                  max={FEATHER_MAX}
                  step={0.01}
                  value={shape.feather ?? 0}
                  disabled={readOnly}
                  onChangeEnd={(v) => edit({ ...shape, feather: v }, tx('labelFeather'))}
                  data-testid="viewer-mask-feather"
                />
              </Group>
              <Button data-testid="viewer-mask-tool-invert" size={size} fz={12} style={{ minHeight }} variant={shape.invert === true ? 'light' : 'default'} aria-pressed={shape.invert === true} aria-keyshortcuts={invertChord.aria} title={invertChord.display ? invertChord.tip(tx('invert')) : undefined} disabled={readOnly} leftSection={<IconContrast size={14} />} onClick={() => clip && commitFrameOps(store, toggleInvertOps(clip), tx('invert'))}>
                {tx('invert')}
              </Button>
              <Button data-testid="viewer-mask-redraw" size={size} fz={12} style={{ minHeight }} variant={redrawing ? 'light' : 'default'} aria-pressed={redrawing} disabled={readOnly} leftSection={<IconPencil size={14} />} onClick={onRedraw}>
                {tx('redraw')}
              </Button>
              <Button data-testid="viewer-mask-remove" size={size} fz={12} style={{ minHeight }} variant="default" color="red" aria-keyshortcuts={removeLive?.aria} title={removeLive?.display ? removeLive.tip(tx('remove')) : undefined} disabled={readOnly} leftSection={<IconTrash size={14} />} onClick={() => clip && commitFrameOps(store, [{ type: 'setProperty', itemId: clip.id, key: 'mask', value: null }], tx('labelRemove'))}>
                {tx('remove')}
              </Button>
            </>
          ) : (
            <Button data-testid="viewer-mask-add" size={size} fz={12} style={{ minHeight }} variant="light" disabled={readOnly} onClick={() => clip && commitFrameOps(store, maskOps(clip, { ...DEFAULT_SHAPE_MASK, kind: drawKind }), tx('labelDraw'))}>
              {tx('add')}
            </Button>
          )}
        </>
      )}
      <Text size="xs" c="dimmed" data-testid="viewer-mask-hint" style={{ flex: '1 1 200px', minWidth: 0 }}>
        {message}
      </Text>
      <Button data-testid="viewer-mask-done" size={size} fz={12} style={{ minHeight, marginInlineStart: 'auto' }} variant="default" onClick={done}>
        {tx('done')}
      </Button>
    </Box>
  );
}
