'use client';

import React, { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react';
import { Box, Button, Group, Popover, SimpleGrid, Stack, Text, Tooltip } from '@mantine/core';
import { EditorIcon } from '../ui';
import { useMediaQuery } from '@mantine/hooks';
import { IconArrowsMaximize, IconFocus2, IconPictureInPicture } from '@tabler/icons-react';
import type { Caption, EditTimeline, MediaClip, TextPosition, TitleClip } from '@/lib/editing/types';
import type { EditorStore } from '@/lib/editing/store/editor-store';
import { useEditorStore } from '@/lib/editing/store/use-editor-store';
import {
  CORNER_HANDLES,
  EDGE_HANDLES,
  boxAsValues,
  padMagnet,
  scaleFromEdge,
  titleBoxFor,
  titleValuesForBox,
  NUDGE_PX,
  NUDGE_PX_BIG,
  baseSize,
  bounds,
  captionFrame,
  commitTextOps,
  commitTransform,
  cornerPresetValues,
  draftFor,
  fillValues,
  fitValues,
  frameFor,
  hitTest,
  markTransformHintSeen,
  nearestTextPosition,
  pictureTargetsAt,
  selectedPicture,
  textPositionOps,
  titleFrame,
  transformHintSeen,
  localToCanvas,
  moveBy,
  nudgeValues,
  padValues,
  rotateTo,
  rotateVec,
  scaleFromCorner,
  wrapDegrees,
  snapMove,
  snapTargets,
  transformAt,
  type Frame,
  type Guide,
  type HandleSpec,
  type TransformValues,
  type Vec,
} from '@/lib/editing/viewer-transform';
import { useT } from '@/i18n';
import { msg } from '@/i18n/msg';
import classes from './TransformOverlay.module.css';

export interface TransformOverlayProps {
  store: EditorStore;
  /** The picture's size in pixels for a clip (from the media pool's probe); null = unknown (treated as canvas-shaped). */
  sourceSizeFor: (clip: MediaClip) => { width: number; height: number } | null;
  readOnly: boolean;
}

type Gesture = {
  kind: 'move' | 'scale' | 'rotate';
  handle: HandleSpec | null;
  start: TransformValues;
  startFrame: Frame;
  startPointer: Vec;
  moved: boolean;
  /** The rotate handle sits below the picture (no room above), so the pointer's angle is half a turn off. */
  flipped: boolean;
};

const ROTATE_OFFSET_PX = 30;

/** The single selected media clip on a picture track that is on screen at the playhead, else null. */
export function selectedVisualClip(doc: EditTimeline, selection: readonly string[], frame: number): MediaClip | null {
  if (selection.length !== 1) return null;
  for (const track of doc.tracks) {
    if (track.kind !== 'video') continue;
    for (const item of track.items) {
      if (item.id === selection[0] && item.type === 'mediaClip' && frame >= item.recordIn && frame < item.recordOut) return item as MediaClip;
    }
  }
  return null;
}

/**
 * R07.2 / T-013: the picture layer over the program frame. It picks what is under a click (the topmost item,
 * Alt-click the one below, empty canvas clears), and shows the selected item's box and handles while the
 * Transform tool is on. While PLAYING only the outline shows; the first press on it pauses and then edits.
 * Gestures draft into {@link draftFor} (preview only) and write ONE batch of ops on release.
 */
export function TransformOverlay({ store, sourceSizeFor, readOnly }: TransformOverlayProps) {
  const pickable = useEditorStore(store, (s) => s.ui.tool === 'select' && (s.ui.viewerTool === 'select' || s.ui.viewerTool === 'transform'));
  const handlesOn = useEditorStore(store, (s) => s.ui.transformTool !== false);
  const playing = useEditorStore(store, (s) => s.ui.playbackSpeed !== 0);
  const doc = useEditorStore(store, (s) => s.doc);
  const selection = useEditorStore(store, (s) => s.ui.selection);
  const frame = useEditorStore(store, (s) => s.ui.playheadFrame);
  if (!pickable) return null;
  const picked = handlesOn ? selectedPicture(doc, selection, frame) : null;
  return (
    <>
      <PickLayer store={store} sourceSizeFor={sourceSizeFor} />
      {picked && picked.kind !== 'caption' && <OverlayFor key={picked.item.id} store={store} doc={doc} clip={picked.item as MediaClip | TitleClip} frame={frame} playing={playing} sourceSizeFor={sourceSizeFor} readOnly={readOnly} />}
      {picked && picked.kind === 'caption' && <TextOverlayFor key={picked.item.id} store={store} doc={doc} item={picked.item as Caption} playing={playing} readOnly={readOnly} />}
    </>
  );
}

/** Click the picture to select what is under the pointer (Alt-click: the next one below); empty canvas clears the selection. */
function PickLayer({ store, sourceSizeFor }: { store: EditorStore; sourceSizeFor: TransformOverlayProps['sourceSizeFor'] }) {
  const ref = useRef<HTMLDivElement | null>(null);
  const onDown = (e: React.PointerEvent) => {
    if (e.button !== 0 || !ref.current) return;
    const s = store.getState();
    const r = ref.current.getBoundingClientRect();
    if (r.width === 0 || r.height === 0) return;
    const p = { x: ((e.clientX - r.left) / r.width) * s.doc.settings.width, y: ((e.clientY - r.top) / r.height) * s.doc.settings.height };
    const targets = pictureTargetsAt(s.doc, s.ui.playheadFrame, sourceSizeFor);
    const current = s.ui.selection.length === 1 ? s.ui.selection[0] : null;
    const hit = hitTest(targets, p, e.altKey ? current : null);
    store.setUi({ selection: hit ? [hit.id] : [] });
  };
  return <div ref={ref} data-testid="viewer-pick-layer" onPointerDown={onDown} style={{ position: 'absolute', inset: 0, zIndex: 2, touchAction: 'manipulation' }} />;
}

/**
 * The one-line, dismissible "how to use the handles" hint; shown once per session. It lets pointers through (only its button takes
 * them): on a picture that touches the top of the frame the rotate handle sits below the picture, under the hint.
 */
function TransformHint({ x, y, width }: { x: number; y: number; width: number }) {
  const t = useT('editor.viewer.transformOverlay');
  const [open, setOpen] = useState(() => !transformHintSeen());
  useEffect(() => {
    if (open) markTransformHintSeen();
  }, [open]);
  if (!open) return null;
  const w = Math.min(width - 16, 440);
  return (
    <Box
      data-testid="viewer-xform-hint"
      role="note"
      style={{ position: 'absolute', left: Math.max(8, Math.min(x - w / 2, width - w - 8)), top: y, width: w, display: 'flex', alignItems: 'center', gap: 8, padding: '4px 8px', background: 'var(--rb-ed-header-bg)', border: '1px solid var(--rb-ed-line)', borderRadius: 4, pointerEvents: 'none', zIndex: 4 }}
    >
      <Text fz={12} style={{ flex: 1, minWidth: 0 }}>
        {t('hint')}
      </Text>
      <Button data-testid="viewer-xform-hint-dismiss" size="compact-xs" fz={12} variant="default" style={{ pointerEvents: 'auto' }} onClick={() => setOpen(false)}>
        {t('hintDismiss')}
      </Button>
    </Box>
  );
}

function useRootSize(rootRef: React.RefObject<HTMLDivElement | null>) {
  const [size, setSize] = useState({ w: 0, h: 0 });
  useEffect(() => {
    const el = rootRef.current;
    if (!el) return;
    const read = () => setSize({ w: el.clientWidth, h: el.clientHeight });
    read();
    const ro = new ResizeObserver(read);
    ro.observe(el);
    return () => ro.disconnect();
  }, [rootRef]);
  return size;
}

function OverlayFor({ store, doc, clip, frame, playing, sourceSizeFor, readOnly }: { store: EditorStore; doc: EditTimeline; clip: MediaClip | TitleClip; frame: number; playing: boolean; sourceSizeFor: TransformOverlayProps['sourceSizeFor']; readOnly: boolean }) {
  const t = useT('editor.viewer.transformOverlay');
  const coarse = useMediaQuery('(pointer: coarse)') ?? false;
  const rootRef = useRef<HTMLDivElement | null>(null);
  const size = useRootSize(rootRef);

  const draftStore = draftFor(store);
  const draft = useSyncExternalStore(draftStore.subscribe, draftStore.get, draftStore.get);
  const canvas = useMemo(() => ({ width: doc.settings.width, height: doc.settings.height }), [doc.settings.width, doc.settings.height]);
  const policy = doc.settings.aspectPolicy;
  const baseOf = useCallback((c: MediaClip) => baseSize(canvas, sourceSizeFor(c), policy, c.crop ?? null), [canvas, sourceSizeFor, policy]);
  // A title's box is its text box (rotation 0 at its anchor); the transform moves, scales and rotates it as the render does.
  const title = clip.type === 'titleClip' ? clip : null;
  const titleBase = useMemo(() => (title ? titleFrame(title, canvas) : null), [title, canvas]);
  const base = title && titleBase ? { w: titleBase.w, h: titleBase.h } : baseOf(clip as MediaClip);
  const frameOf = (v: TransformValues): Frame => (title && titleBase ? titleBoxFor(titleBase, v, canvas) : frameFor(v, base, canvas));
  // The drag math runs in the media-clip convention (centre = canvas centre + position); a title converts in and out of it.
  const toVirtual = (v: TransformValues): TransformValues => (title ? boxAsValues(frameOf(v), base, canvas, v.scaleY !== undefined) : v);
  const fromVirtual = (virt: TransformValues, real: TransformValues): TransformValues => (title && titleBase ? titleValuesForBox(titleBase, frameFor(virt, base, canvas), real, canvas) : virt);
  const values = draft && draft.itemId === clip.id ? draft.values : transformAt(clip, frame);
  const k = size.w > 0 ? size.w / canvas.width : 0;
  const frameBox = frameOf(values);

  const others = useMemo(() => {
    const out: Frame[] = [];
    for (const track of doc.tracks) {
      if (track.kind !== 'video') continue;
      for (const item of track.items) {
        if (item.id === clip.id || item.type !== 'mediaClip' || frame < item.recordIn || frame >= item.recordOut) continue;
        out.push(frameFor(transformAt(item as MediaClip, frame), baseOf(item as MediaClip), canvas));
      }
    }
    return out;
  }, [doc, clip.id, frame, canvas, baseOf]);

  const hit = coarse ? 44 : 24;
  const screen = (local: Vec): Vec => {
    const p = localToCanvas(frameBox, local);
    return { x: p.x * k, y: p.y * k };
  };
  // The rotate handle sits above the top edge; with no room there (a picture flush with the top of the frame) it flips below the bottom edge.
  const rotate = (() => {
    const top = screen({ x: 0, y: -0.5 });
    const up = rotateVec({ x: 0, y: -ROTATE_OFFSET_PX }, frameBox.rotation);
    const fitsAbove = top.y + up.y >= hit / 2 && top.x + up.x >= hit / 2 && top.x + up.x <= size.w - hit / 2;
    if (fitsAbove) return { x: top.x + up.x, y: top.y + up.y, flipped: false };
    const bottom = screen({ x: 0, y: 0.5 });
    return { x: bottom.x - up.x, y: bottom.y - up.y, flipped: true };
  })();

  const [guides, setGuides] = useState<Guide[]>([]);
  const gesture = useRef<Gesture | null>(null);
  const targetsRef = useRef<Guide[]>([]);

  // Escape during a drag cancels the drag and nothing else (the editor's global handler would otherwise deselect);
  // the release afterwards writes nothing. An unmount mid-drag clears the draft so the Inspector never shows an unstored value.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || !gesture.current) return;
      e.preventDefault();
      e.stopPropagation();
      gesture.current = null;
      setGuides([]);
      draftStore.set(null);
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [draftStore]);
  useEffect(
    () => () => {
      if (gesture.current) {
        gesture.current = null;
        draftStore.set(null);
      }
    },
    [draftStore],
  );

  const pointerCanvas = (e: React.PointerEvent): Vec => {
    const r = rootRef.current!.getBoundingClientRect();
    return { x: ((e.clientX - r.left) / r.width) * canvas.width, y: ((e.clientY - r.top) / r.height) * canvas.height };
  };

  const begin = (e: React.PointerEvent, kind: Gesture['kind'], handle: HandleSpec | null) => {
    if (readOnly || k === 0) return;
    e.stopPropagation();
    // While playing the first press pauses, then the gesture edits the frame the playhead stopped on.
    if (playing) store.setUi({ playbackSpeed: 0 });
    (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
    rootRef.current?.focus({ preventScroll: true });
    const start = transformAt(clip, frame);
    gesture.current = { kind, handle, start, startFrame: frameOf(start), startPointer: pointerCanvas(e), moved: false, flipped: kind === 'rotate' && rotate.flipped };
    targetsRef.current = snapTargets(canvas, others);
  };

  const onMove = (e: React.PointerEvent) => {
    const g = gesture.current;
    if (!g) return;
    const p = pointerCanvas(e);
    let next: TransformValues = g.start;
    let fired: Guide[] = [];
    if (g.kind === 'move') {
      const dx = p.x - g.startPointer.x;
      const dy = p.y - g.startPointer.y;
      next = moveBy(g.start, dx, dy, canvas);
      if (!(e.ctrlKey || e.metaKey || e.shiftKey)) {
        // A title first magnets to the nine pad anchors; otherwise the usual guides snap.
        const magnet = title ? padMagnet(title, frameOf(next), canvas, 12 / (k || 1)) : null;
        if (magnet && magnet.position) {
          next = moveBy(next, magnet.dx, magnet.dy, canvas);
        } else {
          const snap = snapMove(frameOf(next), targetsRef.current, 8 / (k || 1));
          next = moveBy(next, snap.dx, snap.dy, canvas);
          fired = snap.guides;
        }
      }
    } else if (g.kind === 'scale' && g.handle) {
      // Free drag (corners change the ratio, edges stretch one axis); Shift keeps the ratio; Alt scales from the centre.
      const options = { fromCentre: e.altKey, keepAspect: e.shiftKey };
      const scaled = g.handle.kind === 'edge' ? scaleFromEdge(toVirtual(g.start), g.startFrame, g.handle, p, canvas, options) : scaleFromCorner(toVirtual(g.start), g.startFrame, g.handle, p, canvas, options);
      next = fromVirtual(scaled, g.start);
    } else if (g.kind === 'rotate') {
      next = fromVirtual({ ...toVirtual(g.start), rotation: wrapDegrees(rotateTo({ x: g.startFrame.cx, y: g.startFrame.cy }, p, e.shiftKey ? 15 : 0) + (g.flipped ? 180 : 0)) }, g.start);
    }
    g.moved = true;
    setGuides(fired);
    draftStore.set({ itemId: clip.id, values: next });
  };

  const end = (e: React.PointerEvent, cancel: boolean) => {
    const g = gesture.current;
    if (!g) return;
    gesture.current = null;
    setGuides([]);
    const live = draftStore.get();
    draftStore.set(null);
    if (cancel || !g.moved || !live) return;
    const label = g.kind === 'move' ? t('movePicture') : g.kind === 'scale' ? t('scalePicture') : t('rotatePicture2');
    try {
      commitTransform(store, clip, live.values, frame, label);
    } catch {
      /* a refused op leaves the document untouched; the draft is already cleared */
    }
  };

  const nudge = (e: React.KeyboardEvent) => {
    if (readOnly || playing || gesture.current) return;
    const step = e.shiftKey ? NUDGE_PX_BIG : NUDGE_PX;
    const d: Record<string, Vec> = { ArrowLeft: { x: -step, y: 0 }, ArrowRight: { x: step, y: 0 }, ArrowUp: { x: 0, y: -step }, ArrowDown: { x: 0, y: step } };
    const v = d[e.key];
    if (!v) return;
    e.preventDefault();
    e.stopPropagation();
    commitTransform(store, clip, nudgeValues(transformAt(clip, frame), v.x, v.y, canvas), frame, t('nudgePicture'));
  };


  return (
    <Box
      ref={rootRef}
      data-testid="viewer-transform"
      role="group"
      aria-label={t('pictureHandlesArrowKeys')}
      tabIndex={0}
      className={classes.root}
      onKeyDown={nudge}
      style={{ position: 'absolute', inset: 0, zIndex: 3, touchAction: 'none', pointerEvents: 'none' }}
    >
      {k > 0 && (
        <>
          <div
            data-testid="viewer-xform-body"
            onPointerDown={(e) => begin(e, 'move', null)}
            onPointerMove={onMove}
            onPointerUp={(e) => end(e, false)}
            onPointerCancel={(e) => end(e, true)}
            style={{
              position: 'absolute',
              left: frameBox.cx * k,
              top: frameBox.cy * k,
              width: frameBox.w * k,
              height: frameBox.h * k,
              transform: `translate(-50%, -50%) rotate(${frameBox.rotation}deg)`,
              border: '1px solid rgba(77,171,247,0.95)',
              cursor: readOnly ? 'var(--rb-cursor-default)' : 'var(--rb-cursor-move)',
              pointerEvents: 'auto',
              touchAction: 'none',
              boxSizing: 'border-box',
            }}
          />
          {!readOnly && !playing &&
            [...EDGE_HANDLES, ...CORNER_HANDLES].map((h) => {
              const p = screen(h.local);
              const edge = h.kind === 'edge';
              return (
                <div
                  key={h.id}
                  data-testid={`viewer-xform-handle-${h.id}`}
                  role="button"
                  aria-roledescription={t.msg(msg('editor-helpers-2.fmt.dragHandle'))}
                  aria-label={t.msg(edge ? msg('editor-helpers-2.fmt.scaleEdge', { edge: h.id }) : msg('editor-helpers-2.fmt.scaleCorner', { corner: h.id }))}
                  // eslint-disable-next-line react-hooks/refs -- an event handler: the gesture refs are read when the pointer goes down, never during render
                  onPointerDown={(e) => begin(e, 'scale', h)}
                  onPointerMove={onMove}
                  onPointerUp={(e) => end(e, false)}
                  onPointerCancel={(e) => end(e, true)}
                  style={{ position: 'absolute', left: clampBox(p.x - hit / 2, size.w, hit), top: clampBox(p.y - hit / 2, size.h, hit), width: hit, height: hit, display: 'flex', alignItems: 'center', justifyContent: 'center', cursor: edge ? (h.local.x !== 0 ? 'ew-resize' : 'ns-resize') : 'nwse-resize', pointerEvents: 'auto', touchAction: 'none' }}
                >
                  <span style={{ width: edge ? 8 : 10, height: edge ? 8 : 10, background: '#fff', border: '2px solid rgb(77,171,247)', borderRadius: edge ? 4 : 2 }} />
                </div>
              );
            })}
          {!readOnly && !playing && (
            <div
              data-testid="viewer-xform-rotate"
              role="button"
              aria-roledescription={t.msg(msg('editor-helpers-2.fmt.dragHandle'))}
              aria-label={t('rotatePicture')}
              onPointerDown={(e) => begin(e, 'rotate', null)}
              onPointerMove={onMove}
              onPointerUp={(e) => end(e, false)}
              onPointerCancel={(e) => end(e, true)}
              style={{ position: 'absolute', left: rotate.x - hit / 2, top: rotate.y - hit / 2, width: hit, height: hit, display: 'flex', alignItems: 'center', justifyContent: 'center', cursor: 'var(--rb-cursor-grab)', pointerEvents: 'auto', touchAction: 'none' }}
            >
              <span style={{ width: 12, height: 12, background: 'rgb(77,171,247)', border: '2px solid #fff', borderRadius: '50%' }} />
            </div>
          )}
          {!readOnly && !playing && <TransformHint x={screen({ x: 0, y: 0.5 }).x} y={Math.min(Math.max(...CORNER_HANDLES.map((h) => screen(h.local).y)) + hit / 2 + 4, Math.max(8, size.h - 40))} width={size.w} />}
          {guides.map((g, i) => (
            <div
              key={`${g.axis}-${g.at}-${i}`}
              data-testid="viewer-xform-guide"
              style={
                g.axis === 'x'
                  ? { position: 'absolute', left: g.at * k, top: 0, bottom: 0, width: 1, background: 'rgba(255,196,0,0.9)', pointerEvents: 'none' }
                  : { position: 'absolute', top: g.at * k, left: 0, right: 0, height: 1, background: 'rgba(255,196,0,0.9)', pointerEvents: 'none' }
              }
            />
          ))}
        </>
      )}
    </Box>
  );
}

/**
 * T-013: a caption on the canvas. Drag moves it to the nearest of the nine pad cells (a dashed ghost shows where it
 * lands). A caption has no free transform, so it keeps the pad; titles use the full box in {@link OverlayFor}.
 */
function TextOverlayFor({ store, doc, item, playing, readOnly }: { store: EditorStore; doc: EditTimeline; item: Caption; playing: boolean; readOnly: boolean }) {
  const t = useT('editor.viewer.transformOverlay');
  const coarse = useMediaQuery('(pointer: coarse)') ?? false;
  const rootRef = useRef<HTMLDivElement | null>(null);
  const size = useRootSize(rootRef);
  const canvas = useMemo(() => ({ width: doc.settings.width, height: doc.settings.height }), [doc.settings.width, doc.settings.height]);
  const k = size.w > 0 ? size.w / canvas.width : 0;
  const [ghost, setGhost] = useState<{ position?: TextPosition } | null>(null);
  const gesture = useRef<{ moved: boolean } | null>(null);
  const ghostRef = useRef(ghost);
  useEffect(() => {
    ghostRef.current = ghost;
  }, [ghost]);
  const hit = coarse ? 44 : 24;

  const baseBox: Frame = captionFrame(item, canvas);
  const shownBox: Frame = ghost ? captionFrame(item, canvas, { position: ghost.position }) : baseBox;
  const showGhost = !!ghost && ghost.position !== undefined;

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || !gesture.current) return;
      e.preventDefault();
      e.stopPropagation();
      gesture.current = null;
      setGhost(null);
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, []);

  const pointerCanvas = (e: React.PointerEvent): Vec => {
    const r = rootRef.current!.getBoundingClientRect();
    return { x: ((e.clientX - r.left) / r.width) * canvas.width, y: ((e.clientY - r.top) / r.height) * canvas.height };
  };
  const begin = (e: React.PointerEvent) => {
    if (readOnly || k === 0) return;
    e.stopPropagation();
    if (playing) store.setUi({ playbackSpeed: 0 });
    (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
    rootRef.current?.focus({ preventScroll: true });
    gesture.current = { moved: false };
  };
  const onMove = (e: React.PointerEvent) => {
    const g = gesture.current;
    if (!g) return;
    const p = pointerCanvas(e);
    g.moved = true;
    setGhost({ position: nearestTextPosition(p, canvas) });
  };
  const end = (cancel: boolean) => {
    const g = gesture.current;
    if (!g) return;
    gesture.current = null;
    const live = ghostRef.current;
    setGhost(null);
    if (cancel || !g.moved || !live) return;
    try {
      if (live.position) commitTextOps(store, textPositionOps(item, live.position), t('moveTitle'));
    } catch {
      /* a refused op leaves the document untouched */
    }
  };
  const boxStyle = (b: Frame, extra: React.CSSProperties): React.CSSProperties => ({ position: 'absolute', left: b.cx * k, top: b.cy * k, width: b.w * k, height: b.h * k, transform: 'translate(-50%, -50%)', boxSizing: 'border-box', ...extra });

  return (
    <Box ref={rootRef} data-testid="viewer-text-transform" role="group" aria-label={t('textBoxLabel')} tabIndex={0} className={classes.root} style={{ position: 'absolute', inset: 0, zIndex: 3, touchAction: 'none', pointerEvents: 'none' }}>
      {k > 0 && (
        <>
          <div
            data-testid="viewer-text-body"
            onPointerDown={begin}
            onPointerMove={onMove}
            onPointerUp={() => end(false)}
            onPointerCancel={() => end(true)}
            style={boxStyle(baseBox, { border: '1px solid rgba(77,171,247,0.95)', cursor: readOnly ? 'var(--rb-cursor-default)' : 'var(--rb-cursor-move)', pointerEvents: 'auto', touchAction: 'none' })}
          />
          {showGhost && <div data-testid="viewer-text-ghost" style={boxStyle(shownBox, { border: '1px dashed rgba(255,196,0,0.95)', pointerEvents: 'none' })} />}
          {!readOnly && !playing && <TransformHint x={baseBox.cx * k} y={Math.min((bounds(baseBox).bottom * k) + hit / 2 + 4, Math.max(8, size.h - 40))} width={size.w} />}
        </>
      )}
    </Box>
  );
}

/** The viewer dock's "Transform" toggle: the picture handles on or off (on by default). Hidden on a phone, where a tap always selects and the Clip sheet keeps its controls. */
export function TransformToolToggle({ store }: { store: EditorStore }) {
  const t = useT('editor.viewer.transformOverlay');
  const coarse = useMediaQuery('(pointer: coarse)') ?? false;
  const on = useEditorStore(store, (s) => s.ui.transformTool !== false);
  return (
    <Tooltip label={t('transformToolTip')} openDelay={300} multiline maw={260}>
      <Button
        data-testid="viewer-transform-tool"
        aria-pressed={on}
        size={coarse ? 'md' : 'compact-sm'}
        fz={12}
        variant={on ? 'light' : 'default'}
        leftSection={<IconArrowsMaximize size={14} />}
        style={coarse ? { minHeight: 44 } : undefined}
        onClick={() => store.setUi({ transformTool: !on, viewerTool: 'select' })}
      >
        {t('transformTool')}
      </Button>
    </Tooltip>
  );
}

/**
 * R07.2: the Fit / Fill / position-pad / picture-in-corner menu. It lives in the viewer's header (never
 * over the picture, where it would sit on top of a picture parked in a corner).
 */
export function TransformTools({ store, sourceSizeFor, readOnly }: TransformOverlayProps) {
  const t = useT('editor.viewer.transformOverlay');
  const coarse = useMediaQuery('(pointer: coarse)') ?? false;
  const doc = useEditorStore(store, (s) => s.doc);
  const selection = useEditorStore(store, (s) => s.ui.selection);
  const frame = useEditorStore(store, (s) => s.ui.playheadFrame);
  const paused = useEditorStore(store, (s) => s.ui.playbackSpeed === 0);
  const clip = paused ? selectedVisualClip(doc, selection, frame) : null;
  if (!clip) return null;
  const canvas = { width: doc.settings.width, height: doc.settings.height };
  const base = baseSize(canvas, sourceSizeFor(clip), doc.settings.aspectPolicy, clip.crop ?? null);
  const apply = (next: TransformValues, label: string) => {
    if (readOnly) return;
    commitTransform(store, clip, next, frame, label);
  };
  return (
    <PositionMenu
      disabled={readOnly}
      coarse={coarse}
      onFit={() => apply(fitValues(base, canvas), t('fitPicture'))}
      onFill={() => apply(fillValues(base, canvas), t('fillPicture'))}
      onPad={(col, row) => apply(padValues(transformAt(clip, frame), base, canvas, col, row), t('placePicture'))}
      onCorner={(col, row) => apply(cornerPresetValues(base, canvas, col, row), t('pictureInCorner2'))}
    />
  );
}

const CELL_NAMES = ['top left', 'top', 'top right', 'left', 'centre', 'right', 'bottom left', 'bottom', 'bottom right'];

/** Keeps a handle's hit box inside the overlay, so a corner handle on a picture that fills the frame never covers the controls next to the viewer. */
function clampBox(start: number, extent: number, hit: number): number {
  return Math.max(0, Math.min(start, extent - hit));
}

function PositionMenu({ disabled, coarse, onFit, onFill, onPad, onCorner }: { disabled: boolean; coarse: boolean; onFit: () => void; onFill: () => void; onPad: (c: -1 | 0 | 1, r: -1 | 0 | 1) => void; onCorner: (c: -1 | 1, r: -1 | 1) => void }) {
  const t = useT('editor.viewer.transformOverlay');
  const [open, setOpen] = useState(false);
  const px = coarse ? 44 : 30;
  const pad = (cls: string, content: React.ReactNode, key: string, label: string, on: () => void) => (
    <Tooltip key={key} label={label} openDelay={300}>
      <EditorIcon data-testid={cls} label={label} variant="default" tooltip={false} disabled={disabled} onClick={on}>
        {content}
      </EditorIcon>
    </Tooltip>
  );
  return (
    <Popover opened={open} onChange={setOpen} position="bottom-end" withArrow shadow="md" withinPortal>
      <Popover.Target>
        <Button data-testid="viewer-xform-menu" size={coarse ? 'md' : 'compact-sm'} fz={12} variant="default" leftSection={<IconFocus2 size={14} />} onClick={() => setOpen((o) => !o)} style={coarse ? { minHeight: 44, minWidth: 44 } : undefined}>
          {t('position')}
        </Button>
      </Popover.Target>
      <Popover.Dropdown data-testid="viewer-xform-popover">
        <Stack gap="xs">
          <Group gap="xs" wrap="nowrap">
            <Button data-testid="viewer-xform-fit" size={coarse ? 'md' : 'xs'} fz={12} variant="default" disabled={disabled} leftSection={<IconArrowsMaximize size={14} />} onClick={onFit} style={coarse ? { minHeight: 44 } : undefined}>
              {t('fit')}
            </Button>
            <Button data-testid="viewer-xform-fill" size={coarse ? 'md' : 'xs'} fz={12} variant="default" disabled={disabled} onClick={onFill} style={coarse ? { minHeight: 44 } : undefined}>
              {t('fill')}
            </Button>
          </Group>
          <Text fz={12} c="dimmed">
            {t('placeOnTheScreen')}
          </Text>
          <SimpleGrid cols={3} spacing={4} w={px * 3 + 8}>
            {CELL_NAMES.map((name, i) => {
              const row = (Math.floor(i / 3) - 1) as -1 | 0 | 1;
              const col = ((i % 3) - 1) as -1 | 0 | 1;
              return pad(`viewer-xform-pad-${i}`, <span style={{ width: 8, height: 8, borderRadius: 2, background: 'currentColor' }} />, `pad-${i}`, `Place ${name}`, () => onPad(col, row));
            })}
          </SimpleGrid>
          <Group gap={4} align="center">
            <IconPictureInPicture size={14} />
            <Text fz={12} c="dimmed">
              {t('pictureInCorner')}
            </Text>
          </Group>
          <SimpleGrid cols={2} spacing={4} w={px * 2 + 4}>
          {/* eslint-disable i18next/no-literal-string -- ids, not text */}
            {([
              ['tl', -1, -1],
              ['tr', 1, -1],
              ['bl', -1, 1],
              ['br', 1, 1],
            ] as const).map(([id, col, row]) =>
              pad(`viewer-xform-corner-${id}`, <span style={{ width: 10, height: 8, borderRadius: 1, border: '1px solid currentColor' }} />, `c-${id}`, t('smallPicture', { name: t(`corners.${id}`) }), () => onCorner(col, row)),
            )}
            {/* eslint-enable i18next/no-literal-string */}
          </SimpleGrid>
        </Stack>
      </Popover.Dropdown>
    </Popover>
  );
}
