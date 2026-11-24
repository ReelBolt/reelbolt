import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

// A UTF-8 character read back as Latin-1 and saved again shows up as "â€" followed by one more character
// (an apostrophe becomes "â€™"). No blog post may contain that sequence.

const dir = join(import.meta.dirname, '..', '..', 'content', 'blog');

test('no blog post contains double-encoded (mojibake) text', () => {
  const files = readdirSync(dir).filter((f) => f.endsWith('.tsx'));
  assert.ok(files.length > 0, 'expected blog posts under content/blog');
  for (const file of files) {
    const lines = readFileSync(join(dir, file), 'utf8').split('\n');
    const bad = lines.flatMap((line, i) => (line.includes('â€') ? [`${file}:${i + 1}`] : []));
    assert.deepEqual(bad, [], `double-encoded characters in ${bad.join(', ')}`);
  }
});
