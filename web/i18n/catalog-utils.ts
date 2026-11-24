import { readdirSync, readFileSync, existsSync } from 'node:fs';
import path from 'node:path';
import { parse, TYPE, type MessageFormatElement } from '@formatjs/icu-messageformat-parser';

/** Shared by the catalogue tests. Reads web/messages straight from disk (no Next, no fallback merge). */
export const MESSAGES_DIR = path.join(__dirname, '..', 'messages');

export function namespaceFiles(locale: string): string[] {
  const dir = path.join(MESSAGES_DIR, locale);
  if (!existsSync(dir)) return [];
  return readdirSync(dir).filter((f) => f.endsWith('.json') && !f.startsWith('_')).sort();
}

export type Flat = Record<string, string>;

/** `{ files: { manager: { a: 'x' } } }` => `{ 'files.manager.a': 'x' }`; non-string leaves are returned as-is for the test to report. */
export function flatten(value: unknown, prefix: string, out: Record<string, unknown> = {}): Record<string, unknown> {
  if (value && typeof value === 'object' && !Array.isArray(value)) {
    for (const [k, v] of Object.entries(value)) flatten(v, prefix ? `${prefix}.${k}` : k, out);
  } else out[prefix] = value;
  return out;
}

export function loadFlat(locale: string): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const file of namespaceFiles(locale)) {
    const ns = file.slice(0, -5);
    flatten(JSON.parse(readFileSync(path.join(MESSAGES_DIR, locale, file), 'utf8')), ns, out);
  }
  return out;
}

export interface Shape {
  /** Argument name -> kind (`arg`, `number`, `date`, `time`, `plural`, `select`, `tag`, `pound`). */
  args: Record<string, string>;
  /** For plural/select arguments: sorted option keys that differ by language are ignored except `other`. */
  hasOther: Record<string, boolean>;
}

export function shapeOf(message: string): Shape {
  const shape: Shape = { args: {}, hasOther: {} };
  const visit = (nodes: MessageFormatElement[]) => {
    for (const n of nodes) {
      switch (n.type) {
        case TYPE.argument: shape.args[n.value] ??= 'arg'; break;
        case TYPE.number: shape.args[n.value] = 'number'; break;
        case TYPE.date: shape.args[n.value] = 'date'; break;
        case TYPE.time: shape.args[n.value] = 'time'; break;
        case TYPE.plural:
        case TYPE.select:
          shape.args[n.value] = n.type === TYPE.plural ? 'plural' : 'select';
          shape.hasOther[n.value] = 'other' in n.options;
          for (const o of Object.values(n.options)) visit(o.value);
          break;
        case TYPE.tag: shape.args[n.value] = 'tag'; visit(n.children); break;
        default: break;
      }
    }
  };
  visit(parse(message));
  return shape;
}
