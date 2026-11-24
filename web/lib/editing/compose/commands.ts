import type { EditorState, EditorStore } from '../store/editor-store';
import type { KeyAction } from '../keymap';
import type { MediaClip } from '../types';
import { selectItem } from '../selection/selection';
import { commitFrameOps, enterViewerTool, maskOps, shapeMaskOf, toggleInvertOps } from '../viewer-transform/frame-handles';
import { adjustmentClipOps } from './adjustment';
import { composeEntries, findComposeItem, resolveComposeEntry, viewerToolForEntry, type ComposeEntryId } from './entries';

/**
 * The Compose keyboard actions (design §10). Pure of React: the workspace hands in the few things only it knows
 * (read-only, phone, ids, translated undo labels) as {@link ComposeCommandEnv}. Availability and the runners share
 * ONE function, so a palette row's reason and a key press can never disagree.
 */

export const COMPOSE_ACTIONS: ReadonlySet<KeyAction> = new Set<KeyAction>([
  'toolMask', 'toolReframe', 'toolCornerPin', 'maskShapeNext', 'maskInvert', 'composeRemoveEntry', 'composeEntryPrevious', 'composeEntryNext', 'addAdjustmentClip',
]);

export type ComposeUnavailable = 'desktopOnly' | 'readOnly' | 'composeNeedsMediaClip' | 'composeNeedsMask' | 'composeNeedsPage' | 'composeNothingToRemove' | 'selectionRequired';

export interface ComposeCommandEnv {
  readOnly: boolean;
  phone: boolean;
  newId: () => string;
  /** Translated undo-step names. */
  labels: { maskShape: string; maskInvert: string; removeMask: string; removeReframe: string; removeKeyer: string; removePin: string; addAdjustment: string };
  adjustmentNames: { clip: string; track: string };
}

const TOOL_ENTRY = { toolMask: 'mask', toolReframe: 'reframe', toolCornerPin: 'pin' } as const;
const REMOVABLE: Partial<Record<ComposeEntryId, 'mask' | 'reframe' | 'keyer' | 'cornerPin'>> = { mask: 'mask', reframe: 'reframe', keyer: 'keyer', pin: 'cornerPin' };

function selectedMediaClip(store: EditorStore): MediaClip | null {
  const s = store.getState();
  const item = findComposeItem(s.doc, s.ui.selection);
  return item && item.type === 'mediaClip' ? item : null;
}

/**
 * Whether the remove-entry chord would remove `entry` right now: on the Compose page, with that entry the
 * selected one and switched on. A control that removes the entry advertises the chord only then.
 */
export function composeRemoveRunsOn(state: Pick<EditorState, 'doc' | 'ui'>, entry: ComposeEntryId): boolean {
  if (state.ui.workspace !== 'compose' || !REMOVABLE[entry]) return false;
  const entries = composeEntries(findComposeItem(state.doc, state.ui.selection));
  return resolveComposeEntry(entries, state.ui.composeEntry) === entry && entries.some((e) => e.id === entry && e.on);
}

/** Why a Compose action cannot run now, or null. `selectionRequired` is the shared "select a clip first" reason. */
export function composeUnavailable(id: KeyAction, store: EditorStore, env: Pick<ComposeCommandEnv, 'readOnly' | 'phone'>): ComposeUnavailable | null {
  if (!COMPOSE_ACTIONS.has(id)) return null;
  if (env.phone) return 'desktopOnly';
  const s = store.getState();
  if (id === 'addAdjustmentClip') return env.readOnly ? 'readOnly' : null;
  if (id === 'toolMask' || id === 'toolReframe' || id === 'toolCornerPin') {
    if (s.ui.selection.length === 0) return 'selectionRequired';
    return selectedMediaClip(store) ? null : 'composeNeedsMediaClip';
  }
  if (id === 'maskShapeNext' || id === 'maskInvert') {
    if (env.readOnly) return 'readOnly';
    const clip = selectedMediaClip(store);
    if (!clip) return s.ui.selection.length === 0 ? 'selectionRequired' : 'composeNeedsMediaClip';
    return shapeMaskOf(clip.mask) ? null : 'composeNeedsMask';
  }
  // Entry navigation and removal act on the list the Compose page shows.
  if (s.ui.workspace !== 'compose') return 'composeNeedsPage';
  const item = findComposeItem(s.doc, s.ui.selection);
  const entries = composeEntries(item);
  if (entries.length === 0) return 'selectionRequired';
  if (id === 'composeRemoveEntry') {
    if (env.readOnly) return 'readOnly';
    const current = resolveComposeEntry(entries, s.ui.composeEntry);
    const entry = entries.find((e) => e.id === current);
    return entry && REMOVABLE[entry.id] && entry.on ? null : 'composeNothingToRemove';
  }
  return null;
}

/** Run a Compose action. Unavailable: nothing happens (the palette shows why; a key press is silent). */
export function runComposeCommand(id: KeyAction, store: EditorStore, env: ComposeCommandEnv, setWorkspace: (workspace: 'compose') => void): void {
  if (composeUnavailable(id, store, env) !== null) return;
  const s = store.getState();
  switch (id) {
    case 'toolMask':
    case 'toolReframe':
    case 'toolCornerPin': {
      const entry = TOOL_ENTRY[id];
      // A professional expects the tool to come up: open Compose (a deliberate page change, unlike the Add entry points).
      if (s.ui.workspace !== 'compose') setWorkspace('compose');
      store.setUi({ composeEntry: entry });
      enterViewerTool(store, entry);
      return;
    }
    case 'maskShapeNext': {
      const clip = selectedMediaClip(store);
      const shape = clip ? shapeMaskOf(clip.mask) : null;
      if (clip && shape) commitFrameOps(store, maskOps(clip, { ...shape, kind: shape.kind === 'ellipse' ? 'rect' : 'ellipse' }), env.labels.maskShape);
      return;
    }
    case 'maskInvert': {
      const clip = selectedMediaClip(store);
      if (clip) commitFrameOps(store, toggleInvertOps(clip), env.labels.maskInvert);
      return;
    }
    case 'composeRemoveEntry': {
      const entries = composeEntries(findComposeItem(s.doc, s.ui.selection));
      const current = resolveComposeEntry(entries, s.ui.composeEntry);
      const clip = selectedMediaClip(store);
      const key = current ? REMOVABLE[current] : undefined;
      if (!clip || !current || !key) return;
      const label = key === 'mask' ? env.labels.removeMask : key === 'reframe' ? env.labels.removeReframe : key === 'keyer' ? env.labels.removeKeyer : env.labels.removePin;
      commitFrameOps(store, [{ type: 'setProperty', itemId: clip.id, key, value: null }], label);
      // A reframe or pin tool with nothing left to edit goes back to Select; the mask tool keeps its draw state.
      if (key === 'reframe' || key === 'cornerPin') {
        if (store.getState().ui.viewerTool === (key === 'reframe' ? 'reframe' : 'pin')) store.setUi({ viewerTool: 'select' });
      }
      return;
    }
    case 'composeEntryPrevious':
    case 'composeEntryNext': {
      const entries = composeEntries(findComposeItem(s.doc, s.ui.selection));
      const current = resolveComposeEntry(entries, s.ui.composeEntry);
      const index = entries.findIndex((e) => e.id === current);
      const target = entries[Math.max(0, Math.min(entries.length - 1, index + (id === 'composeEntryNext' ? 1 : -1)))];
      if (!target) return;
      store.setUi({ composeEntry: target.id });
      const tool = viewerToolForEntry(target, s.ui.viewerTool);
      if (tool === 'select') store.setUi({ viewerTool: 'select' });
      else if (tool) enterViewerTool(store, tool);
      return;
    }
    case 'addAdjustmentClip': {
      const { ops, clipId } = adjustmentClipOps(s.doc, { playheadFrame: s.ui.playheadFrame, inOut: s.ui.inOut }, env.newId, env.adjustmentNames);
      if (commitFrameOps(store, ops, env.labels.addAdjustment)) store.setUi({ selection: selectItem(store.getState().doc, clipId) });
      return;
    }
    default:
      return;
  }
}
