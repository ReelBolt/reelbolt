'use client';

import { useCallback, useEffect, useRef, useState } from 'react';

/**
 * How much of the timeline toolbar is folded: first the trim control into its menu, then the frame steppers into
 * theirs, then the zoom slider (the zoom buttons stay and do the same job), last the words on the text
 * buttons (Remove silence, Cut to the music, Close all gaps: their icons stay, named and with a hint).
 */
export type FoldLevel = 0 | 1 | 2 | 3 | 4;
export const MAX_FOLD: FoldLevel = 4;

export interface ToolbarFold {
  trim: boolean;
  steppers: boolean;
  zoomSlider: boolean;
  /** The text buttons show their icon alone. */
  labels: boolean;
}
export const foldOf = (level: FoldLevel): ToolbarFold => ({ trim: level >= 1, steppers: level >= 2, zoomSlider: level >= 3, labels: level >= 4 });

/** The width the tools need at each level, as far as it has been measured. */
export type NeededWidths = readonly (number | undefined)[];

/**
 * One step of the fold, from a fresh measure of the current level. A change of width at this level (a button
 * appears, the language changes) shifts the other levels by the same amount. It unfolds only when the wider
 * level is known to fit, so it cannot oscillate: a wrong guess is corrected by that level's own measure.
 */
export function nextFold(level: FoldLevel, measured: number, available: number, known: NeededWidths): { level: FoldLevel; known: NeededWidths } {
  const before = known[level];
  const delta = before === undefined ? 0 : measured - before;
  const updated = known.map((width, i) => (i === level ? measured : width === undefined ? undefined : width + delta));
  while (updated.length <= level) updated.push(undefined);
  updated[level] = measured;
  if (measured > available) return { level: level < MAX_FOLD ? ((level + 1) as FoldLevel) : level, known: updated };
  if (level > 0) {
    const wider = updated[level - 1];
    if (wider !== undefined && wider <= available) return { level: (level - 1) as FoldLevel, known: updated };
  }
  return { level, known: updated };
}

/** What the toolbar's children need, whatever room they have now. */
function neededWidth(bar: HTMLElement): number {
  const style = getComputedStyle(bar);
  const gap = parseFloat(style.columnGap) || 0;
  const padding = (parseFloat(style.paddingLeft) || 0) + (parseFloat(style.paddingRight) || 0);
  const children = Array.from(bar.children) as HTMLElement[];
  return children.reduce((sum, child) => sum + child.offsetWidth, padding + gap * Math.max(0, children.length - 1));
}

/** Folds on measured overflow. A ResizeObserver only: it runs when a width changes, never per frame. */
export function useToolbarFold(enabled: boolean): { ref: (bar: HTMLDivElement | null) => void; fold: ToolbarFold } {
  const [level, setLevel] = useState<FoldLevel>(0);
  const levelRef = useRef<FoldLevel>(0);
  const known = useRef<NeededWidths>([]);
  const [bar, setBar] = useState<HTMLDivElement | null>(null);

  const measure = useCallback(() => {
    if (!bar) return;
    const available = bar.clientWidth;
    if (available === 0) return;
    const next = nextFold(levelRef.current, neededWidth(bar), available, known.current);
    known.current = next.known;
    levelRef.current = next.level;
    setLevel(next.level);
    const more = bar.scrollLeft + bar.clientWidth < bar.scrollWidth - 1;
    if (more && next.level === MAX_FOLD) bar.setAttribute('data-more-end', 'true');
    else bar.removeAttribute('data-more-end');
  }, [bar]);

  useEffect(() => {
    if (!enabled || !bar || typeof ResizeObserver === 'undefined') return undefined;
    const observer = new ResizeObserver(measure);
    observer.observe(bar);
    Array.from(bar.children).forEach((child) => observer.observe(child));
    bar.addEventListener('scroll', measure, { passive: true });
    return () => {
      observer.disconnect();
      bar.removeEventListener('scroll', measure);
    };
  }, [enabled, bar, measure]);

  return { ref: setBar, fold: foldOf(enabled ? level : 0) };
}
