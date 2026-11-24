import { test, expect } from '../../../fixtures/editor-v2';
import { discriminatorFirst } from '../../../fixtures/editor-v2/api';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { knownFailure } from '../expected-failures';

/**
 * P1-DOC-01 (verdict-table P1 DOC; plan W0.2 "Validator: pin schemaVersion to 2"): `schemaVersion` is fixed
 * at 2. A missing value is stored as 2; any other value is a 400 naming `schemaVersion`, and nothing is
 * written. Audit api1/api2: `1`, `3`, `0` were all accepted (200) and stored. Platform-independent (=D).
 */
test('P1-DOC-01 schemaVersion is pinned to 2: other values are refused, a missing one is stored as 2', async ({ freshTimeline, api, seed }) => {
  runOn('desktop');
  knownFailure('P1-DOC-01');
  const id = await freshTimeline('blank');
  const url = api.timelinesUrl(seed.projectId, id);

  for (const version of [1, 3, 0]) {
    const cur = await api.getTimeline(seed.projectId, id);
    const doc = { ...structuredClone(cur.document), schemaVersion: version };
    const res = await api.request.fetch(url, { method: 'PATCH', data: { baseRevision: cur.revision, document: discriminatorFirst(doc), ops: [] }, failOnStatusCode: false });
    const body = await res.text();
    expect(res.status(), `schemaVersion ${version} is refused (${body.slice(0, 200)})`).toBe(400);
    expect(body, `the refusal names schemaVersion`).toMatch(/schemaVersion/i);
    const after = await api.getTimeline(seed.projectId, id);
    expect(after.revision, `schemaVersion ${version}: nothing written`).toBe(cur.revision);
    expect(after.document.schemaVersion).toBe(2);
  }

  const cur = await api.getTimeline(seed.projectId, id);
  const { schemaVersion: _drop, ...rest } = structuredClone(cur.document);
  const res = await api.request.fetch(url, { method: 'PATCH', data: { baseRevision: cur.revision, document: discriminatorFirst(rest), ops: [] }, failOnStatusCode: false });
  expect(res.status(), 'a document without schemaVersion is accepted').toBe(200);
  expect((await api.getTimeline(seed.projectId, id)).document.schemaVersion, 'and stored as 2').toBe(2);
});
