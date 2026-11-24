import type { T } from '@/i18n';
import { normalizeKey, parseLimit } from '@/lib/recipes/labels';

function named(t: T, group: string, key: string): string {
  const id = normalizeKey(key);
  return id !== '' && t.has(`${group}.${id}`) ? t(`${group}.${id}`) : t(`${group}.other`);
}

export const stepTypeName = (t: T, key: string) => named(t, 'stepType', key);
export const categoryName = (t: T, key: string) => named(t, 'category', key);
export const fieldName = (t: T, key: string) => named(t, 'field', key);

/** The translated text of a known limit, or null so the caller hides an unknown one. */
export function limitText(t: T, limit: string): string | null {
  const parsed = parseLimit(limit);
  return parsed ? t(`limit.${parsed.key}`, parsed.values) : null;
}
