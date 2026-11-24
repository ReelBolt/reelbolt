/**
 * Browser-side harness for the real-GL smoke test of the compositor's camera-profile input
 * stage (`gl-compositor.ts`) and the viewing-LUT final pass (`viewer-color.ts`).
 *
 * This file is TEST CODE: it is bundled by `build-bundle.mjs` into a temp dir and injected into
 * a blank Playwright page. It only imports product modules, never changes them.
 *
 * Every RGBA8 readback goes through `compositor.withCleanPicture(() => gl.readPixels(...))`, i.e.
 * the same entry point stills/scopes use, so the viewing LUT is off for the measurement unless a
 * check is explicitly measuring the viewed picture.
 */

import {
  createGlCompositor,
  VERTEX_SHADER,
  FRAGMENT_SHADER,
  VIEW_FRAGMENT_SHADER,
  FX_VERTEX_SHADER,
  FX_FRAGMENT_SHADER,
  type Compositor,
  type CompositorLayer,
} from '../../../web/lib/editing/preview/gl-compositor';
import { getCameraProfile, toRec709 } from '../../../web/lib/editing/color/camera-profiles';
import { sampleTableTrilinear, viewingLutFromCube } from '../../../web/lib/editing/color/viewer-color';
import { generateCameraProfileTable } from '../../../web/lib/editing/color/camera-profile-table';
import { parseCubeLut } from '../../../web/lib/editing/color/cube-lut';
import { applyPreviewGradeNodes, previewGradeNodesOf } from '../../../web/lib/editing/color/preview-grade';
import type { GradeStack } from '../../../web/lib/editing/types';
import { createLivePreviewSession } from '../../../web/lib/editing/preview/live-session';
import { doc, media, title, track } from '../../../web/lib/editing/__tests__/fixtures';

const CANVAS_W = 512;
const CANVAS_H = 128;
const RAMP_X = 0;
const RAMP_Y = 0;
const RAMP_W = 256;
const RAMP_H = 16;
const PATCH_X = 288;
const PATCH_Y = 0;
const PATCH_SIZE = 16;
const PATCH_COLS = 6;

/** 24 patch code values as 8-bit triples (the source is an 8-bit canvas, so codes are exact bytes). */
const PATCH_CODES: ReadonlyArray<readonly [number, number, number]> = [
  [0, 0, 0], [8, 8, 8], [16, 16, 16],
  [32, 32, 32], [64, 64, 64], [96, 96, 96],
  [128, 128, 128], [160, 160, 160], [192, 192, 192],
  [224, 224, 224], [255, 255, 255], [255, 0, 0],
  [0, 255, 0], [0, 0, 255], [255, 128, 0],
  [0, 200, 255], [200, 0, 255], [128, 255, 0],
  [255, 255, 0], [0, 255, 255], [255, 0, 255],
  [30, 90, 150], [150, 90, 30], [90, 150, 30],
];

type RGB = [number, number, number];

interface SamplePoint {
  x: number;
  y: number;
  rgb: RGB;
  kind: 'ramp' | 'patch';
}

interface SourceFixture {
  canvas: HTMLCanvasElement;
  points: SamplePoint[];
}

/** A 256x16 grey ramp plus 24 8-bit patches, all on black, on one 512x128 canvas. */
function makeSource(): SourceFixture {
  const canvas = document.createElement('canvas');
  canvas.width = CANVAS_W;
  canvas.height = CANVAS_H;
  const ctx = canvas.getContext('2d');
  if (!ctx) throw new Error('2d context unavailable');
  ctx.fillStyle = '#000000';
  ctx.fillRect(0, 0, CANVAS_W, CANVAS_H);

  const ramp = ctx.createImageData(RAMP_W, RAMP_H);
  for (let y = 0; y < RAMP_H; y += 1) {
    for (let x = 0; x < RAMP_W; x += 1) {
      const v = Math.round((255 * x) / (RAMP_W - 1));
      const i = (y * RAMP_W + x) * 4;
      ramp.data[i] = v;
      ramp.data[i + 1] = v;
      ramp.data[i + 2] = v;
      ramp.data[i + 3] = 255;
    }
  }
  ctx.putImageData(ramp, RAMP_X, RAMP_Y);

  PATCH_CODES.forEach((code, k) => {
    const col = k % PATCH_COLS;
    const row = Math.floor(k / PATCH_COLS);
    ctx.fillStyle = `rgb(${code[0]}, ${code[1]}, ${code[2]})`;
    ctx.fillRect(PATCH_X + col * PATCH_SIZE, PATCH_Y + row * PATCH_SIZE, PATCH_SIZE, PATCH_SIZE);
  });

  const points: SamplePoint[] = [];
  for (let x = 0; x < RAMP_W; x += 1) {
    const code = Math.round((255 * x) / (RAMP_W - 1)) / 255;
    points.push({ x: RAMP_X + x, y: RAMP_Y + Math.floor(RAMP_H / 2), rgb: [code, code, code], kind: 'ramp' });
  }
  PATCH_CODES.forEach((code, k) => {
    const col = k % PATCH_COLS;
    const row = Math.floor(k / PATCH_COLS);
    points.push({
      x: PATCH_X + col * PATCH_SIZE + Math.floor(PATCH_SIZE / 2),
      y: PATCH_Y + row * PATCH_SIZE + Math.floor(PATCH_SIZE / 2),
      rgb: [code[0] / 255, code[1] / 255, code[2] / 255],
      kind: 'patch',
    });
  });
  return { canvas, points };
}

interface CompositorHandle {
  canvas: HTMLCanvasElement;
  compositor: Compositor;
  gl: WebGL2RenderingContext;
  state: { lost: number; restored: number; restoreOk: boolean | null };
  waitRestored: () => Promise<void>;
}

function createCompositor(width = CANVAS_W, height = CANVAS_H): CompositorHandle {
  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;
  canvas.style.position = 'absolute';
  canvas.style.left = '0';
  canvas.style.top = '0';
  document.body.appendChild(canvas);
  const state = { lost: 0, restored: 0, restoreOk: null as boolean | null };
  let onRestored: (() => void) | null = null;
  const compositor = createGlCompositor(canvas, {
    onContextLost: () => {
      state.lost += 1;
    },
    onContextRestored: (ok) => {
      state.restored += 1;
      state.restoreOk = ok;
      onRestored?.();
    },
  });
  if (!compositor) throw new Error('createGlCompositor returned null (no WebGL2?)');
  const gl = canvas.getContext('webgl2');
  if (!gl) throw new Error('webgl2 context unavailable after compositor creation');
  const waitRestored = () =>
    new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('context restore timed out after 10s')), 10_000);
      onRestored = () => {
        clearTimeout(timer);
        resolve();
      };
    });
  return { canvas, compositor, gl, state, waitRestored };
}

function readPixelsNow(gl: WebGL2RenderingContext, width = CANVAS_W, height = CANVAS_H): Uint8Array {
  const bytes = new Uint8Array(width * height * 4);
  gl.readPixels(0, 0, width, height, gl.RGBA, gl.UNSIGNED_BYTE, bytes);
  return bytes;
}

/** readPixels rows run bottom-up; the source canvases are top-down. */
function pixelOffset(x: number, y: number, width = CANVAS_W): number {
  return ((CANVAS_H - 1 - y) * width + x) * 4;
}

function maxAbsDiff(a: Uint8Array, b: Uint8Array): number {
  let max = 0;
  for (let i = 0; i < a.length; i += 1) {
    const d = Math.abs((a[i] as number) - (b[i] as number));
    if (d > max) max = d;
  }
  return max;
}

interface ProgramReport {
  label: string;
  vertexCompiled: boolean;
  fragmentCompiled: boolean;
  linked: boolean;
  vertexLog: string;
  fragmentLog: string;
  linkLog: string;
}

function compileAndLink(gl: WebGL2RenderingContext, label: string, vsSource: string, fsSource: string): ProgramReport {
  const vs = gl.createShader(gl.VERTEX_SHADER);
  const fs = gl.createShader(gl.FRAGMENT_SHADER);
  let vertexCompiled = false;
  let fragmentCompiled = false;
  let linked = false;
  let vertexLog = '';
  let fragmentLog = '';
  let linkLog = '';
  if (vs && fs) {
    gl.shaderSource(vs, vsSource);
    gl.compileShader(vs);
    vertexCompiled = gl.getShaderParameter(vs, gl.COMPILE_STATUS) === true;
    vertexLog = gl.getShaderInfoLog(vs) ?? '';
    gl.shaderSource(fs, fsSource);
    gl.compileShader(fs);
    fragmentCompiled = gl.getShaderParameter(fs, gl.COMPILE_STATUS) === true;
    fragmentLog = gl.getShaderInfoLog(fs) ?? '';
    if (vertexCompiled && fragmentCompiled) {
      const prog = gl.createProgram();
      if (prog) {
        gl.bindAttribLocation(prog, 0, 'aPos');
        gl.bindAttribLocation(prog, 1, 'aUv');
        gl.attachShader(prog, vs);
        gl.attachShader(prog, fs);
        gl.linkProgram(prog);
        linked = gl.getProgramParameter(prog, gl.LINK_STATUS) === true;
        linkLog = gl.getProgramInfoLog(prog) ?? '';
        gl.deleteProgram(prog);
      }
    }
    gl.deleteShader(vs);
    gl.deleteShader(fs);
  }
  return { label, vertexCompiled, fragmentCompiled, linked, vertexLog, fragmentLog, linkLog };
}

function rendererString(gl: WebGL2RenderingContext): string {
  const info = gl.getExtension('WEBGL_debug_renderer_info');
  if (info) {
    const unmasked = gl.getParameter(info.UNMASKED_RENDERER_WEBGL);
    if (unmasked) return String(unmasked);
  }
  return String(gl.getParameter(gl.RENDERER));
}

/** Assertion 1: every compositor program compiles and links in a real WebGL2 context. */
function shaderReport(): Record<string, unknown> {
  const canvas = document.createElement('canvas');
  canvas.width = 8;
  canvas.height = 8;
  const gl = canvas.getContext('webgl2');
  if (!gl) return { available: false, renderer: null, programs: [] };
  const renderer = rendererString(gl);
  const programs = [
    compileAndLink(gl, 'main (layers+grade+input profile)', VERTEX_SHADER, FRAGMENT_SHADER),
    compileAndLink(gl, 'viewing (final LUT pass)', VERTEX_SHADER, VIEW_FRAGMENT_SHADER),
    compileAndLink(gl, 'fx (blur/pixelate/mono/vignette)', FX_VERTEX_SHADER, FX_FRAGMENT_SHADER),
  ];
  const glError = gl.getError();

  // In situ: the compositor itself builds its main program with its own context attributes, with
  // the input stage off and on; a link/compile failure would return null or set a GL error.
  const fixture = makeSource();
  const live = createCompositor();
  const created = live.compositor !== null;
  const liveRenderer = live.compositor.renderer;
  live.compositor.draw([{ layerId: 'src', image: fixture.canvas, opacity: 1 }]);
  const stageOffError = live.gl.getError();
  live.compositor.draw([{ layerId: 'src', image: fixture.canvas, opacity: 1, inputProfile: 'sonySLog3' }]);
  const stageOnError = live.gl.getError();
  live.compositor.dispose();
  live.canvas.remove();

  // Latent defect probe: the stage-off path never initialises `uInputLut` (a sampler3D), so it
  // defaults to texture unit 0, where the layer's 2D texture is bound -> draw-time
  // INVALID_OPERATION. Pointing that sampler at unit 1 (as the stage-on path does) should clear it.
  const probe = probeInputSampler();

  gl.getExtension('WEBGL_lose_context')?.loseContext();
  return {
    available: true,
    renderer,
    programs,
    glError,
    created,
    liveRenderer,
    stageOffError,
    stageOnError,
    samplerMismatchProbe: probe,
  };
}

/** Confirms the stage-off GL error is the uninitialised `uInputLut` sampler3D (test-only probe). */
function probeInputSampler(): Record<string, unknown> {
  const fixture = makeSource();
  const live = createCompositor();
  const gl = live.gl;
  const layer: CompositorLayer = { layerId: 'src', image: fixture.canvas, opacity: 1 };
  live.compositor.draw([layer]);
  const firstStageOffError = gl.getError();
  const firstPicture = live.compositor.withCleanPicture(() => readPixelsNow(gl));
  const program = gl.getParameter(gl.CURRENT_PROGRAM) as WebGLProgram | null;
  const location = program ? gl.getUniformLocation(program, 'uInputLut') : null;
  if (location) gl.uniform1i(location, 1);
  live.compositor.draw([layer]);
  const secondStageOffError = gl.getError();
  const secondPicture = live.compositor.withCleanPicture(() => readPixelsNow(gl));
  const sampler3DUnit = location ? (gl.getUniform(program as WebGLProgram, location) as number) : null;
  const first = pictureStats(firstPicture, fixture.canvas);
  const second = pictureStats(secondPicture, fixture.canvas);
  live.compositor.dispose();
  live.canvas.remove();
  return {
    firstStageOffError,
    secondStageOffErrorWithSampler3DAtUnit1: secondStageOffError,
    sampler3DUnit,
    firstStageOff: first,
    afterSampler3DBound: second,
  };
}

/** Mean luma of the readback and its max per-channel difference from the source canvas. */
function pictureStats(bytes: Uint8Array, source: HTMLCanvasElement): Record<string, number> {
  const ctx = source.getContext('2d');
  if (!ctx) throw new Error('2d context unavailable');
  const src = ctx.getImageData(0, 0, CANVAS_W, CANVAS_H).data;
  let sum = 0;
  let maxDiff = 0;
  let identicalOrNear = 0;
  let total = 0;
  for (let y = 0; y < CANVAS_H; y += 1) {
    for (let x = 0; x < CANVAS_W; x += 1) {
      const off = pixelOffset(x, y);
      const soff = (y * CANVAS_W + x) * 4;
      sum += bytes[off] as number;
      let pixelMax = 0;
      for (let c = 0; c < 3; c += 1) {
        const d = Math.abs((bytes[off + c] as number) - (src[soff + c] as number));
        if (d > pixelMax) pixelMax = d;
      }
      if (pixelMax > maxDiff) maxDiff = pixelMax;
      if (pixelMax <= 1) identicalOrNear += 1;
      total += 1;
    }
  }
  return { meanRed: sum / total, maxDiffFromSource: maxDiff, withinOneFraction: identicalOrNear / total };
}

interface WorstSample {
  kind: 'ramp' | 'patch';
  x: number;
  y: number;
  channel: number;
  error: number;
  out: number;
  expected: number;
  code: RGB;
}

/** Assertion 2: the input-profile stage equals `toRec709` (TS mirror) at ramp + patch points. */
function runProfile(profileId: string): Record<string, unknown> {
  const fixture = makeSource();
  const live = createCompositor();
  const layer: CompositorLayer = { layerId: 'src', image: fixture.canvas, opacity: 1, inputProfile: profileId };
  const result = live.compositor.draw([layer]);
  const bytes = live.compositor.withCleanPicture(() => readPixelsNow(live.gl));
  const glError = live.gl.getError();
  const def = getCameraProfile(profileId as never);
  const table = generateCameraProfileTable(profileId);
  const m = def.gamutToRec709;
  let maxError = 0; // GL vs analytic, in-gamut points only
  let sumError = 0;
  let samples = 0;
  let samplesOverTolerance = 0;
  let maxCpuError = 0; // GL vs CPU trilinear of the same table, every point
  let clippedMax = 0;
  let clippedSamples = 0;
  let inGamutPoints = 0;
  let clippedPoints = 0;
  const offenders: WorstSample[] = [];
  let worst: WorstSample | null = null;
  let worstCpu: WorstSample | null = null;
  const clamp = (v: number) => (v < 0 ? 0 : v > 1 ? 1 : v);
  for (const point of fixture.points) {
    const expected = toRec709(def, point.rgb[0], point.rgb[1], point.rgb[2]);
    const cpu = sampleTableTrilinear(table, 65, [clamp(point.rgb[0]), clamp(point.rgb[1]), clamp(point.rgb[2])]);
    const lin = [0, 1, 2].map((c) => def.decodeToLinear(clamp(point.rgb[c] as number)));
    const preClamp = [0, 1, 2].map((row) => (m[row * 3] as number) * (lin[0] as number) + (m[row * 3 + 1] as number) * (lin[1] as number) + (m[row * 3 + 2] as number) * (lin[2] as number));
    const inGamut = preClamp.every((v) => v >= 0) && expected.every((v) => v < 0.98);
    if (inGamut) inGamutPoints += 1;
    else clippedPoints += 1;
    const off = pixelOffset(point.x, point.y);
    for (let channel = 0; channel < 3; channel += 1) {
      const out = bytes[off + channel] as number;
      const want = Math.min(255, Math.max(0, (expected[channel] as number) * 255));
      const error = Math.abs(out - want);
      const cpuWant = Math.min(255, Math.max(0, (cpu[channel] as number) * 255));
      const cpuError = Math.abs(out - cpuWant);
      const sample: WorstSample = { kind: point.kind, x: point.x, y: point.y, channel, error, out, expected: want, code: [...point.rgb] as RGB };
      if (cpuError > maxCpuError) {
        maxCpuError = cpuError;
        worstCpu = { ...sample, error: cpuError, expected: cpuWant };
      }
      if (!inGamut) {
        clippedSamples += 1;
        if (error > clippedMax) clippedMax = error;
        continue;
      }
      sumError += error;
      samples += 1;
      if (error > 6) {
        samplesOverTolerance += 1;
        if (offenders.length < 5) offenders.push(sample);
      }
      if (error > maxError) {
        maxError = error;
        worst = sample;
      }
    }
  }
  const renderer = live.compositor.renderer;
  live.compositor.dispose();
  live.canvas.remove();
  return {
    profile: profileId,
    samples,
    maxError,
    meanError: sumError / samples,
    maxErrorOutOf255: maxError,
    maxErrorNormalized: maxError / 255,
    samplesOverTolerance,
    offenders,
    worst,
    maxCpuError,
    worstCpu,
    inGamutPoints,
    clippedPoints,
    clippedSamples,
    clippedMax,
    glError,
    renderer,
    repeats: result.repeats,
    blank: result.blank,
  };
}

/** A 17^3 invert .cube in project (Adobe/IRIDAS text) form, R fastest. */
function invertCubeText(size = 17): string {
  const lines = ['TITLE "gl-smoke invert"', `LUT_3D_SIZE ${size}`, 'DOMAIN_MIN 0 0 0', 'DOMAIN_MAX 1 1 1'];
  for (let b = 0; b < size; b += 1) {
    for (let g = 0; g < size; g += 1) {
      for (let r = 0; r < size; r += 1) {
        const inv = (v: number) => (1 - v / (size - 1)).toFixed(6);
        lines.push(`${inv(r)} ${inv(g)} ${inv(b)}`);
      }
    }
  }
  return lines.join('\n');
}

/** Assertion 3: viewing LUT applies, the clean flag bypasses it exactly, and a context loss restores both stages. */
async function runViewingLut(): Promise<Record<string, unknown>> {
  const fixture = makeSource();
  const live = createCompositor();
  // A profile is set on the layer so the stage-off sampler-mismatch defect (see the probe in
  // `shaderReport`) does not discard every draw in this test; the viewing pass is what is measured.
  const layer: CompositorLayer = { layerId: 'src', image: fixture.canvas, opacity: 1, inputProfile: 'sonySLog3' };
  const cubText = invertCubeText(17);
  const cube = parseCubeLut(cubText);
  const spec = viewingLutFromCube('gl-smoke-invert', cube);

  // Base picture: no viewing LUT.
  live.compositor.draw([layer]);
  const base = live.compositor.withCleanPicture(() => readPixelsNow(live.gl));

  // Apply the viewing pass.
  live.compositor.setViewingLut(spec);
  live.compositor.draw([layer]);
  const viewed = readPixelsNow(live.gl);

  // Invert expectation: out = 255 - base (17^3 grid of k/16 values, linear, so exact in half float).
  let invertMaxError = 0;
  let changed = 0;
  for (let i = 0; i < base.length; i += 4) {
    for (let c = 0; c < 3; c += 1) {
      const want = 255 - (base[i + c] as number);
      const got = viewed[i + c] as number;
      const error = Math.abs(got - want);
      if (error > invertMaxError) invertMaxError = error;
    }
    if (Math.abs((viewed[i] as number) - (base[i] as number)) > 2) changed += 1;
  }
  const pixels = CANVAS_W * CANVAS_H;
  const changedFraction = changed / pixels;

  // `applyViewingLut: false` is the export/still readback flag: exactly the no-LUT picture.
  live.compositor.draw([layer], { applyViewingLut: false });
  const flaggedOff = live.compositor.withCleanPicture(() => readPixelsNow(live.gl));
  const flaggedOffVsBase = maxAbsDiff(flaggedOff, base);

  // Toggle back: the viewed picture returns byte-for-byte.
  live.compositor.draw([layer]);
  const viewedAgain = readPixelsNow(live.gl);
  const toggleBackVsViewed = maxAbsDiff(viewedAgain, viewed);

  // withCleanPicture on a viewed canvas is the still/scope readback path: exactly the base.
  const cleanDuringView = live.compositor.withCleanPicture(() => readPixelsNow(live.gl));
  const cleanDuringViewVsBase = maxAbsDiff(cleanDuringView, base);

  // Context loss + restore with BOTH stages active: profile table 3D texture and viewing LUT 3D texture.
  const profileLayer: CompositorLayer = { layerId: 'src', image: fixture.canvas, opacity: 1, inputProfile: 'sonySLog3' };
  live.compositor.draw([profileLayer]);
  const beforeViewed = readPixelsNow(live.gl);
  const beforeClean = live.compositor.withCleanPicture(() => readPixelsNow(live.gl));

  const ext = live.gl.getExtension('WEBGL_lose_context');
  const restore = live.waitRestored();
  if (!ext) throw new Error('WEBGL_lose_context unavailable');
  ext.loseContext();
  // Headless SwiftShader does not always auto-restore; ask for it explicitly once the loss event
  // has been delivered (the compositor calls preventDefault, which is what allows a restore).
  await new Promise((resolve) => setTimeout(resolve, 250));
  try {
    ext.restoreContext();
  } catch {
    // Automatic restore is still possible; `restore` below times out if neither happens.
  }
  await restore;

  live.compositor.draw([profileLayer]);
  const afterViewed = readPixelsNow(live.gl);
  const afterClean = live.compositor.withCleanPicture(() => readPixelsNow(live.gl));
  const lossViewedVsBefore = maxAbsDiff(afterViewed, beforeViewed);
  const lossCleanVsBefore = maxAbsDiff(afterClean, beforeClean);

  const glError = live.gl.getError();
  const renderer = live.compositor.renderer;
  const lossState = { ...live.state };
  live.compositor.dispose();
  live.canvas.remove();

  return {
    lutSize: cube.size,
    invertMaxError,
    invertMaxErrorOutOf255: invertMaxError,
    changedPixels: changed,
    changedFraction,
    flaggedOffVsBase,
    toggleBackVsViewed,
    cleanDuringViewVsBase,
    contextLoss: {
      lost: lossState.lost,
      restored: lossState.restored,
      restoreOk: lossState.restoreOk,
      viewedVsBefore: lossViewedVsBefore,
      cleanVsBefore: lossCleanVsBefore,
    },
    glError,
    renderer,
  };
}

/** Assertion 4: stage off = unchanged (self identity, source identity, ramp monotonicity, toggle-off). */
function runBaseline(): Record<string, unknown> {
  const fixture = makeSource();
  const live = createCompositor();
  const layer: CompositorLayer = { layerId: 'src', image: fixture.canvas, opacity: 1 };

  live.compositor.draw([layer]);
  const first = live.compositor.withCleanPicture(() => readPixelsNow(live.gl));
  live.compositor.draw([layer]);
  const second = live.compositor.withCleanPicture(() => readPixelsNow(live.gl));
  const selfDiff = maxAbsDiff(first, second);

  // Whole-canvas source identity for a no-grade layer.
  const srcCtx = fixture.canvas.getContext('2d');
  if (!srcCtx) throw new Error('2d context unavailable');
  const src = srcCtx.getImageData(0, 0, CANVAS_W, CANVAS_H).data;
  let sourceMaxDiff = 0;
  for (let y = 0; y < CANVAS_H; y += 1) {
    for (let x = 0; x < CANVAS_W; x += 1) {
      const off = pixelOffset(x, y);
      const soff = (y * CANVAS_W + x) * 4;
      for (let c = 0; c < 3; c += 1) {
        const d = Math.abs((first[off + c] as number) - (src[soff + c] as number));
        if (d > sourceMaxDiff) sourceMaxDiff = d;
      }
    }
  }

  // Ramp monotonicity on the output (grey ramp, green channel).
  let monotonic = true;
  let prev = -1;
  for (let x = 0; x < RAMP_W; x += 1) {
    const v = first[pixelOffset(RAMP_X + x, RAMP_Y + Math.floor(RAMP_H / 2)) + 1] as number;
    if (v < prev) monotonic = false;
    prev = v;
  }

  // Grade-only path (identity grade, no profile) stays the source's picture, monotonic.
  live.compositor.draw([{ layerId: 'src', image: fixture.canvas, opacity: 1, grade: [] }]);
  const graded = live.compositor.withCleanPicture(() => readPixelsNow(live.gl));
  const gradeVsSource = maxAbsDiff(graded, first);
  let gradeMonotonic = true;
  prev = -1;
  for (let x = 0; x < RAMP_W; x += 1) {
    const v = graded[pixelOffset(RAMP_X + x, RAMP_Y + Math.floor(RAMP_H / 2)) + 1] as number;
    if (v < prev) gradeMonotonic = false;
    prev = v;
  }

  // Turning the input stage on then off again must land exactly on the baseline (no leaked state).
  live.compositor.draw([{ layerId: 'src', image: fixture.canvas, opacity: 1, inputProfile: 'arriLogC4' }]);
  live.compositor.draw([layer]);
  const afterProfileToggle = live.compositor.withCleanPicture(() => readPixelsNow(live.gl));
  const toggleOffVsFirst = maxAbsDiff(afterProfileToggle, first);

  const glError = live.gl.getError();
  const renderer = live.compositor.renderer;
  const stageOffPicture = pictureStats(first, fixture.canvas);
  live.compositor.dispose();
  live.canvas.remove();

  return {
    selfDiff,
    sourceMaxDiff,
    stageOffPicture,
    rampMonotonic: monotonic,
    gradeVsSource,
    gradeMonotonic,
    toggleOffVsFirst,
    glError,
    renderer,
  };
}

/** Pure CPU comparison of the 65^3 table (trilinear) against the analytic mirror, for diagnostics. */
function profileProbe(profileId: string, r: number, g: number, b: number): Record<string, unknown> {
  const def = getCameraProfile(profileId as never);
  const size = 65;
  const table = generateCameraProfileTable(profileId, size);
  const analytic = toRec709(def, r, g, b);
  const trilinear = sampleTableTrilinear(table, size, [r, g, b]);
  const pos = [r, g, b].map((v) => Math.min(1, Math.max(0, v)) * (size - 1));
  const i0 = pos.map((v) => Math.min(size - 2, Math.floor(v)));
  const frac = pos.map((v, k) => v - (i0[k] as number));
  const neighbours: Record<string, unknown>[] = [];
  for (let db = 0; db < 2; db += 1) {
    for (let dg = 0; dg < 2; dg += 1) {
      for (let dr = 0; dr < 2; dr += 1) {
        const ri = (i0[0] as number) + dr;
        const gi = (i0[1] as number) + dg;
        const bi = (i0[2] as number) + db;
        const idx = ((bi * size + gi) * size + ri) * 3;
        const code: RGB = [ri / (size - 1), gi / (size - 1), bi / (size - 1)];
        neighbours.push({
          code,
          table: [table[idx], table[idx + 1], table[idx + 2]],
          analytic: toRec709(def, code[0], code[1], code[2]),
        });
      }
    }
  }
  return { profileId, input: [r, g, b], analytic, trilinear, frac, neighbours };
}

/** Grade parity: fixture patches through the compositor's per-node shader vs the CPU `applyPreviewGradeNodes`. */
function runGradeParity(patches: number[][], stacks: { name: string; stack: GradeStack }[]): Record<string, unknown>[] {
  const cell = 16;
  const cols = Math.floor(CANVAS_W / cell);
  const canvas = document.createElement('canvas');
  canvas.width = CANVAS_W;
  canvas.height = CANVAS_H;
  const ctx = canvas.getContext('2d');
  if (!ctx) throw new Error('2d context unavailable');
  ctx.fillStyle = '#000';
  ctx.fillRect(0, 0, CANVAS_W, CANVAS_H);
  patches.forEach((p, k) => {
    ctx.fillStyle = `rgb(${p[0]},${p[1]},${p[2]})`;
    ctx.fillRect((k % cols) * cell, Math.floor(k / cols) * cell, cell, cell);
  });
  const live = createCompositor();
  const out = stacks.map(({ name, stack }) => {
    const nodes = previewGradeNodesOf([stack]);
    live.compositor.draw([{ layerId: 'src', image: canvas, opacity: 1, grade: nodes.length > 0 ? nodes : undefined }]);
    const px = live.compositor.withCleanPicture(() => readPixelsNow(live.gl));
    let maxDiff = 0;
    patches.forEach((p, k) => {
      const x = (k % cols) * cell + cell / 2;
      const y = Math.floor(k / cols) * cell + cell / 2;
      const want = applyPreviewGradeNodes(nodes, [p[0] / 255, p[1] / 255, p[2] / 255]);
      for (let c = 0; c < 3; c += 1) maxDiff = Math.max(maxDiff, Math.abs((px[pixelOffset(x, y) + c] as number) - want[c] * 255));
    });
    return { name, maxDiff };
  });
  live.compositor.dispose();
  live.canvas.remove();
  return out;
}

/**
 * The adaptive picture size: `resize` clears a canvas, so the compositor repaints the held picture at the new size
 * (no new image is handed in). Measures the picture before, right after a resize down, and after the resize back.
 */
function runResizeRepaint(): Record<string, unknown> {
  const source = makeSource().canvas;
  const live = createCompositor();
  const layers: CompositorLayer[] = [
    { layerId: 'bg', image: source, opacity: 1, effects: [{ type: 'mono', amount: 1 }, { type: 'blur', amount: 0.2 }] },
    { layerId: 'top', image: source, opacity: 0.5, transform: { x: 0.1, y: 0, scale: 0.5, rotation: 0, opacity: 1, crop: { left: 0, right: 0, top: 0, bottom: 0 } }, shape: { kind: 'ellipse', x: 0.5, y: 0.5, w: 0.8, h: 0.8, feather: 0.1, invert: false } },
  ];
  const mean = (bytes: Uint8Array): number => {
    let sum = 0;
    for (let i = 0; i < bytes.length; i += 4) sum += (bytes[i] as number) + (bytes[i + 1] as number) + (bytes[i + 2] as number);
    return sum / ((bytes.length / 4) * 3);
  };
  live.compositor.draw(layers);
  const before = live.compositor.withCleanPicture(() => readPixelsNow(live.gl));
  live.compositor.resize(CANVAS_W / 2, CANVAS_H / 2);
  const small = live.compositor.withCleanPicture(() => readPixelsNow(live.gl, CANVAS_W / 2, CANVAS_H / 2));
  const smallSize = [live.canvas.width, live.canvas.height];
  // A resize to the size it already has repaints nothing and changes nothing.
  live.compositor.resize(CANVAS_W / 2, CANVAS_H / 2);
  const smallAgain = live.compositor.withCleanPicture(() => readPixelsNow(live.gl, CANVAS_W / 2, CANVAS_H / 2));
  live.compositor.resize(CANVAS_W, CANVAS_H);
  const after = live.compositor.withCleanPicture(() => readPixelsNow(live.gl));
  const glError = live.gl.getError();
  const out = { meanBefore: mean(before), meanSmall: mean(small), meanAfter: mean(after), backDiff: maxAbsDiff(before, after), sameSizeDiff: maxAbsDiff(small, smallAgain), smallSize, glError };
  live.compositor.dispose();
  live.canvas.remove();
  return out;
}

/**
 * The whole live session in a real WebGL2 context, with a slow frame clock standing in for a weak device: a clip with
 * a grade, a soft shape mask, a blur and a vignette under a title. Measures the canvas size and the picture before
 * play, while frames are being skipped, and after pause.
 */
async function runAdaptiveSession(): Promise<Record<string, unknown>> {
  const frameSource = document.createElement('canvas');
  frameSource.width = 960;
  frameSource.height = 540;
  const fctx = frameSource.getContext('2d');
  if (!fctx) throw new Error('2d context unavailable');
  const paint = (n: number) => {
    const g = fctx.createLinearGradient(0, 0, 960, 540);
    g.addColorStop(0, 'rgb(220,120,40)');
    g.addColorStop(1, 'rgb(40,120,220)');
    fctx.fillStyle = g;
    fctx.fillRect(0, 0, 960, 540);
    fctx.fillStyle = '#fff';
    fctx.fillRect((n * 7) % 900, 200, 60, 140);
  };
  const clip = media('clip', 0, 3000, 0, {
    effects: [{ type: 'blur', amount: 0.3, enabled: true }, { type: 'vignette', amount: 0.6, enabled: true }],
    mask: { kind: 'ellipse', x: 0.5, y: 0.5, w: 0.9, h: 0.9, feather: 0.08 },
    grade: { enabled: true, nodes: [{ id: 'n1', enabled: true, primaries: { saturation: 1.3, gain: { master: 0.1 } }, curves: { master: [{ x: 0, y: 0 }, { x: 0.5, y: 0.6 }, { x: 1, y: 1 }] } }] },
  } as Parameters<typeof media>[4]);
  const timeline = doc([track('v1', 'video', [clip]), track('v2', 'video', [title('t1', 0, 3000, 'Adaptive size')])]);
  const canvas = document.createElement('canvas');
  document.body.appendChild(canvas);
  let slowTimer: ReturnType<typeof setTimeout> | null = null;
  const session = createLivePreviewSession({
    canvas,
    getDoc: () => timeline,
    flags: (t) => ({ locked: t.locked, enabled: t.enabled, mute: t.mute, solo: t.solo }),
    proxyUrlFor: () => '/synthetic',
    openSource: async () => ({
      getFrame: async (sourceFrame: number) => {
        paint(sourceFrame);
        const image = await createImageBitmap(frameSource);
        return { sourceFrame, image, close: () => image.close() };
      },
      dispose: () => undefined,
    }),
    audioContext: null,
    playbackResolution: 'half',
    // A weak device: the frame callback comes ten times a second, so at 30 fps two slots in three are skipped.
    requestFrame: (cb) => {
      slowTimer = setTimeout(() => cb(performance.now()), 100);
      return 1;
    },
    cancelFrame: () => {
      if (slowTimer !== null) clearTimeout(slowTimer);
      slowTimer = null;
    },
  });
  if (!session) throw new Error('live session unavailable');
  const gl = canvas.getContext('webgl2');
  if (!gl) throw new Error('webgl2 context unavailable');
  const wait = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));
  const meanNow = (): number => {
    const bytes = new Uint8Array(canvas.width * canvas.height * 4);
    gl.readPixels(0, 0, canvas.width, canvas.height, gl.RGBA, gl.UNSIGNED_BYTE, bytes);
    let sum = 0;
    for (let i = 0; i < bytes.length; i += 4) sum += (bytes[i] as number) + (bytes[i + 1] as number) + (bytes[i + 2] as number);
    return sum / ((bytes.length / 4) * 3);
  };
  session.seek(0);
  await wait(400);
  const paused = { size: [canvas.width, canvas.height], mean: meanNow(), quality: { ...session.adaptiveQuality } };
  session.play(1);
  const sizes: number[] = [];
  const means: number[] = [];
  for (let i = 0; i < 80 && (session.adaptiveQuality?.scale ?? 1) === 1; i += 1) {
    await wait(100);
    sizes.push(canvas.width);
    means.push(meanNow());
  }
  await wait(300);
  const playing = { size: [canvas.width, canvas.height], mean: meanNow(), minMeanWhilePlaying: Math.min(...means, meanNow()), quality: { ...session.adaptiveQuality }, note: session.reducedQuality ?? null, frame: session.currentFrame() };
  session.pause();
  const rightAfterPause = { size: [canvas.width, canvas.height], mean: meanNow() };
  await wait(400);
  const pausedAgain = { size: [canvas.width, canvas.height], mean: meanNow(), quality: { ...session.adaptiveQuality }, note: session.reducedQuality ?? null };
  const glError = gl.getError();
  session.dispose();
  canvas.remove();
  return { paused, playing, rightAfterPause, pausedAgain, glError };
}

(globalThis as { __glc?: unknown }).__glc = {
  runAdaptiveSession,
  runResizeRepaint,
  runGradeParity,
  shaderReport,
  runProfile,
  runViewingLut,
  runBaseline,
  profileProbe,
};
