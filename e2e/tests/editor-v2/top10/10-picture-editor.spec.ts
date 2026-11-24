import * as path from 'path';
import { test, expect } from '../../../fixtures/editor-v2';
import { ensureMedia } from '../../../fixtures/editor-v2/seed';
import { knownFailure } from '../expected-failures';

/**
 * Top-10 #10 (report.md §3), picture editor: a photo opens at its own size, and deleting the source photo
 * does not destroy the pictures made from it. still.png is 1920x1080; on origin/master every new document
 * is 1280x720 and the file delete cascades to the document.
 */
const docs = (projectId: string) => `/api/v1/projects/${projectId}/image-documents`;

test('TOP10-10 TH-DOC-06 a 1920x1080 photo opens in a 1920x1080 picture', async ({ page, api, seed }) => {
  knownFailure('TOP10-10');
  const doc = await api.json<{ id: string }>('POST', docs(seed.projectId), { sourceProjectFileId: seed.files.still, name: `top10-10-${Date.now()}` }, [200, 201]);
  await page.goto(`/app/editor/thumbnails/${doc.id}?project=${seed.projectId}`);
  await expect(page.locator('[role=application][aria-label="Thumbnail canvas"]')).toBeVisible({ timeout: 45_000 });
  await expect(page.getByText(/^\d+ × \d+$/).first(), 'the canvas readout').toHaveText('1920 × 1080');
  await expect.poll(async () => {
    const d = await api.json<{ canvasWidth: number; canvasHeight: number }>('GET', `${docs(seed.projectId)}/${doc.id}`);
    return `${d.canvasWidth}x${d.canvasHeight}`;
  }, { message: 'the stored canvas size' }).toBe('1920x1080');
});

test('TOP10-10 TH-DOC-06 deleting the source photo keeps the picture made from it', async ({ api, seed }, testInfo) => {
  knownFailure('TOP10-10');
  const fileId = await api.upload(seed.projectId, path.join(ensureMedia(), 'still.png'), 'image/png', `top10-10-${testInfo.project.name}-${Date.now()}.png`);
  const doc = await api.json<{ id: string }>('POST', docs(seed.projectId), { sourceProjectFileId: fileId, name: 'top10-10-delete' }, [200, 201]);
  await api.json('DELETE', `/api/v1/projects/${seed.projectId}/files/${fileId}`, undefined, [200, 204]);
  const res = await api.request.get(`${docs(seed.projectId)}/${doc.id}`, { failOnStatusCode: false });
  expect(res.status(), 'the picture still exists after its source photo is deleted').toBe(200);
});
