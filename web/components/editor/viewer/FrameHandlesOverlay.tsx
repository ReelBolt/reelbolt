'use client';

import React, { useEffect, useMemo, useRef, useState } from 'react';
import { Box, Button } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { IconContrast } from '@tabler/icons-react';
import type { MediaClip, ShapeMask } from '@/lib/editing/types';
import type { EditorStore, ViewerTool } from '@/lib/editing/store/editor-store';
import { useEditorStore } from '@/lib/editing/store/use-editor-store';
import { leafPlayheadFrame } from '@/lib/editing/store/coarse-ui';
import { PANEL_PLAYHEAD_QUANTUM_SEC } from '@/components/editor/shell/LivePlayhead';
import {
  TRACKED_WINDOW_NOTE,
  baseSize,
  bounds,
  boxOf,
  boxToFrame,
  commitFrameOps,
  createFrameTargetMemo,
  frameDraftFor,
  frameFor,
  frameToBox,
  maskOps,
  pixelToPoint,
  reframeOps,
  reframeWindow,
  selectedFrameItem,
  toggleInvertOps,
  transformAt,
  useFrameDraft,
  useWindowTarget,
  windowOps,
  type FrameTarget,
} from '@/lib/editing/viewer-transform';
import { PHONE_MEDIA_QUERY } from '@/lib/editing/device-class';
import { OverlayFrameHandles, type FrameValue } from './OverlayFrameHandles';
import { MaskTool } from './MaskToolBar';
import { COLORS } from './handle-style';
import { ReframeToolBar } from './ReframeToolBar';
import { PinTool } from './OverlayQuadHandles';
import { FollowTool } from './FollowDrawLayer';
import { useT } from '@/i18n';

export interface FrameHandlesOverlayProps {
  store: EditorStore;
  /** The picture's size in pixels for a clip (from the media pool's probe); null = unknown. */
  sourceSizeFor: (clip: MediaClip) => { width: number; height: number } | null;
  readOnly: boolean;
}


/** The viewer tools whose handles persist while the transport plays (§7.3). */
const SHAPE_TOOLS: readonly ViewerTool[] = ['mask', 'reframe', 'window'];

/**
 * R10.3 / P9-GRADE-02 / §7.3: the on-canvas mask, reframe-point and power-window handles.
 *
 * Mount matrix. While PAUSED the selected clip's mask, reframe point and window handles are on the
 * picture whether or not a tool is active — today's auto-show, and the phone (which never sets a viewer
 * tool) keeps it. While PLAYING they mount only when a shape tool (`mask`, `reframe` or untracked
 * `window`) is active; they then follow the playhead at the 4 Hz leaf quantum
 * (`PANEL_PLAYHEAD_QUANTUM_SEC`), so the target recomputes a few times a second, never per frame. With
 * no tool, nothing mounts and no playhead work runs.
 *
 * The transform tool and a TRACKED power window stay pause-only: their values follow the picture every
 * frame (a tracked window is read-only at its tracked position), so they never mount during playback.
 *
 * A gesture drafts into the frame draft store (merged into the preview document) and writes ONE batch on
 * release. Starting a gesture while the transport plays stops playback first, so the drag is not fighting
 * the picture; the component tree is stable across that pause, so the gesture is not interrupted.
 */
export function FrameHandlesOverlay(props: FrameHandlesOverlayProps) {
  // Follow subject: the viewer's store for the Follow section, and its one-shot box drag while armed.
  return (
    <>
      <HandlesLayers {...props} />
      <FollowTool store={props.store} sourceSizeFor={props.sourceSizeFor} readOnly={props.readOnly} />
    </>
  );
}

function HandlesLayers({ store, sourceSizeFor, readOnly }: FrameHandlesOverlayProps) {
  const paused = useEditorStore(store, (s) => s.ui.playbackSpeed === 0);
  const shapeTool = useEditorStore(store, (s) => SHAPE_TOOLS.includes(s.ui.viewerTool));
  const pinTool = useEditorStore(store, (s) => s.ui.viewerTool === 'pin');
  // The pin tool is its own layer: the paused auto-show stays mask / reframe / window only (T-013).
  if (pinTool) return (<><PinTool store={store} readOnly={readOnly} sourceSizeFor={sourceSizeFor} />{paused && <FrameHandlesView store={store} sourceSizeFor={sourceSizeFor} readOnly={readOnly} paused={paused} />}</>);
  if (!paused && !shapeTool) return null;
  // The mask tool's draw layer and bar are siblings of the handle layers, never inside them (T-012).
  return (
    <>
      <FrameHandlesView store={store} sourceSizeFor={sourceSizeFor} readOnly={readOnly} paused={paused} />
      <MaskTool store={store} readOnly={readOnly} />
      <ReframeToolBar store={store} readOnly={readOnly} />
    </>
  );
}

function FrameHandlesView({ store, sourceSizeFor, readOnly, paused }: FrameHandlesOverlayProps & { paused: boolean }) {
  // Exact when paused; the 4 Hz leaf quantum while playing (a shape tool stays on the picture).
  const frame = useEditorStore(store, (s) => leafPlayheadFrame(s, PANEL_PLAYHEAD_QUANTUM_SEC));
  const doc = useEditorStore(store, (s) => s.doc);
  const selection = useEditorStore(store, (s) => s.ui.selection);
  const windowTarget = useWindowTarget(store);
  const memo = useMemo(() => createFrameTargetMemo(), []);
  const focus = memo.resolve(doc, selection, frame, windowTarget);
  if (!focus) return null;
  // A tracked window follows the picture per frame: pause-only, so it is dropped while playing.
  const targets = paused ? focus.targets : focus.targets.filter((t) => !(t.id === 'window' && t.tracked));
  if (targets.length === 0) return null;
  return <Layer key={focus.item.id} store={store} targets={targets} frame={frame} sourceSizeFor={sourceSizeFor} readOnly={readOnly} onActivate={pauseForGesture(store)} />;
}

/** A gesture on the picture stops the transport so the drag is not competing with playback. */
function pauseForGesture(store: EditorStore): () => void {
  return () => {
    if (store.getState().ui.playbackSpeed !== 0) store.setUi({ playbackSpeed: 0 });
  };
}

function Layer({ store, targets, frame, sourceSizeFor, readOnly, onActivate }: { store: EditorStore; targets: FrameTarget[]; frame: number; sourceSizeFor: FrameHandlesOverlayProps['sourceSizeFor']; readOnly: boolean; onActivate?: () => void }) {
  const coarse = useMediaQuery('(pointer: coarse)') ?? false;
  const doc = useEditorStore(store, (s) => s.doc);
  const rootRef = useRef<HTMLDivElement | null>(null);
  const [size, setSize] = useState({ w: 0, h: 0 });
  useEffect(() => {
    const el = rootRef.current;
    if (!el) return;
    const read = () => setSize({ w: el.clientWidth, h: el.clientHeight });
    read();
    const ro = new ResizeObserver(read);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  const canvas = useMemo(() => ({ width: doc.settings.width, height: doc.settings.height }), [doc.settings.width, doc.settings.height]);
  const k = size.w > 0 ? size.w / canvas.width : 0;
  const draftStore = frameDraftFor(store);
  const draft = useFrameDraft(store);
  const trackedNote = targets.some((t) => t.id === 'window' && t.tracked);

  return (
    <Box ref={rootRef} data-testid="viewer-frame-handles" tabIndex={-1} style={{ position: 'absolute', inset: 0, zIndex: 4, pointerEvents: 'none', touchAction: 'none', outline: 'none' }}>
      {k > 0 &&
        targets.map((t) => (
          <TargetLayer key={`${t.id}`} store={store} target={t} draft={draft} draftStore={draftStore} frame={frame} canvas={canvas} k={k} size={size} coarse={coarse} readOnly={readOnly} rootRef={rootRef} sourceSizeFor={sourceSizeFor} onActivate={onActivate} />
        ))}
      {trackedNote && (
        <div data-testid="viewer-window-tracked-note" style={{ position: 'absolute', left: 8, bottom: 8, right: 8, fontSize: 12, lineHeight: 1.3, color: '#fff', background: 'rgba(0,0,0,0.65)', padding: '4px 8px', borderRadius: 4, pointerEvents: 'none' }}>
          {TRACKED_WINDOW_NOTE}
        </div>
      )}
    </Box>
  );
}

function TargetLayer({ store, target, draft, draftStore, frame, canvas, k, size, coarse, readOnly, rootRef, sourceSizeFor, onActivate }: {
  store: EditorStore;
  target: FrameTarget;
  draft: ReturnType<typeof useFrameDraft>;
  draftStore: ReturnType<typeof frameDraftFor>;
  frame: number;
  canvas: { width: number; height: number };
  k: number;
  size: { w: number; h: number };
  coarse: boolean;
  readOnly: boolean;
  rootRef: React.RefObject<HTMLDivElement | null>;
  sourceSizeFor: FrameHandlesOverlayProps['sourceSizeFor'];
  onActivate?: () => void;
}) {
  const tx = useT('editor.viewer.frameHandlesOverlay');
  const doc = useEditorStore(store, (s) => s.doc);
  const item = useMemo(() => {
    for (const tr of doc.tracks) for (const i of tr.items) if (i.id === target.itemId) return i;
    return null;
  }, [doc, target.itemId]);
  if (!item) return null;

  // Where the value is right now: the live draft while dragging, else the stored one.
  const mine = draft && draft.itemId === target.itemId && draft.kind === target.id && (draft.kind !== 'window' || (target.id === 'window' && draft.nodeId === target.nodeId)) ? draft : null;

  if (target.id === 'reframe') {
    const point = mine && mine.kind === 'reframe' ? mine.point : target.point;
    const clip = item as MediaClip;
    const base = baseSize(canvas, sourceSizeFor(clip), doc.settings.aspectPolicy, clip.crop ?? null);
    const pic = bounds(frameFor(transformAt(clip, frame), base, canvas));
    const value: FrameValue = { frame: { cx: point.x * canvas.width, cy: point.y * canvas.height, w: 0, h: 0, rotation: 0 }, feather: null };
    return (
      <OverlayFrameHandles
        idPrefix="viewer-reframe"
        label={tx('reframePoint')}
        color={COLORS.reframe}
        kind="point"
        value={value}
        canvas={canvas}
        k={k}
        size={size}
        coarse={coarse}
        readOnly={readOnly}
        rootRef={rootRef}
        onActiveChange={onActivate ? (active) => { if (active) onActivate(); } : undefined}
        dimOutside={reframeWindow(pic, canvas, point)}
        onDraft={(next) => draftStore.set({ kind: 'reframe', itemId: target.itemId, point: pixelToPoint({ x: next.frame.cx, y: next.frame.cy }, canvas) })}
        onCommit={(next, label) => {
          draftStore.set(null);
          if (next) commitFrameOps(store, reframeOps(clip, pixelToPoint({ x: next.frame.cx, y: next.frame.cy }, canvas)), label);
        }}
      />
    );
  }

  const stored: ShapeMask = mine && (mine.kind === 'mask' || mine.kind === 'window') ? mine.shape : target.shape;
  const offset = target.id === 'window' ? target.offset : { x: 0, y: 0 };
  const box = boxOf(stored);
  const shown = boxToFrame({ ...box, x: box.x + offset.x, y: box.y + offset.y }, canvas);
  const tracked = target.id === 'window' && target.tracked;
  const value: FrameValue = { frame: shown, feather: stored.feather ?? 0 };
  const toShape = (next: FrameValue): ShapeMask => {
    const b = frameToBox(next.frame, canvas);
    return { ...stored, x: Math.round((b.x - offset.x) * 10000) / 10000, y: Math.round((b.y - offset.y) * 10000) / 10000, w: b.w, h: b.h, feather: next.feather ?? stored.feather };
  };
  const draftOf = (shape: ShapeMask) => (target.id === 'mask' ? ({ kind: 'mask', itemId: target.itemId, shape } as const) : ({ kind: 'window', itemId: target.itemId, nodeId: target.nodeId, shape } as const));
  return (
    <OverlayFrameHandles
      idPrefix={target.id === 'mask' ? 'viewer-mask' : 'viewer-window'}
      label={target.label}
      color={COLORS[target.id]}
      kind={target.kind}
      value={value}
      canvas={canvas}
      k={k}
      size={size}
      coarse={coarse}
      readOnly={readOnly || tracked}
      rootRef={rootRef}
      onActiveChange={onActivate ? (active) => { if (active) onActivate(); } : undefined}
      onDraft={(next) => draftStore.set(draftOf(toShape(next)))}
      onCommit={(next, label) => {
        draftStore.set(null);
        if (!next) return;
        const shape = toShape(next);
        const ops = target.id === 'mask' ? maskOps(item as MediaClip, shape) : windowOps(item as MediaClip, target.nodeId, shape);
        commitFrameOps(store, ops, label);
      }}
    />
  );
}

/** The mask invert toggle in the viewer's header tools (next to Position). */
export function FrameTools({ store, readOnly }: { store: EditorStore; readOnly: boolean }) {
  const tx = useT('editor.viewer.frameHandlesOverlay');
  const coarse = useMediaQuery('(pointer: coarse)') ?? false;
  const doc = useEditorStore(store, (s) => s.doc);
  const selection = useEditorStore(store, (s) => s.ui.selection);
  const frame = useEditorStore(store, (s) => s.ui.playheadFrame);
  const paused = useEditorStore(store, (s) => s.ui.playbackSpeed === 0);
  // While the mask tool is active the tool bar owns Invert (desktop and tablet); the phone never enters the tool.
  const phone = useMediaQuery(PHONE_MEDIA_QUERY) ?? false;
  const toolBarOwns = useEditorStore(store, (s) => s.ui.viewerTool === 'mask') && !phone;
  const item = paused && !toolBarOwns ? selectedFrameItem(doc, selection, frame) : null;
  if (!item || item.type !== 'mediaClip' || !(item as MediaClip).mask) return null;
  const clip = item as MediaClip;
  const inverted = clip.mask?.invert === true;
  return (
    <Button
      data-testid="viewer-mask-invert"
      size={coarse ? 'md' : 'compact-sm'}
      fz={12}
      variant={inverted ? 'light' : 'default'}
      aria-pressed={inverted}
      disabled={readOnly}
      leftSection={<IconContrast size={14} />}
      onClick={() => commitFrameOps(store, toggleInvertOps(clip), tx('invertMask2'))}
      style={coarse ? { minHeight: 44 } : undefined}
    >
      {tx('invertMask')}
    </Button>
  );
}
