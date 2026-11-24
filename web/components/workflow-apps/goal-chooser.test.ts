import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { ALL_GOAL_TILES, GOAL_TILES } from './goal-chooser';

describe('goal chooser table', () => {
  it('has the eight goals, each once', () => {
    expect(ALL_GOAL_TILES).toHaveLength(8);
    expect(new Set(ALL_GOAL_TILES.map((g) => g.id)).size).toBe(8);
  });

  it('offers seven: the thumbnail tile stays hidden until a thumbnail app exists', () => {
    expect(GOAL_TILES).toHaveLength(7);
    expect(GOAL_TILES.map((g) => g.id)).not.toContain('thumbnail');
    expect(ALL_GOAL_TILES.find((g) => g.id === 'thumbnail')?.hidden).toBe(true);
  });

  it('never offers a tile that does nothing', () => {
    expect(GOAL_TILES.every((g) => g.target.kind === 'template')).toBe(true);
  });

  it('maps every template goal to a key in the template catalog', () => {
    const catalog = readFileSync(
      resolve(__dirname, '../../../inference/src/ReelBolt.Shared/Workflows/WorkflowTemplateCatalog.cs'),
      'utf8',
    );
    for (const g of GOAL_TILES) {
      if (g.target.kind !== 'template') continue;
      expect(catalog, g.target.key).toContain(`"${g.target.key}"`);
    }
  });
});
