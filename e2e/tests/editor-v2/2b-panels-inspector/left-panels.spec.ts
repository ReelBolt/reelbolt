import { test, expect } from '../../../fixtures/editor-v2/lane-b-test';
import { mediaClip, type Item, type Timeline, type Track } from '../../../fixtures/editor-v2/api';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { allSentOps, uuid, selectAt } from '../../../fixtures/editor-v2/lane-b';

/**
 * Lane B: the left panel tabs - Markers, Edits, History, Comments, Structure (inventory timeline.md 2.I,
 * TL-150..155, TL-160..162, TL-200..208, TL-215..221, TL-225..228). Every test asserts the STORED document
 * (or, for Comments, the comments API) and reloads where persistence is the point.
 */

const markers = (tl: Timeline) => tl.document.markers ?? [];

test('TL-150 TL-151 TL-152 add markers by Enter and by the Add button, jump to one', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.seek(45);
  await ed.openPanelTab(/markers/i);
  const name = ed.testId('marker-name-input');
  await name.fill('Hook');
  await name.press('Enter');
  await ed.expectServer((tl) => markers(tl).some((m) => m.name === 'Hook' && m.frame === 45), 'marker "Hook" is stored at the playhead');
  await expect(name, 'the field empties after adding').toHaveValue('');

  await ed.seek(100);
  await name.fill('Act two');
  await ed.tap(ed.testId('marker-add'));
  await ed.expectServer((tl) => markers(tl).some((m) => m.name === 'Act two' && m.frame === 100), 'marker "Act two" is stored');
  await ed.seek(100);
  await ed.tap(ed.testId('marker-add')); // no name: the default
  await ed.expectServer((tl) => markers(tl).some((m) => m.name === 'Marker'), 'an unnamed marker is called "Marker"');

  await ed.seek(0);
  await ed.tap(ed.page.getByRole('button', { name: 'Go to Hook' }));
  await expect(ed.testId('status-timecode'), 'the playhead jumps to the marker').toHaveText('00:00:01:15');
  await ed.reload();
  expect(markers(await ed.serverTimeline()).length).toBe(3);
});

test('TL-153 TL-154 rename a marker with Enter, remove it, and Undo brings it back', async ({ freshTimeline, openEditor, api, seed }) => {
  const id = await freshTimeline('seeded');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.markers = [{ id: uuid(), name: 'Draft', frame: 60, color: null, note: null, durationFrames: null }];
  });
  const ed = await openEditor(id);
  await ed.openPanelTab(/markers/i);
  const field = ed.page.getByRole('textbox', { name: 'Rename Draft' });
  await field.fill('Final');
  await field.press('Enter');
  await ed.expectServer((tl) => markers(tl)[0]?.name === 'Final', 'Enter stores the new marker name');

  await ed.tap(ed.page.getByRole('button', { name: 'Remove Final' }));
  await ed.expectServer((tl) => markers(tl).length === 0, 'the marker is removed');

  await ed.openPanelTab(/history/i);
  await ed.tap(ed.testId('history-undo'));
  await ed.expectServer((tl) => markers(tl).length === 1 && markers(tl)[0].name === 'Final', 'Undo restores the removed marker');
});

test('TL-155 a clip that carries an agent rationale shows it as a marker row and selects the clip', async ({ freshTimeline, openEditor, api, seed }) => {
  const id = await freshTimeline('seeded');
  await api.putDocument(seed.projectId, id, (doc) => {
    const v = doc.tracks.find((t) => t.kind === 'video')!;
    v.items[0].provenance = { origin: 'generated', rationale: 'Keeps the hook', editedByUser: false, agentName: 'VideoStoryEditor' };
  });
  const ed = await openEditor(id);
  await ed.openPanelTab(/markers/i);
  const row = ed.page.locator('[data-marker-kind="rationale"]').first();
  await expect(row).toContainText('Keeps the hook');
  await ed.tap(row.getByRole('button', { name: 'clip', exact: true }));
  await ed.openInspector();
  await expect(ed.testId('inspector-name'), 'the explained clip is selected').toHaveValue('clipA.mp4');
});

test('TL-160 TL-161 TL-162 the edit index filters, sorts and jumps to a clip', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.openPanelTab(/edits/i);
  const count = ed.testId('edit-index-count');
  await expect(count).toHaveText('5 / 5');
  await ed.page.getByLabel('Filter edit index').fill('clipB');
  await expect(count).toHaveText('1 / 5');
  await ed.page.getByLabel('Filter edit index').fill('');
  await expect(count).toHaveText('5 / 5');

  const names = async () => ed.page.locator('[data-testid^="edit-index-row-"] td:nth-child(3)').allInnerTexts();
  await ed.tap(ed.page.getByRole('button', { name: 'Sort by Name' }));
  const ascending = await names();
  expect(ascending).toEqual([...ascending].sort((a, b) => a.localeCompare(b)));
  await ed.tap(ed.page.getByRole('button', { name: 'Sort by Name' }));
  const descending = await names();
  expect(descending).toEqual([...ascending].reverse());

  await ed.tap(ed.page.locator('[data-testid^="edit-index-row-"]', { hasText: 'vertical.mp4' }));
  await expect(ed.testId('status-timecode'), 'the playhead is at the clip start (frame 300)').toHaveText('00:00:10:00');
  await ed.openInspector();
  await expect(ed.testId('inspector-name')).toHaveValue('vertical.mp4');
});

test('TL-225 TL-226 TL-227 TL-228 History: Undo, Redo and jumping to any entry restore the stored document', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  await selectAt(ed, 30);
  await ed.openInspector();
  const V = (tl: Timeline): Track => tl.document.tracks.find((t) => t.kind === 'video')!;
  await ed.testId('inspector-name').fill('One');
  await ed.testId('inspector-name').blur();
  await ed.expectServer((tl) => V(tl).items[0].name === 'One', 'edit 1 stored');
  await ed.testId('inspector-name').fill('Two');
  await ed.testId('inspector-name').blur();
  await ed.expectServer((tl) => V(tl).items[0].name === 'Two', 'edit 2 stored');

  await ed.openPanelTab(/history/i);
  await expect(ed.testId('history-entry-2')).toBeVisible();
  await ed.tap(ed.testId('history-undo'));
  await ed.expectServer((tl) => V(tl).items[0].name === 'One', 'Undo goes back to edit 1');
  await ed.tap(ed.testId('history-redo'));
  await ed.expectServer((tl) => V(tl).items[0].name === 'Two', 'Redo re-applies edit 2');
  await ed.tap(ed.testId('history-entry-0'));
  await ed.expectServer((tl) => V(tl).items[0].name === 'clipA.mp4', 'jumping to "Loaded document" restores the loaded name');
  await ed.tap(ed.testId('history-entry-2'));
  await ed.expectServer((tl) => V(tl).items[0].name === 'Two', 'jumping to entry 2 re-applies both edits');
  await ed.reload();
  expect(V(await ed.serverTimeline()).items[0].name).toBe('Two');
});

test('TL-215 TL-216 TL-217 TL-218 TL-220 TL-221 comments: add (point and range), seek, reply, resolve; no revision is created', async ({ freshTimeline, openEditor, api, seed }) => {
  const id = await freshTimeline('seeded');
  await api.putDocument(seed.projectId, id, (doc) => {
    (doc as unknown as { inOut: unknown }).inOut = { inFrame: 30, outFrame: 90 };
  });
  const ed = await openEditor(id);
  const revisionBefore = (await ed.serverTimeline()).revision;
  await ed.seek(60);
  await ed.openPanelTab('tab-review');
  await ed.testId('review-body').fill('Tighten this cut');
  await ed.tap(ed.testId('review-add'));
  const list = () => api.json<Array<{ id: string; body: string; frame: number; durationFrames: number | null; parentId?: string | null; resolved: boolean }>>('GET', `${api.timelinesUrl(seed.projectId, id)}/comments`);
  await expect.poll(async () => (await list()).some((c) => c.body === 'Tighten this cut' && c.frame === 60 && !c.durationFrames), { message: 'the point comment is stored at the playhead' }).toBe(true);

  await ed.testId('review-body').fill('Range note');
  await ed.page.getByLabel('Cover the marked range').check();
  await ed.tap(ed.testId('review-add'));
  await expect.poll(async () => (await list()).some((c) => c.body === 'Range note' && c.frame === 30 && c.durationFrames === 60), { message: 'the range comment covers In..Out' }).toBe(true);

  const root = (await list()).find((c) => c.body === 'Tighten this cut')!;
  await ed.seek(0);
  await ed.tap(ed.testId(`review-seek-${root.id}`));
  await expect(ed.testId('status-timecode')).toHaveText('00:00:02:00');

  await ed.tap(ed.testId(`review-reply-${root.id}`));
  await ed.testId(`review-reply-body-${root.id}`).fill('Done');
  await ed.tap(ed.testId(`review-reply-send-${root.id}`));
  await expect.poll(async () => (await list()).some((c) => c.body === 'Done' && c.parentId === root.id), { message: 'the reply is stored under the thread' }).toBe(true);

  await ed.tap(ed.testId(`review-resolve-${root.id}`));
  await expect.poll(async () => (await list()).find((c) => c.id === root.id)?.resolved, { message: 'the thread is resolved' }).toBe(true);
  await expect(ed.testId(`review-thread-${root.id}`), 'resolved threads are hidden').toHaveCount(0);
  await ed.page.getByLabel('Show resolved').check();
  await expect(ed.testId(`review-thread-${root.id}`)).toBeVisible();

  expect((await ed.serverTimeline()).revision, 'comments are not part of the document: no revision').toBe(revisionBefore);
});

test('TL-200..204 TL-202 make a compound clip from a selection, then decompose it', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  await selectAt(ed, 30);
  await ed.selectClipAt(200, 0, ['Shift']);
  await ed.openPanelTab('tab-structure');
  await ed.testId('compound-create').click();
  const made = await ed.expectServer((tl) => (tl.document.sequences ?? []).length === 1, 'a sequence is stored');
  const compound = made.document.tracks.flatMap((t) => t.items).find((i) => i.type === 'compoundClip');
  expect(compound, 'the clips were replaced by a compound clip').toBeTruthy();
  await expect.poll(() => allSentOps(ed), { message: 'setSequence was sent' }).toContain('setSequence');
  // TL-202: clicking the compound's name in its row selects it (the Inspector then shows a compound clip).
  await ed.testId(`compound-row-${compound!.id}`).getByText(/Compound clip ·/).click();
  await ed.openInspector();
  await expect(ed.testId('inspector-panel')).toContainText('Nested clip');
  // The panel frame carries no item id; the body inside it does.
  await expect(ed.testId('inspector-panel').locator(`[data-item-id="${compound!.id}"]`)).toHaveCount(1);
  await ed.openPanelTab('tab-structure');
  await ed.testId(`compound-decompose-${compound!.id}`).click();
  const back = await ed.expectServer((tl) => !tl.document.tracks.some((t) => t.items.some((i) => i.type === 'compoundClip')), 'decomposed: no compound clip remains');
  const names = back.document.tracks.flatMap((t) => t.items).map((i) => i.name);
  expect(names, 'the original clips are back').toEqual(expect.arrayContaining(['clipA.mp4', 'clipB.mp4']));
  await ed.page.keyboard.press('Control+z');
  await ed.expectServer((tl) => tl.document.tracks.some((t) => t.items.some((i) => i.type === 'compoundClip')), 'Undo brings the compound back');
});

test('TL-206 TL-207 TL-208 make a multicam from two angles with an offset, then switch angle', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop'); // selecting two clips needs a Shift-click (a phone has no multi-select gesture yet)
  const id = await freshTimeline('seeded');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks = [
      { id: uuid(), kind: 'video', name: 'V1', height: 56, locked: false, enabled: true, mute: false, solo: false, items: [mediaClip(uuid(), 'clipA.mp4', seed.files.clipA, 0, 150)] } as unknown as Track,
      { id: uuid(), kind: 'video', name: 'V2', height: 56, locked: false, enabled: true, mute: false, solo: false, items: [mediaClip(uuid(), 'clipB.mp4', seed.files.clipB, 0, 150)] } as unknown as Track,
      ...doc.tracks.filter((t) => t.kind === 'audio'),
    ];
  });
  const ed = await openEditor(id);
  await selectAt(ed, 30, 0);
  await ed.selectClipAt(30, 1, ['Shift']);
  await ed.openPanelTab('tab-structure');
  const create = ed.testId('multicam-create');
  await expect(create).toBeEnabled();
  const clipB = (await ed.serverTimeline()).document.tracks[1].items[0] as Item;
  const offset = ed.testId(`multicam-offset-${clipB.id}`);
  await offset.fill('12');
  await offset.press('Tab');
  await create.click();
  const made = await ed.expectServer((tl) => tl.document.tracks.some((t) => t.items.some((i) => i.type === 'multicamClip')), 'a multicam clip is stored');
  const mc = made.document.tracks.flatMap((t) => t.items).find((i) => i.type === 'multicamClip') as Item & { angles: Array<{ id: string; offsetFrames?: number; offset?: number }>; cuts?: unknown[] };
  expect(mc.angles.length).toBe(2);
  expect(JSON.stringify(mc.angles), 'the typed offset reached the angle').toContain('12');
  await ed.reload();
  await selectAt(ed, 30, made.document.tracks.findIndex((t) => t.items.some((i) => i.id === mc.id)));
  await ed.openPanelTab('tab-structure');
  await ed.testId('multicam-angle-2').click();
  await ed.expectServer((tl) => {
    const m = tl.document.tracks.flatMap((t) => t.items).find((i) => i.id === mc.id) as typeof mc;
    return JSON.stringify(m) !== JSON.stringify(mc);
  }, 'switching the angle changes the multicam clip');
});
