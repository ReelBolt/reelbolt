/**
 * R10.3: the geometry behind the on-canvas mask / reframe / power-window handles. Pure functions, no DOM.
 *
 * Shapes are stored as canvas fractions (centre x/y, width/height, feather of the shorter canvas side);
 * a gesture works in CANVAS PIXELS on a {@link Frame} (rotation 0 only), reusing the transform layer's
 * {@link HandleSpec}s. Nothing here writes the document: a gesture drafts a {@link FrameDraftState} that is
 * merged into the preview document, and {@link maskOps} / {@link reframeOps} / {@link windowOps} turn the
 * final value into ONE batch on release.
 */

import { useSyncExternalStore } from 'react';
import type { AdjustmentClip, EditTimeline, GradeNode, GradeStack, Item, MediaClip, ClipMask, QuadCorners, ShapeMask } from '@/lib/editing/types';
import type { EditOp } from '@/lib/editing/engine';
import type { EditorStore, ViewerTool } from '@/lib/editing/store/editor-store';
import { evaluateKeyframed } from '@/lib/editing/preview';
import { CORNER_HANDLES, EDGE_HANDLES, clamp, type Frame, type HandleSpec, type Vec } from './geometry';

type Canvas = { width: number; height: number };

/** A shape box in canvas fractions: centre and size. */
export type ShapeBox = { x: number; y: number; w: number; h: number };

/** Rectangle and ellipse share the same eight handles (the ellipse's sit on its bounding box). */
export const BOX_HANDLES: readonly HandleSpec[] = [...CORNER_HANDLES, ...EDGE_HANDLES];

/** The smallest a shape may be, as a fraction of the canvas side. */
export const MIN_SHAPE_FRACTION = 0.02;
export const FEATHER_MAX = 0.5;
/** Keyboard nudge, in canvas fractions (plain / with Shift). */
export const NUDGE_FRACTION = 0.01;
export const NUDGE_FRACTION_BIG = 0.1;

const round4 = (v: number) => Math.round(v * 10000) / 10000;

export function boxToFrame(box: ShapeBox, canvas: Canvas): Frame {
  return { cx: box.x * canvas.width, cy: box.y * canvas.height, w: box.w * canvas.width, h: box.h * canvas.height, rotation: 0 };
}

export function frameToBox(frame: Frame, canvas: Canvas): ShapeBox {
  return { x: round4(frame.cx / canvas.width), y: round4(frame.cy / canvas.height), w: round4(frame.w / canvas.width), h: round4(frame.h / canvas.height) };
}

/** The shape stored in a mask / window, with the document's own defaults for what is absent. */
export function boxOf(shape: ShapeMask): ShapeBox {
  return { x: shape.x ?? 0.5, y: shape.y ?? 0.5, w: shape.w ?? 0.5, h: shape.h ?? 0.5 };
}

/** Moves a frame by a pixel delta, keeping it inside the canvas. */
export function moveFrameBy(start: Frame, dx: number, dy: number, canvas: Canvas): Frame {
  return {
    ...start,
    cx: clamp(start.cx + dx, start.w / 2, canvas.width - start.w / 2),
    cy: clamp(start.cy + dy, start.h / 2, canvas.height - start.h / 2),
  };
}

export type DrawOptions = {
  /** Shift: keep a square picture ratio (equal sides in canvas pixels). */
  keepAspect?: boolean;
  /** Alt: the press point is the centre (matches {@link resizeFrame}). */
  fromCentre?: boolean;
};

/**
 * T-012: the box a draw-drag makes, in canvas fractions. `start` is where the press landed and `pointer` where
 * it is now (canvas pixels). The result stays inside the canvas and is never smaller than
 * {@link MIN_SHAPE_FRACTION} on a side, so a bare click still yields a valid box (the caller decides a click
 * is not a drag and writes nothing).
 */
export function drawBoxFromDrag(start: Vec, pointer: Vec, canvas: Canvas, opts: DrawOptions = {}): ShapeBox {
  const W = canvas.width;
  const H = canvas.height;
  const sx = clamp(start.x, 0, W);
  const sy = clamp(start.y, 0, H);
  const px = clamp(pointer.x, 0, W);
  const py = clamp(pointer.y, 0, H);
  const sgnX = px >= sx ? 1 : -1;
  const sgnY = py >= sy ? 1 : -1;
  let ax = Math.abs(px - sx);
  let ay = Math.abs(py - sy);
  // The room the box has to grow in each direction before it leaves the canvas.
  const roomX = opts.fromCentre ? Math.min(sx, W - sx) : sgnX > 0 ? W - sx : sx;
  const roomY = opts.fromCentre ? Math.min(sy, H - sy) : sgnY > 0 ? H - sy : sy;
  if (opts.keepAspect) {
    const side = Math.min(Math.max(ax, ay), roomX, roomY);
    ax = side;
    ay = side;
  } else {
    ax = Math.min(ax, roomX);
    ay = Math.min(ay, roomY);
  }
  let w = opts.fromCentre ? ax * 2 : ax;
  let h = opts.fromCentre ? ay * 2 : ay;
  w = Math.max(w, MIN_SHAPE_FRACTION * W);
  h = Math.max(h, MIN_SHAPE_FRACTION * H);
  let x0 = opts.fromCentre ? sx - w / 2 : sgnX > 0 ? sx : sx - w;
  let y0 = opts.fromCentre ? sy - h / 2 : sgnY > 0 ? sy : sy - h;
  x0 = clamp(x0, 0, W - w);
  y0 = clamp(y0, 0, H - h);
  return { x: round4((x0 + w / 2) / W), y: round4((y0 + h / 2) / H), w: round4(w / W), h: round4(h / H) };
}

/** What the mask tool is looking at, from the document and the playhead. Pure, so the tool's states are testable. */
export type MaskToolState =
  | { kind: 'no-clip' }
  | { kind: 'off-screen' }
  | { kind: 'object'; clip: MediaClip }
  | { kind: 'draw'; clip: MediaClip }
  | { kind: 'edit'; clip: MediaClip; shape: ShapeMask };

export function maskToolState(doc: EditTimeline, selection: readonly string[], frame: number): MaskToolState {
  if (selection.length !== 1) return { kind: 'no-clip' };
  for (const track of doc.tracks) {
    if (track.kind !== 'video') continue;
    for (const item of track.items) {
      if (item.id !== selection[0] || item.type !== 'mediaClip') continue;
      const clip = item as MediaClip;
      if (frame < clip.recordIn || frame >= clip.recordOut) return { kind: 'off-screen' };
      if (clip.mask && clip.mask.kind === 'segmentation') return { kind: 'object', clip };
      const shape = shapeMaskOf(clip.mask);
      return shape ? { kind: 'edit', clip, shape } : { kind: 'draw', clip };
    }
  }
  return { kind: 'no-clip' };
}

/** A drawn box as a mask: a new mask takes `base` (kind chosen in the tool bar, default feather); a redraw keeps feather and invert. */
export function maskFromBox(box: ShapeBox, base: ShapeMask): ShapeMask {
  return { ...base, x: box.x, y: box.y, w: box.w, h: box.h };
}

export type ResizeOptions = {
  /** Shift on a corner: keep the start aspect. */
  keepAspect?: boolean;
  /** Alt: grow from the centre. */
  fromCentre?: boolean;
};

/**
 * Drags one handle of `start` to `pointer` (canvas pixels). The opposite side stays put (or the centre
 * with Alt); the result stays inside the canvas and never gets smaller than {@link MIN_SHAPE_FRACTION}.
 */
export function resizeFrame(start: Frame, handle: HandleSpec, pointer: Vec, canvas: Canvas, opts: ResizeOptions = {}): Frame {
  const W = canvas.width;
  const H = canvas.height;
  const minW = MIN_SHAPE_FRACTION * W;
  const minH = MIN_SHAPE_FRACTION * H;
  const px = clamp(pointer.x, 0, W);
  const py = clamp(pointer.y, 0, H);
  const lx = Math.sign(handle.local.x);
  const ly = Math.sign(handle.local.y);
  let x0 = start.cx - start.w / 2;
  let x1 = start.cx + start.w / 2;
  let y0 = start.cy - start.h / 2;
  let y1 = start.cy + start.h / 2;

  const axis = (l: number, lo: number, hi: number, p: number, centre: number, min: number, max: number): [number, number] => {
    if (l === 0) return [lo, hi];
    if (opts.fromCentre) {
      const half = clamp(Math.abs(p - centre), min / 2, Math.min(centre, max - centre));
      return [centre - half, centre + half];
    }
    return l > 0 ? [lo, Math.min(max, Math.max(p, lo + min))] : [Math.max(0, Math.min(p, hi - min)), hi];
  };
  [x0, x1] = axis(lx, x0, x1, px, start.cx, minW, W);
  [y0, y1] = axis(ly, y0, y1, py, start.cy, minH, H);

  if (opts.keepAspect && lx !== 0 && ly !== 0 && start.h > 0) {
    const ratio = start.w / start.h;
    let w = x1 - x0;
    let h = y1 - y0;
    if (w / h > ratio) w = h * ratio;
    else h = w / ratio;
    const centred = !!opts.fromCentre;
    const maxW = centred ? 2 * Math.min(start.cx, W - start.cx) : lx > 0 ? W - x0 : x1;
    const maxH = centred ? 2 * Math.min(start.cy, H - start.cy) : ly > 0 ? H - y0 : y1;
    const fit = Math.min(1, maxW / w, maxH / h);
    w *= fit;
    h *= fit;
    const grow = Math.max(1, minW / w, minH / h);
    w *= grow;
    h *= grow;
    if (centred) {
      x0 = start.cx - w / 2;
      x1 = start.cx + w / 2;
      y0 = start.cy - h / 2;
      y1 = start.cy + h / 2;
    } else {
      if (lx > 0) x1 = x0 + w;
      else x0 = x1 - w;
      if (ly > 0) y1 = y0 + h;
      else y0 = y1 - h;
    }
  }
  return { cx: (x0 + x1) / 2, cy: (y0 + y1) / 2, w: x1 - x0, h: y1 - y0, rotation: 0 };
}

/** The feather (fraction of the shorter canvas side) as an inset in canvas pixels, never more than half the shape. */
export function featherInsetPx(frame: Frame, canvas: Canvas, feather: number): number {
  return Math.max(0, Math.min(feather * Math.min(canvas.width, canvas.height), Math.min(frame.w, frame.h) / 2));
}

/** Where the single feather handle sits: on the inner outline, right of centre and a quarter down (clear of the edge handles). */
export function featherHandlePoint(frame: Frame, kind: 'rect' | 'ellipse', canvas: Canvas, feather: number): Vec {
  const inset = featherInsetPx(frame, canvas, feather);
  const hw = frame.w / 2 - inset;
  const hh = frame.h / 2 - inset;
  return kind === 'ellipse' ? { x: frame.cx + hw * Math.SQRT1_2, y: frame.cy + hh * Math.SQRT1_2 } : { x: frame.cx + hw, y: frame.cy + hh / 2 };
}

/** The feather a pointer at `pointer` (canvas pixels) means for a handle placed by {@link featherHandlePoint}. */
export function featherFromPointer(frame: Frame, kind: 'rect' | 'ellipse', canvas: Canvas, pointer: Vec): number {
  const reach = kind === 'ellipse' ? (pointer.x - frame.cx) / Math.SQRT1_2 : pointer.x - frame.cx;
  const inset = clamp(frame.w / 2 - reach, 0, Math.min(frame.w, frame.h) / 2);
  return round4(clamp(inset / Math.min(canvas.width, canvas.height), 0, FEATHER_MAX));
}

/** A keyboard nudge in canvas fractions (positive right / down), clamped inside the canvas. */
export function nudgeFrame(start: Frame, dxFraction: number, dyFraction: number, canvas: Canvas): Frame {
  return moveFrameBy(start, dxFraction * canvas.width, dyFraction * canvas.height, canvas);
}

/** The reframe point after a keyboard nudge or a drag: canvas fractions in [0, 1]. */
export function clampPoint(p: Vec): Vec {
  return { x: round4(clamp(p.x, 0, 1)), y: round4(clamp(p.y, 0, 1)) };
}

export function pixelToPoint(p: Vec, canvas: Canvas): Vec {
  return clampPoint({ x: p.x / canvas.width, y: p.y / canvas.height });
}

/**
 * The part of the picture a reframe keeps, in canvas pixels: the biggest rectangle of the canvas aspect
 * that fits inside the picture, centred on the subject point and held inside the picture.
 */
export function reframeWindow(picture: { left: number; top: number; right: number; bottom: number }, canvas: Canvas, point: Vec): Frame {
  const pw = Math.max(1, picture.right - picture.left);
  const ph = Math.max(1, picture.bottom - picture.top);
  const aspect = canvas.width / canvas.height;
  const w = Math.min(pw, ph * aspect);
  const h = w / aspect;
  const cx = clamp(point.x * canvas.width, picture.left + w / 2, picture.right - w / 2);
  const cy = clamp(point.y * canvas.height, picture.top + h / 2, picture.bottom - h / 2);
  return { cx, cy, w, h, rotation: 0 };
}

// ─────────────────────────────────────────────────────────────────────────────
// Draft (preview-only) and the ops written on release
// ─────────────────────────────────────────────────────────────────────────────

export type FrameDraftState =
  | { kind: 'mask'; itemId: string; shape: ShapeMask }
  | { kind: 'reframe'; itemId: string; point: Vec }
  | { kind: 'window'; itemId: string; nodeId: string; shape: ShapeMask }
  | { kind: 'pin'; itemId: string; pin: QuadCorners }
  | null;

export interface FrameDraftStore {
  get(): FrameDraftState;
  set(next: FrameDraftState): void;
  subscribe(listener: () => void): () => void;
}

export function createFrameDraft(): FrameDraftStore {
  let value: FrameDraftState = null;
  const listeners = new Set<() => void>();
  return {
    get: () => value,
    set(next) {
      value = next;
      for (const l of [...listeners]) l();
    },
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };
}

const frameDrafts = new WeakMap<object, FrameDraftStore>();
/** The one on-canvas-handle draft store of an editor store. */
export function frameDraftFor(store: EditorStore): FrameDraftStore {
  let d = frameDrafts.get(store);
  if (!d) {
    d = createFrameDraft();
    frameDrafts.set(store, d);
  }
  return d;
}

/** The clip whose mask the mask tool is redrawing (Redraw armed), or null. View state, shared by the tool bar and Compose's Redraw. */
export interface MaskRedrawStore {
  get(): string | null;
  set(next: string | null): void;
  subscribe(listener: () => void): () => void;
}
const maskRedraws = new WeakMap<object, MaskRedrawStore>();
export function maskRedrawFor(store: EditorStore): MaskRedrawStore {
  let r = maskRedraws.get(store);
  if (!r) {
    let value: string | null = null;
    const listeners = new Set<() => void>();
    r = {
      get: () => value,
      set: (next) => {
        if (next === value) return;
        value = next;
        for (const l of [...listeners]) l();
      },
      subscribe: (listener) => {
        listeners.add(listener);
        return () => {
          listeners.delete(listener);
        };
      },
    };
    maskRedraws.set(store, r);
  }
  return r;
}

/** Which grade node's power window the Colour panel is showing (set only while its Advanced tools are open). */
export type WindowTarget = { itemId: string; nodeId: string } | null;
export interface WindowTargetStore {
  get(): WindowTarget;
  set(next: WindowTarget): void;
  subscribe(listener: () => void): () => void;
}
const windowTargets = new WeakMap<object, WindowTargetStore>();
export function windowTargetFor(store: EditorStore): WindowTargetStore {
  let t = windowTargets.get(store);
  if (!t) {
    let value: WindowTarget = null;
    const listeners = new Set<() => void>();
    t = {
      get: () => value,
      set(next) {
        if (value?.itemId === next?.itemId && value?.nodeId === next?.nodeId) return;
        value = next;
        for (const l of [...listeners]) l();
      },
      subscribe(listener) {
        listeners.add(listener);
        return () => listeners.delete(listener);
      },
    };
    windowTargets.set(store, t);
  }
  return t;
}

function mapItem(doc: EditTimeline, itemId: string, fn: (item: Item) => Item): EditTimeline {
  let changed = false;
  const tracks = doc.tracks.map((track) => {
    if (!track.items.some((i) => i.id === itemId)) return track;
    changed = true;
    return { ...track, items: track.items.map((i) => (i.id === itemId ? fn(i) : i)) };
  });
  return changed ? { ...doc, tracks } : doc;
}

function withWindowShape(grade: GradeStack | null | undefined, nodeId: string, shape: ShapeMask): GradeStack | null | undefined {
  if (!grade?.nodes) return grade;
  return { ...grade, nodes: grade.nodes.map((n): GradeNode => (n.id === nodeId && n.window ? { ...n, window: { ...n.window, shape } } : n)) };
}

/** A copy of `doc` where the draft is applied (preview only). */
export function applyFrameDraft(doc: EditTimeline, draft: FrameDraftState): EditTimeline {
  if (!draft) return doc;
  return mapItem(doc, draft.itemId, (item) => {
    if (draft.kind === 'mask' && item.type === 'mediaClip') return { ...(item as MediaClip), mask: draft.shape };
    if (draft.kind === 'reframe' && item.type === 'mediaClip') return { ...(item as MediaClip), reframe: { x: draft.point.x, y: draft.point.y } };
    if (draft.kind === 'pin' && item.type === 'mediaClip') return { ...(item as MediaClip), cornerPin: draft.pin };
    if (draft.kind === 'window' && (item.type === 'mediaClip' || item.type === 'adjustmentClip')) {
      const c = item as MediaClip | AdjustmentClip;
      return { ...c, grade: withWindowShape(c.grade, draft.nodeId, draft.shape) } as Item;
    }
    return item;
  });
}

const sameShape = (a: ShapeMask, b: ShapeMask) =>
  (a.kind ?? 'rect') === (b.kind ?? 'rect') && (a.x ?? 0.5) === (b.x ?? 0.5) && (a.y ?? 0.5) === (b.y ?? 0.5) && (a.w ?? 0.5) === (b.w ?? 0.5) && (a.h ?? 0.5) === (b.h ?? 0.5) && (a.feather ?? 0) === (b.feather ?? 0) && (a.invert ?? false) === (b.invert ?? false);

/** The clip's rect/ellipse mask, or null (no mask, or a segmentation mask the frame handles cannot move). */
export function shapeMaskOf(mask: ClipMask | null | undefined): ShapeMask | null {
  return mask && mask.kind !== 'segmentation' ? mask : null;
}

/** The one `setProperty mask` that stores `next`; none when nothing changed. */
export function maskOps(clip: MediaClip, next: ShapeMask): EditOp[] {
  const own = shapeMaskOf(clip.mask);
  if (own && sameShape(own, next)) return [];
  return [{ type: 'setProperty', itemId: clip.id, key: 'mask', value: next }];
}

export function reframeOps(clip: MediaClip, point: Vec): EditOp[] {
  const p = clampPoint(point);
  if ((clip.reframe?.x ?? 0.5) === p.x && (clip.reframe?.y ?? 0.5) === p.y) return [];
  return [{ type: 'setProperty', itemId: clip.id, key: 'reframe', value: { x: p.x, y: p.y } }];
}

// ─────────────────────────────────────────────────────────────────────────────
// Corner pin (T-013)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * The corner pin's four corners are canvas fractions of the OUTPUT picture (the renderer pads the fitted
 * picture to the whole canvas, then pins that canvas onto these corners; `GradeFilterBuilder.CornerPin`).
 * They do not depend on the clip's transform, so the handles sit exactly where the corners land. The
 * validator accepts [-1, 2], so a corner may sit off the picture.
 */
export const PIN_MIN = -1;
export const PIN_MAX = 2;
export const PIN_CORNERS = ['topLeft', 'topRight', 'bottomRight', 'bottomLeft'] as const;
export type PinCorner = (typeof PIN_CORNERS)[number];

/** A corner pin that leaves the picture as it is (the four canvas corners); what Add corner pin writes. */
export const IDENTITY_PIN: QuadCorners = Object.freeze({ topLeft: { x: 0, y: 0 }, topRight: { x: 1, y: 0 }, bottomRight: { x: 1, y: 1 }, bottomLeft: { x: 0, y: 1 } }) as QuadCorners;

export function clampPinValue(v: number): number {
  return round4(clamp(v, PIN_MIN, PIN_MAX));
}

/** One corner moved to `to` (canvas fractions), clamped to what the number fields accept. */
export function movePinCorner(pin: QuadCorners, corner: PinCorner, to: Vec): QuadCorners {
  return { ...pin, [corner]: { x: clampPinValue(to.x), y: clampPinValue(to.y) } };
}

/** A pointer position in canvas pixels as a pin value. */
export function pinPointFromPixel(p: Vec, canvas: Canvas): Vec {
  return { x: clampPinValue(p.x / canvas.width), y: clampPinValue(p.y / canvas.height) };
}

export type PinToolState =
  | { kind: 'no-clip' }
  | { kind: 'off-screen' }
  | { kind: 'no-pin'; clip: MediaClip }
  | { kind: 'edit'; clip: MediaClip; pin: QuadCorners };

/** What the corner pin tool is looking at. Pure, so the tool's states are testable. */
export function pinToolState(doc: EditTimeline, selection: readonly string[], frame: number): PinToolState {
  if (selection.length !== 1) return { kind: 'no-clip' };
  for (const track of doc.tracks) {
    if (track.kind !== 'video') continue;
    for (const item of track.items) {
      if (item.id !== selection[0] || item.type !== 'mediaClip') continue;
      const clip = item as MediaClip;
      if (frame < clip.recordIn || frame >= clip.recordOut) return { kind: 'off-screen' };
      return clip.cornerPin ? { kind: 'edit', clip, pin: clip.cornerPin } : { kind: 'no-pin', clip };
    }
  }
  return { kind: 'no-clip' };
}

/**
 * Whether the pin's canvas-fraction handles also sit on the picture's own corners: the clip is at the
 * identity transform and its fitted picture (`base`, canvas pixels) is exactly the canvas. When it is not, the
 * handles are still where the renderer lands the FRAME's corners, so the tool says so in words.
 */
export function pinHandlesMatchPicture(values: { positionX: number; positionY: number; scale: number; rotation: number }, base: { w: number; h: number }, canvas: Canvas): boolean {
  const eps = 1e-6;
  const identity = Math.abs(values.positionX) < eps && Math.abs(values.positionY) < eps && Math.abs(values.scale - 1) < eps && Math.abs(values.rotation) < eps;
  return identity && Math.abs(base.w - canvas.width) < 0.5 && Math.abs(base.h - canvas.height) < 0.5;
}

const sameCorner = (a: { x: number; y: number }, b: { x: number; y: number }) => a.x === b.x && a.y === b.y;

/** The one `setProperty cornerPin` that stores `next`; none when nothing changed (or the clip has no pin). */
export function pinOps(clip: MediaClip, next: QuadCorners): EditOp[] {
  const own = clip.cornerPin;
  if (!own || PIN_CORNERS.every((c) => sameCorner(own[c], next[c]))) return [];
  return [{ type: 'setProperty', itemId: clip.id, key: 'cornerPin', value: next }];
}

/** The one `setProperty grade` that stores a new window shape; the window's offsets and tracking are untouched. */
export function windowOps(item: MediaClip | AdjustmentClip, nodeId: string, next: ShapeMask): EditOp[] {
  const node = item.grade?.nodes?.find((n) => n.id === nodeId);
  if (!node?.window || sameShape(node.window.shape, next)) return [];
  return [{ type: 'setProperty', itemId: item.id, key: 'grade', value: withWindowShape(item.grade, nodeId, next) as GradeStack }];
}

/** Commit ops as one undo step; false when there were none or the store refused. */
export function commitFrameOps(store: EditorStore, ops: EditOp[], label: string): boolean {
  if (ops.length === 0) return false;
  try {
    store.dispatchBatch(ops, label);
    return true;
  } catch {
    return false;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// What the viewer shows for the selected clip
// ─────────────────────────────────────────────────────────────────────────────

export type FrameTarget =
  | { id: 'mask'; itemId: string; kind: 'rect' | 'ellipse'; shape: ShapeMask; label: string }
  | { id: 'window'; itemId: string; nodeId: string; kind: 'rect' | 'ellipse'; shape: ShapeMask; /** Where the window sits now: shape plus its (tracked) offset. */ offset: Vec; tracked: boolean; label: string }
  | { id: 'reframe'; itemId: string; point: Vec; label: string };

export const TRACKED_WINDOW_NOTE = 'Tracked windows follow the picture. Turn off tracking to move this by hand.';

/** The single selected media / adjustment clip on a picture track that is on screen at `frame`. */
export function selectedFrameItem(doc: EditTimeline, selection: readonly string[], frame: number): MediaClip | AdjustmentClip | null {
  if (selection.length !== 1) return null;
  for (const track of doc.tracks) {
    if (track.kind !== 'video') continue;
    for (const item of track.items) {
      if (item.id === selection[0] && (item.type === 'mediaClip' || item.type === 'adjustmentClip') && frame >= item.recordIn && frame < item.recordOut) return item as MediaClip | AdjustmentClip;
    }
  }
  return null;
}

/** The handle layers for a clip: its mask, its reframe point and (when the Colour panel asks) a grade node's power window. */
export function resolveFrameTargets(item: MediaClip | AdjustmentClip, frame: number, windowTarget: WindowTarget): FrameTarget[] {
  const out: FrameTarget[] = [];
  if (item.type === 'mediaClip') {
    const clip = item as MediaClip;
    const shape = shapeMaskOf(clip.mask);
    if (shape) out.push({ id: 'mask', itemId: clip.id, kind: shape.kind === 'ellipse' ? 'ellipse' : 'rect', shape, label: 'Mask' });
    if (clip.reframe) out.push({ id: 'reframe', itemId: clip.id, point: clampPoint({ x: clip.reframe.x ?? 0.5, y: clip.reframe.y ?? 0.5 }), label: 'Reframe' });
  }
  if (windowTarget && windowTarget.itemId === item.id) {
    const node = item.grade?.nodes?.find((n) => n.id === windowTarget.nodeId);
    const w = node?.window;
    if (w) {
      const ox = evaluateKeyframed(w.offsetX ?? null, frame, typeof w.offsetX?.value === 'number' ? w.offsetX.value : 0);
      const oy = evaluateKeyframed(w.offsetY ?? null, frame, typeof w.offsetY?.value === 'number' ? w.offsetY.value : 0);
      const tracked = (w.offsetX?.keys?.length ?? 0) > 0 || (w.offsetY?.keys?.length ?? 0) > 0;
      out.push({ id: 'window', itemId: item.id, nodeId: windowTarget.nodeId, kind: w.shape.kind === 'rect' ? 'rect' : 'ellipse', shape: w.shape, offset: { x: ox, y: oy }, tracked, label: 'Power window' });
    }
  }
  return out;
}

/** The mask's shape as the invert toggle in the viewer header writes it. */
export function toggleInvertOps(clip: MediaClip): EditOp[] {
  const shape = shapeMaskOf(clip.mask);
  if (!shape) return [];
  return maskOps(clip, { ...shape, invert: !(shape.invert === true) });
}

/** The selected clip and the handle layers the overlay should draw for it. */
export interface FrameTargetFocus {
  item: MediaClip | AdjustmentClip;
  targets: FrameTarget[];
}

export interface FrameTargetMemo {
  /** The same answer for the same document, selection, sampled frame and published window target. */
  resolve(doc: EditTimeline, selection: readonly string[], frame: number, windowTarget: WindowTarget): FrameTargetFocus | null;
}

function sameSelectionIds(a: readonly string[] | null, b: readonly string[]): boolean {
  if (!a || a.length !== b.length) return false;
  for (let i = 0; i < a.length; i += 1) if (a[i] !== b[i]) return false;
  return true;
}

/**
 * Memoises the overlay's target lookup. {@link selectedFrameItem} walks the tracks and
 * {@link resolveFrameTargets} evaluates the window's tracking keys, and the overlay asks once per playhead
 * tick (the 4 Hz leaf quantum while playing, every frame only while paused). The result is reused until the
 * document, the selection, the sampled frame or the published window target changes.
 */
export function createFrameTargetMemo(): FrameTargetMemo {
  let has = false;
  let keyDoc: EditTimeline | null = null;
  let keySelection: readonly string[] | null = null;
  let keyFrame = Number.NaN;
  let keyWindowItem: string | null = null;
  let keyWindowNode: string | null = null;
  let cached: FrameTargetFocus | null = null;
  return {
    resolve(doc, selection, frame, windowTarget) {
      const windowItem = windowTarget?.itemId ?? null;
      const windowNode = windowTarget?.nodeId ?? null;
      if (has && keyDoc === doc && keyFrame === frame && keyWindowItem === windowItem && keyWindowNode === windowNode && sameSelectionIds(keySelection, selection)) return cached;
      const item = selectedFrameItem(doc, selection, frame);
      cached = item ? { item, targets: resolveFrameTargets(item, frame, windowTarget) } : null;
      has = true;
      keyDoc = doc;
      keySelection = selection.slice();
      keyFrame = frame;
      keyWindowItem = windowItem;
      keyWindowNode = windowNode;
      return cached;
    },
  };
}

/**
 * Focus the picture so the keyboard reaches its handles: the overlay root when it is mounted (it takes
 * programmatic focus while paused), else the program frame (the viewer's real picture node).
 */
export function focusViewerPicture(): void {
  if (typeof document === 'undefined') return;
  const target = document.querySelector<HTMLElement>('[data-testid="viewer-frame-handles"]') ?? document.querySelector<HTMLElement>('[data-testid="program-frame"]');
  target?.focus({ preventScroll: true });
}

/**
 * Enter a picture tool (§7.3): the tool becomes view state, playback stops, and the picture takes focus.
 * View state only — the document is untouched. Leaving the tool (Escape, or another tool) restores
 * `select`; the paused auto-show is unaffected either way.
 */
export function enterViewerTool(store: EditorStore, tool: ViewerTool, options: { focusPicture?: boolean } = {}): void {
  store.setUi({ viewerTool: tool, playbackSpeed: 0 });
  // A list that picks the tool with arrow keys keeps its own focus; Enter or a click moves it to the picture.
  if (options.focusPicture !== false) focusViewerPicture();
}

/** The live (uncommitted) on-canvas draft; the inspectors read their document through {@link applyFrameDraft} so sliders follow a drag. */
export function useFrameDraft(store: EditorStore): FrameDraftState {
  const d = frameDraftFor(store);
  return useSyncExternalStore(d.subscribe, d.get, d.get);
}

/** The clip the mask tool is redrawing, or null. */
export function useMaskRedraw(store: EditorStore): string | null {
  const r = maskRedrawFor(store);
  return useSyncExternalStore(r.subscribe, r.get, r.get);
}

/** The power-window target the Colour panel published. */
export function useWindowTarget(store: EditorStore): WindowTarget {
  const t = windowTargetFor(store);
  return useSyncExternalStore(t.subscribe, t.get, t.get);
}
