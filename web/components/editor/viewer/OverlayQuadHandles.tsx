'use client';

import React, { useEffect, useMemo, useRef, useState } from 'react';
import { Box, Button, Text } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { IconCheck } from '@tabler/icons-react';
import type { MediaClip, QuadCorners } from '@/lib/editing/types';
import type { EditorStore } from '@/lib/editing/store/editor-store';
import { useEditorStore } from '@/lib/editing/store/use-editor-store';
import { leafPlayheadFrame } from '@/lib/editing/store/coarse-ui';
import { PANEL_PLAYHEAD_QUANTUM_SEC } from '@/components/editor/shell/LivePlayhead';
import {
  IDENTITY_PIN,
  NUDGE_FRACTION,
  NUDGE_FRACTION_BIG,
  PIN_CORNERS,
  baseSize,
  commitFrameOps,
  frameDraftFor,
  movePinCorner,
  pinHandlesMatchPicture,
  pinOps,
  pinPointFromPixel,
  pinToolState,
  transformAt,
  useFrameDraft,
  type PinCorner,
} from '@/lib/editing/viewer-transform';
import { COLORS, HANDLE_HALO, HANDLE_HIT_COARSE, HANDLE_HIT_FINE } from './handle-style';
import { TOOL_ROW_STYLE, ToolDock } from './tool-dock';
import classes from './PinHandle.module.css';
import { useT } from '@/i18n';

function useCornerNames(): Record<PinCorner, string> {
  const tx = useT('editor.viewer.pinTool');
  return { topLeft: tx('corners.topLeft'), topRight: tx('corners.topRight'), bottomRight: tx('corners.bottomRight'), bottomLeft: tx('corners.bottomLeft') };
}

export interface PinToolProps {
  store: EditorStore;
  readOnly: boolean;
  /** The picture's size in pixels for a clip (the media pool's probe); absent or null: the letterbox is unknown. */
  sourceSizeFor?: (clip: MediaClip) => { width: number; height: number } | null;
}

/**
 * T-013: the corner pin tool. Mounted by `FrameHandlesOverlay` only while the `pin` viewer tool is active
 * (the paused auto-show never draws it). The pin is a static value (it has no keys), so it follows the
 * playhead at the 4 Hz leaf quantum while playing and never reads the exact playhead. A gesture drafts the
 * four corners live (the number fields follow) and writes ONE `setProperty cornerPin` on release.
 *
 * The handles sit on canvas fractions, exactly where the renderer lands the frame's corners. The bar (in the
 * viewer's tool dock, above the picture) carries the one note and Done in every state; when the clip is scaled,
 * moved or does not fill the frame the note says the handles mark the frame's corners, not the picture's own.
 */
export function PinTool({ store, readOnly, sourceSizeFor }: PinToolProps) {
  const doc = useEditorStore(store, (s) => s.doc);
  const selection = useEditorStore(store, (s) => s.ui.selection);
  const frame = useEditorStore(store, (s) => leafPlayheadFrame(s, PANEL_PLAYHEAD_QUANTUM_SEC));
  const state = useMemo(() => pinToolState(doc, selection, frame), [doc, selection, frame]);
  const bar = (note: string, clip: MediaClip | null) => (
    <ToolDock>
      <PinToolBar store={store} readOnly={readOnly} note={note} addFor={clip} />
    </ToolDock>
  );
  const tx = useT('editor.viewer.pinTool');
  if (state.kind === 'edit') {
    const clip = state.clip;
    const canvas = { width: doc.settings.width, height: doc.settings.height };
    const source = sourceSizeFor?.(clip) ?? null;
    const base = baseSize(canvas, source, doc.settings.aspectPolicy, clip.crop ?? null);
    // With the picture's size not yet known the letterbox cannot be ruled out, so the frame note stays.
    const matches = (source !== null || doc.settings.aspectPolicy === 'stretch') && pinHandlesMatchPicture(transformAt(clip, frame), base, canvas);
    return (
      <>
        <PinHandles key={clip.id} store={store} clip={clip} pin={state.pin} readOnly={readOnly} />
        {bar(matches ? tx('hintEdit') : `${tx('hintEdit')} ${tx('frameNote')}`, null)}
      </>
    );
  }
  if (state.kind === 'no-clip') return bar(tx('noClip'), null);
  if (state.kind === 'off-screen') return bar(tx('offScreen'), null);
  if (state.kind === 'no-pin') return bar(tx('noPin'), state.clip);
  return null;
}

/** The pin tool's bar: its one note, Add when the clip has no pin (the write happens on the click), and Done. */
function PinToolBar({ store, readOnly, note, addFor }: { store: EditorStore; readOnly: boolean; note: string; addFor: MediaClip | null }) {
  const tx = useT('editor.viewer.pinTool');
  const coarse = useMediaQuery('(pointer: coarse)') ?? false;
  const size = coarse ? 'md' : 'compact-sm';
  const minHeight = coarse ? 44 : undefined;
  return (
    <Box role="toolbar" aria-label={tx('toolbarLabel')} data-testid="viewer-pin-toolbar" style={TOOL_ROW_STYLE}>
      <Text size="xs" c="dimmed" data-testid="viewer-pin-note" style={{ flex: '1 1 200px', minWidth: 0 }}>
        {note}
      </Text>
      {addFor && (
        <Button data-testid="viewer-pin-add" size={size} fz={12} style={{ minHeight }} variant="light" disabled={readOnly} onClick={() => commitFrameOps(store, [{ type: 'setProperty', itemId: addFor.id, key: 'cornerPin', value: IDENTITY_PIN }], tx('labelAdd'))}>
          {tx('add')}
        </Button>
      )}
      <Button data-testid="viewer-pin-done" size={size} fz={12} style={{ minHeight }} variant="default" leftSection={<IconCheck size={14} />} onClick={() => store.setUi({ viewerTool: 'select' })}>
        {tx('done')}
      </Button>
    </Box>
  );
}

function PinHandles({ store, clip, pin, readOnly }: { store: EditorStore; clip: MediaClip; pin: QuadCorners; readOnly: boolean }) {
  const tx = useT('editor.viewer.pinTool');
  const names = useCornerNames();
  const coarse = useMediaQuery('(pointer: coarse)') ?? false;
  const hit = coarse ? HANDLE_HIT_COARSE : HANDLE_HIT_FINE;
  const ref = useRef<HTMLDivElement | null>(null);
  const width = useEditorStore(store, (s) => s.doc.settings.width);
  const height = useEditorStore(store, (s) => s.doc.settings.height);
  const canvas = useMemo(() => ({ width, height }), [width, height]);
  const draftStore = frameDraftFor(store);
  const draft = useFrameDraft(store);
  const live = draft && draft.kind === 'pin' && draft.itemId === clip.id ? draft.pin : pin;
  const drag = useRef<{ corner: PinCorner; pin: QuadCorners } | null>(null);
  const [active, setActive] = useState<PinCorner | null>(null);

  useEffect(() => {
    ref.current?.focus({ preventScroll: true });
  }, []);

  const toCanvas = (e: { clientX: number; clientY: number }) => {
    const r = ref.current!.getBoundingClientRect();
    return { x: ((e.clientX - r.left) / r.width) * canvas.width, y: ((e.clientY - r.top) / r.height) * canvas.height };
  };
  const stop = () => {
    drag.current = null;
    setActive(null);
    draftStore.set(null);
  };

  // Escape during a drag cancels the drag and nothing else (the global handler would also leave the tool).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && drag.current) {
        e.preventDefault();
        e.stopPropagation();
        stop();
      }
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- stop only touches refs, state setters and the stable draft store
  }, []);
  // A drag that outlives its handles (another chord, the playhead leaving the clip, a page change) clears its draft.
  useEffect(
    () => () => {
      if (drag.current) {
        drag.current = null;
        draftStore.set(null);
      }
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the draft store is stable per editor store
    [],
  );

  const begin = (corner: PinCorner) => (e: React.PointerEvent) => {
    if (e.button !== 0 || readOnly) return;
    e.preventDefault();
    e.stopPropagation();
    (e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId);
    if (store.getState().ui.playbackSpeed !== 0) store.setUi({ playbackSpeed: 0 });
    drag.current = { corner, pin: live };
    setActive(corner);
  };
  const move = (e: React.PointerEvent) => {
    const g = drag.current;
    if (!g) return;
    g.pin = movePinCorner(g.pin, g.corner, pinPointFromPixel(toCanvas(e), canvas));
    draftStore.set({ kind: 'pin', itemId: clip.id, pin: g.pin });
  };
  const end = (e: React.PointerEvent) => {
    const g = drag.current;
    if (!g) return;
    const next = movePinCorner(g.pin, g.corner, pinPointFromPixel(toCanvas(e), canvas));
    stop();
    commitFrameOps(store, pinOps(clip, next), tx('labelMove'));
  };
  const key = (corner: PinCorner) => (e: React.KeyboardEvent) => {
    if (readOnly) return;
    const step = e.shiftKey ? NUDGE_FRACTION_BIG : NUDGE_FRACTION;
    const dx = e.key === 'ArrowLeft' ? -step : e.key === 'ArrowRight' ? step : 0;
    const dy = e.key === 'ArrowUp' ? -step : e.key === 'ArrowDown' ? step : 0;
    if (dx === 0 && dy === 0) return;
    e.preventDefault();
    e.stopPropagation();
    commitFrameOps(store, pinOps(clip, movePinCorner(clip.cornerPin ?? pin, corner, { x: (clip.cornerPin ?? pin)[corner].x + dx, y: (clip.cornerPin ?? pin)[corner].y + dy })), tx('labelNudge'));
  };

  const pct = (c: PinCorner) => ({ left: `${live[c].x * 100}%`, top: `${live[c].y * 100}%` });
  const points = PIN_CORNERS.map((c) => `${live[c].x * 100},${live[c].y * 100}`).join(' ');
  return (
    <div ref={ref} tabIndex={-1} data-testid="viewer-pin" role="group" aria-label={tx('toolbarLabel')} style={{ position: 'absolute', inset: 0, zIndex: 5, pointerEvents: 'none', touchAction: 'none', outline: 'none' }}>
      <svg viewBox="0 0 100 100" preserveAspectRatio="none" style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', overflow: 'visible', pointerEvents: 'none' }} aria-hidden="true">
        <polygon points={points} fill="none" stroke={HANDLE_HALO} strokeWidth={4} vectorEffect="non-scaling-stroke" />
        <polygon points={points} fill="none" stroke={COLORS.pin} strokeWidth={2} vectorEffect="non-scaling-stroke" />
      </svg>
      {PIN_CORNERS.map((c) => (
        <button
          key={c}
          type="button"
          data-testid={`viewer-pin-${c}`}
          className={`${classes.handle}${active === c ? ` ${classes.active}` : ''}`}
          aria-label={tx('corner', { corner: names[c], x: Math.round(live[c].x * 100), y: Math.round(live[c].y * 100) })}
          disabled={readOnly}
          onPointerDown={begin(c)}
          onPointerMove={move}
          onPointerUp={end}
          onPointerCancel={stop}
          onKeyDown={key(c)}
          style={{ ...pct(c), width: hit, height: hit, marginLeft: -hit / 2, marginTop: -hit / 2 }}
        >
          <span className={classes.mark} style={{ '--pin-color': COLORS.pin } as React.CSSProperties} />
        </button>
      ))}
    </div>
  );
}
