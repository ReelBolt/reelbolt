'use client';

import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Box, Button } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { fromFractions } from '@/lib/editing/layout-fractions';
import {
  LAYOUT_MINIMUMS,
  PAGE_GEOMETRY,
  drawerZoneForPage,
  fractionsToPixels,
  pageFractions,
  pixelsToFractions,
  readLayoutRecord,
  resolvePagePixels,
  setPageFractions,
  writeLayoutRecord,
  type Bounds,
  type DrawerZone,
  type PageFractions,
  type PagePixels,
} from '@/lib/editing/layout/editor-layout';
import type { EditorPage } from '@/lib/editing/page-url';
import classes from './workspace.module.css';
import { useT } from '@/i18n';

export interface PanelLayoutProps {
  /**
   * The page whose geometry this frame renders. When set, the frame reads the page's entry from the
   * one layout table/record; the per-call-site initial widths are ignored.
   */
  page?: EditorPage;
  /** Left column (media / markers / edit index). */
  left?: React.ReactNode;
  /** The viewer, centre-top. Deliver (no centre) passes its result column as `right` instead. */
  viewer?: React.ReactNode;
  /** The inspector, right of the viewer. On Deliver this is the result column, which fills the rest. */
  right?: React.ReactNode;
  /** The timeline, full width below. Omitted when the page has none (Deliver). */
  timeline?: React.ReactNode;
  /** The overview band between the viewer row and the timeline (Cut). */
  overview?: React.ReactNode;
  /** The signed-in user id, when the page knows it: the layout record is then keyed per user. */
  userId?: string | null;
  /** The tablet drawer button names the zone it opens (Viewer / Inspector / Grade tools / Properties). */
  drawerZone?: DrawerZone;
  /** Legacy fallback size, CSS px. Only used when `page` is not given. */
  initialLeftWidth?: number;
  /** Legacy fallback size, CSS px. Only used when `page` is not given. */
  initialRightWidth?: number;
  /** Legacy fallback size, CSS px. Only used when `page` is not given. */
  initialTimelineHeight?: number;
  /** Legacy per-viewer key. Only used when `page` is not given. */
  storageKey?: string;
  minPanel?: number;
  /** P9: a saved layout to apply (a new object applies it again). */
  appliedSizes?: Sizes | null;
  /** P9: reports the current sizes, so a layout can be saved. */
  onSizesChange?: (sizes: Sizes, bounds: Bounds) => void;
  /** R12.7: a saved layout in fractions of the window (a new object applies it again). */
  appliedFractions?: Sizes | null;
  /** Bumping this re-reads the page's record after a reset (this page or every page). */
  resetToken?: number;
  /**
   * Touch tablets (coarse pointer, 768-1100 px): the right panel (the Inspector) stays docked beside the
   * viewer instead of hiding behind a button. Desktop windows of the same width are unchanged.
   */
  dockRightOnTablet?: boolean;
  /** Overrides the page's default (Media starts with the timeline hidden and reveals it on demand). */
  timelineVisible?: boolean;
  /** Shown just above the timeline (Media's Show timeline control); on a page whose timeline is on demand. */
  timelineToggle?: React.ReactNode;
}

/** The narrowest the left column is allowed to be on a tablet, so its buttons are not clipped. */
const NARROW_LEFT_MIN = 300;
/** The left panel's floor in the touch two-pane layout under 1000 px. */
const TWO_PANE_LEFT_MIN = 240;
const TWO_PANE_RIGHT_MIN = 220;

/** Panel widths in the touch two-pane layout: under 1000 px the compact minimums are the actual widths (viewer about 303 px at 820); from 1000 px the stored sizes apply (300 / 320 by default, the Inspector capped at 300). */
export function twoPaneWidths(viewport: number, sizes: { left: number; right: number }): { left: number; right: number } {
  if (viewport < 1000) return { left: TWO_PANE_LEFT_MIN, right: TWO_PANE_RIGHT_MIN };
  return { left: Math.max(sizes.left, NARROW_LEFT_MIN), right: Math.min(Math.max(sizes.right, 240), 300) };
}

/** The docked-Inspector layout applies on touch tablets only (coarse pointer, 768-1100 px); a desktop window never gets it. */
export function tabletTwoPane(f: { dock: boolean; narrow: boolean; touch: boolean; wideEnough: boolean; hasRight: boolean }): boolean {
  return f.dock && f.narrow && f.touch && f.wideEnough && f.hasRight;
}

export interface Sizes {
  left: number;
  right: number;
  timeline: number;
  overview?: number;
}

function readSizes(key: string | undefined, fallback: Sizes): Sizes {
  if (!key || typeof window === 'undefined') return fallback;
  try {
    const raw = window.localStorage.getItem(key);
    if (!raw) return fallback;
    const parsed = JSON.parse(raw) as Partial<Sizes>;
    return {
      left: typeof parsed.left === 'number' ? parsed.left : fallback.left,
      right: typeof parsed.right === 'number' ? parsed.right : fallback.right,
      timeline: typeof parsed.timeline === 'number' ? parsed.timeline : fallback.timeline,
    };
  } catch {
    return fallback;
  }
}

function sizesFromPixels(pixels: PagePixels): Sizes {
  return { left: pixels.left, right: pixels.right, timeline: pixels.timeline, overview: pixels.overview };
}

function pixelsFromSizes(sizes: Sizes, fallbackOverview: number): PagePixels {
  return { left: sizes.left, right: sizes.right, timeline: sizes.timeline, overview: sizes.overview ?? fallbackOverview };
}

function browserStorage(): Storage | null {
  return typeof window === 'undefined' ? null : window.localStorage;
}

/**
 * The resizable panel layout: `[left | viewer | right]` over `[timeline]`, with drag handles.
 *
 * With a `page`, the sizes come from the one geometry table and are persisted as fractions in the
 * shared layout record (`lib/editing/layout/editor-layout.ts`): the URL page selects the entry, stored
 * fractions win, and the table default is the fallback. Without a page the legacy per-viewer
 * `storageKey` sizes are kept, so older call sites and tests still behave.
 *
 * Panel sizes are per-viewer conveniences, so they live in `localStorage` (wrapped in try/catch —
 * a private window or blocked storage just means defaults). They are never part of the document.
 */
export function PanelLayout({
  page,
  left,
  viewer,
  right,
  timeline,
  overview,
  userId = null,
  drawerZone,
  initialLeftWidth = 280,
  initialRightWidth = 300,
  initialTimelineHeight = 320,
  storageKey,
  minPanel = 160,
  appliedSizes = null,
  onSizesChange,
  appliedFractions = null,
  resetToken = 0,
  dockRightOnTablet = false,
  timelineVisible,
  timelineToggle,
}: PanelLayoutProps) {
  const t = useT('editor.shell.panelLayout');
  const spec = page ? PAGE_GEOMETRY[page] : null;
  const legacyFallback: Sizes = { left: initialLeftWidth, right: initialRightWidth, timeline: initialTimelineHeight };
  const fallback: Sizes = spec
    ? { left: spec.left?.width ?? 0, right: spec.right && !spec.right.fills ? spec.right.width : 0, timeline: spec.timeline ?? 0, overview: spec.overview ?? 0 }
    : legacyFallback;
  const [sizes, setSizes] = useState<Sizes>(fallback);
  // A page change must not leave the previous page's pixels on screen (or in "Save current") for the
  // one tick before the stored fractions hydrate: adjust the state during render instead.
  const [renderedPage, setRenderedPage] = useState(page);
  if (renderedPage !== page) {
    setRenderedPage(page);
    setSizes(fallback);
  }
  const containerRef = useRef<HTMLDivElement | null>(null);
  // Tablet (<= 1100px): the inspector becomes a drawer behind the named button, the left panel
  // keeps a readable minimum, and the timeline gets at least 45% of the height. Nothing is removed.
  const narrow = useMediaQuery('(max-width: 68.75em)', false, { getInitialValueInEffect: false }) ?? false;
  const [inspectorOpen, setInspectorOpen] = useState(false);
  const touch = useMediaQuery('(pointer: coarse)', false, { getInitialValueInEffect: false }) ?? false;
  const wideEnough = useMediaQuery('(min-width: 48em)', false, { getInitialValueInEffect: false }) ?? false;
  const twoPane = tabletTwoPane({ dock: dockRightOnTablet, narrow, touch, wideEnough, hasRight: right !== undefined });
  // Under 1000 px the docked Inspector and the left panel share the width: the left panel gives up some of its minimum.
  const wide1000 = useMediaQuery('(min-width: 62.5em)', false, { getInitialValueInEffect: false }) ?? false;

  // True once the stored sizes have been read. Writing before that would overwrite them with the
  // defaults on every mount, so a resized panel never survived a reload.
  const hydrated = useRef(false);
  // Only a change the user made (drag, keyboard, applied layout) is written back: hydration and the
  // table default never rewrite the record on their own.
  const dirty = useRef(false);
  const fractionsRef = useRef<PageFractions | null>(null);
  const boundsRef = useRef<Bounds>({ width: 0, height: 0 });

  const measure = useCallback((): Bounds => {
    const box = containerRef.current?.getBoundingClientRect();
    return { width: box && box.width > 0 ? box.width : 1200, height: box && box.height > 0 ? box.height : 800 };
  }, []);

  const minFor = useCallback(
    (which: keyof Sizes): number => {
      if (!page) return minPanel;
      if (which === 'timeline') return LAYOUT_MINIMUMS.timeline;
      if (which === 'overview') return LAYOUT_MINIMUMS.overview;
      return LAYOUT_MINIMUMS.side;
    },
    [minPanel, page],
  );

  const persistFractions = useCallback(
    (fractions: PageFractions) => {
      if (!page) return;
      fractionsRef.current = fractions;
      const record = setPageFractions(readLayoutRecord(browserStorage(), userId), page, fractions);
      writeLayoutRecord(browserStorage(), record, userId);
    },
    [page, userId],
  );

  // Hydration: the page's stored fractions, else the table default. Deferred by one tick because the
  // record is a client-only convenience and applying it after hydration keeps server/client markup equal.
  useEffect(() => {
    hydrated.current = false;
    dirty.current = false;
    const handle = window.setTimeout(() => {
      const bounds = measure();
      boundsRef.current = bounds;
      if (page) {
        const record = readLayoutRecord(browserStorage(), userId);
        fractionsRef.current = pageFractions(record, page);
        setSizes(sizesFromPixels(resolvePagePixels(page, record, bounds)));
      } else {
        setSizes(readSizes(storageKey, legacyFallback));
      }
      hydrated.current = true;
    }, 0);
    return () => window.clearTimeout(handle);
    // Only on mount or when the page/key changes: the fallback object is rebuilt every render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [storageKey, page, userId]);

  useEffect(() => {
    if (!appliedSizes) return;
    const handle = window.setTimeout(() => {
      const bounds = measure();
      const pixels = pixelsFromSizes(appliedSizes, spec?.overview ?? 0);
      const next: Sizes = {
        left: Math.max(minFor('left'), pixels.left),
        right: Math.max(minFor('right'), pixels.right),
        timeline: Math.max(minFor('timeline'), pixels.timeline),
        overview: pixels.overview,
      };
      if (page) {
        persistFractions(pixelsToFractions(page, pixelsFromSizes(next, spec?.overview ?? 0), bounds));
        dirty.current = true;
      }
      setSizes(next);
    }, 0);
    return () => window.clearTimeout(handle);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [appliedSizes, page, minFor, persistFractions]);

  useEffect(() => {
    if (!appliedFractions) return;
    const handle = window.setTimeout(() => {
      const bounds = measure();
      if (page) {
        persistFractions(appliedFractions);
        setSizes(sizesFromPixels(fractionsToPixels(page, appliedFractions, bounds)));
        dirty.current = true;
      } else {
        setSizes(fromFractions(appliedFractions, bounds, minPanel));
      }
    }, 0);
    return () => window.clearTimeout(handle);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [appliedFractions, page, minPanel, persistFractions]);

  // A reset (this page or every page) re-reads the record; the caller has already removed the
  // entries, so the table default applies again.
  useEffect(() => {
    if (resetToken === 0) return;
    const handle = window.setTimeout(() => {
      const bounds = measure();
      if (page) {
        const record = readLayoutRecord(browserStorage(), userId);
        fractionsRef.current = pageFractions(record, page);
        setSizes(sizesFromPixels(resolvePagePixels(page, record, bounds)));
      } else {
        setSizes(legacyFallback);
      }
      dirty.current = false;
      hydrated.current = true;
    }, 0);
    return () => window.clearTimeout(handle);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [resetToken, page, userId]);

  useEffect(() => {
    const bounds = measure();
    boundsRef.current = bounds;
    onSizesChange?.(sizes, bounds);
  }, [sizes, measure, onSizesChange]);

  // Fractions are authoritative: a window resize re-derives the pixels, so a stored layout never drifts.
  useEffect(() => {
    if (!page) return;
    const onResize = () => {
      const bounds = measure();
      boundsRef.current = bounds;
      if (fractionsRef.current) setSizes(sizesFromPixels(fractionsToPixels(page, fractionsRef.current, bounds)));
    };
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, [measure, page]);

  useEffect(() => {
    if (!page || !hydrated.current || !dirty.current) return;
    const bounds = boundsRef.current.width > 0 ? boundsRef.current : measure();
    persistFractions(pixelsToFractions(page, pixelsFromSizes(sizes, spec?.overview ?? 0), bounds));
  }, [sizes, page, measure, persistFractions, spec?.overview]);

  // Legacy (no page): keep the old per-viewer key exactly as before.
  useEffect(() => {
    if (page || !storageKey || typeof window === 'undefined' || !hydrated.current) return;
    try {
      window.localStorage.setItem(storageKey, JSON.stringify({ left: sizes.left, right: sizes.right, timeline: sizes.timeline }));
    } catch {
      /* storage unavailable: sizes stay for this session only */
    }
  }, [sizes, storageKey, page]);

  const startDrag = useCallback(
    (which: keyof Sizes, event: React.PointerEvent) => {
      event.preventDefault();
      const startX = event.clientX;
      const startY = event.clientY;
      const start = sizes[which] ?? 0;
      const container = containerRef.current;
      const onMove = (move: PointerEvent) => {
        dirty.current = true;
        setSizes((current) => {
          const bound = container ? container.getBoundingClientRect() : null;
          let next = start;
          if (which === 'left') next = start + (move.clientX - startX);
          if (which === 'right') next = start - (move.clientX - startX);
          if (which === 'timeline') next = start - (move.clientY - startY);
          const other = which === 'left' ? current.right : which === 'right' ? current.left : 0;
          const max = which === 'timeline'
            ? bound ? bound.height - LAYOUT_MINIMUMS.timeline : 900
            : bound ? bound.width - other - LAYOUT_MINIMUMS.centre : 900;
          return { ...current, [which]: Math.max(minFor(which), Math.min(max, next)) };
        });
      };
      const onUp = () => {
        window.removeEventListener('pointermove', onMove);
        window.removeEventListener('pointerup', onUp);
      };
      window.addEventListener('pointermove', onMove);
      window.addEventListener('pointerup', onUp);
    },
    [minFor, sizes],
  );

  // Keyboard resizing: a focused separator moves 16px per arrow press (64px with Shift), the
  // WAI-ARIA window-splitter pattern, so the layout is not mouse-only.
  const onHandleKey = useCallback(
    (which: keyof Sizes, event: React.KeyboardEvent) => {
      const grow = which === 'right' || which === 'timeline' ? -1 : 1;
      const keys: Record<string, number> =
        which === 'timeline' ? { ArrowUp: -1, ArrowDown: 1 } : { ArrowLeft: -1, ArrowRight: 1 };
      const direction = keys[event.key];
      if (direction === undefined) return;
      event.preventDefault();
      dirty.current = true;
      const step = (event.shiftKey ? 64 : 16) * direction * grow;
      const min = minFor(which);
      setSizes((current) => ({ ...current, [which]: Math.max(min, Math.min(1200, (current[which] ?? 0) + step)) }));
    },
    [minFor],
  );

  const leftWidth = twoPane ? twoPaneWidths(wide1000 ? 1000 : 999, sizes).left : narrow ? Math.max(sizes.left, NARROW_LEFT_MIN) : sizes.left;
  // A tablet-width pane (768-1100 px) gives the timeline at most 44% of the height (never under its minimum), so the viewer keeps its picture.
  const timelineBasis = narrow && wideEnough ? `max(${LAYOUT_MINIMUMS.timeline}px, min(${sizes.timeline}px, 44%))` : narrow ? `max(${sizes.timeline}px, 45%)` : `${sizes.timeline}px`;
  const rightPane = right !== undefined && (
    <Box style={{ flex: `0 0 ${sizes.right}px`, minWidth: 0, overflow: 'auto' }} data-testid="panel-right">
      {right}
    </Box>
  );

  const centrePresent = spec ? spec.centre !== null : true;
  const rightFills = spec?.right?.fills === true;
  const pageHasTimeline = spec ? spec.timeline !== null : true;
  const showTimeline = timeline !== undefined && pageHasTimeline && (timelineVisible ?? (spec ? spec.timelineVisible : true));
  const showOverview = overview !== undefined && (spec ? spec.overview !== null : (sizes.overview ?? 0) > 0);
  const drawerZoneName = drawerZone ?? (page ? drawerZoneForPage(page) : 'inspector');
  const drawerLabel =
    drawerZoneName === 'viewer'
      ? t('drawerViewer')
      : drawerZoneName === 'gradeTools'
        ? t('drawerGradeTools')
        : drawerZoneName === 'properties'
          ? t('drawerProperties')
          : drawerZoneName === 'buses'
            ? t('drawerBuses')
            : t('drawerInspector');

  return (
    <Box
      ref={containerRef}
      data-testid="panel-layout"
      data-page={page}
      style={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0, width: '100%' }}
    >
      <Box style={{ display: 'flex', flex: '1 1 auto', minHeight: 0, position: 'relative' }}>
        {left !== undefined && (
          <>
            <Box style={{ flex: `0 0 ${leftWidth}px`, minWidth: 0, overflow: 'auto' }} data-testid="panel-left">
              {left}
            </Box>
            <Box
              role="separator"
              aria-orientation="vertical"
              aria-label={t('resizeLeftPanel')}
              tabIndex={0}
              aria-valuenow={sizes.left}
              onKeyDown={(e) => onHandleKey('left', e)}
              onPointerDown={(e) => startDrag('left', e)}
              className={classes.handle}
              style={{ cursor: 'col-resize' }}
            />
          </>
        )}
        {centrePresent && (
          <Box
            // Tablet: the floating drawer button sits over the viewer's top-right corner, so the viewer keeps clear of it (the parity badge lives there).
            style={{ flex: '1 1 auto', minWidth: 0, display: 'flex', flexDirection: 'column', paddingRight: right !== undefined && narrow && !twoPane ? 96 : undefined }}
            data-testid="panel-viewer"
          >
            {viewer}
          </Box>
        )}
        {!centrePresent && right !== undefined && (
          <Box style={{ flex: '1 1 auto', minWidth: 0, overflow: 'auto' }} data-testid="panel-right">
            {right}
          </Box>
        )}
        {centrePresent && twoPane && (
          <Box style={{ flex: `0 0 ${twoPaneWidths(wide1000 ? 1000 : 999, sizes).right}px`, minWidth: 0, overflow: 'auto', borderLeft: '1px solid var(--rb-ed-line-strong)', background: 'var(--rb-ed-inspector-bg)' }} data-testid="panel-right">
            {right}
          </Box>
        )}
        {centrePresent && right !== undefined && narrow && !twoPane && (
          <>
            <Box style={{ position: 'absolute', top: 6, right: 6, zIndex: 3 }}>
              <Button
                size="compact-sm"
                variant={inspectorOpen ? 'filled' : 'default'}
                aria-expanded={inspectorOpen}
                aria-controls="panel-inspector-drawer"
                onClick={() => setInspectorOpen((o) => !o)}
                data-testid="inspector-toggle"
                style={{ minHeight: 44 }}
              >
                {drawerLabel}
              </Button>
            </Box>
            {inspectorOpen && (
              <Box
                id="panel-inspector-drawer"
                style={{ position: 'absolute', top: 0, right: 0, bottom: 0, width: Math.min(sizes.right, 360), maxWidth: '90%', zIndex: 2, background: 'var(--rb-ed-inspector-bg)', borderLeft: '1px solid var(--rb-ed-line-strong)', overflow: 'auto', paddingTop: 40 }}
                data-testid="panel-right"
                onKeyDown={(e) => {
                  if (e.key === 'Escape') setInspectorOpen(false);
                }}
              >
                {right}
              </Box>
            )}
          </>
        )}
        {centrePresent && right !== undefined && !narrow && !twoPane && !rightFills && (
          <>
            <Box
              role="separator"
              aria-orientation="vertical"
              aria-label={t('resizeRightPanel')}
              tabIndex={0}
              aria-valuenow={sizes.right}
              onKeyDown={(e) => onHandleKey('right', e)}
              onPointerDown={(e) => startDrag('right', e)}
              className={classes.handle}
              style={{ cursor: 'col-resize' }}
            />
            {rightPane}
          </>
        )}
      </Box>
      {showOverview && (
        <Box
          style={{ flex: `0 0 ${sizes.overview ?? spec?.overview ?? 0}px`, minHeight: 0, overflow: 'hidden', borderTop: '1px solid var(--rb-ed-line-strong)' }}
          data-testid="panel-overview"
        >
          {overview}
        </Box>
      )}
      {timelineToggle && pageHasTimeline && (
        <Box
          style={{ display: 'flex', justifyContent: 'flex-end', alignItems: 'center', padding: '2px 8px', borderTop: '1px solid var(--rb-ed-line-strong)', background: 'var(--rb-ed-inspector-bg)' }}
          data-testid="panel-timeline-toggle"
        >
          {timelineToggle}
        </Box>
      )}
      {showTimeline && (
        <>
          <Box
            role="separator"
            aria-orientation="horizontal"
            aria-label={t('resizeTimeline')}
            tabIndex={0}
            aria-valuenow={sizes.timeline}
            onKeyDown={(e) => onHandleKey('timeline', e)}
            onPointerDown={(e) => startDrag('timeline', e)}
            className={`${classes.handle} ${classes.handleRow}`}
            style={{ cursor: 'row-resize' }}
          />
          <Box style={{ flex: `0 0 ${timelineBasis}`, minHeight: 0, overflow: 'hidden' }} data-testid="panel-timeline">
            {timeline}
          </Box>
        </>
      )}
    </Box>
  );
}
