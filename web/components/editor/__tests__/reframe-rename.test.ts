// @vitest-environment node
/** R10.3: the Compose control is "Reframe" everywhere (UI, tests, docs, user guide); the old name must not come back. */
import { describe, expect, it } from 'vitest';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';

const root = path.resolve(__dirname, '../../..');
const SKIP = new Set(['node_modules', '.next', 'coverage', 'test-results']);

function* files(dir: string): Generator<string> {
  for (const name of readdirSync(dir)) {
    if (SKIP.has(name)) continue;
    const p = path.join(dir, name);
    const s = statSync(p);
    if (s.isDirectory()) yield* files(p);
    else if (/\.(tsx?|md|mdx|json)$/.test(name)) yield p;
  }
}

describe('reframe rename guard', () => {
  it('no "Auto-reframe" string is left in web/ or docs/user-guide', () => {
    const hits: string[] = [];
    for (const dir of [root, path.resolve(root, '../docs/user-guide')]) {
      for (const f of files(dir)) {
        if (f === __filename) continue;
        if (/auto-reframe/i.test(readFileSync(f, 'utf8'))) hits.push(path.relative(path.resolve(root, '..'), f));
      }
    }
    expect(hits).toEqual([]);
  });
});
