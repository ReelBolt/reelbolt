import * as path from 'path';
import { test, expect } from '../../../fixtures/editor-v2';
import { ensureMedia } from '../../../fixtures/editor-v2/seed';
import { knownFailure } from '../expected-failures';

/**
 * Top-10 #5 (report.md §3): an upload is probed and processed without anyone opening it. Nothing here
 * requests derivatives: the upload alone must yield a probe (duration) and a proxy. On origin/master
 * `probe` stays null and derivatives start only when a tile is clicked.
 */
test('TOP10-05 P2-LAZY-04 P2-META-01 an upload is probed and gets a proxy on its own', async ({ api, seed }, testInfo) => {
  knownFailure('TOP10-05');
  test.setTimeout(240_000);
  const name = `top10-05-${testInfo.project.name}-${Date.now()}.mp4`;
  const fileId = await api.upload(seed.projectId, path.join(ensureMedia(), 'clipB.mp4'), 'video/mp4', name);
  const ref = `file:${fileId}`;
  await expect.poll(async () => {
    const media = await api.json<Array<{ mediaRef: string; probe: unknown }>>('GET', `/api/v1/projects/${seed.projectId}/media`);
    return media.find((m) => m.mediaRef === ref)?.probe != null;
  }, { message: 'the upload carries a probe', timeout: 120_000, intervals: [3000] }).toBe(true);
  await expect.poll(async () => {
    const rows = await api.json<Array<{ kind: string }>>('GET', `/api/v1/projects/${seed.projectId}/media/${ref}/derivatives`);
    return rows.some((r) => r.kind === 'proxy');
  }, { message: 'the upload got a proxy without being opened', timeout: 120_000, intervals: [3000] }).toBe(true);
});
