// A key nobody reads is dead weight in every language. This is a ratchet, not a proof: a key counts
// as used when its last path segment (or its full path) shows up as a word anywhere in the source,
// so it catches keys whose code was deleted, not every key that is merely unlucky in its name.
// Keys read through a computed name are declared by prefix in i18n/dynamic-keys.ts.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { dynamicKeyPrefixes } from './dynamic-keys.ts';
import { flatten, namespacesOf, readNamespace, registry, siteRoot } from './catalog-fixtures.ts';

const SOURCE_DIRS = ['app', 'components', 'lib', 'i18n', 'functions'];

function sourceFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) return sourceFiles(path);
    return /\.(ts|tsx)$/.test(name) && !/\.test\.tsx?$/.test(name) ? [path] : [];
  });
}

const words = new Set<string>();
for (const dir of SOURCE_DIRS) {
  for (const file of sourceFiles(join(siteRoot, dir))) {
    for (const m of readFileSync(file, 'utf8').matchAll(/[A-Za-z_][A-Za-z0-9_]*/g)) words.add(m[0]);
  }
}

test('every English catalogue key is read somewhere in the source', () => {
  const unused: string[] = [];
  for (const ns of namespacesOf(registry.defaultLocale)) {
    for (const key of flatten(readNamespace(registry.defaultLocale, ns)).keys()) {
      const full = `${ns}.${key}`;
      if (dynamicKeyPrefixes.some((p) => full === p || full.startsWith(`${p}.`))) continue;
      const leaf = key.replace(/\[\d+\]$/, '').split('.').pop() as string;
      if (!words.has(leaf)) unused.push(full);
    }
  }
  assert.deepEqual(unused, [], 'unused keys: delete them, or declare a computed lookup in i18n/dynamic-keys.ts');
});
