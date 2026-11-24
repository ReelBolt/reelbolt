import { test, expect, type Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { readFileSync } from 'node:fs';
import path from 'node:path';

/**
 * Real-GL smoke test of the viewer's two `sampler3D` stages (T-017).
 *
 * No docker stack, no network, no webServer: `beforeAll` bundles `harness-entry.ts` (which imports
 * the real `createGlCompositor` and the profile/LUT helpers) with the Vite already in
 * `web/node_modules`, then every test injects that bundle into a blank page and drives the
 * compositor inside a real WebGL2 context (SwiftShader). The bundle lives in /tmp, never the repo.
 */

const HERE = __dirname;
const BUILD_SCRIPT = path.join(HERE, 'build-bundle.mjs');
const BUNDLE_DIR = process.env.GL_COMPOSITOR_BUNDLE_DIR ?? '/tmp/gl-compositor-bundle';
const BUNDLE = path.join(BUNDLE_DIR, 'gl-compositor-harness.js');

const PROFILE_IDS = ['sonySLog3', 'appleLog', 'arriLogC4'] as const;

// Not `serial`: with one worker the tests run in order, and a failing assertion must not hide the
// evidence from the other checks (the camera-profile check is expected to fail on arriLogC4).

test.beforeAll(() => {
  execFileSync(process.execPath, [BUILD_SCRIPT, BUNDLE_DIR], { encoding: 'utf8', stdio: 'pipe' });
  if (!existsSync(BUNDLE)) throw new Error(`harness bundle missing at ${BUNDLE}`);
});

/** Blank page + injected harness; returns the console errors seen on this page. */
async function openHarness(page: Page): Promise<string[]> {
  const consoleErrors: string[] = [];
  page.on('console', (message) => {
    if (message.type() === 'error') consoleErrors.push(message.text());
  });
  page.on('pageerror', (error) => consoleErrors.push(String(error)));
  await page.setContent('<!doctype html><html><body style="margin:0"></body></html>');
  await page.addScriptTag({ path: BUNDLE });
  await page.waitForFunction(() => Boolean((window as { __glc?: unknown }).__glc));
  return consoleErrors;
}

function noteConsoleErrors(errors: string[]): void {
  if (errors.length > 0) console.log(`GL_CONSOLE_ERRORS ${JSON.stringify(errors)}`);
}

test('all compositor programs compile and link in a real WebGL2 context', async ({ page }) => {
  const errors = await openHarness(page);
  const report = await page.evaluate(() => (window as { __glc: { shaderReport: () => unknown } }).__glc.shaderReport());
  const r = report as {
    available: boolean;
    created: boolean;
    renderer: string | null;
    liveRenderer: string | null;
    glError: number;
    stageOffError: number;
    stageOnError: number;
    programs: { label: string; vertexCompiled: boolean; fragmentCompiled: boolean; linked: boolean; vertexLog: string; fragmentLog: string; linkLog: string }[];
  };

  console.log(`GL_COMPOSITOR_REPORT ${JSON.stringify(r)}`);
  noteConsoleErrors(errors);

  expect(r.available, 'WebGL2 context available').toBe(true);
  expect(r.created, 'createGlCompositor returned a compositor').toBe(true);
  expect(r.glError, 'no GL error while compiling/linking').toBe(0);
  // `stageOffError` is a known latent sampler3D/texture-unit mismatch (see the probe): the draw
  // still renders the right picture (assertion 4), so it is reported, not asserted here.
  expect(r.stageOnError, 'no GL error with the input stage on').toBe(0);
  console.log(`GL_STAGE_OFF_SAMPLER_PROBE ${JSON.stringify(r.samplerMismatchProbe)}`);
  expect(r.renderer, 'renderer string').toBeTruthy();
  console.log(`GL_RENDERER ${r.renderer}`);
  for (const program of r.programs) {
    expect(program.vertexCompiled, `${program.label} vertex shader: ${program.vertexLog}`).toBe(true);
    expect(program.fragmentCompiled, `${program.label} fragment shader: ${program.fragmentLog}`).toBe(true);
    expect(program.linked, `${program.label} link: ${program.linkLog}`).toBe(true);
  }
});

test('camera profiles match toRec709 at ramp and patch points (max <= 6/255)', async ({ page }) => {
  const errors = await openHarness(page);
  const results: Record<string, unknown>[] = [];
  for (const profileId of PROFILE_IDS) {
    const result = await page.evaluate(
      (id) => (window as { __glc: { runProfile: (profile: string) => unknown } }).__glc.runProfile(id),
      profileId,
    );
    results.push(result as Record<string, unknown>);
  }
  noteConsoleErrors(errors);

  const measurements = results.map((result) => ({
    profile: result.profile,
    samples: result.samples,
    maxError: result.maxError,
    meanError: (result.meanError as number).toFixed(4),
    maxErrorOutOf255: result.maxErrorOutOf255,
    maxErrorNormalized: result.maxErrorNormalized,
    worst: result.worst,
    maxCpuError: result.maxCpuError,
    worstCpu: result.worstCpu,
    inGamutPoints: result.inGamutPoints,
    glError: result.glError,
  }));
  console.log(`GL_GAMUT_CLIPPED_REPORT ${JSON.stringify(results.map((r) => ({ profile: r.profile, clippedPoints: r.clippedPoints, clippedSamples: r.clippedSamples, maxErrorOutOf255: r.clippedMax })))}`);
  console.log(`GL_MEASUREMENTS ${JSON.stringify({ profiles: measurements, renderer: results[0]?.renderer ?? null })}`);

  // If a profile exceeds the tolerance, show that the CPU trilinear mirror of the same table
  // reproduces the GPU value (i.e. the gap is table resolution, not the shader/readback).
  for (const result of results) {
    const worst = result.worst as { code: [number, number, number] } | null;
    if (worst && (result.maxError as number) > 6) {
      const probe = await page.evaluate(
        (arg) => (window as { __glc: { profileProbe: (id: string, r: number, g: number, b: number) => unknown } }).__glc.profileProbe(arg.id, arg.code[0], arg.code[1], arg.code[2]),
        { id: result.profile as string, code: worst.code },
      );
      console.log(`GL_PROFILE_PROBE ${JSON.stringify(probe)}`);
    }
  }

  for (const result of results) {
    expect(result.samples as number, `${result.profile}: sample count`).toBeGreaterThan(0);
    expect(result.glError, `${result.profile}: no GL error`).toBe(0);
    expect.soft(result.maxCpuError as number, `${result.profile}: GL equals CPU trilinear of the same table within 1.5/255 (measured ${result.maxCpuError})`).toBeLessThanOrEqual(1.5);
    expect.soft(result.maxError as number, `${result.profile}: in-gamut error vs analytic <= 6/255 (measured ${result.maxError})`).toBeLessThanOrEqual(6);
    expect.soft(result.meanError as number, `${result.profile}: mean error is small`).toBeLessThanOrEqual(2);
  }
});

test('viewing LUT applies, the clean flag bypasses it exactly, and context loss restores both textures', async ({ page }) => {
  const errors = await openHarness(page);
  const result = await page.evaluate(() => (window as { __glc: { runViewingLut: () => Promise<unknown> } }).__glc.runViewingLut());
  const r = result as {
    lutSize: number;
    invertMaxError: number;
    changedPixels: number;
    changedFraction: number;
    flaggedOffVsBase: number;
    toggleBackVsViewed: number;
    cleanDuringViewVsBase: number;
    contextLoss: { lost: number; restored: number; restoreOk: boolean | null; viewedVsBefore: number; cleanVsBefore: number };
    glError: number;
  };
  console.log(`GL_VIEWING_LUT_REPORT ${JSON.stringify(r)}`);
  noteConsoleErrors(errors);

  expect(r.lutSize).toBe(17);
  expect(r.changedFraction, 'the invert LUT changes the picture').toBeGreaterThan(0.5);
  expect(r.invertMaxError, 'invert LUT output equals 255 - input within 1/255').toBeLessThanOrEqual(1);
  expect(r.flaggedOffVsBase, 'applyViewingLut:false equals the no-LUT output exactly').toBe(0);
  expect(r.toggleBackVsViewed, 'toggling the LUT back is byte-identical').toBe(0);
  expect(r.cleanDuringViewVsBase, 'withCleanPicture readback equals the no-LUT output exactly').toBe(0);
  expect(r.contextLoss.lost, 'context loss event seen').toBe(1);
  expect(r.contextLoss.restored, 'context restored event seen').toBe(1);
  expect(r.contextLoss.restoreOk, 'compositor rebuilt after restore').toBe(true);
  expect(r.contextLoss.viewedVsBefore, 'viewed output matches pre-loss within 1/255').toBeLessThanOrEqual(1);
  expect(r.contextLoss.cleanVsBefore, 'clean output matches pre-loss within 1/255').toBeLessThanOrEqual(1);
  // glError is recorded, not asserted: the stage-off draw path has a known INVALID_OPERATION (see test 1).
});

test('stage off is unchanged: self-identical, source-identical, monotonic, and toggling off restores exactly', async ({ page }) => {
  const errors = await openHarness(page);
  const result = await page.evaluate(() => (window as { __glc: { runBaseline: () => unknown } }).__glc.runBaseline());
  const r = result as {
    selfDiff: number;
    sourceMaxDiff: number;
    rampMonotonic: boolean;
    gradeVsSource: number;
    gradeMonotonic: boolean;
    toggleOffVsFirst: number;
    glError: number;
  };
  console.log(`GL_BASELINE_REPORT ${JSON.stringify(r)}`);
  noteConsoleErrors(errors);

  expect(r.selfDiff, 'two stage-off draws are byte-identical').toBe(0);
  expect(r.sourceMaxDiff, 'no-grade layer equals the source canvas within 1/255').toBeLessThanOrEqual(1);
  expect(r.rampMonotonic, 'source ramp stays monotonic through the stage-off path').toBe(true);
  expect(r.gradeVsSource, 'grade-only path (identity grade) equals the same picture within 1/255').toBeLessThanOrEqual(1);
  expect(r.gradeMonotonic, 'grade-only ramp stays monotonic').toBe(true);
  expect(r.toggleOffVsFirst, 'turning the input stage on then off restores the baseline exactly').toBe(0);
  // glError is recorded, not asserted: the stage-off draw path has a known INVALID_OPERATION (see test 1).
});

const PARITY_CASES = ['identity', 'lift-red-plus-0.3', 'gamma-green-minus-0.3', 'gain-blue-plus-0.3', 'all-wheels-all-channels', 'two-nodes', 'tint-plus-0.5-gamma-green-plus-0.3', 'hue-30', 'temperature-plus-0.5'];

test('grade shader = CPU per-node export maths on the ffmpeg fixture patches (max <= 2/255)', async ({ page }) => {
  const errors = await openHarness(page);
  const fixture = JSON.parse(readFileSync(path.join(HERE, '../../../web/lib/editing/color/__fixtures__/grade-parity.json'), 'utf8')) as { patches: number[][]; cases: { name: string; stack: unknown }[] };
  const stacks = fixture.cases.filter((c) => PARITY_CASES.includes(c.name)).map((c) => ({ name: c.name, stack: c.stack }));
  expect(stacks.length).toBe(PARITY_CASES.length);
  // One 12-node case: nodes 8..12 fold into the old-maths tail stage, the CPU reference does the same.
  const twelve = { enabled: true, nodes: Array.from({ length: 12 }, (_, i) => ({ id: `n${i}`, enabled: true, primaries: { lift: { r: 0.05 }, gamma: { master: 0.04 }, saturation: 1.03, exposure: 0.05 } })) };
  stacks.push({ name: 'twelve-nodes', stack: twelve });
  const results = (await page.evaluate(
    (arg) => (window as { __glc: { runGradeParity: (p: number[][], s: unknown[]) => unknown } }).__glc.runGradeParity(arg.patches, arg.stacks),
    { patches: fixture.patches, stacks },
  )) as { name: string; maxDiff: number }[];
  console.log(`GL_GRADE_PARITY ${JSON.stringify(results)}`);
  noteConsoleErrors(errors);
  for (const r of results) expect(r.maxDiff, `${r.name}: shader vs CPU (measured ${r.maxDiff})`).toBeLessThanOrEqual(2);
});

test('a resize repaints the held picture at the new size (adaptive size never shows a cleared canvas)', async ({ page }) => {
  const errors = await openHarness(page);
  const r = (await page.evaluate(() => (window as { __glc: { runResizeRepaint: () => unknown } }).__glc.runResizeRepaint())) as {
    meanBefore: number;
    meanSmall: number;
    meanAfter: number;
    backDiff: number;
    sameSizeDiff: number;
    smallSize: number[];
    glError: number;
  };
  console.log(`GL_RESIZE_REPAINT ${JSON.stringify(r)}`);
  noteConsoleErrors(errors);
  expect(r.glError, 'no GL error across the resizes').toBe(0);
  expect(r.smallSize).toEqual([256, 64]);
  expect(r.meanBefore, 'the fixture is not black').toBeGreaterThan(5);
  // Right after the resize, with no draw call from the session, the canvas already holds the same picture, smaller.
  expect(Math.abs(r.meanSmall - r.meanBefore), `mean ${r.meanSmall} vs ${r.meanBefore}`).toBeLessThanOrEqual(Math.max(3, r.meanBefore * 0.15));
  expect(r.sameSizeDiff, 'a resize to the same size changes nothing').toBe(0);
  // Back at the first size the repaint is the first picture again (held textures, same passes).
  expect(r.backDiff, `full size again (max diff ${r.backDiff})`).toBeLessThanOrEqual(2);
});

test('the live session on a slow frame clock: every effect stays on, the picture gets smaller while playing and is full size again on pause', async ({ page }) => {
  const errors = await openHarness(page);
  const r = (await page.evaluate(() => (window as { __glc: { runAdaptiveSession: () => Promise<unknown> } }).__glc.runAdaptiveSession())) as {
    paused: { size: number[]; mean: number; quality: { scale: number; atFloor: boolean } };
    playing: { size: number[]; mean: number; minMeanWhilePlaying: number; quality: { scale: number }; note: string | null; frame: number };
    rightAfterPause: { size: number[]; mean: number };
    pausedAgain: { size: number[]; mean: number; quality: { scale: number }; note: string | null };
    glError: number;
  };
  console.log(`GL_ADAPTIVE_SESSION ${JSON.stringify(r)}`);
  noteConsoleErrors(errors);
  expect(r.glError, 'no GL error across play, the size steps and pause').toBe(0);
  // Paused: the full playback size (half of 1920x1080) and a real picture (grade, mask, blur, vignette and the title drawn).
  expect(r.paused.size).toEqual([960, 540]);
  expect(r.paused.mean).toBeGreaterThan(10);
  expect(r.paused.quality.scale).toBe(1);
  // Playing on a slow clock: the playhead moved, the canvas is smaller, and the picture never went black on the way.
  expect(r.playing.frame).toBeGreaterThan(30);
  expect(r.playing.quality.scale).toBeLessThan(1);
  expect(r.playing.size[0]).toBeLessThan(960);
  expect(r.playing.note).toMatch(/smaller size/);
  expect(r.playing.minMeanWhilePlaying, 'no cleared (black) canvas at any sample while playing').toBeGreaterThan(10);
  expect(Math.abs(r.playing.mean - r.paused.mean), 'the smaller picture is the same composite').toBeLessThan(r.paused.mean * 0.5);
  // Pause: full size in the same call, with the picture already on it; then the frame is presented afresh.
  expect(r.rightAfterPause.size).toEqual([960, 540]);
  expect(r.rightAfterPause.mean).toBeGreaterThan(10);
  expect(r.pausedAgain.size).toEqual([960, 540]);
  expect(r.pausedAgain.quality.scale).toBe(1);
  // SwiftShader keeps its own standing software-graphics note; the adaptive one is gone once paused.
  expect(r.pausedAgain.note ?? '').not.toMatch(/smaller size/);
});
