import * as path from 'path';
import { test, expect } from '../../../fixtures/editor-v2';
import { runOn, uniqueCopy } from '../../../fixtures/editor-v2/rows';
import { ensureMedia } from '../../../fixtures/editor-v2/seed';
import { knownFailure } from '../expected-failures';

/**
 * TH-DOC-06 (remediation-plan.md W0.8 T4, decision D10 copy-on-delete): deleting a photo in Files warns that
 * pictures use it and keep their own copy; afterwards the picture made from it still opens with its pixels.
 * The audit (d6, m4 t4): the Files dialog never mentions pictures and the delete hard-deletes the whole
 * picture document (GET -> 404). Desktop and both phones (the delete flow is tap-only).
 */
test('TH-DOC-06 deleting the source photo in Files warns about the picture, and the picture still opens', async ({ page, api, seed }, testInfo) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  knownFailure('TH-DOC-06');
  const name = `thdoc06-${testInfo.project.name}-${Date.now()}.png`;
  const fileId = await api.upload(seed.projectId, uniqueCopy(path.join(ensureMedia(), 'still.png')), 'image/png', name);
  const docs = `/api/v1/projects/${seed.projectId}/image-documents`;
  const doc = await api.json<{ id: string }>('POST', docs, { sourceProjectFileId: fileId, name: `thdoc06-${testInfo.project.name}` }, [200, 201]);

  await page.goto(`/app/projects/${seed.projectId}/files`);
  await page.getByRole('tab', { name: /^Files/ }).click();
  const select = page.getByLabel(`Select ${name}`);
  await expect(select).toBeVisible({ timeout: 30_000 });
  await select.check({ force: true });
  await page.getByRole('region', { name: 'Selection' }).getByRole('button', { name: 'Delete' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  await expect(dialog, 'the delete dialog says a picture uses this photo and keeps its own copy').toContainText(/picture/i);
  await expect(dialog).toContainText(/keep/i);
  await dialog.getByRole('button', { name: /^delete/i }).click();
  await expect.poll(async () => (await api.listFiles(seed.projectId)).some((f) => f.id === fileId), { message: 'the photo is deleted' }).toBe(false);

  const res = await api.request.get(`${docs}/${doc.id}`, { failOnStatusCode: false });
  expect(res.status(), 'the picture still exists').toBe(200);
  await page.goto(`/app/editor/thumbnails/${doc.id}?project=${seed.projectId}`);
  await expect(page.locator('[role=application][aria-label="Thumbnail canvas"]'), 'the picture opens').toBeVisible({ timeout: 45_000 });
});
