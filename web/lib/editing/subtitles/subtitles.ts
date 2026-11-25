/**
 * Subtitles (P6): captions generated from the transcript, edited as timeline items.
 *
 * A caption is a `Caption` item on a video track — the same item the renderer burns into the
 * picture and the same item `GET …/export?format=srt|vtt` writes as a sidecar. There is no second
 * subtitle model: generating captions inserts items, splitting one is a `blade` plus two text
 * writes, merging two is a `lift` plus an `overwrite`, and every edit is an ordinary undo step.
 *
 * ## Burn-in versus sidecar
 *
 * The render draws every ENABLED caption on an ENABLED track. The SRT/VTT writers export every
 * enabled caption on ANY track. So "sidecar only" is: keep the captions, disable their track —
 * the render skips the track, the sidecar still carries the text.
 */

import { apply, type EditOp } from '../engine';
import type { Caption, CaptionLook, EditTimeline, TextPosition, Track } from '../types';

/** A word placed on the timeline, in record frames. */
export interface TimedWord {
  startFrame: number;
  endFrame: number;
  text: string;
}

export interface CaptionCue {
  startFrame: number;
  endFrame: number;
  /** The cue text, already wrapped into lines with `\n`. */
  text: string;
}

export interface BuildCuesOptions {
  /** Characters per line before wrapping. Default 42 (the common broadcast ceiling). */
  maxCharsPerLine?: number;
  /** Lines per cue. Default 2. */
  maxLines?: number;
  /** A cue never runs longer than this. Default 7 s worth of frames at the given rate. */
  maxDurationFrames?: number;
  /** A cue never shows shorter than this; it is extended up to the next cue's start. Default 1 s. */
  minDurationFrames?: number;
  /** Frames left between two cues so they read as separate. Default 2. */
  gapFrames?: number;
}

/** Greedy word wrap at `maxChars`; a single word longer than the line is kept whole on its own line. */
export function wrapLines(text: string, maxChars: number): string[] {
  const words = text.split(/\s+/).filter((w) => w.length > 0);
  const lines: string[] = [];
  let line = '';
  for (const word of words) {
    if (line.length === 0) line = word;
    else if (line.length + 1 + word.length <= maxChars) line += ` ${word}`;
    else {
      lines.push(line);
      line = word;
    }
  }
  if (line.length > 0) lines.push(line);
  return lines;
}

/** Balances a two-line cue so the lines are of similar length (the upper one shorter, as readers expect). */
function balance(words: string[], maxChars: number, maxLines: number): string {
  const lines = wrapLines(words.join(' '), maxChars);
  if (lines.length !== 2 || maxLines < 2) return lines.join('\n');
  // Try every split point and keep the one with the smallest length difference that still fits.
  let best = lines;
  let bestDiff = Math.abs(lines[0].length - lines[1].length);
  for (let i = 1; i < words.length; i++) {
    const a = words.slice(0, i).join(' ');
    const b = words.slice(i).join(' ');
    if (a.length > maxChars || b.length > maxChars) continue;
    const diff = Math.abs(a.length - b.length) + (a.length > b.length ? 0.5 : 0);
    if (diff < bestDiff) {
      best = [a, b];
      bestDiff = diff;
    }
  }
  return best.join('\n');
}

const endsSentence = (word: string): boolean => /[.!?…]["')\]]?$/.test(word);
const endsClause = (word: string): boolean => /[,;:]["')\]]?$/.test(word);

/**
 * Packs timed words into cues: a cue closes when the next word would overflow the character
 * budget, when its duration would exceed the ceiling, after a sentence end, or across a pause
 * longer than a second. Cues never overlap and keep `gapFrames` apart.
 */
export function buildCues(words: readonly TimedWord[], fps: number, options: BuildCuesOptions = {}): CaptionCue[] {
  const maxChars = Math.max(8, options.maxCharsPerLine ?? 42);
  const maxLines = Math.max(1, options.maxLines ?? 2);
  const budget = maxChars * maxLines;
  const maxDuration = Math.max(1, options.maxDurationFrames ?? Math.round(7 * fps));
  const minDuration = Math.max(1, options.minDurationFrames ?? Math.round(fps));
  const gap = Math.max(0, options.gapFrames ?? 2);
  const pauseFrames = Math.round(fps);

  const sorted = words.filter((w) => w.text.trim().length > 0 && w.endFrame >= w.startFrame).sort((a, b) => a.startFrame - b.startFrame);
  const cues: CaptionCue[] = [];
  let group: TimedWord[] = [];

  const flush = () => {
    if (group.length === 0) return;
    cues.push({
      startFrame: group[0].startFrame,
      endFrame: Math.max(group[group.length - 1].endFrame, group[0].startFrame + 1),
      text: balance(group.map((w) => w.text.trim()), maxChars, maxLines),
    });
    group = [];
  };

  for (const word of sorted) {
    if (group.length > 0) {
      const chars = group.reduce((n, w) => n + w.text.trim().length + 1, 0) + word.text.trim().length;
      const lines = wrapLines([...group, word].map((w) => w.text.trim()).join(' '), maxChars).length;
      const duration = word.endFrame - group[0].startFrame;
      const last = group[group.length - 1];
      const pause = word.startFrame - last.endFrame;
      const sentence = endsSentence(last.text) && group.length >= 2;
      const clauseAndLong = endsClause(last.text) && chars > budget * 0.75;
      if (chars > budget || lines > maxLines || duration > maxDuration || pause > pauseFrames || sentence || clauseAndLong) flush();
    }
    group.push(word);
  }
  flush();

  // Timing clean-up: minimum display time, then no overlaps and a reading gap.
  for (let i = 0; i < cues.length; i++) {
    const cue = cues[i];
    if (cue.endFrame - cue.startFrame < minDuration) cue.endFrame = cue.startFrame + minDuration;
    const next = cues[i + 1];
    if (next && cue.endFrame > next.startFrame - gap) cue.endFrame = Math.max(cue.startFrame + 1, next.startFrame - gap);
  }
  return cues;
}

export interface GenerateCaptionsOptions {
  cues: readonly CaptionCue[];
  /**
   * The track to put the captions on; `null` reuses the captions track (a video track holding only
   * captions, preferring one named `trackName`) or, when there is none, adds one named `trackName`.
   */
  trackId: string | null;
  trackName?: string;
  language?: string;
  /** R09.6: the burn-in look and pad position given to every new caption (omitted = the default look). */
  look?: CaptionLook | null;
  position?: TextPosition | null;
  newItemId: () => string;
  newTrackId: () => string;
}

export interface GenerateCaptionsPlan {
  ops: EditOp[];
  preview: EditTimeline;
  trackId: string | null;
  inserted: number;
  /** Cues skipped because the target track already has an item there. */
  skipped: number;
}

/**
 * The track generated captions go on by default: a video track holding captions and nothing else,
 * preferring one named `name`. Null when there is none.
 */
export function findCaptionTrack(doc: EditTimeline, name = 'Subtitles'): Track | null {
  const candidates = doc.tracks.filter((t) => t.kind === 'video' && t.items.length > 0 && t.items.every((i) => i.type === 'caption'));
  return candidates.find((t) => t.name === name) ?? candidates[0] ?? null;
}

/**
 * Inserts the cues as `Caption` items. On a fresh track every cue lands; on an existing track a cue
 * overlapping an item is skipped and counted, never forced. Generating again with no track chosen
 * targets the existing captions track ({@link findCaptionTrack}), so the second run skips the cues
 * already there instead of cloning the whole track. One undo step through `dispatchBatch`.
 */
export function compileGenerateCaptions(doc: EditTimeline, options: GenerateCaptionsOptions): GenerateCaptionsPlan {
  const ops: EditOp[] = [];
  let current = doc;
  const run = (op: EditOp) => {
    current = apply(current, op).doc;
    ops.push(op);
  };

  let trackId = options.trackId ?? findCaptionTrack(doc, options.trackName ?? 'Subtitles')?.id ?? null;
  if (trackId === null) {
    trackId = options.newTrackId();
    const videoTracks = doc.tracks.filter((t) => t.kind === 'video').length;
    const track: Track = { id: trackId, kind: 'video', name: options.trackName ?? 'Subtitles', height: 40, locked: false, enabled: true, mute: false, solo: false, items: [] };
    run({ type: 'addTrack', index: videoTracks, track });
  } else if (!doc.tracks.some((t) => t.id === trackId)) {
    return { ops: [], preview: doc, trackId: null, inserted: 0, skipped: options.cues.length };
  }

  let inserted = 0;
  let skipped = 0;
  for (const cue of [...options.cues].sort((a, b) => a.startFrame - b.startFrame)) {
    const items = (current.tracks.find((t) => t.id === trackId) as Track).items;
    const overlaps = items.some((i) => i.recordIn < cue.endFrame && i.recordOut > cue.startFrame);
    if (overlaps || cue.endFrame <= cue.startFrame) {
      skipped++;
      continue;
    }
    const caption: Caption = { id: options.newItemId(), type: 'caption', name: cue.text.split('\n')[0].slice(0, 40), recordIn: cue.startFrame, recordOut: cue.endFrame, text: cue.text, language: options.language ?? 'en', enabled: true, provenance: null, ...(options.look ? { look: options.look } : {}), ...(options.position ? { position: options.position } : {}) };
    run({ type: 'placeOnTop', trackId, item: caption });
    inserted++;
  }
  return { ops, preview: current, trackId, inserted, skipped };
}

/** Splits a caption's text at the word boundary nearest the given frame's share of its span. */
export function splitTextAtFrame(text: string, recordIn: number, recordOut: number, atFrame: number): [string, string] {
  const words = text.split(/\s+/).filter((w) => w.length > 0);
  if (words.length < 2) return [text, ''];
  const share = (atFrame - recordIn) / Math.max(1, recordOut - recordIn);
  const index = Math.min(words.length - 1, Math.max(1, Math.round(share * words.length)));
  return [words.slice(0, index).join(' '), words.slice(index).join(' ')];
}

/**
 * Splits a caption at `atFrame` (strictly inside its span): a `blade` plus a text write on each
 * half. The text divides at the word boundary matching the frame's share of the span unless
 * `atWordIndex` is given.
 */
export function compileSplitCaption(doc: EditTimeline, captionId: string, atFrame: number, newItemId: string, atWordIndex?: number): EditOp[] {
  const caption = doc.tracks.flatMap((t) => t.items).find((i): i is Caption => i.id === captionId && i.type === 'caption');
  if (!caption || atFrame <= caption.recordIn || atFrame >= caption.recordOut) return [];
  let left: string;
  let right: string;
  if (atWordIndex !== undefined) {
    const words = caption.text.split(/\s+/).filter((w) => w.length > 0);
    const index = Math.min(words.length - 1, Math.max(1, atWordIndex));
    left = words.slice(0, index).join(' ');
    right = words.slice(index).join(' ');
  } else {
    [left, right] = splitTextAtFrame(caption.text, caption.recordIn, caption.recordOut, atFrame);
  }
  const ops: EditOp[] = [{ type: 'blade', itemId: captionId, atFrame, newItemId }];
  ops.push({ type: 'setProperty', itemId: captionId, key: 'text', value: left });
  ops.push({ type: 'setProperty', itemId: newItemId, key: 'text', value: right });
  return ops;
}

/**
 * Merges a caption with the caption that follows it on the same track: the follower is lifted and
 * the first is re-spanned to the follower's end with the two texts joined by a space.
 */
export function compileMergeCaptionWithNext(doc: EditTimeline, captionId: string): EditOp[] {
  for (const track of doc.tracks) {
    const index = track.items.findIndex((i) => i.id === captionId);
    if (index < 0) continue;
    const first = track.items[index];
    const next = track.items[index + 1];
    if (!first || first.type !== 'caption' || !next || next.type !== 'caption') return [];
    const merged: Caption = { ...first, recordOut: next.recordOut, text: `${first.text.trim()} ${next.text.trim()}`.trim() };
    return [
      { type: 'lift', itemId: next.id },
      { type: 'overwrite', itemId: first.id, item: merged },
    ];
  }
  return [];
}

/** Every caption of the document in record order, with its track. */
export function listCaptions(doc: EditTimeline): Array<{ caption: Caption; track: Track }> {
  const out: Array<{ caption: Caption; track: Track }> = [];
  for (const track of doc.tracks) for (const item of track.items) if (item.type === 'caption') out.push({ caption: item, track });
  return out.sort((a, b) => a.caption.recordIn - b.caption.recordIn);
}

// ─────────────────────────────────────────────────────────────────────────────
// R09.6: looks per aspect, manual add, timing nudge
// ─────────────────────────────────────────────────────────────────────────────

export const CAPTION_LOOK_LABELS: Record<CaptionLook, string> = {
  clean: 'Clean',
  bold: 'Bold',
  boxed: 'On a box',
  outline: 'Outlined',
  spotlight: 'Yellow',
};

export const CAPTION_LOOK_ORDER: readonly CaptionLook[] = ['clean', 'bold', 'boxed', 'outline', 'spotlight'];

export type AspectKind = 'vertical' | 'square' | 'wide';

/** The shape of the picture, which decides the default look. */
export function aspectKind(width: number, height: number): AspectKind {
  if (height > width * 1.05) return 'vertical';
  if (Math.abs(width - height) <= Math.min(width, height) * 0.05) return 'square';
  return 'wide';
}

/** The look a new caption gets per picture shape: big and bold for phones, clean for wide video. */
export function defaultCaptionStyle(width: number, height: number): { look: CaptionLook; position: TextPosition } {
  const kind = aspectKind(width, height);
  if (kind === 'vertical') return { look: 'bold', position: 'bottomCenter' };
  if (kind === 'square') return { look: 'boxed', position: 'bottomCenter' };
  return { look: 'clean', position: 'bottomCenter' };
}

export interface AddCaptionOptions {
  atFrame: number;
  /** Default 3 seconds. */
  durationFrames?: number;
  text: string;
  /** The track; null reuses the captions track or adds one. */
  trackId: string | null;
  language?: string;
  look?: CaptionLook | null;
  position?: TextPosition | null;
  newItemId: () => string;
  newTrackId: () => string;
}

/**
 * Adds one caption at a frame (the playhead). When something already sits there the caption is
 * shortened to the free space before the next item; with no room at all nothing is added and the
 * reason is said in plain words.
 */
export function compileAddCaptionAt(doc: EditTimeline, options: AddCaptionOptions): { ops: EditOp[]; refusal: string | null; captionId: string | null } {
  const text = options.text.replace(/\r\n?/g, '\n').trim();
  if (text.length === 0) return { ops: [], refusal: 'Type the caption text first.', captionId: null };
  const fps = doc.settings.rate.den === 0 ? 30 : doc.settings.rate.num / doc.settings.rate.den;
  const wanted = Math.max(1, options.durationFrames ?? Math.round(3 * fps));
  const trackId = options.trackId ?? findCaptionTrack(doc)?.id ?? null;
  const track = trackId ? doc.tracks.find((t) => t.id === trackId) : undefined;
  let end = options.atFrame + wanted;
  if (track) {
    if (track.items.some((i) => i.recordIn <= options.atFrame && i.recordOut > options.atFrame)) return { ops: [], refusal: 'There is already a caption here. Move the playhead to a free spot.', captionId: null };
    const next = track.items.filter((i) => i.recordIn >= options.atFrame).sort((a, b) => a.recordIn - b.recordIn)[0];
    if (next) end = Math.min(end, next.recordIn);
    if (end <= options.atFrame) return { ops: [], refusal: 'There is no room for a caption here. Move the playhead to a free spot.', captionId: null };
  }
  const id = options.newItemId();
  const caption: Caption = { id, type: 'caption', name: text.split('\n')[0].slice(0, 40), recordIn: options.atFrame, recordOut: end, text, language: options.language ?? 'en', enabled: true, provenance: null, ...(options.look ? { look: options.look } : {}), ...(options.position ? { position: options.position } : {}) };
  const ops: EditOp[] = [];
  let target = track?.id ?? null;
  if (target === null) {
    target = options.newTrackId();
    const videoTracks = doc.tracks.filter((t) => t.kind === 'video').length;
    ops.push({ type: 'addTrack', index: videoTracks, track: { id: target, kind: 'video', name: 'Subtitles', height: 40, locked: false, enabled: true, mute: false, solo: false, items: [] } });
  }
  ops.push({ type: 'placeOnTop', trackId: target, item: caption });
  return { ops, refusal: null, captionId: id };
}

/**
 * Moves a caption's start and/or end by whole frames (a timing nudge). `edge` picks what moves: the
 * start, the end, or the whole caption. Refuses a caption shorter than one frame, a negative start
 * or an overlap with its neighbour, in plain words.
 */
export function compileNudgeCaption(doc: EditTimeline, captionId: string, edge: 'start' | 'end' | 'both', deltaFrames: number): { ops: EditOp[]; refusal: string | null } {
  for (const track of doc.tracks) {
    const index = track.items.findIndex((i) => i.id === captionId);
    if (index < 0) continue;
    const caption = track.items[index];
    if (caption.type !== 'caption') return { ops: [], refusal: 'That is not a caption.' };
    const recordIn = caption.recordIn + (edge === 'end' ? 0 : deltaFrames);
    const recordOut = caption.recordOut + (edge === 'start' ? 0 : deltaFrames);
    if (recordIn < 0) return { ops: [], refusal: 'A caption cannot start before the beginning.' };
    if (recordOut <= recordIn) return { ops: [], refusal: 'A caption needs to stay on screen for at least one frame.' };
    const prev = track.items[index - 1];
    const next = track.items[index + 1];
    if ((prev && prev.recordOut > recordIn) || (next && next.recordIn < recordOut)) return { ops: [], refusal: 'That would run into the neighbouring caption. Nudge that one first.' };
    return { ops: [{ type: 'overwrite', itemId: captionId, item: { ...caption, recordIn, recordOut } }], refusal: null };
  }
  return { ops: [], refusal: 'That caption is no longer on the timeline.' };
}

// ─────────────────────────────────────────────────────────────────────────────
// R12.9: SRT / WebVTT import
// ─────────────────────────────────────────────────────────────────────────────

export interface ImportedCue {
  startSec: number;
  endSec: number;
  text: string;
}

export interface ParsedSubtitles {
  format: 'srt' | 'vtt';
  cues: ImportedCue[];
  /** Plain-language notes: what was skipped and why. */
  problems: string[];
}

export const MAX_IMPORT_BYTES = 2 * 1024 * 1024;
export const MAX_IMPORT_CUES = 5000;

/** Removes markup tags until none are left, so nested fragments like `<scr<b>ipt>` cannot reassemble into a tag. */
function stripTags(text: string): string {
  let out = text;
  for (;;) {
    const next = out.replace(/<[^<>]+>/g, '');
    if (next === out) return out;
    out = next;
  }
}

const TIMING = /^\s*((?:\d{1,3}:)?\d{1,2}:\d{2}[,.]\d{1,3})\s*-->\s*((?:\d{1,3}:)?\d{1,2}:\d{2}[,.]\d{1,3})/;

function timestampSec(raw: string): number {
  const parts = raw.replace(',', '.').split(':');
  const seconds = Number(parts[parts.length - 1]);
  const minutes = Number(parts[parts.length - 2] ?? 0);
  const hours = Number(parts.length > 2 ? parts[0] : 0);
  return hours * 3600 + minutes * 60 + seconds;
}

const decodeEntities = (t: string): string => t.replace(/&nbsp;/g, ' ').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&');

/** Reads SRT or WebVTT text into cues. Never throws: anything unreadable becomes a plain-language problem. */
export function parseSubtitleFile(raw: string): ParsedSubtitles {
  const problems: string[] = [];
  const text = raw.replace(/^\uFEFF/, '').replace(/\r\n?/g, '\n');
  const format: 'srt' | 'vtt' = /^WEBVTT/.test(text.trimStart()) ? 'vtt' : 'srt';
  const blocks = text.split(/\n{2,}/);
  const cues: ImportedCue[] = [];
  let seen = 0;
  for (const block of blocks) {
    const lines = block.split('\n').filter((l, i, all) => !(i === all.length - 1 && l.trim() === ''));
    if (lines.length === 0 || /^(WEBVTT|NOTE|STYLE|REGION)\b/.test(lines[0].trim())) continue;
    const timingIndex = lines.findIndex((l) => l.includes('-->'));
    if (timingIndex < 0) continue;
    seen++;
    const match = TIMING.exec(lines[timingIndex]);
    if (!match) {
      problems.push(`Cue ${seen} has a time range that could not be read and was skipped.`);
      continue;
    }
    const startSec = timestampSec(match[1]);
    const endSec = timestampSec(match[2]);
    const body = decodeEntities(stripTags(lines.slice(timingIndex + 1).join('\n').replace(/\{\\[^}]*\}/g, ''))).split('\n').map((l) => l.trim()).filter((l) => l.length > 0).join('\n');
    if (!Number.isFinite(startSec) || !Number.isFinite(endSec) || endSec <= startSec) {
      problems.push(`Cue ${seen} ends before it starts and was skipped.`);
      continue;
    }
    if (body.length === 0) {
      problems.push(`Cue ${seen} has no text and was skipped.`);
      continue;
    }
    cues.push({ startSec, endSec, text: body });
  }
  if (cues.length === 0 && problems.length === 0) problems.push('No captions were found. This does not look like an SRT or WebVTT file.');
  if (cues.length > MAX_IMPORT_CUES) {
    problems.push(`The file has ${cues.length} captions; only the first ${MAX_IMPORT_CUES} are used.`);
    cues.length = MAX_IMPORT_CUES;
  }
  cues.sort((a, b) => a.startSec - b.startSec);
  return { format, cues, problems };
}

export interface ImportCuesOptions {
  cues: readonly ImportedCue[];
  /** The track; null reuses the captions track or adds one named Subtitles. */
  trackId: string | null;
  mode: 'replace' | 'append';
  /** Shift every cue by this many seconds (negative moves earlier). */
  offsetSec?: number;
  language?: string;
  look?: CaptionLook | null;
  position?: TextPosition | null;
  newItemId: () => string;
  newTrackId: () => string;
}

export interface ImportCuesPlan extends GenerateCaptionsPlan {
  /** Captions removed first (replace mode). */
  removed: number;
}

/** Turns imported cues into captions on the timeline's clock: replace drops the target track's captions first. One undo step. */
export function compileImportCues(doc: EditTimeline, options: ImportCuesOptions): ImportCuesPlan {
  const fps = doc.settings.rate.den === 0 ? 30 : doc.settings.rate.num / doc.settings.rate.den;
  const offset = options.offsetSec ?? 0;
  const frames: CaptionCue[] = options.cues
    .map((c) => ({ startFrame: Math.round((c.startSec + offset) * fps), endFrame: Math.round((c.endSec + offset) * fps), text: c.text }))
    .filter((c) => c.endFrame > 0)
    .map((c) => ({ ...c, startFrame: Math.max(0, c.startFrame) }))
    .filter((c) => c.endFrame > c.startFrame);
  const ops: EditOp[] = [];
  let current = doc;
  let removed = 0;
  const targetId = options.trackId ?? findCaptionTrack(doc)?.id ?? null;
  if (options.mode === 'replace' && targetId) {
    const track = doc.tracks.find((t) => t.id === targetId);
    for (const item of track?.items ?? []) {
      if (item.type !== 'caption') continue;
      const op: EditOp = { type: 'lift', itemId: item.id };
      current = apply(current, op).doc;
      ops.push(op);
      removed++;
    }
  }
  const plan = compileGenerateCaptions(current, { cues: frames, trackId: targetId, trackName: 'Subtitles', language: options.language, look: options.look, position: options.position, newItemId: options.newItemId, newTrackId: options.newTrackId });
  return { ...plan, ops: [...ops, ...plan.ops], removed };
}
