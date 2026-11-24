import { test as base, expect, SEEDED_LAYOUT } from './index';
import { mediaClip } from './api';
import type { Seed } from './seed';
import { uuid } from './lane-b';

export { expect };

/** The seeded timeline's canonical content: V1 clipA, clipB, vertical, still (150 frames each) and A1 tone.wav. */
async function resetSeeded(seed: Seed, timelineId: string): Promise<void> {
  const names = { clipA: 'clipA.mp4', clipB: 'clipB.mp4', vertical: 'vertical.mp4', still: 'still.png' } as const;
  await seed.api.putDocument(seed.projectId, timelineId, (doc) => {
    const v = doc.tracks.find((t) => t.kind === 'video')!;
    const a = doc.tracks.find((t) => t.kind === 'audio')!;
    v.items = (['clipA', 'clipB', 'vertical', 'still'] as const).map((k) => mediaClip(uuid(), names[k], seed.files[k], SEEDED_LAYOUT[k][0], SEEDED_LAYOUT[k][1]));
    a.items = [mediaClip(uuid(), 'tone.wav', seed.files.tone, SEEDED_LAYOUT.tone[0], SEEDED_LAYOUT.tone[1])];
    doc.tracks = [v, a];
    const loose = doc as unknown as Record<string, unknown>;
    for (const key of ['colorGroups', 'sequences', 'buses', 'inOut']) delete loose[key];
    doc.markers = [];
  });
}

/**
 * `test` for the lane-B specs: `freshTimeline('seeded')` returns a copy of the seeded timeline RESET to its
 * canonical content. The shared seeded timeline is opened and edited by other specs in a full run (grade,
 * viewer, timeline ops), so a plain duplicate can carry a grade, a retime or a channel map into a test that
 * asserts a clean clip.
 */
export const test = base.extend({
  freshTimeline: async ({ seed, freshTimeline }, use) => {
    await use(async (from) => {
      const id = await freshTimeline(from);
      if (from === 'seeded') await resetSeeded(seed, id);
      return id;
    });
  },
});
