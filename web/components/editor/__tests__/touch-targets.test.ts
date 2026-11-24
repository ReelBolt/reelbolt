import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

const EDITOR = join(__dirname, '..');
const css = (rel: string) => readFileSync(join(EDITOR, rel), 'utf8');

/** The text of every `@media (pointer: coarse)` block (nested or top-level), brace-matched. */
function coarseBlocks(src: string): string {
  let out = '';
  let from = 0;
  for (;;) {
    const at = src.indexOf('(pointer: coarse)', from);
    if (at < 0) return out;
    const open = src.indexOf('{', at);
    let depth = 0;
    let i = open;
    for (; i < src.length; i++) {
      if (src[i] === '{') depth++;
      if (src[i] === '}' && --depth === 0) break;
    }
    out += src.slice(open, i + 1);
    from = i;
  }
}

describe('coarse-pointer targets are 44 px', () => {
  it('ui and audio icon buttons', () => {
    expect(coarseBlocks(css('audio/audio.module.css'))).toMatch(/min-height:\s*44px/);
    expect(coarseBlocks(css('ui/ui.module.css'))).toMatch(/44px/);
  });
  it('clip-depth chips (32 px fine)', () => {
    expect(coarseBlocks(css('panels/clip-depth/clip-depth.module.css'))).toMatch(/\.chip\s*{[^}]*44px/);
  });
  it('the pane-tabs more button (32 px fine) and the status bar', () => {
    const coarse = coarseBlocks(css('shell/workspace.module.css'));
    expect(coarse).toMatch(/\.paneTabsMore\s*{[^}]*44px/);
    expect(coarse).toMatch(/\.status\s*{[^}]*44px/);
  });
  it('uses no raw hex or gradient in the clip-depth thumbnails', () => {
    expect(css('panels/clip-depth/clip-depth.module.css')).not.toMatch(/#[0-9a-fA-F]{3,6}\b|gradient/);
  });
});
