'use client';

import React, { useEffect, useMemo, useRef, useState } from 'react';
import { UnstyledButton, useComputedColorScheme } from '@mantine/core';
import { IconChevronLeft, IconChevronRight } from '@tabler/icons-react';
import type { EditTimeline } from '@/lib/editing/types';
import { framesToTimecode } from '@/lib/editing/time';
import {
  LONG_PRESS_MS,
  TAP_SLOP_PX,
  clampZoom,
  distanceBetween,
  inEdgeDeadZone,
  isDoubleTap,
  midpointOf,
  pinchViewport,
  zoomToRange,
  type TapRecord,
} from '@/lib/editing/timeline-gestures';
import { TOUCH_SURFACE_STYLE } from '../timeline/TimelineCanvas';
import { readClipColors, withClipColors } from '../timeline/editorTheme';
import { DARK_THEME, LIGHT_THEME, hitTestBadge, hitTestJobChip, paintTimeline, totalTimelineHeight, type PaintContext, type PaintUi } from '../timeline/paint';
import {
  adjacentClip,
  describeSelectionMsg,
  handleRects,
  laneRow,
  panPlayhead,
  stripClips,
  stripDoc,
  stripFrameToX,
  stripHitTest,
  stripLanes,
  stripViewport,
  timelineEnd,
} from './strip-geometry';
import { useTouchDragEnabled } from './touch-drag-flag';
import { requestClipSheet } from './clip-sheet-bus';
import classes from './phone.module.css';
import type { ClipBadgeKind } from '@/lib/editing/display/clip-badges';
import { useT } from '@/i18n';

export interface ClipStripProps {
  doc: EditTimeline;
  playheadFrame: number;
  selection: readonly string[];
  inFrame: number | null;
  outFrame: number | null;
  pixelsPerFrame: number;
  trackFlags: PaintUi['trackFlags'];
  parityOf: PaintUi['parityOf'];
  rationaleFrames: readonly number[];
  clipArt?: PaintUi['clipArt'];
  artVersion?: number;
  view?: PaintUi['view'];
  isLinked?: PaintUi['isLinked'];
  busyClipIds?: PaintUi['busyClipIds'];
  badgesOf?: PaintUi['badgesOf'];
  jobOf?: PaintUi['jobOf'];
  /** A tap on a clip's badge: the editor opens the clip sheet at that section. */
  onClipBadge?: (target: { itemId: string; kind: ClipBadgeKind | 'more' }) => void;
  /** A tap on a clip's job chip: review, or the error text. */
  onClipJob?: (target: { itemId: string; job: NonNullable<ReturnType<NonNullable<PaintUi['jobOf']>>> }) => void;
  readOnly?: boolean;
  /** Moves the playhead (the strip's pan is a seek). */
  onSeek: (frame: number) => void;
  /** Same selection model as the desktop: a tap selects one clip, a tap on empty space clears it. */
  onSelect: (itemId: string | null, modifiers: { shift: boolean; toggle: boolean }) => void;
  onZoom: (pixelsPerFrame: number) => void;
  /** The same trim the desktop handle drag issues; the editor applies the active trim mode. */
  onTrimItem: (itemId: string, edge: 'in' | 'out', deltaFrames: number) => void;
  /** A still finger held on a clip (touch drag off): open the clip action menu there. */
  onClipMenu?: (target: { itemId: string; clientX: number; clientY: number }) => void;
  /** A clip picked up by a long press and dragged (touch drag on): move it on its own track. */
  onMoveItem?: (itemId: string, toTrackId: string, toRecordIn: number) => void;
}

type Touch =
  | { kind: 'one'; id: number; downX: number; downY: number; x: number; y: number; startPlayhead: number; startOffset: number; panning: boolean; hitId: string | null; hitTrackId: string | null; hitIn: number; held: 'no' | 'menu' | 'pickup'; timer: ReturnType<typeof setTimeout> | null }
  | { kind: 'two'; ids: [number, number]; startDistance: number; startPpf: number; startMidX: number; startPlayhead: number }
  | { kind: 'settle' };

/**
 * The phone's clip strip (RM.4): the same document, selection and ops as the desktop timeline, with a
 * centre-locked playhead and touch gestures. It paints with the desktop's own `paintTimeline` (posters,
 * waveforms, labels, --rb-clip-* colours) over a presentation-only copy of the document whose lanes are
 * resized: the primary video track and its linked audio are full lanes, the rest thin summary lanes.
 *
 *   one finger: tap selects (never seeks); drag pans, which seeks
 *   two fingers: pinch zooms time; moving both pans without touching the selection
 *   double tap on a clip: zoom to fit it
 */
export function ClipStrip(props: ClipStripProps) {
  const tx = useT('editor.phone.clipStrip');
  const { doc, playheadFrame, selection, pixelsPerFrame, onSeek, onSelect, onZoom, onTrimItem } = props;
  const wrapperRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const [size, setSize] = useState({ width: 320, height: 160 });
  const [laneOffset, setLaneOffset] = useState(0);
  const touchDrag = useTouchDragEnabled();
  const [pickup, setPickup] = useState<{ itemId: string; dx: number } | null>(null);
  const [trimDrag, setTrimDrag] = useState<{ itemId: string; edge: 'in' | 'out'; dx: number } | null>(null);
  const pointers = useRef(new Map<number, { x: number; y: number }>());
  const touch = useRef<Touch | null>(null);
  const lastTap = useRef<TapRecord | null>(null);
  const latest = useRef({ playheadFrame, pixelsPerFrame });
  useEffect(() => {
    latest.current = { playheadFrame, pixelsPerFrame };
  });

  const lanes = useMemo(() => stripLanes(doc), [doc]);
  const sdoc = useMemo(() => stripDoc(doc, lanes), [doc, lanes]);
  const selectionSet = useMemo(() => new Set(selection), [selection]);
  const ui: PaintUi = useMemo(
    () => ({
      playheadFrame,
      selection: selectionSet,
      inFrame: props.inFrame,
      outFrame: props.outFrame,
      trackFlags: props.trackFlags,
      parityOf: props.parityOf,
      rationaleFrames: props.rationaleFrames,
      clipArt: props.clipArt,
      view: props.view,
      isLinked: props.isLinked,
      busyClipIds: props.busyClipIds,
      badgesOf: props.badgesOf,
      jobOf: props.jobOf,
    }),
    // `artVersion` is a dependency on purpose: the art object is stable, its contents change.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [playheadFrame, selectionSet, props.inFrame, props.outFrame, props.trackFlags, props.parityOf, props.rationaleFrames, props.clipArt, props.view, props.isLinked, props.busyClipIds, props.badgesOf, props.jobOf, props.artVersion],
  );
  const scheme = useComputedColorScheme('dark', { getInitialValueInEffect: true });
  const theme = useMemo(() => withClipColors(scheme === 'light' ? LIGHT_THEME : DARK_THEME, readClipColors(scheme === 'light' ? 'light' : 'dark')), [scheme]);

  const contentHeight = totalTimelineHeight(sdoc, ui);
  const canvasHeight = Math.max(size.height, contentHeight);
  const maxOffset = Math.max(0, contentHeight - size.height);
  const offset = Math.min(laneOffset, maxOffset);
  const dpr = typeof window !== 'undefined' ? window.devicePixelRatio || 1 : 1;
  const viewport = useMemo(() => stripViewport(playheadFrame, size.width, canvasHeight, pixelsPerFrame, dpr), [playheadFrame, size.width, canvasHeight, pixelsPerFrame, dpr]);
  const lastFrame = timelineEnd(doc);

  useEffect(() => {
    const el = wrapperRef.current;
    if (!el) return;
    const update = () => {
      const rect = el.getBoundingClientRect();
      setSize({ width: Math.max(1, Math.floor(rect.width)), height: Math.max(1, Math.floor(rect.height)) });
    };
    update();
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(update);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const backingW = Math.round(size.width * dpr);
    const backingH = Math.round(canvasHeight * dpr);
    if (canvas.width !== backingW) canvas.width = backingW;
    if (canvas.height !== backingH) canvas.height = backingH;
    const ctx = canvas.getContext('2d') as unknown as PaintContext | null;
    if (!ctx) return;
    paintTimeline(ctx, sdoc, viewport, ui, theme);
  }, [sdoc, viewport, ui, size.width, canvasHeight, dpr, theme]);

  const local = (event: React.PointerEvent): { x: number; y: number } => {
    const rect = wrapperRef.current?.getBoundingClientRect();
    return { x: event.clientX - (rect?.left ?? 0), y: event.clientY - (rect?.top ?? 0) + offset };
  };

  const onPointerDown = (event: React.PointerEvent<HTMLDivElement>) => {
    if (event.pointerType === 'touch' && inEdgeDeadZone(event.clientX, typeof window === 'undefined' ? 1000 : window.innerWidth)) return;
    const p = local(event);
    pointers.current.set(event.pointerId, { x: event.clientX, y: event.clientY });
    try {
      event.currentTarget.setPointerCapture(event.pointerId);
    } catch {
      // Capture is best effort (a synthetic pointer has nothing to capture).
    }
    if (pointers.current.size === 1) {
      const hit = stripHitTest(sdoc, viewport, ui, lanes, p.x, p.y);
      touch.current = { kind: 'one', id: event.pointerId, downX: event.clientX, downY: event.clientY, x: event.clientX, y: event.clientY, startPlayhead: latest.current.playheadFrame, startOffset: offset, panning: false, hitId: hit?.item.id ?? null, hitTrackId: hit?.track.id ?? null, hitIn: hit?.item.recordIn ?? 0, held: 'no', timer: null };
      const one = touch.current;
      if (hit && !props.readOnly) {
        // A still finger held on a clip: the clip menu, or (touch drag on) the clip is picked up.
        one.timer = setTimeout(() => {
          if (touch.current !== one || one.panning) return;
          if (touchDrag && props.onMoveItem) {
            one.held = 'pickup';
            onSelect(hit.item.id, { shift: false, toggle: false });
            setPickup({ itemId: hit.item.id, dx: 0 });
          } else if (props.onClipMenu) {
            one.held = 'menu';
            onSelect(hit.item.id, { shift: false, toggle: false });
            props.onClipMenu({ itemId: hit.item.id, clientX: one.x, clientY: one.y });
          }
        }, LONG_PRESS_MS);
      }
    } else if (pointers.current.size === 2) {
      const [a, b] = [...pointers.current.entries()];
      touch.current = {
        kind: 'two',
        ids: [a[0], b[0]],
        startDistance: distanceBetween(a[1], b[1]),
        startPpf: latest.current.pixelsPerFrame,
        startMidX: midpointOf(a[1], b[1]).x,
        startPlayhead: latest.current.playheadFrame,
      };
    } else {
      touch.current = { kind: 'settle' };
    }
  };

  const onPointerMove = (event: React.PointerEvent<HTMLDivElement>) => {
    const t = touch.current;
    if (!pointers.current.has(event.pointerId) || !t) return;
    pointers.current.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (t.kind === 'one' && t.id === event.pointerId) {
      const dx = event.clientX - t.downX;
      const dy = event.clientY - t.downY;
      if (t.held === 'pickup') {
        setPickup({ itemId: t.hitId ?? '', dx });
        return;
      }
      if (t.held === 'menu') return;
      if (!t.panning && Math.hypot(dx, dy) <= TAP_SLOP_PX) return;
      if (t.timer) clearTimeout(t.timer);
      t.panning = true;
      t.x = event.clientX;
      t.y = event.clientY;
      if (Math.abs(dx) >= Math.abs(dy) || maxOffset === 0) onSeek(panPlayhead(t.startPlayhead, dx, latest.current.pixelsPerFrame, lastFrame));
      else setLaneOffset(Math.min(maxOffset, Math.max(0, t.startOffset - dy)));
    } else if (t.kind === 'two' && t.ids.includes(event.pointerId)) {
      const a = pointers.current.get(t.ids[0]);
      const b = pointers.current.get(t.ids[1]);
      if (!a || !b) return;
      const mid = midpointOf(a, b);
      const zoomed = pinchViewport({ distance: t.startDistance, pixelsPerFrame: t.startPpf, anchorFrame: t.startPlayhead }, distanceBetween(a, b), size.width / 2);
      const ppf = clampZoom(zoomed.pixelsPerFrame);
      if (ppf !== latest.current.pixelsPerFrame) onZoom(ppf);
      onSeek(panPlayhead(t.startPlayhead, mid.x - t.startMidX, ppf, lastFrame));
    }
  };

  const finish = (event: React.PointerEvent<HTMLDivElement>, cancelled: boolean) => {
    const t = touch.current;
    pointers.current.delete(event.pointerId);
    if (pointers.current.size === 0) touch.current = null;
    else if (t && t.kind !== 'settle') touch.current = { kind: 'settle' };
    if (t?.kind === 'one' && t.timer) clearTimeout(t.timer);
    if (t?.kind === 'one' && t.held === 'pickup') {
      const delta = Math.round((event.clientX - t.downX) / latest.current.pixelsPerFrame);
      setPickup(null);
      if (!cancelled && delta !== 0 && t.hitId && t.hitTrackId) props.onMoveItem?.(t.hitId, t.hitTrackId, Math.max(0, t.hitIn + delta));
      return;
    }
    if (cancelled || !t || t.kind !== 'one' || t.id !== event.pointerId || t.panning || t.held !== 'no') return;
    // A tap on a job chip or an adjustment badge opens that, after selecting the clip.
    if (t.hitId !== null) {
      const p = local(event);
      const jobTap = props.onClipJob ? hitTestJobChip(sdoc, viewport, ui, p.x, p.y) : null;
      if (jobTap && jobTap.job.state !== 'running') {
        onSelect(jobTap.item.id, { shift: false, toggle: false });
        props.onClipJob?.({ itemId: jobTap.item.id, job: jobTap.job });
        return;
      }
      const badgeTap = props.onClipBadge ? hitTestBadge(sdoc, viewport, ui, p.x, p.y) : null;
      if (badgeTap) {
        onSelect(badgeTap.item.id, { shift: false, toggle: false });
        props.onClipBadge?.({ itemId: badgeTap.item.id, kind: badgeTap.rect.kind });
        return;
      }
    }
    // A tap selects and never seeks. A tap on empty space clears the selection.
    const id = t.hitId;
    const record: TapRecord = { time: event.timeStamp, x: event.clientX, y: event.clientY, itemId: id };
    if (id !== null && isDoubleTap(lastTap.current, record)) {
      lastTap.current = null;
      zoomToClip(id);
      return;
    }
    lastTap.current = record;
    onSelect(id, { shift: false, toggle: false });
  };

  const zoomToClip = (itemId: string) => {
    const item = doc.tracks.flatMap((tr) => tr.items).find((i) => i.id === itemId);
    if (!item) return;
    onZoom(zoomToRange(item.recordIn, item.recordOut, size.width).pixelsPerFrame);
    onSeek(Math.round((item.recordIn + item.recordOut) / 2));
  };

  // The Fit chip (the legacy toolbar's "Zoom to fit"): the whole programme across the strip, the playhead in the middle.
  const fit = () => {
    if (lastFrame <= 0) return;
    onZoom(zoomToRange(0, lastFrame, size.width).pixelsPerFrame);
    onSeek(Math.round(lastFrame / 2));
  };

  const clips = useMemo(() => stripClips(doc, lanes), [doc, lanes]);
  const selectedId = selection[0] ?? null;
  const step = (direction: -1 | 1) => {
    const target = adjacentClip(clips, selectedId, playheadFrame, direction);
    if (!target) return;
    onSelect(target.id, { shift: false, toggle: false });
    onSeek(target.recordIn);
  };
  const hasPrev = adjacentClip(clips, selectedId, playheadFrame, -1) !== null;
  const hasNext = adjacentClip(clips, selectedId, playheadFrame, 1) !== null;
  const description = tx.msg(describeSelectionMsg(doc, lanes, selection));

  // Trim handles: the selected clip only, in a direct lane.
  const selectedItem = selectedId === null ? null : doc.tracks.flatMap((tr) => tr.items.map((item) => ({ item, trackId: tr.id }))).find((e) => e.item.id === selectedId) ?? null;
  const handles: React.ReactNode[] = [];
  if (selectedItem && !props.readOnly && lanes.fullIds.has(selectedItem.trackId)) {
    const row = laneRow(sdoc, ui, selectedItem.trackId);
    if (row) {
      for (const edge of ['in', 'out'] as const) {
        const frame = edge === 'in' ? selectedItem.item.recordIn : selectedItem.item.recordOut;
        const dragDx = trimDrag && trimDrag.itemId === selectedItem.item.id && trimDrag.edge === edge ? trimDrag.dx : 0;
        const edgeX = stripFrameToX(frame, playheadFrame, size.width, pixelsPerFrame) + dragDx;
        if (edgeX < -22 || edgeX > size.width + 22) continue;
        const r = handleRects(edgeX, edge);
        handles.push(
          <div
            key={edge}
            aria-hidden
            data-testid={`trim-handle-${edge}`}
            className={classes.stripHandle}
            style={{ left: r.hitX, width: r.hitW, top: row.y, height: row.height }}
            onPointerDown={(e) => {
              e.stopPropagation();
              e.currentTarget.setPointerCapture?.(e.pointerId);
              setTrimDrag({ itemId: selectedItem.item.id, edge, dx: 0 });
              (e.currentTarget as HTMLElement).dataset.startX = String(e.clientX);
            }}
            onPointerMove={(e) => {
              if (!trimDrag) return;
              const startX = Number((e.currentTarget as HTMLElement).dataset.startX ?? e.clientX);
              setTrimDrag({ ...trimDrag, dx: e.clientX - startX });
            }}
            onPointerUp={(e) => {
              e.stopPropagation();
              if (!trimDrag) return;
              const delta = Math.round(trimDrag.dx / pixelsPerFrame);
              setTrimDrag(null);
              if (delta !== 0) onTrimItem(trimDrag.itemId, edge, delta);
            }}
            onPointerCancel={() => setTrimDrag(null)}
          >
            <span className={classes.stripHandleBar} style={{ left: r.paintX - r.hitX, width: r.paintW }} aria-hidden />
          </div>,
        );
      }
    }
  }

  return (
    <section className={classes.strip} aria-label={tx('clipStrip')} data-testid="clip-strip">
      <div className={classes.stripBar}>
        <UnstyledButton className={classes.stripStep} onClick={() => step(-1)} disabled={!hasPrev} aria-label={tx('previousClip')} data-testid="strip-prev">
          <IconChevronLeft size={20} aria-hidden />
        </UnstyledButton>
        <div className={classes.stripReadout}>
          <span className={classes.stripTimecode} data-testid="status-timecode">
            {framesToTimecode(playheadFrame, doc.settings.rate)}
          </span>
          <span className={classes.stripDescription} data-testid="strip-description" role="status">
            {description}
          </span>
        </div>
        {selectedItem?.item.type === 'mediaClip' && !props.readOnly && (
          <UnstyledButton className={classes.stripChip} onClick={() => requestClipSheet('picture')} aria-label={tx('clipSettingsFitPosition')} data-testid="strip-clip-chip">
            {tx('clip')}
          </UnstyledButton>
        )}
        <UnstyledButton className={classes.stripFit} onClick={fit} disabled={lastFrame <= 0} aria-label={tx('fitTheWholeTimeline')} data-testid="strip-fit">
          {tx('fit')}
        </UnstyledButton>
        <UnstyledButton className={classes.stripStep} onClick={() => step(1)} disabled={!hasNext} aria-label={tx('nextClip')} data-testid="strip-next">
          <IconChevronRight size={20} aria-hidden />
        </UnstyledButton>
      </div>
      <div
        ref={wrapperRef}
        className={classes.stripSurface}
        data-testid="timeline-canvas-wrapper"
        style={TOUCH_SURFACE_STYLE}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={(e) => finish(e, false)}
        onPointerCancel={(e) => finish(e, true)}
      >
        <div style={{ position: 'absolute', left: 0, top: -offset, width: size.width, height: canvasHeight }}>
          <canvas
            ref={canvasRef}
            style={{ ...TOUCH_SURFACE_STYLE, width: size.width, height: canvasHeight, display: 'block' }}
            aria-hidden
            data-testid="timeline-canvas"
            data-centre-lock="true"
            data-playhead-frame={playheadFrame}
            data-ppf={pixelsPerFrame}
          />
          {pickup && selectedItem && (
            <div className={classes.stripPickup} style={{ left: stripFrameToX(selectedItem.item.recordIn, playheadFrame, size.width, pixelsPerFrame) + pickup.dx, width: Math.max(8, (selectedItem.item.recordOut - selectedItem.item.recordIn) * pixelsPerFrame), top: laneRow(sdoc, ui, selectedItem.trackId)?.y ?? 0, height: laneRow(sdoc, ui, selectedItem.trackId)?.height ?? 56 }} aria-hidden data-testid="strip-pickup" />
          )}
          {handles}
        </div>
      </div>
    </section>
  );
}
