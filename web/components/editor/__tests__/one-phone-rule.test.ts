import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

function walk(dir: string, out: string[] = []): string[] {
  for (const name of readdirSync(dir)) {
    if (name === '__tests__' || name === 'node_modules') continue;
    const p = join(dir, name);
    if (statSync(p).isDirectory()) walk(p, out);
    else if (/\.(tsx?|css)$/.test(name) && !/\.test\./.test(name)) out.push(p);
  }
  return out;
}

describe('one phone rule (W3-D1)', () => {
  it('no editor source decides "phone" with its own 48em / 767 px literal', () => {
    const root = resolve(process.cwd(), 'components/editor');
    const offenders = walk(root).filter((f) => /max-width:\s*(48em|767)/.test(readFileSync(f, 'utf8')));
    expect(offenders).toEqual([]);
  });
});
