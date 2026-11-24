#!/usr/bin/env node
// Dead relative link gate for the documentation of this tree.
//
//   node scripts/check-doc-links.mjs [root]      (root defaults to the current directory)
//
// Checks every Markdown link `[text](target)`, image and reference definition `[ref]: target` in the top-level
// *.md files, docs/**/*.md(x) and the docs-site sources. A link fails when its RELATIVE target file or directory
// does not exist or lies outside the root; lines over 4000 characters are not scanned; "#anchors" are not verified. Absolute URLs, mailto:, "/site/paths" and pure "#anchors" are skipped,
// as are code fences and inline code. Prints every dead link as file:line and exits 1 if there is any.
import { existsSync, readdirSync, readFileSync, lstatSync } from 'node:fs';
import { dirname, isAbsolute, join, normalize, relative, resolve, sep } from 'node:path';

const root = resolve(process.argv[2] ?? '.');
const SKIP = new Set(['node_modules', '.git', '.next', '.docusaurus', 'build', 'dist']);
const EXTERNAL = /^(https?:|mailto:|tel:|#|\/|\$|<|\{|data:|[a-z][a-z0-9+.-]*:)/i;

function* walk(dir) {
  for (const name of readdirSync(dir).sort()) {
    if (SKIP.has(name)) continue;
    const p = join(dir, name);
    const st = lstatSync(p);
    if (st.isSymbolicLink()) continue;
    if (st.isDirectory()) yield* walk(p);
    else if (/\.mdx?$/.test(name)) yield p;
  }
}

function* files() {
  for (const name of readdirSync(root).sort()) {
    const p = join(root, name);
    if (/\.md$/.test(name) && lstatSync(p).isFile()) yield p;
  }
  for (const top of ['docs', 'docs-site']) {
    const d = join(root, top);
    if (existsSync(d)) yield* walk(d);
  }
}

const INLINE = /(`[^`]*`)|!?\[(?:[^\]`]|`[^`]*`)*\]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)/g;
const MAX_LINE = 4000;
const REFDEF = /^\s{0,3}\[[^\]]+\]:\s*<?(\S+?)>?(?:\s+["'(].*)?$/;

const dead = [];
let checked = 0;
for (const file of files()) {
  const dir = dirname(file);
  let fence = false;
  readFileSync(file, 'utf8').split(/\r?\n/).forEach((line, i) => {
    if (/^\s*(```|~~~)/.test(line)) { fence = !fence; return; }
    if (fence) return;
    if (line.length > MAX_LINE) return;   // no link scan on a pathological line (regex backtracking); real prose lines are far shorter
    const targets = [];
    for (const m of line.matchAll(INLINE)) if (m[2] !== undefined) targets.push(m[2]);
    const rd = REFDEF.exec(line);
    if (rd) targets.push(rd[1]);
    for (const target of targets) {
      if (EXTERNAL.test(target)) continue;
      let t = target.split('#')[0].split('?')[0];
      try { t = decodeURIComponent(t); } catch { /* keep as written */ }
      if (!t) continue;
      checked++;
      // Confined to the root: a target that resolves outside it is dead even when a file is there.
      const resolved = normalize(join(dir, t));
      const rel = relative(root, resolved);
      const outside = rel === '..' || rel.startsWith(`..${sep}`) || isAbsolute(rel);
      if (outside || !existsSync(resolved)) dead.push(`${relative(root, file)}:${i + 1}: dead link ${target}`);
    }
  });
}

if (dead.length) {
  console.error(`check-doc-links: ${dead.length} dead relative link(s) (file:line):`);
  for (const d of dead) console.error(`  ${d}`);
  process.exit(1);
}
console.log(`check-doc-links: ${checked} relative link(s) checked, none dead`);
