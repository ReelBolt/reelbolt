'use client';

/**
 * T-008: the transient grade preview behind a live colour-wheel drag. The editor publishes the stack a
 * drag WOULD write (never the document itself) through `GradePreviewContext`; the workspace derives the
 * document the viewer composites by replacing that one item's or colour group's `grade`, so the picture
 * follows the wheel while the document — and the undo history — stays untouched until release.
 *
 * `withGradePreview` is pure: it returns the SAME document object when there is nothing to preview (no
 * preview, a missing target, or a stack already identical), so an idle preview never invalidates the
 * viewer's memoised document.
 */

import { createContext, useContext } from 'react';
import type { AdjustmentClip, ColorGroup, EditTimeline, GradeStack, Item, MediaClip } from '../types';

/** Where a transient grade belongs: a timeline item (media or adjustment clip) or a colour group. */
export interface GradePreviewTarget {
  kind: 'item' | 'group';
  id: string;
}

/** The stack a drag would write, for the target it belongs to. `stack: null` means an empty grade. */
export interface GradePreview {
  target: GradePreviewTarget;
  stack: GradeStack | null;
  /**
   * Further stacks shown together with this one. Solo uses it: with one node of a clip soloed, the clip's
   * colour group stack is switched off in the same preview (and the other way round), so only that node
   * changes the picture. Like the main stack, none of this is written.
   */
  also?: readonly { target: GradePreviewTarget; stack: GradeStack | null }[];
}

export interface GradePreviewValue {
  /** Publishes (or, with `null`, clears) the live preview. */
  set(preview: GradePreview | null): void;
}

/** The no-op default keeps the grade editor usable on its own (tests, the phone surface). */
export const GradePreviewContext = createContext<GradePreviewValue>({ set: () => undefined });

export function useGradePreview(): GradePreviewValue {
  return useContext(GradePreviewContext);
}

const isGradableItem = (item: Item): item is MediaClip | AdjustmentClip => item.type === 'mediaClip' || item.type === 'adjustmentClip';

/** The document with the previewed grade in place; the same object when nothing would change. */
export function withGradePreview(doc: EditTimeline, preview: GradePreview | null): EditTimeline {
  if (!preview) return doc;
  // A preview that stays up (a soloed node) is asked for again on every render of the workspace: the same
  // document and the same preview object give back the same derived document, so the viewer's memoised
  // document holds while nothing changed.
  if (last && last.doc === doc && last.preview === preview) return last.result;
  let result = withOneStack(doc, preview.target, preview.stack);
  for (const extra of preview.also ?? []) result = withOneStack(result, extra.target, extra.stack);
  last = { doc, preview, result };
  return result;
}

let last: { doc: EditTimeline; preview: GradePreview; result: EditTimeline } | null = null;

function withOneStack(doc: EditTimeline, target: GradePreviewTarget, stack: GradeStack | null): EditTimeline {
  if (target.kind === 'group') {
    const groups = doc.colorGroups;
    const index = groups?.findIndex((group) => group.id === target.id) ?? -1;
    if (!groups || index < 0) return doc;
    if (groups[index].grade === stack) return doc;
    const next: ColorGroup[] = groups.map((group, i) => (i === index ? { ...group, grade: stack } : group));
    return { ...doc, colorGroups: next };
  }

  for (let t = 0; t < doc.tracks.length; t++) {
    const track = doc.tracks[t];
    const index = track.items.findIndex((item) => item.id === target.id && isGradableItem(item));
    if (index < 0) continue;
    const item = track.items[index] as MediaClip | AdjustmentClip;
    if (item.grade === stack) return doc;
    const items = track.items.map((it, i) => (i === index && isGradableItem(it) ? { ...it, grade: stack } : it));
    return { ...doc, tracks: doc.tracks.map((tr, i) => (i === t ? { ...track, items } : tr)) };
  }

  return doc;
}
