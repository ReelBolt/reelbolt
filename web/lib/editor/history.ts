/**
 * Undo / redo for the thumbnail editor: a command stack over the document state.
 *
 * Pure: every function takes the stack and the state and returns new ones. Nothing here talks to
 * the server — the editor diffs the local state against what the server last confirmed
 * (`lib/editor/doc-sync.ts`) and sends the difference, so an undo is "change the local state back,
 * and let the next flush replay it". That is what "server writes are replayed from the stack"
 * means in the contract.
 *
 * ## What is and is not undoable
 *
 * - Property edits (transform, opacity, blend, visibility, lock, label, role, text content), layer
 *   order and canvas settings are commands: both directions are an absolute write.
 * - A layer the server just created (a cut-out, an AI result, a text layer) is undone by TAKING IT
 *   OUT of the shown document and redone by putting it back. The server has no "un-delete", so the
 *   row stays (hidden) while a redo is possible and is deleted once it is not.
 * - Deleting a layer is irreversible and is therefore not a command: the editor deletes it on the
 *   server and calls {@link clearHistory}, so no earlier step can resurrect a row that is gone.
 */

import {
  renumber,
  type EditorDocState,
  type EditorLayer,
  type EditorTransform,
} from './model';
import type { BlendMode, EditImageLayerRole, LayerCrop, TextContent } from '@/lib/types/image-document';
import { originalCanvasSize } from './canvas-size';

export interface Hold {
  ids: readonly string[];
  whileState: 'done' | 'undone';
}

export interface Command {
  /** What the user did, in the words the undo button's tooltip shows. */
  label: string;
  /**
   * Commands with the same key that arrive within {@link MERGE_WINDOW_MS} of each other collapse
   * into one undo step (a drag, a run of arrow-key nudges, a slider). A mergeable command MUST be
   * an absolute setter, because the merged command keeps the OLDEST `undo` and the NEWEST `do`.
   */
  mergeKey?: string;
  /**
   * Layers this step takes out of the shown document. The server cannot un-delete, so their rows are
   * kept while the step is in the `whileState` position ({@link retainedLayerIds}).
   */
  holds?: readonly Hold[];
  do(state: EditorDocState): EditorDocState;
  undo(state: EditorDocState): EditorDocState;
}

interface Entry {
  command: Command;
  at: number;
}

export interface HistoryStack {
  past: readonly Entry[];
  future: readonly Entry[];
}

export const MERGE_WINDOW_MS = 600;
export const HISTORY_LIMIT = 100;

export const emptyHistory: HistoryStack = { past: [], future: [] };

export interface HistoryResult {
  history: HistoryStack;
  state: EditorDocState;
}

export function canUndo(h: HistoryStack): boolean {
  return h.past.length > 0;
}

export function canRedo(h: HistoryStack): boolean {
  return h.future.length > 0;
}

/** The label of the step an undo would take back (for a tooltip), or null. */
export function undoLabel(h: HistoryStack): string | null {
  return h.past.length > 0 ? h.past[h.past.length - 1].command.label : null;
}

export function redoLabel(h: HistoryStack): string | null {
  return h.future.length > 0 ? h.future[h.future.length - 1].command.label : null;
}

/** Runs a command, records it, and discards any redo history (a new edit forks the timeline). */
export function execute(
  h: HistoryStack,
  state: EditorDocState,
  command: Command,
  now: number,
): HistoryResult {
  const next = command.do(state);
  const top = h.past[h.past.length - 1];
  if (
    top &&
    command.mergeKey !== undefined &&
    top.command.mergeKey === command.mergeKey &&
    now - top.at <= MERGE_WINDOW_MS
  ) {
    const merged: Command = {
      label: command.label,
      mergeKey: command.mergeKey,
      holds: command.holds,
      do: command.do,
      undo: top.command.undo,
    };
    return {
      history: { past: [...h.past.slice(0, -1), { command: merged, at: now }], future: [] },
      state: next,
    };
  }
  const past = [...h.past, { command, at: now }];
  return {
    history: { past: past.length > HISTORY_LIMIT ? past.slice(past.length - HISTORY_LIMIT) : past, future: [] },
    state: next,
  };
}

export function undo(h: HistoryStack, state: EditorDocState): HistoryResult {
  const top = h.past[h.past.length - 1];
  if (!top) return { history: h, state };
  return {
    history: { past: h.past.slice(0, -1), future: [...h.future, top] },
    state: top.command.undo(state),
  };
}

export function redo(h: HistoryStack, state: EditorDocState): HistoryResult {
  const next = h.future[h.future.length - 1];
  if (!next) return { history: h, state };
  return {
    history: { past: [...h.past, next], future: h.future.slice(0, -1) },
    state: next.command.do(state),
  };
}

/** Forgets everything (after an irreversible server write, or a reload from the server). */
export function clearHistory(): HistoryStack {
  return emptyHistory;
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// Command constructors
// ─────────────────────────────────────────────────────────────────────────────────────────────

/** The per-layer fields an edit may set. Everything else on a layer is server-owned. */
export interface LayerPatch {
  label?: string;
  role?: EditImageLayerRole;
  visible?: boolean;
  locked?: boolean;
  opacity?: number;
  blendMode?: BlendMode;
  transform?: EditorTransform;
  content?: TextContent;
  /** null clears the crop. */
  crop?: LayerCrop | null;
}

const PATCH_KEYS: (keyof LayerPatch)[] = [
  'label',
  'role',
  'visible',
  'locked',
  'opacity',
  'blendMode',
  'transform',
  'content',
  'crop',
];

function setLayer(state: EditorDocState, id: string, apply: (l: EditorLayer) => EditorLayer): EditorDocState {
  let changed = false;
  const layers = state.layers.map((l) => {
    if (l.id !== id) return l;
    changed = true;
    return apply(l);
  });
  return changed ? { ...state, layers } : state;
}

function patchEquals(layer: EditorLayer, patch: LayerPatch): boolean {
  return PATCH_KEYS.every((key) => {
    if (!(key in patch)) return true;
    const a = layer[key];
    const b = patch[key];
    return a === b || JSON.stringify(a) === JSON.stringify(b);
  });
}

/**
 * Sets some fields of one layer. `null` when the layer is gone or the patch changes nothing, so a
 * no-op drag never costs an undo step. The previous values are captured here, which is what makes
 * the command an absolute setter in both directions.
 */
export function patchLayer(
  state: EditorDocState,
  layerId: string,
  patch: LayerPatch,
  label: string,
  mergeKey?: string,
): Command | null {
  const layer = state.layers.find((l) => l.id === layerId);
  if (!layer || patchEquals(layer, patch)) return null;
  const before: LayerPatch = {};
  for (const key of PATCH_KEYS) {
    if (key in patch) (before as Record<string, unknown>)[key] = layer[key];
  }
  const write = (p: LayerPatch) => (s: EditorDocState) =>
    setLayer(s, layerId, (l) => ({ ...l, ...p }));
  return { label, mergeKey, do: write(patch), undo: write(before) };
}

/** Moves a layer to a 1-based position in the stack (1 = back), renumbering the rest. */
export function reorderLayer(
  state: EditorDocState,
  layerId: string,
  position: number,
  label = 'Reorder layers',
): Command | null {
  const from = state.layers.findIndex((l) => l.id === layerId);
  if (from < 0) return null;
  const to = Math.min(state.layers.length, Math.max(1, Math.round(position))) - 1;
  if (to === from) return null;
  const beforeIds = state.layers.map((l) => l.id);
  const arrange = (ids: string[]) => (s: EditorDocState): EditorDocState => {
    const byId = new Map(s.layers.map((l) => [l.id, l]));
    const ordered = ids.map((id) => byId.get(id)).filter((l): l is EditorLayer => !!l);
    // Layers added since the command was made keep their relative place at the front.
    const extra = s.layers.filter((l) => !ids.includes(l.id));
    return { ...s, layers: renumber([...ordered, ...extra]) };
  };
  const afterIds = [...beforeIds];
  const [moved] = afterIds.splice(from, 1);
  afterIds.splice(to, 0, moved);
  return { label, do: arrange(afterIds), undo: arrange(beforeIds) };
}

export interface CanvasPatch {
  canvasWidth?: number;
  canvasHeight?: number;
  backgroundColor?: string | null;
}

/** Changes the canvas size and/or background. `null` when nothing would change. */
export function patchCanvas(
  state: EditorDocState,
  patch: CanvasPatch,
  label: string,
  mergeKey?: string,
): Command | null {
  const same =
    (patch.canvasWidth === undefined || patch.canvasWidth === state.canvasWidth) &&
    (patch.canvasHeight === undefined || patch.canvasHeight === state.canvasHeight) &&
    (patch.backgroundColor === undefined || patch.backgroundColor === state.backgroundColor);
  if (same) return null;
  const before: CanvasPatch = {};
  if (patch.canvasWidth !== undefined) before.canvasWidth = state.canvasWidth;
  if (patch.canvasHeight !== undefined) before.canvasHeight = state.canvasHeight;
  if (patch.backgroundColor !== undefined) before.backgroundColor = state.backgroundColor;
  const write = (p: CanvasPatch) => (s: EditorDocState): EditorDocState => ({
    ...s,
    ...(p.canvasWidth !== undefined ? { canvasWidth: p.canvasWidth } : {}),
    ...(p.canvasHeight !== undefined ? { canvasHeight: p.canvasHeight } : {}),
    ...(p.backgroundColor !== undefined ? { backgroundColor: p.backgroundColor } : {}),
  });
  return { label, mergeKey, do: write(patch), undo: write(before) };
}

/**
 * "Original size": the canvas becomes the photo's own size (long side capped, see
 * `originalCanvasSize`) and the photo's layer goes back to showing the whole photo in it — top-left
 * corner, no rotation or flips, scaled only when the photo is larger than the cap. One undo step for
 * both. `null` when nothing would change or the photo's size is unknown.
 */
export function originalSize(
  state: EditorDocState,
  layerId: string,
  photo: { width: number; height: number },
): Command | null {
  const layer = state.layers.find((l) => l.id === layerId);
  const size = originalCanvasSize(photo);
  if (!layer || !size) return null;
  const transform: EditorTransform = {
    x: 0,
    y: 0,
    scale: size.width / photo.width,
    rotation: 0,
    flipX: false,
    flipY: false,
  };
  const t = layer.transform;
  const sameTransform =
    t.x === transform.x &&
    t.y === transform.y &&
    Math.abs(t.scale - transform.scale) < 1e-9 &&
    t.rotation === 0 &&
    !t.flipX &&
    !t.flipY;
  if (sameTransform && state.canvasWidth === size.width && state.canvasHeight === size.height) return null;
  const before = { canvasWidth: state.canvasWidth, canvasHeight: state.canvasHeight, transform: layer.transform };
  const write =
    (p: { canvasWidth: number; canvasHeight: number; transform: EditorTransform }) =>
    (s: EditorDocState): EditorDocState => ({
      ...setLayer(s, layerId, (l) => ({ ...l, transform: p.transform })),
      canvasWidth: p.canvasWidth,
      canvasHeight: p.canvasHeight,
    });
  return {
    label: 'Original size',
    do: write({ canvasWidth: size.width, canvasHeight: size.height, transform }),
    undo: write(before),
  };
}

/**
 * Takes layers out of the shown document and puts them back, remembering them: the two halves of
 * {@link layersAdded} (out = undo, back = redo) and {@link layersRemoved} (out = do, back = undo).
 */
function layerSwap(ids: readonly string[]) {
  const set = new Set(ids);
  let stash: { layer: EditorLayer; index: number }[] = [];
  return {
    /** Puts the remembered layers back where they were, shown; shows the rest of `ids`. */
    back: (s: EditorDocState): EditorDocState => {
      const have = new Set(s.layers.map((l) => l.id));
      let layers = s.layers.map((l) => (set.has(l.id) && !l.visible ? { ...l, visible: true } : l));
      const back = stash.filter((e) => !have.has(e.layer.id)).sort((a, b) => a.index - b.index);
      for (const { layer, index } of back) {
        const at = Math.min(index, layers.length);
        layers = [...layers.slice(0, at), { ...layer, visible: true }, ...layers.slice(at)];
      }
      stash = [];
      return back.length > 0 || layers.some((l, i) => l !== s.layers[i]) ? { ...s, layers: renumber(layers) } : s;
    },
    out: (s: EditorDocState): EditorDocState => {
      stash = s.layers.flatMap((layer, index) => (set.has(layer.id) ? [{ layer, index }] : []));
      return stash.length > 0 ? { ...s, layers: renumber(s.layers.filter((l) => !set.has(l.id))) } : s;
    },
  };
}

/**
 * The step recorded after the server created layers (a cut-out, an AI result, a text layer): the
 * layers are already in the state, so `do` is "make sure they are shown" and `undo` TAKES THEM OUT of
 * the document the editor shows (layers list, canvas, export). Redo puts them back where they were.
 *
 * The server keeps layers immutable and has no "un-delete", so a taken-out layer stays on the server
 * (hidden by the next flush) while it can still be redone; the editor deletes it for good once the
 * redo steps are gone (see `useEditorDocument`). The taken-out layers are remembered here, in the
 * command, which is what lets redo bring back the very same layer.
 */
export function layersAdded(ids: readonly string[], label: string): Command {
  const swap = layerSwap(ids);
  return { label, do: swap.back, undo: swap.out, holds: [{ ids, whileState: 'undone' }] };
}

/**
 * Deleting layers as an undoable step (the Delete key): they leave the shown document, and undo
 * brings them back. The server row is kept (hidden) while the step can be undone and deleted after.
 */
export function layersRemoved(ids: readonly string[], label: string): Command {
  const swap = layerSwap(ids);
  return { label, do: swap.out, undo: swap.back, holds: [{ ids, whileState: 'done' }] };
}

/**
 * The layers a step in this history can still bring back, so the server must keep their rows:
 * those of an undone "added" step (redo) and of a done "removed" step (undo).
 */
export function retainedLayerIds(h: HistoryStack): Set<string> {
  const kept = new Set<string>();
  const keep = (list: readonly Entry[], when: Hold['whileState']) => {
    for (const e of list) for (const hold of e.command.holds ?? []) if (hold.whileState === when) hold.ids.forEach((id) => kept.add(id));
  };
  keep(h.past, 'done');
  keep(h.future, 'undone');
  return kept;
}

/** Several steps that undo and redo together as one. */
export function sequence(label: string, steps: readonly Command[]): Command {
  return {
    label,
    holds: steps.flatMap((c) => c.holds ?? []),
    do: (s) => steps.reduce((acc, c) => c.do(acc), s),
    undo: (s) => [...steps].reverse().reduce((acc, c) => c.undo(acc), s),
  };
}

/** Sets fields on several layers as one undo step (align, distribute). `null` when nothing changes. */
export function patchLayers(
  state: EditorDocState,
  items: readonly { id: string; patch: LayerPatch }[],
  label: string,
): Command | null {
  const steps = items.map((i) => patchLayer(state, i.id, i.patch, label)).filter((c): c is Command => c !== null);
  return steps.length === 0 ? null : sequence(label, steps);
}

/**
 * Swaps layers for new ones the server just made (merge, crop): the new ones are shown and the old
 * ones leave the document, as one undo step. Undo brings the old ones back and takes the new out.
 */
export function layersReplaced(oldIds: readonly string[], newIds: readonly string[], label: string): Command {
  return sequence(label, [layersAdded(newIds, label), layersRemoved(oldIds, label)]);
}
