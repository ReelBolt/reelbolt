import { describe, expect, it } from 'vitest';

import type { EditTimeline, MediaClip, Track } from '../../types';
import { mapSourceSilenceToRecord, type SourceMatcher } from '../../silence/remove-silence';
import { sourceWindowToRecordSpans, wordIsOnTrack, wordRecordFrame, wordsToRecordSpans, type TranscriptWord } from '../transcript';

// Two files on one track, both starting at source 0: file A (source 0) then file B (source 1).
const clip = (id: string, file: string, recordIn: number, recordOut: number): MediaClip => ({
  id,
  type: 'mediaClip',
  name: id,
  recordIn,
  recordOut,
  provenance: null,
  assetRef: { kind: 'projectFile', projectFileId: file },
  sourceIn: 0,
  playbackRate: 1,
  enabled: true,
});

const doc = (): EditTimeline => {
  const track: Track = { id: 'v1', kind: 'video', name: 'V1', height: 64, locked: false, enabled: true, mute: false, solo: false, items: [clip('a', 'file-a', 0, 300), clip('b', 'file-b', 300, 600)] };
  return {
    schemaVersion: 2,
    id: 'tl',
    projectId: 'p',
    name: 'tl',
    settings: { width: 320, height: 180, rate: { num: 30, den: 1 }, sampleRate: 48000, pixelAspect: { num: 1, den: 1 }, aspectPolicy: 'fit', startTimecodeFrames: 0 },
    tracks: [track],
    markers: [],
    inOut: null,
    seed: null,
  };
};

const files = ['file-a', 'file-b'];
const matches: SourceMatcher = (c, sourceIndex) => c.assetRef.projectFileId === files[sourceIndex];
const word = (id: string, startSec: number, endSec: number, sourceIndex: number): TranscriptWord => ({ id, startSec, endSec, text: id, sourceIndex, segmentId: null, estimated: false });

describe('source-safe mapping (two-file track)', () => {
  it("maps file A's silence only onto file A's clip", () => {
    const d = doc();
    const [a, b] = d.tracks[0].items as MediaClip[];
    const span = { id: 'g0', startSec: 3, endSec: 4.5, sourceIndex: 0 };
    expect(mapSourceSilenceToRecord(a, span, 30, matches)).toEqual({ startFrame: 90, endFrame: 135 });
    expect(mapSourceSilenceToRecord(b, span, 30, matches)).toBeNull();
    // Without a matcher the same seconds would also land on file B — the audit failure.
    expect(mapSourceSilenceToRecord(b, span, 30)).not.toBeNull();
  });

  it("maps a source-1 window only onto file B's clip", () => {
    expect(sourceWindowToRecordSpans(doc(), 'v1', { startSec: 2, endSec: 3, sourceIndex: 1 }, 30, matches)).toEqual([{ startFrame: 360, endFrame: 390 }]);
    expect(sourceWindowToRecordSpans(doc(), 'v1', { startSec: 2, endSec: 3, sourceIndex: 0 }, 30, matches)).toEqual([{ startFrame: 60, endFrame: 90 }]);
  });

  it('deletes words of one file without touching the other, and never merges windows across files', () => {
    const d = doc();
    expect(wordsToRecordSpans(d, 'v1', [word('w1', 1, 1.5, 0)], 30, matches)).toEqual([{ startFrame: 30, endFrame: 45 }]);
    expect(wordsToRecordSpans(d, 'v1', [word('w2', 1, 1.5, 1)], 30, matches)).toEqual([{ startFrame: 330, endFrame: 345 }]);
    // Same seconds, two files: two pieces, one per file — not one window spanning both.
    expect(wordsToRecordSpans(d, 'v1', [word('w1', 1, 1.5, 0), word('w2', 1.6, 2, 1)], 30, matches)).toEqual([
      { startFrame: 30, endFrame: 45 },
      { startFrame: 348, endFrame: 360 },
    ]);
    expect(wordRecordFrame(d, 'v1', word('w2', 1, 1.5, 1), 30, matches)).toBe(330);
    expect(wordIsOnTrack(d, 'v1', word('w3', 1, 1.5, 2), 30, matches)).toBe(false);
  });
});
