// Catalogue gates for the marketing site (mirrors web/): every registered language has the same
// files, the same keys and the same ICU shape as English, nothing is empty, and no message carries
// markup, an entity or a typed price. Adding a language needs no change here.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync } from 'node:fs';
import { join } from 'node:path';
import { parse } from '@formatjs/icu-messageformat-parser';
import {
  flatten,
  icuShape,
  isDirectory,
  localeCodes,
  messagesRoot,
  namespacesOf,
  readNamespace,
  registry,
} from './catalog-fixtures.ts';

const source = registry.defaultLocale;
const others = localeCodes.filter((c) => c !== source);
const sourceNamespaces = namespacesOf(source);

test('English, the source language, has catalogue files', () => {
  assert.ok(sourceNamespaces.length > 0, `messages/${source}/ has no <namespace>.json`);
});

test('every registered locale has catalogue files, the same set as English', () => {
  for (const locale of others) {
    assert.ok(isDirectory(join(messagesRoot, locale)), `messages/${locale}/ is missing (registered in i18n/locales.json)`);
    assert.deepEqual(namespacesOf(locale), sourceNamespaces, `messages/${locale}/ must hold exactly the files of messages/${source}/`);
  }
});

test('no catalogue directory exists for a language that is not registered', () => {
  for (const dir of readdirSync(messagesRoot)) {
    assert.ok(localeCodes.includes(dir), `messages/${dir}/ is not in i18n/locales.json`);
  }
});

for (const locale of others) {
  for (const ns of sourceNamespaces) {
    test(`${locale}/${ns}: same keys as ${source}, nothing missing or extra`, () => {
      const want = [...flatten(readNamespace(source, ns)).keys()].sort();
      const have = [...flatten(readNamespace(locale, ns)).keys()].sort();
      assert.deepEqual(
        { missing: want.filter((k) => !have.includes(k)), extra: have.filter((k) => !want.includes(k)) },
        { missing: [], extra: [] },
      );
    });

    test(`${locale}/${ns}: same ICU shape (arguments, plural/select kind, rich tags) as ${source}`, () => {
      const en = flatten(readNamespace(source, ns));
      const tr = flatten(readNamespace(locale, ns));
      for (const [key, value] of en) {
        const other = tr.get(key);
        if (typeof value !== 'string' || typeof other !== 'string') continue;
        assert.deepEqual(icuShape(other), icuShape(value), `${locale}/${ns}:${key}`);
      }
    });
  }
}

for (const locale of localeCodes) {
  for (const ns of namespacesOf(locale)) {
    test(`${locale}/${ns}: every value is a non-empty string that parses as ICU (plurals have 'other')`, () => {
      for (const [key, value] of flatten(readNamespace(locale, ns))) {
        assert.equal(typeof value, 'string', `${ns}:${key} must be a string`);
        assert.notEqual((value as string).trim(), '', `${ns}:${key} is empty`);
        // The parser requires an `other` clause on every plural/select and rejects malformed braces.
        assert.doesNotThrow(() => parse(value as string), `${locale}/${ns}:${key} is not valid ICU`);
      }
    });

    test(`${locale}/${ns}: no raw markup, no HTML entity, no typed price`, () => {
      for (const [key, value] of flatten(readNamespace(locale, ns))) {
        const text = value as string;
        // Rich text is `<name>...</name>` with a bare lowercase name, resolved by t.rich(); never attributes.
        const tags = text.match(/<[^>]*>/g) ?? [];
        for (const tag of tags) {
          assert.match(tag, /^<\/?[a-z][a-zA-Z0-9]*>$/, `${locale}/${ns}:${key} has markup ${tag}`);
        }
        assert.doesNotMatch(text, /&[a-zA-Z#0-9]+;/, `${locale}/${ns}:${key} has an HTML entity`);
        assert.doesNotMatch(text, /[$€£]\s?\d|\d\s?(USD|EUR|€|\$)/, `${locale}/${ns}:${key} hand-writes a price: prices come from the plan catalog`);
      }
    });
  }
}
