// Runs after `docusaurus build` (npm `postbuild`). Proves the offline search index exists for every locale and
// answers real queries: it loads build/**/search-index.json with the same lunr + language packs the browser
// uses and checks that "dub" and "subtitles" hit in English and "sottotitoli" hits in Italian.
// Also checks that no indexed document URL leaves the user guide (no developer route).
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(import.meta.url);
const lunr = require('lunr');
require('lunr-languages/lunr.stemmer.support')(lunr);
require('lunr-languages/lunr.it')(lunr);
require('lunr-languages/lunr.multi')(lunr);
const registry = JSON.parse(fs.readFileSync(path.join(root, 'locales.json'), 'utf8'));
const expected = { [registry.defaultLocale]: ['dub', 'subtitles'], it: ['sottotitoli'] };
const problems = [];

for (const locale of registry.locales.map((l) => l.code)) {
  const dir = locale === registry.defaultLocale ? 'build' : path.join('build', locale);
  const file = path.join(root, dir, 'search-index.json');
  if (!fs.existsSync(file)) { problems.push(`${locale}: ${dir}/search-index.json is missing`); continue; }
  const entries = JSON.parse(fs.readFileSync(file, 'utf8'));
  const indexes = entries.map((e) => ({ docs: e.documents, idx: lunr.Index.load(e.index) }));
  for (const e of entries) for (const d of e.documents) {
    if (/\/developer(\/|$)/.test(d.u)) problems.push(`${locale}: indexed developer route ${d.u}`);
  }
  for (const term of expected[locale] || []) {
    const hits = indexes.reduce((n, { idx }) => n + idx.search(term).length, 0);
    if (hits === 0) problems.push(`${locale}: query "${term}" returns no hits`);
    else console.log(`assert-search-index: ${locale} "${term}" -> ${hits} hits`);
  }
}
if (problems.length) { console.error('assert-search-index FAILED:\n- ' + problems.join('\n- ')); process.exit(1); }
console.log('assert-search-index: ok');
