// node --test i18n/pick-locale.test.mjs
// The same vectors run against the canonical file and against both vendored copies, so a copy that
// drifts (or a hand edit of one) fails here as well as in `node i18n/sync.mjs --check`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const vectors = JSON.parse(readFileSync(join(root, 'i18n/pick-locale.vectors.json'), 'utf8'));
const registry = JSON.parse(readFileSync(join(root, 'i18n/locales.json'), 'utf8'));

const copies = ['i18n/pick-locale.js', 'site/functions/_pick-locale.js', 'nginx/pick-locale.js'];

for (const file of copies) {
  const { default: picker } = await import(join(root, file));
  const run = (set) => {
    for (const v of set.vectors) {
      test(`${file}: ${v.name}`, () => {
        assert.equal(picker.pickLocale(v.cookie, v.acceptLanguage, set.locales), v.expect);
      });
    }
  };
  run(vectors);
  run(vectors.threeLocales);
}

test('the vectors use the registered locales (default first)', () => {
  assert.deepEqual(vectors.locales, registry.locales.map((l) => l.code));
  assert.equal(vectors.locales[0], registry.defaultLocale);
});

test('the canonical file is ES5: no modern syntax njs would reject', () => {
  const src = readFileSync(join(root, 'i18n/pick-locale.js'), 'utf8')
    .split('\n')
    .filter((line) => !line.trim().startsWith('//'))
    .join('\n');
  assert.doesNotMatch(src, /=>/, 'arrow function');
  assert.doesNotMatch(src, /\b(let|const|class)\s/, 'let/const/class');
  assert.doesNotMatch(src, /`/, 'template string');
  assert.doesNotMatch(src, /\.(includes|startsWith|endsWith|padStart|find|findIndex)\(/, 'ES6+ method');
  assert.doesNotMatch(src, /\bfor\s*\(\s*(const|let)\b|\bof\b/, 'for-of');
});

// nginx/i18n.js wraps the picker for njs: '' stays on English, else '/<code>' plus the query string.
test('nginx/i18n.js targetFor redirects only non-default picks and keeps the query', async () => {
  const { default: i18n } = await import(join(root, 'nginx/i18n.js'));
  assert.equal(i18n.targetFor(null, 'it', '', ['en', 'it']), '/it');
  assert.equal(i18n.targetFor(null, 'it', 'utm_source=x', ['en', 'it']), '/it?utm_source=x');
  assert.equal(i18n.targetFor(null, 'en', 'utm_source=x', ['en', 'it']), '');
  assert.equal(i18n.targetFor('rb_locale=en', 'it', '', ['en', 'it']), '');
});
