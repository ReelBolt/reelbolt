/**
 * R07.2: the live (uncommitted) transform of a viewer gesture. While a handle is dragged nothing is
 * written to the document: the draft is merged into the PREVIEW document only (so the picture follows
 * the pointer), the inspector mirrors it through {@link useTransformMirror}, and one batch of ops is
 * dispatched on release (one undo step).
 */

import { useSyncExternalStore } from 'react';
import type { ClipTransform, EditTimeline, Item, Keyframed, MediaClip, TitleClip } from '@/lib/editing/types';
import type { EditOp } from '@/lib/editing/engine';
import type { EditorStore } from '@/lib/editing/store/editor-store';
import { evaluateKeyframed } from '@/lib/editing/preview';
import type { TransformValues } from './geometry';

/** What a viewer gesture edits: a media clip or a title (both carry a `transform`). */
export type TransformHost = { id: string; transform?: ClipTransform | null };

export type TransformDraftState = { itemId: string; values: TransformValues } | null;

export interface TransformDraftStore {
  get(): TransformDraftState;
  set(next: TransformDraftState): void;
  subscribe(listener: () => void): () => void;
}

export function createTransformDraft(): TransformDraftStore {
  let value: TransformDraftState = null;
  const listeners = new Set<() => void>();
  return {
    get: () => value,
    set(next) {
      value = next;
      for (const l of [...listeners]) l();
    },
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };
}

const drafts = new WeakMap<object, TransformDraftStore>();
/** The one draft store of an editor store (the workspace, the viewer overlay and the inspector share it). */
export function draftFor(store: EditorStore): TransformDraftStore {
  let d = drafts.get(store);
  if (!d) {
    d = createTransformDraft();
    drafts.set(store, d);
  }
  return d;
}

const CHANNELS = ['positionX', 'positionY', 'scale', 'scaleY', 'rotation'] as const;
type Channel = (typeof CHANNELS)[number];

const channelOf = (clip: TransformHost, param: Channel): Keyframed<number> | null | undefined => clip.transform?.[param];
const hasKeys = (c: Keyframed<number> | null | undefined) => Array.isArray(c?.keys) && c!.keys!.length > 0;

/** The clip's transform numbers at a frame (identity for what is absent). */
export function transformAt(clip: TransformHost, frame: number): TransformValues {
  const read = (param: Channel, identity: number) => {
    const c = channelOf(clip, param);
    return evaluateKeyframed(c ?? null, frame, typeof c?.value === 'number' ? c.value : identity);
  };
  const values: TransformValues = { positionX: read('positionX', 0), positionY: read('positionY', 0), scale: read('scale', 1), rotation: read('rotation', 0) };
  // `scaleY` is only there when the clip has a vertical scale (absent = uniform).
  if (!clip.transform?.scaleY) return values;
  const scaleY = read('scaleY', values.scale);
  // A stored 0 means "no value" (uniform), the same as the validator and the export read it.
  return scaleY > 0 ? { ...values, scaleY } : values;
}

/** Which of the four channels carry keyframes (a gesture then writes a key at the playhead). */
export function keyedChannels(clip: TransformHost): Record<Channel, boolean> {
  return { positionX: hasKeys(channelOf(clip, 'positionX')), positionY: hasKeys(channelOf(clip, 'positionY')), scale: hasKeys(channelOf(clip, 'scale')), scaleY: hasKeys(channelOf(clip, 'scaleY')), rotation: hasKeys(channelOf(clip, 'rotation')) };
}

const EPS = 1e-9;

/**
 * The ops that store `next` for a clip, in ONE batch: the static channels in a single `setProperty
 * transform` (explicit values, so a preset stores exactly what it says), the keyed ones as `setKeyframe`
 * at `frame`. Channels that did not change write nothing.
 */
export function transformOps(clip: TransformHost, next: TransformValues, frame: number): EditOp[] {
  const current = transformAt(clip, frame);
  const keyed = keyedChannels(clip);
  const ops: EditOp[] = [];
  const staticTransform: Record<string, unknown> = { ...(clip.transform ?? {}) };
  let staticChanged = false;
  for (const param of CHANNELS) {
    const target = param === 'scaleY' ? next.scaleY : next[param];
    // A vertical scale the values do not carry stays as stored; one that is newly set is written even when it equals the
    // old uniform scale (the clip then has an explicit height).
    if (target === undefined) continue;
    const before = param === 'scaleY' ? current.scaleY : current[param];
    if (before !== undefined && Math.abs(target - before) <= EPS) continue;
    if (keyed[param]) {
      ops.push({ type: 'setKeyframe', target: { itemId: clip.id }, param, frame, value: target, interp: 'linear' });
    } else {
      staticTransform[param] = { value: target, keys: null };
      staticChanged = true;
    }
  }
  if (staticChanged) ops.unshift({ type: 'setProperty', itemId: clip.id, key: 'transform', value: staticTransform as ClipTransform });
  return ops;
}

/** Commit values as one undo step. Returns false when nothing changed or the store refused. */
export function commitTransform(store: EditorStore, clip: TransformHost, next: TransformValues, frame: number, label = 'Move picture'): boolean {
  const ops = transformOps(clip, next, frame);
  if (ops.length === 0) return false;
  store.dispatchBatch(ops, label);
  return true;
}

/** A copy of `doc` where the clip `itemId` shows `values` at `frame` (static channels set, keyed channels given a key at the frame). */
export function applyDraft(doc: EditTimeline, draft: NonNullable<TransformDraftState>, frame: number): EditTimeline {
  let changed = false;
  const tracks = doc.tracks.map((track) => {
    let hit = false;
    const items = track.items.map((item): Item => {
      if (item.id !== draft.itemId || (item.type !== 'mediaClip' && item.type !== 'titleClip')) return item;
      hit = true;
      const clip = item as MediaClip | TitleClip;
      const staticTransform: Record<string, unknown> = { ...(clip.transform ?? {}) };
      for (const param of CHANNELS) {
        const value = draft.values[param];
        if (value === undefined) continue;
        const c = channelOf(clip, param);
        if (hasKeys(c)) {
          const keys = [...c!.keys!.filter((k) => k.frame !== frame), { frame, value, interp: 'linear' as const }].sort((a, b) => a.frame - b.frame);
          staticTransform[param] = { ...c, keys };
        } else staticTransform[param] = { value, keys: null };
      }
      return { ...clip, transform: staticTransform as ClipTransform };
    });
    if (!hit) return track;
    changed = true;
    return { ...track, items };
  });
  return changed ? { ...doc, tracks } : doc;
}

export type TransformMirror = { values: TransformValues; keyed: Record<Channel, boolean>; live: boolean };

/**
 * For the inspector (T-016): the selected clip's four transform numbers at the playhead, following a
 * viewer drag live. `null` when the item is not a media clip of the main timeline.
 */
export function useTransformMirror(store: EditorStore, itemId: string | null): TransformMirror | null {
  const draft = draftFor(store);
  const live = useSyncExternalStore(draft.subscribe, draft.get, draft.get);
  const doc = useSyncExternalStore((l) => store.subscribe(l), () => store.getState().doc, () => store.getState().doc);
  const frame = useSyncExternalStore((l) => store.subscribe(l), () => store.getState().ui.playheadFrame, () => store.getState().ui.playheadFrame);
  return mirrorOf(doc, itemId, frame, live);
}

/** The mirror for one item (pure; exported for tests). */
export function mirrorOf(doc: EditTimeline, itemId: string | null, frame: number, live: TransformDraftState): TransformMirror | null {
  if (!itemId) return null;
  for (const track of doc.tracks) {
    for (const item of track.items) {
      if (item.id !== itemId || (item.type !== 'mediaClip' && item.type !== 'titleClip')) continue;
      const clip = item as MediaClip | TitleClip;
      const dragging = live !== null && live.itemId === itemId;
      return { values: dragging ? live.values : transformAt(clip, frame), keyed: keyedChannels(clip), live: dragging };
    }
  }
  return null;
}
