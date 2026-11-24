import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

describe('Colour scopes splitter', () => {
  const css = readFileSync(join(__dirname, '..', 'colour-depth.module.css'), 'utf8');

  it('shows a focus ring for the keyboard instead of removing the outline', () => {
    const block = /\.scopesResize:focus-visible\s*\{([^}]*)\}/.exec(css);
    expect(block, 'a focus-visible rule exists').not.toBeNull();
    expect(block![1]).toMatch(/outline:\s*2px solid/);
    expect(block![1]).not.toMatch(/outline:\s*none/);
  });

  it('has no touch band: the panel draws the splitter for a fine pointer only', () => {
    expect(css).not.toMatch(/\.scopesResize::after/);
    const panel = readFileSync(join(__dirname, '..', 'ScopesPanel.tsx'), 'utf8');
    expect(panel).toMatch(/open && !coarse && \(/);
  });
});
