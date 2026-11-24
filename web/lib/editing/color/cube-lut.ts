/**
 * Browser-side `.cube` parser for the preview-only viewing LUT. Mirrors the server's `CubeLut.Parse`
 * (inference/.../TimelineRender/CubeLut.cs) in what it accepts and refuses: Adobe/IRIDAS text: 3D,
 * 1D, or a shaper + 3D pair, 16 MiB cap.
 * 3D-only files keep the scalar domain (min of DOMAIN_MIN, max of DOMAIN_MAX), values normalised into 0..1 and
 * clamped. Pure; no DOM.
 */

export const CUBE_LUT_MIN_SIZE = 2;
export const CUBE_LUT_MAX_SIZE = 65;
export const CUBE_LUT_MIN_1D_SIZE = 2;
export const CUBE_LUT_MAX_1D_SIZE = 65536;
export const CUBE_LUT_MAX_BYTES = 16 * 1024 * 1024;
/** The 3D grid a shaper + 3D file is baked to when an intensity below 1 is asked for. */
export const CUBE_LUT_BAKE_SIZE = 65;

export type CubeLutErrorCode =
  | 'too_large'
  | 'size_out_of_range'
  | 'size_missing'
  | 'wrong_row_count'
  | 'not_finite'
  | 'bad_domain'
  | 'malformed'
  | 'unsupported_keyword';

/** The numbers a refusal can name (only the ones that apply to its code are set). */
export interface CubeLutErrorDetails {
  expected?: number;
  found?: number;
  keyword?: string;
}

/** A refused `.cube`. `code` is stable; map it to user text at the UI layer. */
export class CubeLutError extends Error {
  readonly code: CubeLutErrorCode;
  readonly details: CubeLutErrorDetails;
  constructor(code: CubeLutErrorCode, detail?: string, details: CubeLutErrorDetails = {}) {
    super(detail ? `${code}: ${detail}` : code);
    this.name = 'CubeLutError';
    this.code = code;
    this.details = details;
  }
}

export type CubeLutKind = '3d' | '1d' | 'shaper3d';

/** The canonical 1D table: `size` entries over x in [0,1] (the input range is already folded in), RGB interleaved, 0..1. */
export interface CubeLutOneD {
  size: number;
  data: Float32Array;
}

export interface CubeLut {
  /** Absent = `3d` (tables built outside the parser, e.g. camera profiles). */
  kind?: CubeLutKind;
  /** The 3D grid size; 0 for a 1D-only file. */
  size: number;
  /** The scalar domain the file declared (min of DOMAIN_MIN on all three, as the server reads it). 0..1 for 1D and shaper files. */
  domainMin: [number, number, number];
  domainMax: [number, number, number];
  /** size^3 * 3 floats, R fastest, already normalised into 0..1 by the domain. Empty for a 1D-only file. */
  data: Float32Array;
  /** Present for `1d` and `shaper3d`. */
  oneD?: CubeLutOneD;
  title?: string;
}

const INT_RE = /^[+-]?\d+$/;
const FLOAT_RE = /^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$/;
const NONFINITE_RE = /^[+-]?(inf(inity)?|nan)$/i;

function startsWithKey(line: string, key: string): boolean {
  return line.length >= key.length && line.slice(0, key.length).toUpperCase() === key;
}

function parseNumbers(text: string, count: number): number[] {
  const parts = text.split(/[ \t]+/).filter((p) => p.length > 0);
  if (parts.length !== count) throw new CubeLutError('malformed', `expected ${count === 3 ? 'three' : 'two'} numbers`);
  const out: number[] = [];
  for (const part of parts) {
    if (!FLOAT_RE.test(part)) {
      if (NONFINITE_RE.test(part)) throw new CubeLutError('not_finite', part);
      throw new CubeLutError('malformed', 'not a number');
    }
    const value = Math.fround(Number(part));
    if (!Number.isFinite(value)) throw new CubeLutError('not_finite', part);
    out.push(value);
  }
  return out;
}

function parseSize(arg: string, min: number, max: number): number {
  const n = INT_RE.test(arg) ? Number(arg) : Number.NaN;
  if (!Number.isInteger(n) || n < min || n > max) throw new CubeLutError('size_out_of_range');
  return n;
}

const clamp01 = (v: number): number => Math.min(1, Math.max(0, v));

/** `eval(x)` of the spec: the parsed table read over its input range [a,b], linear, clamped. */
function evalTable(rows: ArrayLike<number>, size: number, a: number, b: number, x: number, c: number): number {
  const t = clamp01((x - a) / (b - a)) * (size - 1);
  const i0 = Math.floor(t);
  const i1 = Math.min(size - 1, i0 + 1);
  const f = t - i0;
  const v0 = rows[i0 * 3 + c] as number;
  const v1 = rows[i1 * 3 + c] as number;
  return v0 + (v1 - v0) * f;
}

/** The canonical table over [0,1]: verbatim when the range is 0..1 (no float noise), else re-evaluated at i/(N-1). */
function canonicalOneD(rows: Float32Array, size: number, a: number, b: number): Float32Array {
  if (a === 0 && b === 1) return Float32Array.from(rows);
  const out = new Float32Array(size * 3);
  for (let i = 0; i < size; i += 1) {
    const x = i / (size - 1);
    for (let c = 0; c < 3; c += 1) out[i * 3 + c] = clamp01(evalTable(rows, size, a, b, x, c));
  }
  return out;
}

export function parseCubeLut(text: string): CubeLut {
  if (text.length > CUBE_LUT_MAX_BYTES) throw new CubeLutError('too_large');

  let size3 = 0;
  let size1 = 0;
  let title: string | undefined;
  let domainMin = 0;
  let domainMax = 1;
  let domainKeyword: string | undefined;
  let range1: [number, number] = [0, 1];
  let range1Set = false;
  let range3: [number, number] = [0, 1];
  let range3Set = false;
  const values: number[] = [];

  for (const rawLine of text.split('\n')) {
    const line = rawLine.trim();
    if (line.length === 0 || line.startsWith('#')) continue;

    if (/^TITLE(\s|$)/i.test(line)) {
      title = line.slice(5).trim().replace(/^"+|"+$/g, '');
      continue;
    }
    if (startsWithKey(line, 'LUT_3D_INPUT_RANGE')) {
      const [lo, hi] = parseNumbers(line.slice(18), 2) as [number, number];
      range3 = [lo, hi];
      range3Set = true;
      continue;
    }
    if (startsWithKey(line, 'LUT_1D_INPUT_RANGE')) {
      const [lo, hi] = parseNumbers(line.slice(18), 2) as [number, number];
      range1 = [lo, hi];
      range1Set = true;
      continue;
    }
    if (startsWithKey(line, 'LUT_3D_SIZE')) {
      size3 = parseSize(line.slice(11).trim(), CUBE_LUT_MIN_SIZE, CUBE_LUT_MAX_SIZE);
      continue;
    }
    if (startsWithKey(line, 'LUT_1D_SIZE')) {
      size1 = parseSize(line.slice(11).trim(), CUBE_LUT_MIN_1D_SIZE, CUBE_LUT_MAX_1D_SIZE);
      continue;
    }
    if (startsWithKey(line, 'DOMAIN_MIN')) {
      domainMin = Math.min(...parseNumbers(line.slice(10), 3));
      domainKeyword ??= 'DOMAIN_MIN';
      continue;
    }
    if (startsWithKey(line, 'DOMAIN_MAX')) {
      domainMax = Math.max(...parseNumbers(line.slice(10), 3));
      domainKeyword ??= 'DOMAIN_MAX';
      continue;
    }
    if (/^[A-Za-z]/.test(line)) {
      const word = line.split(/[ \t]+/)[0] as string;
      if (!NONFINITE_RE.test(word)) {
        // Echoed in the UI: letters, digits and '_' only, at most 40 characters (the engine bounds it the same way).
        const keyword = word.replace(/[^A-Za-z0-9_]/g, '').slice(0, 40) || '?';
        throw new CubeLutError('unsupported_keyword', keyword, { keyword });
      }
    }
    const triple = parseNumbers(line, 3);
    values.push(triple[0] as number, triple[1] as number, triple[2] as number);
  }

  if (size3 === 0 && size1 === 0) throw new CubeLutError('size_missing');
  const kind: CubeLutKind = size3 > 0 && size1 > 0 ? 'shaper3d' : size1 > 0 ? '1d' : '3d';
  if (kind === 'shaper3d' && domainKeyword !== undefined) {
    throw new CubeLutError('unsupported_keyword', 'use LUT_1D_INPUT_RANGE / LUT_3D_INPUT_RANGE', { keyword: domainKeyword });
  }

  const rows3 = size3 * size3 * size3;
  const expectedRows = (kind === 'shaper3d' ? size1 + rows3 : kind === '1d' ? size1 : rows3);
  const foundRows = Math.floor(values.length / 3);
  if (values.length !== expectedRows * 3) {
    const detail = kind === '3d' ? `${size3}^3 needs ${rows3}, found ${foundRows}` : `needs ${expectedRows}, found ${foundRows}`;
    throw new CubeLutError('wrong_row_count', detail, { expected: expectedRows, found: foundRows });
  }

  if (kind === '3d') {
    // A 3D-only file reads LUT_3D_INPUT_RANGE as DOMAIN_MIN a a a / DOMAIN_MAX b b b (last line wins, as before).
    if (range3Set) {
      domainMin = range3[0];
      domainMax = range3[1];
    }
    if (!(domainMax > domainMin)) throw new CubeLutError('bad_domain');
    const data = new Float32Array(values.length);
    const span = domainMax - domainMin;
    for (let i = 0; i < values.length; i += 1) {
      const normalised = ((values[i] as number) - domainMin) / span;
      data[i] = Number.isFinite(normalised) ? clamp01(normalised) : 0;
    }
    const lut: CubeLut = { kind, size: size3, domainMin: [domainMin, domainMin, domainMin], domainMax: [domainMax, domainMax, domainMax], data };
    if (title !== undefined) lut.title = title;
    return lut;
  }

  if (kind === '1d') {
    // Input range: LUT_1D_INPUT_RANGE, else the scalar DOMAIN_MIN/MAX, else 0..1.
    const [a, b] = range1Set ? range1 : ([domainMin, domainMax] as [number, number]);
    if (!(b > a)) throw new CubeLutError('bad_domain');
    const rows = new Float32Array(values.length);
    for (let i = 0; i < values.length; i += 1) rows[i] = clamp01(values[i] as number);
    const lut: CubeLut = {
      kind,
      size: 0,
      domainMin: [0, 0, 0],
      domainMax: [1, 1, 1],
      data: new Float32Array(0),
      oneD: { size: size1, data: canonicalOneD(rows, size1, a, b) },
    };
    if (title !== undefined) lut.title = title;
    return lut;
  }

  // Shaper + 3D: the N 1D rows first, then the n^3 3D rows.
  const [a, b] = range1;
  const [c, d] = range3;
  if (!(b > a) || !(d > c)) throw new CubeLutError('bad_domain');
  const shaper = new Float32Array(size1 * 3);
  for (let i = 0; i < shaper.length; i += 1) shaper[i] = clamp01(((values[i] as number) - c) / (d - c));
  const data = new Float32Array(rows3 * 3);
  for (let i = 0; i < data.length; i += 1) data[i] = clamp01(values[size1 * 3 + i] as number);
  const lut: CubeLut = {
    kind,
    size: size3,
    domainMin: [0, 0, 0],
    domainMax: [1, 1, 1],
    data,
    oneD: { size: size1, data: canonicalOneD(shaper, size1, a, b) },
  };
  if (title !== undefined) lut.title = title;
  return lut;
}

/** Canonical `.cube` text of the 3D part as the server writer emits it (`0.######` numbers, domain 0..1). */
export function toCubeText(lut: CubeLut): string {
  const num = (v: number): string => {
    const s = v.toFixed(6).replace(/0+$/, '').replace(/\.$/, '');
    return s === '-0' || s === '' ? '0' : s;
  };
  const lines = [`TITLE "${(lut.title ?? 'ReelBolt').replace(/"/g, "'").replace(/\n/g, ' ')}"`, `LUT_3D_SIZE ${lut.size}`, 'DOMAIN_MIN 0 0 0', 'DOMAIN_MAX 1 1 1'];
  for (let i = 0; i < lut.data.length; i += 3) {
    lines.push(`${num(lut.data[i] as number)} ${num(lut.data[i + 1] as number)} ${num(lut.data[i + 2] as number)}`);
  }
  return `${lines.join('\n')}\n`;
}

type Rgb = [number, number, number];

/** 1D stage: linear on the canonical table (ffmpeg `lut1d interp=linear`), per channel, input clamped to 0..1. */
function sampleOneD(oneD: CubeLutOneD, x: number, c: number): number {
  const t = clamp01(x) * (oneD.size - 1);
  const i0 = Math.floor(t);
  const i1 = Math.min(oneD.size - 1, i0 + 1);
  const v0 = oneD.data[i0 * 3 + c] as number;
  const v1 = oneD.data[i1 * 3 + c] as number;
  return v0 + (v1 - v0) * (t - i0);
}

/** 3D stage: tetrahedral interpolation exactly as ffmpeg's `lut3d interp=tetrahedral`; `data` is size^3*3, R fastest. */
export function sampleTetrahedral(data: ArrayLike<number>, size: number, rgb: Readonly<Rgb>): Rgb {
  const px = clamp01(rgb[0]) * (size - 1);
  const py = clamp01(rgb[1]) * (size - 1);
  const pz = clamp01(rgb[2]) * (size - 1);
  const r0 = Math.floor(px);
  const g0 = Math.floor(py);
  const b0 = Math.floor(pz);
  const r1 = Math.min(size - 1, r0 + 1);
  const g1 = Math.min(size - 1, g0 + 1);
  const b1 = Math.min(size - 1, b0 + 1);
  const dr = px - r0;
  const dg = py - g0;
  const db = pz - b0;
  const at = (r: number, g: number, b: number, c: number): number => data[((b * size + g) * size + r) * 3 + c] as number;
  const out: Rgb = [0, 0, 0];
  for (let c = 0; c < 3; c += 1) {
    const c000 = at(r0, g0, b0, c);
    const c111 = at(r1, g1, b1, c);
    let v: number;
    if (dr > dg) {
      if (dg > db) v = (1 - dr) * c000 + (dr - dg) * at(r1, g0, b0, c) + (dg - db) * at(r1, g1, b0, c) + db * c111;
      else if (dr > db) v = (1 - dr) * c000 + (dr - db) * at(r1, g0, b0, c) + (db - dg) * at(r1, g0, b1, c) + dg * c111;
      else v = (1 - db) * c000 + (db - dr) * at(r0, g0, b1, c) + (dr - dg) * at(r1, g0, b1, c) + dg * c111;
    } else if (db > dg) v = (1 - db) * c000 + (db - dg) * at(r0, g0, b1, c) + (dg - dr) * at(r0, g1, b1, c) + dr * c111;
    else if (db > dr) v = (1 - dg) * c000 + (dg - db) * at(r0, g1, b0, c) + (db - dr) * at(r0, g1, b1, c) + dr * c111;
    else v = (1 - dg) * c000 + (dg - dr) * at(r0, g1, b0, c) + (dr - db) * at(r1, g1, b0, c) + db * c111;
    out[c] = v;
  }
  return out;
}

const bakes = new WeakMap<CubeLut, Map<number, Float32Array>>();

/** Shaper + 3D at intensity < 1: the composite on a 65^3 grid, blended toward identity by k (the engine's single lut3d). */
export function bakedComposite(lut: CubeLut, k: number): Float32Array {
  let perLut = bakes.get(lut);
  if (!perLut) {
    perLut = new Map();
    bakes.set(lut, perLut);
  }
  const hit = perLut.get(k);
  if (hit) return hit;
  const n = CUBE_LUT_BAKE_SIZE;
  const table = new Float32Array(n * n * n * 3);
  const oneD = lut.oneD as CubeLutOneD;
  for (let b = 0; b < n; b += 1) {
    for (let g = 0; g < n; g += 1) {
      for (let r = 0; r < n; r += 1) {
        const grid: Rgb = [r / (n - 1), g / (n - 1), b / (n - 1)];
        const shaped: Rgb = [sampleOneD(oneD, grid[0], 0), sampleOneD(oneD, grid[1], 1), sampleOneD(oneD, grid[2], 2)];
        const out = sampleTetrahedral(lut.data, lut.size, shaped);
        const base = ((b * n + g) * n + r) * 3;
        for (let c = 0; c < 3; c += 1) table[base + c] = (grid[c] as number) + ((out[c] as number) - (grid[c] as number)) * k;
      }
    }
  }
  // Keep the two most recent intensities per LUT (a drag asks for many values; each table is ~3.3 MB).
  while (perLut.size >= 2) perLut.delete(perLut.keys().next().value as number);
  perLut.set(k, table);
  return table;
}

/**
 * CPU reference of what the render applies (the parity target of the ffmpeg fixture). `rgb` is 0..1 and clamped;
 * `intensity` is the grade node's (the viewing LUT has none = 1). The 3D stage reads its input as 0..1 and the table
 * is already normalised, exactly like the rewritten cube the render hands ffmpeg.
 */
export function sampleCubeLut(lut: CubeLut, rgb: readonly [number, number, number], intensity = 1): [number, number, number] {
  const k = clamp01(intensity);
  const x: Rgb = [clamp01(rgb[0]), clamp01(rgb[1]), clamp01(rgb[2])];
  const blend = (out: Rgb): Rgb => [x[0] + (out[0] - x[0]) * k, x[1] + (out[1] - x[1]) * k, x[2] + (out[2] - x[2]) * k];
  const kind = lut.kind ?? '3d';
  if (kind === '3d') return blend(sampleTetrahedral(lut.data, lut.size, x));
  const oneD = lut.oneD as CubeLutOneD;
  if (kind === '1d') return blend([sampleOneD(oneD, x[0], 0), sampleOneD(oneD, x[1], 1), sampleOneD(oneD, x[2], 2)]);
  if (k >= 1) return sampleTetrahedral(lut.data, lut.size, [sampleOneD(oneD, x[0], 0), sampleOneD(oneD, x[1], 1), sampleOneD(oneD, x[2], 2)]);
  return sampleTetrahedral(bakedComposite(lut, k), CUBE_LUT_BAKE_SIZE, x);
}
