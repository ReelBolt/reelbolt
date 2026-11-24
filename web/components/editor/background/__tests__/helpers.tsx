import { vi } from 'vitest';
import { createEditorStore, type EditorStore } from '@/lib/editing/store';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { assetRef } from '@/lib/editing/__tests__/fixtures';
import type { MediaClip } from '@/lib/editing/types';
import { anchorForClips } from '@/lib/editing/background/anchor';
import { getResultsStore, type BackgroundEntry } from '@/lib/editing/background/results-store';

/** jsdom has no matchMedia / ResizeObserver, which Mantine reads. */
export function installDomStubs(): void {
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
  (globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
  Element.prototype.scrollIntoView ??= vi.fn();
}

export const STEP = '00000000-0000-4000-8000-0000000000aa';

/** Clip x (the one a job runs on) and clip o (another video), on one video track. */
export function makeStore(extra: MediaClip[] = []): EditorStore {
  return createEditorStore({
    timelineId: 't',
    projectId: 'p',
    doc: doc([track(TRACKS.v1, 'video', [media('x', 0, 60, 0), media('o', 60, 120, 0, { assetRef: assetRef(2) }), ...extra])]),
    revision: 1,
  });
}

export function launchOn(store: EditorStore, over: Partial<BackgroundEntry> = {}): BackgroundEntry {
  const entry: BackgroundEntry = {
    id: 'r1',
    tool: 'objectEffect',
    requestId: 'r1',
    executionId: 'e1',
    anchor: anchorForClips(store.getState().doc, ['x'], { startSec: 0, endSec: null }),
    intent: { prompt: 'face', effect: 'blur', strength: 0.7, invert: false, featherPx: 0 },
    state: 'running',
    createdAt: '2026-01-01T00:00:00Z',
    ...over,
  };
  getResultsStore('t').launch(entry);
  return entry;
}

export const clipOf = (store: EditorStore, id: string): MediaClip => store.getState().doc.tracks.flatMap((t) => t.items).find((i) => i.id === id) as MediaClip;
