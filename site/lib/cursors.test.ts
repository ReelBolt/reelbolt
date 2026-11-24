// The marketing site's branded pointer set.
//
// The site carries the UI subset only (default, pointer, text, not-allowed, progress, grab,
// grabbing, move) — the editor tool cursors are dashboard-only. The PNGs are masters under
// brand/cursors, copied into site/public/cursors by `node brand/scripts/sync.mjs`; the catalog's
// "site": true flag is the list, so a new cursor cannot half-arrive. Each token ends in a standard
// keyword: with images blocked the site behaves exactly as it did before the set existed.
//
// This test also pins the public-page safety rule: the site may only reference the copies it
// serves (/cursors/...), never a brand master path that does not exist in the site build.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

interface CursorSpec {
  name: string;
  group: 'ui' | 'editor';
  file: string;
  at2x: string;
  hotspot: { x: number; y: number };
  fallback: string;
  site?: boolean;
}

const siteRoot = path.resolve(fileURLToPath(new URL('..', import.meta.url)));
const repoRoot = path.resolve(siteRoot, '..');
const css = readFileSync(path.join(siteRoot, 'app/globals.css'), 'utf8');
const catalog = JSON.parse(readFileSync(path.join(repoRoot, 'brand/cursors/cursors.json'), 'utf8')) as {
  cursors: CursorSpec[];
};
const siteCursors = catalog.cursors.filter((c) => c.site === true);

const CSS_KEYWORDS = new Set([
  'auto',
  'default',
  'none',
  'context-menu',
  'help',
  'pointer',
  'progress',
  'wait',
  'cell',
  'crosshair',
  'text',
  'vertical-text',
  'alias',
  'copy',
  'move',
  'no-drop',
  'not-allowed',
  'grab',
  'grabbing',
  'all-scroll',
  'col-resize',
  'row-resize',
  'n-resize',
  'e-resize',
  's-resize',
  'w-resize',
  'ne-resize',
  'nw-resize',
  'se-resize',
  'sw-resize',
  'ew-resize',
  'ns-resize',
  'nesw-resize',
  'nwse-resize',
  'zoom-in',
  'zoom-out',
  'inherit',
]);

/** Deliberate native exceptions: I-beam in editable text, and the OS help/inherit affordances. */
const NATIVE_EXCEPTIONS = new Set(['text', 'help', 'inherit', 'auto']);

function tokenBlock(source: string): Map<string, string> {
  const anchor = source.indexOf('--rb-cursor-default: url(');
  assert.ok(anchor > -1, 'the site declares no --rb-cursor-default token');
  const start = source.lastIndexOf(':root {', anchor);
  const end = source.indexOf('\n}', anchor);
  const tokens = new Map<string, string>();
  for (const line of source.slice(start, end).split('\n')) {
    const m = /--rb-cursor-([a-z0-9-]+):\s*(.+);\s*$/.exec(line.trim());
    if (m) tokens.set(m[1], m[2]);
  }
  return tokens;
}

function coarseOverrides(source: string): Map<string, string> {
  const anchor = source.indexOf('--rb-cursor-default: default;');
  assert.ok(anchor > -1, 'the site declares no coarse-pointer token override');
  const start = source.lastIndexOf('@media (pointer: coarse)', anchor);
  const end = source.indexOf('\n  }\n}', anchor);
  const overrides = new Map<string, string>();
  for (const line of source.slice(start, end).split('\n')) {
    const m = /--rb-cursor-([a-z0-9-]+):\s*([a-z-]+);/.exec(line.trim());
    if (m) overrides.set(m[1], m[2]);
  }
  return overrides;
}

const tokens = tokenBlock(css);

test('the site carries exactly the UI subset the catalog marks for it', () => {
  assert.equal(siteCursors.length, 8);
  assert.deepEqual([...tokens.keys()].sort(), siteCursors.map((c) => c.name).sort());
});

test('every site token points at a served /cursors asset and ends in its keyword', () => {
  for (const cursor of siteCursors) {
    const value = tokens.get(cursor.name);
    assert.ok(value, `${cursor.name} has no token`);
    const expected =
      `url('/cursors/${cursor.file}') ${cursor.hotspot.x} ${cursor.hotspot.y}, ` +
      `url('/cursors/${cursor.at2x}') ${cursor.hotspot.x * 2} ${cursor.hotspot.y * 2}, ${cursor.fallback}`;
    assert.equal(value, expected);
    const keyword = value.slice(value.lastIndexOf(',') + 1).trim();
    assert.equal(keyword, cursor.fallback);
    assert.ok(CSS_KEYWORDS.has(keyword), `${cursor.name} falls back to an unknown keyword`);
    for (const m of value.matchAll(/url\('([^']+)'\)/g)) {
      assert.ok(m[1].startsWith('/cursors/'), `${cursor.name} references ${m[1]}`);
      readFileSync(path.join(siteRoot, 'public', m[1]), 'utf8');
    }
  }
});

test('touch and forced colors get no custom cursor: every site token collapses to its keyword', () => {
  const coarse = coarseOverrides(css);
  assert.deepEqual([...coarse.keys()].sort(), [...tokens.keys()].sort());
  for (const cursor of siteCursors) assert.equal(coarse.get(cursor.name), cursor.fallback);
});

function sourceFiles(dir: string): string[] {
  const out: string[] = [];
  for (const entry of readdirSync(dir)) {
    if (entry === 'node_modules' || entry === '.next' || entry === 'out' || entry === 'public') continue;
    const abs = path.join(dir, entry);
    if (statSync(abs).isDirectory()) out.push(...sourceFiles(abs));
    else if (/\.(css|tsx?)$/.test(abs)) out.push(abs);
  }
  return out;
}

test('every cursor rule is a token or a recorded exception, and never a bare url()', () => {
  const bad: string[] = [];
  const cursorWords = new Set([...CSS_KEYWORDS, ...catalog.cursors.map((c) => c.name)]);
  for (const file of [...sourceFiles(path.join(siteRoot, 'app')), ...sourceFiles(path.join(siteRoot, 'components')), ...sourceFiles(path.join(siteRoot, 'lib'))]) {
    const rel = path.relative(siteRoot, file);
    readFileSync(file, 'utf8')
      .split('\n')
      .forEach((line, i) => {
        const raw = file.endsWith('.css')
          ? /\bcursor\s*:\s*([^;]+);/.exec(line)
          : /\bcursor['"]?\s*:\s*([^,}\n]+)/.exec(line);
        if (!raw) return;
        const values = file.endsWith('.css')
          ? [raw[1].trim()]
          : [...raw[1].matchAll(/['"]([^'"]+)['"]/g)].map((m) => m[1]);
        for (const value of values) {
          if (value.includes('url(')) {
            bad.push(`${rel}:${i + 1} has a bare url() — use a token`);
            continue;
          }
          const token = /^var\(--rb-cursor-([a-z0-9-]+)\)$/.exec(value);
          if (token) {
            if (!tokens.has(token[1])) bad.push(`${rel}:${i + 1} uses unknown token ${token[1]}`);
            continue;
          }
          // Expressions carry non-cursor string literals too; only real cursor words are judged.
          if (!cursorWords.has(value)) continue;
          if (!NATIVE_EXCEPTIONS.has(value)) bad.push(`${rel}:${i + 1} is an un-branded "${value}"`);
        }
      });
  }
  assert.deepEqual(bad, []);
});

test('no stock Tailwind cursor utility survives, and every brand utility is in the subset', () => {
  const self = path.resolve(fileURLToPath(import.meta.url));
  const stock = /(?<![-\w])cursor-(?:pointer|default|not-allowed|progress|grab|grabbing|move|text|crosshair)\b/g;
  const brand = /\bcursor-brand-([a-z-]+)\b/g;
  const bad: string[] = [];
  for (const file of [...sourceFiles(path.join(siteRoot, 'app')), ...sourceFiles(path.join(siteRoot, 'components')), ...sourceFiles(path.join(siteRoot, 'lib'))]) {
    if (file === self) continue; // this file names the stock utilities on purpose
    const rel = path.relative(siteRoot, file);
    const text = readFileSync(file, 'utf8');
    for (const m of text.matchAll(stock)) bad.push(`${rel} uses stock ${m[0]}`);
    for (const m of text.matchAll(brand)) {
      if (!tokens.has(m[1])) bad.push(`${rel} uses unknown ${m[0]}`);
    }
  }
  assert.deepEqual(bad, []);
});

test('the site references only assets it serves, never a brand master or a dashboard copy', () => {
  const self = path.resolve(fileURLToPath(import.meta.url));
  const bad: string[] = [];
  for (const file of sourceFiles(siteRoot)) {
    if (file === self) continue; // this file names the forbidden paths on purpose
    const rel = path.relative(siteRoot, file);
    const text = readFileSync(file, 'utf8');
    for (const m of text.matchAll(/url\(\s*['"]?([^'")]+)['"]?\s*\)/g)) {
      const ref = m[1].trim();
      if (ref.startsWith('/')) {
        if (ref.includes('brand/') || ref.startsWith('/app/')) bad.push(`${rel} references ${ref}`);
      } else if (ref.includes('..')) {
        bad.push(`${rel} references ${ref}`);
      }
    }
    for (const m of text.matchAll(/(?:from|import)\s+['"]([^'"]+)['"]/g)) {
      if (m[1].includes('brand/') || m[1].includes('web/')) bad.push(`${rel} imports ${m[1]}`);
    }
  }
  assert.deepEqual(bad, []);
});
