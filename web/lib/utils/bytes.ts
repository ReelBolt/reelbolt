/**
 * Byte sizes, in the one place every surface reads them from.
 *
 * This is `lib/files/model.ts`'s `formatBytes`, lifted out so a module that must not pull in the file
 * tree (`lib/api/quota.ts`, for the storage-quota sentence) can print a size too. `model.ts`
 * re-exports it, so every existing caller keeps working.
 */

import type { Format } from '@/i18n/format';

/**
 * "4.2 GB", "15 GB", "0 B" — binary units, one decimal when it says something, none otherwise.
 * Pass `f` (`useFormat()`) so the decimal separator follows the active language.
 */
export function formatBytes(bytes: number, f?: Format): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.min(units.length - 1, Math.floor(Math.log(bytes) / Math.log(1024)));
  const v = bytes / Math.pow(1024, i);
  if (i === 0) return `${f ? f.number(v) : v} B`;
  if (f) return `${f.number(v, { maximumFractionDigits: 1 })} ${units[i]}`;
  const rounded = v.toFixed(1);
  // "15.0 GB" reads worse than "15 GB" in a sentence, so a whole number keeps no decimal at all.
  return `${rounded.endsWith('.0') ? v.toFixed(0) : rounded} ${units[i]}`;
}
