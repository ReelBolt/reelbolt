import type { EditorApi, Item, Timeline, TimelineDocument } from './api';
import { mediaClip } from './api';
import type { Seed } from './seed';

/** Wave 2 audio lane (R08.x): small audio-only timelines built through the REST API. */

export type AudioTrackSpec = { name: string; clips: Array<{ name: string; recordIn: number; recordOut: number }> };

/** A blank timeline whose audio tracks are exactly `tracks` (tone.wav clips); the video track stays empty. */
export async function audioTimeline(
  freshTimeline: (from: 'blank' | 'seeded' | 'derush') => Promise<string>,
  api: EditorApi,
  seed: Seed,
  tracks: AudioTrackSpec[],
): Promise<string> {
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    const template = doc.tracks.find((t) => t.kind === 'audio')!;
    const built = tracks.map((spec) => ({
      ...structuredClone(template),
      id: crypto.randomUUID(),
      name: spec.name,
      items: spec.clips.map((c): Item => mediaClip(crypto.randomUUID(), c.name, seed.files.tone, c.recordIn, c.recordOut)),
    }));
    doc.tracks = [...doc.tracks.filter((t) => t.kind !== 'audio'), ...built];
  });
  return id;
}

export const audioTrack = (tl: Timeline, name: string) => tl.document.tracks.find((t) => t.kind === 'audio' && t.name === name) as
  | (TimelineDocument['tracks'][number] & { gainDb?: { value?: number | null; keys?: Array<{ frame: number; value: number }> | null }; denoise?: { kind: string; strength: number } | null; leveler?: { kind: string; targetDb: number } | null })
  | undefined;
