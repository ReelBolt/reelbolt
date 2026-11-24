import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { CLIP_FALLBACK, type ClipScheme } from '../editorTheme';

// global.css is the single source of the clip colours; CLIP_FALLBACK must mirror it exactly.
const css = readFileSync(join(__dirname, '..', '..', '..', '..', 'app', 'global.css'), 'utf8');

function block(scheme: ClipScheme): string {
  const start = scheme === 'dark' ? css.indexOf("[data-mantine-color-scheme='dark']") : 0;
  const first = css.indexOf('--rb-clip-video', start);
  return css.slice(first, css.indexOf('}', first));
}

describe('clip colours have one source', () => {
  for (const scheme of ['light', 'dark'] as const) {
    it(`${scheme} fallback table equals the --rb-clip-* custom properties`, () => {
      const declared = Object.fromEntries([...block(scheme).matchAll(/--rb-clip-([a-z]+):\s*(#[0-9a-fA-F]{6})/g)].map((m) => [m[1], m[2].toLowerCase()]));
      expect(declared).toEqual(CLIP_FALLBACK[scheme]);
    });
  }
});
