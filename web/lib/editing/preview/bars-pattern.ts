/**
 * The `bars` generator as the render draws it: ffmpeg's `smptehdbars` (RP 219 HD bars), measured on a 1920x1080
 * frame with the real `ffmpeg` and stored here in that frame's coordinates, scaled to any canvas. The layout and
 * every flat colour are exact; the luma ramp steps (one level each) can sit a pixel or two off the render's.
 */

export interface BarsRect {
  x: number;
  y: number;
  w: number;
  h: number;
  color: [number, number, number];
}

const DESIGN_W = 1920;
const DESIGN_H = 1080;

type Cell = [x0: number, x1: number, color: [number, number, number]];

const g = (v: number): [number, number, number] => [v, v, v];
const SIDE = 240;
const RIGHT = 1682;

/** Row bands in the 1920x1080 frame: [y0, y1, cells]. The ramp band's gradient is added separately. */
const BANDS: ReadonlyArray<[number, number, readonly Cell[]]> = [
  [0, 630, [
    [0, SIDE, g(102)],
    [240, 446, g(190)],
    [446, 652, [188, 201, 6]],
    [652, 858, [15, 210, 188]],
    [858, 1064, [14, 222, 4]],
    [1064, 1270, [175, 0, 185]],
    [1270, 1476, [174, 0, 1]],
    [1476, RIGHT, [0, 0, 182]],
    [RIGHT, 1920, g(102)],
  ]],
  [630, 720, [
    [0, SIDE, [21, 255, 252]],
    [240, 446, [0, 61, 103]],
    [446, RIGHT, g(190)],
    [RIGHT, 1920, [2, 0, 243]],
  ]],
  [720, 810, [
    [0, SIDE, [251, 255, 10]],
    [240, 446, [62, 0, 118]],
    [446, 530, g(0)],
    [1586, RIGHT, g(255)],
    [RIGHT, 1920, [232, 0, 1]],
  ]],
  [810, 1080, [
    [0, SIDE, g(38)],
    [240, 550, g(0)],
    [550, 962, g(255)],
    [962, 1270, g(0)],
    [1270, 1338, g(4)],
    [1338, 1406, g(0)],
    [1406, 1474, g(10)],
    [1474, RIGHT, g(0)],
    [RIGHT, 1920, g(38)],
  ]],
];

const RAMP_X0 = 530;
const RAMP_X1 = 1586;
const RAMP_Y0 = 720;
const RAMP_Y1 = 810;

/** The rectangles of the pattern on a `width` x `height` canvas, in paint order (edges rounded, never gapped). */
export function barsRects(width: number, height: number): BarsRect[] {
  const sx = width / DESIGN_W;
  const sy = height / DESIGN_H;
  const out: BarsRect[] = [];
  const add = (x0: number, x1: number, y0: number, y1: number, color: [number, number, number]) => {
    const rx0 = Math.round(x0 * sx);
    const rx1 = Math.round(x1 * sx);
    const ry0 = Math.round(y0 * sy);
    const ry1 = Math.round(y1 * sy);
    if (rx1 > rx0 && ry1 > ry0) out.push({ x: rx0, y: ry0, w: rx1 - rx0, h: ry1 - ry0, color });
  };
  for (const [y0, y1, cells] of BANDS) for (const [x0, x1, color] of cells) add(x0, x1, y0, y1, color);
  // The ramp: levels 1..255 across the ramp span, one level per step.
  const span = RAMP_X1 - RAMP_X0;
  for (let level = 1; level <= 255; level++) {
    const x0 = RAMP_X0 + ((level - 1) * span) / 255;
    const x1 = level === 255 ? RAMP_X1 : RAMP_X0 + (level * span) / 255;
    add(x0, x1, RAMP_Y0, RAMP_Y1, g(level));
  }
  return out;
}

/** Paints the pattern onto a 2D context the size of its canvas. */
export function drawBars(ctx: { canvas: { width: number; height: number }; fillStyle: string | CanvasGradient | CanvasPattern; fillRect(x: number, y: number, w: number, h: number): void }): void {
  const { width, height } = ctx.canvas;
  for (const r of barsRects(width, height)) {
    ctx.fillStyle = `rgb(${r.color[0]},${r.color[1]},${r.color[2]})`;
    ctx.fillRect(r.x, r.y, r.w, r.h);
  }
}
