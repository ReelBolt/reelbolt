import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import { selectAt } from '../../../fixtures/editor-v2/lane-b';
import { startRender, waitRender } from '../../../fixtures/editor-v2/rows';

/**
 * Wave 1 (T-008): the Versions panel. Versions are created by real causes (a named version from the button, a render
 * start, a restore), listed in plain words with their retention, and EVERY listed version restores to its stored document.
 */

type Version = { revision: number; name?: string; cause: string; createdAt: string; expiresAt?: string };

async function openVersions(ed: EditorPage): Promise<void> {
  await ed.openInspector();
  await ed.tap(ed.page.getByRole('tab', { name: /versions/i }).first());
  await expect(ed.testId('revision-history-panel')).toBeVisible();
}

async function refresh(ed: EditorPage): Promise<void> {
  await ed.tap(ed.testId('revision-history-panel').getByRole('button', { name: 'Refresh' }));
}

async function renameFirstClip(ed: EditorPage, name: string): Promise<void> {
  await selectAt(ed, 30);
  await ed.openInspector();
  await ed.tap(ed.page.getByRole('tab', { name: /inspector/i }).first());
  await ed.testId('inspector-name').fill(name);
  await ed.testId('inspector-name').blur();
  await ed.expectServer((t) => t.document.tracks[0].items[0].name === name, `the edit "${name}" is stored`);
}

test('R01.2 R02.4 versions: named, before-render and before-restore points are listed in plain words, and every row restores', async ({ seed, api, freshTimeline, openEditor }) => {
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  const versions = () => api.json<{ versions: Version[] }>('GET', `${api.timelinesUrl(seed.projectId, id)}/versions`).then((r) => r.versions);
  const storedName = async (revision: number) =>
    (await api.json<{ document: { tracks: Array<{ items: Array<{ name: string }> }> } }>('GET', `${api.timelinesUrl(seed.projectId, id)}/revisions/${revision}`)).document.tracks[0].items[0].name;

  // 1. A named version from the button.
  await renameFirstClip(ed, 'Edit A');
  await openVersions(ed);
  await expect(ed.testId('versions-rule')).toHaveText('Auto-saved checkpoints are kept for 30 days. Name a version to keep it forever.');
  await ed.tap(ed.testId('version-save-named'));
  await ed.testId('version-name-input').fill('Version A');
  await ed.tap(ed.testId('version-name-save'));
  let named!: Version;
  await expect.poll(async () => (named = (await versions()).find((v) => v.name === 'Version A')!) !== undefined, { message: 'the named version is stored' }).toBe(true);
  expect(named.cause).toBe('named');
  expect(named.expiresAt, 'a named version never expires').toBeUndefined();
  await expect(ed.testId(`version-title-${named.revision}`)).toHaveText('Version A');
  await expect(ed.testId(`version-retention-${named.revision}`)).toHaveText('kept forever');

  // 2. A render start takes a "before a render" point of the revision it pinned.
  await renameFirstClip(ed, 'Edit B');
  const pinned = await startRender(api, seed.projectId, id);
  let rendered!: Version;
  await expect.poll(async () => (rendered = (await versions()).find((v) => v.cause === 'beforeRender')!) !== undefined, { message: 'a render start stores a version' }).toBe(true);
  expect(rendered.expiresAt, 'an automatic point expires').toBeTruthy();
  await waitRender(api, seed.projectId, id, pinned.executionId); // never leave a Running render behind: it holds the org's execution slot for the next spec

  // 3. Restoring Version A asks first, stores the stored document as a new revision and keeps the state it replaced.
  await renameFirstClip(ed, 'Edit C'); // a revision with no point of its own yet, so the restore has something to keep
  await openVersions(ed);
  await refresh(ed);
  const before = await ed.serverTimeline();
  await ed.tap(ed.testId(`revision-restore-${named.revision}`));
  await expect(ed.testId('version-restore-text')).toContainText('Before you restored');
  expect((await ed.serverTimeline()).revision, 'asking changed nothing').toBe(before.revision);
  await ed.tap(ed.testId(`revision-restore-confirm-${named.revision}`));
  await ed.expectServer((t) => t.document.tracks[0].items[0].name === 'Edit A' && t.revision > before.revision, 'Version A is back as a new revision');
  let restoredPoint!: Version;
  await expect.poll(async () => (restoredPoint = (await versions()).find((v) => v.cause === 'beforeRestore')!) !== undefined, { message: 'the replaced state is kept' }).toBe(true);

  // 4. The list says each cause in plain words with the right retention.
  await refresh(ed);
  await expect(ed.testId(`version-title-${rendered.revision}`)).toBeVisible();
  await expect(ed.testId(`version-meta-${rendered.revision}`)).toContainText('Before a render').catch(async () => {
    await expect(ed.testId(`version-title-${rendered.revision}`)).toHaveText('Before a render');
  });
  await expect(ed.testId(`version-retention-${rendered.revision}`)).toHaveText(/^deleted in (29|30) days$/);
  await expect(ed.testId(`version-title-${restoredPoint.revision}`)).toHaveText('Before you restored');

  // 5. EVERY listed version restores: the stored document comes back as a new revision.
  const listed = (await versions()).map((v) => v.revision);
  expect(listed.length).toBeGreaterThanOrEqual(3);
  for (const revision of listed) {
    const prior = (await ed.serverTimeline()).revision;
    const expected = await storedName(revision);
    await refresh(ed);
    await ed.tap(ed.testId(`revision-restore-${revision}`));
    await ed.tap(ed.testId(`revision-restore-confirm-${revision}`));
    await ed.expectServer((t) => t.revision > prior && t.document.tracks[0].items[0].name === expected, `version ${revision} restored its stored document`);
  }
});

test('R01.2 a version can be named, renamed and have its name removed', async ({ seed, api, freshTimeline, openEditor }) => {
  const id = await freshTimeline('seeded');
  // Revision 1 (the original) is never deleted, so name a later point: an API edit, then a named version of it.
  await api.putDocument(seed.projectId, id, (doc) => { doc.name = 'second revision'; });
  const made = await api.json<Version>('POST', `${api.timelinesUrl(seed.projectId, id)}/versions`, { name: 'temp', cause: 'named' }, [200, 201]);
  const ed = await openEditor(id);
  const versions = () => api.json<{ versions: Version[] }>('GET', `${api.timelinesUrl(seed.projectId, id)}/versions`).then((r) => r.versions);
  await openVersions(ed);
  const first = (await versions()).find((v) => v.revision === made.revision)!;
  const ours = (v: Version | undefined) => v?.name;

  await ed.tap(ed.testId(`version-name-${first.revision}`));
  await ed.testId('version-name-input').fill('Keep this');
  await ed.tap(ed.testId('version-name-save'));
  await expect.poll(async () => ours((await versions()).find((v) => v.revision === first.revision))).toBe('Keep this');
  await expect(ed.testId(`version-retention-${first.revision}`)).toHaveText('kept forever');

  await ed.tap(ed.testId(`version-name-${first.revision}`));
  await ed.testId('version-name-input').fill('Kept, renamed');
  await ed.tap(ed.testId('version-name-save'));
  await expect.poll(async () => ours((await versions()).find((v) => v.revision === first.revision))).toBe('Kept, renamed');

  await ed.tap(ed.testId(`version-unname-${first.revision}`));
  await expect.poll(async () => (await versions()).find((v) => v.revision === first.revision)?.name).toBeUndefined();
  await expect(ed.testId(`version-retention-${first.revision}`)).toHaveText(/^deleted in/);
});
