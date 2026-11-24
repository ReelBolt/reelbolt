'use client';

import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Button, Group, Loader, Tooltip } from '@mantine/core';
import { useReducedMotion } from '@mantine/hooks';
import { IconFocusCentered, IconZoomIn, IconZoomOut } from '@tabler/icons-react';
import { domScratch, domTextMeasurer } from '@/lib/editor/canvas-dom';
import { useT } from '@/i18n';
import {
  canvasToLayerLocal,
  hitTest,
  layerBounds,
  layerLocalToCanvas,
  placeLayers,
  type Bitmap,
  type BitmapMap,
  type PlacedLayer,
  type Point,
} from '@/lib/editor/compose';
import {
  REVEAL_WAIT_LIMIT_MS,
  WORKING_BLUR_IN_MS,
  layersReady,
  resolveTarget,
  sampleEffect,
  type AiEffect,
} from '@/lib/editor/ai-effect';
import type { LayerMaskBuffer } from '@/lib/editor/layer-mask';
import { BRUSH_MAX, brushSizeInLayerPixels, type MaskPainter } from '@/lib/editor/mask';
import type { EditorDocState, EditorTransform } from '@/lib/editor/model';
import {
  hitHandle,
  isNudgeKey,
  moveByDelta,
  nudge,
  rotationFromDrag,
  sameTransform,
  scaleFromCornerDrag,
  handlePositions,
  type CornerHandle,
} from '@/lib/editor/transform-tools';
import {
  applyPinch,
  centred,
  classifyWheel,
  fitInsetsFor,
  fitView,
  formatZoom,
  nextZoomStep,
  panBy,
  pinchState,
  screenToDoc,
  wheelPan,
  wheelZoomFactor,
  zoomAt,
  ROTATE_HANDLE_OFFSET_PX,
  ZOOM_BAR_INSET_PX,
  type Size2,
  type View,
} from '@/lib/editor/viewport';
import { paintWorkspace, type Surface } from '@/lib/editor/workspace';
import type { SubjectBoxPrompt, SubjectPointPrompt } from '@/lib/types/image-document';
import { IntentGate, needsTouchIntent } from './touch-intent';
import { useCoarsePointer } from './useCoarsePointer';
import { EditorIcon } from '../ui';

export type ToolId = 'select' | 'hand' | 'text' | 'brush' | 'subject' | 'layermask';

export interface SubjectPrompts {
  points: SubjectPointPrompt[];
  boxes: SubjectBoxPrompt[];
}

export interface BrushState {
  /** Brush diameter in CANVAS pixels (what the user sees); converted to layer pixels per stroke. */
  size: number;
  erase: boolean;
}

export interface EditorCanvasProps {
  doc: EditorDocState;
  bitmaps: BitmapMap;
  /** Bumped when a web font finishes loading, so text is re-measured and redrawn. */
  fontVersion: number;
  selectedId: string | null;
  /** Layers a background job is working on: the selection frame of one of them shows a working mark. */
  busyIds?: ReadonlySet<string>;
  tool: ToolId;
  onSelect(id: string | null): void;
  onTransform(layerId: string, transform: EditorTransform, label: string, mergeKey?: string): void;
  onPlaceText(point: Point): void;
  painter: MaskPainter | null;
  maskLayerId: string | null;
  /** Bumped on every brush stroke so the tint is redrawn. */
  maskTick: number;
  brush: BrushState;
  onMaskChange(): void;
  /**
   * Mask editing (tool `layermask`): the buffer strokes are painted into, in the layer's pixels. The
   * brush size and `erase` (= Hide) come from `brush`. Alt flips Hide/Show for a stroke.
   */
  layerMask?: { layerId: string; buffer: LayerMaskBuffer; hardness: number } | null;
  /**
   * The picture layer a subject is cut from (the selected picture layer, else the original). Clicks and
   * boxes with the subject tool are recorded in THIS layer's pixels.
   */
  subjectLayerId: string | null;
  prompts: SubjectPrompts;
  onAddPoint(point: SubjectPointPrompt): void;
  onAddBox(box: SubjectBoxPrompt): void;
  /**
   * True when a plain click with the subject tool marks something to LEAVE OUT (the panel's "Leave out"
   * choice). Shift-click always does, on a keyboard; this is the way to do it by touch.
   */
  subjectLeaveOut?: boolean;
  /** The AI tool's in-place effect, or null. */
  effect?: AiEffect | null;
  /** Called once a reveal has finished, so the editor can drop the effect. */
  onEffectDone?(): void;
  /**
   * Layers whose image could not be loaded (deleted from storage). Each is drawn as a crossed-out
   * placeholder where it sits, never in an export.
   */
  missing?: ReadonlySet<string>;
}

type Drag =
  | { kind: 'move'; layerId: string; start: EditorTransform; origin: Point; latest: EditorTransform }
  | { kind: 'scale'; layerId: string; handle: CornerHandle; base: PlacedLayer; latest: EditorTransform }
  | { kind: 'rotate'; layerId: string; base: PlacedLayer; latest: EditorTransform }
  | { kind: 'paint'; layer: PlacedLayer; last: Point }
  | { kind: 'subject'; start: Point }
  | { kind: 'pan'; start: Point; view: View };

const HANDLE_PX = 10;
const ROTATE_OFFSET_PX = ROTATE_HANDLE_OFFSET_PX;
/** How close a finger must land to a handle, in CSS pixels: a 44 px target. */
const TOUCH_HANDLE_SLOP_PX = 22;
const NO_MISSING: ReadonlySet<string> = new Set();
const CLICK_SLOP_PX = 4;
const IDENTITY: View = { zoom: 1, x: 0, y: 0 };

const isTypingTarget = (target: EventTarget | null) => {
  const el = target as HTMLElement | null;
  if (!el || !el.tagName) return false;
  return el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT' || el.isContentEditable;
};

function makeSurface(): Surface | null {
  if (typeof document === 'undefined') return null;
  const made = domScratch(1, 1);
  return made ? { canvas: made.canvas as Surface['canvas'], ctx: made.ctx } : null;
}

/** A small checkerboard, in device pixels, for the transparent part of the frame. */
function makeChecker(ctx: CanvasRenderingContext2D, dpr: number): CanvasPattern | null {
  const cell = Math.max(4, Math.round(8 * dpr));
  const tile = domScratch(cell * 2, cell * 2);
  if (!tile) return null;
  tile.ctx.fillStyle = '#2b2e37';
  tile.ctx.fillRect(0, 0, cell * 2, cell * 2);
  tile.ctx.fillStyle = '#3a3e49';
  tile.ctx.fillRect(0, 0, cell, cell);
  tile.ctx.fillRect(cell, cell, cell, cell);
  return typeof ctx.createPattern === 'function' ? ctx.createPattern(tile.canvas as CanvasImageSource, 'repeat') : null;
}

/**
 * The editor's workspace: an infinite, pannable and zoomable surface with the picture's frame in it.
 * Layers that reach past the frame are drawn beside it, dimmed, rather than cut off — only the export
 * crops to the frame. On top of the compositor's output sit the select / text / brush / subject
 * tools' pointer handling, an SVG overlay for handles, prompts and the brush cursor, and the AI
 * tools' in-place blur.
 *
 * Moving around: hold Space and drag, drag with the middle mouse button, use the hand tool, or drag
 * with two fingers; zoom with the mouse wheel, Ctrl/Cmd + wheel, a trackpad or touch pinch, the
 * zoom buttons, or + / − / 0 / 1 while the canvas has focus.
 *
 * Coordinates: pointer events are converted to DOCUMENT pixels through the view, and every tool
 * works in that space. A drag in progress is a local `draft` transform laid over the selected layer
 * until the pointer is released, at which point ONE `onTransform` reports the result (one undo step
 * per drag, not one per pixel).
 */
export function EditorCanvas(props: EditorCanvasProps) {
  const {
    doc, bitmaps, fontVersion, selectedId, tool, onSelect, onTransform, onPlaceText, painter, maskLayerId,
    maskTick, brush, onMaskChange, layerMask = null, subjectLayerId, prompts, onAddPoint, onAddBox, subjectLeaveOut = false, effect = null, onEffectDone,
    missing = NO_MISSING, busyIds = NO_MISSING,
  } = props;
  const t = useT('editor-thumbnail-a.canvas');
  const coarse = useCoarsePointer();
  const insets = useMemo(() => fitInsetsFor(coarse), [coarse]);

  const canvasRef = useRef<HTMLCanvasElement>(null);
  const wrapperRef = useRef<HTMLDivElement>(null);
  const drag = useRef<Drag | null>(null);
  const touches = useRef(new Map<number, Point>());
  const pinch = useRef<{ mid: Point; distance: number } | null>(null);
  const intent = useRef(new IntentGate());
  const surfaces = useRef<{ layers: Surface | null; blur: Surface | null } | null>(null);
  const checker = useRef<{ dpr: number; pattern: CanvasPattern | null } | null>(null);
  const userMoved = useRef(false);
  const revealStart = useRef<{ effect: AiEffect; at: number } | null>(null);
  const paintRef = useRef<() => boolean>(() => false);
  const effectDoneRef = useRef(onEffectDone);

  const [draft, setDraft] = useState<{ layerId: string; transform: EditorTransform } | null>(null);
  const [view, setView] = useState<View>(IDENTITY);
  const [size, setSize] = useState<Size2 | null>(null);
  const [fittedFor, setFittedFor] = useState<string | null>(null);
  const [cursor, setCursor] = useState<Point | null>(null);
  const [rubber, setRubber] = useState<{ a: Point; b: Point } | null>(null);
  const [spaceDown, setSpaceDown] = useState(false);
  const [panning, setPanning] = useState(false);
  const reducedMotion = useReducedMotion() ?? false;

  const frame = useMemo(() => ({ width: doc.canvasWidth, height: doc.canvasHeight }), [doc.canvasWidth, doc.canvasHeight]);

  // Fit the frame to the workspace the first time its size is known, and again whenever the picture
  // changes size (a new shape, an undo). Set during render, keyed by what it was fitted for, so there
  // is no effect writing state after the fact.
  const fitKey = size ? `${frame.width}x${frame.height}:${coarse ? 'touch' : 'mouse'}` : null;
  if (size && fitKey !== fittedFor) {
    setFittedFor(fitKey);
    setView(fitView(frame, size, insets));
  }

  const layers = useMemo(
    () =>
      draft
        ? doc.layers.map((l) => (l.id === draft.layerId ? { ...l, transform: draft.transform } : l))
        : doc.layers,
    [doc.layers, draft],
  );

  const placed = useMemo(() => {
    // Text is measured with whatever face the browser has right now; `fontVersion` changes when a
    // web font finishes loading, so the same layers are measured again with the real one.
    void fontVersion;
    return placeLayers(layers, bitmaps, domTextMeasurer());
  }, [layers, bitmaps, fontVersion]);

  const selected = placed.find((l) => l.id === selectedId) ?? null;
  const maskLayer = placed.find((l) => l.id === (tool === 'layermask' ? layerMask?.layerId : maskLayerId)) ?? null;
  const flipHide = useRef(false);
  const subjectLayer = placed.find((l) => l.id === subjectLayerId) ?? null;
  const zoom = view.zoom;
  const cssSize: Size2 = size ?? { width: frame.width, height: frame.height };

  // ── painting ────────────────────────────────────────────────────────────────────────────────
  // One function paints the whole workspace for "now". It is replaced on every render (so it always
  // sees the latest props) and called after every render, and on every animation frame while an AI
  // effect is showing. It answers true when a reveal has just finished.
  // `maskTick` changes on every brush stroke; the painter mutates in place, so the new prop value is
  // what re-renders (and so repaints) the tint.
  void maskTick;
  useEffect(() => {
    effectDoneRef.current = onEffectDone;
    paintRef.current = () => {
      const canvas = canvasRef.current;
      const ctx = canvas?.getContext('2d');
      if (!canvas || !ctx) return false;
      const dpr = typeof window !== 'undefined' ? Math.min(3, window.devicePixelRatio || 1) : 1;
      const W = Math.max(1, Math.round(cssSize.width * dpr));
      const H = Math.max(1, Math.round(cssSize.height * dpr));
      if (canvas.width !== W) canvas.width = W;
      if (canvas.height !== H) canvas.height = H;
      if (!surfaces.current) surfaces.current = { layers: makeSurface(), blur: makeSurface() };
      if (!checker.current || checker.current.dpr !== dpr) checker.current = { dpr, pattern: makeChecker(ctx, dpr) };

      const now = typeof performance !== 'undefined' ? performance.now() : Date.now();
      let effectInput: { region: { x: number; y: number; w: number; h: number }; look: ReturnType<typeof sampleEffect> } | null = null;
      let finished = false;
      if (effect) {
        let sample = effect;
        if (effect.phase === 'revealing') {
          // Hold the working look until the new pixels can be drawn, then reveal from that moment.
          const ready =
            !effect.waitFor ||
            layersReady(effect.waitFor, placed, (id) => bitmaps.has(id)) ||
            now - effect.startedAt > REVEAL_WAIT_LIMIT_MS;
          if (!ready) {
            sample = { phase: 'working', target: effect.target, startedAt: effect.startedAt - WORKING_BLUR_IN_MS };
          } else {
            if (revealStart.current?.effect !== effect) revealStart.current = { effect, at: now };
            sample = { ...effect, startedAt: revealStart.current.at };
          }
        }
        const look = sampleEffect(sample, now, reducedMotion);
        const region = resolveTarget(sample.target, placed, frame);
        if (region && !look.done) effectInput = { region, look };
        finished = sample.phase === 'revealing' && look.done;
      }

      paintWorkspace(
        ctx,
        {
          view: size ? view : IDENTITY,
          dpr,
          size: cssSize,
          frame,
          backgroundColor: doc.backgroundColor,
          layers: placed,
          bitmaps,
          checker: checker.current.pattern,
          mask: painter && maskLayer && tool === 'brush' ? { layer: maskLayer, tint: painter.tint as unknown as Bitmap, alpha: 0.55 } : null,
          effect: effectInput,
        },
        { layers: surfaces.current.layers, blur: surfaces.current.blur, scratch: domScratch },
      );
      return finished;
    };
    paintRef.current();
  });

  // While an AI effect is on screen, repaint every frame until a reveal finishes.
  useEffect(() => {
    if (!effect || typeof requestAnimationFrame === 'undefined') return;
    let frameId = 0;
    let stopped = false;
    const tick = () => {
      if (stopped) return;
      if (paintRef.current()) {
        effectDoneRef.current?.();
        return;
      }
      frameId = requestAnimationFrame(tick);
    };
    frameId = requestAnimationFrame(tick);
    return () => {
      stopped = true;
      cancelAnimationFrame(frameId);
    };
  }, [effect]);

  // ── the workspace's size ────────────────────────────────────────────────────────────────────
  const frameRef = useRef(frame);
  const insetsRef = useRef(insets);
  useEffect(() => {
    frameRef.current = frame;
    insetsRef.current = insets;
  }, [frame, insets]);

  useEffect(() => {
    const el = wrapperRef.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    let last: Size2 | null = null;
    const observer = new ResizeObserver(() => {
      const rect = el.getBoundingClientRect();
      if (rect.width < 1 || rect.height < 1) return;
      if (last && Math.abs(last.width - rect.width) < 0.5 && Math.abs(last.height - rect.height) < 0.5) return;
      const next = { width: rect.width, height: rect.height };
      // Until the user moves the view, a resize keeps the whole picture in sight.
      if (last && !userMoved.current) setView(fitView(frameRef.current, next, insetsRef.current));
      last = next;
      setSize(next);
    });
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  const viewRef = useRef(view);
  useEffect(() => {
    viewRef.current = view;
  }, [view]);

  const moveView = useCallback((next: View | ((v: View) => View)) => {
    userMoved.current = true;
    setView(next);
  }, []);

  const fit = useCallback(() => {
    userMoved.current = false;
    if (size) setView(fitView(frame, size, insets));
  }, [frame, insets, size]);

  const zoomTo = useCallback(
    (target: number) => {
      if (!size) return;
      moveView((v) => zoomAt(v, target / v.zoom, { x: size.width / 2, y: size.height / 2 }));
    },
    [moveView, size],
  );

  const actualSize = useCallback(() => {
    if (!size) return;
    moveView(centred(frame, size, 1));
  }, [frame, moveView, size]);

  // The wheel: zoom or pan (see `classifyWheel`). Registered natively because React's wheel listener
  // is passive and could not stop the page from scrolling.
  useEffect(() => {
    const el = wrapperRef.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const rect = el.getBoundingClientRect();
      const anchor = { x: e.clientX - rect.left, y: e.clientY - rect.top };
      userMoved.current = true;
      if (classifyWheel(e) === 'zoom') {
        const factor = wheelZoomFactor(e.deltaY, e.deltaMode);
        setView((v) => zoomAt(v, factor, anchor));
      } else {
        const d = wheelPan(e);
        setView((v) => panBy(v, d.dx, d.dy));
      }
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, []);

  // A tool still waiting for its intent never fires after the canvas is gone.
  useEffect(() => {
    const gate = intent.current;
    return () => gate.cancel();
  }, []);

  // Space held = the hand tool, the convention of every picture editor.
  useEffect(() => {
    const down = (e: KeyboardEvent) => {
      if (e.code !== 'Space' || e.repeat || isTypingTarget(e.target)) return;
      const target = e.target as Node | null;
      const inCanvas = !!target && !!wrapperRef.current?.contains(target);
      const onPage = target === document.body || target === document.documentElement;
      if (!inCanvas && !onPage) return; // a focused button keeps its own Space
      e.preventDefault();
      setSpaceDown(true);
    };
    const up = (e: KeyboardEvent) => {
      if (e.code === 'Space') setSpaceDown(false);
    };
    const reset = () => setSpaceDown(false);
    window.addEventListener('keydown', down);
    window.addEventListener('keyup', up);
    window.addEventListener('blur', reset);
    return () => {
      window.removeEventListener('keydown', down);
      window.removeEventListener('keyup', up);
      window.removeEventListener('blur', reset);
    };
  }, []);

  // ── pointers ────────────────────────────────────────────────────────────────────────────────
  const toScreen = (e: { clientX: number; clientY: number }): Point => {
    const rect = wrapperRef.current?.getBoundingClientRect();
    return rect ? { x: e.clientX - rect.left, y: e.clientY - rect.top } : { x: e.clientX, y: e.clientY };
  };
  const activeView = size ? view : IDENTITY;
  const toDoc = (e: { clientX: number; clientY: number }): Point => screenToDoc(activeView, toScreen(e));

  const brushStroke = (layer: PlacedLayer, from: Point, to: Point) => {
    if (tool === 'layermask') {
      if (!layerMask) return;
      layerMask.buffer.stroke(from, to, {
        size: brushSizeInLayerPixels(Math.min(BRUSH_MAX, brush.size), layer.transform.scale),
        hardness: layerMask.hardness,
        hide: brush.erase !== flipHide.current,
      });
      onMaskChange();
      return;
    }
    if (!painter) return;
    painter.stroke(from, to, {
      size: brushSizeInLayerPixels(Math.min(BRUSH_MAX, brush.size), layer.transform.scale),
      erase: brush.erase,
    });
    onMaskChange();
  };

  const cancelDrag = () => {
    drag.current = null;
    setDraft(null);
    setRubber(null);
    setPanning(false);
  };

  const onPointerDown = (e: React.PointerEvent) => {
    const screen = toScreen(e);
    if (e.pointerType === 'touch') {
      touches.current.set(e.pointerId, screen);
      if (touches.current.size === 2) {
        // A second finger turns whatever the first one started into a pinch (and a tool still waiting for its
        // intent never fires: a pinch must not paint, erase or add a text layer).
        intent.current.cancel();
        cancelDrag();
        const [a, b] = [...touches.current.values()];
        pinch.current = pinchState(a, b);
        return;
      }
      if (touches.current.size > 2) return;
    }
    wrapperRef.current?.focus({ preventScroll: true });
    (e.target as Element).setPointerCapture?.(e.pointerId);

    if (e.button === 1 || (e.button === 0 && (spaceDown || tool === 'hand'))) {
      e.preventDefault();
      drag.current = { kind: 'pan', start: screen, view: viewRef.current };
      setPanning(true);
      return;
    }
    if (e.button !== 0) return;
    const p = screenToDoc(activeView, screen);

    // A finger waits a moment before a one-finger tool fires (T7); a mouse or pen fires at once.
    const fire = (run: () => void) => {
      if (needsTouchIntent(tool, e.pointerType)) intent.current.begin(e.pointerId, run);
      else run();
    };
    if (tool === 'text') {
      fire(() => onPlaceText(p));
      return;
    }
    if (tool === 'layermask') {
      if (layerMask && maskLayer && !maskLayer.locked) {
        flipHide.current = e.altKey;
        fire(() => {
          const local = canvasToLayerLocal(maskLayer, p);
          layerMask.buffer.checkpoint();
          drag.current = { kind: 'paint', layer: maskLayer, last: local };
          brushStroke(maskLayer, local, local);
        });
      }
      return;
    }
    if (tool === 'brush') {
      if (painter && maskLayer) {
        fire(() => {
          const local = canvasToLayerLocal(maskLayer, p);
          drag.current = { kind: 'paint', layer: maskLayer, last: local };
          brushStroke(maskLayer, local, local);
        });
      }
      return;
    }
    if (tool === 'subject') {
      fire(() => {
        drag.current = { kind: 'subject', start: p };
        setRubber({ a: p, b: p });
      });
      return;
    }

    // select
    if (selected && !selected.locked) {
      const slop = e.pointerType === 'touch' ? TOUCH_HANDLE_SLOP_PX : HANDLE_PX * 1.6;
      const handle = hitHandle(selected, p, slop / zoom, ROTATE_OFFSET_PX / zoom);
      if (handle === 'rotate') {
        drag.current = { kind: 'rotate', layerId: selected.id, base: selected, latest: selected.transform };
        return;
      }
      if (handle) {
        drag.current = { kind: 'scale', layerId: selected.id, handle, base: selected, latest: selected.transform };
        return;
      }
    }
    const hit = hitTest(placed, p);
    onSelect(hit?.id ?? null);
    if (hit && !hit.locked) {
      drag.current = { kind: 'move', layerId: hit.id, start: hit.transform, origin: p, latest: hit.transform };
    }
  };

  const onPointerMove = (e: React.PointerEvent) => {
    const screen = toScreen(e);
    if (e.pointerType === 'touch' && touches.current.has(e.pointerId)) {
      touches.current.set(e.pointerId, screen);
      if (pinch.current && touches.current.size >= 2) {
        const [a, b] = [...touches.current.values()];
        const next = pinchState(a, b);
        const before = pinch.current;
        pinch.current = next;
        moveView((v) => applyPinch(v, before, next));
        return;
      }
    }
    const p = screenToDoc(activeView, screen);
    if (tool === 'brush' || tool === 'layermask') setCursor(p);
    const d = drag.current;
    if (!d) return;
    switch (d.kind) {
      case 'pan':
        moveView(panBy(d.view, screen.x - d.start.x, screen.y - d.start.y));
        break;
      case 'move': {
        const next = moveByDelta(d.start, { x: p.x - d.origin.x, y: p.y - d.origin.y }, e.shiftKey);
        d.latest = next;
        setDraft({ layerId: d.layerId, transform: next });
        break;
      }
      case 'scale': {
        const next = scaleFromCornerDrag(d.base, d.handle, p);
        d.latest = next;
        setDraft({ layerId: d.layerId, transform: next });
        break;
      }
      case 'rotate': {
        const next = { ...d.base.transform, rotation: rotationFromDrag(d.base, p, e.shiftKey) };
        d.latest = next;
        setDraft({ layerId: d.layerId, transform: next });
        break;
      }
      case 'paint': {
        const local = canvasToLayerLocal(d.layer, p);
        brushStroke(d.layer, d.last, local);
        d.last = local;
        break;
      }
      case 'subject':
        setRubber({ a: d.start, b: p });
        break;
    }
  };

  const finish = (e: React.PointerEvent) => {
    if (e.pointerType === 'touch') {
      touches.current.delete(e.pointerId);
      // A tap lifts before the intent wait is over: fire the tool now.
      if (e.type === 'pointercancel') intent.current.cancel();
      else intent.current.release(e.pointerId);
      if (pinch.current) {
        if (touches.current.size < 2) pinch.current = null;
        return; // the end of a pinch is never a click
      }
    }
    const d = drag.current;
    drag.current = null;
    if (!d) return;
    const p = toDoc(e);
    if (d.kind === 'pan') {
      setPanning(false);
    } else if (d.kind === 'move' || d.kind === 'scale' || d.kind === 'rotate') {
      const startTransform = d.kind === 'move' ? d.start : d.base.transform;
      if (!sameTransform(startTransform, d.latest)) {
        onTransform(d.layerId, d.latest, d.kind === 'move' ? t('history.move') : d.kind === 'scale' ? t('history.resize') : t('history.rotate'));
      }
      setDraft(null);
    } else if (d.kind === 'subject') {
      setRubber(null);
      if (!subjectLayer) return;
      const a = canvasToLayerLocal(subjectLayer, d.start);
      const b = canvasToLayerLocal(subjectLayer, p);
      const round = (n: number) => Math.round(n);
      const dragged = Math.hypot(p.x - d.start.x, p.y - d.start.y) * zoom;
      if (dragged < CLICK_SLOP_PX) {
        if (a.x < 0 || a.y < 0 || a.x > subjectLayer.size.w || a.y > subjectLayer.size.h) return;
        onAddPoint({ x: round(a.x), y: round(a.y), label: e.shiftKey || subjectLeaveOut ? 0 : 1 });
      } else {
        const clampX = (n: number) => Math.min(subjectLayer.size.w, Math.max(0, n));
        const clampY = (n: number) => Math.min(subjectLayer.size.h, Math.max(0, n));
        onAddBox({
          xMin: round(clampX(Math.min(a.x, b.x))),
          yMin: round(clampY(Math.min(a.y, b.y))),
          xMax: round(clampX(Math.max(a.x, b.x))),
          yMax: round(clampY(Math.max(a.y, b.y))),
        });
      }
    }
  };

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (!e.ctrlKey && !e.metaKey && !e.altKey) {
      if (e.key === '+' || e.key === '=') {
        e.preventDefault();
        zoomTo(nextZoomStep(zoom, 1));
        return;
      }
      if (e.key === '-' || e.key === '_') {
        e.preventDefault();
        zoomTo(nextZoomStep(zoom, -1));
        return;
      }
      if (e.key === '0') {
        e.preventDefault();
        fit();
        return;
      }
      if (e.key === '1') {
        e.preventDefault();
        actualSize();
        return;
      }
    }
    if (tool !== 'select' || !selected) return;
    if (e.key === 'Escape') {
      onSelect(null);
      return;
    }
    if (!isNudgeKey(e.key) || selected.locked) return;
    e.preventDefault();
    onTransform(selected.id, nudge(selected.transform, e.key, e.shiftKey), t('history.nudge'), `nudge:${selected.id}`);
  };

  const hs = HANDLE_PX / zoom;
  const stroke = 2 / zoom;
  const showHandles = tool === 'select' && selected && !selected.locked && selected.visible;
  const outline = tool === 'select' && selected ? layerBounds(selected).corners : null;
  const handles = showHandles && selected ? handlePositions(selected, ROTATE_OFFSET_PX / zoom) : null;
  // A layer whose image is gone still shows where it sits (when its size is known), crossed out.
  const placeholders = missing.size
    ? layers.flatMap((l) =>
        missing.has(l.id) && l.visible && l.width && l.height
          ? [{ id: l.id, corners: layerBounds({ transform: l.transform, size: { w: l.width, h: l.height } }).corners }]
          : [],
      )
    : [];
  const cursorStyle =
    panning ? 'var(--rb-cursor-grabbing)'
      : spaceDown || tool === 'hand' ? 'var(--rb-cursor-grab)'
        : tool === 'brush' || tool === 'layermask' ? 'var(--rb-cursor-mask-brush)'
          : tool === 'subject' ? 'var(--rb-cursor-crosshair)'
            : tool === 'text' ? 'var(--rb-cursor-text)'
              : 'var(--rb-cursor-default)';

  return (
    <div
      ref={wrapperRef}
      role="application"
      aria-label={t('label')}
      aria-describedby="thumbnail-canvas-help"
      tabIndex={0}
      onKeyDown={onKeyDown}
      style={{
        position: 'relative',
        width: '100%',
        height: '100%',
        overflow: 'hidden',
        outline: 'none',
        background: '#0b0d12',
        touchAction: 'none',
        userSelect: 'none',
      }}
    >
      <span id="thumbnail-canvas-help" style={{ position: 'absolute', width: 1, height: 1, overflow: 'hidden', clip: 'rect(0 0 0 0)' }}>
        {t('help')}
      </span>
      <canvas
        ref={canvasRef}
        aria-label={t('picture')}
        role="img"
        style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', display: 'block', cursor: cursorStyle }}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={finish}
        onPointerCancel={finish}
        onPointerLeave={() => setCursor(null)}
        onContextMenu={(e) => e.preventDefault()}
      />

      {outline && selected && busyIds.has(selected.id) && (
        <div
          role="status"
          data-testid="canvas-busy"
          style={{
            position: 'absolute',
            left: activeView.x + Math.min(...outline.map((c) => c.x)) * activeView.zoom,
            top: Math.max(0, activeView.y + Math.min(...outline.map((c) => c.y)) * activeView.zoom - 22),
            pointerEvents: 'none',
            display: 'flex',
            alignItems: 'center',
            gap: 4,
            padding: '1px 6px',
            borderRadius: 4,
            fontSize: 12,
            color: 'var(--mantine-color-white)',
            background: 'var(--mantine-color-orange-filled)',
          }}
        >
          <Loader size="xs" color="white" />
          <span>{t('working')}</span>
        </div>
      )}

      <svg
        aria-hidden="true"
        viewBox={`0 0 ${cssSize.width} ${cssSize.height}`}
        preserveAspectRatio="none"
        style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', pointerEvents: 'none', overflow: 'visible' }}
      >
        <g transform={`translate(${activeView.x} ${activeView.y}) scale(${activeView.zoom})`}>
          {placeholders.map(({ id, corners }) => (
            <g key={`missing-${id}`} data-testid={`missing-layer-${id}`}>
              <polygon
                points={corners.map((c) => `${c.x},${c.y}`).join(' ')}
                fill="rgba(134, 142, 150, 0.18)"
                stroke="#868e96"
                strokeWidth={stroke}
                strokeDasharray={`${6 / zoom} ${4 / zoom}`}
              />
              <line x1={corners[0].x} y1={corners[0].y} x2={corners[2].x} y2={corners[2].y} stroke="#868e96" strokeWidth={stroke} />
              <line x1={corners[1].x} y1={corners[1].y} x2={corners[3].x} y2={corners[3].y} stroke="#868e96" strokeWidth={stroke} />
            </g>
          ))}
          {outline && (
            <polygon
              points={outline.map((c) => `${c.x},${c.y}`).join(' ')}
              fill="none"
              stroke={selected && busyIds.has(selected.id) ? '#fd7e14' : selected?.locked ? '#f59f00' : '#4dabf7'}
              strokeWidth={stroke}
              strokeDasharray={selected?.locked ? `${6 / zoom} ${4 / zoom}` : undefined}
            />
          )}
          {handles && (
            <>
              <line
                x1={(handles.tl.x + handles.tr.x) / 2}
                y1={(handles.tl.y + handles.tr.y) / 2}
                x2={handles.rotate.x}
                y2={handles.rotate.y}
                stroke="#4dabf7"
                strokeWidth={stroke}
              />
              <circle cx={handles.rotate.x} cy={handles.rotate.y} r={hs * 0.7} fill="#101218" stroke="#4dabf7" strokeWidth={stroke} />
              {/* eslint-disable-next-line i18next/no-literal-string -- ids, not text */}
              {(['tl', 'tr', 'br', 'bl'] as const).map((id) => (
                <rect
                  key={id}
                  x={handles[id].x - hs / 2}
                  y={handles[id].y - hs / 2}
                  width={hs}
                  height={hs}
                  fill="#ffffff"
                  stroke="#4dabf7"
                  strokeWidth={stroke}
                />
              ))}
            </>
          )}

          {tool === 'subject' && subjectLayer && (
            <>
              {prompts.boxes.map((b, i) => {
                const tl = layerLocalToCanvas(subjectLayer, { x: b.xMin, y: b.yMin });
                const br = layerLocalToCanvas(subjectLayer, { x: b.xMax, y: b.yMax });
                return (
                  <rect
                    key={`box-${i}`}
                    x={Math.min(tl.x, br.x)}
                    y={Math.min(tl.y, br.y)}
                    width={Math.abs(br.x - tl.x)}
                    height={Math.abs(br.y - tl.y)}
                    fill="rgba(77,171,247,0.12)"
                    stroke="#4dabf7"
                    strokeWidth={stroke}
                  />
                );
              })}
              {prompts.points.map((pt, i) => {
                const c = layerLocalToCanvas(subjectLayer, pt);
                return (
                  <circle
                    key={`pt-${i}`}
                    cx={c.x}
                    cy={c.y}
                    r={hs * 0.6}
                    fill={pt.label === 1 ? '#40c057' : '#fa5252'}
                    stroke="#ffffff"
                    strokeWidth={stroke}
                  />
                );
              })}
            </>
          )}
          {rubber && (
            <rect
              x={Math.min(rubber.a.x, rubber.b.x)}
              y={Math.min(rubber.a.y, rubber.b.y)}
              width={Math.abs(rubber.b.x - rubber.a.x)}
              height={Math.abs(rubber.b.y - rubber.a.y)}
              fill="rgba(77,171,247,0.12)"
              stroke="#4dabf7"
              strokeWidth={stroke}
              strokeDasharray={`${6 / zoom} ${4 / zoom}`}
            />
          )}
          {(tool === 'brush' || tool === 'layermask') && cursor && maskLayer && (
            <circle
              cx={cursor.x}
              cy={cursor.y}
              r={Math.min(BRUSH_MAX, brush.size) / 2}
              fill="none"
              stroke={brush.erase ? '#fa5252' : '#ffffff'}
              strokeWidth={stroke}
            />
          )}
        </g>
      </svg>

      <Group
        gap={4}
        wrap="nowrap"
        style={{
          position: 'absolute',
          right: 8,
          bottom: ZOOM_BAR_INSET_PX,
          padding: 4,
          borderRadius: 8,
          background: 'rgba(16, 18, 24, 0.86)',
          border: '1px solid rgba(255,255,255,0.08)',
        }}
      >
        <Tooltip label={t('zoomOutTip')} openDelay={400}>
          <EditorIcon tooltip={false} icon={IconZoomOut} c="#d0d4dc" label={t('zoomOut')} disabled={!size} onClick={() => zoomTo(nextZoomStep(zoom, -1))} />
        </Tooltip>
        <Tooltip label={t('actualSizeTip')} openDelay={400}>
          <Button
            variant="subtle"
            color="gray"
            size="compact-xs" fz="xs"
            c="#d0d4dc"
            miw={52}
            h={coarse ? 44 : undefined}
            aria-label={t('zoomActual', { zoom: formatZoom(zoom) })}
            disabled={!size}
            onClick={actualSize}
          >
            {formatZoom(zoom)}
          </Button>
        </Tooltip>
        <Tooltip label={t('zoomInTip')} openDelay={400}>
          <EditorIcon tooltip={false} icon={IconZoomIn} c="#d0d4dc" label={t('zoomIn')} disabled={!size} onClick={() => zoomTo(nextZoomStep(zoom, 1))} />
        </Tooltip>
        <Tooltip label={t('fitTip')} openDelay={400}>
          <EditorIcon tooltip={false} icon={IconFocusCentered} c="#d0d4dc" label={t('fit')} disabled={!size} onClick={fit} />
        </Tooltip>
      </Group>
    </div>
  );
}
