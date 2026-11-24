import { test, expect, freshProject, hubUrl, createFull, getFull, isPhone, press, stageOf } from '../../../fixtures/editor-v2/picture-lane';
import { picturesUrl } from '../../../fixtures/editor-v2/picture';

/**
 * Lane D, picture editor: the hub's Pictures tab and the ways into the editor (inventory rows PE-001..PE-015).
 * Pictures are made through the REST API except where the row IS the button that makes one.
 */

// The app registers a service worker; Playwright WebKit cannot route requests it handles, so route stubs need it blocked (as 09-analysis/transcript does).
test.use({ serviceWorkers: 'block' });

test('PE-001 PE-002 PE-006 the project picker switches the Pictures list, which starts empty', async ({ page, api, pics }, testInfo) => {
  const empty = await freshProject(api, `pe001-${testInfo.project.name}`);
  const withOne = await createFull(api, pics.projectId, pics.quad, `pe001-${testInfo.project.name}-${Date.now()}`);

  await page.goto(hubUrl(empty.id));
  await expect(page.getByRole('tab', { name: 'Pictures' }), 'the Pictures tab is the selected one').toHaveAttribute('aria-selected', 'true');
  await expect(page).toHaveURL(/view=pictures/);
  await expect(page.getByText('Start a picture', { exact: true })).toBeVisible();
  await expect(page.getByText('Your pictures', { exact: true })).toBeVisible();
  await expect(page.getByText('No pictures yet'), 'a project with no pictures says so').toBeVisible();

  // Switch to the seed project through the picker: the URL and the list follow.
  await page.getByTestId('editor-project').click();
  const projectName = (await api.json<Array<{ id: string; name: string }>>('GET', '/api/v1/projects')).find((p) => p.id === pics.projectId)!.name;
  await page.getByRole('option', { name: projectName, exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`project=${pics.projectId}`));
  await expect(page).toHaveURL(/view=pictures/);
  await expect(page.getByRole('list', { name: 'Your pictures' }).getByRole('link', { name: `Open ${withOne.name}` })).toBeVisible();
  await expect(page.getByText('No pictures yet')).toHaveCount(0);
});

test('PE-003 PE-004 PE-005 start a picture from a project image: one new document, reused on the second press', async ({ page, api }, testInfo) => {
  const proj = await freshProject(api, `pe004-${testInfo.project.name}`);
  // A non-image file must never be offered.
  const fs = await import('fs');
  const path = await import('path');
  const notes = path.join('/tmp/editor-remediation/media/picture-lane', 'notes.txt');
  fs.mkdirSync(path.dirname(notes), { recursive: true });
  fs.writeFileSync(notes, 'not a picture\n');
  await api.upload(proj.id, notes, 'text/plain');
  const phone = isPhone(testInfo.project.name);

  await page.goto(hubUrl(proj.id));
  const select = page.getByRole('textbox', { name: 'Start from one of your images' }).or(page.getByRole('combobox', { name: 'Start from one of your images' })).first();
  await select.click();
  await expect(page.getByRole('option', { name: 'quad.png' }), 'the project image is offered').toBeVisible();
  await expect(page.getByRole('option', { name: 'notes.txt' }), 'a text file is never offered as an image').toHaveCount(0);
  await page.getByRole('option', { name: 'quad.png' }).click();

  const open = page.getByRole('button', { name: 'Open in the picture editor' });
  await press(open, phone);
  await expect(page).toHaveURL(/\/editor\/thumbnails\/[0-9a-f-]{36}\?project=/, { timeout: 30_000 });
  const docId = /thumbnails\/([0-9a-f-]{36})/.exec(page.url())![1];
  await expect(stageOf(page)).toBeVisible({ timeout: 45_000 });
  await expect(page.getByText('1920 × 1080', { exact: true }), 'the canvas opens at the photo size').toBeVisible();
  const created = await getFull(api, proj.id, docId);
  expect(created.layers.length, 'one layer: the original photo').toBe(1);
  expect(created.canvasWidth).toBe(1920);

  // Back to the hub (breadcrumb): the list has ONE row, and pressing the button again reuses the document.
  await page.goto(hubUrl(proj.id));
  const rows = page.getByRole('list', { name: 'Your pictures' }).getByRole('listitem');
  await expect(rows).toHaveCount(1);
  await expect(rows.first(), 'the row names the picture, its layer count and age').toContainText(/quad\.png/);
  await expect(rows.first()).toContainText('1 layer');
  await page.getByRole('textbox', { name: 'Start from one of your images' }).or(page.getByRole('combobox', { name: 'Start from one of your images' })).first().click();
  await page.getByRole('option', { name: 'quad.png' }).click();
  await press(page.getByRole('button', { name: 'Open in the picture editor' }), phone);
  await expect(page).toHaveURL(new RegExp(`/editor/thumbnails/${docId}`), { timeout: 30_000 });
  const list = await api.json<Array<{ id: string }>>('GET', picturesUrl(proj.id));
  expect(list.map((d) => d.id), 'the second press opened the same document, no copy').toEqual([docId]);

  // The row link opens the editor on that id (PE-005).
  await page.goto(hubUrl(proj.id));
  await press(page.getByRole('link', { name: /^Open quad\.png/ }), phone);
  await expect(page).toHaveURL(new RegExp(`/editor/thumbnails/${docId}`));
});

test('PE-007 a picture can be deleted from the hub, after a confirmation; the photo stays in Files', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const name = `pe007-${testInfo.project.name}-${Date.now()}`;
  const keep = await createFull(api, pics.projectId, pics.quad, `${name}-keep`);
  const doomed = await createFull(api, pics.projectId, pics.quad, name);

  await page.goto(hubUrl(pics.projectId));
  const row = page.getByRole('listitem').filter({ has: page.getByRole('link', { name: `Open ${name}`, exact: true }) });
  await expect(row).toBeVisible();
  await press(page.getByRole('button', { name: `More for ${name}`, exact: true }), phone);
  await press(page.getByRole('menuitem', { name: 'Delete picture' }), phone);

  // Cancel keeps it.
  const dialog = page.getByRole('dialog', { name: 'Delete this picture?' });
  await expect(dialog).toBeVisible();
  await press(dialog.getByRole('button', { name: 'Cancel' }), phone);
  await expect(dialog).toBeHidden();
  expect((await api.request.get(picturesUrl(pics.projectId, doomed.id))).status(), 'cancel deletes nothing').toBe(200);

  await press(page.getByRole('button', { name: `More for ${name}`, exact: true }), phone);
  await press(page.getByRole('menuitem', { name: 'Delete picture' }), phone);
  await press(page.getByRole('dialog', { name: 'Delete this picture?' }).getByRole('button', { name: 'Delete picture' }), phone);
  await expect(page.getByRole('link', { name: `Open ${name}`, exact: true })).toHaveCount(0);
  await expect.poll(async () => (await api.request.get(picturesUrl(pics.projectId, doomed.id), { failOnStatusCode: false })).status(), { message: 'the picture is gone on the server' }).toBe(404);
  expect((await api.request.get(picturesUrl(pics.projectId, keep.id))).status(), 'the other picture is untouched').toBe(200);
  expect((await api.listFiles(pics.projectId)).some((f) => f.id === pics.quad), "the photo it was made from stays in the project's files").toBe(true);
});

test('PE-008 a picture can be renamed from the hub; the name persists', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const name = `pe008-${testInfo.project.name}-${Date.now()}`;
  const pic = await createFull(api, pics.projectId, pics.quad, name);

  await page.goto(hubUrl(pics.projectId));
  await press(page.getByRole('button', { name: `More for ${name}`, exact: true }), phone);
  await press(page.getByRole('menuitem', { name: 'Rename' }), phone);
  const dialog = page.getByRole('dialog', { name: 'Rename picture' });
  await dialog.getByLabel('Picture name').fill(`${name} renamed`);
  await press(dialog.getByRole('button', { name: 'Save name' }), phone);
  await expect(page.getByRole('link', { name: `Open ${name} renamed` })).toBeVisible();
  await expect.poll(async () => (await getFull(api, pics.projectId, pic.id)).name).toBe(`${name} renamed`);
  await page.reload();
  await expect(page.getByRole('link', { name: `Open ${name} renamed` }), 'the new name survives a reload').toBeVisible();
});

test('PE-009 PE-010 "Edit as thumbnail" in Files (row menu and preview drawer) opens the picture editor, reusing the document', async ({ page, api }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const proj = await freshProject(api, `pe009-${testInfo.project.name}`);

  await page.goto(`/app/projects/${proj.id}/files`);
  await page.getByRole('tab', { name: /^Files/ }).click();
  await expect(page.getByLabel('Select quad.png')).toBeVisible({ timeout: 30_000 });
  await press(page.getByRole('button', { name: 'Actions for quad.png' }), phone);
  await press(page.getByRole('menuitem', { name: 'Edit as picture' }), phone);
  await expect(page).toHaveURL(/\/editor\/thumbnails\/[0-9a-f-]{36}\?project=/, { timeout: 30_000 });
  const docId = /thumbnails\/([0-9a-f-]{36})/.exec(page.url())![1];
  await expect(stageOf(page)).toBeVisible({ timeout: 45_000 });
  const doc = await getFull(api, proj.id, docId);
  expect(doc.sourceProjectFileId, 'the document was made from that file').toBe(proj.quad);

  // From the preview drawer: the same document.
  await page.goto(`/app/projects/${proj.id}/files`);
  await page.getByRole('tab', { name: /^Files/ }).click();
  await expect(page.getByLabel('Select quad.png')).toBeVisible({ timeout: 30_000 });
  await page.getByText('quad.png', { exact: true }).first().click();
  await press(page.getByRole('button', { name: 'Edit as thumbnail' }), phone);
  await expect(page).toHaveURL(new RegExp(`/editor/thumbnails/${docId}`), { timeout: 30_000 });
  const list = await api.json<Array<{ id: string }>>('GET', picturesUrl(proj.id));
  expect(list.map((d) => d.id), 'no second document was made').toEqual([docId]);
});

test('PE-012 PE-013 PE-014 PE-015 the editor page: breadcrumbs, missing project, Early preview label, load error', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const pic = await createFull(api, pics.projectId, pics.quad, `pe012-${testInfo.project.name}-${Date.now()}`);

  await page.goto(`/app/editor/thumbnails/${pic.id}?project=${pics.projectId}`);
  await expect(stageOf(page)).toBeVisible({ timeout: 45_000 });
  await expect(page.getByText('Early preview').first(), 'every editor surface carries the Early preview label').toBeVisible();

  // Breadcrumbs: "Pictures" lands on the Pictures tab of the same project; "Editor" on the hub.
  await press(page.getByRole('link', { name: 'Pictures', exact: true }).last(), phone);
  await expect(page).toHaveURL(new RegExp(`/editor\\?project=${pics.projectId}&view=pictures`));
  await page.goBack();
  await expect(stageOf(page)).toBeVisible({ timeout: 45_000 });
  await press(page.getByRole('link', { name: 'Editor', exact: true }).last(), phone);
  await expect(page).toHaveURL(new RegExp(`/editor\\?project=${pics.projectId}`));
  await expect(page.getByRole('tab', { name: 'Videos' })).toBeVisible();

  // No ?project=: an alert and a way to the hub.
  await page.goto(`/app/editor/thumbnails/${pic.id}`);
  await expect(page.getByText(/project/i).first()).toBeVisible();
  await press(page.getByRole('link', { name: 'Go to the Editor' }).or(page.getByRole('button', { name: 'Go to the Editor' })), phone);
  await expect(page).toHaveURL(/\/editor\?view=pictures|\/editor\?/);

  // A picture that does not exist: the load error, and "Try again" asks again (it fails again, honestly).
  await page.goto(`/app/editor/thumbnails/00000000-0000-4000-8000-000000000001?project=${pics.projectId}`);
  await expect(page.getByText('This picture could not be opened')).toBeVisible({ timeout: 30_000 });
  const again = page.waitForRequest((r) => r.url().includes('/image-documents/00000000-0000-4000-8000-000000000001') && r.method() === 'GET');
  await press(page.getByRole('button', { name: 'Try again' }), phone);
  await again;
  await expect(page.getByText('This picture could not be opened')).toBeVisible();
});
