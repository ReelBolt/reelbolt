// shots.json: load, validate, and the --check comparison against the image folder.
import { readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { imgDir, listImages, manifestFile } from './env.mjs';
import { MAX_BYTES } from './encode.mjs';

export const VIEWPORTS = {
  desktop: { viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1, isMobile: false, hasTouch: false },
  phone: { viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true },
};
const STEP_KEYS = ['click', 'clickAt', 'fill', 'hover', 'press', 'scroll', 'scrollTo', 'waitFor', 'wait'];
const SHOT_KEYS = ['id', 'guide', 'route', 'viewport', 'alpha', 'auth', 'scenario', 'chat', 'waitFor', 'steps', 'clip', 'highlight', 'fullPage', 'height', 'storage', 'note'];
const ID = /^[a-z0-9][a-z0-9-]*$/;

export function loadManifest() {
  const raw = JSON.parse(readFileSync(manifestFile, 'utf8'));
  const shots = Array.isArray(raw) ? raw : raw.shots;
  const errors = [];
  const seen = new Set();
  if (!Array.isArray(shots)) throw new Error('shots.json must hold a "shots" array');
  for (const [i, s] of shots.entries()) {
    const at = `shots[${i}]${s?.id ? ` (${s.id})` : ''}`;
    if (typeof s.id !== 'string' || !ID.test(s.id)) errors.push(`${at}: "id" must be lower-case letters, digits and dashes`);
    else if (seen.has(s.id)) errors.push(`${at}: duplicate id`);
    else seen.add(s.id);
    for (const k of Object.keys(s)) if (!SHOT_KEYS.includes(k)) errors.push(`${at}: unknown key "${k}"`);
    if (typeof s.guide !== 'string' || !s.guide) errors.push(`${at}: "guide" (the page slug that uses it) is required`);
    if (typeof s.route !== 'string' || !s.route.startsWith('/app')) errors.push(`${at}: "route" must start with /app`);
    if (!VIEWPORTS[s.viewport]) errors.push(`${at}: "viewport" must be one of ${Object.keys(VIEWPORTS).join(', ')}`);
    for (const [j, step] of (s.steps ?? []).entries()) {
      const keys = STEP_KEYS.filter((k) => k in step);
      if (keys.length !== 1) errors.push(`${at}: steps[${j}] must use exactly one of ${STEP_KEYS.join(', ')}`);
      if ('fill' in step && typeof step.value !== 'string') errors.push(`${at}: steps[${j}] fill needs a string "value"`);
    }
    const c = s.clip;
    if (c != null && typeof c !== 'string' && !(typeof c === 'object' && ['x', 'y', 'width', 'height'].every((k) => Number.isFinite(c[k])))) {
      errors.push(`${at}: "clip" must be null, a selector, or {x,y,width,height}`);
    }
  }
  if (errors.length) throw new Error(`shots.json is invalid:\n  ${errors.join('\n  ')}`);
  return shots;
}

/** Every shot has an image and every image has a shot. Returns a list of problems (empty = fine). */
export function checkImages(shots) {
  const images = new Map(listImages().map((f) => [f.replace(/\.(webp|png)$/, ''), f]));
  const problems = [];
  for (const s of shots) if (!images.has(s.id)) problems.push(`shot "${s.id}" has no image (run \`node screenshots/run.mjs\`)`);
  const ids = new Set(shots.map((s) => s.id));
  for (const [id, f] of images) {
    if (!ids.has(id)) problems.push(`image "${f}" has no shot in shots.json`);
    else if (statSync(join(imgDir, f)).size > MAX_BYTES) problems.push(`image "${f}" is over the ${MAX_BYTES / 1024} KB budget`);
  }
  return problems;
}
