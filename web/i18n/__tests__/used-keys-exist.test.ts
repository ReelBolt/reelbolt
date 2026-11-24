import { readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { DEFAULT_LOCALE } from '../config';
import { loadFlat } from '../catalog-utils';

/**
 * The reverse of unused-keys: every catalogue key referenced from source must exist in English. A key that
 * is missing from every catalogue renders EMPTY in production (never the raw key), so a typo would ship as
 * a blank label; this test is the build-time guard.
 *
 * LITERAL keys are checked EXACTLY: declared dynamic prefixes (dynamic-keys.ts) do not excuse a literal
 * key, they only exist for `${...}` lookups, which this test cannot resolve and therefore skips.
 *
 * Understood references:
 *   const <name> = useT('ns.sub') / await getT('ns.sub', locale)   binds <name> to that namespace (any identifier)
 *   <name>('key') / <name>.rich('key')                              key must exist as ns.sub.key
 *   <name>.raw('key')                                               key or a group under it must exist
 *   msg('ns.key', ...)                                              absolute key, must exist
 * `<name>.has('key')` is exempt (it exists to probe for missing keys). A translator received as a parameter
 * (no useT/getT binding in the file) is not checked.
 */
const ROOT = path.join(__dirname, '..', '..');
const SKIP_DIRS = new Set(['node_modules', '.next', 'messages', '__tests__', 'test', 'scripts', '.scratch', '.scratch-i18n', 'public', 'out']);
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

/** Removes comments while leaving string and template literals intact (a '/*' inside a string is not a comment). */
// A `/` after one of these tokens (or `return`) opens a regex literal rather than a division.
function startsRegex(before: string): boolean {
  const t = before.trimEnd();
  return t === '' || /[(,=:[!&|?{};]$/.test(t) || /\breturn$/.test(t);
}

export function stripComments(src: string): string {
  let out = '';
  let i = 0;
  while (i < src.length) {
    const c = src[i];
    const n = src[i + 1];
    if (c === '"' || c === "'" || c === '`') {
      let j = i + 1;
      while (j < src.length && src[j] !== c) j += src[j] === '\\' ? 2 : 1;
      out += src.slice(i, j + 1);
      i = j + 1;
    } else if (c === '/' && n !== '/' && n !== '*' && startsRegex(out)) {
      // Conservative regex-literal heuristic: ends at the next unescaped `/` on the same line (class-aware).
      let j = i + 1;
      let inClass = false;
      while (j < src.length && src[j] !== '\n' && (inClass || src[j] !== '/')) {
        if (src[j] === '\\') j++;
        else if (src[j] === '[') inClass = true;
        else if (src[j] === ']') inClass = false;
        j++;
      }
      if (src[j] === '/') {
        out += src.slice(i, j + 1);
        i = j + 1;
      } else {
        out += c;
        i++;
      }
    } else if (c === '/' && n === '/') {
      while (i < src.length && src[i] !== '\n') i++;
    } else if (c === '/' && n === '*') {
      const end = src.indexOf('*/', i + 2);
      i = end === -1 ? src.length : end + 2;
    } else {
      out += c;
      i++;
    }
  }
  return out;
}

const STR = `(?:'([^'\\n]*)'|"([^"\\n]*)"|\`([^\`\\n$]*)\`)`;
const pick = (m: RegExpMatchArray, from = 1) => m[from] ?? m[from + 1] ?? m[from + 2];

describe('referenced catalogue keys exist', () => {
  it('stripComments keeps strings that look like comments', () => {
    expect(stripComments("const g = 'a/**/*.tsx'; // t('gone')\n/* t('gone2') */ t('kept')")).toBe("const g = 'a/**/*.tsx'; \n t('kept')");
  });

  it('stripComments skips regex literals, even with a quote inside', () => {
    expect(stripComments("const r = /['\"]x/; // t('gone')\nt('kept')")).toBe("const r = /['\"]x/; \nt('kept')");
    expect(stripComments("if (/a\\/b[/]c/.test(s)) return /'/; /* t('gone') */")).toBe("if (/a\\/b[/]c/.test(s)) return /'/; ");
  });

  it('lists any key used in code that English does not define', () => {
    const keySet = new Set(Object.keys(loadFlat(DEFAULT_LOCALE)));
    const namespaces = new Set([...keySet].map((k) => k.split('.')[0]));
    const exact = (k: string) => keySet.has(k);
    const group = (k: string) => keySet.has(k) || [...keySet].some((e) => e.startsWith(`${k}.`));
    const missing: string[] = [];

    for (const file of SRC_ROOTS.flatMap((r) => sourceFiles(path.join(ROOT, r)))) {
      const text = stripComments(readFileSync(file, 'utf8'));
      const rel = path.relative(ROOT, file);
      // A file may bind the same name in several components (`const t = useT('a')` ... `const t = useT('b')`): a use
      // belongs to the nearest binding of that name before it.
      const bindings: Array<{ name: string; ns: string; at: number }> = [];
      for (const m of text.matchAll(new RegExp(`\\b(?:const|let|var)\\s+(\\w+)\\s*=\\s*(?:await\\s+)?(?:useT|getT)\\(\\s*${STR}?\\s*(?:,[^)]*)?\\)`, 'g'))) {
        bindings.push({ name: m[1], ns: m[2] ?? m[3] ?? m[4] ?? '', at: m.index ?? 0 });
      }
      for (const name of new Set(bindings.map((x) => x.name))) {
        for (const m of text.matchAll(new RegExp(`(?<![.\\w])${name}(\\.(?:rich|raw))?\\(\\s*${STR}`, 'g'))) {
          const at = m.index ?? 0;
          const binding = bindings.filter((x) => x.name === name && x.at < at).pop() ?? bindings.find((x) => x.name === name);
          if (!binding) continue;
          const ns = binding.ns;
          const key = m[2] ?? m[3] ?? m[4];
          if (!key) continue;
          const full = ns ? `${ns}.${key}` : key;
          const ok = m[1] === '.raw' ? group(full) : exact(full);
          if (!ok) missing.push(`${rel}: ${name}${m[1] ?? ''}('${key}') in '${ns}'`);
        }
      }
      for (const m of text.matchAll(new RegExp(`(?<![.\\w])msg\\(\\s*${STR}`, 'g'))) {
        const k = pick(m);
        // Only absolute keys that start with a real namespace; anything else is some other `msg(` call.
        if (k && k.includes('.') && namespaces.has(k.split('.')[0]) && !exact(k)) missing.push(`${rel}: msg('${k}')`);
      }
    }
    expect(missing, `Keys used in code but missing from the English catalogue:\n${missing.join('\n')}`).toEqual([]);
  });
});
