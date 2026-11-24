import { parse, TYPE, type MessageFormatElement } from '@formatjs/icu-messageformat-parser';
import { printAST } from '@formatjs/icu-messageformat-parser/printer.js';

/**
 * The `en-XA` pseudo-locale: accents, ~40% padding and [brackets], generated from English at load
 * time so truncation, clipping and hard-coded strings show up. Only produced when
 * NEXT_PUBLIC_I18N_PSEUDO=1 (and under Vitest); never in the registry, never shipped.
 */
const ACCENTS: Record<string, string> = {
  a: 'à', b: 'ƀ', c: 'ç', d: 'ð', e: 'é', f: 'ƒ', g: 'ĝ', h: 'ĥ', i: 'î', j: 'ĵ', k: 'ķ', l: 'ļ', m: 'ɱ',
  n: 'ñ', o: 'ö', p: 'þ', q: 'ǫ', r: 'ŕ', s: 'š', t: 'ţ', u: 'û', v: 'ṽ', w: 'ŵ', x: 'ẋ', y: 'ý', z: 'ž',
  A: 'À', B: 'Ɓ', C: 'Ç', D: 'Ð', E: 'É', F: 'Ƒ', G: 'Ĝ', H: 'Ĥ', I: 'Î', J: 'Ĵ', K: 'Ķ', L: 'Ļ', M: 'Ṁ',
  N: 'Ñ', O: 'Ö', P: 'Þ', Q: 'Ǫ', R: 'Ŕ', S: 'Š', T: 'Ţ', U: 'Û', V: 'Ṽ', W: 'Ŵ', X: 'Ẋ', Y: 'Ý', Z: 'Ž',
};

const accent = (s: string): string => s.replace(/[a-zA-Z]/g, (c) => ACCENTS[c] ?? c);

function walk(nodes: MessageFormatElement[]): void {
  for (const node of nodes) {
    if (node.type === TYPE.literal) node.value = accent(node.value);
    else if (node.type === TYPE.plural || node.type === TYPE.select) {
      for (const option of Object.values(node.options)) walk(option.value);
    } else if (node.type === TYPE.tag) walk(node.children);
  }
}

export function pseudoizeString(message: string): string {
  let ast: MessageFormatElement[];
  try {
    ast = parse(message);
  } catch {
    return message;
  }
  walk(ast);
  const printed = printAST(ast);
  const pad = '·'.repeat(Math.ceil(message.length * 0.4));
  return `[${printed}${pad}]`;
}

export function pseudoize<T>(value: T): T {
  if (typeof value === 'string') return pseudoizeString(value) as unknown as T;
  if (Array.isArray(value)) return value.map(pseudoize) as unknown as T;
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, pseudoize(v)])) as T;
  }
  return value;
}
