/**
 * P6 workflow speed — the pure modules: the transcript as a ripple engine (word deletion is an
 * exact frame cut, fillers are individually rejectable, paragraphs move as blades+extracts+inserts),
 * subtitle cue packing and split/merge, the cut helpers (smart insert, review speed, boring shots),
 * take stacks, the timeline event diff, and still/clip file naming.
 */

import { describe, expect, it } from 'vitest';
import { apply } from '../engine';
import { doc, media, caption, track, TRACKS } from './fixtures';
import {
  compileMoveSpan,
  compileRippleCut,
  findFillerCandidates,
  readTranscript,
  wordsToRecordSpans,
  wordIsOnTrack,
  type TranscriptWord,
} from '../transcript/transcript';
import { buildCues, compileGenerateCaptions, compileMergeCaptionWithNext, compileSplitCaption, listCaptions, wrapLines } from '../subtitles/subtitles';
import { compileSmartInsert, nearestEditPoint, overviewPixelsPerFrame, rankBoringShots, readShots, reviewPlan, reviewSpeed, reviewStopAt } from '../cut/cut';
import { buildTakeStacks, compileSwapTake, readDuplicateGroups } from '../takes/take-stacks';
import { diffTimelines, summarizeDiff } from '../compare/diff';
import { clipExportFileName, clipExportRange, singleSelectedItem, stillFileName } from '../stills/stills';
import type { EditTimeline, MediaClip } from '../types';

const FPS = 30;

/** One clip covering source 0..300 frames at record 0..300, a second clip of another take after it. */
const interviewDoc = (): EditTimeline =>
  doc([track(TRACKS.v1, 'video', [media('c1', 0, 300, 0), media('c2', 300, 480, 600)])]);

/** A word-timed transcript: "so um the the quick brown fox. jumps over" over source seconds 0..9. */
const artifact = {
  shots: [
    { id: 's0', startSec: 0, endSec: 10, visual: { motionMean: 0.01, motionClass: 'Static', duplicateGroupId: 'k0', isBestTake: false } },
    { id: 's1', startSec: 20, endSec: 26, visual: { motionMean: 0.02, motionClass: 'Static', duplicateGroupId: 'k0', isBestTake: true } },
    { id: 's2', startSec: 26, endSec: 28, visual: { motionMean: 0.5, motionClass: 'Fast' } },
    { id: 's3', startSec: 28, endSec: 40 },
  ],
  duplicateGroups: [{ id: 'k0', shotIds: ['s0', 's1'], bestShotId: 's1', meanSimilarity: 0.93 }],
  segments: [
    { id: 't0', startSec: 0, endSec: 5, text: 'so um the the quick brown fox.' },
    { id: 't1', startSec: 7, endSec: 9, text: 'jumps over' },
  ],
  words: [
    { id: 'w0', startSec: 0.0, endSec: 0.5, text: 'so' },
    { id: 'w1', startSec: 0.5, endSec: 1.0, text: 'um' },
    { id: 'w2', startSec: 1.0, endSec: 1.5, text: 'the' },
    { id: 'w3', startSec: 1.6, endSec: 2.0, text: 'the' },
    { id: 'w4', startSec: 2.0, endSec: 3.0, text: 'quick' },
    { id: 'w5', startSec: 3.0, endSec: 4.0, text: 'brown' },
    { id: 'w6', startSec: 4.0, endSec: 5.0, text: 'fox.' },
    { id: 'w7', startSec: 7.0, endSec: 8.0, text: 'jumps' },
    { id: 'w8', startSec: 8.0, endSec: 9.0, text: 'over' },
  ],
};

describe('transcript: reading', () => {
  it('reads words into their segments and groups paragraphs by pause', () => {
    const t = readTranscript(artifact);
    expect(t.words.map((w) => w.text)).toEqual(['so', 'um', 'the', 'the', 'quick', 'brown', 'fox.', 'jumps', 'over']);
    expect(t.words[0].segmentId).toBe('t0');
    expect(t.words[7].segmentId).toBe('t1');
    expect(t.hasEstimatedWords).toBe(false);
    expect(t.paragraphs).toHaveLength(2);
    expect(t.paragraphs[0].words).toHaveLength(7);
  });

  it('estimates word timing from a segment that carries no words, and says so', () => {
    const t = readTranscript({ segments: [{ id: 't0', startSec: 10, endSec: 12, text: 'two words' }] });
    expect(t.hasEstimatedWords).toBe(true);
    expect(t.words).toHaveLength(2);
    expect(t.words[0].estimated).toBe(true);
    expect(t.words[0].startSec).toBe(10);
    expect(t.words[1].endSec).toBe(12);
    expect(t.words[0].endSec).toBeCloseTo(t.words[1].startSec, 9);
  });

  it('offers nothing for an unreadable artifact', () => {
    expect(readTranscript(null).words).toEqual([]);
    expect(readTranscript({ segments: 'nope' }).paragraphs).toEqual([]);
  });
});

describe('transcript: ripple cut', () => {
  it('deleting a word range removes exactly its frames and ripples the rest (one program)', () => {
    const d = interviewDoc();
    const t = readTranscript(artifact);
    // "the the quick" = source 1.0s..3.0s = frames 30..90 on clip c1.
    const selected = t.words.slice(2, 5);
    const spans = wordsToRecordSpans(d, TRACKS.v1, selected, FPS);
    expect(spans).toEqual([{ startFrame: 30, endFrame: 90 }]);

    let n = 0;
    const plan = compileRippleCut(d, { trackId: TRACKS.v1, spans, newItemId: () => `n${++n}` });
    expect(plan.cuts).toBe(1);
    expect(plan.removedFrames).toBe(60);
    const v1 = plan.preview.tracks[0].items as MediaClip[];
    // c1 head keeps 0..30, the tail (source 90..300) follows at 30..240, c2 ripples to 240..420.
    expect(v1.map((c) => [c.recordIn, c.recordOut, c.sourceIn])).toEqual([[0, 30, 0], [30, 240, 90], [240, 420, 600]]);
    // Every op re-applies from the original document, so the plan is replayable as a batch.
    let replay = d;
    for (const op of plan.ops) replay = apply(replay, op).doc;
    expect(replay).toEqual(plan.preview);
    // The deleted words are no longer on the track; the kept ones still are.
    expect(wordIsOnTrack(plan.preview, TRACKS.v1, t.words[3], FPS)).toBe(false);
    expect(wordIsOnTrack(plan.preview, TRACKS.v1, t.words[5], FPS)).toBe(true);
  });

  it('a selection with a hole cuts two pieces, rightmost first', () => {
    const d = interviewDoc();
    const t = readTranscript(artifact);
    const spans = wordsToRecordSpans(d, TRACKS.v1, [t.words[1], t.words[5]], FPS);
    expect(spans).toEqual([{ startFrame: 15, endFrame: 30 }, { startFrame: 90, endFrame: 120 }]);
    const plan = compileRippleCut(d, { trackId: TRACKS.v1, spans, newItemId: (() => { let i = 0; return () => `x${++i}`; })() });
    expect(plan.cuts).toBe(2);
    expect(plan.removedFrames).toBe(45);
  });

  it('ignores words whose source is not on the track', () => {
    const d = doc([track(TRACKS.v1, 'video', [media('c1', 0, 60, 3000)])]);
    const t = readTranscript(artifact);
    expect(wordsToRecordSpans(d, TRACKS.v1, t.words, FPS)).toEqual([]);
  });
});

describe('transcript: fillers', () => {
  it('finds fillers and immediate repeats as separately rejectable candidates', () => {
    const t = readTranscript(artifact);
    const found = findFillerCandidates(t.words);
    expect(found.map((c) => [c.kind, c.text])).toEqual([['filler', 'um'], ['repeat', 'the the']]);
    // The repeat removes the FIRST utterance.
    expect(found[1].words[0].id).toBe('w2');
    expect(findFillerCandidates(t.words, { includeRepeats: false })).toHaveLength(1);
    expect(findFillerCandidates(t.words, { fillers: ['fox'] }).map((c) => c.text)).toEqual(['the the', 'fox.']);
  });

  it('does not flag a repeat across a long pause or of one letter', () => {
    const words: TranscriptWord[] = [
      { id: 'a', startSec: 0, endSec: 0.2, text: 'I', sourceIndex: 0, segmentId: null, estimated: false },
      { id: 'b', startSec: 0.2, endSec: 0.4, text: 'I', sourceIndex: 0, segmentId: null, estimated: false },
      { id: 'c', startSec: 1, endSec: 1.2, text: 'go', sourceIndex: 0, segmentId: null, estimated: false },
      { id: 'd', startSec: 3, endSec: 3.2, text: 'go', sourceIndex: 0, segmentId: null, estimated: false },
    ];
    expect(findFillerCandidates(words)).toEqual([]);
  });
});

describe('transcript: paragraph move', () => {
  it('moves a span after another by blading, extracting and inserting, exactly', () => {
    const d = interviewDoc();
    let n = 0;
    // Move frames 30..90 of c1 to the end (toFrame 480).
    const plan = compileMoveSpan(d, { trackId: TRACKS.v1, from: { startFrame: 30, endFrame: 90 }, toFrame: 480, newItemId: () => `n${++n}` });
    expect(plan.refusal).toBeNull();
    const items = plan.preview.tracks[0].items as MediaClip[];
    expect(items.map((c) => [c.recordIn, c.recordOut, c.sourceIn])).toEqual([[0, 30, 0], [30, 240, 90], [240, 420, 600], [420, 480, 30]]);
    expect(plan.landedAt).toBe(420);
    let replay = d;
    for (const op of plan.ops) replay = apply(replay, op).doc;
    expect(replay).toEqual(plan.preview);
  });

  it('moves a later span before an earlier one and refuses a target inside the span', () => {
    const d = interviewDoc();
    let n = 0;
    const plan = compileMoveSpan(d, { trackId: TRACKS.v1, from: { startFrame: 300, endFrame: 480 }, toFrame: 60, newItemId: () => `n${++n}` });
    expect(plan.refusal).toBeNull();
    const items = plan.preview.tracks[0].items as MediaClip[];
    expect(items.map((c) => [c.recordIn, c.recordOut, c.sourceIn])).toEqual([[0, 60, 0], [60, 240, 600], [240, 480, 60]]);
    expect(compileMoveSpan(d, { trackId: TRACKS.v1, from: { startFrame: 30, endFrame: 90 }, toFrame: 60, newItemId: () => 'z' }).refusal).toMatch(/inside/);
    expect(compileMoveSpan(d, { trackId: TRACKS.v1, from: { startFrame: 30, endFrame: 90 }, toFrame: 90, newItemId: () => 'z' }).refusal).toMatch(/already/);
  });
});

describe('subtitles', () => {
  it('wraps and balances lines, packs cues by budget, sentence and pause', () => {
    expect(wrapLines('a quick brown fox jumps over the lazy dog', 12)).toEqual(['a quick', 'brown fox', 'jumps over', 'the lazy dog']);
    const words = [
      { startFrame: 0, endFrame: 15, text: 'So' },
      { startFrame: 15, endFrame: 30, text: 'this' },
      { startFrame: 30, endFrame: 45, text: 'is' },
      { startFrame: 45, endFrame: 60, text: 'it.' },
      { startFrame: 61, endFrame: 90, text: 'Next' },
      { startFrame: 90, endFrame: 120, text: 'thought' },
      { startFrame: 200, endFrame: 230, text: 'after' },
      { startFrame: 230, endFrame: 260, text: 'a pause' },
    ];
    const cues = buildCues(words, FPS, { maxCharsPerLine: 20, maxLines: 2 });
    expect(cues.map((c) => c.text)).toEqual(['So this is it.', 'Next thought', 'after a pause']);
    expect(cues[0]).toMatchObject({ startFrame: 0, endFrame: 59 });
    expect(cues[1].startFrame).toBe(61);
    // The one-second minimum extends a short cue; the gap before the next cue is kept.
    expect(cues[1].endFrame).toBe(120);
    expect(cues[2].endFrame).toBe(260);
    // A long run is split by the character budget and balanced across two lines.
    const long = Array.from({ length: 12 }, (_, i) => ({ startFrame: i * 10, endFrame: i * 10 + 10, text: `word${i}` }));
    const packed = buildCues(long, FPS, { maxCharsPerLine: 20, maxLines: 2 });
    expect(packed.length).toBeGreaterThan(1);
    for (const cue of packed) for (const line of cue.text.split('\n')) expect(line.length).toBeLessThanOrEqual(20);
  });

  it('generates captions on a new track, splits one at a frame and merges two back', () => {
    const d = interviewDoc();
    let n = 0;
    const plan = compileGenerateCaptions(d, {
      cues: [{ startFrame: 0, endFrame: 60, text: 'So this is it.' }, { startFrame: 61, endFrame: 120, text: 'Next thought' }],
      trackId: null,
      newItemId: () => `cap${++n}`,
      newTrackId: () => 'subs',
    });
    expect(plan.inserted).toBe(2);
    expect(plan.skipped).toBe(0);
    expect(plan.preview.tracks.map((t) => t.id)).toEqual([TRACKS.v1, 'subs']);
    expect(listCaptions(plan.preview).map((c) => c.caption.text)).toEqual(['So this is it.', 'Next thought']);

    const split = compileSplitCaption(plan.preview, 'cap1', 30, 'cap3');
    let d2 = plan.preview;
    for (const op of split) d2 = apply(d2, op).doc;
    expect(listCaptions(d2).map((c) => [c.caption.recordIn, c.caption.recordOut, c.caption.text])).toEqual([[0, 30, 'So this'], [30, 60, 'is it.'], [61, 120, 'Next thought']]);

    const merge = compileMergeCaptionWithNext(d2, 'cap1');
    let d3 = d2;
    for (const op of merge) d3 = apply(d3, op).doc;
    expect(listCaptions(d3).map((c) => [c.caption.recordIn, c.caption.recordOut, c.caption.text])).toEqual([[0, 60, 'So this is it.'], [61, 120, 'Next thought']]);
    // Merging across a non-caption, or with nothing after, is refused as an empty program.
    expect(compileMergeCaptionWithNext(d3, 'cap2')).toEqual([]);
  });

  it('skips a cue that would overlap an item on an existing track', () => {
    const d = doc([track(TRACKS.v2, 'video', [caption('old', 0, 50, 'old')])]);
    const plan = compileGenerateCaptions(d, { cues: [{ startFrame: 10, endFrame: 40, text: 'x' }, { startFrame: 60, endFrame: 90, text: 'y' }], trackId: TRACKS.v2, newItemId: () => 'n', newTrackId: () => 't' });
    expect(plan.inserted).toBe(1);
    expect(plan.skipped).toBe(1);
  });
});

describe('cut workspace helpers', () => {
  it('smart-inserts at the nearest edit point and ripples what follows', () => {
    const d = interviewDoc();
    expect(nearestEditPoint(d, TRACKS.v1, 290)).toBe(300);
    expect(nearestEditPoint(d, TRACKS.v1, 100)).toBe(0);
    const plan = compileSmartInsert(d, TRACKS.v1, media('new', 0, 45, 0), 310);
    expect(plan?.atFrame).toBe(300);
    const after = apply(d, plan!.op).doc.tracks[0].items;
    expect(after.map((i) => [i.id, i.recordIn, i.recordOut])).toEqual([['c1', 0, 300], ['new', 300, 345], ['c2', 345, 525]]);
  });

  it('scales review speed to clip length and quantises to shuttle steps', () => {
    expect(reviewSpeed(30, FPS)).toBe(1);
    expect(reviewSpeed(180, FPS)).toBe(2);
    expect(reviewSpeed(3000, FPS)).toBe(8);
    const plan = reviewPlan(interviewDoc(), TRACKS.v1, FPS);
    expect(plan.map((s) => s.speed)).toEqual([3, 2]);
    expect(reviewStopAt(plan, 350)?.itemId).toBe('c2');
    expect(reviewStopAt(plan, 999)).toBeNull();
  });

  it('ranks boring shots by length and stillness, and locates them on the track', () => {
    const shots = readShots(artifact);
    expect(shots).toHaveLength(4);
    expect(shots[3].motionMean).toBeNull();
    const boring = rankBoringShots(shots, {}, { doc: interviewDoc(), trackId: TRACKS.v1, fps: FPS });
    // s0 (10 s, still) outranks s1 (6 s); s2 moves too much; s3 has no measurement.
    expect(boring.map((b) => b.id)).toEqual(['s0', 's1']);
    expect(boring[0].record).toEqual({ startFrame: 0, endFrame: 300, clipId: 'c1' });
    expect(boring[1].record).toEqual({ startFrame: 300, endFrame: 480, clipId: 'c2' });
    expect(rankBoringShots(shots, { minDurationSec: 8 }).map((b) => b.id)).toEqual(['s0']);
    expect(overviewPixelsPerFrame(interviewDoc(), 480)).toBe(1);
  });
});

describe('take stacks', () => {
  it('offers the duplicate-group alternates of a clip and swaps with one replace', () => {
    const d = interviewDoc();
    const stacks = buildTakeStacks(d, TRACKS.v1, readShots(artifact), readDuplicateGroups(artifact), FPS);
    expect(stacks).toHaveLength(2);
    expect(stacks[0]).toMatchObject({ clipId: 'c1', groupId: 'k0' });
    expect(stacks[0].currentShot.id).toBe('s0');
    expect(stacks[0].alternates.map((a) => [a.shot.id, a.isBest, a.fits])).toEqual([['s1', true, false]]);
    const op = compileSwapTake(d, 'c1', stacks[0].alternates[0].shot, FPS);
    expect(op).toMatchObject({ type: 'replace', itemId: 'c1' });
    const after = apply(d, op!).doc.tracks[0].items[0] as MediaClip;
    expect([after.recordIn, after.recordOut, after.sourceIn]).toEqual([0, 300, 600]);
    expect(compileSwapTake(after === after ? apply(d, op!).doc : d, 'c1', stacks[0].alternates[0].shot, FPS)).toBeNull();
    expect(readDuplicateGroups({ duplicateGroups: [{ id: 'k', shotIds: ['only'] }] })).toEqual([]);
  });
});

describe('timeline compare', () => {
  it('lists added, removed, moved, trimmed and changed items by id', () => {
    const before = interviewDoc();
    let after = apply(before, { type: 'trim', itemId: 'c1', edge: 'out', mode: 'ripple', deltaFrames: -30 }).doc;
    after = apply(after, { type: 'setProperty', itemId: 'c2', key: 'name', value: 'Take two' }).doc;
    after = apply(after, { type: 'append', trackId: TRACKS.v1, item: media('c3', 0, 10, 0) }).doc;
    const diff = diffTimelines(before, after);
    expect(diff.counts).toEqual({ added: 1, removed: 0, moved: 1, trimmed: 1, changed: 0 });
    expect(diff.items.find((i) => i.itemId === 'c2')).toMatchObject({ kind: 'moved', changedKeys: ['name'] });
    expect(diff.items.find((i) => i.itemId === 'c1')).toMatchObject({ kind: 'trimmed', before: { recordIn: 0, recordOut: 300 }, after: { recordIn: 0, recordOut: 270 } });
    expect(diff.lengthBefore).toBe(480);
    expect(diff.lengthAfter).toBe(460);
    expect(summarizeDiff(diff)).toBe('1 added, 1 moved, 1 trimmed');
    expect(summarizeDiff(diffTimelines(before, before))).toBe('No differences');
    const removed = diffTimelines(before, apply(before, { type: 'removeTrack', trackId: TRACKS.v1 }).doc);
    expect(removed.counts.removed).toBe(2);
    expect(removed.tracks).toEqual([{ kind: 'removed', trackId: TRACKS.v1, nameBefore: TRACKS.v1, nameAfter: null, changedKeys: [] }]);
  });
});

describe('stills and clip export', () => {
  it('names a still with its timecode and marks it preview quality', () => {
    expect(stillFileName('My Cut!', 1234, { num: 30, den: 1 })).toBe('my-cut-00-00-41-04-preview.png');
    expect(stillFileName('   ', 0, { num: 30, den: 1 })).toBe('timeline-00-00-00-00-preview.png');
  });

  it('exports a clip as the ordinary render over exactly its span', () => {
    const d = interviewDoc();
    const item = singleSelectedItem(d, ['c2']);
    expect(item?.id).toBe('c2');
    expect(singleSelectedItem(d, ['c1', 'c2'])).toBeNull();
    expect(clipExportRange(item!)).toEqual({ inFrame: 300, outFrame: 480 });
    expect(clipExportFileName(d, item!)).toBe('fixture-c2.mp4');
  });
});
