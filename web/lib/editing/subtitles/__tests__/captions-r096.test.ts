import { describe, expect, it } from 'vitest';
import { apply, assertInvariants } from '../../engine';
import { caption, doc, media, track } from '../../__tests__/fixtures';
import { aspectKind, compileAddCaptionAt, compileImportCues, compileNudgeCaption, defaultCaptionStyle, parseSubtitleFile } from '../subtitles';

let n = 0;
const fresh = () => `00000000-0000-4000-8000-${String(++n + 7000).padStart(12, '0')}`;
const run = (d: ReturnType<typeof doc>, ops: Parameters<typeof apply>[1][]) => ops.reduce((acc, op) => apply(acc, op).doc, d);

const base = () => doc([track('t-v', 'video', [media('21111111-0000-4000-8000-000000000001', 0, 600)])]);

describe('R09.6 looks per aspect', () => {
  it('picks a bold look for vertical, boxed for square, clean for wide', () => {
    expect(defaultCaptionStyle(1080, 1920).look).toBe('bold');
    expect(defaultCaptionStyle(1080, 1080).look).toBe('boxed');
    expect(defaultCaptionStyle(1920, 1080).look).toBe('clean');
    expect(aspectKind(1920, 1080)).toBe('wide');
  });
});

describe('R09.6 manual add and nudge', () => {
  it('adds a multiline caption at the playhead with a look', () => {
    const plan = compileAddCaptionAt(base(), { atFrame: 30, text: 'Line one\nLine two', trackId: null, look: 'boxed', position: 'topCenter', newItemId: fresh, newTrackId: fresh });
    expect(plan.refusal).toBeNull();
    const next = run(base(), plan.ops);
    assertInvariants(next, { ids: 'opaque' });
    const c = next.tracks.flatMap((t) => t.items).find((i) => i.type === 'caption');
    expect(c).toMatchObject({ text: 'Line one\nLine two', look: 'boxed', position: 'topCenter', recordIn: 30, recordOut: 120 });
  });

  it('refuses to add on top of a caption, in plain words', () => {
    const d = doc([track('t-s', 'video', [caption('31111111-0000-4000-8000-000000000001', 0, 90, 'x')])]);
    const plan = compileAddCaptionAt(d, { atFrame: 10, text: 'y', trackId: 't-s', newItemId: fresh, newTrackId: fresh });
    expect(plan.ops).toEqual([]);
    expect(plan.refusal).toMatch(/already a caption/);
  });

  it('nudges a caption and refuses to run into its neighbour', () => {
    const a = '31111111-0000-4000-8000-000000000001';
    const b = '31111111-0000-4000-8000-000000000002';
    const d = doc([track('t-s', 'video', [caption(a, 0, 60, 'a'), caption(b, 90, 150, 'b')])]);
    const ok = compileNudgeCaption(d, a, 'both', 10);
    expect(run(d, ok.ops).tracks[0].items[0]).toMatchObject({ recordIn: 10, recordOut: 70 });
    const bad = compileNudgeCaption(d, a, 'end', 40);
    expect(bad.ops).toEqual([]);
    expect(bad.refusal).toMatch(/neighbouring caption/);
    expect(compileNudgeCaption(d, a, 'start', -5).refusal).toMatch(/before the beginning/);
  });
});

const SRT = '﻿1\r\n00:00:01,000 --> 00:00:03,500\r\n<i>Hello</i> there\r\nsecond line\r\n\r\n2\r\n00:00:04,000 --> 00:00:03,000\r\nbackwards\r\n\r\n3\r\n00:01:05,250 --> 00:01:07,000\r\nA &amp; B\r\n';
const VTT = 'WEBVTT\n\nNOTE a note\n\nintro\n00:01.000 --> 00:02.000 align:start\nOne\n\n01:00:00.000 --> 01:00:01.000\nTwo\n';

describe('R12.9 SRT and WebVTT import', () => {
  it('reads SRT with tags, entities, CRLF, a BOM and a bad cue reported in plain words', () => {
    const parsed = parseSubtitleFile(SRT);
    expect(parsed.format).toBe('srt');
    expect(parsed.cues).toEqual([
      { startSec: 1, endSec: 3.5, text: 'Hello there\nsecond line' },
      { startSec: 65.25, endSec: 67, text: 'A & B' },
    ]);
    expect(parsed.problems).toEqual(['Cue 2 ends before it starts and was skipped.']);
  });

  it('reads WebVTT with notes, ids, cue settings and hour stamps', () => {
    const parsed = parseSubtitleFile(VTT);
    expect(parsed.format).toBe('vtt');
    expect(parsed.cues.map((c) => [c.startSec, c.text])).toEqual([[1, 'One'], [3600, 'Two']]);
  });

  it('says so when the file is not subtitles', () => {
    expect(parseSubtitleFile('just some words').problems[0]).toMatch(/No captions were found/);
  });

  it('appends cues, skipping what overlaps, and replaces the track on request', () => {
    const cues = parseSubtitleFile(SRT).cues;
    const appended = compileImportCues(base(), { cues, trackId: null, mode: 'append', newItemId: fresh, newTrackId: fresh });
    expect(appended.inserted).toBe(2);
    const afterAppend = run(base(), appended.ops);
    assertInvariants(afterAppend, { ids: 'opaque' });
    const track2 = afterAppend.tracks.find((t) => t.items.some((i) => i.type === 'caption'));
    expect(track2?.items).toHaveLength(2);
    const replaced = compileImportCues(afterAppend, { cues: [cues[0]], trackId: null, mode: 'replace', newItemId: fresh, newTrackId: fresh });
    expect(replaced.removed).toBe(2);
    const afterReplace = run(afterAppend, replaced.ops);
    assertInvariants(afterReplace, { ids: 'opaque' });
    expect(afterReplace.tracks.flatMap((t) => t.items).filter((i) => i.type === 'caption')).toHaveLength(1);
  });

  it('shifts cues by an offset', () => {
    const plan = compileImportCues(base(), { cues: [{ startSec: 1, endSec: 2, text: 'x' }], trackId: null, mode: 'append', offsetSec: 2, newItemId: fresh, newTrackId: fresh });
    const c = run(base(), plan.ops).tracks.flatMap((t) => t.items).find((i) => i.type === 'caption');
    expect(c).toMatchObject({ recordIn: 90, recordOut: 120 });
  });
});
