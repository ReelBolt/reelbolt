import { nudge, type NudgeKey } from '@/lib/editor/transform-tools';
import type { EditorTransform } from '@/lib/editor/model';

/** What the layer row's overflow menu offers. Deleting is a separate, red item, never next to an arrow (T15). */
export type LayerMenuAction = 'forward' | 'back' | 'front' | 'backmost';

export const LAYER_MENU_LABEL: Record<LayerMenuAction, string> = {
  forward: 'Bring forward',
  back: 'Send back',
  front: 'Bring to front',
  backmost: 'Send to back',
};

/**
 * The 1-based position from the back (what the server's `zOrder` takes) a menu action moves the layer at
 * `index` (0 = back) to, or null when it is already there so the item is disabled.
 */
export function reorderTarget(action: LayerMenuAction, index: number, count: number): number | null {
  const last = count - 1;
  switch (action) {
    case 'forward':
      return index >= last ? null : index + 2;
    case 'back':
      return index <= 0 ? null : index;
    case 'front':
      return index >= last ? null : count;
    case 'backmost':
      return index <= 0 ? null : 1;
  }
}

/** One press of the nudge pad: 1 canvas pixel, or 10 when the big step is on. */
export function nudgeStepPx(big: boolean): number {
  return big ? 10 : 1;
}

export function nudgeTransform(t: EditorTransform, key: NudgeKey, big: boolean): EditorTransform {
  return nudge(t, key, big);
}

/** The mask entries of a layer's menu. */
export type MaskMenuAction = 'add' | 'edit' | 'invert' | 'delete' | 'only';

export const MASK_MENU_LABEL: Record<MaskMenuAction, string> = {
  add: 'Add mask',
  edit: 'Edit mask',
  invert: 'Invert mask',
  delete: 'Delete mask',
  only: 'Show mask only',
};

export interface MaskMenuItem {
  action: MaskMenuAction;
  label: string;
  disabled: boolean;
}

/**
 * Which mask items a layer's menu shows. A text layer has no pixels, so it gets none. Without a mask
 * only "Add mask" is offered; with one, the rest. Everything waits while a mask write is running or
 * while another layer's mask is being painted (`paintingElsewhere`), and a locked layer cannot change.
 * "Show mask only" is a view setting, so it stays available on a locked layer.
 */
export function maskMenuItems(
  layer: { kind: string; matteUrl: string | null; locked: boolean },
  state: { busy: boolean; painting: boolean },
): MaskMenuItem[] {
  if (layer.kind === 'text') return [];
  const off = state.busy || state.painting;
  if (!layer.matteUrl) return [{ action: 'add', label: MASK_MENU_LABEL.add, disabled: off || layer.locked }];
  const edits: MaskMenuAction[] = ['edit', 'invert', 'delete'];
  return [
    ...edits.map((action) => ({ action, label: MASK_MENU_LABEL[action], disabled: off || layer.locked })),
    { action: 'only' as const, label: MASK_MENU_LABEL.only, disabled: state.busy },
  ];
}
