import { EDITOR_PAGES, type EditorPage } from '../page-url';
import { POOL_PANES_STORAGE_KEY, parsePoolPanes } from '../media/pool-panes';

/**
 * The editor's one panel-geometry table and one persistence model.
 *
 * Every page fills the same frame; the table says how. Sizes are stored as **fractions** of the
 * container, so a layout made on a wide monitor still fits a laptop. Pixels are derived from the
 * fractions and clamped to the minimums; the table's pixel sizes are the fallback when nothing has
 * been stored yet.
 *
 * This module has no React and no DOM of its own: the caller passes the storage and the bounds it
 * measured, which keeps the parse/clamp/migrate rules testable without a browser.
 */

/** The part of a zone a page owns, and the size it opens at. */
export type ZoneKind =
  | 'bins'
  | 'pool'
  | 'viewer'
  | 'viewer-metadata'
  | 'source-tape'
  | 'inspector'
  | 'panel-tabs'
  | 'masks'
  | 'properties'
  | 'nodes'
  | 'grade-tools'
  | 'scopes'
  | 'mixer'
  | 'buses'
  | 'settings'
  | 'result';

export interface PageZone {
  kind: ZoneKind;
  /** Default width in CSS pixels. Ignored when `fills` is true. */
  width: number;
  /** The zone takes the rest of the row (Deliver's result column). */
  fills?: boolean;
}

/** Where the picture lives on a page. Media keeps it in the right zone; Deliver has no shared viewer. */
export type ViewerSlot = 'centre' | 'right' | 'none';

export interface PageGeometry {
  page: EditorPage;
  left: PageZone | null;
  /** The centre zone's kind, or `null` when the page is a two-column page (Deliver). */
  centre: { kind: ZoneKind } | null;
  right: PageZone | null;
  /** Timeline height in CSS pixels; `null` means the page has no timeline at all (Deliver). */
  timeline: number | null;
  /** Whether the timeline is shown on first load. Media starts hidden and reveals it on demand. */
  timelineVisible: boolean;
  /** Height of the timeline when the user reveals it on a page that starts hidden (Media). */
  timelineOnDemand: number | null;
  /** The overview band between the viewer row and the timeline (Cut), in CSS pixels. */
  overview: number | null;
  /** Scopes docked under the viewer in the centre zone (Colour), in CSS pixels. */
  scopes: number | null;
  viewer: ViewerSlot;
}

/**
 * The geometry table. It is the fallback, not the only record: stored fractions win once present.
 * Deliver is a two-column page that fills the frame — it declares no centre and no timeline.
 */
export const PAGE_GEOMETRY: Readonly<Record<EditorPage, PageGeometry>> = Object.freeze({
  media: {
    page: 'media',
    left: { kind: 'bins', width: 280 },
    centre: { kind: 'pool' },
    right: { kind: 'viewer-metadata', width: 420 },
    timeline: 200,
    timelineVisible: false,
    timelineOnDemand: 200,
    overview: null,
    scopes: null,
    viewer: 'right',
  },
  cut: {
    page: 'cut',
    left: { kind: 'source-tape', width: 320 },
    centre: { kind: 'viewer' },
    right: { kind: 'inspector', width: 320 },
    timeline: 240,
    timelineVisible: true,
    timelineOnDemand: null,
    overview: 120,
    scopes: null,
    viewer: 'centre',
  },
  edit: {
    page: 'edit',
    left: { kind: 'panel-tabs', width: 300 },
    centre: { kind: 'viewer' },
    right: { kind: 'panel-tabs', width: 320 },
    timeline: 320,
    timelineVisible: true,
    timelineOnDemand: null,
    overview: null,
    scopes: null,
    viewer: 'centre',
  },
  compose: {
    page: 'compose',
    left: { kind: 'masks', width: 340 },
    centre: { kind: 'viewer' },
    right: { kind: 'properties', width: 340 },
    timeline: 180,
    timelineVisible: true,
    timelineOnDemand: null,
    overview: null,
    scopes: null,
    viewer: 'centre',
  },
  colour: {
    page: 'colour',
    left: { kind: 'nodes', width: 300 },
    centre: { kind: 'viewer' },
    right: { kind: 'grade-tools', width: 360 },
    timeline: 180,
    timelineVisible: true,
    timelineOnDemand: null,
    overview: null,
    scopes: 150,
    viewer: 'centre',
  },
  audio: {
    page: 'audio',
    left: { kind: 'mixer', width: 360 },
    centre: { kind: 'viewer' },
    right: { kind: 'buses', width: 320 },
    timeline: 280,
    timelineVisible: true,
    timelineOnDemand: null,
    overview: null,
    scopes: null,
    viewer: 'centre',
  },
  deliver: {
    page: 'deliver',
    left: { kind: 'settings', width: 420 },
    centre: null,
    right: { kind: 'result', width: 0, fills: true },
    timeline: null,
    timelineVisible: false,
    timelineOnDemand: null,
    overview: null,
    scopes: null,
    viewer: 'none',
  },
});

/** Nothing becomes narrower than this. */
export const LAYOUT_MINIMUMS = Object.freeze({ side: 240, timeline: 160, overview: 120, centre: 320 });

/** The storage record: `{ [page]: { fractions } }`, per device (suffixed by the user id when known). */
export const LAYOUT_STORAGE_KEY = 'reelbolt:editor:layout:v2';

export interface Bounds {
  width: number;
  height: number;
}

export interface PageFractions {
  left?: number;
  right?: number;
  timeline?: number;
  overview?: number;
}

export interface PagePixels {
  left: number;
  right: number;
  timeline: number;
  overview: number;
}

export type LayoutRecord = Partial<Record<EditorPage, { fractions: PageFractions }>>;

/** The part of `localStorage` this module needs (a fake is enough in a test). */
export interface LayoutStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem?(key: string): void;
}

const clamp = (value: number, low: number, high: number): number => Math.min(Math.max(low, value), Math.max(low, high));
const round4 = (value: number): number => Math.round(value * 10000) / 10000;

/** The per-device key; a signed-in user gets their own record on the same browser. */
export function layoutStorageKey(userId?: string | null): string {
  return userId ? `${LAYOUT_STORAGE_KEY}:${userId}` : LAYOUT_STORAGE_KEY;
}

function isFraction(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 && value <= 1;
}

function parseFractions(raw: unknown): PageFractions | null {
  if (!raw || typeof raw !== 'object') return null;
  const source = raw as Record<string, unknown>;
  const fractions: PageFractions = {};
  for (const key of ['left', 'right', 'timeline', 'overview'] as const) {
    if (isFraction(source[key])) fractions[key] = source[key];
  }
  return Object.keys(fractions).length > 0 ? fractions : null;
}

/** A stored record; a bad page or a bad fraction is dropped rather than trusted. */
export function parseLayoutRecord(raw: string | null): LayoutRecord {
  if (!raw) return {};
  try {
    const parsed = JSON.parse(raw) as unknown;
    if (!parsed || typeof parsed !== 'object') return {};
    const record: LayoutRecord = {};
    for (const page of EDITOR_PAGES) {
      const entry = (parsed as Record<string, unknown>)[page];
      if (!entry || typeof entry !== 'object') continue;
      const fractions = parseFractions((entry as Record<string, unknown>).fractions);
      if (fractions) record[page] = { fractions };
    }
    return record;
  } catch {
    return {};
  }
}

/** Storage off, full or holding junk is a soft failure: the table defaults apply. */
export function readLayoutRecord(storage: LayoutStorage | null | undefined, userId?: string | null): LayoutRecord {
  if (!storage) return {};
  try {
    return parseLayoutRecord(storage.getItem(layoutStorageKey(userId)));
  } catch {
    return {};
  }
}

export function writeLayoutRecord(storage: LayoutStorage | null | undefined, record: LayoutRecord, userId?: string | null): void {
  if (!storage) return;
  try {
    storage.setItem(layoutStorageKey(userId), JSON.stringify(record));
  } catch {
    // A convenience, never document state: the layout stays for this visit.
  }
}

/** The stored fractions for one page, or `null` when that page has never been resized. */
export function pageFractions(record: LayoutRecord, page: EditorPage): PageFractions | null {
  return record[page]?.fractions ?? null;
}

/** The table's opening size in pixels, before any clamp against a real window. */
export function defaultPagePixels(page: EditorPage): PagePixels {
  const geometry = PAGE_GEOMETRY[page];
  return {
    left: geometry.left?.width ?? 0,
    right: geometry.right && !geometry.right.fills ? geometry.right.width : 0,
    timeline: geometry.timeline ?? 0,
    overview: geometry.overview ?? 0,
  };
}

/**
 * Pixels for a page's fractions in a container of `bounds`. Every zone is clamped to its minimum;
 * a page with a centre keeps at least `LAYOUT_MINIMUMS.centre` for it. Missing fractions use the
 * table's pixel size, so a fresh page opens exactly as §6.1 says.
 */
export function fractionsToPixels(page: EditorPage, fractions: PageFractions, bounds: Bounds): PagePixels {
  const geometry = PAGE_GEOMETRY[page];
  const width = Math.max(1, Math.round(bounds.width));
  const height = Math.max(1, Math.round(bounds.height));
  const min = LAYOUT_MINIMUMS.side;
  const centreMin = geometry.centre ? LAYOUT_MINIMUMS.centre : min;
  const defaults = defaultPagePixels(page);

  let left = geometry.left
    ? fractions.left !== undefined
      ? clamp(Math.round(fractions.left * width), min, width)
      : defaults.left
    : 0;
  let right = geometry.right && !geometry.right.fills
    ? fractions.right !== undefined
      ? clamp(Math.round(fractions.right * width), min, width)
      : defaults.right
    : 0;

  // The centre (or, on a two-column page, the minimum left column) never disappears.
  left = Math.min(left, Math.max(min, width - right - centreMin));
  if (geometry.right && !geometry.right.fills) right = Math.min(right, Math.max(min, width - left - centreMin));
  if (geometry.right?.fills) right = Math.max(0, width - left);

  let timeline = 0;
  if (geometry.timeline !== null) {
    timeline = fractions.timeline !== undefined
      ? clamp(Math.round(fractions.timeline * height), LAYOUT_MINIMUMS.timeline, Math.max(LAYOUT_MINIMUMS.timeline, height - LAYOUT_MINIMUMS.overview))
      : defaults.timeline;
  }

  let overview = 0;
  if (geometry.overview !== null) {
    overview = fractions.overview !== undefined
      ? clamp(Math.round(fractions.overview * height), LAYOUT_MINIMUMS.overview, Math.max(LAYOUT_MINIMUMS.overview, height - timeline - LAYOUT_MINIMUMS.timeline))
      : defaults.overview;
  }

  return { left, right, timeline, overview };
}

/** The fractions a pixel layout means in this container — what gets stored and re-derived on resize. */
export function pixelsToFractions(page: EditorPage, pixels: PagePixels, bounds: Bounds): PageFractions {
  const geometry = PAGE_GEOMETRY[page];
  const width = Math.max(1, bounds.width);
  const height = Math.max(1, bounds.height);
  const fractions: PageFractions = {};
  if (geometry.left) fractions.left = round4(pixels.left / width);
  if (geometry.right && !geometry.right.fills) fractions.right = round4(pixels.right / width);
  if (geometry.timeline !== null) fractions.timeline = round4(pixels.timeline / height);
  if (geometry.overview !== null) fractions.overview = round4(pixels.overview / height);
  return fractions;
}

/**
 * Hydration order for one page: the stored fractions for that page, else the table default. The URL
 * page has already selected `page` before this is called.
 */
export function resolvePagePixels(page: EditorPage, record: LayoutRecord, bounds: Bounds): PagePixels {
  return fractionsToPixels(page, pageFractions(record, page) ?? {}, bounds);
}

export function setPageFractions(record: LayoutRecord, page: EditorPage, fractions: PageFractions): LayoutRecord {
  return { ...record, [page]: { fractions } };
}

/** Reset this page: it opens at the table default again, the other pages keep their fractions. */
export function resetPage(record: LayoutRecord, page: EditorPage): LayoutRecord {
  const next: LayoutRecord = { ...record };
  delete next[page];
  return next;
}

/** Reset every page: the whole record goes. */
export function resetAllPages(): LayoutRecord {
  return {};
}

export interface PoolPanesMigration {
  record: LayoutRecord;
  /** True when the legacy key existed: it was read (once) and deleted. */
  migrated: boolean;
}

/**
 * The pool-panes key is read ONCE, mapped onto the Media page's fractions and deleted. If the media
 * entry already has fractions, those win; the legacy key is still deleted so it is never read again.
 * Storage off or the removal refused is a soft failure (the caller keeps working; the old key simply
 * survives for a later attempt).
 */
export function migratePoolPanes(
  storage: LayoutStorage | null | undefined,
  bounds: Bounds,
  userId?: string | null,
): PoolPanesMigration {
  const record = readLayoutRecord(storage, userId);
  if (!storage) return { record, migrated: false };
  let legacy: string | null = null;
  try {
    legacy = storage.getItem(POOL_PANES_STORAGE_KEY);
  } catch {
    legacy = null;
  }
  if (legacy === null) return { record, migrated: false };

  const panes = parsePoolPanes(legacy);
  const width = Math.max(1, bounds.width);
  const existing = record.media?.fractions ?? {};
  const fractions: PageFractions = { ...existing };
  if (existing.left === undefined) fractions.left = round4(clamp(panes.binsWidth / width, 0, 1));
  if (existing.right === undefined) fractions.right = round4(clamp(panes.detailWidth / width, 0, 1));
  const next = existing.left === undefined || existing.right === undefined
    ? setPageFractions(record, 'media', fractions)
    : record;
  writeLayoutRecord(storage, next, userId);
  try {
    storage.removeItem?.(POOL_PANES_STORAGE_KEY);
  } catch {
    // The key stays if storage refuses; the next boot tries again.
  }
  return { record: next, migrated: true };
}

/** The tablet drawer button is named after the zone it opens, never a generic label over the picture. */
export type DrawerZone = 'viewer' | 'inspector' | 'gradeTools' | 'properties' | 'buses';

export function drawerZoneForPage(page: EditorPage): DrawerZone {
  switch (page) {
    case 'media':
      return 'viewer';
    case 'colour':
      return 'gradeTools';
    case 'compose':
      return 'properties';
    case 'audio':
      return 'buses';
    default:
      return 'inspector';
  }
}
