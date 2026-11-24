import type { Locator } from '@playwright/test';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';

const ref = (id: string) => `file:${id}`;
const tile = (ed: EditorPage, mediaRef: string): Locator => ed.page.locator(`[data-testid="media-clip-tile"][data-media-ref="${mediaRef}"]`).first();

async function openPool(ed: EditorPage): Promise<void> {
  await ed.workspace('media');
  await expect(ed.testId('media-pool')).toBeVisible({ timeout: 30_000 });
  await expect(ed.page.getByTestId('media-clip-tile').first()).toBeVisible();
}

type Bin = { id: string; name: string; parentBinId: string | null };

test('R05.3 deleting a bin shows an Undo toast, and Undo brings back the bin, its sub-bin and its pinned clip', async ({ seed, freshTimeline, openEditor }) => {
  runOn('desktop');
  const base = `/api/v1/projects/${seed.projectId}/media/bins`;
  const stamp = Date.now();
  const name = `Undo-${stamp}`;
  const subName = `UndoSub-${stamp}`;
  const mediaRef = ref(seed.files.clipA);
  const parent = await seed.api.json<Bin>('POST', base, { name }, [200, 201]);
  const sub = await seed.api.json<Bin>('POST', base, { name: subName, parentBinId: parent.id }, [200, 201]);
  await seed.api.json('POST', `${base}/${parent.id}/items`, { mediaRef }, [200, 201]);
  await seed.api.json('POST', `${base}/${sub.id}/items`, { mediaRef: ref(seed.files.clipB) }, [200, 201]);
  const ed = await openEditor(await freshTimeline('blank'));
  const all = () => seed.api.json<Bin[]>('GET', base);
  try {
    await openPool(ed);
    await ed.tap(ed.page.getByRole('button', { name: `Actions for ${name}` }));
    await ed.testId(`bin-action-delete-${parent.id}`).click();
    await ed.testId('bin-delete-confirm').click();
    await expect.poll(async () => (await all()).filter((b) => b.name === name || b.name === subName).length).toBe(0);

    const undo = ed.testId('bin-undo-delete');
    await expect(undo).toBeVisible();
    await expect(ed.testId('bin-undo-toast')).toContainText('Bin deleted.');
    await ed.tap(undo);

    // The stored bins are back: same names, the sub-bin hangs under the restored parent, and the pins are there.
    await expect.poll(async () => (await all()).filter((b) => b.name === name || b.name === subName).length).toBe(2);
    const bins = await all();
    const back = bins.find((b) => b.name === name)!;
    const backSub = bins.find((b) => b.name === subName)!;
    expect(backSub.parentBinId).toBe(back.id);
    const pins = (id: string) => seed.api.json<Array<{ mediaRef: string }>>('GET', `${base}/${id}/items`);
    await expect.poll(async () => (await pins(back.id)).map((p) => p.mediaRef)).toEqual([mediaRef]);
    await expect.poll(async () => (await pins(backSub.id)).map((p) => p.mediaRef)).toEqual([ref(seed.files.clipB)]);
    await expect(ed.page.getByRole('button', { name: `Actions for ${name}` })).toBeVisible();
  } finally {
    for (const b of await all()) {
      if (b.name === name || b.name === subName) await seed.api.json('DELETE', `${base}/${b.id}`, undefined, [200, 204, 404]);
    }
  }
});
