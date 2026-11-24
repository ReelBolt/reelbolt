'use client';

import { readClipColors, withClipColors } from './editorTheme';
import React, { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Box, useComputedColorScheme } from '@mantine/core';
import { useMediaQuery, useReducedMotion } from '@mantine/hooks';
import { useFormat, useT } from '@/i18n';
import type { ClipBadgeKind } from '@/lib/editing/display/clip-badges';
import { useDeviceClass } from '@/lib/editing/use-device-class';
import type { EditTimeline, Item, Rational, Track } from '@/lib/editing/types';
import type { EditOp } from '@/lib/editing/engine';
import { SeamControls } from './SeamControls';
import { hitTestEvent } from './eventsLane';
import { EventsLaneKeys } from './EventsLaneKeys';
import type { LaneEvent } from '@/lib/editing/events/events-data';
import { CLIP_DRAG_MIME, CLIP_KIND_MIME_PREFIX, readDragPayload } from '@/lib/editing/media/bins';
import { subscribePoolTouchDrag, type PoolTouchDragDetail } from '@/lib/editing/media/touch-drag';
import { linkIdOf, locate } from '@/lib/editing/link';
import type { SnapSource } from '@/lib/editing/selection/snapping';
import { bodyTrimRequest, describeTrim, planTrimDrag, probeMove, probeTrim, type TrimDragRequest, type TrimPlan } from '@/lib/editing/trim-readout';
import { gapAt, type TimelineGap } from '@/lib/editing/timeline-edit/gap-objects';
import {
  LONG_PRESS_MS,
  classifyOneFinger,
  distanceBetween,
  inEdgeDeadZone,
  isDoubleTap,
  midpointOf,
  pinchViewport,
  zoomToRange,
  type PinchStart,
  type TapRecord,
} from '@/lib/editing/timeline-gestures';
import { useTouchDragEnabled } from '@/lib/editing/touch-drag';
import type { ParityClass } from '@/lib/editing/preview';
import {
  DARK_THEME,
  LIGHT_THEME,
  RULER_HEIGHT,
  headerHeight,
  MARKER_LANE_HEIGHT,
  frameToX,
  hitTestItem,
  hitTestBadge,
  hitTestJobChip,
  paintTimeline,
  totalTimelineHeight,
  trackAtY,
  trackRows,
  xToFrame,
  type PaintContext,
  type PaintStats,
  type PaintUi,
  type Viewport,
} from './paint';

/** What the Events lane needs from its host: the ticks, the words, and the three things a tick can do. */
export interface EventsLaneProps {
  items: readonly LaneEvent[];
  /** Type word -> the label painted at zoom. */
  labels: Readonly<Record<string, string>>;
  /** Snapping to ticks is on. */
  snap: boolean;
  /** The focused or last-chosen tick. */
  activeKey: string | null;
  /** The sentence for a tick (type and time): the hover and focus tooltip, and what a screen reader hears. */
  describe: (event: LaneEvent) => string;
  /** The lane's accessible name and keyboard hint. */
  ariaLabel: string;
  /** Click, tap or keyboard step: the playhead goes to the tick and it becomes the active one. */
  onActivate: (event: LaneEvent) => void;
  /** Right click, long press, tap on touch, Enter or the context-menu key: open "Act on this event" for the tick. */
  onMenu: (event: LaneEvent, at: { clientX: number; clientY: number }) => void;
}

export interface TimelineCanvasProps {
  doc: EditTimeline;
  playheadFrame: number;
  selection: readonly string[];
  inFrame: number | null;
  outFrame: number | null;
  pixelsPerFrame: number;
  scrollFrame: number;
  tool: 'select' | 'blade' | 'trim' | 'hand';
  trackFlags: PaintUi['trackFlags'];
  parityOf: (itemId: string) => ParityClass;
  rationaleFrames: readonly number[];
  /** T-008: the music clip's beat grid in record frames; see PaintUi.beatGrid. */
  beatGrid?: PaintUi['beatGrid'];
  /** The Events lane under the ruler (derived, read-only). Absent = no lane. */
  eventsLane?: EventsLaneProps;
  /** The last render's regions for the ruler strip; see PaintUi.renderRegions. */
  renderRegions?: PaintUi['renderRegions'];
  /** P8: an AI-assist proposal's ghost diff; see PaintUi.ghosts. */
  ghosts?: PaintUi['ghosts'];
  /** R03.4: clip art (thumbnails, waveforms, offline) and the view toggles; `artVersion` changes when art arrives so the canvas repaints. */
  clipArt?: PaintUi['clipArt'];
  artVersion?: number;
  view?: PaintUi['view'];
  isLinked?: PaintUi['isLinked'];
  /** Clips a background job is working on; see PaintUi.busyClipIds. */
  busyClipIds?: PaintUi['busyClipIds'];
  /** What a background job shows on each clip; see PaintUi.jobOf. */
  jobOf?: PaintUi['jobOf'];
  /** True while a job runs with no progress figure: the canvas repaints on a timer for the moving stripe (not under reduced motion). */
  jobsAnimating?: boolean;
  /** A click or tap on a job chip ("Ready to review" opens the review). */
  onClipJob?: (target: { itemId: string; job: NonNullable<ReturnType<NonNullable<PaintUi['jobOf']>>> }) => void;
  /** The adjustment badges per clip (lib/editing/display/clip-badges.ts); see PaintUi.badgesOf. */
  badgesOf?: PaintUi['badgesOf'];
  /** A click or tap on a clip's badge (the clip is selected first): open its section, or the clip's list for the "+N" badge. */
  /** The hover text of a badge (its name, and the parity reason when not faithful); null = none. */
  badgeTitle?: (itemId: string, kind: ClipBadgeKind | 'more') => string | null;
  onClipBadge?: (target: { itemId: string; kind: ClipBadgeKind | 'more' }) => void;
  snap: (frame: number, excludeItemIds?: readonly string[]) => number;
  /** T-020: what a snapped frame caught, for the guide's label. Called when the guide moves, never per pointer move. */
  snapSource?: (frame: number, excludeItemIds?: readonly string[]) => SnapSource | null;
  onSeek: (frame: number) => void;
  onSelect: (itemId: string | null, modifiers: { shift: boolean; toggle: boolean }) => void;
  onMarquee: (rect: { trackLo: number; trackHi: number; frameLo: number; frameHi: number }) => void;
  onBlade: (itemId: string, atFrame: number) => void;
  /** `separately`: Alt/Option held (or the toggle) - edit this clip only, leaving its linked partners alone (J/L cuts). */
  onMoveItem: (itemId: string, toTrackId: string, toRecordIn: number, separately?: boolean) => void;
  onTrimItem: (itemId: string, edge: 'in' | 'out', deltaFrames: number, separately?: boolean) => void;
  /** What a trim drag will really do: the same plan `onTrimItem` dispatches. Absent: the clip on its own. */
  planTrim?: (request: TrimDragRequest) => TrimPlan;
  onZoom: (pixelsPerFrame: number, anchorFrame: number) => void;
  onScroll: (scrollFrame: number) => void;
  onPaintStats?: (stats: PaintStats) => void;
  height?: number;
  /** R03.3: the trim mode a handle drag applies; named in the readout while dragging. */
  trimMode?: 'ripple' | 'roll' | 'slip' | 'slide';
  /** R03.1: the selected gap (an empty span between clips), drawn as a hatched band. */
  selectedGap?: TimelineGap | null;
  /** A click on empty track space between clips selects that gap; null clears it. */
  onSelectGap?: (gap: TimelineGap | null) => void;
  /** Right-click / long-press: the clip under the pointer (already selected) and where to open the menu. */
  /** `area` says what was under the pointer; `frame` is the timeline frame there; `badge` is the adjustment badge hit, if any. */
  onClipContextMenu?: (target: { itemId: string | null; clientX: number; clientY: number; area?: 'clip' | 'lane' | 'ruler'; frame?: number; badge?: ClipBadgeKind | 'more' }) => void;
  /** R07.3/R07.4: the "+" on every cut, transition edit glyphs and fade corner handles. Absent = none. */
  seamControls?: { rate: Rational; newId: () => string; readOnly: boolean; onApply: (ops: EditOp[], label: string) => void };
  /** A read-only overview strip: it seeks and selects, and shows no trim handles or resize cursors. */
  overview?: boolean;
  /** Double-click on a clip. */
  onItemDoubleClick?: (itemId: string) => void;
  /**
   * R05.4: a media-pool tile was dropped on a track at a time. Without it the timeline accepts no pool drops.
   * `frame` is the snapped drop position; the caller decides what placing means (and refuses a wrong-kind track).
   */
  onPoolDrop?: (mediaRef: string, trackId: string, frame: number) => void;
}

/** Why a body drag in Slip or Slide is an ordinary move: said by the cursor and by a hint. */
type BodyFallback = 'noSource' | 'noNeighbour';

type Drag =
  | { kind: 'scrub'; fromFrame: number }
  | { kind: 'marquee'; startX: number; startY: number; x: number; y: number }
  | { kind: 'move'; itemId: string; trackId: string; grabOffsetFrames: number; durationFrames: number; startX: number; startY: number; x: number; y: number; moved: boolean; fallback: BodyFallback | null }
  | { kind: 'trim'; itemId: string; edge: 'in' | 'out'; startFrame: number; currentFrame: number; alt: boolean }
  /** T-019: a clip's body dragged in Slip or Slide mode; commits the same `trim` the handle route sends. */
  | { kind: 'bodytrim'; itemId: string; mode: 'slip' | 'slide'; startX: number; x: number; moved: boolean; alt: boolean }
  | { kind: 'pan'; startX: number; startScroll: number };

/**
 * The canvas, its wrapper and the trim handles own every touch gesture: without `touch-action:
 * none` a phone browser claims the drag for scrolling and sends `pointercancel` after one move.
 */
export const TOUCH_SURFACE_STYLE = { touchAction: 'none', overscrollBehavior: 'contain' } as const;

/**
 * One touch gesture at a time, kept in a ref (not state) so a second finger sees the first at once.
 * `drag` hands the pointer to the shared drag state (ruler scrub, trim handle, picked-up clip).
 */
type TouchGesture =
  | { kind: 'drag'; id: number; sub: 'ruler' | 'trim' | 'pickup' }
  | {
      kind: 'pending';
      id: number;
      downX: number;
      downY: number;
      clientX: number;
      clientY: number;
      x: number;
      y: number;
      downTime: number;
      startScroll: number;
      mode: 'pending' | 'pan' | 'menu';
      hit: { itemId: string; trackId: string; locked: boolean; recordIn: number; recordOut: number } | null;
      timer: ReturnType<typeof setTimeout> | null;
    }
  | { kind: 'pinch'; ids: [number, number]; start: PinchStart; startScroll: number; startPpf: number }
  /** A pinch ended or a third finger landed: ignore the rest until every finger is up. */
  | { kind: 'settle' };


/** Trim handle sizes: painted bar and hit area, for a mouse and for a finger. */
const HANDLE = {
  fine: { hit: 10, paint: 6 },
  coarse: { hit: 44, paint: 10 },
} as const;

/**
 * The canvas timeline. Lanes, items, ruler, markers and the playhead are painted (viewport window
 * only, see `paint.ts`); the ONLY DOM the timeline owns beyond the canvas is the selected item's two
 * trim handles. Device-pixel-ratio aware: the backing store is `dpr ×` the CSS size and the
 * transform is set once per paint, so 1 px lines and text stay crisp.
 */
export function TimelineCanvas(props: TimelineCanvasProps) {
  const {
    doc,
    playheadFrame,
    selection,
    inFrame,
    outFrame,
    pixelsPerFrame,
    scrollFrame,
    tool,
    trackFlags,
    parityOf,
    rationaleFrames,
    beatGrid,
    eventsLane,
    renderRegions,
    ghosts,
    clipArt,
    artVersion = 0,
    view,
    isLinked,
    busyClipIds,
    badgesOf,
    jobOf,
    jobsAnimating,
    onClipJob,
    onClipBadge,
    badgeTitle,
    snap,
    snapSource,
    onSeek,
    onSelect,
    onMarquee,
    onBlade,
    onMoveItem,
    onTrimItem,
    planTrim,
    onZoom,
    onScroll,
    onPaintStats,
    height,
    trimMode = 'ripple',
    selectedGap = null,
    onSelectGap,
    onClipContextMenu,
    onItemDoubleClick,
    seamControls,
    onPoolDrop,
  } = props;

  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const wrapperRef = useRef<HTMLDivElement | null>(null);
  const selectedHandlesRef = useRef<{ item: Item; track: Track; y: number; height: number } | null>(null);
  const [width, setWidth] = useState(800);
  const [drag, setDrag] = useState<Drag | null>(null);
  // d7 N2: the frame the dragged edge (or the scrub) snapped to, painted as a guide line.
  const [snapGuideFrame, setSnapGuideFrame] = useState<number | null>(null);
  const guideSourceRef = useRef<{ frame: number } | null>(null);
  const [snapGuideSource, setSnapGuideSource] = useState<SnapSource | null>(null);
  // A phone gets no marquee (cut on phone): an empty-area drag pans instead.
  const phone = useDeviceClass() === 'phone';
  const coarsePointer = useMediaQuery('(pointer: coarse)', false, { getInitialValueInEffect: true }) ?? false;
  const touchDragEnabled = useTouchDragEnabled();
  const pointersRef = useRef(new Map<number, { x: number; y: number }>());
  const gestureRef = useRef<TouchGesture | null>(null);
  const lastTapRef = useRef<TapRecord | null>(null);
  const lastPointerTypeRef = useRef<string>('mouse');
  const longPressRef = useRef<() => void>(() => {});
  const handleSize = coarsePointer ? HANDLE.coarse : HANDLE.fine;

  const t = useT('editor-timeline.canvas');
  const f = useFormat();
  const labels = useMemo(() => ({ offline: t('offline'), lengthUnchecked: t('lengthUnchecked'), yours: t('yours'), compound: t('compound'), jobProcessing: t('jobProcessing'), jobReview: t('jobReview'), jobFailed: t('jobFailed') }), [t]);
  const selectionSet = useMemo(() => new Set(selection), [selection]);
  // T-020: the words at the snap guide. Null when nothing is snapped, so snapping off paints neither guide nor label.
  const snapGuideLabel = useMemo(
    () => (snapGuideFrame === null || !snapGuideSource ? null : t(`snapLabel.${snapGuideSource.kind}`, { name: snapGuideSource.kind === 'marker' ? snapGuideSource.name : '', track: snapGuideSource.kind === 'clipEdge' ? snapGuideSource.trackName : '' })),
    [snapGuideFrame, snapGuideSource, t],
  );
  // T-019: what the drag in flight will do, asked of the engine's own clamp. Only changes when the drag does.
  const trimPreview = useMemo(() => {
    let request: TrimDragRequest | null = null;
    if (drag?.kind === 'trim') request = { itemId: drag.itemId, mode: trimMode, edge: drag.edge, deltaFrames: drag.currentFrame - drag.startFrame, separately: drag.alt };
    else if (drag?.kind === 'bodytrim' && drag.moved) {
      const found = locate(doc, drag.itemId);
      if (found) {
        const items = found.track.items;
        const index = items.findIndex((i) => i.id === drag.itemId);
        const body = bodyTrimRequest(drag.mode, Math.round((drag.x - drag.startX) / pixelsPerFrame), index > 0, index + 1 < items.length);
        request = { itemId: drag.itemId, mode: drag.mode, edge: body.edge, deltaFrames: body.deltaFrames, separately: drag.alt };
      }
    }
    if (!request) return null;
    // The plan the release dispatches (memoised on the request): the ghosts, the count and the limit are the result, not a copy of it.
    const plan = planTrim ? planTrim(request) : planTrimDrag(doc, request);
    const readout = describeTrim({ mode: request.mode, edge: request.edge, requested: request.deltaFrames, probe: plan.probe, rate: doc.settings.rate, shift: plan.shift, stoppedBySource: plan.stoppedBySource });
    return { ...readout, shiftClips: plan.shift.clips, mode: request.mode, left: drag?.kind === 'trim' ? drag.currentFrame : null };
  }, [drag, doc, trimMode, pixelsPerFrame, planTrim]);
  const baseCursor = tool === 'blade' ? 'var(--rb-cursor-razor)' : tool === 'hand' ? 'var(--rb-cursor-pan)' : 'var(--rb-cursor-default)';
  const readoutRef = useRef<HTMLDivElement | null>(null);
  const [hoverEdge, setHoverEdge] = useState<'in' | 'out' | null>(null);
  const [hoverEventKey, setHoverEventKey] = useState<string | null>(null);
  const events: PaintUi['events'] = useMemo(
    () => (eventsLane ? { items: eventsLane.items, labels: eventsLane.labels, activeKey: eventsLane.activeKey, hoverKey: hoverEventKey, snap: eventsLane.snap } : undefined),
    [eventsLane, hoverEventKey],
  );
  const ui: PaintUi = useMemo(
    () => ({ labels, playheadFrame, selection: selectionSet, inFrame, outFrame, snapGuideFrame, snapGuideLabel, trackFlags, parityOf, rationaleFrames, beatGrid, events, renderRegions, ghosts, clipArt, view, isLinked, busyClipIds, badgesOf, jobOf }),
    // `artVersion` is a dependency on purpose: the art object is stable, its contents change.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [playheadFrame, selectionSet, inFrame, outFrame, snapGuideFrame, snapGuideLabel, trackFlags, parityOf, rationaleFrames, beatGrid, events, renderRegions, ghosts, clipArt, view, isLinked, busyClipIds, badgesOf, jobOf, artVersion, labels],
  );

  // The canvas follows the app's colour scheme like every other surface of the editor.
  const scheme = useComputedColorScheme('dark', { getInitialValueInEffect: true });
  const theme = useMemo(() => withClipColors(scheme === 'light' ? LIGHT_THEME : DARK_THEME, readClipColors(scheme === 'light' ? 'light' : 'dark')), [scheme]);

  const canvasHeight = height ?? Math.max(160, totalTimelineHeight(doc, ui));
  const dpr = typeof window !== 'undefined' ? window.devicePixelRatio || 1 : 1;
  const viewport: Viewport = useMemo(
    () => ({ width, height: canvasHeight, scrollFrame, pixelsPerFrame, devicePixelRatio: dpr }),
    [width, canvasHeight, scrollFrame, pixelsPerFrame, dpr],
  );

  // Track the wrapper's width.
  useEffect(() => {
    const el = wrapperRef.current;
    if (!el) return;
    const update = () => setWidth(Math.max(1, Math.floor(el.getBoundingClientRect().width)));
    update();
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(update);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  // Paint on every relevant change.
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const backingW = Math.round(width * dpr);
    const backingH = Math.round(canvasHeight * dpr);
    if (canvas.width !== backingW) canvas.width = backingW;
    if (canvas.height !== backingH) canvas.height = backingH;
    const ctx = canvas.getContext('2d') as unknown as PaintContext | null;
    if (!ctx) return;
    const stats = paintTimeline(ctx, doc, viewport, ui, theme);
    onPaintStats?.(stats);
  }, [doc, viewport, ui, width, canvasHeight, dpr, onPaintStats, theme]);

  // A job with no progress figure shows a moving stripe: repaint the canvas (only) on a timer, never under reduced motion.
  const reducedMotion = useReducedMotion() ?? false;
  const animateJobs = jobsAnimating === true && !reducedMotion;
  useEffect(() => {
    if (!animateJobs) return;
    const id = window.setInterval(() => {
      const ctx = canvasRef.current?.getContext('2d') as unknown as PaintContext | null | undefined;
      if (ctx) paintTimeline(ctx, doc, viewport, { ...ui, nowMs: Date.now() }, theme);
    }, 100);
    return () => window.clearInterval(id);
  }, [animateJobs, doc, viewport, ui, theme]);

  const localPoint = (event: React.PointerEvent | PointerEvent): { x: number; y: number } => {
    const rect = canvasRef.current?.getBoundingClientRect();
    return { x: event.clientX - (rect?.left ?? 0), y: event.clientY - (rect?.top ?? 0) };
  };

  /** The Events lane tick under a canvas point (null with no lane, or between ticks). */
  const eventAt = (x: number, y: number): LaneEvent | null =>
    eventsLane ? hitTestEvent(eventsLane.items, viewport, RULER_HEIGHT + MARKER_LANE_HEIGHT, x, y, coarsePointer ? 12 : 6) : null;

  const trackIndexAt = (y: number): number => {
    const rows = trackRows(doc, ui);
    for (let i = 0; i < rows.length; i++) {
      if (y >= rows[i].y && y < rows[i].y + rows[i].height) return i;
    }
    return y < headerHeight(ui) ? 0 : Math.max(0, rows.length - 1);
  };

  /**
   * T-019: in Slip or Slide a plain mouse or pen drag on a clip's body is the trim, not a move. A selection
   * modifier, a multi-clip selection, a touch pointer, or a clip the mode cannot act on (slip needs a source
   * offset, slide needs a neighbour) keeps the ordinary move, so nothing is taken away that the mode cannot give back.
   */
  const bodyFallback = useCallback(
    (item: Item): BodyFallback | null => {
      if (trimMode !== 'slip' && trimMode !== 'slide') return null;
      const found = locate(doc, item.id);
      if (!found) return null;
      const probe = probeTrim(doc, item.id, trimMode === 'slip' ? 'in' : (found.track.items.at(-1)?.id === item.id ? 'in' : 'out'), trimMode, 0);
      return 'blocked' in probe ? probe.blocked : null;
    },
    [doc, trimMode],
  );
  const bodyTrimApplies = useCallback(
    (item: Item, event: React.PointerEvent): boolean => {
      if (props.overview || (trimMode !== 'slip' && trimMode !== 'slide')) return false;
      if (event.pointerType === 'touch' || event.shiftKey || event.ctrlKey || event.metaKey) return false;
      if (selectionSet.size > 1 && selectionSet.has(item.id)) return false;
      return locate(doc, item.id) !== null && bodyFallback(item) === null;
    },
    [doc, props.overview, trimMode, selectionSet, bodyFallback],
  );

  // The cursor over a clip names what a drag on its body will do; set on the element, never through state. A clip the mode
  // cannot act on keeps the plain cursor, since the drag will move it. Re-read when the mode changes under a still pointer.
  const hoverRef = useRef<{ x: number; y: number } | null>(null);
  const paintBodyCursor = useCallback(() => {
    const canvas = canvasRef.current;
    if (!canvas || props.overview || tool !== 'select') return;
    const at = hoverRef.current;
    const hit = at && (trimMode === 'slip' || trimMode === 'slide') ? hitTestItem(doc, viewport, ui, at.x, at.y) : null;
    canvas.style.cursor = hit && bodyFallback(hit.item) === null ? `var(--rb-cursor-${trimMode})` : baseCursor;
  }, [doc, viewport, ui, tool, trimMode, baseCursor, bodyFallback, props.overview]);
  useEffect(() => {
    paintBodyCursor();
  }, [trimMode, paintBodyCursor]);

  const onPointerDown = useCallback(
    (event: React.PointerEvent<HTMLCanvasElement>) => {
      // A right-click opens the clip menu (onContextMenu); it must not start a move, a marquee or a selection change here.
      if (event.button === 2) return;
      const { x, y } = localPoint(event);
      capturePointer(event.currentTarget as HTMLCanvasElement, event.pointerId);

      if (tool === 'hand' || event.button === 1) {
        setDrag({ kind: 'pan', startX: x, startScroll: scrollFrame });
        return;
      }

      const tick = eventAt(x, y);
      if (tick) {
        eventsLane?.onActivate(tick);
        return;
      }
      if (y < headerHeight(ui)) {
        setDrag({ kind: 'scrub', fromFrame: playheadFrame });
        onSeek(snap(xToFrame(x, viewport)));
        return;
      }

      // A click on the "Ready to review" chip selects the clip and opens the review.
      const jobHit = tool === 'select' && onClipJob ? hitTestJobChip(doc, viewport, ui, x, y) : null;
      if (jobHit && jobHit.job.state !== 'running') {
        if (!selectionSet.has(jobHit.item.id)) onSelect(jobHit.item.id, { shift: false, toggle: false });
        onClipJob?.({ itemId: jobHit.item.id, job: jobHit.job });
        return;
      }
      // A click on an adjustment badge selects the clip and opens that adjustment.
      const badgeHit = tool === 'select' && onClipBadge ? hitTestBadge(doc, viewport, ui, x, y) : null;
      if (badgeHit) {
        if (!selectionSet.has(badgeHit.item.id)) onSelect(badgeHit.item.id, { shift: false, toggle: false });
        onClipBadge?.({ itemId: badgeHit.item.id, kind: badgeHit.rect.kind });
        return;
      }
      const hit = hitTestItem(doc, viewport, ui, x, y);
      if (tool === 'blade') {
        if (hit) onBlade(hit.item.id, snap(xToFrame(x, viewport), [hit.item.id]));
        return;
      }

      if (hit) {
        const flags = trackFlags(hit.track);
        if (!selectionSet.has(hit.item.id) || event.shiftKey || event.ctrlKey || event.metaKey) {
          onSelect(hit.item.id, { shift: event.shiftKey, toggle: event.ctrlKey || event.metaKey });
        }
        if (flags.locked) return;
        if (bodyTrimApplies(hit.item, event)) {
          setDrag({ kind: 'bodytrim', itemId: hit.item.id, mode: trimMode as 'slip' | 'slide', startX: x, x, moved: false, alt: event.altKey });
          return;
        }
        const frameAt = xToFrame(x, viewport);
        setDrag({
          kind: 'move',
          itemId: hit.item.id,
          trackId: hit.track.id,
          grabOffsetFrames: frameAt - hit.item.recordIn,
          durationFrames: hit.item.recordOut - hit.item.recordIn,
          startX: x,
          startY: y,
          x,
          y,
          moved: false,
          fallback: props.overview || event.pointerType === 'touch' || event.shiftKey || event.ctrlKey || event.metaKey || (selectionSet.size > 1 && selectionSet.has(hit.item.id)) ? null : bodyFallback(hit.item),
        });
        return;
      }

      onSelect(null, { shift: false, toggle: false });
      // R03.1: empty space BETWEEN clips is a gap you can select (Delete then closes it).
      const gapTrack = trackAtY(doc, ui, y);
      onSelectGap?.(gapTrack ? gapAt(doc, gapTrack.id, Math.floor(xToFrame(x, viewport))) : null);
      if (phone) {
        setDrag({ kind: 'pan', startX: x, startScroll: scrollFrame });
        return;
      }
      setDrag({ kind: 'marquee', startX: x, startY: y, x, y });
    },
    [doc, viewport, ui, tool, scrollFrame, playheadFrame, phone, snap, onSeek, onSelect, onSelectGap, onBlade, trackFlags, selectionSet, eventsLane, coarsePointer, trimMode, bodyTrimApplies, bodyFallback, props.overview],
  );

  const [hoverTitle, setHoverTitle] = useState<string | null>(null);
  const updateBadgeHover = (event: React.PointerEvent<HTMLCanvasElement>) => {
    if (eventsLane) {
      const { x, y } = localPoint(event);
      const tick = eventAt(x, y);
      setHoverEventKey((prev) => (prev === (tick?.key ?? null) ? prev : tick?.key ?? null));
      if (tick) {
        const text = eventsLane.describe(tick);
        setHoverTitle((prev) => (prev === text ? prev : text));
        return;
      }
    }
    if (!(badgeTitle && badgesOf) && !jobOf) return;
    const { x, y } = localPoint(event);
    const below = y >= headerHeight(ui);
    const found = below && badgeTitle ? hitTestBadge(doc, viewport, ui, x, y) : null;
    const jobHit = below && !found ? hitTestJobChip(doc, viewport, ui, x, y) : null;
    const next = found && badgeTitle ? badgeTitle(found.item.id, found.rect.kind) : jobHit ? (jobHit.job.message ? `${jobHit.job.label}: ${jobHit.job.message}` : jobHit.job.label || null) : null;
    setHoverTitle((prev) => (prev === next ? prev : next));
  };

  const onPointerMove = useCallback(
    (event: React.PointerEvent<HTMLCanvasElement>) => {
      if (!drag) {
        // The cursor over a clip names the body gesture the mode gives it; set on the element, never through state.
        const at = localPoint(event);
        hoverRef.current = event.pointerType === 'touch' ? null : at;
        paintBodyCursor();
        return;
      }
      const { x, y } = localPoint(event);
      const guide = (raw: number, snapped: number, exclude?: readonly string[]) => {
        if (snapped === raw) {
          guideSourceRef.current = null;
          setSnapGuideFrame(null);
          return;
        }
        // The label's source is looked up only when the guide lands on a new frame, never per pointer move.
        if (guideSourceRef.current?.frame !== snapped) {
          guideSourceRef.current = { frame: snapped };
          setSnapGuideSource(snapSource ? snapSource(snapped, exclude) : null);
        }
        setSnapGuideFrame(snapped);
      };
      switch (drag.kind) {
        case 'scrub': {
          const raw = xToFrame(x, viewport);
          const snapped = snap(raw);
          guide(raw, snapped);
          onSeek(snapped);
          break;
        }
        case 'marquee':
          setDrag({ ...drag, x, y });
          break;
        case 'move': {
          // ui1 D12: the pointer is kept so the drag ghost follows it.
          setDrag({ ...drag, x, y, moved: drag.moved || Math.abs(x - drag.startX) + Math.abs(y - drag.startY) > 3 });
          const raw = Math.max(0, xToFrame(x, viewport) - drag.grabOffsetFrames);
          guide(raw, Math.max(0, snap(raw, [drag.itemId])), [drag.itemId]);
          break;
        }
        case 'trim': {
          const raw = xToFrame(x, viewport);
          const snapped = snap(raw, [drag.itemId]);
          guide(raw, snapped, [drag.itemId]);
          if (snapped !== drag.currentFrame || event.altKey !== drag.alt) setDrag({ ...drag, currentFrame: snapped, alt: event.altKey });
          break;
        }
        case 'bodytrim':
          setDrag({ ...drag, x, alt: event.altKey, moved: drag.moved || Math.abs(x - drag.startX) > 3 });
          break;
        case 'pan':
          onScroll(Math.max(0, drag.startScroll - (x - drag.startX) / pixelsPerFrame));
          break;
        default:
          break;
      }
    },
    [drag, viewport, snap, snapSource, onSeek, onScroll, pixelsPerFrame, paintBodyCursor],
  );

  const onPointerUp = useCallback(
    (event: React.PointerEvent<HTMLCanvasElement>) => {
      if (!drag) return;
      const { x, y } = localPoint(event);
      if (drag.kind === 'marquee') {
        const frameLo = Math.min(xToFrame(drag.startX, viewport), xToFrame(x, viewport));
        const frameHi = Math.max(xToFrame(drag.startX, viewport), xToFrame(x, viewport));
        if (Math.abs(x - drag.startX) > 3 || Math.abs(y - drag.startY) > 3) {
          onMarquee({
            trackLo: Math.min(trackIndexAt(drag.startY), trackIndexAt(y)),
            trackHi: Math.max(trackIndexAt(drag.startY), trackIndexAt(y)),
            frameLo,
            frameHi: Math.max(frameLo + 1, frameHi),
          });
        }
      } else if (drag.kind === 'move' && drag.moved) {
        const target = trackAtY(doc, ui, y) ?? doc.tracks.find((t) => t.id === drag.trackId) ?? null;
        if (target) {
          const toRecordIn = Math.max(0, snap(xToFrame(x, viewport) - drag.grabOffsetFrames, [drag.itemId]));
          if (event.altKey) onMoveItem(drag.itemId, target.id, toRecordIn, true);
          else onMoveItem(drag.itemId, target.id, toRecordIn);
        }
      } else if (drag.kind === 'bodytrim') {
        const found = drag.moved ? locate(doc, drag.itemId) : null;
        if (found) {
          const items = found.track.items;
          const index = items.findIndex((i) => i.id === drag.itemId);
          const body = bodyTrimRequest(drag.mode, Math.round((x - drag.startX) / pixelsPerFrame), index > 0, index + 1 < items.length);
          if (body.deltaFrames !== 0) {
            if (event.altKey) onTrimItem(drag.itemId, body.edge, body.deltaFrames, true);
            else onTrimItem(drag.itemId, body.edge, body.deltaFrames);
          }
        }
      } else if (drag.kind === 'trim') {
        const delta = snap(xToFrame(x, viewport), [drag.itemId]) - drag.startFrame;
        if (delta !== 0) {
          if (event.altKey) onTrimItem(drag.itemId, drag.edge, delta, true);
          else onTrimItem(drag.itemId, drag.edge, delta);
        }
      }
      releaseCapture(canvasRef.current, event.pointerId);
      setDrag(null);
      setSnapGuideFrame(null);
    },
    [drag, doc, ui, viewport, snap, onMarquee, onMoveItem, onTrimItem, trackIndexAt, pixelsPerFrame],
  );

  /**
   * The browser took the pointer (a system gesture, a palm, a scroll it decided to own). Nothing is
   * half-applied: move, trim and marquee only ever commit on pointerup, so dropping the drag is
   * enough; a scrub puts the playhead back and a pan puts the scroll back where they started. The
   * snap guide the last move painted is dropped too, or it would stay over the timeline.
   */
  const onPointerCancel = useCallback(
    (event?: React.PointerEvent<HTMLCanvasElement>) => {
      if (event) releaseCapture(canvasRef.current, event.pointerId);
      if (!drag) return;
      if (drag.kind === 'scrub') onSeek(drag.fromFrame);
      else if (drag.kind === 'pan') onScroll(drag.startScroll);
      setDrag(null);
      setSnapGuideFrame(null);
    },
    [drag, onSeek, onScroll],
  );

  // Escape cancels the drag in flight: one press, no write, and it does not reach the editor's Deselect.
  const cancelDragRef = useRef(onPointerCancel);
  useEffect(() => {
    cancelDragRef.current = onPointerCancel;
  }, [onPointerCancel]);
  const dragging = drag !== null;
  useEffect(() => {
    if (!dragging) return undefined;
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      event.preventDefault();
      event.stopImmediatePropagation();
      cancelDragRef.current();
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [dragging]);

  // ---- Touch (R03.8 / RM.3) ---------------------------------------------------------------
  // Mouse and pen keep the handlers above. A finger goes through these: tap selects, one finger
  // pans, two fingers pinch-zoom, a 500 ms hold opens the clip menu (or picks the clip up when the
  // per-device flag is on). Move, trim and marquee commit only on pointerup, so a cancel never
  // leaves a half-applied edit and no transaction stays open.
  const clearPendingTimer = (g: TouchGesture | null) => {
    if (g && g.kind === 'pending' && g.timer !== null) {
      clearTimeout(g.timer);
      g.timer = null;
    }
  };

  /** Puts back whatever the gesture had already changed; used by cancel, lost capture and a second finger. */
  const abortGesture = () => {
    const g = gestureRef.current;
    clearPendingTimer(g);
    if (g?.kind === 'pending' && g.mode === 'pan') onScroll(g.startScroll);
    if (g?.kind === 'pinch') {
      onZoom(g.startPpf, g.start.anchorFrame);
      onScroll(g.startScroll);
    }
    if (g?.kind === 'drag') onPointerCancel();
  };

  const touchDown = (event: React.PointerEvent<HTMLCanvasElement>) => {
    const { x, y } = localPoint(event);
    const id = event.pointerId;
    if (inEdgeDeadZone(event.clientX, window.innerWidth)) return;
    const pointers = pointersRef.current;
    if (pointers.size >= 2) return;
    pointers.set(id, { x, y });
    capturePointer(event.currentTarget, id);
    const g = gestureRef.current;
    if (g?.kind === 'settle') return;
    if (g?.kind === 'drag' && g.sub === 'trim') return;
    if (g && pointers.size === 2) {
      // A second finger: whatever the first was doing is put back, then the pair pinches.
      const first = [...pointers.keys()].find((k) => k !== id);
      abortGesture();
      if (first === undefined) return;
      const a = pointers.get(first) as { x: number; y: number };
      const b = { x, y };
      const mid = midpointOf(a, b);
      gestureRef.current = {
        kind: 'pinch',
        ids: [first, id],
        start: { distance: distanceBetween(a, b), pixelsPerFrame, anchorFrame: xToFrame(mid.x, viewport) },
        startScroll: scrollFrame,
        startPpf: pixelsPerFrame,
      };
      setSnapGuideFrame(null);
      return;
    }
    const tick = eventAt(x, y);
    if (tick) {
      // A tap on a tick is the whole gesture on touch: the playhead goes there and the choices open (no long press needed).
      eventsLane?.onActivate(tick);
      eventsLane?.onMenu(tick, { clientX: event.clientX, clientY: event.clientY });
      return;
    }
    if (y < headerHeight(ui)) {
      gestureRef.current = { kind: 'drag', id, sub: 'ruler' };
      setDrag({ kind: 'scrub', fromFrame: playheadFrame });
      onSeek(snap(xToFrame(x, viewport)));
      return;
    }
    const found = tool === 'hand' ? null : hitTestItem(doc, viewport, ui, x, y);
    const hit = found
      ? { itemId: found.item.id, trackId: found.track.id, locked: trackFlags(found.track).locked, recordIn: found.item.recordIn, recordOut: found.item.recordOut }
      : null;
    const pending: Extract<TouchGesture, { kind: 'pending' }> = {
      kind: 'pending',
      id,
      downX: x,
      downY: y,
      clientX: event.clientX,
      clientY: event.clientY,
      x,
      y,
      downTime: Date.now(),
      startScroll: scrollFrame,
      mode: 'pending',
      hit,
      timer: null,
    };
    if (hit && tool === 'select') pending.timer = setTimeout(() => longPressRef.current(), LONG_PRESS_MS);
    gestureRef.current = pending;
  };

  const fireLongPress = () => {
    const g = gestureRef.current;
    if (!g || g.kind !== 'pending' || g.mode !== 'pending' || !g.hit) return;
    g.timer = null;
    const facts = { downX: g.downX, downY: g.downY, x: g.x, y: g.y, elapsedMs: LONG_PRESS_MS, onRuler: false, onClip: true, released: false };
    if (classifyOneFinger(facts) !== 'long-press') return;
    const hit = g.hit;
    if (!selectionSet.has(hit.itemId)) onSelect(hit.itemId, { shift: false, toggle: false });
    if (touchDragEnabled && !hit.locked) {
      gestureRef.current = { kind: 'drag', id: g.id, sub: 'pickup' };
      setDrag({ fallback: null,
        kind: 'move',
        itemId: hit.itemId,
        trackId: hit.trackId,
        grabOffsetFrames: xToFrame(g.x, viewport) - hit.recordIn,
        durationFrames: hit.recordOut - hit.recordIn,
        startX: g.x,
        startY: g.y,
        x: g.x,
        y: g.y,
        moved: true,
      });
      return;
    }
    g.mode = 'menu';
    onClipContextMenu?.({ itemId: hit.itemId, clientX: g.clientX, clientY: g.clientY });
  };
  useEffect(() => {
    longPressRef.current = fireLongPress;
  });
  useEffect(
    () => () => {
      const g = gestureRef.current;
      if (g && g.kind === 'pending' && g.timer !== null) clearTimeout(g.timer);
    },
    [],
  );

  const touchMove = (event: React.PointerEvent<HTMLCanvasElement>) => {
    const pointers = pointersRef.current;
    if (!pointers.has(event.pointerId)) return;
    const { x, y } = localPoint(event);
    pointers.set(event.pointerId, { x, y });
    const g = gestureRef.current;
    if (!g || g.kind === 'settle') return;
    if (g.kind === 'pinch') {
      const a = pointers.get(g.ids[0]);
      const b = pointers.get(g.ids[1]);
      if (!a || !b) return;
      const next = pinchViewport(g.start, distanceBetween(a, b), midpointOf(a, b).x);
      onZoom(next.pixelsPerFrame, g.start.anchorFrame);
      onScroll(next.scrollFrame);
      return;
    }
    if (g.kind === 'drag') {
      if (g.id === event.pointerId) onPointerMove(event);
      return;
    }
    if (g.id !== event.pointerId) return;
    g.x = x;
    g.y = y;
    if (g.mode === 'menu') return;
    const kind = classifyOneFinger({ downX: g.downX, downY: g.downY, x, y, elapsedMs: Date.now() - g.downTime, onRuler: false, onClip: false, released: false });
    if (g.mode === 'pending' && kind === 'pan') {
      clearPendingTimer(g);
      g.mode = 'pan';
    }
    if (g.mode === 'pan') onScroll(Math.max(0, g.startScroll - (x - g.downX) / pixelsPerFrame));
  };

  const finishTap = (g: Extract<TouchGesture, { kind: 'pending' }>) => {
    const hit = g.hit;
    const jobTap = hit && tool === 'select' && onClipJob ? hitTestJobChip(doc, viewport, ui, g.x, g.y) : null;
    if (jobTap && jobTap.job.state !== 'running') {
      if (!selectionSet.has(jobTap.item.id)) onSelect(jobTap.item.id, { shift: false, toggle: false });
      onClipJob?.({ itemId: jobTap.item.id, job: jobTap.job });
      return;
    }
    const badgeHit = hit && tool === 'select' && onClipBadge ? hitTestBadge(doc, viewport, ui, g.x, g.y) : null;
    if (badgeHit) {
      if (!selectionSet.has(badgeHit.item.id)) onSelect(badgeHit.item.id, { shift: false, toggle: false });
      onClipBadge?.({ itemId: badgeHit.item.id, kind: badgeHit.rect.kind });
      return;
    }
    if (tool === 'blade') {
      if (hit) onBlade(hit.itemId, snap(xToFrame(g.x, viewport), [hit.itemId]));
      return;
    }
    const tap: TapRecord = { time: Date.now(), x: g.x, y: g.y, itemId: hit ? hit.itemId : null };
    if (hit) {
      if (!selectionSet.has(hit.itemId)) onSelect(hit.itemId, { shift: false, toggle: false });
      if (isDoubleTap(lastTapRef.current, tap)) {
        lastTapRef.current = null;
        const fit = zoomToRange(hit.recordIn, hit.recordOut, width);
        onZoom(fit.pixelsPerFrame, hit.recordIn);
        onScroll(fit.scrollFrame);
        return;
      }
      lastTapRef.current = tap;
      return;
    }
    lastTapRef.current = null;
    onSelect(null, { shift: false, toggle: false });
    const gapTrack = trackAtY(doc, ui, g.y);
    onSelectGap?.(gapTrack ? gapAt(doc, gapTrack.id, Math.floor(xToFrame(g.x, viewport))) : null);
  };

  const touchEnd = (event: React.PointerEvent<HTMLCanvasElement>, cancelled: boolean) => {
    const pointers = pointersRef.current;
    if (!pointers.has(event.pointerId)) return;
    pointers.delete(event.pointerId);
    releaseCapture(canvasRef.current, event.pointerId);
    const g = gestureRef.current;
    const done = () => {
      if (pointers.size === 0) gestureRef.current = null;
    };
    if (!g || g.kind === 'settle') return done();
    if (g.kind === 'pinch') {
      if (cancelled) abortGesture();
      gestureRef.current = pointers.size === 0 ? null : { kind: 'settle' };
      return;
    }
    if (g.id !== event.pointerId) return;
    if (g.kind === 'drag') {
      if (cancelled) onPointerCancel();
      else onPointerUp(event);
      gestureRef.current = null;
      return done();
    }
    clearPendingTimer(g);
    if (cancelled) abortGesture();
    else if (g.mode === 'pending') finishTap(g);
    gestureRef.current = null;
    done();
  };

  // The browser or the page took the pointer without an up or a cancel (element removed, capture stolen).
  const onLostPointerCapture = (event: React.PointerEvent<HTMLCanvasElement>) => {
    if (event.pointerType === 'touch' && pointersRef.current.has(event.pointerId)) touchEnd(event, true);
  };

  const onContextMenu = useCallback(
    (event: React.MouseEvent<HTMLCanvasElement>) => {
      if (!onClipContextMenu && !eventsLane) return;
      event.preventDefault();
      // A finger's long press has its own 500 ms timer; the browser's contextmenu is not used for touch.
      if (lastPointerTypeRef.current === 'touch') return;
      const rect = canvasRef.current?.getBoundingClientRect();
      const x = event.clientX - (rect?.left ?? 0);
      const y = event.clientY - (rect?.top ?? 0);
      const tick = eventAt(x, y);
      if (tick && eventsLane) {
        eventsLane.onActivate(tick);
        eventsLane.onMenu(tick, { clientX: event.clientX, clientY: event.clientY });
        return;
      }
      if (!onClipContextMenu) return;
      const hit = y < headerHeight(ui) ? null : hitTestItem(doc, viewport, ui, x, y);
      if (hit && !selectionSet.has(hit.item.id)) onSelect(hit.item.id, { shift: false, toggle: false });
      const inRuler = y < RULER_HEIGHT + MARKER_LANE_HEIGHT;
      const frame = Math.max(0, Math.floor(xToFrame(x, viewport)));
      // Empty lane space: a gap under the pointer becomes the selected gap, so the menu can close it.
      if (!hit && !inRuler) {
        const gapTrack = trackAtY(doc, ui, y);
        onSelectGap?.(gapTrack ? gapAt(doc, gapTrack.id, frame) : null);
      }
      const badgeHit = hit ? hitTestBadge(doc, viewport, ui, x, y) : null;
      onClipContextMenu({ itemId: hit ? hit.item.id : null, clientX: event.clientX, clientY: event.clientY, ...(badgeHit ? { badge: badgeHit.rect.kind } : {}), area: hit ? 'clip' : inRuler ? 'ruler' : 'lane', frame });
    },
    [doc, viewport, ui, selectionSet, onSelect, onSelectGap, onClipContextMenu, eventsLane, coarsePointer],
  );
  const onDoubleClick = useCallback(
    (event: React.MouseEvent<HTMLCanvasElement>) => {
      if (!onItemDoubleClick) return;
      // A double tap on a clip zooms to it (touchEnd); the synthesized dblclick must not also open the inspector.
      if (lastPointerTypeRef.current === 'touch') return;
      const rect = canvasRef.current?.getBoundingClientRect();
      const x = event.clientX - (rect?.left ?? 0);
      const y = event.clientY - (rect?.top ?? 0);
      if (y < headerHeight(ui)) return;
      const hit = hitTestItem(doc, viewport, ui, x, y);
      if (hit) onItemDoubleClick(hit.item.id);
    },
    [doc, viewport, ui, onItemDoubleClick],
  );

  const onWheel = useCallback(
    (event: WheelEvent) => {
      if (event.ctrlKey || event.metaKey) {
        event.preventDefault();
        const { x } = localPoint(event as unknown as PointerEvent);
        const anchorFrame = xToFrame(x, viewport);
        const factor = event.deltaY < 0 ? 1.25 : 0.8;
        onZoom(Math.min(64, Math.max(0.01, pixelsPerFrame * factor)), anchorFrame);
        return;
      }
      if (Math.abs(event.deltaX) > Math.abs(event.deltaY) || event.shiftKey) {
        event.preventDefault();
        const delta = (event.shiftKey ? event.deltaY : event.deltaX) / pixelsPerFrame;
        onScroll(Math.max(0, scrollFrame + delta));
      }
    },
    [viewport, pixelsPerFrame, scrollFrame, onZoom, onScroll],
  );
  // ui1 D9: React attaches wheel listeners as passive, so preventDefault there is ignored (and logged)
  // and Ctrl+wheel / pinch zoomed the page. The handler is registered natively, non-passive, once;
  // a ref carries the latest closure.
  const onWheelRef = useRef(onWheel);
  useEffect(() => {
    onWheelRef.current = onWheel;
  });
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const listener = (event: WheelEvent) => onWheelRef.current(event);
    canvas.addEventListener('wheel', listener, { passive: false });
    return () => canvas.removeEventListener('wheel', listener);
  }, []);

  // ui1 D12: where a moved clip will land (frame and lane), drawn while the drag is on.
  let dragGhost: { left: number; top: number; width: number; height: number } | null = null;
  let moveBlocked = false;
  if (drag?.kind === 'move' && drag.moved) {
    const rows = trackRows(doc, ui);
    const row = rows.find((r) => drag.y >= r.y && drag.y < r.y + r.height) ?? rows.find((r) => r.track.id === drag.trackId);
    if (row) {
      const toRecordIn = Math.max(0, snap(xToFrame(drag.x, viewport) - drag.grabOffsetFrames, [drag.itemId]));
      dragGhost = { left: frameToX(toRecordIn, viewport), top: row.y + 2, width: drag.durationFrames * pixelsPerFrame, height: row.height - 4 };
      moveBlocked = probeMove(doc, drag.itemId, row.track.id, toRecordIn) === 'overlap';
    }
  }
  // T-020: the clips a ripple trim will shift, drawn lightly where they will land (visible ones only, capped).
  // The readout stays inside the canvas whatever its words measure: placed after layout, on the element, every paint of a drag.
  useLayoutEffect(() => {
    const el = readoutRef.current;
    if (!el) return;
    const anchor = Number(el.dataset.anchor ?? 4);
    el.style.left = `${Math.max(4, Math.min(width - el.offsetWidth - 4, anchor))}px`;
  });

  const rippleGhosts: { id: string; left: number; top: number; width: number; height: number }[] = [];
  if (trimPreview && trimPreview.shiftClips.length > 0) {
    const rowOf = new Map(trackRows(doc, ui).map((r) => [r.track.id, r]));
    for (const clip of trimPreview.shiftClips) {
      const row = rowOf.get(clip.trackId);
      if (!row) continue;
      const left = frameToX(clip.to, viewport);
      const width = clip.length * pixelsPerFrame;
      if (left + width < 0 || left > viewport.width) continue;
      rippleGhosts.push({ id: clip.itemId, left, top: row.y + 2, width, height: row.height - 4 });
      if (rippleGhosts.length >= 300) break;
    }
  }

  // DOM handles for the ONE selected item (not for every item). A plain computation, not a memo:
  // it is a linear scan over the selected item's track and runs once per paint.
  let selectedHandles: { item: Item; track: Track; y: number; height: number } | null = null;
  if (!props.overview && (selection.length === 1 || (selection.length > 1 && selectionIsOneLinkGroup(doc, selection)))) {
    const id = selection[0];
    for (const row of trackRows(doc, ui)) {
      const item = row.track.items.find((i: Item) => i.id === id);
      if (item) {
        selectedHandles = { item, track: row.track as Track, y: row.y, height: row.height };
        break;
      }
    }
  }

  /** The handle's branded cursor names the mode; Roll keeps the two-sided trim cursors. */
  const handleCursor = (side: 'left' | 'right'): string =>
    trimMode === 'ripple' ? 'var(--rb-cursor-ripple)' : trimMode === 'slip' ? 'var(--rb-cursor-slip)' : trimMode === 'slide' ? 'var(--rb-cursor-slide)' : side === 'left' ? 'var(--rb-cursor-trim-left)' : 'var(--rb-cursor-trim-right)';

  const beginTrim = useCallback((edge: 'in' | 'out', event: React.PointerEvent<HTMLDivElement>) => {
    event.stopPropagation();
    event.preventDefault();
    const canvas = canvasRef.current;
    const handles = selectedHandlesRef.current;
    if (!canvas || !handles) return;
    // Another finger is already on the timeline: a trim needs the whole gesture to itself.
    if (event.pointerType === 'touch' && pointersRef.current.size > 0) return;
    capturePointer(canvas, event.pointerId);
    if (event.pointerType === 'touch') {
      const rect = canvas.getBoundingClientRect();
      pointersRef.current.set(event.pointerId, { x: event.clientX - rect.left, y: event.clientY - rect.top });
      gestureRef.current = { kind: 'drag', id: event.pointerId, sub: 'trim' };
    }
    setDrag({
      kind: 'trim',
      itemId: handles.item.id,
      edge,
      startFrame: edge === 'in' ? handles.item.recordIn : handles.item.recordOut,
      currentFrame: edge === 'in' ? handles.item.recordIn : handles.item.recordOut,
      alt: event.altKey,
    });
  }, []);
  const onTrimInDown = useCallback((event: React.PointerEvent<HTMLDivElement>) => beginTrim('in', event), [beginTrim]);
  const onTrimOutDown = useCallback((event: React.PointerEvent<HTMLDivElement>) => beginTrim('out', event), [beginTrim]);

  // R05.4: a pool tile dragged over the timeline. The kind of the source rides in the drag's type list (the
  // data itself is unreadable until the drop), so a lane of the wrong kind answers "no" while dragging.
  const [poolDrop, setPoolDrop] = useState<{ trackId: string; frame: number; allowed: boolean } | null>(null);
  const poolDragKind = (event: React.DragEvent): 'video' | 'audio' | 'image' | null => {
    const types = Array.from(event.dataTransfer?.types ?? []);
    if (!types.includes(CLIP_DRAG_MIME)) return null;
    const kind = types.find((t) => t.startsWith(CLIP_KIND_MIME_PREFIX))?.slice(CLIP_KIND_MIME_PREFIX.length);
    return kind === 'audio' || kind === 'image' || kind === 'video' ? kind : 'video';
  };
  const poolTargetAtPoint = (clientX: number, clientY: number, kind: 'video' | 'audio' | 'image') => {
    const rect = canvasRef.current?.getBoundingClientRect();
    const x = clientX - (rect?.left ?? 0);
    const y = clientY - (rect?.top ?? 0);
    if (rect && (x < 0 || y < 0 || x > rect.width || y > rect.height)) return null;
    const rows = trackRows(doc, ui);
    const row = rows.find((r) => y >= r.y && y < r.y + r.height);
    if (!row) return null;
    const frame = Math.max(0, Math.round(snap(xToFrame(x, viewport))));
    const allowed = row.track.kind === (kind === 'audio' ? 'audio' : 'video');
    return { trackId: row.track.id, frame, allowed };
  };
  const poolTargetAt = (event: React.DragEvent, kind: 'video' | 'audio' | 'image') =>
    poolTargetAtPoint(event.clientX, event.clientY, kind);

  // R05.4: a tile lifted with a long press on a touch screen. The same target maths and the same
  // `onPoolDrop` as a mouse drop, so the place rules are shared.
  const touchHandlerRef = useRef<(detail: PoolTouchDragDetail) => void>(() => {});
  useEffect(() => {
    touchHandlerRef.current = (detail) => {
      if (!onPoolDrop) return;
      if (detail.phase === 'cancel') {
        setPoolDrop(null);
        return;
      }
      const target = poolTargetAtPoint(detail.clientX, detail.clientY, detail.kind);
      if (detail.phase === 'drop') {
        setPoolDrop(null);
        if (target?.allowed) onPoolDrop(detail.mediaRef, target.trackId, target.frame);
        return;
      }
      setPoolDrop(target);
    };
  });
  useEffect(() => subscribePoolTouchDrag((detail) => touchHandlerRef.current(detail)), []);
  const onPoolDragOver = (event: React.DragEvent<HTMLDivElement>) => {
    if (!onPoolDrop) return;
    const kind = poolDragKind(event);
    if (!kind) return;
    const target = poolTargetAt(event, kind);
    setPoolDrop(target);
    if (target?.allowed) {
      event.preventDefault();
      event.dataTransfer.dropEffect = 'copy';
    } else if (event.dataTransfer) {
      event.dataTransfer.dropEffect = 'none';
    }
  };
  const onPoolDropHere = (event: React.DragEvent<HTMLDivElement>) => {
    if (!onPoolDrop) return;
    const kind = poolDragKind(event);
    setPoolDrop(null);
    if (!kind) return;
    const payload = readDragPayload(event.dataTransfer);
    const target = poolTargetAt(event, kind);
    if (!payload || payload.kind !== 'clip' || !target?.allowed) return;
    event.preventDefault();
    onPoolDrop(payload.mediaRef, target.trackId, target.frame);
  };

  useEffect(() => {
    selectedHandlesRef.current = selectedHandles;
  });

  return (
    <Box
      ref={wrapperRef}
      data-testid="timeline-canvas-wrapper"
      data-touch-action="none"
      style={{ position: 'relative', width: '100%', overflow: 'hidden', userSelect: 'none', ...TOUCH_SURFACE_STYLE }}
      onDragOver={onPoolDragOver}
      onDragLeave={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setPoolDrop(null);
      }}
      onDrop={onPoolDropHere}
    >
      <canvas
        ref={canvasRef}
        data-testid={props.overview ? 'timeline-overview-canvas' : 'timeline-canvas'}
        data-touch-action="none"
        role="img"
        aria-label={t('ariaLabel', { count: doc.tracks.length })}
        title={hoverTitle ?? undefined}
        style={{ width, height: canvasHeight, display: 'block', cursor: baseCursor, ...TOUCH_SURFACE_STYLE }}
        onPointerDown={(event) => {
          lastPointerTypeRef.current = event.pointerType;
          if (event.pointerType === 'touch') touchDown(event);
          else onPointerDown(event);
        }}
        onPointerMove={(event) => {
          if (event.pointerType === 'touch') touchMove(event);
          else {
            onPointerMove(event);
            updateBadgeHover(event);
          }
        }}
        onPointerUp={(event) => (event.pointerType === 'touch' ? touchEnd(event, false) : onPointerUp(event))}
        onPointerCancel={(event) => (event.pointerType === 'touch' ? touchEnd(event, true) : onPointerCancel(event))}
        onLostPointerCapture={onLostPointerCapture}
        onContextMenu={onContextMenu}
        onDoubleClick={onDoubleClick}
      />
      {poolDrop && (() => {
        const row = trackRows(doc, ui).find((r) => r.track.id === poolDrop.trackId);
        if (!row) return null;
        return (
          <>
          <Box
            data-testid="pool-drop-lane"
            data-allowed={poolDrop.allowed ? 'true' : 'false'}
            aria-hidden
            style={{
              position: 'absolute',
              pointerEvents: 'none',
              left: 0,
              right: 0,
              top: row.y,
              height: row.height,
              background: poolDrop.allowed ? 'rgba(77,171,247,0.16)' : 'rgba(250,82,82,0.14)',
              outline: `2px dashed ${poolDrop.allowed ? 'var(--mantine-color-blue-4)' : 'var(--mantine-color-red-6)'}`,
              outlineOffset: -2,
            }}
          />
          <Box
            data-testid="pool-drop-indicator"
            data-track={poolDrop.trackId}
            data-frame={poolDrop.frame}
            data-allowed={poolDrop.allowed ? 'true' : 'false'}
            aria-hidden
            style={{
              position: 'absolute',
              pointerEvents: 'none',
              left: frameToX(poolDrop.frame, viewport),
              top: row.y,
              height: row.height,
              width: 2,
              background: poolDrop.allowed ? 'var(--mantine-color-blue-4)' : 'var(--mantine-color-red-6)',
              boxShadow: poolDrop.allowed ? '0 0 0 1px rgba(77,171,247,0.5)' : 'none',
            }}
          />
          </>
        );
      })()}
      {selectedGap && (() => {
        const row = trackRows(doc, ui).find((r) => r.track.id === selectedGap.trackId);
        if (!row) return null;
        const left = frameToX(selectedGap.start, viewport);
        return (
          <Box
            data-testid="selected-gap"
            data-gap-track={selectedGap.trackId}
            data-gap-start={selectedGap.start}
            data-gap-end={selectedGap.end}
            role="img"
            aria-label={t('gapLabel', { count: selectedGap.end - selectedGap.start })}
            style={{
              position: 'absolute',
              pointerEvents: 'none',
              left,
              top: row.y + 2,
              width: Math.max(2, (selectedGap.end - selectedGap.start) * pixelsPerFrame),
              height: row.height - 4,
              border: '1px dashed var(--mantine-color-yellow-5)',
              background: 'repeating-linear-gradient(135deg, rgba(250,176,5,0.28) 0 6px, rgba(250,176,5,0.08) 6px 12px)',
              borderRadius: 3,
            }}
          />
        );
      })()}
      {trimPreview && (
        <Box
          ref={readoutRef}
          data-anchor={trimPreview.left !== null ? frameToX(trimPreview.left, viewport) + 8 : (drag?.kind === 'bodytrim' ? drag.x + 12 : 4)}
          data-testid="trim-readout"
          data-mode={trimPreview.mode}
          data-limit={trimPreview.limit ?? undefined}
          role="status"
          style={{
            position: 'absolute',
            pointerEvents: 'none',
            top: headerHeight(ui) + 2,
            padding: '2px 8px',
            borderRadius: 4,
            fontSize: 12,
            background: 'var(--mantine-color-dark-8)',
            color: 'var(--mantine-color-gray-0)',
            border: '1px solid var(--mantine-color-yellow-5)',
            width: 'max-content',
            maxWidth: Math.max(120, width - 8),
          }}
        >
          {trimPreview.key === 'none'
            ? t('trimReadout.none', { mode: t(`trimModes.${trimPreview.mode}`) })
            : t(`trimReadout.${trimPreview.key}`, { ...trimPreview.values })}
          {trimPreview.limit && <span data-testid="trim-limit"> {t(`trimReadout.limit.${trimPreview.limit}`)}</span>}
          {trimPreview.shift && <span data-testid="trim-shift"> {t('trimReadout.shift', { ...trimPreview.shift })}</span>}
        </Box>
      )}
      {drag?.kind === 'move' && drag.moved && drag.fallback && (
        <Box
          ref={readoutRef}
          data-anchor={drag.x + 12}
          data-testid="trim-fallback-hint"
          data-reason={drag.fallback}
          role="status"
          style={{ position: 'absolute', pointerEvents: 'none', top: RULER_HEIGHT + MARKER_LANE_HEIGHT + 2, padding: '2px 8px', borderRadius: 4, fontSize: 12, background: 'var(--mantine-color-dark-8)', color: 'var(--mantine-color-gray-0)', border: '1px solid var(--mantine-color-yellow-5)', width: 'max-content', maxWidth: Math.max(120, width - 8) }}
        >
          {t(`trimReadout.fallback.${drag.fallback}`)}
        </Box>
      )}
      {rippleGhosts.map((g) => (
        <Box
          key={g.id}
          data-testid="ripple-ghost"
          aria-hidden
          style={{ position: 'absolute', pointerEvents: 'none', left: g.left, top: g.top, width: g.width, height: g.height, border: '1px dashed var(--rb-clip-selected)', background: 'var(--rb-clip-video)', opacity: 0.35, borderRadius: 3 }}
        />
      ))}
      {dragGhost && (
        <Box
          data-testid="timeline-drag-ghost"
          aria-hidden
          style={{
            position: 'absolute',
            pointerEvents: 'none',
            left: dragGhost.left,
            top: dragGhost.top,
            width: dragGhost.width,
            height: dragGhost.height,
            border: '1px dashed var(--mantine-color-blue-4)',
            background: 'rgba(77, 171, 247, 0.18)',
            borderRadius: 3,
          }}
        />
      )}
      {dragGhost && moveBlocked && (
        <Box
          data-testid="move-overlap"
          role="status"
          style={{ position: 'absolute', pointerEvents: 'none', left: Math.max(4, Math.min(width - 260, dragGhost.left)), top: Math.max(RULER_HEIGHT, dragGhost.top - 24), padding: '2px 8px', borderRadius: 4, fontSize: 12, background: 'var(--mantine-color-dark-8)', color: 'var(--mantine-color-gray-0)', border: '1px solid var(--mantine-color-red-5)', whiteSpace: 'nowrap' }}
        >
          {t('moveOverlap')}
        </Box>
      )}
      {drag?.kind === 'marquee' && (
        <Box
          data-testid="timeline-marquee"
          style={{
            position: 'absolute',
            pointerEvents: 'none',
            left: Math.min(drag.startX, drag.x),
            top: Math.min(drag.startY, drag.y),
            width: Math.abs(drag.x - drag.startX),
            height: Math.abs(drag.y - drag.startY),
            border: '1px dashed var(--mantine-color-yellow-5)',
            background: 'rgba(255, 212, 59, 0.08)',
          }}
        />
      )}
      {selectedHandles && (
        <>
          {hoverEdge && !drag && (
            <Box
              data-testid="trim-mode-mark"
              data-mode={trimMode}
              aria-hidden
              style={{
                position: 'absolute',
                pointerEvents: 'none',
                left: frameToX(hoverEdge === 'in' ? selectedHandles.item.recordIn : selectedHandles.item.recordOut, viewport) + (hoverEdge === 'in' ? -handleSize.hit / 2 - 4 : handleSize.hit / 2 + 4),
                transform: hoverEdge === 'in' ? 'translateX(-100%)' : undefined,
                top: selectedHandles.y,
                padding: '0 4px',
                borderRadius: 3,
                fontSize: 12,
                lineHeight: '16px',
                background: 'var(--mantine-color-dark-8)',
                color: 'var(--mantine-color-yellow-5)',
                border: '1px solid var(--mantine-color-yellow-5)',
                whiteSpace: 'nowrap',
              }}
            >
              {t(`trimModes.${trimMode}`)}
            </Box>
          )}
          <Box
            data-testid="trim-handle-in"
            data-touch-action="none"
            role="slider"
            aria-label={t('trimIn')}
            aria-valuenow={selectedHandles.item.recordIn}
            onPointerDown={onTrimInDown}
            onPointerEnter={() => setHoverEdge('in')}
            onPointerLeave={() => setHoverEdge(null)}
            style={{
              position: 'absolute',
              left: frameToX(selectedHandles.item.recordIn, viewport) - handleSize.hit / 2,
              top: selectedHandles.y,
              width: handleSize.hit,
              height: selectedHandles.height,
              display: 'flex',
              justifyContent: 'center',
              alignItems: 'center',
              cursor: handleCursor('left'),
              ...TOUCH_SURFACE_STYLE,
            }}
          >
            <Box style={{ width: handleSize.paint, height: Math.max(8, selectedHandles.height - 8), background: 'var(--mantine-color-yellow-5)', borderRadius: 2, pointerEvents: 'none' }} />
          </Box>
          <Box
            data-testid="trim-handle-out"
            data-touch-action="none"
            role="slider"
            aria-label={t('trimOut')}
            aria-valuenow={selectedHandles.item.recordOut}
            onPointerDown={onTrimOutDown}
            onPointerEnter={() => setHoverEdge('out')}
            onPointerLeave={() => setHoverEdge(null)}
            style={{
              position: 'absolute',
              left: frameToX(selectedHandles.item.recordOut, viewport) - handleSize.hit / 2,
              top: selectedHandles.y,
              width: handleSize.hit,
              height: selectedHandles.height,
              display: 'flex',
              justifyContent: 'center',
              alignItems: 'center',
              cursor: handleCursor('right'),
              ...TOUCH_SURFACE_STYLE,
            }}
          >
            <Box style={{ width: handleSize.paint, height: Math.max(8, selectedHandles.height - 8), background: 'var(--mantine-color-yellow-5)', borderRadius: 2, pointerEvents: 'none' }} />
          </Box>
        </>
      )}
      {eventsLane && (
        <EventsLaneKeys
          eventsLane={eventsLane}
          viewport={viewport}
          top={RULER_HEIGHT + MARKER_LANE_HEIGHT}
          playheadFrame={playheadFrame}
          canvasRef={canvasRef}
        />
      )}
      {seamControls && !phone && (
        <SeamControls
          doc={doc}
          viewport={viewport}
          rows={trackRows(doc, ui)}
          selectedId={selection.length === 1 ? selection[0] : null}
          rate={seamControls.rate}
          coarse={coarsePointer}
          readOnly={seamControls.readOnly}
          newId={seamControls.newId}
          onApply={seamControls.onApply}
        />
      )}
    </Box>
  );
}

/** True when every selected clip shares one link id (a linked picture and its sound): the group trims through its first clip. */
function selectionIsOneLinkGroup(doc: EditTimeline, selection: readonly string[]): boolean {
  const first = locate(doc, selection[0]);
  const linkId = first ? linkIdOf(first.item) : null;
  return linkId !== null && selection.every((id) => {
    const found = locate(doc, id);
    return found !== null && linkIdOf(found.item) === linkId;
  });
}

/** Gives the pointer back; a pointer that is already gone has nothing to release. */
function releaseCapture(element: Element | null, pointerId: number) {
  try {
    if (element?.hasPointerCapture?.(pointerId)) element.releasePointerCapture(pointerId);
  } catch {
    // Already released.
  }
}

/** Pointer capture keeps a drag's moves on the canvas; a pointer that is already gone cannot be captured. */
function capturePointer(element: Element, pointerId: number) {
  try {
    element.setPointerCapture(pointerId);
  } catch {
    // InvalidStateError: the pointer ended before capture; the drag simply ends on its own.
  }
}
