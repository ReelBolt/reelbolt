// Copies brand masters into every app location that must match them byte for byte, and verifies the
// one pack that is generated rather than copied.
// Each Docker build context is its own folder (./site, ./web, ./docs-site), so assets have to be
// copied in rather than referenced. No dependencies: plain node.
//
//   node brand/scripts/sync.mjs           copy masters to every target (and regenerate a stale pack)
//   node brand/scripts/sync.mjs --check   exit 1 if any target is missing or differs (CI gate)
//
// Targets listed here are the complete set; inline JSX/CSS copies of the mark are NOT synced
// (see docs/brand.md "Files that intentionally differ").
//
// The desktop runner's icons (runner/assets/icons) are not copies of a master — they are rasters
// and .ico containers produced from one by brand/scripts/runner-icons.mjs, together with the
// icons.manifest.json that records which masters and which bytes they came from. This script
// verifies that record instead of comparing bytes against an SVG, so a brand change that was never
// regenerated (and a pack someone edited by hand) both fail the check. Verification only hashes, so
// the CI gate still needs no npm install.
import { createHash } from 'node:crypto';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const brandDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = path.resolve(brandDir, '..');

/** master (relative to brand/) -> targets (relative to repo root) */
const MAP = {
  'app-icon.svg': [
    'site/assets/logo.svg',
    'site/app/icon.svg',
    'web/assets/logo.svg',
    'web/app/icon.svg',
    'web/public/logo.svg',
    'docs-site/static/img/logo.svg',
    'runner/frontend/src/assets/logo.svg',
  ],
  'app-icon-maskable.svg': ['site/assets/logo-maskable.svg'],
};

// Cursor artwork is rendered from the SVGs in brand/cursors/src by brand/scripts/cursors.mjs; the
// PNG masters and the public copies are listed here so a new cursor needs no change to this file.
// The dashboard's contact sheet reads the catalog from web/lib/cursors/catalog.json. The marketing
// site carries the UI subset only; the catalog's "site": true marks exactly which cursors cross
// into site/public (docs/design/cursors.md).
const cursorCatalog = JSON.parse(
  await readFile(path.join(brandDir, 'cursors', 'cursors.json'), 'utf8'),
);
for (const cursor of cursorCatalog.cursors) {
  for (const file of [cursor.file, cursor.at2x]) {
    const targets = [`web/public/cursors/${file}`];
    if (cursor.site === true) targets.push(`site/public/cursors/${file}`);
    MAP[`cursors/${file}`] = targets;
  }
}
MAP['cursors/cursors.json'] = ['web/lib/cursors/catalog.json'];

/** pack (relative to repo root) -> the script that generates it and the manifest it records */
const GENERATED = {
  'runner/assets/icons': {
    script: 'brand/scripts/runner-icons.mjs',
    manifest: 'icons.manifest.json',
  },
  'brand/cursors': {
    script: 'brand/scripts/cursors.mjs',
    manifest: 'cursors.manifest.json',
  },
};

const sha256 = (buf) => createHash('sha256').update(buf).digest('hex');
const check = process.argv.includes('--check');
let drift = 0;

// Repair generated packs BEFORE the copy pass: a master regenerated here has to reach its copies
// in the same run, or a writing-mode sync would leave a stale public copy behind (caught only by
// the next --check). Only the packs that actually drifted are regenerated — running every
// generator would rewrite an unrelated pack whose bytes differ solely by encoder/build.
let problems = await generatedDrift();
if (problems.length && !check) {
  for (const [pack, entry] of Object.entries(GENERATED)) {
    if ((await packDrift(pack, entry)).length === 0) continue;
    const run = spawnSync(process.execPath, [path.join(repoRoot, entry.script)], { stdio: 'inherit' });
    if (run.status !== 0) {
      console.error(`${entry.script} failed (exit ${run.status})`);
      process.exit(1);
    }
  }
  problems = await generatedDrift();
}

for (const [master, targets] of Object.entries(MAP)) {
  const want = await readFile(path.join(brandDir, master));
  for (const t of targets) {
    const abs = path.join(repoRoot, t);
    let have = null;
    try {
      have = await readFile(abs);
    } catch {
      // missing
    }
    const same = have !== null && have.equals(want);
    if (same) continue;
    if (check) {
      drift++;
      console.error(`DRIFT  ${t}  (${have === null ? 'missing' : 'differs'} from brand/${master})`);
    } else {
      await mkdir(path.dirname(abs), { recursive: true });
      await writeFile(abs, want);
      console.log(`wrote  ${t}`);
    }
  }
}

/** One line per way one generated pack has drifted from the masters and bytes it records. */
async function packDrift(pack, { script, manifest: manifestName }) {
  const problems = [];
  let manifest;
  try {
    manifest = JSON.parse(await readFile(path.join(repoRoot, pack, manifestName), 'utf8'));
  } catch {
    problems.push(`${pack}/${manifestName} is missing — run node ${script}`);
    return problems;
  }
  for (const [master, want] of Object.entries(manifest.sources ?? {})) {
    let have = null;
    try {
      have = sha256(await readFile(path.join(brandDir, master)));
    } catch {
      // master removed
    }
    if (have !== want) {
      problems.push(
        `${pack} was generated from a different brand/${master} — run node ${script}`,
      );
    }
  }
  for (const output of manifest.outputs ?? []) {
    let have = null;
    try {
      have = sha256(await readFile(path.join(repoRoot, pack, output.file)));
    } catch {
      // missing
    }
    if (have === null) {
      problems.push(`${pack}/${output.file} is missing — run node ${script}`);
    } else if (have !== output.sha256) {
      problems.push(
        `${pack}/${output.file} is not the file ${script} wrote — regenerate it`,
      );
    }
  }
  return problems;
}

/** Every generated pack's drift, flattened. */
async function generatedDrift() {
  const problems = [];
  for (const [pack, entry] of Object.entries(GENERATED)) {
    problems.push(...(await packDrift(pack, entry)));
  }
  return problems;
}

for (const p of problems) {
  drift++;
  console.error(`DRIFT  ${p}`);
}

if (check) {
  if (drift) {
    console.error(`\n${drift} file(s) out of sync. Run: node brand/scripts/sync.mjs`);
    process.exit(1);
  }
  console.log('brand: all synced targets match the masters');
}
