/**
 * The Cut workspace's pure helpers (P6): the nearest edit point and smart insert, the fast-review
 * speed plan, and the boring-shot ranking over the analysis artifact's measured motion.
 *
 * Nothing here reads a timestamp from a model: the boring-shot ranking is arithmetic over the
 * deterministic per-shot `visual.motionMean` and duration the `VideoAnalyze` step measured, and
 * every edit is an engine op the store applies.
 */

import type { Insert } from '../engine';
import { editPoints, timelineEndFrame } from '../transport/navigation';
import { mapSourceSilenceToRecord, type SourceMatcher } from '../silence/remove-silence';
import type { EditTimeline, Item, MediaClip } from '../types';

// ─────────────────────────────────────────────────────────────────────────────
// Smart insert
// ─────────────────────────────────────────────────────────────────────────────

/** The edit point on `trackId` nearest to `frame` (the track's end counts; 0 on an empty track). */
export function nearestEditPoint(doc: EditTimeline, trackId: string, frame: number): number {
  const points = editPoints(doc, [trackId]);
  if (points.length === 0) return 0;
  let best = points[0];
  for (const p of points) if (Math.abs(p - frame) < Math.abs(best - frame)) best = p;
  return best;
}

export interface SmartInsertPlan {
  op: Insert;
  atFrame: number;
}

/**
 * Places `item` at the edit point nearest the playhead, rippling what follows — the Cut page's
 * one-key "smart insert". The item arrives with a duration only; its span is set here.
 */
export function compileSmartInsert(doc: EditTimeline, trackId: string, item: Item, playheadFrame: number): SmartInsertPlan | null {
  const track = doc.tracks.find((t) => t.id === trackId);
  if (!track) return null;
  const duration = item.recordOut - item.recordIn;
  if (duration <= 0) return null;
  const atFrame = nearestEditPoint(doc, trackId, playheadFrame);
  const placed = { ...item, recordIn: atFrame, recordOut: atFrame + duration } as Item;
  const index = track.items.filter((i) => i.recordIn < atFrame).length;
  return { op: { type: 'insert', trackId, index, item: placed }, atFrame };
}

// ─────────────────────────────────────────────────────────────────────────────
// Fast review
// ─────────────────────────────────────────────────────────────────────────────

export interface ReviewSpeedOptions {
  /** How long every clip should take to review, in seconds. Default 3. */
  targetSeconds?: number;
  minSpeed?: number;
  maxSpeed?: number;
}

/** The playback speed that shows a clip of `durationFrames` in about `targetSeconds`, clamped. */
export function reviewSpeed(durationFrames: number, fps: number, options: ReviewSpeedOptions = {}): number {
  const target = Math.max(0.5, options.targetSeconds ?? 3);
  const min = options.minSpeed ?? 1;
  const max = options.maxSpeed ?? 8;
  const seconds = durationFrames / Math.max(1e-6, fps);
  const raw = seconds / target;
  const clamped = Math.min(max, Math.max(min, raw));
  // Quantise to the shuttle steps a transport can show (1, 1.5, 2, 3, 4, 6, 8).
  const steps = [1, 1.5, 2, 3, 4, 6, 8].filter((s) => s >= min && s <= max);
  return steps.reduce((best, s) => (Math.abs(s - clamped) < Math.abs(best - clamped) ? s : best), steps[0] ?? clamped);
}

export interface ReviewStop {
  itemId: string;
  startFrame: number;
  endFrame: number;
  speed: number;
}

/** One stop per media clip on the track: play it at a speed scaled to its length. */
export function reviewPlan(doc: EditTimeline, trackId: string, fps: number, options?: ReviewSpeedOptions): ReviewStop[] {
  const track = doc.tracks.find((t) => t.id === trackId);
  if (!track) return [];
  return track.items
    .filter((i): i is MediaClip => i.type === 'mediaClip')
    .map((c) => ({ itemId: c.id, startFrame: c.recordIn, endFrame: c.recordOut, speed: reviewSpeed(c.recordOut - c.recordIn, fps, options) }));
}

/** The review stop containing `frame`, so the transport can switch speed as playback crosses a cut. */
export function reviewStopAt(plan: readonly ReviewStop[], frame: number): ReviewStop | null {
  return plan.find((s) => frame >= s.startFrame && frame < s.endFrame) ?? null;
}

// ─────────────────────────────────────────────────────────────────────────────
// Boring shots
// ─────────────────────────────────────────────────────────────────────────────

export interface AnalysedShot {
  id: string;
  startSec: number;
  endSec: number;
  sourceIndex: number;
  /** Mean inter-frame motion of the shot (0..1), or null when visual analysis did not run. */
  motionMean: number | null;
  motionClass: string | null;
  duplicateGroupId: string | null;
  isBestTake: boolean;
}

const num = (e: Record<string, unknown>, a: string, b: string): number | null => {
  const v = e[a] ?? e[b];
  return typeof v === 'number' && Number.isFinite(v) ? v : null;
};

/** The `shots` of a VideoAnalyze artifact, read defensively. */
export function readShots(artifact: unknown): AnalysedShot[] {
  if (typeof artifact !== 'object' || artifact === null) return [];
  const raw = (artifact as Record<string, unknown>).shots ?? (artifact as Record<string, unknown>).Shots;
  if (!Array.isArray(raw)) return [];
  const out: AnalysedShot[] = [];
  raw.forEach((entry, index) => {
    if (typeof entry !== 'object' || entry === null) return;
    const e = entry as Record<string, unknown>;
    const startSec = num(e, 'startSec', 'StartSec');
    const endSec = num(e, 'endSec', 'EndSec');
    if (startSec === null || endSec === null || endSec <= startSec) return;
    const visualRaw = e.visual ?? e.Visual;
    const visual = typeof visualRaw === 'object' && visualRaw !== null ? (visualRaw as Record<string, unknown>) : null;
    const idRaw = e.id ?? e.Id;
    const classRaw = visual ? visual.motionClass ?? visual.MotionClass : null;
    const groupRaw = visual ? visual.duplicateGroupId ?? visual.DuplicateGroupId : null;
    const bestRaw = visual ? visual.isBestTake ?? visual.IsBestTake : null;
    out.push({
      id: typeof idRaw === 'string' ? idRaw : `s${index}`,
      startSec,
      endSec,
      sourceIndex: num(e, 'sourceIndex', 'SourceIndex') ?? 0,
      motionMean: visual ? num(visual, 'motionMean', 'MotionMean') : null,
      motionClass: typeof classRaw === 'string' ? classRaw : null,
      duplicateGroupId: typeof groupRaw === 'string' ? groupRaw : null,
      isBestTake: bestRaw === true,
    });
  });
  return out;
}

export interface BoringShot extends AnalysedShot {
  durationSec: number;
  /** Higher is more boring: long and still. */
  score: number;
  /** Where the shot sits on the timeline track, when it does. */
  record: { startFrame: number; endFrame: number; clipId: string } | null;
}

export interface BoringShotOptions {
  /** Shots shorter than this are never boring. Default 4 s. */
  minDurationSec?: number;
  /** Shots moving more than this are never boring. Default 0.08 (the "Static" class ceiling). */
  maxMotion?: number;
}

/**
 * Ranks the shots a viewer is most likely to find slow: long AND still. Shots without a measured
 * motion are left out rather than guessed about. When a document and track are given, each shot
 * is also located on the timeline so the panel can select or remove it.
 */
export function rankBoringShots(shots: readonly AnalysedShot[], options: BoringShotOptions = {}, locate?: { doc: EditTimeline; trackId: string; fps: number; matches?: SourceMatcher }): BoringShot[] {
  const minDuration = options.minDurationSec ?? 4;
  const maxMotion = options.maxMotion ?? 0.08;
  const out: BoringShot[] = [];
  for (const shot of shots) {
    const durationSec = shot.endSec - shot.startSec;
    if (shot.motionMean === null || durationSec < minDuration || shot.motionMean > maxMotion) continue;
    const stillness = 1 - Math.min(1, shot.motionMean / maxMotion);
    const score = durationSec * (0.5 + 0.5 * stillness);
    let record: BoringShot['record'] = null;
    if (locate) {
      const track = locate.doc.tracks.find((t) => t.id === locate.trackId);
      for (const item of track?.items ?? []) {
        if (item.type !== 'mediaClip') continue;
        const span = mapSourceSilenceToRecord(item, { id: shot.id, startSec: shot.startSec, endSec: shot.endSec, sourceIndex: shot.sourceIndex }, locate.fps, locate.matches);
        if (span) {
          record = { ...span, clipId: item.id };
          break;
        }
      }
    }
    out.push({ ...shot, durationSec, score, record });
  }
  return out.sort((a, b) => b.score - a.score);
}

/** A spoken word, in source seconds, as the speech-aware ranking reads it. */
export interface SpokenWord {
  startSec: number;
  endSec: number;
  sourceIndex: number;
}

export type SpeechState = 'unchecked' | 'none' | 'trimmed' | 'blocked';

/** R09.8: a boring shot with the speech taken into account. */
export interface SpeechAwareShot extends BoringShot {
  speech: SpeechState;
  /** The part of the shot that is safe to cut (no speech), in source seconds; null when `blocked`. */
  cutSec: { startSec: number; endSec: number } | null;
  /** Where that part sits on the timeline, when it does. */
  cutRecord: { startFrame: number; endFrame: number; clipId: string } | null;
  /** Seconds of the shot that are kept because someone is talking. */
  keptSpeechSec: number;
}

/** The longest stretch of `[startSec, endSec]` that stays `marginSec` away from every word. */
export function longestQuietStretch(startSec: number, endSec: number, words: readonly Pick<SpokenWord, 'startSec' | 'endSec'>[], marginSec = 0.25): { startSec: number; endSec: number } | null {
  const blocked = words
    .map((w) => ({ a: Math.max(startSec, w.startSec - marginSec), b: Math.min(endSec, w.endSec + marginSec) }))
    .filter((r) => r.b > r.a)
    .sort((x, y) => x.a - y.a);
  let best: { startSec: number; endSec: number } | null = null;
  let cursor = startSec;
  const consider = (a: number, b: number) => {
    if (b - a > 1e-6 && (!best || b - a > best.endSec - best.startSec)) best = { startSec: a, endSec: b };
  };
  for (const r of blocked) {
    consider(cursor, r.a);
    cursor = Math.max(cursor, r.b);
  }
  consider(cursor, endSec);
  return best;
}

/**
 * R09.8: boring shots that never cut a sentence in half. A shot with no speech is offered whole; a
 * shot with speech is trimmed to its longest quiet stretch (at least `minCutSec`), or left out of the
 * cut list (`blocked`) when it is all talking. With no transcript at all every shot is `unchecked`
 * and offered as measured.
 */
export function speechAwareShots(
  boring: readonly BoringShot[],
  words: readonly SpokenWord[] | null,
  options: { marginSec?: number; minCutSec?: number } = {},
  locate?: { doc: EditTimeline; fps: number; matches?: SourceMatcher },
): SpeechAwareShot[] {
  const minCut = options.minCutSec ?? 1.5;
  return boring.map((shot): SpeechAwareShot => {
    if (words === null) return { ...shot, speech: 'unchecked', cutSec: { startSec: shot.startSec, endSec: shot.endSec }, cutRecord: shot.record, keptSpeechSec: 0 };
    const inside = words.filter((w) => w.sourceIndex === shot.sourceIndex && w.endSec > shot.startSec && w.startSec < shot.endSec);
    if (inside.length === 0) return { ...shot, speech: 'none', cutSec: { startSec: shot.startSec, endSec: shot.endSec }, cutRecord: shot.record, keptSpeechSec: 0 };
    const quiet = longestQuietStretch(shot.startSec, shot.endSec, inside, options.marginSec ?? 0.25);
    if (!quiet || quiet.endSec - quiet.startSec < minCut) return { ...shot, speech: 'blocked', cutSec: null, cutRecord: null, keptSpeechSec: shot.durationSec };
    let cutRecord: SpeechAwareShot['cutRecord'] = null;
    if (locate && shot.record) {
      const clip = locate.doc.tracks.flatMap((t) => t.items).find((i): i is MediaClip => i.id === shot.record?.clipId && i.type === 'mediaClip');
      const span = clip ? mapSourceSilenceToRecord(clip, { id: shot.id, startSec: quiet.startSec, endSec: quiet.endSec, sourceIndex: shot.sourceIndex }, locate.fps, locate.matches) : null;
      if (clip && span) cutRecord = { ...span, clipId: clip.id };
    }
    return { ...shot, speech: 'trimmed', cutSec: quiet, cutRecord, keptSpeechSec: shot.durationSec - (quiet.endSec - quiet.startSec) };
  });
}

/** The pixels-per-frame that fits the whole timeline into `widthPx` (the overview strip's zoom). */
export function overviewPixelsPerFrame(doc: EditTimeline, widthPx: number): number {
  const end = Math.max(1, timelineEndFrame(doc));
  return Math.max(0.01, Math.min(64, widthPx / end));
}
