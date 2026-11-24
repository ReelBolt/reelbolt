import { readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { DEFAULT_LOCALE } from '../config';
import { DYNAMIC_KEY_PREFIXES } from '../dynamic-keys';
import { loadFlat } from '../catalog-utils';

/**
 * Static scan: a catalogue key must be referenced from source. Understood forms (single or double
 * quotes, template literals without ${}):
 *   const t = useT('ns.sub') / getT('ns.sub')  then  t('key') / t.rich('key') / t.has('key') / t.raw('key')
 *   msg('ns.key', ...)
 *   any string literal equal to a full catalogue key ("ns.sub.key") -- covers lookup tables and Msg-returning helpers.
 * Keys under a prefix in dynamic-keys.ts are exempt. A `${...}` lookup needs a declared prefix.
 */
const ROOT = path.join(__dirname, '..', '..');
const SKIP_DIRS = new Set(['node_modules', '.next', 'messages', '__tests__', 'test', 'scripts', '.scratch', 'public', 'out']);
const SRC_ROOTS = ['app', 'components', 'lib', 'i18n', 'hooks'];

function sourceFiles(dir: string, out: string[] = []): string[] {
  let entries: string[];
  try { entries = readdirSync(dir); } catch { return out; }
  for (const name of entries) {
    if (SKIP_DIRS.has(name)) continue;
    const full = path.join(dir, name);
    const st = statSync(full);
    if (st.isDirectory()) sourceFiles(full, out);
    else if (/\.(ts|tsx)$/.test(name) && !/\.test\.(ts|tsx)$/.test(name)) out.push(full);
  }
  return out;
}

const STR = `(?:'([^'\\n]*)'|"([^"\\n]*)"|\`([^\`\\n$]*)\`)`;
const pick = (m: RegExpMatchArray, from = 1) => m[from] ?? m[from + 1] ?? m[from + 2];

describe('catalogue keys are used', () => {
  it('reports unused keys by name', () => {
    const keys = Object.keys(loadFlat(DEFAULT_LOCALE));
    const used = new Set<string>();
    const literals = new Set<string>();
    const namespaces = new Set<string>();

    const files = SRC_ROOTS.flatMap((r) => sourceFiles(path.join(ROOT, r)));
    for (const file of files) {
      const text = readFileSync(file, 'utf8');
      // Namespaces bound in this file. Calls are attributed to every namespace the file binds (conservative: fewer false "unused").
      const fileNs: string[] = [];
      for (const m of text.matchAll(new RegExp(`\\b(?:useT|getT)\\(\\s*${STR}?\\s*\\)`, 'g'))) fileNs.push(pick(m) ?? '');
      for (const ns of fileNs) if (ns) namespaces.add(ns);
      // Any identifier bound with `const <name> = useT(...)` / `getT(...)` is a translator in this file (tx, tc, tm, ...), besides t/tr/translate.
      const names = new Set(['t', 'tr', 'translate']);
      for (const m of text.matchAll(/\b(?:const|let|var)\s+(\w+)\s*=\s*(?:await\s+)?(?:useT|getT)\(/g)) names.add(m[1]);
      for (const m of text.matchAll(new RegExp(`(?<![.\\w])(?:${[...names].join('|')})(?:\\.(?:rich|has|raw|markup))?\\(\\s*${STR}`, 'g'))) {
        const key = pick(m);
        if (!key) continue;
        for (const ns of fileNs.length ? fileNs : ['']) used.add(ns ? `${ns}.${key}` : key);
      }
      for (const m of text.matchAll(new RegExp(`\\bmsg\\(\\s*${STR}`, 'g'))) { const k = pick(m); if (k) used.add(k); }
      for (const m of text.matchAll(new RegExp(STR, 'g'))) { const s = pick(m); if (s && s.includes('.')) literals.add(s); }
    }

    const keySet = new Set(keys);
    const unused = keys.filter((key) => {
      if (used.has(key) || literals.has(key)) return false;
      if (DYNAMIC_KEY_PREFIXES.some((p) => key === p || key.startsWith(`${p}.`))) return false;
      // `t.rich('a')` etc. where the namespace was a sub path: match by suffix of a namespace-qualified use.
      for (const u of used) if (keySet.has(u) && u === key) return false;
      return true;
    });
    expect(unused, `Unused catalogue keys (delete them, or declare a dynamic prefix in i18n/dynamic-keys.ts):\n${unused.join('\n')}`).toEqual([]);
  });
});
