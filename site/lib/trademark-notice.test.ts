import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync, statSync } from 'node:fs';

import { CONTROLLER_NAME, TRADEMARK_STATEMENT } from './legal-placeholders.ts';
import { TRADEMARK_SYMBOL } from './trademark.ts';

// The ReelBolt word mark is an APPLICATION (filed 2026-10-27), not a registration. Until a
// registration is granted the site may use the unregistered symbol and must never use the
// registered-mark symbol.
const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), 'utf8');

test('the symbol is the unregistered-mark symbol', () => {
  assert.equal(TRADEMARK_SYMBOL, '™');
});

test('the statement follows the operator wording and names the owner', () => {
  assert.equal(TRADEMARK_STATEMENT, 'ReelBolt™ is a trademark of Tommaso Celano.');
  assert.ok(TRADEMARK_STATEMENT.includes(CONTROLLER_NAME));
});

test('the footer prints the statement and the header wordmark carries the symbol', () => {
  assert.match(read('../components/layout/SiteFooter.tsx'), /\{TRADEMARK_STATEMENT\}/);
  assert.match(read('../components/layout/SiteHeader.tsx'), /\{TRADEMARK_SYMBOL\}/);
});

test('the Terms carry a trademarks clause that says the application is pending', () => {
  const terms = read('../app/(en)/legal/terms/page.tsx');
  assert.match(terms, /\{TRADEMARK_STATEMENT\}/);
  assert.match(terms, /12\.1 Trademarks/);
  assert.match(terms, /is\s+pending;\s+no\s+registration\s+has\s+been\s+granted/);
  assert.match(terms, /Neither\s+grants\s+any\s+right\s+in\s+the[\s\S]*unless\s+we\s+agree\s+otherwise\s+in\s+writing/);
});

test('structured data keeps the brand name plain', () => {
  for (const file of ['structured-data.ts', 'seo.ts', 'site-config.ts']) {
    assert.doesNotMatch(read(file), /™|&trade;/, `${file} must not put the symbol in a name`);
  }
});

function walk(dir: URL, out: string[] = []): string[] {
  for (const name of readdirSync(dir)) {
    if (name === 'node_modules' || name === '.next' || name === 'public') continue;
    const path = new URL(name, dir);
    const info = statSync(path);
    if (info.isDirectory()) walk(new URL(`${name}/`, dir), out);
    else if (/\.(tsx?|mdx?)$/.test(name) && !name.endsWith('.test.ts')) out.push(path.pathname);
  }
  return out;
}

test('no site source uses the registered-mark symbol or leaks an application number', () => {
  for (const root of ['../app/', '../components/', '../content/', './']) {
    for (const file of walk(new URL(root, import.meta.url))) {
      const text = readFileSync(file, 'utf8');
      assert.doesNotMatch(text, /®|&reg;|&#174;/, `${file} uses the registered-mark symbol`);
      assert.doesNotMatch(text, /302026\d{6}/, `${file} contains a trademark application number`);
    }
  }
});
