import type { EditTimeline, Item, Track } from '@/lib/editing/types';
import { linkIdOf } from '@/lib/editing/link/groups';
import { MARKER_LANE_HEIGHT, RULER_HEIGHT, frameToX, hitTestItem, trackRows, type PaintUi, type Viewport } from '../timeline/paint';
import { msg, type Msg } from '@/i18n/msg';

/** A directly edited lane is never thinner than this (touch rule); summary lanes are thin on purpose. */
export const STRIP_LANE_MIN = 56;
export const SUMMARY_LANE_HEIGHT = 28;
/** The selected clip's painted trim handle, and the finger-sized area around it. */
export const HANDLE_PAINT_PX = 11;
export const HANDLE_HIT_PX = 44;
/** A tap in a summary lane may miss the thin lane by this much vertically and still count. */
export const SUMMARY_TAP_SLOP_PX = 8;
export const STRIP_HEADER_PX = RULER_HEIGHT + MARKER_LANE_HEIGHT;

export interface StripLanes {
  /** The primary video track (the first video track that holds clips), or null for an empty timeline. */
  primaryId: string | null;
  /** The primary track plus the audio tracks that hold the sound linked to its clips: edited directly. */
  fullIds: ReadonlySet<string>;
  /** Every other track: a thin summary lane. */
  summaryIds: ReadonlySet<string>;
}

export function stripLanes(doc: EditTimeline): StripLanes {
  const videos = doc.tracks.filter((t) => t.kind === 'video');
  const primary = videos.find((t) => t.items.length > 0) ?? videos[0] ?? null;
  const full = new Set<string>();
  if (primary) {
    full.add(primary.id);
    const links = new Set<string>();
    for (const item of primary.items) {
      const id = linkIdOf(item);
      if (id !== null) links.add(id);
    }
    for (const track of doc.tracks) {
      if (track.kind !== 'audio') continue;
      if (track.items.some((item) => {
        const id = linkIdOf(item);
        return id !== null && links.has(id);
      })) full.add(track.id);
    }
  }
  const summary = new Set<string>();
  for (const track of doc.tracks) if (!full.has(track.id)) summary.add(track.id);
  return { primaryId: primary?.id ?? null, fullIds: full, summaryIds: summary };
}

/** The document with lane heights set for the strip: direct lanes >= 56 px, summary lanes thin. Presentation only. */
export function stripDoc(doc: EditTimeline, lanes: StripLanes): EditTimeline {
  return { ...doc, tracks: doc.tracks.map((t) => ({ ...t, height: lanes.fullIds.has(t.id) ? Math.max(STRIP_LANE_MIN, Math.round(t.height) || STRIP_LANE_MIN) : SUMMARY_LANE_HEIGHT })) };
}

/** Scroll that keeps `playheadFrame` under the centre of a `widthPx` strip. May be negative before the first frame. */
export function centreScroll(playheadFrame: number, widthPx: number, pixelsPerFrame: number): number {
  return playheadFrame - widthPx / 2 / pixelsPerFrame;
}

export function stripViewport(playheadFrame: number, widthPx: number, heightPx: number, pixelsPerFrame: number, devicePixelRatio: number): Viewport {
  return { width: widthPx, height: heightPx, pixelsPerFrame, devicePixelRatio, scrollFrame: centreScroll(playheadFrame, widthPx, pixelsPerFrame) };
}

/** x of a frame in the centre-locked strip; the playhead is always at widthPx / 2. */
export function stripFrameToX(frame: number, playheadFrame: number, widthPx: number, pixelsPerFrame: number): number {
  return frameToX(frame, stripViewport(playheadFrame, widthPx, 0, pixelsPerFrame, 1));
}

export function stripXToFrame(x: number, playheadFrame: number, widthPx: number, pixelsPerFrame: number): number {
  return playheadFrame + (x - widthPx / 2) / pixelsPerFrame;
}

/** Dragging the strip by `dxPx` moves the content with the finger, so the playhead goes the other way. */
export function panPlayhead(startFrame: number, dxPx: number, pixelsPerFrame: number, lastFrame: number): number {
  return Math.min(Math.max(0, lastFrame), Math.max(0, Math.round(startFrame - dxPx / pixelsPerFrame)));
}

export function timelineEnd(doc: EditTimeline): number {
  let end = 0;
  for (const track of doc.tracks) for (const item of track.items) end = Math.max(end, item.recordOut);
  return end;
}

/** What a tap at (x, y) lands on: a clip (summary lanes forgive a near miss in y), or nothing. */
export function stripHitTest(doc: EditTimeline, viewport: Viewport, ui: PaintUi, lanes: StripLanes, x: number, y: number): { item: Item; track: Track } | null {
  const direct = hitTestItem(doc, viewport, ui, x, y);
  if (direct) return direct;
  const frame = x / viewport.pixelsPerFrame + viewport.scrollFrame;
  for (const row of trackRows(doc, ui)) {
    if (!lanes.summaryIds.has(row.track.id)) continue;
    if (y < row.y - SUMMARY_TAP_SLOP_PX || y >= row.y + row.height + SUMMARY_TAP_SLOP_PX) continue;
    const item = row.track.items.find((i) => i.recordIn <= frame && frame < i.recordOut);
    if (item) return { item, track: row.track };
  }
  return null;
}

/** The clips of the primary track in time order: what "Clip 2 of 5" counts. */
export function stripClips(doc: EditTimeline, lanes: StripLanes): Item[] {
  const track = doc.tracks.find((t) => t.id === lanes.primaryId);
  return track ? [...track.items].sort((a, b) => a.recordIn - b.recordIn) : [];
}

function fpsOf(doc: EditTimeline): number {
  return doc.settings.rate.den === 0 ? 30 : doc.settings.rate.num / doc.settings.rate.den;
}

/** m:ss for a frame position ("0:04"). */
export function spanClock(frame: number, fps: number): string {
  const total = Math.max(0, Math.floor(frame / (fps || 30)));
  return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`;
}

/** The text alternative for the strip: "Clip 2 of 5, Beach.mp4, 0:04 to 0:09". */
export function describeSelection(doc: EditTimeline, lanes: StripLanes, selection: readonly string[]): string {
  const clips = stripClips(doc, lanes);
  const fps = fpsOf(doc);
  const total = clips.length;
  if (total === 0 && lanes.summaryIds.size === 0) return 'The timeline is empty';
  const id = selection[0];
  if (id === undefined) return total === 1 ? 'No clip selected, 1 clip' : `No clip selected, ${total} clips`;
  const index = clips.findIndex((c) => c.id === id);
  let found: Item | undefined = clips[index];
  let prefix: string;
  if (found) {
    prefix = `Clip ${index + 1} of ${total}`;
  } else {
    let on: Track | undefined;
    for (const t of doc.tracks) {
      const hit = t.items.find((i) => i.id === id);
      if (hit) {
        found = hit;
        on = t;
        break;
      }
    }
    if (!found || !on) return `No clip selected, ${total} clips`;
    const ordered = [...on.items].sort((a, b) => a.recordIn - b.recordIn);
    prefix = `Clip ${ordered.findIndex((i) => i.id === id) + 1} of ${ordered.length} on ${on.name || 'another track'}`;
  }
  const extra = selection.length > 1 ? `, and ${selection.length - 1} more selected` : '';
  return `${prefix}, ${found.name || 'Untitled'}, ${spanClock(found.recordIn, fps)} to ${spanClock(found.recordOut, fps)}${extra}`;
}

/**
 * The clip a "previous" / "next" button goes to: relative to the selected primary-track clip, or to the
 * playhead when nothing is selected. Null at either end.
 */
export function adjacentClip(clips: readonly Item[], selectedId: string | null, playheadFrame: number, direction: -1 | 1): Item | null {
  if (clips.length === 0) return null;
  const at = selectedId === null ? -1 : clips.findIndex((c) => c.id === selectedId);
  if (at >= 0) return clips[at + direction] ?? null;
  if (direction === 1) return clips.find((c) => c.recordIn > playheadFrame) ?? null;
  for (let i = clips.length - 1; i >= 0; i--) if (clips[i].recordIn < playheadFrame) return clips[i];
  return null;
}

export interface HandleRects {
  /** The painted bar: x, width. */
  paintX: number;
  paintW: number;
  /** The finger-sized area, centred on the clip edge, at least 44 px wide and the lane tall. */
  hitX: number;
  hitW: number;
}

/** Trim handle geometry for one edge of the selected clip at screen x `edgeX`. The painted bar sits inside the clip. */
export function handleRects(edgeX: number, edge: 'in' | 'out'): HandleRects {
  const paintX = edge === 'in' ? edgeX : edgeX - HANDLE_PAINT_PX;
  return { paintX, paintW: HANDLE_PAINT_PX, hitX: edgeX - HANDLE_HIT_PX / 2, hitW: HANDLE_HIT_PX };
}

/** Screen row of a track in the strip, for placing DOM over the canvas. */
export function laneRow(doc: EditTimeline, ui: PaintUi, trackId: string): { y: number; height: number } | null {
  const row = trackRows(doc, ui).find((r) => r.track.id === trackId);
  return row ? { y: row.y, height: row.height } : null;
}

/** The same text alternative as `describeSelection`, as a catalogue message (what the strip's aria-label shows). */
export function describeSelectionMsg(doc: EditTimeline, lanes: StripLanes, selection: readonly string[]): Msg {
  const clips = stripClips(doc, lanes);
  const fps = fpsOf(doc);
  const total = clips.length;
  if (total === 0 && lanes.summaryIds.size === 0) return msg('editor-helpers-2.text.timelineEmpty');
  const id = selection[0];
  if (id === undefined) return msg('editor-helpers-2.fmt.noneSelected', { count: total });
  const index = clips.findIndex((c) => c.id === id);
  let found: Item | undefined = clips[index];
  let on: Track | undefined;
  let n = index + 1;
  let count = total;
  if (!found) {
    for (const t of doc.tracks) {
      const hit = t.items.find((i) => i.id === id);
      if (hit) {
        found = hit;
        on = t;
        break;
      }
    }
    if (!found || !on) return msg('editor-helpers-2.fmt.noneSelected', { count: total });
    const ordered = [...on.items].sort((a, b) => a.recordIn - b.recordIn);
    n = ordered.findIndex((i) => i.id === id) + 1;
    count = ordered.length;
  }
  const common = { n, total: count, name: found.name || msg('editor-helpers-2.fmt.untitled'), from: spanClock(found.recordIn, fps), to: spanClock(found.recordOut, fps), more: Math.max(0, selection.length - 1) };
  return on ? msg('editor-helpers-2.fmt.clipOfOn', { ...common, track: on.name || msg('editor-helpers-2.fmt.anotherTrack') }) : msg('editor-helpers-2.fmt.clipOf', common);
}
