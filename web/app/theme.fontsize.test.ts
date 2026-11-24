import { readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import { findSmallFonts } from '../components/editor/__tests__/no-small-fonts';

const WEB = join(__dirname, '..');

// The editor test already guards components/editor and app/global.css; this extends the 12 px text floor to the files that
// carry the dashboard look (theme.ts, chrome and dashboard CSS modules, the projects toolbar).
describe('dashboard look keeps the 12 px text floor', () => {
  it('has no font size under 12 px in theme.ts and the chrome/dashboard/projects styles', () => {
    const files = ['app/theme.ts', 'components/shell/chrome.module.css', 'components/shell/BrandMark.module.css', 'components/dashboard/dashboard.module.css', 'components/projects/projects.module.css'];
    const bad: string[] = [];
    for (const f of files) {
      for (const v of findSmallFonts(readFileSync(join(WEB, f), 'utf8'))) bad.push(`${relative(WEB, join(WEB, f))}:${v.line}: ${v.text}`);
    }
    expect(bad).toEqual([]);
  });
});
