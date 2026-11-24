/**
import { msg, type Msg } from '@/i18n/msg';
 * Reduces whatever `GET .../versions/{id}/diff` answered into the few lines the draft banner shows.
 * The route answers two shapes: a design diff (`counts` + added/removed/changed token paths) and a
 * product-map diff (`{ product: { routes, components, dependencies, ...optional sections } }` with
 * no `counts`). Anything else (still loading, a new server shape) is `null` -> "summary unavailable".
 */

import { msg, type Msg } from '@/i18n/msg';

export interface DraftSummaryLine {
  /** Null for the design headline, which has no label. */
  label: Msg | null;
  added: number;
  changed: number;
  removed: number;
}

export interface DraftSummary {
  lines: DraftSummaryLine[];
  /** Names of the changed things, most relevant first. */
  names: string[];
  /** Product-map sections other than routes/components/dependencies that changed. */
  otherSections: (Msg | string)[];
}

const PRODUCT_COLLECTIONS: [key: string, label: Msg][] = [
  ['routes', msg('brandLibraries.profile.draftReview.collections.routes')],
  ['components', msg('brandLibraries.profile.draftReview.collections.components')],
  ['dependencies', msg('brandLibraries.profile.draftReview.collections.dependencies')],
];

const OTHER_COLLECTIONS: [key: string, label: Msg][] = [
  ['directories', msg('brandLibraries.profile.draftReview.collections.directories')],
  ['entryPoints', msg('brandLibraries.profile.draftReview.collections.entryPoints')],
  ['patterns', msg('brandLibraries.profile.draftReview.collections.patterns')],
];

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function list(v: unknown): unknown[] {
  return Array.isArray(v) ? v : [];
}

/** A name from a plain string (added/removed) or a `{ name }` / `{ path }` entry (changed). */
function nameOf(v: unknown): string | null {
  if (typeof v === 'string') return v;
  if (isRecord(v)) {
    if (typeof v.name === 'string') return v.name;
    if (typeof v.path === 'string') return v.path;
  }
  return null;
}

function names(v: unknown): string[] {
  return list(v).map(nameOf).filter((n): n is string => n !== null);
}

function num(v: unknown): number {
  return typeof v === 'number' && Number.isFinite(v) ? v : 0;
}

export function summarizeDraftDiff(raw: unknown): DraftSummary | null {
  if (!isRecord(raw)) return null;

  if (isRecord(raw.product)) {
    const product = raw.product;
    const lines: DraftSummaryLine[] = [];
    const changedNames: string[] = [];
    const addedNames: string[] = [];
    const removedNames: string[] = [];
    for (const [key, label] of PRODUCT_COLLECTIONS) {
      const c = isRecord(product[key]) ? product[key] : {};
      const a = names(c.added);
      const ch = names(c.changed);
      const r = names(c.removed);
      lines.push({ label, added: a.length, changed: ch.length, removed: r.length });
      changedNames.push(...ch);
      addedNames.push(...a);
      removedNames.push(...r);
    }
    const otherSections: (Msg | string)[] = [];
    for (const [key, label] of OTHER_COLLECTIONS) {
      const c = product[key];
      if (isRecord(c) && names(c.added).length + names(c.removed).length + names(c.changed).length > 0) {
        otherSections.push(label);
      }
    }
    otherSections.push(...names(product.fields));
    return { lines, names: [...changedNames, ...addedNames, ...removedNames], otherSections };
  }

  if (isRecord(raw.counts)) {
    const counts = raw.counts;
    return {
      lines: [{ label: null, added: num(counts.added), changed: num(counts.changed), removed: num(counts.removed) }],
      names: [...names(raw.changed), ...names(raw.added), ...names(raw.removed)],
      otherSections: [],
    };
  }

  return null;
}

/** True when nothing changed at all. */
export function isEmptySummary(s: DraftSummary): boolean {
  return s.lines.every((l) => l.added + l.changed + l.removed === 0) && s.otherSections.length === 0;
}
