import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { test, expect } from '../../../fixtures/editor-v2';
import { ensureMedia } from '../../../fixtures/editor-v2/seed';

/**
 * Wave 1 hub (T-007): the Media workspace takes uploads, and deleting a bin asks first. Both rows are checked on the
 * server (the pool and the bin list), not on the control.
 */

test('R-upload a file uploaded from the Media workspace appears in the pool', async ({ seed, api, freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('blank'));
  await ed.workspace('media');
  await expect(ed.testId('media-pool')).toBeVisible({ timeout: 30_000 });
  const phoneSheet = ed.isPhone;

  const fileName = `hub-upload-${Date.now()}.png`;
  const tmp = path.join(os.tmpdir(), fileName);
  fs.copyFileSync(path.join(ensureMedia(), 'still.png'), tmp);
  try {
    const tilesBefore = await ed.page.getByTestId('media-clip-tile').count();
    // The phone's Add media sheet has its own picker and upload row.
    await ed.page.locator(phoneSheet ? '[data-testid="add-media-input"]' : '[data-testid="media-upload-header"] input[type="file"]').setInputFiles(tmp);

    await expect.poll(async () => (await api.listFiles(seed.projectId)).some((f) => f.originalFileName === fileName), {
      message: 'the file is in the project', timeout: 60_000,
    }).toBe(true);
    await expect.poll(async () => {
      const pool = await api.json<Array<{ fileName: string }>>('GET', `/api/v1/projects/${seed.projectId}/media`);
      return pool.some((item) => item.fileName === fileName);
    }, { message: 'the media pool lists the new file', timeout: 60_000 }).toBe(true);
    await expect(ed.page.getByTestId('media-clip-tile')).toHaveCount(tilesBefore + 1, { timeout: 30_000 });
    await expect(ed.page.getByTestId(phoneSheet ? 'add-media-upload-row' : 'media-upload-row').first()).toHaveAttribute('data-status', 'done');
  } finally {
    fs.rmSync(tmp, { force: true });
  }
});

test('R04.3 deleting a bin asks first, naming it and what goes; Cancel keeps it and Delete removes it', async ({ seed, api, freshTimeline, openEditor }) => {
  const binName = `Bin ${Date.now()}`;
  const bin = await api.json<{ id: string; name: string }>('POST', `/api/v1/projects/${seed.projectId}/media/bins`, { name: binName }, [200, 201]);
  const bins = () => api.json<Array<{ id: string }>>('GET', `/api/v1/projects/${seed.projectId}/media/bins`);

  const ed = await openEditor(await freshTimeline('blank'));
  await ed.workspace('media');
  if (ed.isPhone) {
    // The phone lists bins as chips in the Add media sheet and has no bin tree: deleting or reparenting a bin is "not on a phone" (docs/editor.md).
    await expect(ed.page.getByRole('button', { name: binName })).toBeVisible({ timeout: 30_000 });
    await expect(ed.testId('bin-tree')).toHaveCount(0);
    await expect(ed.page.getByRole('button', { name: `Actions for ${binName}` })).toHaveCount(0);
    return;
  }
  await expect(ed.testId('bin-tree')).toBeVisible({ timeout: 30_000 });

  await ed.page.getByRole('button', { name: `Actions for ${binName}` }).click();
  await ed.page.getByTestId(`bin-action-delete-${bin.id}`).click();
  const text = await ed.page.getByTestId('bin-delete-text').innerText();
  expect(text).toContain(binName);
  expect(text).toMatch(/0 sub-bins/);
  expect(text).toContain('Clips stay in your project; only the grouping goes.');
  await ed.page.getByTestId('bin-delete-cancel').click();
  await expect(ed.page.getByTestId('bin-delete-text')).toHaveCount(0);
  expect((await bins()).some((b) => b.id === bin.id), 'Cancel left the bin on the server').toBe(true);

  await ed.page.getByRole('button', { name: `Actions for ${binName}` }).click();
  await ed.page.getByTestId(`bin-action-delete-${bin.id}`).click();
  await ed.page.getByTestId('bin-delete-confirm').click();
  await expect.poll(async () => (await bins()).some((b) => b.id === bin.id), { message: 'Delete removed it on the server' }).toBe(false);
});
