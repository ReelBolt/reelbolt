/**
 * R07.2 / R10.3: the geometry behind the viewer's handle layer. Pure functions, no DOM.
 *
 * Everything works in CANVAS PIXELS (the timeline's own resolution, y down), so rotation never shears:
 * the document stores position as fractions of the canvas (x of its width, y of its height, positive
 * right/down), scale relative to the fitted picture, and rotation in degrees clockwise (the compositor's
 * convention in gl-compositor.ts). The handle layer is generic: a {@link Frame} is any rotated rectangle
 * with a list of {@link HandleSpec}s, so crop / reframe / corner-pin handles can reuse the drag math later.
 */

export type Vec = { x: number; y: number };

/** The editable numbers of a clip's transform, in document units. `scaleY` absent means the same as `scale` (a uniform scale). */
export type TransformValues = { positionX: number; positionY: number; scale: number; scaleY?: number; rotation: number };

/** The vertical scale of values: `scaleY` when set, else the uniform `scale`. */
export const scaleYOf = (v: TransformValues): number => (v.scaleY !== undefined && v.scaleY > 0 ? v.scaleY : v.scale);

export const IDENTITY_TRANSFORM: TransformValues = { positionX: 0, positionY: 0, scale: 1, rotation: 0 };

export const SCALE_MIN = 0.02;
export const SCALE_MAX = 8;
export const POSITION_LIMIT = 2;

/** A rotated rectangle in canvas pixels: centre, size, rotation in degrees clockwise. */
export type Frame = { cx: number; cy: number; w: number; h: number; rotation: number };

export type CornerId = 'tl' | 'tr' | 'br' | 'bl';

export type HandleKind = 'corner' | 'rotate' | 'edge';

/** One draggable point of a frame, in the frame's local space (units of w/h around the centre: -0.5..0.5). */
export type HandleSpec = { id: string; kind: HandleKind; local: Vec };

export const EDGE_HANDLES: readonly HandleSpec[] = [
  { id: 't', kind: 'edge', local: { x: 0, y: -0.5 } },
  { id: 'r', kind: 'edge', local: { x: 0.5, y: 0 } },
  { id: 'b', kind: 'edge', local: { x: 0, y: 0.5 } },
  { id: 'l', kind: 'edge', local: { x: -0.5, y: 0 } },
];

export const CORNER_HANDLES: readonly HandleSpec[] = [
  { id: 'tl', kind: 'corner', local: { x: -0.5, y: -0.5 } },
  { id: 'tr', kind: 'corner', local: { x: 0.5, y: -0.5 } },
  { id: 'br', kind: 'corner', local: { x: 0.5, y: 0.5 } },
  { id: 'bl', kind: 'corner', local: { x: -0.5, y: 0.5 } },
];

const RAD = Math.PI / 180;

export const clamp = (v: number, lo: number, hi: number) => (v < lo ? lo : v > hi ? hi : v);

export function rotateVec(v: Vec, degrees: number): Vec {
  const c = Math.cos(degrees * RAD);
  const s = Math.sin(degrees * RAD);
  return { x: v.x * c - v.y * s, y: v.x * s + v.y * c };
}

/** A frame-local point (units of the frame's size, centre origin) to canvas pixels. */
export function localToCanvas(frame: Frame, local: Vec): Vec {
  const r = rotateVec({ x: local.x * frame.w, y: local.y * frame.h }, frame.rotation);
  return { x: frame.cx + r.x, y: frame.cy + r.y };
}

/** A canvas point into frame-local pixels (centre origin, unrotated). */
export function canvasToLocal(frame: Frame, p: Vec): Vec {
  return rotateVec({ x: p.x - frame.cx, y: p.y - frame.cy }, -frame.rotation);
}

/** The picture's size at scale 1 in canvas pixels: source fitted into the canvas by the aspect policy, minus the crop. */
export function baseSize(
  canvas: { width: number; height: number },
  source: { width: number; height: number } | null,
  policy: 'fit' | 'fill' | 'stretch',
  crop?: { left?: number; top?: number; right?: number; bottom?: number } | null,
): { w: number; h: number } {
  let fx = 1;
  let fy = 1;
  if (source && source.width > 0 && source.height > 0 && policy !== 'stretch') {
    const s = source.width / source.height;
    const c = canvas.width / canvas.height;
    if (Math.abs(s - c) >= 1e-6) {
      const wider = s > c;
      if (policy === 'fill') [fx, fy] = wider ? [s / c, 1] : [1, c / s];
      else [fx, fy] = wider ? [1, c / s] : [s / c, 1];
    }
  }
  const edge = (v: number | undefined) => (typeof v === 'number' && Number.isFinite(v) && v > 0 && v < 1 ? v : 0);
  const cw = Math.max(0.01, 1 - edge(crop?.left) - edge(crop?.right));
  const ch = Math.max(0.01, 1 - edge(crop?.top) - edge(crop?.bottom));
  return { w: canvas.width * fx * cw, h: canvas.height * fy * ch };
}

/** The frame a clip occupies for given transform values. */
export function frameFor(values: TransformValues, base: { w: number; h: number }, canvas: { width: number; height: number }): Frame {
  return {
    cx: canvas.width / 2 + values.positionX * canvas.width,
    cy: canvas.height / 2 + values.positionY * canvas.height,
    w: base.w * values.scale,
    h: base.h * scaleYOf(values),
    rotation: values.rotation,
  };
}

/** The axis-aligned bounds of a rotated frame. */
export function bounds(frame: Frame): { left: number; top: number; right: number; bottom: number } {
  const pts = CORNER_HANDLES.map((h) => localToCanvas(frame, h.local));
  return {
    left: Math.min(...pts.map((p) => p.x)),
    right: Math.max(...pts.map((p) => p.x)),
    top: Math.min(...pts.map((p) => p.y)),
    bottom: Math.max(...pts.map((p) => p.y)),
  };
}

export function clampValues(v: TransformValues): TransformValues {
  return {
    positionX: clamp(v.positionX, -POSITION_LIMIT, POSITION_LIMIT),
    positionY: clamp(v.positionY, -POSITION_LIMIT, POSITION_LIMIT),
    scale: clamp(v.scale, SCALE_MIN, SCALE_MAX),
    ...(v.scaleY !== undefined ? { scaleY: clamp(v.scaleY, SCALE_MIN, SCALE_MAX) } : {}),
    rotation: clamp(v.rotation, -360, 360),
  };
}

/** Move by a pixel delta. */
export function moveBy(start: TransformValues, dx: number, dy: number, canvas: { width: number; height: number }): TransformValues {
  return clampValues({ ...start, positionX: start.positionX + dx / canvas.width, positionY: start.positionY + dy / canvas.height });
}

export type ScaleOptions = { fromCentre?: boolean; /** Shift: keep the picture's ratio (the default is a free drag). */ keepAspect?: boolean };

/**
 * Apply per-axis factors about an anchor (frame-local, units of the frame size). The anchor stays put, the centre
 * moves with it; works in the frame's rotated space so a rotated clip scales along its own axes. `scaleY` is only
 * written when the aspect changes (or was already set): a uniform result stays a uniform `scale`.
 */
function applyFactors(start: TransformValues, startFrame: Frame, anchorLocal: Vec, fx: number, fy: number, canvas: { width: number; height: number }): TransformValues {
  const anchor = localToCanvas(startFrame, anchorLocal);
  const d = rotateVec({ x: startFrame.cx - anchor.x, y: startFrame.cy - anchor.y }, -startFrame.rotation);
  const moved = rotateVec({ x: d.x * fx, y: d.y * fy }, startFrame.rotation);
  const centre = { x: anchor.x + moved.x, y: anchor.y + moved.y };
  const scale = start.scale * fx;
  const sy = scaleYOf(start) * fy;
  const uniform = start.scaleY === undefined && Math.abs(fx - fy) < 1e-9;
  return clampValues({
    ...start,
    scale,
    ...(uniform ? {} : { scaleY: sy }),
    positionX: (centre.x - canvas.width / 2) / canvas.width,
    positionY: (centre.y - canvas.height / 2) / canvas.height,
  });
}

/**
 * Drag a corner. FREE by default (width and height follow the pointer independently, so the ratio changes and
 * `scaleY` is written); `keepAspect` (Shift) keeps the ratio. The opposite corner stays put, or the centre with
 * `fromCentre` (Alt).
 */
export function scaleFromCorner(
  start: TransformValues,
  startFrame: Frame,
  corner: HandleSpec,
  pointer: Vec,
  canvas: { width: number; height: number },
  options: ScaleOptions = {},
): TransformValues {
  const anchorLocal: Vec = options.fromCentre ? { x: 0, y: 0 } : { x: -corner.local.x, y: -corner.local.y };
  const anchor = localToCanvas(startFrame, anchorLocal);
  const cornerVec = { x: (corner.local.x - anchorLocal.x) * startFrame.w, y: (corner.local.y - anchorLocal.y) * startFrame.h };
  const p = canvasToLocal({ ...startFrame, cx: anchor.x, cy: anchor.y }, pointer);
  const denom = cornerVec.x * cornerVec.x + cornerVec.y * cornerVec.y;
  if (denom === 0) return start;
  const lo = (s: number) => SCALE_MIN / s;
  const hi = (s: number) => SCALE_MAX / s;
  if (options.keepAspect) {
    const factor = clamp((p.x * cornerVec.x + p.y * cornerVec.y) / denom, Math.max(lo(start.scale), lo(scaleYOf(start))), Math.min(hi(start.scale), hi(scaleYOf(start))));
    return applyFactors(start, startFrame, anchorLocal, factor, factor, canvas);
  }
  const fx = clamp(p.x / cornerVec.x, lo(start.scale), hi(start.scale));
  const fy = clamp(p.y / cornerVec.y, lo(scaleYOf(start)), hi(scaleYOf(start)));
  return applyFactors(start, startFrame, anchorLocal, fx, fy, canvas);
}

/** Drag an edge midpoint: stretch ONE axis (the other keeps its size). The opposite edge stays put, or the centre with `fromCentre`. */
export function scaleFromEdge(
  start: TransformValues,
  startFrame: Frame,
  edge: HandleSpec,
  pointer: Vec,
  canvas: { width: number; height: number },
  options: ScaleOptions = {},
): TransformValues {
  const anchorLocal: Vec = options.fromCentre ? { x: 0, y: 0 } : { x: -edge.local.x, y: -edge.local.y };
  const anchor = localToCanvas(startFrame, anchorLocal);
  const p = canvasToLocal({ ...startFrame, cx: anchor.x, cy: anchor.y }, pointer);
  const horizontal = edge.local.x !== 0;
  const extent = horizontal ? (edge.local.x - anchorLocal.x) * startFrame.w : (edge.local.y - anchorLocal.y) * startFrame.h;
  if (extent === 0) return start;
  const current = horizontal ? start.scale : scaleYOf(start);
  const factor = clamp((horizontal ? p.x : p.y) / extent, SCALE_MIN / current, SCALE_MAX / current);
  return horizontal ? applyFactors(start, startFrame, anchorLocal, factor, 1, canvas) : applyFactors(start, startFrame, anchorLocal, 1, factor, canvas);
}

/** Normalise an angle into (-180, 180]. */
export function wrapDegrees(deg: number): number {
  let d = ((((deg + 180) % 360) + 360) % 360) - 180;
  if (d === -180) d = 180;
  return d;
}

/** The rotation that points the clip's "up" at the pointer; `step` snaps to multiples (Shift = 15). */
export function rotateTo(centre: Vec, pointer: Vec, step = 0): number {
  const deg = Math.atan2(pointer.x - centre.x, -(pointer.y - centre.y)) / RAD;
  const snapped = step > 0 ? Math.round(deg / step) * step : deg;
  return wrapDegrees(snapped);
}

// ── presets ──────────────────────────────────────────────────────────────────

/** Fit: the whole picture visible inside the canvas, centred, upright. */
export function fitValues(base: { w: number; h: number }, canvas: { width: number; height: number }): TransformValues {
  return { positionX: 0, positionY: 0, rotation: 0, scale: round(Math.min(canvas.width / base.w, canvas.height / base.h)) };
}

/** Fill: the picture covers the whole canvas (the overflow is cut), centred, upright. */
export function fillValues(base: { w: number; h: number }, canvas: { width: number; height: number }): TransformValues {
  return { positionX: 0, positionY: 0, rotation: 0, scale: round(Math.max(canvas.width / base.w, canvas.height / base.h)) };
}

/** A cell of the 3x3 pad: -1 = left/top, 0 = centre, 1 = right/bottom. Keeps scale and rotation; edges flush with the canvas. */
export function padValues(current: TransformValues, base: { w: number; h: number }, canvas: { width: number; height: number }, col: -1 | 0 | 1, row: -1 | 0 | 1, margin = 0): TransformValues {
  const b = bounds(frameFor(current, base, canvas));
  const bw = (b.right - b.left) / canvas.width;
  const bh = (b.bottom - b.top) / canvas.height;
  return {
    ...current,
    positionX: round(col * ((1 - bw) / 2 - margin)),
    positionY: round(row * ((1 - bh) / 2 - margin)),
  };
}

export const CORNER_PRESET_SCALE = 0.3;
export const CORNER_PRESET_MARGIN = 0.04;

/** Picture-in-corner: a small, upright picture tucked into a corner with a margin. */
export function cornerPresetValues(base: { w: number; h: number }, canvas: { width: number; height: number }, col: -1 | 1, row: -1 | 1): TransformValues {
  const upright: TransformValues = { positionX: 0, positionY: 0, rotation: 0, scale: round(CORNER_PRESET_SCALE * Math.min(canvas.width / base.w, canvas.height / base.h)) };
  return padValues(upright, base, canvas, col, row, CORNER_PRESET_MARGIN);
}

export const NUDGE_PX = 1;
export const NUDGE_PX_BIG = 10;

export function nudgeValues(start: TransformValues, dxPx: number, dyPx: number, canvas: { width: number; height: number }): TransformValues {
  return moveBy(start, dxPx, dyPx, canvas);
}

function round(v: number): number {
  return Math.round(v * 1e6) / 1e6;
}

// ── snapping ─────────────────────────────────────────────────────────────────

export type Guide = { axis: 'x' | 'y'; at: number; kind: 'centre' | 'third' | 'safe' | 'clip' };

/** Title-safe (90%) and action-safe (95%) margins as canvas fractions. */
export const SAFE_MARGINS = [0.05, 0.1] as const;

/** Every vertical (x) and horizontal (y) line a moving frame may snap to. */
export function snapTargets(canvas: { width: number; height: number }, others: readonly Frame[]): Guide[] {
  const out: Guide[] = [
    { axis: 'x', at: canvas.width / 2, kind: 'centre' },
    { axis: 'y', at: canvas.height / 2, kind: 'centre' },
    { axis: 'x', at: canvas.width / 3, kind: 'third' },
    { axis: 'x', at: (canvas.width * 2) / 3, kind: 'third' },
    { axis: 'y', at: canvas.height / 3, kind: 'third' },
    { axis: 'y', at: (canvas.height * 2) / 3, kind: 'third' },
  ];
  for (const m of SAFE_MARGINS) {
    out.push({ axis: 'x', at: canvas.width * m, kind: 'safe' }, { axis: 'x', at: canvas.width * (1 - m), kind: 'safe' });
    out.push({ axis: 'y', at: canvas.height * m, kind: 'safe' }, { axis: 'y', at: canvas.height * (1 - m), kind: 'safe' });
  }
  for (const f of others) {
    const b = bounds(f);
    out.push({ axis: 'x', at: b.left, kind: 'clip' }, { axis: 'x', at: (b.left + b.right) / 2, kind: 'clip' }, { axis: 'x', at: b.right, kind: 'clip' });
    out.push({ axis: 'y', at: b.top, kind: 'clip' }, { axis: 'y', at: (b.top + b.bottom) / 2, kind: 'clip' }, { axis: 'y', at: b.bottom, kind: 'clip' });
  }
  return out;
}

/**
 * Snap a moving frame: its left/centre/right and top/centre/bottom against every target within
 * `threshold` pixels. Returns the pixel correction to add to the move and the guides that fired.
 */
export function snapMove(frame: Frame, targets: readonly Guide[], threshold: number): { dx: number; dy: number; guides: Guide[] } {
  const b = bounds(frame);
  const xs = [b.left, (b.left + b.right) / 2, b.right];
  const ys = [b.top, (b.top + b.bottom) / 2, b.bottom];
  const best = (edges: number[], axis: 'x' | 'y') => {
    let delta = 0;
    let dist = threshold + 1;
    let hit: Guide[] = [];
    for (const t of targets) {
      if (t.axis !== axis) continue;
      for (const e of edges) {
        const d = t.at - e;
        const a = Math.abs(d);
        if (a > threshold) continue;
        if (a < dist - 1e-9) {
          dist = a;
          delta = d;
          hit = [t];
        } else if (Math.abs(a - dist) <= 1e-9 && d === delta) hit.push(t);
      }
    }
    return { delta, hit };
  };
  const bx = best(xs, 'x');
  const by = best(ys, 'y');
  return { dx: bx.delta, dy: by.delta, guides: [...bx.hit, ...by.hit] };
}
