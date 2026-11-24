#!/usr/bin/env node
// Finishes one translated user-guide page so links and anchors keep working.
//   node docs-site/scripts/i18n-stamp.mjs <english .md> <italian .md>
// 1. Gives every heading of the translation an explicit id `{#id}` equal to the anchor of the English heading at the same position
//    (so `page.md#anchor` links keep resolving in every language). Fails if the heading counts differ.
// 2. Writes `i18n_source_sha` (git blob hash of the English file) into the front matter; scripts/i18n-docs-stale.mjs compares it.
// Needs github-slugger (docs-site/node_modules).
import { readFileSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';

const [en, it] = process.argv.slice(2);
if (!en || !it) { console.error('usage: i18n-stamp.mjs <english.md> <italian.md>'); process.exit(2); }
const require = createRequire(import.meta.url);
const { default: GithubSlugger } = await import(require.resolve('github-slugger'));

const idRe = /\s*\{#([^}\s]+)\}\s*$/;
function headings(text) {
  const lines = text.split('\n'); const out = []; let fence = null;
  lines.forEach((line, i) => {
    const f = line.match(/^\s*(```+|~~~+)/);
    if (f) { fence = fence ? (line.trim().startsWith(fence) ? null : fence) : f[1]; return; }
    if (fence) return;
    const m = line.match(/^(#{1,6})\s+(.*?)\s*#*\s*$/);
    if (m) out.push({ line: i, level: m[1].length, text: m[2] });
  });
  return out;
}
const enText = readFileSync(en, 'utf8'); const itText = readFileSync(it, 'utf8');
const enH = headings(enText); const itH = headings(itText);
if (enH.length !== itH.length) {
  console.error(`heading count differs: english ${enH.length}, italian ${itH.length} (${it})`); process.exit(1);
}
const slugger = new GithubSlugger();
const ids = enH.map((h) => { const m = h.text.match(idRe); return m ? m[1] : slugger.slug(h.text.replace(/[`*]/g, '')); });
const lines = itText.split('\n');
itH.forEach((h, i) => {
  const base = lines[h.line].replace(idRe, '');
  lines[h.line] = `${base} {#${ids[i]}}`;
});
let out = lines.join('\n');
const sha = execFileSync('git', ['hash-object', en]).toString().trim();
if (/^---\n/.test(out)) {
  const end = out.indexOf('\n---', 4);
  let fm = out.slice(4, end).split('\n').filter((l) => !l.startsWith('i18n_source_sha:'));
  fm.push(`i18n_source_sha: ${sha}`);
  out = `---\n${fm.join('\n')}${out.slice(end)}`;
} else {
  out = `---\ni18n_source_sha: ${sha}\n---\n${out}`;
}
writeFileSync(it, out);
console.log(`stamped ${it} (${itH.length} headings, ${sha.slice(0, 8)})`);
