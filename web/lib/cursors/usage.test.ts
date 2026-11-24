// @vitest-environment node
/**
 * The branded pointer set, as it is actually wired.
 *
 * `catalog.test.ts` guards the artwork contract. This file guards the *usage* contract from
 * docs/design/cursors.md:
 *  - the token block is the only place a cursor `url()` may appear, and every token ends in a
 *    standard keyword (so a blocked PNG or an unsupported browser degrades to today's cursor);
 *  - every `cursor:` declaration in the app is either one of those tokens or one of the deliberate
 *    native exceptions (I-beam in editable text, resize/zoom, help/inherit);
 *  - every asset a token points at exists under the served path.
 * A broken rule would fall back silently in a browser; here it fails loudly.
 */
import { readFileSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import catalog from '@/lib/cursors/catalog.json';

interface CursorSpec {
  name: string;
  file: string;
  at2x: string;
  hotspot: { x: number; y: number };
  fallback: string;
}

const webRoot = path.resolve(__dirname, '../..');
const cursors = (catalog as { cursors: CursorSpec[] }).cursors;
const globalCss = readFileSync(path.join(webRoot, 'app/global.css'), 'utf8');

/** A CSS cursor keyword the browser understands without an image. */
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

/**
 * Deliberate exceptions (Owner Phase-3 rule): the I-beam inside editable text, resize/zoom where
 * precision matters, and the OS help/inherit affordances. Everything else must be branded.
 */
const NATIVE_EXCEPTIONS = new Set([
  'text',
  'help',
  'inherit',
  'col-resize',
  'row-resize',
  'ew-resize',
  'ns-resize',
  'nwse-resize',
  'nesw-resize',
  'zoom-in',
  'zoom-out',
  'auto',
]);

const TOUCH_SURFACES = [/components\/editor\/phone\//, /components\/editor\/review\/MobileReview\.tsx$/];

const filesUnder = (dir: string, ext: string[]): string[] => {
  const out: string[] = [];
  for (const entry of readdirSync(dir)) {
    if (entry === 'node_modules' || entry === '.next' || entry === 'public') continue;
    const abs = path.join(dir, entry);
    if (statSync(abs).isDirectory()) out.push(...filesUnder(abs, ext));
    else if (ext.some((e) => abs.endsWith(e))) out.push(abs);
  }
  return out;
};

const sourceFiles = [...filesUnder(path.join(webRoot, 'app'), ['.css', '.tsx', '.ts']), ...filesUnder(path.join(webRoot, 'components'), ['.css', '.tsx', '.ts']), ...filesUnder(path.join(webRoot, 'lib'), ['.css', '.tsx', '.ts'])];

/** Every `--rb-cursor-*: ...` declaration inside the branded-token `:root` block of global.css. */
function tokenBlock(css: string): Map<string, string> {
  const anchor = css.indexOf('--rb-cursor-default: url(');
  expect(anchor).toBeGreaterThan(-1);
  const start = css.lastIndexOf(':root {', anchor);
  const end = css.indexOf('\n}', anchor);
  const block = css.slice(start, end);
  const tokens = new Map<string, string>();
  for (const line of block.split('\n')) {
    const m = /--rb-cursor-([a-z0-9-]+):\s*(.+);\s*$/.exec(line.trim());
    if (m) tokens.set(m[1], m[2]);
  }
  return tokens;
}

/** Every `--rb-cursor-x: keyword;` override in the coarse/forced-colors block of global.css. */
function coarseOverrides(css: string): Map<string, string> {
  const anchor = css.indexOf('--rb-cursor-default: default;');
  expect(anchor).toBeGreaterThan(-1);
  const start = css.lastIndexOf('@media (pointer: coarse)', anchor);
  const end = css.indexOf('\n  }\n}', anchor);
  const block = css.slice(start, end);
  const overrides = new Map<string, string>();
  for (const line of block.split('\n')) {
    const m = /--rb-cursor-([a-z0-9-]+):\s*([a-z-]+);/.exec(line.trim());
    if (m) overrides.set(m[1], m[2]);
  }
  return overrides;
}

const tokens = tokenBlock(globalCss);

describe('branded cursor tokens', () => {
  it('declares one token per catalog cursor, with the exact chain from the contract', () => {
    expect([...tokens.keys()].sort()).toEqual(cursors.map((c) => c.name).sort());
    for (const c of cursors) {
      const expected =
        `url('/app/cursors/${c.file}') ${c.hotspot.x} ${c.hotspot.y}, ` +
        `url('/app/cursors/${c.at2x}') ${c.hotspot.x * 2} ${c.hotspot.y * 2}, ${c.fallback}`;
      expect(tokens.get(c.name)).toBe(expected);
    }
  });

  it.each([...tokens.keys()])('%s ends in a standard keyword fallback', (name) => {
    const value = tokens.get(name)!;
    const keyword = value.slice(value.lastIndexOf(',') + 1).trim();
    expect(CSS_KEYWORDS.has(keyword)).toBe(true);
    expect(keyword).toBe(cursors.find((c) => c.name === name)!.fallback);
  });

  it('points every url() at an asset under the served /app path', () => {
    for (const value of tokens.values()) {
      for (const m of value.matchAll(/url\('([^']+)'\)/g)) {
        expect(m[1].startsWith('/app/cursors/')).toBe(true);
        const file = path.join(webRoot, 'public', m[1].replace('/app/', ''));
        expect(() => readFileSync(file)).not.toThrow();
      }
    }
  });

  it('reduces every token to its bare keyword on a coarse pointer or in forced colors', () => {
    const coarse = coarseOverrides(globalCss);
    expect([...coarse.keys()].sort()).toEqual([...tokens.keys()].sort());
    for (const [name, keyword] of coarse) {
      expect(keyword).toBe(cursors.find((c) => c.name === name)!.fallback);
    }
  });
});

function cursorDeclarations(file: string): { line: number; value: string }[] {
  const text = readFileSync(file, 'utf8');
  const declarations: { line: number; value: string }[] = [];
  text.split('\n').forEach((line, i) => {
    if (file.endsWith('.css')) {
      const m = /\bcursor\s*:\s*([^;]+);/.exec(line);
      if (m) declarations.push({ line: i + 1, value: m[1].trim() });
      return;
    }
    const m = /\bcursor['"]?\s*:\s*([^,}\n]+)/.exec(line);
    if (!m) return;
    for (const literal of m[1].matchAll(/['"]([^'"]+)['"]/g)) {
      declarations.push({ line: i + 1, value: literal[1] });
    }
  });
  return declarations;
}

describe('every cursor declaration', () => {
  it('is a branded token or a recorded native exception, and never a bare url()', () => {
    const bad: string[] = [];
    const cursorWords = new Set([...CSS_KEYWORDS, ...cursors.map((c) => c.name)]);
    for (const file of sourceFiles) {
      const rel = path.relative(webRoot, file);
      if (TOUCH_SURFACES.some((re) => re.test(rel))) continue;
      for (const { line, value } of cursorDeclarations(file)) {
        if (value.includes('url(')) {
          bad.push(`${rel}:${line} has a bare url() — use a token`);
          continue;
        }
        const token = /^var\(--rb-cursor-([a-z0-9-]+)\)$/.exec(value);
        if (token) {
          if (!tokens.has(token[1])) bad.push(`${rel}:${line} uses unknown token ${token[1]}`);
          continue;
        }
        // Expressions such as `tool === 'blade' ? ...` carry non-cursor string literals too.
        if (!cursorWords.has(value)) continue;
        if (!NATIVE_EXCEPTIONS.has(value)) bad.push(`${rel}:${line} is an un-branded "${value}"`);
      }
    }
    expect(bad).toEqual([]);
  });

  it('keeps the editor tool surfaces on their own cursors', () => {
    const canvas = readFileSync(path.join(webRoot, 'components/editor/timeline/TimelineCanvas.tsx'), 'utf8');
    expect(canvas).toContain('var(--rb-cursor-razor)');
    expect(canvas).toContain('var(--rb-cursor-pan)');
    expect(canvas).toContain('var(--rb-cursor-trim-left)');
    expect(canvas).toContain('var(--rb-cursor-trim-right)');

    const editorCanvas = readFileSync(path.join(webRoot, 'components/editor/thumbnail/EditorCanvas.tsx'), 'utf8');
    expect(editorCanvas).toContain('var(--rb-cursor-grabbing)');
    expect(editorCanvas).toContain('var(--rb-cursor-grab)');
    expect(editorCanvas).toContain('var(--rb-cursor-mask-brush)');
  });

  it('never brands a disabled Mantine menu item or combobox option as clickable', () => {
    // Mantine's disabled rules are class-specificity and this file loads after them, so the
    // pointer rule must exclude the disabled states and the not-allowed rule must cover them.
    expect(globalCss).toContain('.mantine-Menu-item:not([data-disabled]):not(:disabled)');
    expect(globalCss).toContain('.mantine-Combobox-option:not([data-combobox-disabled]):not([data-disabled]):not(:disabled)');
    expect(globalCss).toMatch(/\.mantine-Menu-item\[data-disabled\][\s\S]*?cursor: var\(--rb-cursor-not-allowed\)/);
    expect(globalCss).toMatch(/\.mantine-Combobox-option\[data-combobox-disabled\][\s\S]*?cursor: var\(--rb-cursor-not-allowed\)/);
  });

  it('leaves the variable-valued crop handles on native resize keywords', () => {
    // The eight crop handles take `cursor: h.cursor` from a constant map; the literal scanner
    // cannot see through the variable, so the map itself is asserted here.
    const crop = readFileSync(path.join(webRoot, 'components/editor/thumbnail/CropDialog.tsx'), 'utf8');
    const handleCursors = [...crop.matchAll(/id: '\w+'[^\n]*cursor: '([^']+)'/g)].map((m) => m[1]);
    expect(handleCursors).toHaveLength(8);
    for (const value of handleCursors) {
      expect(['ew-resize', 'ns-resize', 'nwse-resize', 'nesw-resize']).toContain(value);
    }
  });
});
