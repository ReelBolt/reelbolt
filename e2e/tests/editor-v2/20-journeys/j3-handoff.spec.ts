import * as fs from 'fs';
import * as path from 'path';
import { test, expect, type EditorPage, type Seed } from '../../../fixtures/editor-v2';
import type { Item, Timeline } from '../../../fixtures/editor-v2/api';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { TABLET, tapOn } from './journey-helpers';

/**
 * T-027 Journey 3 (Wave 2 exit): hand off. Export OTIO and EDL with the honest scope text the server states (the
 * panel's words equal GET .../export/scope, item for item), download both and read them; then import an OTIO in which
 * one clip points at a file that is not in the project: the timeline opens with an Offline marker, the clip is
 * relinked through the UI and the stored document ends with it in place.
 */

const DL_DIR = '/tmp/editor-remediation/journeys-downloads';
const MEDIA_SENTENCE = {
  'reelbolt-url': 'Your media is not inside the file. It points at the files in this project by link, so the other app needs access to ReelBolt, or you relink the media there.',
  none: 'The file names no media, so it opens anywhere.',
} as const;

type Scope = { format: string; carries: string[]; dropped: string[]; mediaMode: keyof typeof MEDIA_SENTENCE };
type OtioClip = { OTIO_SCHEMA: string; name: string; media_reference?: { target_url?: string }; source_range?: { duration: { value: number }; start_time: { value: number } } };
type Otio = { OTIO_SCHEMA: string; name: string; metadata?: unknown; tracks: { children: Array<{ kind: string; children: OtioClip[] }> } };

const videoOf = (tl: Timeline): Item[] => tl.document.tracks.filter((t) => t.kind === 'video').flatMap((t) => t.items);

async function exportAs(ed: EditorPage, seed: Seed, format: 'otio' | 'edl', label: string, touch: boolean): Promise<{ file: string; scope: Scope }> {
  await tapOn(ed, ed.testId('export-scope-format'), touch);
  await tapOn(ed, ed.page.getByRole('option', { name: label, exact: true }), touch);
  const scope = await seed.api.json<Scope>('GET', `${seed.api.timelinesUrl(seed.projectId, ed.timelineId)}/export/scope?format=${format}`);
  expect(scope.carries.length, `${format}: the server states what the file carries`).toBeGreaterThan(0);

  // The panel says exactly what the server says: every "carries" and "leaves out" line, and the media sentence.
  await expect(ed.testId('export-scope-carries').locator('li'), `${format}: the carries list is the server's`).toHaveText(scope.carries);
  if (scope.dropped.length > 0) await expect(ed.testId('export-scope-dropped').locator('li'), `${format}: the leaves-out list is the server's`).toHaveText(scope.dropped);
  else await expect(ed.testId('export-scope')).toContainText('Nothing from the timeline.');
  await expect(ed.testId('export-scope-media'), `${format}: the media sentence follows the server's media mode (${scope.mediaMode})`).toHaveText(MEDIA_SENTENCE[scope.mediaMode]);

  const [download] = await Promise.all([ed.page.waitForEvent('download'), tapOn(ed, ed.testId(`deliver-export-${format}`), touch)]);
  fs.mkdirSync(DL_DIR, { recursive: true });
  const file = path.join(DL_DIR, `j3-${Date.now()}-${download.suggestedFilename()}`);
  await download.saveAs(file);
  expect(download.suggestedFilename()).toMatch(new RegExp(`\\.${format}$`));
  expect(fs.statSync(file).size, `${format} downloads a non-empty file`).toBeGreaterThan(50);
  return { file, scope };
}

async function journey3(ed: EditorPage, seed: Seed, touch: boolean): Promise<void> {
  const { page, timelineId } = ed;
  const original = await ed.serverTimeline();
  const pictures = videoOf(original);
  expect(pictures, 'the seeded timeline has four pictures').toHaveLength(4);

  // 1. Deliver > Export for other apps: OTIO then EDL.
  await tapOn(ed, page.getByTestId('workspace-deliver'), touch);
  await expect(ed.testId('export-panel')).toBeVisible();
  const otio = await exportAs(ed, seed, 'otio', 'OpenTimelineIO', touch);
  await expect(ed.testId('export-scope-summary')).toContainText('lossless');
  const edl = await exportAs(ed, seed, 'edl', 'CMX3600 EDL', touch);
  await expect(ed.testId('export-scope-summary')).toContainText('Cuts only');
  expect(edl.scope.carries.join(' '), 'the EDL scope says it is cuts').toMatch(/cut|clip|event/i);
  expect(edl.scope.dropped.length, 'the EDL admits what it leaves out').toBeGreaterThan(0);

  // The downloaded files hold what the scope says: every picture is an OTIO clip / an EDL event.
  const otioDoc = JSON.parse(fs.readFileSync(otio.file, 'utf8')) as Otio;
  expect(otioDoc.OTIO_SCHEMA).toMatch(/^Timeline\./);
  const videoTrack = otioDoc.tracks.children.find((t) => t.kind === 'Video')!;
  const clips = videoTrack.children.filter((c) => c.OTIO_SCHEMA.startsWith('Clip'));
  expect(clips.map((c) => c.name), 'the OTIO lists the four pictures in order').toEqual(pictures.map((p) => p.name));
  expect(clips.map((c) => c.source_range?.duration.value), 'with their lengths').toEqual(pictures.map((p) => p.recordOut - p.recordIn));
  const edlText = fs.readFileSync(edl.file, 'utf8');
  expect(edlText, 'the EDL is a CMX3600 list').toMatch(/^TITLE:/m);
  expect((edlText.match(/^\d{3,}\s/gm) ?? []).length, 'one EDL event per picture').toBe(pictures.length);

  // 2. Import the OTIO with ONE clip pointing at a file this project does not have.
  const gone = pictures[2];
  const broken = structuredClone(otioDoc);
  // ReelBolt's own file embeds the whole document and re-imports losslessly; what another tool hands back has no such block.
  delete broken.metadata;
  const bt = broken.tracks.children.find((t) => t.kind === 'Video')!;
  const ghost = bt.children.find((c) => c.name === gone.name)!;
  // Neither the project-file link nor the file name may resolve: a link to a file that is not in this project, under another name.
  ghost.media_reference = { ...(ghost.media_reference ?? {}), target_url: `reelbolt://project-file/${crypto.randomUUID()}`, name: 'gone-forever-not-in-this-project.mp4' } as OtioClip['media_reference'];
  const brokenFile = path.join(DL_DIR, `j3-broken-${Date.now()}.otio`);
  fs.writeFileSync(brokenFile, JSON.stringify(broken));
  await page.goto(`/app/editor?project=${seed.projectId}`);
  await expect(page.getByTestId('timelines-table')).toBeVisible({ timeout: 30_000 });
  const [chooser] = await Promise.all([page.waitForEvent('filechooser'), touch ? page.getByTestId('timeline-import').tap() : page.getByTestId('timeline-import').click()]);
  await chooser.setFiles(brokenFile);
  await expect(page).toHaveURL(new RegExp(`/app/editor/${seed.projectId}/timeline/[0-9a-f-]{36}`), { timeout: 45_000 });
  const importedId = page.url().split('/timeline/')[1].split(/[?#]/)[0];
  expect(importedId, 'the import made a new timeline').not.toBe(timelineId);
  try {
    // 3. The Offline marker names the missing clip; the other three arrived.
    await expect(page.getByTestId('offline-banner')).toBeVisible({ timeout: 45_000 });
    await expect(page.getByTestId('offline-message')).toContainText(`1 clip could not be found in this project: ${gone.name}`);
    const imported = await seed.api.getTimeline(seed.projectId, importedId);
    expect((imported.document.markers ?? []).map((m) => m.name), 'one Offline marker, named after the clip').toEqual([`Offline: ${gone.name}`]);
    const arrived = videoOf(imported);
    expect(arrived.map((i) => [i.name, i.recordIn, i.recordOut]), 'the three found clips are where they were').toEqual(
      pictures.filter((p) => p.id !== gone.id).map((p) => [p.name, p.recordIn, p.recordOut]),
    );
    const markerFrame = (imported.document.markers ?? [])[0].frame;
    expect(markerFrame, 'the marker sits where the missing clip was').toBe(gone.recordIn);

    // 4. Relink through the UI.
    await tapOn(ed, page.getByTestId('import-close'), touch); // T-002: the import result sheet opens first
    await tapOn(ed, page.getByTestId(`offline-relink-${gone.name}`), touch);
    await tapOn(ed, page.getByTestId('offline-source'), touch);
    await tapOn(ed, page.getByRole('option', { name: 'vertical.mp4', exact: true }), touch);
    await tapOn(ed, page.getByTestId('offline-confirm'), touch);
    await expect.poll(async () => {
      const t = await seed.api.getTimeline(seed.projectId, importedId);
      return JSON.stringify({ offline: (t.document.markers ?? []).filter((m) => m.name.startsWith('Offline: ')).length, relinked: videoOf(t).some((i) => i.assetRef?.projectFileId === seed.files.vertical) });
    }, { message: 'the clip is relinked and the marker is gone', timeout: 30_000 }).toBe(JSON.stringify({ offline: 0, relinked: true }));

    // The document ends with the clip where the marker was, and the whole film reads like the original.
    const done = await seed.api.getTimeline(seed.projectId, importedId);
    const finalPictures = videoOf(done);
    expect(finalPictures.map((i) => [i.recordIn, i.assetRef?.projectFileId]), 'A, B, the relinked clip, then the still: the same order and places as the original').toEqual(
      pictures.map((p) => [p.recordIn, p.assetRef?.projectFileId]),
    );
    expect(finalPictures.find((i) => i.assetRef?.projectFileId === seed.files.vertical)!.recordIn, 'the relinked clip is at the marker').toBe(markerFrame);
    await expect(page.getByTestId('offline-banner')).toHaveCount(0);
    await page.reload();
    // The editor reopens on the remembered page (Deliver, where the hand-off began), so go to Edit to see the timeline.
    await tapOn(ed, page.getByTestId('workspace-edit'), touch);
    await expect(page.getByTestId('timeline-canvas')).toBeVisible({ timeout: 45_000 });
    await expect(page.getByTestId('offline-banner'), 'after a reload nothing is offline').toHaveCount(0);
    expect(videoOf(await seed.api.getTimeline(seed.projectId, importedId)).some((i) => i.assetRef?.projectFileId === seed.files.vertical)).toBe(true);
  } finally {
    await seed.api.json('DELETE', `${seed.api.timelinesUrl(seed.projectId, importedId)}`, undefined, [200, 204, 404]);
  }
}

test.describe('J3 hand off', () => {
  test('J3 desktop: export OTIO and EDL with the server\'s scope text, import with a missing file, relink', async ({ freshTimeline, openEditor, seed }) => {
    runOn('desktop');
    test.setTimeout(420_000);
    await journey3(await openEditor(await freshTimeline('seeded')), seed, false);
  });

  test.describe('tablet 1024x768, touch, coarse pointer', () => {
    test.use({ viewport: TABLET, hasTouch: true });
    test('J3 tablet: the same hand off by touch', async ({ page, freshTimeline, openEditor, seed }) => {
      runOn('desktop');
      test.setTimeout(420_000);
      const ed = await openEditor(await freshTimeline('seeded'));
      expect(await page.evaluate(() => window.matchMedia('(pointer: coarse)').matches), 'a coarse pointer').toBe(true);
      await journey3(ed, seed, true);
    });
  });
});
