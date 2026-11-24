import { expect } from '@playwright/test';
import { mediaClip, type EditorApi, type Item, type Timeline, type TimelineDocument, type Track } from './api';
import type { EditorPage } from './editor-page';

/**
 * Helpers of the panels / Inspector lane (initiative editor-v2-playback-verify, lane B). New file: the shared
 * fixtures stay untouched.
 */

export const uuid = (): string => crypto.randomUUID();

export function firstTrack(doc: TimelineDocument, kind: 'video' | 'audio'): Track {
  const t = doc.tracks.find((x) => x.kind === kind);
  expect(t, `a ${kind} track`).toBeTruthy();
  return t!;
}

export function trackIndex(doc: TimelineDocument, kind: 'video' | 'audio'): number {
  return doc.tracks.findIndex((t) => t.kind === kind);
}

/** The stored item with this name, anywhere in the document. */
export function itemNamed(tl: Timeline, name: string): Item {
  for (const t of tl.document.tracks) {
    const it = t.items.find((i) => i.name === name);
    if (it) return it;
  }
  throw new Error(`no item named ${name}`);
}

export function itemById(tl: Timeline, id: string): Item {
  for (const t of tl.document.tracks) {
    const it = t.items.find((i) => i.id === id);
    if (it) return it;
  }
  throw new Error(`no item ${id}`);
}

export function titleItem(text: string, recordIn: number, recordOut: number, extra: Record<string, unknown> = {}): Item {
  return { type: 'titleClip', id: uuid(), name: `title ${text}`, recordIn, recordOut, text, fontSize: 64, fontFamily: 'sans-serif', color: '#FFFFFF', enabled: true, provenance: null, ...extra } as Item;
}

export function generatorItem(name: string, recordIn: number, recordOut: number, extra: Record<string, unknown> = {}): Item {
  return { type: 'generatorClip', id: uuid(), name, recordIn, recordOut, generatorType: 'color', generatorConfig: { color: '#336699' }, enabled: true, provenance: null, ...extra } as Item;
}

export function captionItem(text: string, recordIn: number, recordOut: number): Item {
  return { type: 'caption', id: uuid(), name: `caption ${text}`, recordIn, recordOut, text, language: 'en', enabled: true, provenance: null } as Item;
}

/**
 * Appends a new video track holding `items` (PATCH with `ops: []`, how the suite seeds). With `dropAudio` the
 * audio tracks are removed first, so the two picture tracks are the only rows and both fit in the timeline
 * panel (the canvas does not scroll vertically in a 900 px window, a third row would be off screen).
 */
export async function addVideoTrack(api: EditorApi, projectId: string, timelineId: string, name: string, items: Item[], options: { dropAudio?: boolean } = {}): Promise<Timeline> {
  return api.putDocument(projectId, timelineId, (doc) => {
    if (options.dropAudio) doc.tracks = doc.tracks.filter((t) => t.kind !== 'audio');
    doc.tracks.push({ id: uuid(), kind: 'video', name, height: 56, locked: false, enabled: true, mute: false, solo: false, items } as unknown as Track);
  });
}

/** How many ops of `type` the page has sent in successful PATCHes. */
export function sentOps(ed: EditorPage, type: string): number {
  return ed.patches.filter((p) => p.status === 200).reduce((n, p) => n + p.ops.filter((o) => o === type).length, 0);
}

/** Every op type sent in successful PATCHes, in order. */
export function allSentOps(ed: EditorPage): string[] {
  return ed.patches.filter((p) => p.status === 200).flatMap((p) => p.ops);
}

/**
 * Selects the item drawn at `frame` on track `trackIndex`. Desktop clicks it on the canvas; a phone selects it
 * through the Edits panel (a row tap selects the item and moves the playhead to its start): Playwright's
 * touch tap on the <canvas> does not select under WebKit (the existing P5-KEY-01 fails the same way there),
 * and clips past ~150 frames are off screen on a 412 px timeline. Same selection, reachable by tap.
 */
export async function selectAt(ed: EditorPage, frame: number, trackIndex = 0): Promise<void> {
  if (!ed.isPhone) {
    await ed.selectClipAt(frame, trackIndex);
    return;
  }
  const doc = (await ed.serverTimeline()).document;
  const item = doc.tracks[trackIndex].items.find((i) => i.recordIn <= frame && frame < i.recordOut);
  expect(item, `an item at frame ${frame} on track ${trackIndex}`).toBeTruthy();
  await ed.openPanelTab(/edits/i);
  await ed.tap(ed.testId(`edit-index-row-${item!.id}`));
}

/** selectAt on the first/nth track of a kind, then opens the Inspector. */
export async function selectOnTrack(ed: EditorPage, kind: 'video' | 'audio', frame: number, nth = 0): Promise<void> {
  const doc = (await ed.serverTimeline()).document;
  let seen = -1;
  const idx = doc.tracks.findIndex((t) => t.kind === kind && ++seen === nth);
  expect(idx, `a ${kind} track #${nth}`).toBeGreaterThanOrEqual(0);
  await selectAt(ed, frame, idx);
  await ed.openInspector();
  await expect(ed.testId('inspector-panel')).toBeVisible();
}

/** A media clip with a fresh id, for a track the test adds. */
export function mediaClipOnNewTrack(name: string, projectFileId: string, recordIn: number, recordOut: number, extra: Partial<Item> = {}): Item {
  return mediaClip(uuid(), name, projectFileId, recordIn, recordOut, extra);
}
