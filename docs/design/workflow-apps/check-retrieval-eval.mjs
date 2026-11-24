#!/usr/bin/env node
// Validates docs/design/workflow-apps/retrieval-eval.json. Run from the repo root:
//   node docs/design/workflow-apps/check-retrieval-eval.mjs [--strict]
// Checks shape, unique ids, and that every `gold` entry (docPath#section) names a file and a `##` section that exist in the
// retrievable corpus. `pendingGold` entries (assistant-kb, lane 3b-2) may name documents that do not exist yet: such a
// query is `pending-kb` (reported, excluded from the metrics). When the document exists, its section must too
// (a warning; an error with --strict). Exits non-zero when any error is found.
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const docsRoot = join(here, '..', '..');
const strict = process.argv.includes('--strict');
const fixture = JSON.parse(readFileSync(join(here, 'retrieval-eval.json'), 'utf8'));
const LOCALES = new Set(['en', 'it', 'es', 'de', 'fr', 'pt']);
const TYPES = new Set(['goal', 'step', 'template', 'recipe', 'howto', 'paraphrase']);
const problems = [];
const warnings = [];

// Same rule as PlatformDocsCorpus.ChunkDocument: sections are `## ` headings outside code fences; empty sections vanish.
function sectionsOf(relPath) {
  const file = join(docsRoot, relPath);
  if (!existsSync(file)) return null;
  let md = readFileSync(file, 'utf8').replace(/\r\n/g, '\n');
  if (md.startsWith('---\n')) {
    const end = md.indexOf('\n---', 4);
    if (end > 0) {
      const nl = md.indexOf('\n', end + 4);
      md = nl < 0 ? '' : md.slice(nl + 1);
    }
  }
  const found = new Map([['', 0]]);
  let current = '';
  let fence = false;
  let h1 = false;
  for (const line of md.split('\n')) {
    const t = line.trimStart();
    if (t.startsWith('```') || t.startsWith('~~~')) fence = !fence;
    if (!fence && line.startsWith('# ') && !h1) { h1 = true; continue; }
    if (!fence && line.startsWith('## ')) { current = line.slice(3).trim(); found.set(current, 0); continue; }
    found.set(current, found.get(current) + line.trim().length);
  }
  return new Set([...found].filter(([, n]) => n > 0).map(([h]) => h));
}

const cache = new Map();
function sections(doc) {
  if (!cache.has(doc)) cache.set(doc, sectionsOf(doc));
  return cache.get(doc);
}
function split(entry) {
  const i = entry.indexOf('#');
  return i < 0 ? [entry, null] : [entry.slice(0, i), entry.slice(i + 1)];
}
const retrievable = (doc) => doc.startsWith('user-guide/') || doc.startsWith('assistant-kb/') || !doc.includes('/');

const ids = new Set();
let pending = 0;
const byLocale = {};
const byType = {};
for (const [n, item] of (fixture.queries ?? []).entries()) {
  const where = `queries[${n}] ${item.id ?? '?'}`;
  if (!/^Q\d{3}$/.test(item.id ?? '')) problems.push(`${where}: id must look like Q001`);
  else if (ids.has(item.id)) problems.push(`${where}: duplicate id`);
  else ids.add(item.id);
  if (!LOCALES.has(item.locale)) problems.push(`${where}: bad locale ${item.locale}`);
  if (!TYPES.has(item.type)) problems.push(`${where}: bad type ${item.type}`);
  if (typeof item.query !== 'string' || item.query.trim().length < 3) problems.push(`${where}: query text missing`);
  byLocale[item.locale] = (byLocale[item.locale] ?? 0) + 1;
  byType[item.type] = (byType[item.type] ?? 0) + 1;
  const gold = item.gold ?? [];
  const pend = item.pendingGold ?? [];
  if (!Array.isArray(gold) || !Array.isArray(pend)) { problems.push(`${where}: gold and pendingGold must be arrays`); continue; }
  if (gold.length === 0 && pend.length === 0) problems.push(`${where}: needs gold or pendingGold`);
  for (const g of gold) {
    const [doc, sec] = split(g);
    if (sec === null) { problems.push(`${where}: gold ${g} must be docPath#section`); continue; }
    if (!retrievable(doc)) problems.push(`${where}: gold ${doc} is not in the retrievable corpus`);
    const have = sections(doc);
    if (have === null) problems.push(`${where}: gold document missing: ${doc} (move it to pendingGold if the knowledge base will add it)`);
    else if (!have.has(sec)) problems.push(`${where}: gold section not found in ${doc}: "${sec}"`);
  }
  let missingDoc = false;
  for (const g of pend) {
    const [doc, sec] = split(g);
    if (sec === null || !doc.startsWith('assistant-kb/')) { problems.push(`${where}: pendingGold ${g} must be assistant-kb/<file>.md#section`); continue; }
    const have = sections(doc);
    if (have === null) missingDoc = true;
    else if (!have.has(sec)) (strict ? problems : warnings).push(`${where}: pendingGold section not in ${doc}: "${sec}" (update the entry to the real heading)`);
  }
  if (gold.length === 0 && missingDoc) pending++;
}

console.log(`queries: ${ids.size}; pending-kb (no gold yet): ${pending}`);
console.log('by locale:', JSON.stringify(byLocale));
console.log('by type:', JSON.stringify(byType));
if ((fixture.queries ?? []).length < 140) problems.push(`expected about 150 queries, found ${(fixture.queries ?? []).length}`);
for (const w of warnings) console.warn(`warn: ${w}`);
if (problems.length > 0) {
  for (const p of problems) console.error(`error: ${p}`);
  process.exit(1);
}
console.log('retrieval-eval.json: ok');
