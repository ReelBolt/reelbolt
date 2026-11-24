import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

describe('activity indicator touch target', () => {
  const css = readFileSync(join(__dirname, '..', 'activity.module.css'), 'utf8');

  it('outranks the editor 24 px ActionIcon floor with a 44 px coarse-pointer target', () => {
    const block = /@media \(pointer: coarse\) \{\s*:global\(\[data-editor-surface\]\) \.tapTarget:global\(\.mantine-ActionIcon-root\) \{([^}]*)\}/.exec(css);
    expect(block, 'an editor-surface coarse rule exists').not.toBeNull();
    expect(block![1]).toMatch(/min-height:\s*44px/);
    expect(block![1]).toMatch(/min-width:\s*44px/);
  });
});
