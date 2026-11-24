import { test, expect } from '../../../fixtures/editor-v2';
import { discriminatorFirst, type Timeline } from '../../../fixtures/editor-v2/api';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { knownFailure } from '../expected-failures';

const itemCount = (tl: Timeline) => tl.document.tracks.reduce((n, t) => n + t.items.length, 0);

/**
 * P1-SYNC-08 (plan W0.2 "Concurrent PATCH → 409, never 500"): racing writers on the same baseRevision get
 * exactly one 200; every loser gets the same 409 body naming `currentRevision`, never a 500 (audit api9:
 * 8 parallel writers x 12 trials, 11 trials had 1-7 HTTP 500s with an empty body). Platform-independent.
 */
test('P1-SYNC-08 eight racing writers on one revision: one 200, seven 409 with currentRevision, no 500', async ({ freshTimeline, api, seed }) => {
  runOn('desktop');
  knownFailure('P1-SYNC-08');
  const id = await freshTimeline('seeded');
  const url = api.timelinesUrl(seed.projectId, id);
  const seen: string[] = [];
  for (let trial = 0; trial < 3; trial++) {
    const cur = await api.getTimeline(seed.projectId, id);
    const bodies = Array.from({ length: 8 }, (_, w) => {
      const doc = structuredClone(cur.document);
      doc.tracks[0].name = `race-${trial}-${w}`;
      return { baseRevision: cur.revision, document: discriminatorFirst(doc), ops: [] };
    });
    const res = await Promise.all(bodies.map((data) => api.request.fetch(url, { method: 'PATCH', data, failOnStatusCode: false })));
    const statuses = res.map((r) => r.status()).sort();
    seen.push(statuses.join(','));
    const losers = await Promise.all(res.filter((r) => r.status() === 409).map(async (r) => (await r.json()) as { currentRevision?: number }));
    expect(statuses, `trial ${trial}: one 200 and seven 409 (all trials: ${seen.join(' | ')})`).toEqual([200, ...Array(7).fill(409)]);
    for (const l of losers) expect(l.currentRevision, 'the 409 names the current revision').toBe(cur.revision + 1);
  }
});

/**
 * P3-SYNC-10 (plan W0.2 "flush on pagehide/visibilitychange with keepalive"): an edit followed by a full
 * navigation 150 ms later is stored (audit y4/y5/s4: no PATCH was ever sent, the edit was lost on desktop
 * and both phones). The edit and the navigation run in one page script so the gap is exact on every device.
 */
test('P3-SYNC-10 an edit followed by a full navigation 150 ms later is stored', async ({ freshTimeline, openEditor }) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  knownFailure('P3-SYNC-10');
  const ed = await openEditor(await freshTimeline('seeded'));
  const before = await ed.serverTimeline();
  await ed.seek(75);
  await ed.selectClipUnderPlayhead();
  await ed.page.evaluate(() => {
    const visible = (sel: string) => [...document.querySelectorAll<HTMLElement>(sel)].find((e) => e.offsetParent !== null)!;
    visible('[data-testid="blade-at-playhead"], [data-testid="phone-action-split"]').click();
    setTimeout(() => { location.href = '/app/editor'; }, 150);
  });
  await expect(ed.page).not.toHaveURL(new RegExp(`/timeline/${ed.timelineId}`), { timeout: 20_000 });
  await ed.expectServer((tl) => itemCount(tl) === itemCount(before) + 1, 'the blade made 150 ms before leaving is on the server', 10_000);
});
