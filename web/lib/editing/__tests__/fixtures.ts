/**
 * Shared document fixtures for the P3 shell tests — built through the same invariants the engine
 * maintains, so a test's input is always a document the engine may edit.
 */

import type { Caption, EditTimeline, Item, Marker, MediaClip, TitleClip, Track } from '../types';

export const TRACKS = { v1: 'track-v1', v2: 'track-v2', a1: 'track-a1', a2: 'track-a2' };

export const assetRef = (n: number) => ({ kind: 'projectFile' as const, projectFileId: `00000000-0000-4000-8000-${String(n).padStart(12, '0')}` });

export const media = (id: string, recordIn: number, recordOut: number, sourceIn = 0, extra: Partial<MediaClip> = {}): MediaClip => ({
  id,
  type: 'mediaClip',
  name: id,
  recordIn,
  recordOut,
  assetRef: assetRef(1),
  sourceIn,
  playbackRate: 1,
  enabled: true,
  provenance: null,
  ...extra,
});

export const title = (id: string, recordIn: number, recordOut: number, text = id): TitleClip => ({
  id,
  type: 'titleClip',
  name: id,
  recordIn,
  recordOut,
  text,
  fontSize: 32,
  fontFamily: 'Inter',
  color: '#ffffff',
  enabled: true,
  provenance: null,
});

export const caption = (id: string, recordIn: number, recordOut: number, text = id): Caption => ({
  id,
  type: 'caption',
  name: id,
  recordIn,
  recordOut,
  text,
  language: 'en',
  enabled: true,
  provenance: null,
});

export const track = (id: string, kind: Track['kind'], items: Item[], extra: Partial<Track> = {}): Track => ({
  id,
  kind,
  name: id,
  height: 64,
  locked: false,
  enabled: true,
  mute: false,
  solo: false,
  items,
  ...extra,
});

export const marker = (id: string, frame: number, extra: Partial<Marker> = {}): Marker => ({
  id,
  frame,
  name: id,
  color: null,
  note: null,
  durationFrames: null,
  ...extra,
});

export function doc(tracks: Track[], markers: Marker[] = []): EditTimeline {
  return {
    schemaVersion: 2,
    id: 'timeline-1',
    projectId: 'project-1',
    name: 'Fixture',
    settings: {
      width: 1920,
      height: 1080,
      rate: { num: 30, den: 1 },
      sampleRate: 48000,
      pixelAspect: { num: 1, den: 1 },
      aspectPolicy: 'fit',
      startTimecodeFrames: 0,
    },
    tracks,
    markers,
    inOut: null,
    seed: null,
  };
}

/** Two video tracks, two audio tracks, a gap on V1 and a rationale-bearing clip. */
export function standardDoc(): EditTimeline {
  return doc(
    [
      track(TRACKS.v1, 'video', [
        media('c1', 0, 60, 0, {
          provenance: { origin: 'generated', rationale: 'Opening shot keeps the hook.', editedByUser: false, agentName: 'VideoStoryEditor', providerName: 'vLLM' },
        }),
        media('c2', 60, 120, 100),
        media('c3', 150, 240, 300),
      ]),
      track(TRACKS.v2, 'video', [title('t1', 30, 90, 'Lower third')]),
      track(TRACKS.a1, 'audio', [media('a1', 0, 240, 0, { gainDb: { value: -6, keys: null } })]),
      track(TRACKS.a2, 'audio', [media('a2', 0, 120, 0, { gainDb: { value: 0, keys: [{ frame: 0, value: -12, interp: 'linear' }, { frame: 60, value: 0, interp: 'step' }] } })]),
    ],
    [marker('m1', 45, { name: 'Hook' }), marker('m2', 150, { name: 'Act two' })],
  );
}
