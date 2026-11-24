'use client';

/**
 * The editor's record of background jobs for one timeline: what was launched, what is still running, what landed
 * and needs the user's OK, what was applied. It lives outside React (and is saved to localStorage) so a result
 * survives a selection change, an unmounted panel and a reload: nothing a job paid for is dropped because a
 * component went away.
 */

import { useSyncExternalStore } from 'react';
import { getUserFromCookie } from '../../hooks/use-auth';
import { userScopeFromEmail } from '../sync/user-scope';
import { allMediaClips, rebaseAnchor, type RebaseReason, type ResultAnchor } from './anchor';
import { nestedClipIds } from './edit-guard';
import type { EditTimeline, MediaClip } from '../types';

export type BackgroundState = 'running' | 'needsReview' | 'applied' | 'discarded' | 'failed';

export interface BackgroundReview {
  applicable: string[];
  reasons: RebaseReason[];
  lostCount: number;
  /** Set when the result could be applied but the editor would not take it right now. */
  blocked?: 'readOnly' | 'refused';
  /** The editor's own plain sentence for a refusal. */
  refusal?: string;
}

export interface BackgroundEntry {
  id: string;
  /** `objectEffect` today; any future document-changing background tool names itself. */
  tool: 'objectEffect' | (string & {});
  requestId: string;
  executionId?: string;
  anchor: ResultAnchor;
  /** Tool-specific JSON (for `objectEffect`: prompt, effect, strength, invert, featherPx). */
  intent: Record<string, unknown>;
  state: BackgroundState;
  review?: BackgroundReview;
  /** Why a run failed (the server's code and sentence), set with state `failed`. */
  failure?: { code: string | null; message: string | null };
  /** 0..1 when the job reports how far it is; absent = indeterminate (the clip shows a moving stripe). */
  progress?: number;
  /** True when polling gave up (network or a refusal); the panel offers a retry. */
  stalled?: boolean;
  createdAt: string;
}

export const BACKGROUND_CAP = 50;
export const backgroundKey = (userId: string, timelineId: string): string => `reelbolt.editor.bg.${userId}.${timelineId}`;

type Listener = () => void;
const EMPTY: readonly BackgroundEntry[] = Object.freeze([]);

const isEntry = (value: unknown): value is BackgroundEntry => {
  if (typeof value !== 'object' || value === null) return false;
  const e = value as Partial<BackgroundEntry>;
  return typeof e.id === 'string' && typeof e.tool === 'string' && typeof e.requestId === 'string' && typeof e.state === 'string' && typeof e.anchor === 'object' && e.anchor !== null && Array.isArray(e.anchor.clipIds);
};

export class ResultsStore {
  private entries: readonly BackgroundEntry[];
  private readonly listeners = new Set<Listener>();

  constructor(
    readonly timelineId: string,
    private readonly storageKey: string,
    private readonly storage: Pick<Storage, 'getItem' | 'setItem'> | null,
  ) {
    this.entries = this.load();
  }

  private load(): readonly BackgroundEntry[] {
    try {
      const raw = this.storage?.getItem(this.storageKey);
      if (!raw) return EMPTY;
      const parsed: unknown = JSON.parse(raw);
      return Array.isArray(parsed) ? parsed.filter(isEntry).slice(-BACKGROUND_CAP) : EMPTY;
    } catch {
      return EMPTY;
    }
  }

  private commit(next: readonly BackgroundEntry[]): void {
    let kept = next;
    if (kept.length > BACKGROUND_CAP) {
      // Drop the oldest FINISHED entries first; an unfinished one is never lost to the cap while a finished one remains.
      const open = (e: BackgroundEntry) => e.state === 'running' || e.state === 'needsReview';
      let excess = kept.length - BACKGROUND_CAP;
      kept = kept.filter((e) => {
        if (excess > 0 && !open(e)) {
          excess -= 1;
          return false;
        }
        return true;
      });
      if (kept.length > BACKGROUND_CAP) kept = kept.slice(kept.length - BACKGROUND_CAP);
    }
    this.entries = kept;
    try {
      this.storage?.setItem(this.storageKey, JSON.stringify(kept));
    } catch {
      /* a full or blocked store keeps the in-memory list; the next change tries again */
    }
    for (const listener of this.listeners) listener();
  }

  getEntries = (): readonly BackgroundEntry[] => this.entries;

  subscribe = (listener: Listener): (() => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };

  /** Registers a launch (or a result adopted from the server). The same id twice is one entry. */
  launch = (entry: BackgroundEntry): void => {
    if (this.entries.some((e) => e.id === entry.id)) return;
    this.commit([...this.entries, entry]);
  };

  update = (id: string, patch: Partial<Omit<BackgroundEntry, 'id'>>): void => {
    if (!this.entries.some((e) => e.id === id)) return;
    this.commit(this.entries.map((e) => (e.id === id ? { ...e, ...patch } : e)));
  };

  resolve = (id: string, state: 'applied' | 'discarded'): void => {
    this.update(id, { state, review: undefined });
  };

  /** Clip ids the running entries are about, rebased onto `doc` (a split piece is busy too). */
  busyClipIds = (doc: EditTimeline, canApply?: (clip: MediaClip) => boolean): Set<string> => {
    const busy = new Set<string>();
    const present = new Set(allMediaClips(doc).map((c) => c.id));
    for (const entry of this.entries) {
      if (entry.state !== 'running') continue;
      const outcome = rebaseAnchor(doc, entry.anchor, canApply);
      const ids = outcome.kind === 'apply' ? outcome.clipIds : outcome.applicable;
      for (const id of ids) busy.add(id);
      // A target that cannot take the result later is still the clip the user is waiting on.
      for (const id of entry.anchor.clipIds) if (present.has(id)) busy.add(id);
    }
    // A compound shows busy when a clip nested inside it is.
    if (busy.size > 0) {
      for (const item of doc.tracks.flatMap((t) => t.items)) if (item.type === 'compoundClip' && nestedClipIds(doc, item.id).some((id) => busy.has(id))) busy.add(item.id);
    }
    return busy;
  };
}

const stores = new Map<string, ResultsStore>();
let activeTimelineId: string | null = null;
let activeDoc: (() => EditTimeline) | null = null;
const activeListeners = new Set<Listener>();

/** The store of one timeline (one instance per timeline and signed-in user, for the page's lifetime). */
export function getResultsStore(timelineId: string): ResultsStore {
  const userId = userScopeFromEmail(getUserFromCookie()?.email);
  const key = backgroundKey(userId, timelineId);
  let store = stores.get(key);
  if (!store) {
    store = new ResultsStore(timelineId, key, typeof window === 'undefined' ? null : safeStorage());
    stores.set(key, store);
  }
  return store;
}

function safeStorage(): Storage | null {
  try {
    return window.localStorage;
  } catch {
    return null;
  }
}

/** Forgets every cached store (tests; a reload does the same). */
export function resetResultsStores(): void {
  stores.clear();
  activeTimelineId = null;
  activeDoc = null;
}

/** The timeline the open editor shows; panels that sit far from the workspace read it. */
export function setActiveTimeline(timelineId: string | null, getDoc?: () => EditTimeline): void {
  activeDoc = timelineId === null ? null : (getDoc ?? null);
  if (activeTimelineId === timelineId) return;
  activeTimelineId = timelineId;
  for (const listener of activeListeners) listener();
}

/** The open editor's current document (null when no editor is mounted). */
export const getActiveDoc = (): EditTimeline | null => (activeDoc ? activeDoc() : null);
const subscribeActive = (l: Listener): (() => void) => {
  activeListeners.add(l);
  return () => {
    activeListeners.delete(l);
  };
};
const noopSubscribe = (): (() => void) => () => {};
const noEntries = (): readonly BackgroundEntry[] => EMPTY;

/**
 * The entries of `timelineId` (default: the open editor's timeline), live.
 * Returns the store too, so a caller can launch / update / resolve.
 */
export function useBackgroundResults(timelineId?: string | null): { entries: readonly BackgroundEntry[]; store: ResultsStore | null } {
  const active = useSyncExternalStore(subscribeActive, () => activeTimelineId, () => null);
  const id = timelineId === undefined ? active : timelineId;
  const store = id ? getResultsStore(id) : null;
  const entries = useSyncExternalStore(store ? store.subscribe : noopSubscribe, store ? store.getEntries : noEntries, noEntries);
  return { entries, store };
}
