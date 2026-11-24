import type { GradeWheel } from '../types';

/**
 * Pure maths behind the round colour wheels. A stored wheel is `{r, g, b, master}` in [-1, 1] (0 = identity).
 * The puck shows the zero-sum part of (r, g, b); `residual` (their mean) is a brightness shift that every
 * puck move preserves; `master` is the separate luma slider and is never touched here.
 * Screen placement agrees with the vectorscope: x = Cb, y = Cr (up), BT.709.
 */

/** Screen radius (fraction of the disc) -> cast strength is `radius ** RADIUS_CURVE`; inverse is the root. */
export const RADIUS_CURVE = 2;
/** Strength below this snaps to the centre (screen radius < 5% of the disc). */
export const CENTRE_SNAP_STRENGTH = 0.0025;
/** Angle snap tolerance in degrees. */
export const ANGLE_SNAP_DEGREES = 4;

export type WheelPuck = {
  /** Disc units in [-1, 1], y up. */
  x: number;
  y: number;
  /** Cast strength 0..1. */
  strength: number;
  /** Screen (vectorscope) angle in degrees [0, 360), counter-clockwise from +x. */
  angle: number;
  /** Cast hue angle in degrees [0, 360): 0 red, 60 yellow, 120 green, 180 cyan, 240 blue, 300 magenta. */
  hue: number;
  /** Mean of r, g, b: the brightness shift carried alongside the colour. */
  residual: number;
};

export type WheelFromPuckOptions = { snapAngle?: boolean };
export type WheelHue = 'red' | 'orange' | 'yellow' | 'green' | 'cyan' | 'blue' | 'purple' | 'magenta' | 'neutral';
export type WheelDescription = { hue: WheelHue; strength: number; master: number; residual: number };

type Vec3 = [number, number, number];

const DEG = 180 / Math.PI;
const RAD = Math.PI / 180;

const round3 = (v: number): number => {
  const r = Math.round(v * 1000) / 1000;
  return r === 0 ? 0 : r;
};
/** Stored wheel channels keep 6 decimals so the residual and the hue survive rounding (the readout rounds to 3). */
const round6 = (v: number): number => {
  const r = Math.round(v * 1e6) / 1e6;
  return r === 0 ? 0 : r;
};

/**
 * Largest fraction of the requested cast that leaves every channel inside [-1, 1] next to `residual`.
 * The residual itself is never moved: when the cast would overflow a channel it is shortened along the
 * same hue instead (uniform scaling keeps the hue exact).
 */
function fitCast(p: Vec3, residual: number, maxScale: number): number {
  let s = maxScale;
  for (const v of p) {
    if (v > 0) s = Math.min(s, (1 - residual) / v);
    else if (v < 0) s = Math.min(s, (-1 - residual) / v);
  }
  return Math.max(0, Math.min(maxScale, s));
}
const clamp1 = (v: number): number => Math.min(1, Math.max(-1, v));
const norm360 = (a: number): number => ((a % 360) + 360) % 360;
const angleDiff = (a: number, b: number): number => {
  const d = Math.abs(norm360(a) - norm360(b));
  return d > 180 ? 360 - d : d;
};

/** BT.709 chroma of a zero-sum cast. */
function chroma(p: Vec3): { cb: number; cr: number } {
  return {
    cb: -0.1146 * p[0] - 0.3854 * p[1] + 0.5 * p[2],
    cr: 0.5 * p[0] - 0.4542 * p[1] - 0.0458 * p[2],
  };
}

/** Exact inverse of {@link chroma} on the zero-sum plane (pb = -pr - pg). */
function fromChroma(cb: number, cr: number): Vec3 {
  // cb = -0.6146 pr - 0.8854 pg ; cr = 0.5458 pr - 0.4084 pg
  const det = -0.6146 * -0.4084 - -0.8854 * 0.5458;
  const pr = (-0.4084 * cb + 0.8854 * cr) / det;
  const pg = (-0.6146 * cr - 0.5458 * cb) / det;
  return [pr, pg, -pr - pg];
}

const strengthOf = (p: Vec3): number => Math.sqrt((2 / 3) * (p[0] * p[0] + p[1] * p[1] + p[2] * p[2]));
const castHueOf = (p: Vec3): number => norm360(Math.atan2((p[1] - p[2]) / Math.sqrt(3), p[0]) * DEG);
const castVec = (hueDeg: number, c: number): Vec3 => [
  c * Math.cos(hueDeg * RAD),
  c * Math.cos((hueDeg - 120) * RAD),
  c * Math.cos((hueDeg + 120) * RAD),
];

/** Screen angles of the six primary/secondary hues (red, yellow, green, cyan, blue, magenta). */
const SNAP_ANGLES: number[] = [0, 60, 120, 180, 240, 300].map((h) => {
  const { cb, cr } = chroma(castVec(h, 1));
  return norm360(Math.atan2(cr, cb) * DEG);
});

function parts(w: GradeWheel): { p: Vec3; residual: number; master: number } {
  const r = w.r ?? 0;
  const g = w.g ?? 0;
  const b = w.b ?? 0;
  const residual = (r + g + b) / 3;
  return { p: [r - residual, g - residual, b - residual], residual, master: w.master ?? 0 };
}

/** Where a wheel's puck sits (disc units, y up) plus its readout values. */
export function wheelPuck(w: GradeWheel): WheelPuck {
  const { p, residual } = parts(w);
  const strength = Math.min(1, strengthOf(p));
  const { cb, cr } = chroma(p);
  const mag = Math.hypot(cb, cr);
  if (mag < 1e-12) return { x: 0, y: 0, strength: 0, angle: 0, hue: 0, residual };
  const radius = Math.min(1, Math.sqrt(strength) ** (2 / RADIUS_CURVE));
  const screen = Math.atan2(cr, cb);
  return {
    x: (cb / mag) * radius,
    y: (cr / mag) * radius,
    strength,
    angle: norm360(screen * DEG),
    hue: castHueOf(p),
    residual,
  };
}

/** The wheel for a puck at disc position (x, y), keeping `prev.master` and `prev`'s residual. */
export function wheelFromPuck(x: number, y: number, prev: GradeWheel, opts: WheelFromPuckOptions = {}): GradeWheel {
  const { residual, master } = parts(prev);
  let radius = Math.hypot(x, y);
  if (radius > 1) radius = 1;
  let p: Vec3 = [0, 0, 0];
  if (radius >= 0.05 - 1e-9) {
    let screen = norm360(Math.atan2(y, x) * DEG);
    if (opts.snapAngle) {
      for (const s of SNAP_ANGLES) if (angleDiff(screen, s) <= ANGLE_SNAP_DEGREES) { screen = s; break; }
    }
    const dir = fromChroma(Math.cos(screen * RAD), Math.sin(screen * RAD));
    const c0 = strengthOf(dir);
    const k = radius ** RADIUS_CURVE / c0;
    p = [dir[0] * k, dir[1] * k, dir[2] * k];
    // Keep the stored brightness shift and the puck's hue: shrink the cast until every channel fits.
    const s = fitCast(p, residual, 1);
    p = [p[0] * s, p[1] * s, p[2] * s];
  }
  return {
    r: round6(clamp1(p[0] + residual)),
    g: round6(clamp1(p[1] + residual)),
    b: round6(clamp1(p[2] + residual)),
    master,
  };
}

/** Arrow-key move of the puck by (dx, dy) * step disc units. Leaving the centre jumps to the snap radius. */
export function nudgeWheel(w: GradeWheel, dx: number, dy: number, step = 0.02, opts: WheelFromPuckOptions = {}): GradeWheel {
  const puck = wheelPuck(w);
  const len = Math.hypot(dx, dy);
  if (len === 0) return wheelFromPuck(puck.x, puck.y, w, opts);
  let nx = puck.x + dx * step;
  let ny = puck.y + dy * step;
  if (Math.hypot(puck.x, puck.y) === 0) {
    nx = (dx / len) * 0.05;
    ny = (dy / len) * 0.05;
  }
  return wheelFromPuck(nx, ny, w, opts);
}

/** Clears r, g, b entirely (residual included); keeps master. */
export function resetWheelColour(w: GradeWheel): GradeWheel {
  return { r: 0, g: 0, b: 0, master: w.master ?? 0 };
}

export function isIdentityWheel(w: GradeWheel): boolean {
  return (w.r ?? 0) === 0 && (w.g ?? 0) === 0 && (w.b ?? 0) === 0 && (w.master ?? 0) === 0;
}

/** Pointer position -> disc coordinates (y up), clamped to the unit disc. */
export function pointerToDisc(clientX: number, clientY: number, rect: { left: number; top: number; width: number; height: number }): { x: number; y: number } {
  const hw = rect.width / 2 || 1;
  const hh = rect.height / 2 || 1;
  const x = (clientX - (rect.left + hw)) / hw;
  const y = -(clientY - (rect.top + hh)) / hh;
  const len = Math.hypot(x, y);
  return len > 1 ? { x: x / len, y: y / len } : { x, y };
}

const HUE_BUCKETS: Array<[WheelHue, number]> = [
  ['red', 0], ['orange', 30], ['yellow', 60], ['green', 120], ['cyan', 180], ['blue', 240], ['purple', 270], ['magenta', 300],
];

/** Plain data for an accessible readout (keys, not English). strength is 0..100. */
export function describeWheel(w: GradeWheel): WheelDescription {
  const puck = wheelPuck(w);
  const strength = Math.round(puck.strength * 100);
  let hue: WheelHue = 'neutral';
  if (strength >= 1) {
    let best = 361;
    for (const [name, deg] of HUE_BUCKETS) {
      const d = angleDiff(puck.hue, deg);
      if (d < best) { best = d; hue = name; }
    }
  }
  return { hue, strength, master: w.master ?? 0, residual: round3(puck.residual) };
}
