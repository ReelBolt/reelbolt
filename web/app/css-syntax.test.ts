// @vitest-environment node
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import postcss from 'postcss';
import { describe, expect, it } from 'vitest';

// Guards against a merge that leaves an unclosed block or conflict marker in a stylesheet: that parses
// in no unit test, but fails `next build` (and so the web image) with "CssSyntaxError: Unclosed block".
const ROOTS = ['app', 'components'];

function cssFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    if (name === 'node_modules' || name.startsWith('.')) return [];
    const path = join(dir, name);
    if (statSync(path).isDirectory()) return cssFiles(path);
    return path.endsWith('.css') ? [path] : [];
  });
}

const files = ROOTS.flatMap((root) => cssFiles(join(__dirname, '..', root)));

describe('stylesheets parse', () => {
  it('finds the global stylesheet and the component modules', () => {
    expect(files.some((f) => f.endsWith(join('app', 'global.css')))).toBe(true);
    expect(files.length).toBeGreaterThan(10);
  });

  it.each(files.map((f) => [f.slice(join(__dirname, '..').length + 1), f]))('%s', (_name, file) => {
    const css = readFileSync(file, 'utf8');
    expect(css).not.toMatch(/^(<{7}|={7}|>{7})( |$)/m);
    expect(() => postcss.parse(css, { from: file })).not.toThrow();
    let open = 0;
    let close = 0;
    for (const ch of css.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(["'])(?:\\.|(?!\1).)*\1/g, '')) {
      if (ch === '{') open++;
      else if (ch === '}') close++;
    }
    expect(open).toBe(close);
  });
});

describe('touch targets (usability lows)', () => {
  it('gives portaled Menu items 44px rows on a coarse pointer, and drops the dead h1 class', () => {
    const css = readFileSync(join(__dirname, 'global.css'), 'utf8');
    expect(css).toMatch(/@media \(pointer: coarse\) \{\s*\.mantine-Menu-dropdown \.mantine-Menu-item \{\s*min-height: 44px;/);
    expect(css).not.toContain('visually-hidden-h1');
  });
});
