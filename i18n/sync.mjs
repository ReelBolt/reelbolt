#!/usr/bin/env node
// Vendors i18n/locales.json (the one language registry) byte-identically into every app.
//   node i18n/sync.mjs          copy
//   node i18n/sync.mjs --check  exit 1 on drift (CI "i18n registry drift")
// Adding a language: add its catalogues under web/messages/<code>/, site/messages/<code>/,
// docs-site/i18n/<code>/, api/services/email_templates/<code>/, then one entry in locales.json and run this.
import { copyFileSync, existsSync, readFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const source = join(root, 'i18n', 'locales.json');
const targets = ['web/i18n/locales.json', 'site/i18n/locales.json', 'docs-site/locales.json', 'api/services/locales.json', 'nginx/locales.json'];
// pick-locale.js: the one `/` language picker, vendored into nginx's njs and the legacy Pages Function copy.
const pickSource = join(root, 'i18n', 'pick-locale.js');
const pickTargets = ['site/functions/_pick-locale.js', 'nginx/pick-locale.js'];
const check = process.argv.includes('--check');
let drift = 0;
for (const [from, list] of [[source, targets], [pickSource, pickTargets]]) {
for (const t of list) {
  const dest = join(root, t);
  const same = existsSync(dest) && readFileSync(dest).equals(readFileSync(from));
  if (same) continue;
  if (check) { console.error(`drift: ${t}`); drift++; continue; }
  mkdirSync(dirname(dest), { recursive: true });
  copyFileSync(from, dest);
  console.log(`synced ${t}`);
}
}
if (check && drift) process.exit(1);
