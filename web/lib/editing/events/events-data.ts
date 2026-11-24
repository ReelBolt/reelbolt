/**
 * The events lane's data (video events analysis, editor surface): what the free `events` analysis kind
 * stores for a source file, read defensively, and mapped onto the record frames of the timeline's video
 * clips. The lane is derived, read-only data; none of it is in the document.
 *
 * An event is `{id: "e{n}", type, frame, timeSec, strength 0..1, region?, confidence}`, plus what the detector
 * measured beside the frame when it gave any: `endFrame`, `direction`, `speed` and `kind` (a result stored before
 * these existed simply lacks them). Every action the editor offers on an event resolves its time from
 * `frame`/`timeSec` here, never from text a model wrote.
 */

import type { EditTimeline, MediaClip } from '../types';
import { sourceSecToRecordFrame } from '../beats/map-to-record';

/** The closed type vocabulary the detector writes (append-only; an unknown word is drawn as `other`). */
export const EVENT_TYPES = [
  'appear', 'disappear', 'move_start', 'move_stop', 'hit', 'text_in', 'text_out',
  'camera_move_start', 'camera_move_stop', 'transition', 'change', 'beat', 'speech_start', 'speech_end',
  'flash', 'caption_tick',
] as const;
export type EventType = (typeof EVENT_TYPES)[number];

export interface EventRegion { x: number; y: number; w: number; h: number }

/** What kind of picture an event happens in (a closed list; any other word is read as unknown). */
export const EVENT_KINDS = ['graphic', 'text', 'footage'] as const;
export type EventKind = (typeof EVENT_KINDS)[number];

export interface SourceEvent {
  /** `e{n}`, in frame order within the source. */
  id: string;
  /** A word from {@link EVENT_TYPES}, or any other word the detector learns later. */
  type: string;
  /** The source's own frame grid. */
  frame: number;
  timeSec: number;
  strength: number;
  region: EventRegion | null;
  confidence: number;
  /** The last frame of a spread-out event (a fade, a slide, a camera move), when the detector gave one. */
  endFrame?: number;
  /** The direction word of a move (left, right, up, down, a diagonal, grow; in or out for a camera), when given. */
  direction?: string;
  /** The speed word of a move (slow, medium, fast), when given. */
  speed?: string;
  /** The kind of picture the event happens in, when the detector could tell. */
  kind?: EventKind;
}

export interface SourceEvents {
  fps: number;
  frameCount: number;
  events: SourceEvent[];
  truncated: boolean;
}

/** What the lane knows about one file's stored events, as far as deciding whether to find them (again) goes. */
export interface FileEventsNeed {
  state: string;
  events: SourceEvents | null;
  outdated?: boolean;
}

/**
 * True for a file the lane's "find events" would analyse: it has no events yet (not run, failed, or unreadable), or the
 * ones it has are outdated (from an earlier detector, or incomplete). An outdated file shows its ticks until it is analysed
 * again; while that run is queued or running the lane shows none for it, and the fresh result brings them back.
 */
export const needsEvents = (f: FileEventsNeed): boolean =>
  f.state === 'none' || f.state === 'failed' || (f.state === 'ready' && (f.events === null || f.outdated === true));

const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);
const num = (v: unknown): number | null => (typeof v === 'number' && Number.isFinite(v) ? v : null);
const clamp01 = (v: number): number => Math.min(1, Math.max(0, v));
/** A short lower-case word (letters and hyphens only), or undefined: stored text never reaches the screen unchecked. */
const word = (v: unknown): string | undefined => (typeof v === 'string' && /^[a-z][a-z-]{0,15}$/.test(v) ? v : undefined);
const kindOf = (v: unknown): EventKind | undefined => (EVENT_KINDS as readonly unknown[]).includes(v) ? (v as EventKind) : undefined;

function readRegion(raw: unknown): EventRegion | null {
  if (!isObject(raw)) return null;
  const x = num(raw.x), y = num(raw.y), w = num(raw.w), h = num(raw.h);
  return x === null || y === null || w === null || h === null ? null : { x: clamp01(x), y: clamp01(y), w: clamp01(w), h: clamp01(h) };
}

/** The `events` data of a file, or null when it is not readable. Bad entries are dropped, never thrown on. */
export function readEventsResult(raw: unknown): SourceEvents | null {
  if (!isObject(raw) || !Array.isArray(raw.events)) return null;
  const fps = num(raw.fps);
  const events: SourceEvent[] = [];
  for (const e of raw.events) {
    if (!isObject(e) || typeof e.id !== 'string' || typeof e.type !== 'string') continue;
    const frame = num(e.frame);
    if (frame === null || frame < 0) continue;
    const timeSec = num(e.timeSec) ?? (fps !== null && fps > 0 ? frame / fps : null);
    if (timeSec === null || timeSec < 0) continue;
    const event: SourceEvent = { id: e.id, type: e.type, frame: Math.trunc(frame), timeSec, strength: clamp01(num(e.strength) ?? 0), region: readRegion(e.region), confidence: clamp01(num(e.confidence) ?? 0) };
    const endFrame = num(e.endFrame);
    if (endFrame !== null && endFrame >= event.frame) event.endFrame = Math.trunc(endFrame);
    const direction = word(e.direction), speed = word(e.speed), kind = kindOf(e.kind);
    if (direction !== undefined) event.direction = direction;
    if (speed !== undefined) event.speed = speed;
    if (kind !== undefined) event.kind = kind;
    events.push(event);
  }
  events.sort((a, b) => a.timeSec - b.timeSec);
  return { fps: fps !== null && fps > 0 ? fps : 30, frameCount: Math.max(0, Math.trunc(num(raw.frameCount) ?? 0)), events, truncated: raw.truncated === true };
}

/** One event placed on the timeline: the clip it plays through and its record frame. */
export interface LaneEvent {
  /** Unique on the lane: file, event id and record frame. */
  key: string;
  fileId: string;
  eventId: string;
  clipId: string;
  trackId: string;
  type: string;
  /** The record frame on the timeline. */
  frame: number;
  /** The source second the event sits at (the clip's file). */
  sourceSec: number;
  strength: number;
  region: EventRegion | null;
  /** The kind of picture the event happens in, when the detector could tell. */
  kind?: EventKind;
}

/** The project file a media clip plays, or null (a step result, a generator). */
export const clipFileId = (clip: MediaClip): string | null =>
  clip.assetRef?.kind === 'projectFile' && typeof clip.assetRef.projectFileId === 'string' && clip.assetRef.projectFileId.length > 0 ? clip.assetRef.projectFileId : null;

/** The video clips of the timeline that show a project file, in track then time order. Holds have no motion and are skipped. */
export function laneClips(doc: EditTimeline): { clip: MediaClip; trackId: string; fileId: string }[] {
  const out: { clip: MediaClip; trackId: string; fileId: string }[] = [];
  for (const track of doc.tracks) {
    if (track.kind !== 'video') continue;
    for (const item of track.items) {
      if (item.type !== 'mediaClip' || item.hold === true || item.enabled === false) continue;
      const fileId = clipFileId(item);
      if (fileId !== null) out.push({ clip: item, trackId: track.id, fileId });
    }
  }
  return out;
}

/** The distinct files the lane reads, capped so a huge timeline cannot fan out requests. */
export function laneFileIds(doc: EditTimeline, cap = 8): string[] {
  const ids: string[] = [];
  for (const { fileId } of laneClips(doc)) if (!ids.includes(fileId) && ids.length < cap) ids.push(fileId);
  return ids;
}

const rateOf = (doc: EditTimeline): number => (doc.settings.rate.den > 0 && doc.settings.rate.num > 0 ? doc.settings.rate.num / doc.settings.rate.den : 30);

/** Every event of the files in `bySource`, on the record frames of the clips that play them, ascending. */
export function laneEvents(doc: EditTimeline, bySource: ReadonlyMap<string, SourceEvents>): LaneEvent[] {
  const fps = rateOf(doc);
  const seen = new Set<string>();
  const out: LaneEvent[] = [];
  for (const { clip, trackId, fileId } of laneClips(doc)) {
    const source = bySource.get(fileId);
    if (!source) continue;
    for (const e of source.events) {
      const frame = sourceSecToRecordFrame(clip, e.timeSec, fps);
      if (frame === null || frame < clip.recordIn || frame >= clip.recordOut) continue;
      const key = `${fileId}:${e.id}:${frame}`;
      if (seen.has(key)) continue;
      seen.add(key);
      const placed: LaneEvent = { key, fileId, eventId: e.id, clipId: clip.id, trackId, type: e.type, frame, sourceSec: e.timeSec, strength: e.strength, region: e.region };
      if (e.kind !== undefined) placed.kind = e.kind;
      out.push(placed);
    }
  }
  out.sort((a, b) => a.frame - b.frame || a.key.localeCompare(b.key));
  return out;
}

/** The first event at or after `frame` within `slack` frames before it, else the next one after; null when none. */
export function nearestEvent(events: readonly LaneEvent[], frame: number, slack = 12): LaneEvent | null {
  let best: LaneEvent | null = null;
  for (const e of events) {
    if (Math.abs(e.frame - frame) <= slack && (best === null || Math.abs(e.frame - frame) < Math.abs(best.frame - frame))) best = e;
  }
  if (best) return best;
  return events.find((e) => e.frame > frame) ?? null;
}
