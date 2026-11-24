import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { siteRoot } from './catalog-fixtures.ts';

test('every file in i18n/migrated.json exists (the no-literal-string ratchet cannot silently lose a file)', () => {
  const migrated: string[] = JSON.parse(readFileSync(join(siteRoot, 'i18n/migrated.json'), 'utf8'));
  assert.ok(migrated.length > 0);
  for (const file of migrated) assert.ok(existsSync(join(siteRoot, file)), `${file} is listed but missing`);
});
