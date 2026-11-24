import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import en from '@/messages/en/editor.json';
import it_ from '@/messages/it/editor.json';
import enColor from '@/messages/en/editor-color.json';
import itColor from '@/messages/it/editor-color.json';
import { WORKSPACES } from '../shell/workspaces';

const root = join(process.cwd());
const globalCss = readFileSync(join(root, 'app/global.css'), 'utf8');
const moduleCss = readFileSync(join(root, 'components/editor/shell/workspace.module.css'), 'utf8');

/** WCAG relative luminance and contrast for two #rrggbb colours. */
function luminance(hex: string): number {
  const c = hex.replace('#', '');
  const [r, g, b] = [0, 2, 4].map((i) => {
    const v = parseInt(c.slice(i, i + 2), 16) / 255;
    return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}
const contrast = (a: string, b: string) => {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
};

/** The declarations of one CSS rule (the first `selector { ... }` block). */
function rule(selector: string): string {
  const start = moduleCss.indexOf(`${selector} {`);
  expect(start, `${selector} is defined`).toBeGreaterThanOrEqual(0);
  return moduleCss.slice(start, moduleCss.indexOf('}', start));
}

describe('T-002 page label token and copy', () => {
  it('defines --rb-ed-fg-muted with the light and dark values, and meets 4.5:1 on both surfaces', () => {
    expect(globalCss).toContain('--rb-ed-fg-muted: #495057;');
    expect(globalCss).toContain('--rb-ed-fg-muted: #c1c4cc;');
    expect(contrast('#495057', '#ffffff')).toBeGreaterThanOrEqual(4.5);
    expect(contrast('#c1c4cc', '#12141a')).toBeGreaterThanOrEqual(4.5);
  });

  it('uses the token for inactive page labels in the band and the rail, never Mantine dimmed', () => {
    for (const selector of ['.switcherItem', '.railItem']) {
      const body = rule(selector);
      expect(body, selector).toContain('var(--rb-ed-fg-muted)');
      expect(body, selector).not.toContain('--mantine-color-dimmed');
    }
  });

  it('the desktop band labels are never hidden by a width rule', () => {
    expect(moduleCss).not.toMatch(/\.switcherLabel[^{]*\{[^}]*display:\s*none/);
    expect(moduleCss).toContain('.pageBand');
  });

  it('renames the English page to Colour; the Italian label stays Colore', () => {
    expect(en.shell.workspaceBar.names.color.label).toBe('Colour');
    expect(it_.shell.workspaceBar.names.color.label).toBe('Colore');
    expect(enColor.workspace.title).toBe('Colour');
    expect(itColor.workspace.title).toBe('Colore');
    expect(WORKSPACES.find((w) => w.id === 'color')?.label).toBe('Colour');
  });

  it('keeps the page order the workflow names', () => {
    expect(WORKSPACES.map((w) => w.id)).toEqual(['media', 'cut', 'edit', 'compose', 'color', 'audio', 'deliver']);
  });
});
