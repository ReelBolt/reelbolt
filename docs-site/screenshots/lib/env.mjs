// Where things live, and how the harness finds Playwright, sharp and the web app's dependencies
// without any absolute home path: everything is derived from this file's location or from git.
import { createRequire } from 'node:module';
import { existsSync, mkdirSync, mkdtempSync, rmSync, readdirSync, readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
export const harnessDir = resolve(here, '..');
export const repoRoot = resolve(harnessDir, '../..');
export const webDir = join(repoRoot, 'web');
export const imgDir = join(repoRoot, 'docs/user-guide/basics/img');
export const manifestFile = join(harnessDir, 'shots.json');

/** The checkout that owns the shared .git (the main checkout when this is a git worktree). */
export function mainCheckout() {
  try {
    const common = execFileSync('git', ['rev-parse', '--path-format=absolute', '--git-common-dir'], { cwd: repoRoot, encoding: 'utf8' }).trim();
    return dirname(common);
  } catch {
    return repoRoot;
  }
}

/** Roots that may hold installed dependencies, nearest first: this checkout, then the main one. */
function roots() {
  return [...new Set([repoRoot, mainCheckout()])];
}

/** Playwright comes from the repository's e2e/node_modules (this checkout, else the main checkout). */
export function loadPlaywright() {
  for (const root of roots()) {
    const pkg = join(root, 'e2e/package.json');
    if (!existsSync(join(root, 'e2e/node_modules/playwright'))) continue;
    return createRequire(pkg)('playwright');
  }
  throw new Error('Playwright not found: run `npm ci` in e2e/ (the harness uses e2e/node_modules/playwright).');
}

/** sharp ships with the web app (Next.js image optimisation); it encodes the WebP files. */
export function loadSharp() {
  return createRequire(join(webDir, 'package.json'))('sharp');
}

/**
 * The web app's node_modules, hard-linked (cp -al, no extra disk) from the first checkout that has them,
 * when this checkout has none. Returns true when it created the link tree.
 */
export function ensureWebModules() {
  if (existsSync(join(webDir, 'node_modules/next'))) {
    assertDependenciesInstalled();
    return false;
  }
  const donor = roots().map((r) => join(r, 'web/node_modules')).find((p) => p !== join(webDir, 'node_modules') && existsSync(join(p, 'next')));
  if (!donor) throw new Error('web/node_modules is missing: run `npm ci` in web/ (or in the main checkout; the harness hard-links it).');
  execFileSync('cp', ['-al', donor, join(webDir, 'node_modules')], { stdio: 'inherit' });
  assertDependenciesInstalled();
  return true;
}

/** A lagging node_modules (older than the lockfile) fails deep inside `next dev`; say so up front instead. */
function assertDependenciesInstalled() {
  const pkg = JSON.parse(readFileSync(join(webDir, 'package.json'), 'utf8'));
  const missing = Object.keys({ ...pkg.dependencies, ...pkg.devDependencies }).filter((d) => !existsSync(join(webDir, 'node_modules', d)));
  if (missing.length) {
    throw new Error(`web/node_modules lacks ${missing.join(', ')}: run \`npm ci\` in web/ (or delete web/node_modules so the harness links it from a checkout that is up to date).`);
  }
}

/**
 * Scratch directory for logs and temp files. SCREENSHOT_SCRATCH wins; else <repo>/../_scratch when it
 * exists; else a fresh os tmpdir directory that `cleanup()` removes. Never anything else is left behind.
 */
export function makeScratch() {
  const given = process.env.SCREENSHOT_SCRATCH;
  const sibling = resolve(repoRoot, '../_scratch');
  let dir;
  let owned = false;
  if (given) dir = resolve(given);
  else if (existsSync(sibling)) dir = join(sibling, 'screenshots');
  else { dir = mkdtempSync(join(tmpdir(), 'rb-shots-')); owned = true; }
  mkdirSync(dir, { recursive: true });
  // A temp folder of our own inside it, so cleanup removes exactly what this run created and nothing that was there before.
  const tmp = mkdtempSync(join(dir, 'tmp-'));
  // Chromium's profile dirs, Next's caches and sharp's temp files all follow TMPDIR.
  process.env.TMPDIR = tmp;
  return {
    dir,
    log: join(dir, 'next-dev.log'),
    cleanup() {
      rmSync(tmp, { recursive: true, force: true });
      if (owned) rmSync(dir, { recursive: true, force: true });
    },
  };
}

export const listImages = () => (existsSync(imgDir) ? readdirSync(imgDir).filter((f) => f.endsWith('.webp') || f.endsWith('.png')) : []);
