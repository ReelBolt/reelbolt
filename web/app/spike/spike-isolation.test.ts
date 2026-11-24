/** @vitest-environment node */
/**
 * T-200 — spike-isolation drift guard.  REMOVABLE (see
 * `lib/editing/preview/spike/README.md`).
 *
 * Acceptance criterion 6: "no module under `web/app/(app)/**` or `web/components/**`
 * imports anything under `web/lib/editing/preview/spike/**` or `web/app/spike/**` — the
 * harness cannot leak into the product surface."
 *
 * The harness is throwaway: it carries a committed 3.2 MB fixture, two demuxer dependencies
 * that exist only to be compared, Node-only shims and a static server. All of it must be
 * deletable in one commit without touching the product. An import edge from the product
 * surface would make that false, and — because nothing about a stray import fails loudly —
 * it would only be discovered at deletion time. Hence a test rather than a convention.
 *
 * The scan is over SOURCE TEXT, by regex, and deliberately so:
 *   - An AST-based check would need a TS/JSX parser dependency for a throwaway harness.
 *   - Regex can over-report (a path inside a comment or a string), which is the safe
 *     direction: a false positive fails loudly and is fixed by rewording, whereas a false
 *     negative silently defeats the guard.
 *
 * Both `@/...` alias imports and relative imports are covered. Relative imports are
 * resolved against the importing file so `../../lib/editing/preview/spike/x` is caught too.
 */

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

const HERE = dirname(fileURLToPath(import.meta.url)); // web/app/spike
const WEB_ROOT = resolve(HERE, '..', '..'); // web

/** The product surface: nothing here may reach the harness. */
const PRODUCT_DIRS = [resolve(WEB_ROOT, 'app', '(app)'), resolve(WEB_ROOT, 'components')];

/** The throwaway harness. A path matching either prefix is forbidden to the product. */
const FORBIDDEN_PREFIXES = [
  resolve(WEB_ROOT, 'lib', 'editing', 'preview', 'spike'),
  resolve(WEB_ROOT, 'app', 'spike'),
];

const SOURCE_EXT = ['.ts', '.tsx', '.js', '.jsx', '.mjs', '.cjs'];

function walk(dir: string): string[] {
  const out: string[] = [];
  let entries;
  try {
    entries = readdirSync(dir);
  } catch {
    return out; // directory absent — the guard is vacuous, which the test below asserts
  }
  for (const name of entries) {
    if (name === 'node_modules' || name === '.next') continue;
    const abs = resolve(dir, name);
    const st = statSync(abs);
    if (st.isDirectory()) out.push(...walk(abs));
    else if (SOURCE_EXT.some((e) => name.endsWith(e))) out.push(abs);
  }
  return out;
}

/** Every module specifier in a file, from `import`/`export ... from`/`require`/dynamic import. */
const SPECIFIER_RE =
  /(?:\bfrom\s*|\bimport\s*\(\s*|\brequire\s*\(\s*)(['"])([^'"]+)\1/g;

function specifiersOf(source: string): string[] {
  const found: string[] = [];
  for (const m of source.matchAll(SPECIFIER_RE)) found.push(m[2]);
  return found;
}

function isForbidden(abs: string): boolean {
  const norm = resolve(abs);
  return FORBIDDEN_PREFIXES.some((p) => norm === p || norm.startsWith(p + sep));
}

/** Resolve a specifier to an absolute path (no extension probing — prefix tests suffice). */
function resolveSpecifier(spec: string, fromFile: string): string | null {
  if (spec.startsWith('@/')) return resolve(WEB_ROOT, spec.slice(2));
  if (spec.startsWith('.')) return resolve(dirname(fromFile), spec);
  return null; // bare package specifier
}

const productFiles = PRODUCT_DIRS.flatMap(walk);

describe('the spike harness cannot leak into the product surface', () => {
  it('actually found the product directories to scan (the guard is not vacuous)', () => {
    // Without this, a renamed directory would turn the whole guard into a no-op that still
    // reports green — the exact failure mode this file exists to prevent.
    expect(PRODUCT_DIRS.every((d) => statSync(d).isDirectory())).toBe(true);
    expect(productFiles.length).toBeGreaterThan(50);
  });

  it('has no import edge from app/(app)/** or components/** into the spike harness', () => {
    const offenders = [];
    for (const file of productFiles) {
      const source = readFileSync(file, 'utf8');
      for (const spec of specifiersOf(source)) {
        const abs = resolveSpecifier(spec, file);
        if (abs && isForbidden(abs)) {
          offenders.push(`${relative(WEB_ROOT, file)} imports "${spec}"`);
        }
      }
    }
    expect(offenders).toEqual([]);
  });

  it('detects a violation when one is planted (the regex actually matches)', () => {
    // Positive control. The rule above only proves the ABSENCE of a match; without this,
    // a regex that matches nothing at all would also report green.
    const planted = [
      `import { x } from '@/lib/editing/preview/spike/harness.mjs';`,
      // Relative specifiers resolve against the importing file's DIRECTORY, so from
      // `components/planted.ts` one `..` reaches `web/` and two would leave `web/` entirely.
      `const m = await import('../app/spike/page');`,
      `const s = require('../lib/editing/preview/spike/serve.mjs');`,
      `export { y } from '@/app/spike/page';`,
    ];
    const fakeFile = resolve(WEB_ROOT, 'components', 'planted.ts');
    const hits = planted
      .flatMap((src) => specifiersOf(src))
      .map((spec) => resolveSpecifier(spec, fakeFile))
      .filter((abs) => abs && isForbidden(abs));
    expect(hits).toHaveLength(planted.length);
  });

  it('does not forbid the product surface from importing itself', () => {
    // Negative control for the resolver: a normal product import must NOT be flagged.
    const fakeFile = resolve(WEB_ROOT, 'components', 'planted.ts');
    for (const spec of ['@/components/shell/AppShell', '../lib/api/client', 'react']) {
      const abs = resolveSpecifier(spec, fakeFile);
      expect(abs === null || !isForbidden(abs)).toBe(true);
    }
  });
});
