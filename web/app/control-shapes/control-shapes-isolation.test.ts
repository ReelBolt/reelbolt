/** @vitest-environment node */
/**
 * control-shape-restyle T-001: isolation drift guard for the preview route.
 *
 * Mirrors `app/spike/spike-isolation.test.ts`. The preview is throwaway evidence: it must be
 * deletable in one commit, and no product module may import it or link to it. The scan is over
 * source text by regex, deliberately: a false positive fails loudly and is fixed by rewording,
 * while a false negative silently defeats the guard.
 */

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

const HERE = dirname(fileURLToPath(import.meta.url)); // web/app/control-shapes
const WEB_ROOT = resolve(HERE, '..', '..'); // web

/** The product surface: nothing here may reach the preview. */
const PRODUCT_DIRS = [resolve(WEB_ROOT, 'app', '(app)'), resolve(WEB_ROOT, 'components')];

/** The throwaway preview. A path matching this prefix is forbidden to the product. */
const FORBIDDEN_PREFIX = resolve(WEB_ROOT, 'app', 'control-shapes');

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

const SPECIFIER_RE =
  /(?:\bfrom\s*|\bimport\s*\(\s*|\brequire\s*\(\s*)(['"])([^'"]+)\1/g;

function specifiersOf(source: string): string[] {
  const found: string[] = [];
  for (const m of source.matchAll(SPECIFIER_RE)) found.push(m[2]);
  return found;
}

function isForbidden(abs: string): boolean {
  const norm = resolve(abs);
  return norm === FORBIDDEN_PREFIX || norm.startsWith(FORBIDDEN_PREFIX + sep);
}

function resolveSpecifier(spec: string, fromFile: string): string | null {
  if (spec.startsWith('@/')) return resolve(WEB_ROOT, spec.slice(2));
  if (spec.startsWith('.')) return resolve(dirname(fromFile), spec);
  return null; // bare package specifier
}

const productFiles = PRODUCT_DIRS.flatMap(walk);

describe('the control-shapes preview cannot leak into the product surface', () => {
  it('actually found the product directories to scan (the guard is not vacuous)', () => {
    expect(PRODUCT_DIRS.every((d) => statSync(d).isDirectory())).toBe(true);
    expect(productFiles.length).toBeGreaterThan(50);
  });

  it('has no import edge from app/(app)/** or components/** into the preview route', () => {
    const offenders: string[] = [];
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

  it('has no link or route string pointing at the preview from the product surface', () => {
    const offenders: string[] = [];
    for (const file of productFiles) {
      const source = readFileSync(file, 'utf8');
      if (source.includes('control-shapes')) offenders.push(relative(WEB_ROOT, file));
    }
    expect(offenders).toEqual([]);
  });

  it('detects a violation when one is planted (the regex actually matches)', () => {
    const planted = [
      `import { x } from '@/app/control-shapes/page';`,
      `const m = await import('../app/control-shapes/page');`,
      `const p = require('@/app/control-shapes/control-shapes.module.css');`,
      `export { y } from '@/app/control-shapes/page';`,
    ];
    const fakeFile = resolve(WEB_ROOT, 'components', 'planted.ts');
    const hits = planted
      .flatMap((src) => specifiersOf(src))
      .map((spec) => resolveSpecifier(spec, fakeFile))
      .filter((abs) => abs && isForbidden(abs));
    expect(hits).toHaveLength(planted.length);
  });

  it('does not forbid the product surface from importing itself', () => {
    const fakeFile = resolve(WEB_ROOT, 'components', 'planted.ts');
    for (const spec of ['@/components/shell/AppShell', '../lib/api/client', 'react']) {
      const abs = resolveSpecifier(spec, fakeFile);
      expect(abs === null || !isForbidden(abs)).toBe(true);
    }
  });
});
