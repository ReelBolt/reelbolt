'use client';

/**
 * The link between the Follow section (inspector / clip sheet) and the viewer: the viewer registers its editor
 * store here, and the section arms a one-shot box drag the viewer then draws. One editor is open per page, so
 * this is a single small external store, not a per-store map. Nothing here touches the document.
 */

import { useSyncExternalStore } from 'react';
import type { EditorStore } from '../store/editor-store';
import type { SubjectBox } from '../api/follow';
import type { ObjectSeed } from '../selection/seed';

export type FollowArm = {
  clipId: string;
  /** Called once with the box (source fractions) and the timeline frame the drag was made at. Used when `onSeed` is absent. */
  onBox?: (box: SubjectBox, frame: number) => void;
  /**
   * Picker mode (click to select): a tap or a drawn box becomes a marker + box the user confirms; the confirmed seed
   * (source fractions, source seconds) and the timeline frame come back here once.
   */
  onSeed?: (seed: ObjectSeed, frame: number) => void;
};

export type FollowHostState = { store: EditorStore | null; armed: FollowArm | null };

let state: FollowHostState = { store: null, armed: null };
const listeners = new Set<() => void>();
const set = (next: FollowHostState) => {
  state = next;
  listeners.forEach((l) => l());
};

/** The viewer calls this with its store; the returned function unregisters it (only if it is still the registered one). */
export function registerFollowStore(store: EditorStore): () => void {
  set({ ...state, store });
  return () => {
    if (state.store === store) set({ store: null, armed: null });
  };
}

export const armFollowBox = (arm: FollowArm): void => set({ ...state, armed: arm });
export const disarmFollowBox = (): void => {
  if (state.armed) set({ ...state, armed: null });
};

export const getFollowHost = (): FollowHostState => state;
const subscribe = (l: () => void) => {
  listeners.add(l);
  return () => listeners.delete(l);
};

export function useFollowHost(): FollowHostState {
  return useSyncExternalStore(subscribe, getFollowHost, getFollowHost);
}
