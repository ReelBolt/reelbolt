import { readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import { findSmallFonts } from './no-small-fonts';

const WEB = join(__dirname, '..', '..', '..');

function walk(dir: string, out: string[] = []): string[] {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) {
      if (name === '__tests__' || name === 'node_modules') continue;
      walk(p, out);
    } else if (/\.(tsx?|css)$/.test(name) && !/\.test\.tsx?$/.test(name)) out.push(p);
  }
  return out;
}

describe('no-small-fonts scanner', () => {
  it('flags seeded violations', () => {
    expect(findSmallFonts('<Text size="10px">x</Text>')).toHaveLength(1);
    expect(findSmallFonts('style={{ fontSize: 11 }}')).toHaveLength(1);
    expect(findSmallFonts("style={{ fontSize: '9px' }}")).toHaveLength(1);
    expect(findSmallFonts('.a { font-size: 10px; }')).toHaveLength(1);
    expect(findSmallFonts('.a { font-size: 0.7rem; }')).toHaveLength(1);
    expect(findSmallFonts("ctx.font = '10px system-ui'")).toHaveLength(1);
    expect(findSmallFonts('<Badge size="xs" variant="light">x</Badge>')).toHaveLength(1);
  });
  it('accepts 12 px and above', () => {
    expect(findSmallFonts('<Text size="xs">x</Text>')).toHaveLength(0);
    expect(findSmallFonts('style={{ fontSize: 12 }}')).toHaveLength(0);
    expect(findSmallFonts('<Badge size="xs" fz="xs">x</Badge>')).toHaveLength(0);
    expect(findSmallFonts('.a { font-size: 0.75rem; }')).toHaveLength(0);
    expect(findSmallFonts("ctx.font = 'bold 12px system-ui'")).toHaveLength(0);
  });
});

describe('editor sources keep the 12 px floor', () => {
  it('has no font size under 12 px in components/editor or app/global.css', () => {
    const files = [...walk(join(WEB, 'components', 'editor')), join(WEB, 'app', 'global.css')];
    const bad: string[] = [];
    for (const f of files) {
      for (const v of findSmallFonts(readFileSync(f, 'utf8'))) bad.push(`${relative(WEB, f)}:${v.line}: ${v.text}`);
    }
    expect(bad).toEqual([]);
  });
});

/**
 * The rest of web/ (VF2 widened, goal-chooser-and-app-navigation T-002). Known offenders are checked in at
 * no-small-fonts.baseline.json as `path: count`. A new offender fails, and so does a baseline entry that was
 * fixed (or shrank): lower the number or delete the entry so the list only ever shrinks.
 * Regenerate with `UPDATE_FONT_BASELINE=1 npx vitest run components/editor/__tests__/no-small-fonts.test.ts`.
 */
describe('the rest of web/ against the checked-in baseline', () => {
  it('has no new small font and no stale baseline entry', () => {
    const baselinePath = join(__dirname, 'no-small-fonts.baseline.json');
    const found: Record<string, number> = {};
    const roots = ['app', 'components', 'lib'].map((d) => join(WEB, d));
    for (const f of roots.flatMap((r) => walk(r))) {
      const rel = relative(WEB, f).split('\\').join('/');
      if (rel.startsWith('components/editor/') || rel === 'app/global.css') continue;
      const n = findSmallFonts(readFileSync(f, 'utf8')).length;
      if (n > 0) found[rel] = n;
    }
    if (process.env.UPDATE_FONT_BASELINE === '1') {
      const sorted = Object.fromEntries(Object.entries(found).sort(([a], [b]) => a.localeCompare(b)));
      writeFileSync(baselinePath, `${JSON.stringify(sorted, null, 2)}\n`);
    }
    const baseline = JSON.parse(readFileSync(baselinePath, 'utf8')) as Record<string, number>;
    const added = Object.entries(found).filter(([f, n]) => n > (baseline[f] ?? 0)).map(([f, n]) => `${f}: ${n} (baseline ${baseline[f] ?? 0})`);
    const fixed = Object.entries(baseline).filter(([f, n]) => (found[f] ?? 0) < n).map(([f, n]) => `${f}: ${found[f] ?? 0} (baseline ${n}): lower or remove it`);
    expect({ added, fixed }).toEqual({ added: [], fixed: [] });
  });
});
