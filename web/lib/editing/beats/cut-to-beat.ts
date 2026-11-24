/**
 * "Cut to the music": compiles a beat grid into a program of the engine's own ops. Pure: the input
 * document is never written to, and every op is produced by running `apply` on the evolving document
 * (like compileRemoveSilence), so ops are valid by construction. An op the engine refuses is dropped,
 * never thrown. Locked tracks are skipped and reported.
 *
 * The grid is mapped through the MUSIC clip (`beatSecondsToRecordFrames`) onto record frames inside
 * that clip; "grid frames" below are those frames at the plan's `unit`.
 *
 * ## snapCuts
 * For every pair of ADJACENT media clips (a.recordOut === b.recordIn) on each target track, the edit
 * point moves to the nearest grid frame within `toleranceFrames` via `trim roll` on a's out edge.
 * The delta the engine allows (source handles) is kept; zero allowed -> 'noHandle'. A linked partner
 * on another track with the same cut is rolled by the same delta; a locked partner track skips the
 * whole edit point ('locked'). Points outside the music clip's span -> 'outsideMusic'.
 *
 * ## split
 * Each item is bladed at every grid frame strictly inside it, right to left (the left half keeps the
 * id). A blade leaving a piece shorter than `minPieceFrames` is skipped ('tooShort'). Same-span linked
 * partners are bladed at the same frames; touched link ids are re-paired with `relinkOps`.
 *
 * ## markers
 * One marker per grid frame in the music clip (bars #f59f00, beats #868e96), named by the plan's
 * `markerName`; frames that already hold a marker are skipped; stops at the cap ('markerCap').
 *
 * ## fit (auto-assemble)
 * Clips of the track (record order, music clip excluded) are laid end to end from the start point
 * (music start, or the first bar line). Cursor C starts there. For each clip with length L:
 *   1. stopAtMusicEnd and C >= music end -> lift it ('pastMusicEnd'); its partners too.
 *   2. End E: barsPerClip 'auto' = the last grid frame after C within L of C (at least the first
 *      one after C); a number = that many bar lines after C. No grid frame left -> E = C + L.
 *   3. onDrop 'strongestClip' (needs `dropSec`): D = grid frame nearest the drop. Clips before the
 *      strongest one fill [start, D): E is capped at D, the clip before it ends exactly at D (lengthened
 *      by whole grid units within its source), and pre-drop clips that would start at or after D are
 *      lifted ('beforeDrop'). The strongest clip then starts at D and the rest follow it. When the
 *      strongest clip is FIRST, the other clips are laid in order before D while they end on or before
 *      D, the last of them is adjusted to end on D, then the strongest clip sits on D, then the
 *      remaining clips follow in order. If the clips before D cannot reach it (not enough source), the
 *      gap is left and reported ('gapBeforeDrop'); the strongest clip still starts on D.
 *      A clip with a linked item on a locked track is left alone ('locked'): neither it nor its linked
 *      items get an op, and the layout treats them as fixed obstacles (see below).
 *   4. stopAtMusicEnd: E > music end -> E = music end (the last kept clip); later clips are lifted.
 *   5. The out edge is trimmed by (E - C) - L; the engine's clamp may stop short (a clip shorter than
 *      the unit that cannot lengthen is kept as it is and reported 'shortClip'). C += actual length.
 * The layout is one ordered pass: every movable clip (with its linked items) is first moved to a
 * staging area past anything the layout can reach, then each is trimmed there and moved to its final
 * place, so no move ever waits on another and nothing stays in the staging area. A final place that
 * overlaps a fixed item (a locked clip and its linked items, or any other item on the clip's or its
 * linked items' tracks) is pushed to the first grid frame after it. Linked items: same span -> the
 * same trim and move; other spans -> the same shift. A clip lifted is lifted with its linked items.
 */

import { apply, type EditOp } from '../engine';
import type { EditTimeline, Item, MediaClip, Track } from '../types';
import { relinkOps } from '../silence/remove-silence';
import { gridLines, type BeatGrid, type GridLine, type GridUnit } from './beat-grid';
import { beatSecondsToRecordFrames, sourceSecToRecordFrame } from './map-to-record';

export type SkipReason = 'locked' | 'hold' | 'noMusic' | 'outsideMusic' | 'noHandle' | 'tooShort' | 'markerCap' | 'shortClip' | 'pastMusicEnd' | 'beforeDrop' | 'gapBeforeDrop' | 'invalid';

export type CutToBeatMode =
  | { kind: 'snapCuts'; trackIds: string[]; toleranceFrames: number }
  | { kind: 'split'; itemIds: string[]; minPieceFrames: number }
  | { kind: 'markers'; maxMarkers?: number }
  | {
      kind: 'fit';
      trackId: string;
      barsPerClip: 'auto' | number;
      startAt: 'musicStart' | 'firstDownbeat';
      onDrop: 'none' | 'strongestClip';
      rank?: Record<string, number>;
      stopAtMusicEnd: boolean;
      /** Drop position in source seconds of the music file (BeatAnalysis.dropSec); needed for onDrop. */
      dropSec?: number | null;
    };

export interface CutToBeatPlan {
  musicClipId: string;
  grid: BeatGrid;
  unit: GridUnit;
  newItemId: () => string;
  mode: CutToBeatMode;
  /** Names for markers, supplied by the UI (translated). Default: the bar number for bars, empty for beats. */
  markerName?: (line: { frame: number; bar: boolean; barNumber: number }) => string;
  /**
   * Total source length of a clip's file in timeline frames (before playbackRate), or null when unknown.
   * `fit` lengthens a clip only within it; without a number a clip is never lengthened ('shortClip').
   */
  sourceFrames?: (clip: MediaClip) => number | null;
  /** The undo label. Default `cutToBeat.<mode>`. */
  label?: string;
}

export interface CutToBeatSummary {
  cutsMoved: number;
  splits: number;
  markers: number;
  clipsFitted: number;
  clipsDropped: number;
  skipped: { itemId: string | null; reason: SkipReason }[];
}

export interface CutToBeatResult {
  ops: EditOp[];
  label: string;
  preview: EditTimeline;
  summary: CutToBeatSummary;
}

export const BAR_MARKER_COLOR = '#f59f00';
export const BEAT_MARKER_COLOR = '#868e96';
export const DEFAULT_MAX_MARKERS = 500;
export const HARD_MAX_MARKERS = 1000;

const lengthOf = (i: Item): number => i.recordOut - i.recordIn;

/** Record frames a clip's out edge may still grow: 0 when the source length is unknown. */
function extensionRoom(clip: MediaClip, sourceFrames: ((c: MediaClip) => number | null) | undefined): number {
  const total = sourceFrames?.(clip);
  if (typeof total !== 'number' || !Number.isFinite(total)) return 0;
  const rate = typeof clip.playbackRate === 'number' && clip.playbackRate > 0 ? clip.playbackRate : 1;
  const sourceIn = typeof clip.sourceIn === 'number' ? clip.sourceIn : 0;
  // Reversed, the out edge reaches back toward sourceIn (the engine clamps at frame 0); forward, toward the source end.
  if (clip.reverse === true) return Math.max(0, Math.floor(sourceIn / rate));
  return Math.max(0, Math.floor((total - sourceIn - lengthOf(clip) * rate) / rate));
}

export function compileCutToBeat(doc: EditTimeline, plan: CutToBeatPlan): CutToBeatResult {
  const label = plan.label ?? `cutToBeat.${plan.mode.kind}`;
  const summary: CutToBeatSummary = { cutsMoved: 0, splits: 0, markers: 0, clipsFitted: 0, clipsDropped: 0, skipped: [] };
  const ops: EditOp[] = [];
  let current = doc;
  const finish = (): CutToBeatResult => ({ ops, label, preview: current, summary });
  const skip = (itemId: string | null, reason: SkipReason): void => {
    summary.skipped.push({ itemId, reason });
  };

  const locate = (d: EditTimeline, id: string): { track: Track; item: Item } | null => {
    for (const track of d.tracks) {
      const item = track.items.find((i) => i.id === id);
      if (item) return { track, item };
    }
    return null;
  };
  const clipOf = (id: string): { track: Track; clip: MediaClip } | null => {
    const found = locate(current, id);
    return found && found.item.type === 'mediaClip' ? { track: found.track, clip: found.item } : null;
  };
  /** Runs an op on the evolving document; false (and no op recorded) when the engine refuses it. */
  const run = (op: EditOp): boolean => {
    try {
      current = apply(current, op).doc;
    } catch {
      return false;
    }
    ops.push(op);
    return true;
  };

  const musicFound = locate(doc, plan.musicClipId);
  if (!musicFound || musicFound.item.type !== 'mediaClip') {
    skip(plan.musicClipId, 'noMusic');
    return finish();
  }
  const music = musicFound.item;
  if (music.hold === true) {
    skip(plan.musicClipId, 'hold');
    return finish();
  }
  const fps = doc.settings.rate.den > 0 ? doc.settings.rate.num / doc.settings.rate.den : 30;
  const mapLines = (lines: GridLine[]): { frame: number; bar: boolean; barNumber: number }[] => {
    const mapped = beatSecondsToRecordFrames(music, lines, fps);
    return Array.isArray(mapped) ? mapped : [];
  };
  const lines = mapLines(gridLines(plan.grid, plan.unit));
  if (lines.length === 0) return finish();
  const frames = lines.map((l) => l.frame);

  const partnersOf = (clip: MediaClip, ownTrackId: string): { track: Track; clip: MediaClip }[] => {
    if (typeof clip.linkId !== 'string' || clip.linkId.length === 0) return [];
    const out: { track: Track; clip: MediaClip }[] = [];
    for (const t of current.tracks) {
      if (t.id === ownTrackId) continue;
      for (const i of t.items) {
        if (i.type === 'mediaClip' && i.linkId === clip.linkId && i.recordIn === clip.recordIn && i.recordOut === clip.recordOut) out.push({ track: t, clip: i });
      }
    }
    return out;
  };

  const mode = plan.mode;

  if (mode.kind === 'snapCuts') {
    const tolerance = Math.max(0, Math.floor(mode.toleranceFrames));
    for (const trackId of mode.trackIds) {
      const track = doc.tracks.find((t) => t.id === trackId);
      if (!track) continue;
      if (track.locked) {
        skip(null, 'locked');
        continue;
      }
      const pairs: string[] = [];
      const media = track.items;
      for (let i = 0; i + 1 < media.length; i++) {
        const a = media[i];
        const b = media[i + 1];
        if (a.type === 'mediaClip' && b.type === 'mediaClip' && a.recordOut === b.recordIn) pairs.push(a.id);
      }
      for (const aId of pairs) {
        const found = clipOf(aId);
        if (!found) continue;
        const a = found.clip;
        const point = a.recordOut;
        const idx = found.track.items.findIndex((i) => i.id === aId);
        const next = found.track.items[idx + 1];
        if (!next || next.type !== 'mediaClip' || next.recordIn !== point) continue;
        if (point < music.recordIn || point > music.recordOut) {
          skip(aId, 'outsideMusic');
          continue;
        }
        let target = frames[0];
        for (const f of frames) if (Math.abs(f - point) < Math.abs(target - point)) target = f;
        let delta = target - point;
        if (delta === 0 || Math.abs(delta) > tolerance) continue;
        // Never roll a clip past the end of its source file (only when the plan knows the length).
        if (delta > 0 && typeof plan.sourceFrames?.(a) === 'number') {
          delta = Math.min(delta, extensionRoom(a, plan.sourceFrames));
          if (delta <= 0) {
            skip(aId, 'noHandle');
            continue;
          }
        }

        const partners = partnersOf(a, found.track.id)
          .map((p) => {
            const at = p.track.items.findIndex((i) => i.id === p.clip.id);
            const after = p.track.items[at + 1];
            return after && after.type === 'mediaClip' && after.recordIn === point ? p : null;
          })
          .filter((p): p is { track: Track; clip: MediaClip } => p !== null);
        if (partners.some((p) => p.track.locked)) {
          skip(aId, 'locked');
          continue;
        }
        const snapshot = current;
        if (!run({ type: 'trim', itemId: aId, edge: 'out', mode: 'roll', deltaFrames: delta })) {
          skip(aId, 'invalid');
          continue;
        }
        const effective = (clipOf(aId)?.clip.recordOut ?? point) - point;
        if (effective === 0) {
          ops.pop();
          current = snapshot;
          skip(aId, 'noHandle');
          continue;
        }
        for (const p of partners) run({ type: 'trim', itemId: p.clip.id, edge: 'out', mode: 'roll', deltaFrames: effective });
        summary.cutsMoved++;
      }
    }
    return finish();
  }

  if (mode.kind === 'split') {
    const touched = new Set<string>();
    const handled = new Set<string>();
    for (const itemId of mode.itemIds) {
      if (handled.has(itemId)) continue;
      const found = clipOf(itemId);
      if (!found) {
        skip(itemId, 'invalid');
        continue;
      }
      if (found.track.locked) {
        skip(itemId, 'locked');
        continue;
      }
      const clip = found.clip;
      const minPiece = Math.max(1, Math.floor(mode.minPieceFrames));
      const partners = partnersOf(clip, found.track.id);
      const usable: MediaClip[] = [];
      for (const p of partners) {
        handled.add(p.clip.id);
        if (p.track.locked) skip(p.clip.id, 'locked');
        else usable.push(p.clip);
      }
      if (typeof clip.linkId === 'string') touched.add(clip.linkId);
      const inside = frames.filter((f) => f > clip.recordIn && f < clip.recordOut).sort((x, y) => y - x);
      let rightEdge = clip.recordOut;
      for (const f of inside) {
        if (rightEdge - f < minPiece || f - clip.recordIn < minPiece) {
          skip(itemId, 'tooShort');
          continue;
        }
        if (!run({ type: 'blade', itemId, atFrame: f, newItemId: plan.newItemId() })) continue;
        for (const p of usable) run({ type: 'blade', itemId: p.id, atFrame: f, newItemId: plan.newItemId() });
        summary.splits++;
        rightEdge = f;
      }
    }
    for (const op of relinkOps(current, touched, plan.newItemId)) run(op);
    return finish();
  }

  if (mode.kind === 'markers') {
    const cap = Math.min(HARD_MAX_MARKERS, Math.max(1, Math.floor(mode.maxMarkers ?? DEFAULT_MAX_MARKERS)));
    const taken = new Set(doc.markers.map((m) => m.frame));
    const name = plan.markerName ?? ((l: { bar: boolean; barNumber: number }) => (l.bar ? String(l.barNumber) : ''));
    for (const line of lines) {
      if (taken.has(line.frame)) continue;
      if (summary.markers >= cap) {
        skip(null, 'markerCap');
        break;
      }
      const ok = run({ type: 'addMarker', marker: { id: plan.newItemId(), frame: line.frame, name: name(line), color: line.bar ? BAR_MARKER_COLOR : BEAT_MARKER_COLOR } });
      if (!ok) continue;
      taken.add(line.frame);
      summary.markers++;
    }
    return finish();
  }

  // fit
  const track = doc.tracks.find((t) => t.id === mode.trackId);
  if (!track) return finish();
  if (track.locked) {
    skip(null, 'locked');
    return finish();
  }
  // A clip's group: the clip plus every item sharing its link id on another track. Same-span members
  // ("partners") get the same trim; the others keep their length and move by the same shift. A clip
  // with any member on a locked track is not touched: it and its members stay where they are and act
  // as fixed obstacles for the layout ('locked').
  type Member = { id: string; trackId: string; equal: boolean; offset: number; length: number };
  type Group = { clip: MediaClip; members: Member[] };
  const owned = new Set<string>();
  const groups = new Map<string, Group>();
  const clips: MediaClip[] = [];
  for (const c of track.items
    .filter((i): i is MediaClip => i.type === 'mediaClip' && i.id !== plan.musicClipId)
    .sort((a, b) => a.recordIn - b.recordIn)) {
    const members: Member[] = [];
    let locked = false;
    if (typeof c.linkId === 'string' && c.linkId.length > 0) {
      for (const t of doc.tracks) {
        if (t.id === track.id) continue;
        for (const i of t.items) {
          if (i.type !== 'mediaClip' || i.linkId !== c.linkId || owned.has(i.id)) continue;
          if (t.locked) locked = true;
          members.push({ id: i.id, trackId: t.id, equal: i.recordIn === c.recordIn && i.recordOut === c.recordOut, offset: i.recordIn - c.recordIn, length: lengthOf(i) });
        }
      }
    }
    if (locked) {
      skip(c.id, 'locked');
      continue;
    }
    for (const m of members) owned.add(m.id);
    groups.set(c.id, { clip: c, members });
    clips.push(c);
  }
  if (clips.length === 0) return finish();

  let start = music.recordIn;
  if (mode.startAt === 'firstDownbeat') {
    const bars = mapLines(gridLines(plan.grid, 'bar'));
    if (bars.length > 0) start = bars[0].frame;
  }
  const fitFrames = mode.barsPerClip === 'auto' ? frames : mapLines(gridLines(plan.grid, 'bar')).map((l) => l.frame);
  const after = (cursor: number): number[] => fitFrames.filter((f) => f > cursor);
  const musicEnd = music.recordOut;

  let dropFrame: number | null = null;
  let strongest = -1;
  let order = clips;
  if (mode.onDrop === 'strongestClip' && typeof mode.dropSec === 'number' && mode.rank) {
    const raw = sourceSecToRecordFrame(music, mode.dropSec, fps);
    if (raw !== null) {
      let best = frames[0];
      for (const f of frames) if (Math.abs(f - raw) < Math.abs(best - raw)) best = f;
      dropFrame = best;
      let top = Number.NEGATIVE_INFINITY;
      clips.forEach((c, i) => {
        const r = mode.rank?.[c.id];
        if (typeof r === 'number' && r > top) {
          top = r;
          strongest = i;
        }
      });
      if (strongest < 0) dropFrame = null;
    }
  }
  const endFor = (from: number, length: number): number => {
    const candidates = after(from);
    let end = from + length;
    if (candidates.length > 0) {
      if (mode.barsPerClip === 'auto') {
        const fitting = candidates.filter((f) => f - from <= length);
        end = fitting.length > 0 ? fitting[fitting.length - 1] : candidates[0];
      } else {
        const n = Math.max(1, Math.floor(mode.barsPerClip));
        end = candidates[Math.min(n, candidates.length) - 1];
      }
    }
    return end;
  };
  if (dropFrame !== null && strongest === 0) {
    // The strongest clip is first: lay the others before the drop while they end on or before it,
    // then the strongest clip on the drop, then the rest in order.
    const strong = clips[0];
    const others = clips.slice(1);
    const pre: MediaClip[] = [];
    let at = start;
    let n = 0;
    for (const c of others) {
      const e = endFor(at, lengthOf(c));
      if (e > at && e <= dropFrame) {
        pre.push(c);
        at = e;
        n++;
      } else break;
    }
    order = [...pre, strong, ...others.slice(n)];
    strongest = pre.length;
  }

  // Lay out in one ordered pass. Every movable group is first lifted out of the way to a staging area
  // past anything the layout can reach, so a group's final place is only ever blocked by fixed items
  // (locked groups, other items on the tracks) or groups already placed; it is never left there.
  const globalEnd = current.tracks.reduce((m, t) => t.items.reduce((mm, i) => Math.max(mm, i.recordOut), m), 0);
  let maxGap = 1;
  for (let k = 0; k < fitFrames.length; k++) maxGap = Math.max(maxGap, fitFrames[k] - (k > 0 ? fitFrames[k - 1] : start));
  const perClip = (mode.barsPerClip === 'auto' ? 2 : Math.max(1, Math.floor(mode.barsPerClip)) + 1) * maxGap + 1;
  const itemCount = current.tracks.reduce((n, t) => n + t.items.length, 0);
  let reach = globalEnd + musicEnd + Math.max(0, dropFrame ?? 0) + start + 1 + (itemCount + order.length) * (maxGap + 1);
  const extentOf = (g: Group): { lo: number; hi: number } => {
    let lo = 0;
    let hi = lengthOf(g.clip);
    for (const m of g.members) {
      lo = Math.min(lo, m.offset);
      hi = Math.max(hi, m.offset + m.length);
    }
    return { lo, hi };
  };
  for (const c of order) {
    const e = extentOf(groups.get(c.id) as Group);
    reach += e.hi - e.lo + perClip;
  }
  const stageBase = reach;
  let stageAt = stageBase;
  const staged: MediaClip[] = [];
  const stagedLength = new Map<string, number>();
  for (const c of order) {
    const g = groups.get(c.id) as Group;
    const e = extentOf(g);
    const at = stageAt - e.lo;
    const moved: { id: string; trackId: string; back: number }[] = [];
    const parts = [{ id: c.id, trackId: track.id, offset: 0, from: c.recordIn }, ...g.members.map((m) => ({ id: m.id, trackId: m.trackId, offset: m.offset, from: c.recordIn + m.offset }))];
    let ok = true;
    for (const part of parts) {
      if (run({ type: 'move', itemId: part.id, toTrackId: part.trackId, toRecordIn: at + part.offset })) moved.push({ id: part.id, trackId: part.trackId, back: part.from });
      else {
        ok = false;
        break;
      }
    }
    if (!ok) {
      for (const m of moved.reverse()) run({ type: 'move', itemId: m.id, toTrackId: m.trackId, toRecordIn: m.back });
      skip(c.id, 'invalid');
      continue;
    }
    staged.push(c);
    stagedLength.set(c.id, lengthOf(c));
    stageAt += e.hi - e.lo + perClip;
  }
  if (dropFrame !== null && strongest >= 0) {
    const strongId = order[strongest].id;
    strongest = staged.findIndex((c) => c.id === strongId);
    if (strongest < 0) dropFrame = null;
  }

  /** Earliest start >= `from` (on the grid when possible) at which the group's footprint is free, or null when free at `from`. */
  const blockedUntil = (g: Group, from: number, length: number): number | null => {
    const spans = [{ trackId: track.id, lo: from, hi: from + length }, ...g.members.map((m) => ({ trackId: m.trackId, lo: from + m.offset, hi: from + m.offset + (m.equal ? length : m.length) }))];
    let need = from;
    for (const sp of spans) {
      const shift = sp.lo - from;
      if (sp.lo < 0) need = Math.max(need, -shift);
      const t = current.tracks.find((x) => x.id === sp.trackId);
      for (const i of t?.items ?? []) {
        if (i.recordIn >= stageBase) continue;
        if (i.recordIn < sp.hi && i.recordOut > sp.lo) need = Math.max(need, i.recordOut - shift);
      }
    }
    if (need <= from) return null;
    const onGrid = fitFrames.find((f) => f >= need);
    return onGrid ?? need;
  };
  /** Sets the out-edge delta of the clip and its same-span partners to one common value; returns it. */
  const setDelta = (g: Group, target: number): number => {
    const ids = [g.clip.id, ...g.members.filter((m) => m.equal).map((m) => m.id)];
    const base = lengthOf(g.clip);
    const deltaOf = (id: string): number => {
      const f = locate(current, id);
      return f ? lengthOf(f.item) - base : 0;
    };
    let achieved = target;
    for (const id of ids) {
      const req = target - deltaOf(id);
      if (req !== 0) run({ type: 'trim', itemId: id, edge: 'out', mode: 'ripple', deltaFrames: req });
      const got = deltaOf(id);
      achieved = target >= 0 ? Math.min(achieved, got) : Math.max(achieved, got);
    }
    for (const id of ids) {
      const req = achieved - deltaOf(id);
      if (req !== 0) run({ type: 'trim', itemId: id, edge: 'out', mode: 'ripple', deltaFrames: req });
    }
    return achieved;
  };
  const liftGroup = (g: Group): void => {
    for (const id of [g.clip.id, ...g.members.map((m) => m.id)]) if (locate(current, id)) run({ type: 'lift', itemId: id });
  };

  let cursor = start;
  let capped = false;
  let fitted = 0;
  for (let i = 0; i < staged.length; i++) {
    const clip = staged[i];
    const g = groups.get(clip.id) as Group;
    const length = stagedLength.get(clip.id) ?? lengthOf(clip);
    const drop = (reason: SkipReason): void => {
      liftGroup(g);
      summary.clipsDropped++;
      skip(clip.id, reason);
    };
    let at = cursor;
    let gapReported = false;
    let shortReported = false;
    let placedLength: number | null = null;
    let outcome: SkipReason | null = null;
    for (let attempt = 0; attempt < 1000; attempt++) {
      if (capped || (mode.stopAtMusicEnd && at >= musicEnd)) {
        outcome = 'pastMusicEnd';
        break;
      }
      if (dropFrame !== null && i < strongest && at >= dropFrame) {
        outcome = 'beforeDrop';
        break;
      }
      if (dropFrame !== null && i === strongest && at < dropFrame) {
        if (!gapReported) skip(clip.id, 'gapBeforeDrop');
        gapReported = true;
        at = dropFrame;
      }
      let end = endFor(at, length);
      if (dropFrame !== null && i < strongest && (i === strongest - 1 || end >= dropFrame)) end = dropFrame;
      let capNow = false;
      if (mode.stopAtMusicEnd && end >= musicEnd) {
        end = musicEnd;
        capNow = true;
      }
      let wanted = end - at - length;
      if (wanted > 0) {
        let room = extensionRoom(clip, plan.sourceFrames);
        for (const m of g.members) {
          if (!m.equal) continue;
          const live = clipOf(m.id);
          if (live) room = Math.min(room, extensionRoom(live.clip, plan.sourceFrames));
        }
        if (room < wanted) {
          wanted = room;
          if (!shortReported) skip(clip.id, 'shortClip');
          shortReported = true;
        }
      }
      const achieved = setDelta(g, wanted);
      if (wanted > 0 && achieved < wanted && !shortReported) {
        skip(clip.id, 'shortClip');
        shortReported = true;
      }
      const finalLength = length + achieved;
      const next = blockedUntil(g, at, finalLength);
      if (next === null) {
        placedLength = finalLength;
        if (capNow) capped = true;
        break;
      }
      at = Math.max(next, at + 1);
    }
    if (outcome !== null || placedLength === null) {
      drop(outcome ?? 'invalid');
      continue;
    }
    const finalAt = at;
    const parts = [{ id: clip.id, trackId: track.id, offset: 0 }, ...g.members.map((m) => ({ id: m.id, trackId: m.trackId, offset: m.offset }))];
    let ok = true;
    for (const part of parts) if (!run({ type: 'move', itemId: part.id, toTrackId: part.trackId, toRecordIn: finalAt + part.offset })) ok = false;
    if (!ok) {
      liftGroup(g);
      summary.clipsDropped++;
      skip(clip.id, 'invalid');
      continue;
    }
    fitted++;
    cursor = finalAt + placedLength;
  }
  summary.clipsFitted = fitted;
  return finish();
}
