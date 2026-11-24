import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import { findSmallCssTargets, findSmallMantineTargets } from './touch-targets';

const WEB = join(__dirname, '..', '..', '..');
const EDITOR = join(WEB, 'components', 'editor');

function phoneFiles(ext: RegExp): string[] {
  const dir = join(EDITOR, 'phone');
  return readdirSync(dir)
    .filter((n) => ext.test(n))
    .map((n) => join(dir, n));
}

/** The phone surface: the phone folder, the phone Deliver variant and the thumbnail phone shell. */
const TSX_FILES = [
  ...phoneFiles(/\.tsx$/),
  join(EDITOR, 'deliver', 'RenderSettings.tsx'),
  join(EDITOR, 'deliver', 'FilmCard.tsx'),
  join(EDITOR, 'deliver', 'DeliverWorkspace.tsx'),
  // The picture editor's panels render inside the shared BottomSheet, whose CSS lifts every Mantine control to 44 px
  // (asserted below). What sits outside the sheet is scanned here.
  join(EDITOR, 'thumbnail', 'ThumbnailEditor.tsx'),
  join(EDITOR, 'thumbnail', 'ResultBar.tsx'),
];
const CSS_FILES = [...phoneFiles(/\.css$/), join(EDITOR, 'thumbnail', 'ThumbnailEditor.module.css')];

describe('touch-target scanner', () => {
  it('flags seeded small controls', () => {
    expect(findSmallCssTargets('.a { width: 30px; height: 44px; }')).toHaveLength(1);
    expect(findSmallCssTargets('.a { min-height: 36px; }')).toHaveLength(1);
    expect(findSmallCssTargets('@media (x) { .b { height: 20px; } }')).toHaveLength(1);
    expect(findSmallMantineTargets('<ActionIcon w={32} h={32}>')).toHaveLength(1);
    expect(findSmallMantineTargets('<Button size="xs">go</Button>')).toHaveLength(1);
    expect(findSmallMantineTargets('<ActionIcon size={28} />')).toHaveLength(1);
  });
  it('accepts 44 px controls and decoration', () => {
    expect(findSmallCssTargets('.a { width: 44px; min-height: 48px; }')).toHaveLength(0);
    expect(findSmallCssTargets('.grabberPill { width: 40px; height: 4px; }')).toHaveLength(0);
    expect(findSmallCssTargets('.rule { height: 1px; }')).toHaveLength(0);
    expect(findSmallMantineTargets('<ActionIcon w={44} h={44}>')).toHaveLength(0);
    expect(findSmallMantineTargets('<Button size="xs" h={44}>go</Button>')).toHaveLength(0);
    expect(findSmallMantineTargets("<Button size=\"xs\" style={{ minHeight: 44 }}>go</Button>")).toHaveLength(0);
  });
});

describe('the phone surface keeps 44 px touch targets', () => {
  it('has no control under 44 px in the phone CSS', () => {
    const bad: string[] = [];
    for (const f of CSS_FILES) for (const v of findSmallCssTargets(readFileSync(f, 'utf8'))) bad.push(`${relative(WEB, f)}:${v.line}: ${v.text}`);
    expect(bad).toEqual([]);
  });
  it('has no small Mantine control in the phone sheets and the phone Deliver', () => {
    const bad: string[] = [];
    for (const f of TSX_FILES) for (const v of findSmallMantineTargets(readFileSync(f, 'utf8'))) bad.push(`${relative(WEB, f)}:${v.line}: ${v.text}`);
    expect(bad).toEqual([]);
  });
  it('lifts every Mantine control inside the shared sheet and the phone shell to 44 px', () => {
    const css = readFileSync(join(EDITOR, 'phone', 'phone.module.css'), 'utf8');
    for (const scope of ['.sheet', '.shell']) {
      const block = new RegExp(`${scope.replace('.', '\\.')} :is\\([^{]*mantine-Button-root[^{]*mantine-Input-input[^{]*mantine-SegmentedControl-root[^{]*\\{[^}]*min-height:\\s*44px`);
      expect(css).toMatch(block);
    }
    expect(css).toMatch(/mantine-Switch-root[^{]*\{[^}]*min-height:\s*44px/);
  });
});
