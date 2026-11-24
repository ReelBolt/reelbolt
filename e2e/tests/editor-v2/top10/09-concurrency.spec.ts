import { test, expect } from '../../../fixtures/editor-v2';
import { discriminatorFirst } from '../../../fixtures/editor-v2/api';
import { knownFailure } from '../expected-failures';

/**
 * Top-10 #9 (report.md §3), server half: two writers with the same baseRevision get one 200 and one 409,
 * never a 500 (audit: 500 with an empty body in 11 of 12 trials). The client half (edits lost on
 * navigation, autosave starved during playback) is pinned by guards/flush.spec.ts.
 */
test('TOP10-09 P1-SYNC-08 concurrent PATCHes on one revision answer 200 and 409, never 500', async ({ freshTimeline, api, seed }) => {
  knownFailure('TOP10-09');
  const id = await freshTimeline('seeded');
  const url = api.timelinesUrl(seed.projectId, id);
  const statuses: number[][] = [];
  for (let trial = 0; trial < 4; trial++) {
    const cur = await api.getTimeline(seed.projectId, id);
    const bodies = [0, 1].map((w) => {
      const doc = structuredClone(cur.document);
      doc.tracks[0].name = `race-${trial}-${w}`;
      return { baseRevision: cur.revision, document: discriminatorFirst(doc), ops: [] };
    });
    const res = await Promise.all(bodies.map((data) => api.request.fetch(url, { method: 'PATCH', data, failOnStatusCode: false })));
    statuses.push(res.map((r) => r.status()).sort());
  }
  expect(statuses, 'each race: one 200, one 409').toEqual(Array(4).fill([200, 409]));
});
