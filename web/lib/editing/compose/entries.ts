/**
 * Compose is a selected-target editor (design §5.5): the left column lists what is on the selected clip, the
 * right column shows the properties of the ONE selected entry. This module derives that list from the item. It
 * is pure (no React, no store) so it is unit-tested, and it is the ONE place that says which entry links to which
 * viewer tool: the mask tool bar (T-012) and the pin/quad viewer tools (T-013) hang off `ENTRY_VIEWER_TOOL`.
 */
import type { AdjustmentClip, EditTimeline, InsertClip, Item, MediaClip } from '../types';
import type { ViewerTool } from '../store/editor-store';

export type ComposeEntryId = 'mask' | 'reframe' | 'keyer' | 'pin' | 'effects' | 'motion' | 'chain' | 'grade' | 'matte' | 'quad';

/** How an entry is doing, as data; the list turns it into words ("Mask, ellipse", "Effects (3)", "Keyer, off"). */
export type ComposeEntryState =
  | { kind: 'off' }
  | { kind: 'on' }
  | { kind: 'shape'; shape: 'rect' | 'ellipse' }
  | { kind: 'object' }
  | { kind: 'count'; count: number };

export interface ComposeEntry {
  id: ComposeEntryId;
  state: ComposeEntryState;
  /** True when the entry has something applied (an off entry offers its one-click Add in the properties column). */
  on: boolean;
}

/**
 * The entry-to-viewer-tool mapping. Only entries that are drawn on the picture have a tool. T-012 adds the mask
 * tool bar and T-013 the pin tool (an insert's quad has none: the renderer does not draw inserts yet) by extending THIS table (and the `ViewerTool` union), nowhere else.
 */
export const ENTRY_VIEWER_TOOL: Partial<Record<ComposeEntryId, Extract<ViewerTool, 'mask' | 'reframe' | 'pin'>>> = {
  mask: 'mask',
  reframe: 'reframe',
  pin: 'pin',
};

/** The viewer tools a Compose entry can own; leaving such an entry returns the viewer to Select. */
const SHAPE_TOOLS: readonly ViewerTool[] = ['mask', 'reframe', 'pin'];

function mediaEntries(clip: MediaClip): ComposeEntry[] {
  const effects = clip.effects?.length ?? 0;
  const mask = clip.mask ?? null;
  const maskState: ComposeEntryState = !mask ? { kind: 'off' } : mask.kind === 'segmentation' ? { kind: 'object' } : { kind: 'shape', shape: mask.kind === 'ellipse' ? 'ellipse' : 'rect' };
  const motionOn = clip.stabilize === true || clip.retimeQuality === 'opticalFlow';
  return [
    { id: 'mask', state: maskState, on: !!mask },
    { id: 'reframe', state: clip.reframe ? { kind: 'on' } : { kind: 'off' }, on: !!clip.reframe },
    { id: 'keyer', state: clip.keyer ? { kind: 'on' } : { kind: 'off' }, on: !!clip.keyer },
    { id: 'pin', state: clip.cornerPin ? { kind: 'on' } : { kind: 'off' }, on: !!clip.cornerPin },
    { id: 'effects', state: { kind: 'count', count: effects }, on: effects > 0 },
    { id: 'motion', state: motionOn ? { kind: 'on' } : { kind: 'off' }, on: motionOn },
    { id: 'chain', state: { kind: 'on' }, on: true },
  ];
}

function adjustmentEntries(clip: AdjustmentClip): ComposeEntry[] {
  const effects = clip.effects?.length ?? 0;
  const graded = !!clip.grade && (clip.grade.nodes?.length ?? 0) > 0;
  return [
    { id: 'grade', state: graded ? { kind: 'on' } : { kind: 'off' }, on: graded },
    { id: 'effects', state: { kind: 'count', count: effects }, on: effects > 0 },
    { id: 'chain', state: { kind: 'on' }, on: true },
  ];
}

function insertEntries(clip: InsertClip): ComposeEntry[] {
  const keys = clip.quad?.keys?.length ?? 0;
  return [
    { id: 'matte', state: clip.matte ? { kind: 'on' } : { kind: 'off' }, on: !!clip.matte },
    { id: 'quad', state: { kind: 'count', count: keys }, on: keys > 0 },
  ];
}

/** The entries for the selected item, in list order. Items Compose has no controls for have none. */
export function composeEntries(item: Item | null): ComposeEntry[] {
  if (!item) return [];
  switch (item.type) {
    case 'mediaClip':
      return mediaEntries(item);
    case 'adjustmentClip':
      return adjustmentEntries(item);
    case 'insertClip':
      return insertEntries(item);
    default:
      return [];
  }
}

/** The entry to show: the current one when it applies to this item, else the first (selection changed kind). */
export function resolveComposeEntry(entries: readonly ComposeEntry[], current: ComposeEntryId | null): ComposeEntryId | null {
  if (current && entries.some((e) => e.id === current)) return current;
  return entries[0]?.id ?? null;
}

/**
 * What the viewer tool becomes when the user picks an entry. Mask and reframe enter their tool only when the
 * entry is on; any other entry puts a shape tool back to Select. `null` means leave the viewer tool alone.
 */
export function viewerToolForEntry(entry: ComposeEntry | undefined, current: ViewerTool): ViewerTool | null {
  const tool = entry ? ENTRY_VIEWER_TOOL[entry.id] : undefined;
  if (entry && tool && entry.on) return tool;
  return SHAPE_TOOLS.includes(current) ? 'select' : null;
}

/** The one selected item, or null (nothing selected, several selected, or an id the document no longer has). */
export function findComposeItem(doc: EditTimeline, selection: readonly string[]): Item | null {
  if (selection.length !== 1) return null;
  for (const track of doc.tracks) {
    const item = track.items.find((i) => i.id === selection[0]);
    if (item) return item;
  }
  return null;
}
