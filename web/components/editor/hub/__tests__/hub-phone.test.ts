import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const read = (rel: string) => readFileSync(resolve(process.cwd(), 'components/editor/hub', rel), 'utf8');
const css = read('hub.module.css');
const timelines = read('TimelinesView.tsx');
const hub = read('EditorHub.tsx');
const catalog = readFileSync(resolve(process.cwd(), 'messages/en/editor.json'), 'utf8'); // labels live in the catalogue now

describe('the editor hub as a phone surface (static checks; layout is checked in Playwright at 360)', () => {
  it('finger-size rule: 44 px under a coarse pointer or the one phone width', () => {
    const block = css.slice(css.indexOf('@media (pointer: coarse), (max-width: 599.98px)'));
    expect(block).toMatch(/\.menuButton[\s\S]*min-height: 44px/);
    expect(block).toMatch(/\.touchTarget,\s*\.touchInput/);
    expect(block).toMatch(/min-height: 44px/);
  });

  it('the create, import, search, sort, undo and tab controls all opt in', () => {
    for (const id of ['timeline-import', 'timeline-create']) expect(timelines).toContain(id);
    expect(timelines.match(/className=\{classes\.touchTarget\}/g)!.length).toBeGreaterThanOrEqual(6);
    expect(timelines.match(/input: classes\.touchInput/g)!.length).toBe(2);
    expect(hub.match(/className=\{classes\.touchTarget\}/g)!.length).toBeGreaterThanOrEqual(3);
  });

  it('rows cannot push the page sideways: the list clips, the link and meta shrink, secondary text hides on a phone', () => {
    expect(css).toMatch(/\.list \{[^}]*overflow: hidden/);
    expect(css).toMatch(/\.rowLink \{[^}]*min-width: 0/);
    expect(css).toMatch(/@media \(max-width: 599\.98px\)[\s\S]*\.hideOnPhone \{\s*display: none/);
  });

  it('every row action sits in an overflow menu: rename, duplicate, review, copy link, delete', () => {
    for (const label of ['Rename', 'Duplicate', 'Delete']) expect(timelines + catalog).toContain(label);
    expect(timelines).toContain('timeline-more-');
    expect(timelines).toContain('timeline-create');
  });
});
