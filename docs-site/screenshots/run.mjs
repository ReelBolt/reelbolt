#!/usr/bin/env node
// `node screenshots/run.mjs` (in docs-site): regenerate the guide screenshots. See README.md.
//   node screenshots/run.mjs                   every shot in shots.json
//   node screenshots/run.mjs --only a,b     just those ids
//   node screenshots/run.mjs --check        no browser: every shot has an image and every image has a shot
//   node screenshots/run.mjs --list         print the shot ids
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { ensureWebModules, imgDir, loadPlaywright, makeScratch } from './lib/env.mjs';
import { encode, looksUnchanged, MAX_BYTES } from './lib/encode.mjs';
import { checkImages, loadManifest } from './lib/manifest.mjs';

const args = process.argv.slice(2);
const flag = (name) => args.includes(`--${name}`);
const value = (name) => {
  const i = args.indexOf(`--${name}`);
  return i === -1 ? undefined : args[i + 1];
};
const log = (m) => console.log(m);

const shots = loadManifest();

if (flag('list')) {
  for (const s of shots) log(`${s.id}\t${s.guide}\t${s.viewport}\t${s.route}`);
  process.exit(0);
}
if (flag('check')) {
  const problems = checkImages(shots);
  if (problems.length) {
    console.error(`screenshots --check failed:\n  ${problems.join('\n  ')}`);
    process.exit(1);
  }
  log(`screenshots --check: ${shots.length} shots, all have images, no stray images.`);
  process.exit(0);
}

let selected = shots;
const only = value('only');
if (only) {
  const ids = only.split(',').map((s) => s.trim()).filter(Boolean);
  const unknown = ids.filter((id) => !shots.some((s) => s.id === id));
  if (unknown.length) {
    console.error(`unknown shot id(s): ${unknown.join(', ')}\nknown: ${shots.map((s) => s.id).join(', ')}`);
    process.exit(2);
  }
  selected = shots.filter((s) => ids.includes(s.id));
}

const scratch = makeScratch();
const { startWeb, stopWeb } = await import('./lib/server.mjs');
const { shoot, unmatched } = await import('./lib/shoot.mjs');

let web;
let browser;
let failed = false;
const teardown = async () => {
  try { await browser?.close(); } catch { /* closing */ }
  browser = undefined;
  try { await stopWeb(); } finally { web = undefined; scratch.cleanup(); }
};
for (const sig of ['SIGINT', 'SIGTERM']) process.once(sig, () => { teardown().finally(() => process.exit(130)); });

try {
  ensureWebModules();
  log(`starting the dashboard (next dev), log: ${scratch.log}`);
  web = await startWeb({ logFile: scratch.log });
  log(`dashboard up at ${web.base}`);
  browser = await loadPlaywright().chromium.launch({ args: ['--disable-lcd-text', '--font-render-hinting=none'] });
  mkdirSync(imgDir, { recursive: true });
  for (const shot of selected) {
    try {
      const png = await shoot(browser, web.base, shot, log);
      const { buffer, quality, tooBig } = await encode(png);
      const file = join(imgDir, `${shot.id}.webp`);
      // Same picture as the committed one (only noise differs): keep the committed bytes, so a re-run changes nothing.
      if (!flag('force') && existsSync(file) && (await looksUnchanged(readFileSync(file), buffer))) {
        log(`kept  ${shot.id}.webp  unchanged`);
        continue;
      }
      writeFileSync(file, buffer);
      log(`wrote ${shot.id}.webp  ${(buffer.length / 1024).toFixed(0)} KB (q${quality})${tooBig ? `  OVER the ${MAX_BYTES / 1024} KB budget` : ''}`);
      if (tooBig) failed = true;
    } catch (err) {
      // One broken shot must not hide the others: report it and carry on.
      console.error(`FAILED ${shot.id}: ${String(err.message ?? err).split('\n')[0]}`);
      failed = true;
    }
  }
  if (unmatched.size) {
    console.warn('\nUnmatched /api requests (answered 404 {}); add them to fixtures/respond.mjs if the shot needs them:');
    for (const u of [...unmatched].sort()) console.warn(`  ${u}`);
  }
} catch (err) {
  console.error(err);
  failed = true;
} finally {
  await teardown();
}
if (!failed && !only) {
  const problems = checkImages(shots);
  if (problems.length) { console.error(`\n${problems.join('\n')}`); failed = true; }
}
process.exit(failed ? 1 : 0);
