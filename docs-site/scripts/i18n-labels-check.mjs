#!/usr/bin/env node
// Checks every bold UI label (**...**) of the Italian user guide against the real Italian dashboard catalogues.
//   node docs-site/scripts/i18n-labels-check.mjs [--json]
// Pairs each Italian bold label with the English page's bold label at the same position, maps the English label to its catalogue
// key(s) and proposes the Italian value for that key. Labels that match no Italian catalogue value are reported.
import { readFileSync, readdirSync, existsSync, statSync } from 'node:fs';
import { join, relative, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');

export function flatten(obj, prefix = '', out = {}) {
  for (const [k, v] of Object.entries(obj)) {
    const key = prefix ? `${prefix}.${k}` : k;
    if (v && typeof v === 'object') flatten(v, key, out); else if (typeof v === 'string') out[key] = v;
  }
  return out;
}
export function loadCatalogue(locale, base = join(root, 'web', 'messages')) {
  const dir = join(base, locale); const out = {};
  for (const f of readdirSync(dir)) {
    if (!f.endsWith('.json') || f.startsWith('_')) continue;
    Object.assign(out, flatten(JSON.parse(readFileSync(join(dir, f), 'utf8')), f.slice(0, -5)));
  }
  return out;
}
export const norm = (s) => s.replace(/\{[^}]*\}/g, '').replace(/[\s ]+/g, ' ').replace(/[:.…]+$/u, '').trim().toLowerCase();

export function boldLabels(text) {
  const out = []; let fence = null;
  for (const line of text.split('\n')) {
    const f = line.match(/^\s*(```+|~~~+)/);
    if (f) { fence = fence ? null : f[1]; continue; }
    if (fence) continue;
    for (const m of line.matchAll(/\*\*([^*]+?)\*\*/g)) out.push(m[1].trim());
  }
  return out;
}
export function index(cat) {
  const m = new Map();
  for (const [k, v] of Object.entries(cat)) { const n = norm(v); if (!n) continue; if (!m.has(n)) m.set(n, []); m.get(n).push(k); }
  return m;
}
export function check(enText, itText, ctx) {
  const enL = boldLabels(enText); const itL = boldLabels(itText); const res = [];
  const paired = enL.length === itL.length;
  itL.forEach((label, i) => {
    const n = norm(label);
    if (ctx.itIdx.has(n)) { res.push({ label, ok: true }); return; }
    const en = paired ? enL[i] : null; let suggest = null; let keys = [];
    if (en) {
      keys = ctx.enIdx.get(norm(en)) ?? [];
      const vals = [...new Set(keys.map((k) => ctx.it[k]).filter(Boolean))];
      if (vals.length) suggest = vals;
    }
    res.push({ label, ok: false, en, suggest, keys: keys.slice(0, 3) });
  });
  return res;
}
function walk(d) { return readdirSync(d).flatMap((f) => { const p = join(d, f); return statSync(p).isDirectory() ? walk(p) : /\.mdx?$/.test(f) ? [p] : []; }); }

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const en = loadCatalogue('en'); const it = loadCatalogue('it');
  const ctx = { it, enIdx: index(en), itIdx: index(it) };
  const itDir = join(root, 'docs/user-guide-i18n/it/docusaurus-plugin-content-docs/current');
  let total = 0, bad = 0; const rows = [];
  for (const p of walk(itDir)) {
    const rel = relative(itDir, p); const enP = join(root, 'docs/user-guide', rel);
    const enText = existsSync(enP) ? readFileSync(enP, 'utf8') : '';
    for (const r of check(enText, readFileSync(p, 'utf8'), ctx)) { total++; if (!r.ok) { bad++; rows.push({ page: rel, ...r }); } }
  }
  if (process.argv.includes('--json')) console.log(JSON.stringify(rows, null, 1));
  else for (const r of rows) console.log(`${r.page}\t${r.label}\t<- en: ${r.en ?? '?'}\t=> ${r.suggest ? r.suggest.join(' | ') : '(no catalogue value)'}`);
  console.error(`labels checked: ${total}, unmatched: ${bad}`);
}
