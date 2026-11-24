import type { EditTimeline, MediaClip, Track } from '../../types';
import type { BeatAnalysis } from '../beat-grid';

export const clip = (id: string, recordIn: number, recordOut: number, extra: Partial<MediaClip> = {}): MediaClip => ({
  id,
  type: 'mediaClip',
  name: id,
  recordIn,
  recordOut,
  provenance: null,
  assetRef: { kind: 'projectFile', projectFileId: `file-${id}` },
  sourceIn: 0,
  playbackRate: 1,
  enabled: true,
  ...extra,
});

export const track = (id: string, kind: 'video' | 'audio', items: MediaClip[], locked = false): Track => ({ id, kind, name: id, height: 64, locked, enabled: true, mute: false, solo: false, items });

export const timeline = (tracks: Track[]): EditTimeline => ({
  schemaVersion: 2,
  id: 'tl',
  projectId: 'p',
  name: 'tl',
  settings: { width: 320, height: 180, rate: { num: 30, den: 1 }, sampleRate: 48000, pixelAspect: { num: 1, den: 1 }, aspectPolicy: 'fit', startTimecodeFrames: 0 },
  tracks,
  markers: [],
  inOut: null,
  seed: null,
});

/** 120 bpm (0.5 s beats), beat k at k*0.5 s, downbeat index 0 -> every 15 frames at 30 fps. */
export const analysis = (extra: Partial<BeatAnalysis> = {}): BeatAnalysis => ({
  bpm: 120,
  beatSec: 0.5,
  beatPhaseSec: 0,
  barPhaseSec: 0,
  beatsPerBar: 4,
  beatTimesSec: Array.from({ length: 33 }, (_, i) => i * 0.5),
  firstDownbeatIndex: 0,
  durationSec: 16,
  analysedSec: 16,
  dropSec: null,
  outroSec: null,
  confidence: 0.9,
  steady: true,
  tempoWord: 'allegro',
  halfTempoScore: 0,
  doubleTempoScore: 0,
  truncated: false,
  ...extra,
});

export const counter = (prefix = 'n') => {
  let n = 0;
  return () => `${prefix}${++n}`;
};
