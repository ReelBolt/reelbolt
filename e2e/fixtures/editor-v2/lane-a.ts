import { expect } from '@playwright/test';
import type { EditorApi, Item, Timeline, TimelineDocument } from './api';
import { mediaClip } from './api';
import type { EditorPage } from './editor-page';
import type { Seed } from './seed';

/**
 * Helpers of the lane-A specs (tests/editor-v2/2a-timeline-core): small timelines built through the
 * REST API so every spec starts from a known document and reads the SERVER's copy afterwards.
 */

export type ClipSpec = { name: string; file: keyof Seed['files']; recordIn: number; recordOut: number; extra?: Partial<Item> };

/** A fresh blank timeline whose first video track holds exactly `clips`. Extra video/audio tracks may be added. */
export async function clipsTimeline(
  freshTimeline: (from: 'blank' | 'seeded') => Promise<string>,
  api: EditorApi,
  seed: Seed,
  clips: ClipSpec[],
  extra: { secondVideoTrack?: boolean; audioClips?: ClipSpec[]; mutate?: (doc: TimelineDocument) => void } = {},
): Promise<string> {
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    const v = doc.tracks.find((t) => t.kind === 'video')!;
    v.items = clips.map((c) => mediaClip(crypto.randomUUID(), c.name, seed.files[c.file], c.recordIn, c.recordOut, c.extra));
    if (extra.secondVideoTrack) doc.tracks.splice(1, 0, { ...structuredClone(v), id: crypto.randomUUID(), name: 'Video 2', items: [] });
    if (extra.audioClips) {
      const a = doc.tracks.find((t) => t.kind === 'audio')!;
      a.items = extra.audioClips.map((c) => mediaClip(crypto.randomUUID(), c.name, seed.files[c.file], c.recordIn, c.recordOut, c.extra));
    }
    extra.mutate?.(doc);
  });
  return id;
}

/** The standard pair: clipA [0,60) and clipB [60,120) on V1 (everything on screen at 412 px). */
export const PAIR: ClipSpec[] = [
  { name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 60 },
  { name: 'clipB.mp4', file: 'clipB', recordIn: 60, recordOut: 120 },
];

export const itemNamed = (tl: Timeline, name: string, track = 0): Item | undefined => tl.document.tracks[track].items.find((i) => i.name === name);
export const countItems = (tl: Timeline, track = 0): number => tl.document.tracks[track].items.length;

/** Pans the timeline body so a clip drawn at `frame` is on screen, then selects it. */
export async function select(ed: EditorPage, frame: number, track = 0): Promise<void> {
  await ed.selectClipAt(frame, track);
}

/** Waits for the sync chip to settle ("Saved") and returns the server copy. */
export async function settled(ed: EditorPage): Promise<Timeline> {
  return ed.expectSaved();
}

/** A visible toast (Mantine notification) containing `text`. */
export function toastWith(ed: EditorPage, text: RegExp) {
  return ed.page.locator('.mantine-Notification-root').filter({ hasText: text }).first();
}

export async function expectNoNewPatch(ed: EditorPage, sentBefore: number, message: string, settleMs = 1500): Promise<void> {
  // A negative: wait a bounded time for the sync debounce to have fired if it were going to.
  await ed.page.waitForTimeout(settleMs);
  expect(ed.patches.length, `${message}: ${ed.patchSummary()}`).toBe(sentBefore);
}

/** Moves the playhead through the timecode field and leaves the field, so the next key press is a shortcut (never typed into the field). */
export async function seekAndBlur(ed: EditorPage, frame: number): Promise<void> {
  await ed.seek(frame);
  await ed.page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
}

/**
 * The number of PATCHes the page has sent once the earlier ones are fully recorded. The server copy can
 * show an edit a moment before the page's response listener has logged it, so a "no further PATCH"
 * check must start from a count that has settled (`atLeast` known edits, all answered 200).
 */
export async function settledPatchCount(ed: EditorPage, atLeast: number): Promise<number> {
  await expect.poll(() => ed.patches.length, { message: `${atLeast} PATCH(es) recorded` }).toBeGreaterThanOrEqual(atLeast);
  await expect(ed.chip).toHaveText(/^\s*Saved/);
  return ed.patches.length;
}
