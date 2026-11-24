import type { Item } from '@/lib/editing/types';
import { FIX_NODE_NAME, LOOK_PREFIX } from '@/lib/editing/color/look-gallery';
import { FIX_NODE_NAME as AUTO_FIX_NODE_NAME } from '@/lib/editing/color/exposure-fix';

/**
 * Work on a clip that the Simple shell shows in the picture but has no control for. The clip panel says it in one
 * line, with the way to the full editor, so nothing made there is hidden or silently lost.
 */
export type FullWorkKind = 'colour' | 'mask' | 'keyer' | 'effects' | 'cornerPin' | 'stabilize' | 'reframe' | 'crop' | 'reverse' | 'freeze' | 'animation';

export interface FullWork {
  kind: FullWorkKind;
  /** How many (colour steps, effects); 1 for the rest. */
  count: number;
}

const hasKeys = (value: unknown): boolean => {
  if (!value || typeof value !== 'object') return false;
  const keys = (value as { keys?: unknown }).keys;
  return Array.isArray(keys) && keys.length > 0;
};

/** The item types the Simple shell edits directly; every other type is edited in the full editor. */
export const SIMPLE_ITEM_TYPES: ReadonlySet<string> = new Set(['mediaClip', 'titleClip', 'caption']);

export function fullEditorWork(item: Item): FullWork[] {
  if (item.type !== 'mediaClip') return [];
  const out: FullWork[] = [];
  // Looks, the five quick fixes and Fix exposure are the Simple shell's own colour steps; anything else was made with the colour tools.
  const own = (name: string) => name.startsWith(LOOK_PREFIX) || name === FIX_NODE_NAME || name === AUTO_FIX_NODE_NAME;
  const colour = (item.grade?.nodes ?? []).filter((node) => !own(node.name ?? '')).length;
  if (colour > 0) out.push({ kind: 'colour', count: colour });
  if (item.mask) out.push({ kind: 'mask', count: 1 });
  if (item.keyer) out.push({ kind: 'keyer', count: 1 });
  if ((item.effects?.length ?? 0) > 0) out.push({ kind: 'effects', count: item.effects?.length ?? 0 });
  if (item.cornerPin) out.push({ kind: 'cornerPin', count: 1 });
  if (item.stabilize) out.push({ kind: 'stabilize', count: 1 });
  if (item.reframe) out.push({ kind: 'reframe', count: 1 });
  if (item.crop) out.push({ kind: 'crop', count: 1 });
  if (item.reverse) out.push({ kind: 'reverse', count: 1 });
  if (item.hold) out.push({ kind: 'freeze', count: 1 });
  const transform = item.transform as Record<string, unknown> | null | undefined;
  if (hasKeys(item.gainDb) || (transform && Object.values(transform).some(hasKeys))) out.push({ kind: 'animation', count: 1 });
  return out;
}

/** Where "Open in the full editor" lands for a piece of work. */
export function fullWorkPage(kind: FullWorkKind): 'color' | 'compose' | 'edit' {
  if (kind === 'colour') return 'color';
  if (kind === 'mask' || kind === 'keyer' || kind === 'cornerPin' || kind === 'reframe') return 'compose';
  return 'edit';
}
