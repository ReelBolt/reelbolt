import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

describe('timeline canvas text keeps the 12 px floor', () => {
  it('every ctx.font literal in paint.ts, clipPaint.ts and eventsLane.ts is >= 12 px', () => {
    const src = ['paint.ts', 'clipPaint.ts', 'eventsLane.ts'].map((f) => readFileSync(join(__dirname, '..', f), 'utf8')).join('\n');
    const sizes = [...src.matchAll(/ctx\.font\s*=\s*['"`](?:bold |italic )?(\d+(?:\.\d+)?)px/g)].map((m) => Number(m[1]));
    expect(sizes.length).toBeGreaterThan(0);
    for (const s of sizes) expect(s).toBeGreaterThanOrEqual(12);
  });
});
