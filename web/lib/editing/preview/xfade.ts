/**
 * T-008b: the export's `xfade` transitions for the live viewer. A port of ffmpeg's libavfilter/vf_xfade.c (the 8-bit
 * planar kernels): {@link xfadePixel} is the per-pixel reference (pure, tested against real ffmpeg frames), and
 * {@link XFADE_FRAGMENT_SHADER} is the same maths on the GPU. Pixel coordinates are ffmpeg's: x right, y DOWN from 0,
 * integers; `progress` is the KERNEL's progress, which is `1 - t` (t = 0 at the first frame of the transition, 1 at the end).
 */

export type XfadeParity = 'exact' | 'approximate';

/** The words, in shader index order (`uType`). Mirrors `TRANSITION_TYPES` in engine/ops.ts. */
export const XFADE_WORDS: readonly string[] = [
  'fade', 'dissolve', 'fadeblack', 'fadewhite', 'fadegrays', 'distance', 'pixelize',
  'wipeleft', 'wiperight', 'wipeup', 'wipedown', 'wipetl', 'wipetr', 'wipebl', 'wipebr',
  'slideleft', 'slideright', 'slideup', 'slidedown',
  'smoothleft', 'smoothright', 'smoothup', 'smoothdown',
  'circlecrop', 'rectcrop', 'circleopen', 'circleclose',
  'vertopen', 'vertclose', 'horzopen', 'horzclose',
  'diagtl', 'diagtr', 'diagbl', 'diagbr',
  'hlslice', 'hrslice', 'vuslice', 'vdslice',
  'radial', 'zoomin', 'squeezev', 'squeezeh', 'hblur',
];

/**
 * How close the viewer is to the export, per word. `exact`: the same kernel as ffmpeg on RGB (identical up to 8-bit rounding
 * and the preview's resolution). `approximate`: same idea, but the export runs it in YUV or the GPU cannot reproduce the
 * arithmetic (see the note per word).
 */
export const XFADE_PARITY: Readonly<Record<string, { parity: XfadeParity; note?: string }>> = {
  dissolve: { parity: 'approximate', note: 'The grain pattern comes from sin() on huge arguments; GPU sin differs from the CPU, so the speckle differs pixel by pixel.' },
  distance: { parity: 'approximate', note: 'The export measures colour distance in YUV planes; the viewer uses RGB.' },
  fadegrays: { parity: 'approximate', note: 'The export greys by luma in YUV; the viewer averages RGB.' },
  hblur: { parity: 'approximate', note: 'The export averages the whole window; the viewer takes at most 64 evenly spaced taps (identical while the window is 64 pixels or less).' },
};

export const xfadeParityOf = (word: string): XfadeParity => XFADE_PARITY[word]?.parity ?? 'exact';
export const xfadeIndexOf = (word: string): number => XFADE_WORDS.indexOf(word);

export type Rgb = readonly [number, number, number];
export type Sampler = (x: number, y: number) => Rgb;

const f = Math.fround;
const clamp01 = (v: number) => (v < 0 ? 0 : v > 1 ? 1 : v);
/** ffmpeg's `mix(a, b, m)` = `a*m + b*(1-m)`. */
const mix = (a: number, b: number, m: number) => a * m + b * (1 - m);
const smoothstep = (e0: number, e1: number, x: number) => {
  const t = clamp01((x - e0) / (e1 - e0));
  return t * t * (3 - 2 * t);
};
const fract = (a: number) => a - Math.floor(a);
const trunc = Math.trunc;
/** C's `lrintf` under the default rounding mode: round half to even. */
const lrint = (v: number) => {
  const r = Math.round(v);
  return Math.abs(v % 1) === 0.5 && r % 2 !== 0 ? r - 1 : r;
};
/** C's `%` (the sign of the dividend). */
const cmod = (a: number, b: number) => a % b;

/** ffmpeg's `frand`, with float32 rounding at each step. */
export function frand(x: number, y: number): number {
  const r = f(f(Math.sin(f(f(x * f(12.9898)) + f(y * f(78.233))))) * f(43758.545));
  return r - Math.floor(r);
}

const per = (a: Rgb, b: Rgb, fn: (av: number, bv: number, c: number) => number): Rgb => [fn(a[0], b[0], 0), fn(a[1], b[1], 1), fn(a[2], b[2], 2)];

/**
 * One output pixel of transition `word` at kernel progress `progress` (`1 - t`), for a `w` x `h` frame, as float RGB in 0..255
 * (ffmpeg stores it truncated). `a` is the outgoing picture, `b` the incoming one. Unknown words return the incoming picture.
 */
export function xfadePixel(word: string, progress: number, x: number, y: number, w: number, h: number, a: Sampler, b: Sampler): Rgb {
  const P = progress;
  const A = a(x, y);
  const B = b(x, y);
  const hw = trunc(w / 2);
  const hh = trunc(h / 2);
  const pick = (cond: boolean): Rgb => (cond ? B : A);
  const smoothMix = (s: number): Rgb => {
    const m = smoothstep(0, 1, s);
    return per(B, A, (bv, av) => mix(bv, av, m));
  };
  switch (word) {
    case 'fade':
      return per(A, B, (av, bv) => mix(av, bv, P));
    case 'wipeleft':
      return pick(x > trunc(w * P));
    case 'wiperight':
      return pick(!(x > trunc(w * (1 - P))));
    case 'wipeup':
      return pick(y > trunc(h * P));
    case 'wipedown':
      return pick(!(y > trunc(h * (1 - P))));
    case 'wipetl':
      return pick(!(y <= trunc(h * P) && x <= trunc(w * P)));
    case 'wipetr':
      return pick(!(y <= trunc(h * P) && x > trunc(w * (1 - P))));
    case 'wipebl':
      return pick(!(y > trunc(h * (1 - P)) && x <= trunc(w * P)));
    case 'wipebr':
      return pick(!(y > trunc(h * (1 - P)) && x > trunc(w * (1 - P))));
    case 'slideleft':
    case 'slideright': {
      const z = word === 'slideleft' ? trunc(-P * w) : trunc(P * w);
      const zx = z + x;
      const zz = cmod(zx, w) + w * (cmod(zx, w) < 0 ? 1 : 0);
      return zx >= 0 && zx < w ? b(zz, y) : a(zz, y);
    }
    case 'slideup':
    case 'slidedown': {
      const z = word === 'slideup' ? trunc(-P * h) : trunc(P * h);
      const zy = z + y;
      const zz = cmod(zy, h) + h * (cmod(zy, h) < 0 ? 1 : 0);
      return zy >= 0 && zy < h ? b(x, zz) : a(x, zz);
    }
    case 'circlecrop': {
      const z = f(Math.pow(2 * Math.abs(P - 0.5), 3) * Math.hypot(hw, hh));
      const dist = Math.hypot(x - hw, y - hh);
      return z < dist ? [0, 0, 0] : P < 0.5 ? B : A;
    }
    case 'rectcrop': {
      const zh = trunc(Math.abs(P - 0.5) * h);
      const zw = trunc(Math.abs(P - 0.5) * w);
      const inside = Math.abs(x - hw) < zw && Math.abs(y - hh) < zh;
      return !inside ? [0, 0, 0] : P < 0.5 ? B : A;
    }
    case 'distance': {
      let d = 0;
      for (let c = 0; c < 3; c += 1) d += (A[c] / 255 - B[c] / 255) * (A[c] / 255 - B[c] / 255);
      const near = Math.sqrt(d) <= P ? 1 : 0;
      return per(A, B, (av, bv) => mix(mix(av, bv, near), bv, P));
    }
    case 'fadeblack':
    case 'fadewhite': {
      const bg = word === 'fadeblack' ? 0 : 255;
      return per(A, B, (av, bv) => mix(mix(av, bg, smoothstep(0.8, 1, P)), mix(bg, bv, smoothstep(0.2, 1, P)), P));
    }
    case 'fadegrays': {
      const ga = trunc((A[0] + A[1] + A[2]) / 3);
      const gb = trunc((B[0] + B[1] + B[2]) / 3);
      return per(A, B, (av, bv) => mix(mix(av, ga, smoothstep(0.8, 1, P)), mix(gb, bv, smoothstep(0.2, 1, P)), P));
    }
    case 'radial':
      return smoothMix(Math.atan2(x - hw, y - hh) - (P - 0.5) * (Math.PI * 2.5));
    case 'smoothleft':
      return smoothMix(1 + x / w - P * 2);
    case 'smoothright':
      return smoothMix(1 + (w - 1 - x) / w - P * 2);
    case 'smoothup':
      return smoothMix(1 + y / h - P * 2);
    case 'smoothdown':
      return smoothMix(1 + (h - 1 - y) / h - P * 2);
    case 'circleopen': {
      const z = Math.hypot(hw, hh);
      const s = Math.hypot(x - hw, y - hh) / z + (P - 0.5) * 3;
      const m = smoothstep(0, 1, s);
      return per(A, B, (av, bv) => mix(av, bv, m));
    }
    case 'circleclose': {
      const z = Math.hypot(hw, hh);
      return smoothMix(Math.hypot(x - hw, y - hh) / z + (1 - P - 0.5) * 3);
    }
    case 'vertopen': {
      const w2 = w / 2;
      return smoothMix(2 - Math.abs((x - w2) / w2) - P * 2);
    }
    case 'vertclose': {
      const w2 = w / 2;
      return smoothMix(1 + Math.abs((x - w2) / w2) - P * 2);
    }
    case 'horzopen': {
      const h2 = h / 2;
      return smoothMix(2 - Math.abs((y - h2) / h2) - P * 2);
    }
    case 'horzclose': {
      const h2 = h / 2;
      return smoothMix(1 + Math.abs((y - h2) / h2) - P * 2);
    }
    case 'dissolve': {
      const s = frand(x, y) * 2 + P * 2 - 1.5;
      return s >= 0.5 ? A : B;
    }
    case 'pixelize': {
      const d = Math.min(P, 1 - P);
      const dist = Math.ceil(d * 50) / 50;
      // float32 steps: floor(x / sq) is sensitive to the last bit (x / 0.6f is just under an integer at some x).
      const sq = f(f(f(2 * dist) * Math.min(w, h)) / 20);
      const sx = dist > 0 ? trunc(Math.min(f(f(Math.floor(f(x / sq)) + 0.5) * sq), w - 1)) : x;
      const sy = dist > 0 ? trunc(Math.min(f(f(Math.floor(f(y / sq)) + 0.5) * sq), h - 1)) : y;
      const pa = a(sx, sy);
      const pb = b(sx, sy);
      return per(pa, pb, (av, bv) => mix(av, bv, P));
    }
    case 'diagtl':
      return smoothMix(1 + ((x / w) * y) / h - P * 2);
    case 'diagtr':
      return smoothMix(1 + (((w - 1 - x) / w) * y) / h - P * 2);
    case 'diagbl':
      return smoothMix(1 + ((x / w) * (h - 1 - y)) / h - P * 2);
    case 'diagbr':
      return smoothMix(1 + (((w - 1 - x) / w) * (h - 1 - y)) / h - P * 2);
    case 'hlslice':
    case 'hrslice':
    case 'vuslice':
    case 'vdslice': {
      const v = word === 'hlslice' ? x / w : word === 'hrslice' ? (w - 1 - x) / w : word === 'vuslice' ? y / h : (h - 1 - y) / h;
      const s = smoothstep(-0.5, 0, v - P * 1.5);
      const ss = s <= fract(10 * v) ? 0 : 1;
      return per(B, A, (bv, av) => mix(bv, av, ss));
    }
    case 'zoomin': {
      const zf = smoothstep(0.5, 1, P);
      const u = 0.5 + (x / w - 0.5) * zf;
      const v = 0.5 + (y / h - 0.5) * zf;
      const zv = a(Math.ceil(u * (w - 1)), Math.ceil(v * (h - 1)));
      const m = smoothstep(0, 0.5, P);
      return per(zv, B, (av, bv) => mix(av, bv, m));
    }
    case 'squeezev': {
      const z = P > 0 ? f(0.5 + f(f(f(x / w) - 0.5) / P)) : -1;
      return z < 0 || z > 1 ? B : a(lrint(f(z * (w - 1))), y);
    }
    case 'squeezeh': {
      const z = P > 0 ? f(0.5 + f(f(f(y / h) - 0.5) / P)) : -1;
      return z < 0 || z > 1 ? B : a(x, lrint(f(z * (h - 1))));
    }
    case 'hblur': {
      const prog = P <= 0.5 ? P * 2 : (1 - P) * 2;
      const size = trunc(1 + hw * prog);
      const n = Math.min(size, w - x);
      const sa = [0, 0, 0];
      const sb = [0, 0, 0];
      for (let i = 0; i < n; i += 1) {
        const pa = a(x + i, y);
        const pb = b(x + i, y);
        for (let c = 0; c < 3; c += 1) {
          sa[c] += pa[c];
          sb[c] += pb[c];
        }
      }
      return [mix(sa[0] / n, sb[0] / n, P), mix(sa[1] / n, sb[1] / n, P), mix(sa[2] / n, sb[2] / n, P)];
    }
    default:
      return B;
  }
}

/**
 * The same kernels in GLSL ES 3.00. Inputs: the two full-canvas pictures (`uA` outgoing, `uB` incoming) as textures of the
 * canvas size, the kernel progress `uProgress` (= 1 - t), the word `uType` (index in {@link XFADE_WORDS}) and the size.
 * Reads are by texel (`texelFetch`), flipped so y counts down from the top as in ffmpeg. Output is opaque.
 */
export const XFADE_FRAGMENT_SHADER = `#version 300 es
precision highp float;
precision highp int;
in vec2 vUv;
uniform sampler2D uA;
uniform sampler2D uB;
uniform float uProgress;
uniform int uType;
uniform ivec2 uSize;
out vec4 outColor;

int W;
int H;
vec3 fa(int x, int y) { return texelFetch(uA, ivec2(clamp(x, 0, W - 1), H - 1 - clamp(y, 0, H - 1)), 0).rgb; }
vec3 fb(int x, int y) { return texelFetch(uB, ivec2(clamp(x, 0, W - 1), H - 1 - clamp(y, 0, H - 1)), 0).rgb; }
// ffmpeg: mix(a, b, m) = a*m + b*(1-m)
vec3 fmix(vec3 a, vec3 b, float m) { return a * m + b * (1.0 - m); }
float fsmooth(float e0, float e1, float x) { float t = clamp((x - e0) / (e1 - e0), 0.0, 1.0); return t * t * (3.0 - 2.0 * t); }
int cmod(int a, int b) { return a - b * (a / b); }
float frand(int x, int y) { float r = sin(float(x) * 12.9898 + float(y) * 78.233) * 43758.545; return r - floor(r); }
vec3 smoothMix(float s, vec3 a, vec3 b) { return fmix(b, a, fsmooth(0.0, 1.0, s)); }

void main() {
  W = uSize.x;
  H = uSize.y;
  int x = int(gl_FragCoord.x);
  int y = H - 1 - int(gl_FragCoord.y);
  float P = uProgress;
  float w = float(W);
  float h = float(H);
  float fx = float(x);
  float fy = float(y);
  int hw = W / 2;
  int hh = H / 2;
  vec3 A = fa(x, y);
  vec3 B = fb(x, y);
  vec3 o = B;
  int t = uType;
  if (t == 0) o = fmix(A, B, P);
  else if (t == 1) { float s = frand(x, y) * 2.0 + P * 2.0 - 1.5; o = s >= 0.5 ? A : B; }
  else if (t == 2 || t == 3) {
    vec3 bg = t == 2 ? vec3(0.0) : vec3(1.0);
    o = fmix(fmix(A, bg, fsmooth(0.8, 1.0, P)), fmix(bg, B, fsmooth(0.2, 1.0, P)), P);
  }
  else if (t == 4) {
    vec3 a8 = floor(A * 255.0 + 0.5);
    vec3 b8 = floor(B * 255.0 + 0.5);
    vec3 ga = vec3(floor((a8.r + a8.g + a8.b) / 3.0) / 255.0);
    vec3 gb = vec3(floor((b8.r + b8.g + b8.b) / 3.0) / 255.0);
    o = fmix(fmix(A, ga, fsmooth(0.8, 1.0, P)), fmix(gb, B, fsmooth(0.2, 1.0, P)), P);
  }
  else if (t == 5) { vec3 d = A - B; float dist = sqrt(dot(d, d)); float near = dist <= P ? 1.0 : 0.0; o = fmix(fmix(A, B, near), B, P); }
  else if (t == 6) {
    float d = min(P, 1.0 - P);
    float dist = ceil(d * 50.0) / 50.0;
    float sq = 2.0 * dist * min(w, h) / 20.0;
    int sx = x;
    int sy = y;
    if (dist > 0.0) { sx = int(min((floor(fx / sq) + 0.5) * sq, w - 1.0)); sy = int(min((floor(fy / sq) + 0.5) * sq, h - 1.0)); }
    o = fmix(fa(sx, sy), fb(sx, sy), P);
  }
  else if (t == 7) o = x > int(w * P) ? B : A;
  else if (t == 8) o = x > int(w * (1.0 - P)) ? A : B;
  else if (t == 9) o = y > int(h * P) ? B : A;
  else if (t == 10) o = y > int(h * (1.0 - P)) ? A : B;
  else if (t == 11) o = (y <= int(h * P) && x <= int(w * P)) ? A : B;
  else if (t == 12) o = (y <= int(h * P) && x > int(w * (1.0 - P))) ? A : B;
  else if (t == 13) o = (y > int(h * (1.0 - P)) && x <= int(w * P)) ? A : B;
  else if (t == 14) o = (y > int(h * (1.0 - P)) && x > int(w * (1.0 - P))) ? A : B;
  else if (t == 15 || t == 16) {
    int z = t == 15 ? int(-P * w) : int(P * w);
    int zx = z + x;
    int zz = cmod(zx, W) + W * (cmod(zx, W) < 0 ? 1 : 0);
    o = (zx >= 0 && zx < W) ? fb(zz, y) : fa(zz, y);
  }
  else if (t == 17 || t == 18) {
    int z = t == 17 ? int(-P * h) : int(P * h);
    int zy = z + y;
    int zz = cmod(zy, H) + H * (cmod(zy, H) < 0 ? 1 : 0);
    o = (zy >= 0 && zy < H) ? fb(x, zz) : fa(x, zz);
  }
  else if (t == 19) o = smoothMix(1.0 + fx / w - P * 2.0, A, B);
  else if (t == 20) o = smoothMix(1.0 + (w - 1.0 - fx) / w - P * 2.0, A, B);
  else if (t == 21) o = smoothMix(1.0 + fy / h - P * 2.0, A, B);
  else if (t == 22) o = smoothMix(1.0 + (h - 1.0 - fy) / h - P * 2.0, A, B);
  else if (t == 23) {
    float z = pow(2.0 * abs(P - 0.5), 3.0) * length(vec2(float(hw), float(hh)));
    float dist = length(vec2(float(x - hw), float(y - hh)));
    o = z < dist ? vec3(0.0) : (P < 0.5 ? B : A);
  }
  else if (t == 24) {
    int zh = int(abs(P - 0.5) * h);
    int zw = int(abs(P - 0.5) * w);
    bool inside = abs(x - hw) < zw && abs(y - hh) < zh;
    o = !inside ? vec3(0.0) : (P < 0.5 ? B : A);
  }
  else if (t == 25) { float z = length(vec2(float(hw), float(hh))); float s = length(vec2(float(x - hw), float(y - hh))) / z + (P - 0.5) * 3.0; o = fmix(A, B, fsmooth(0.0, 1.0, s)); }
  else if (t == 26) { float z = length(vec2(float(hw), float(hh))); o = smoothMix(length(vec2(float(x - hw), float(y - hh))) / z + (1.0 - P - 0.5) * 3.0, A, B); }
  else if (t == 27) { float w2 = w / 2.0; o = smoothMix(2.0 - abs((fx - w2) / w2) - P * 2.0, A, B); }
  else if (t == 28) { float w2 = w / 2.0; o = smoothMix(1.0 + abs((fx - w2) / w2) - P * 2.0, A, B); }
  else if (t == 29) { float h2 = h / 2.0; o = smoothMix(2.0 - abs((fy - h2) / h2) - P * 2.0, A, B); }
  else if (t == 30) { float h2 = h / 2.0; o = smoothMix(1.0 + abs((fy - h2) / h2) - P * 2.0, A, B); }
  else if (t == 31) o = smoothMix(1.0 + fx / w * fy / h - P * 2.0, A, B);
  else if (t == 32) o = smoothMix(1.0 + (w - 1.0 - fx) / w * fy / h - P * 2.0, A, B);
  else if (t == 33) o = smoothMix(1.0 + fx / w * (h - 1.0 - fy) / h - P * 2.0, A, B);
  else if (t == 34) o = smoothMix(1.0 + (w - 1.0 - fx) / w * (h - 1.0 - fy) / h - P * 2.0, A, B);
  else if (t >= 35 && t <= 38) {
    float v = t == 35 ? fx / w : t == 36 ? (w - 1.0 - fx) / w : t == 37 ? fy / h : (h - 1.0 - fy) / h;
    float s = fsmooth(-0.5, 0.0, v - P * 1.5);
    float ss = s <= fract(10.0 * v) ? 0.0 : 1.0;
    o = fmix(B, A, ss);
  }
  else if (t == 39) o = smoothMix(atan(fx - float(hw), fy - float(hh)) - (P - 0.5) * (3.14159265358979 * 2.5), A, B);
  else if (t == 40) {
    float zf = fsmooth(0.5, 1.0, P);
    float u = 0.5 + (fx / w - 0.5) * zf;
    float v = 0.5 + (fy / h - 0.5) * zf;
    vec3 zv = fa(int(ceil(u * (w - 1.0))), int(ceil(v * (h - 1.0))));
    o = fmix(zv, B, fsmooth(0.0, 0.5, P));
  }
  else if (t == 41) {
    float z = P > 0.0 ? 0.5 + (fx / w - 0.5) / P : -1.0;
    o = (z < 0.0 || z > 1.0) ? B : fa(int(roundEven(z * (w - 1.0))), y);
  }
  else if (t == 42) {
    float z = P > 0.0 ? 0.5 + (fy / h - 0.5) / P : -1.0;
    o = (z < 0.0 || z > 1.0) ? B : fa(x, int(roundEven(z * (h - 1.0))));
  }
  else if (t == 43) {
    float prog = P <= 0.5 ? P * 2.0 : (1.0 - P) * 2.0;
    int size = int(1.0 + float(hw) * prog);
    int n = min(size, W - x);
    vec3 sa = vec3(0.0);
    vec3 sb = vec3(0.0);
    int taps = min(n, 64);
    for (int i = 0; i < 64; i++) {
      if (i >= taps) break;
      int dx = n <= 64 ? i : int(float(i) * float(n) / float(taps));
      sa += fa(x + dx, y);
      sb += fb(x + dx, y);
    }
    float c = float(taps);
    o = fmix(sa / c, sb / c, P);
  }
  outColor = vec4(o, 1.0);
}`;
