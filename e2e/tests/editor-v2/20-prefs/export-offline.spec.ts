import * as fs from 'fs';
import * as path from 'path';
import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { lightApi } from '../../../fixtures/editor-v2/ws';

/** R12.1 export panel scope + hand off + real downloads; R12.2 (journey 3) import with offline media and Relink. */

const FORMATS = [
  { id: 'otio', ext: 'otio' }, { id: 'edl', ext: 'edl' }, { id: 'fcpxml', ext: 'fcpxml' }, { id: 'srt', ext: 'srt' }, { id: 'vtt', ext: 'vtt' },
] as const;

async function withCaptions(freshTimeline: (f: 'seeded') => Promise<string>, api: import('../../../fixtures/editor-v2/api').EditorApi, projectId: string): Promise<string> {
  const id = await freshTimeline('seeded');
  await api.putDocument(projectId, id, (doc) => {
    doc.tracks.push({
      id: crypto.randomUUID(), kind: 'video', name: 'Subtitles', height: 40, locked: false, enabled: true, mute: false, solo: false,
      items: [{ type: 'caption', id: crypto.randomUUID(), name: 'c1', recordIn: 0, recordOut: 60, text: 'Hello there', language: 'en', enabled: true }],
    });
  });
  return id;
}

for (const viewport of [null, { width: 1024, height: 768 }] as const) {
  test.describe(viewport ? 'tablet (coarse pointer, 1024x768)' : 'desktop', () => {
    if (viewport) test.use({ viewport, hasTouch: true });
    test(`R12.1 ${viewport ? 'tablet ' : ''}the export panel says what each format carries and every format downloads a non-empty file`, async ({ freshTimeline, openEditor, api, seed }) => {
      runOn('desktop');
      const ed = await openEditor(await withCaptions(freshTimeline, api, seed.projectId));
      await ed.workspace('deliver');
      await expect(ed.testId('export-panel')).toBeVisible();
      for (const f of FORMATS) {
        await ed.tap(ed.testId('export-scope-format'));
        await ed.tap(ed.page.getByRole('option', { name: { otio: 'OpenTimelineIO', edl: 'CMX3600 EDL', fcpxml: 'Final Cut Pro XML', srt: 'SRT captions', vtt: 'WebVTT captions' }[f.id], exact: true }));
        await expect(ed.testId('export-scope-carries').locator('li').first(), `${f.id}: the carries list comes from the server`).toBeVisible();
        await expect(ed.testId('export-scope-media')).not.toBeEmpty();
        await expect(ed.testId('export-handoff')).toContainText('Hand off:');
        if (f.id === 'edl') await expect(ed.testId('export-scope-summary')).toContainText('Cuts only');
        const [download] = await Promise.all([ed.page.waitForEvent('download'), ed.tap(ed.testId(`deliver-export-${f.id}`))]);
        const file = test.info().outputPath(`export.${f.ext}`);
        await download.saveAs(file);
        expect(fs.statSync(file).size, `${f.id} downloads a non-empty file`).toBeGreaterThan(20);
        expect(download.suggestedFilename()).toMatch(new RegExp(`\\.${f.ext}$`));
      }
    });
  });
}

test('R12.2 importing an OTIO with missing media opens the timeline with an Offline marker and Relink places a project file there in one undo step', async ({ page, seed, api }) => {
  runOn('desktop');
  test.setTimeout(180_000);
  const light = lightApi(page);
  const ghost = (name: string, url: string) => ({
    OTIO_SCHEMA: 'Clip.1', name, media_reference: { OTIO_SCHEMA: 'ExternalReference.1', target_url: url },
    source_range: { OTIO_SCHEMA: 'TimeRange.1', start_time: { OTIO_SCHEMA: 'RationalTime.1', value: 0, rate: 30 }, duration: { OTIO_SCHEMA: 'RationalTime.1', value: 30, rate: 30 } },
  });
  const otio = { OTIO_SCHEMA: 'Timeline.1', name: 'offline-journey', tracks: { OTIO_SCHEMA: 'Stack.1', children: [{ OTIO_SCHEMA: 'Track.1', kind: 'Video', children: [ghost('ghost-one', 'ghost-one-gone.mp4'), ghost('ghost-two', 'ghost-two-gone.mp4')] }] } };
  const file = test.info().outputPath('offline.otio');
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify(otio));

  await page.goto(`/app/editor?project=${seed.projectId}`);
  await expect(page.getByTestId('timelines-table')).toBeVisible({ timeout: 30_000 });
  const [chooser] = await Promise.all([page.waitForEvent('filechooser'), page.getByTestId('timeline-import').click()]);
  await chooser.setFiles(file);
  await expect(page).toHaveURL(new RegExp(`/app/editor/${seed.projectId}/timeline/[0-9a-f-]{36}`), { timeout: 30_000 });
  const id = page.url().split('/timeline/')[1].split(/[?#]/)[0];
  try {
    await expect(page.getByTestId('offline-banner')).toBeVisible({ timeout: 45_000 });
    await expect(page.getByTestId('offline-message')).toContainText('2 clips could not be found in this project: ghost-one, ghost-two');
    const stored = await light.getTimeline(seed.projectId, id);
    expect((stored.document.markers ?? []).filter((m) => m.name.startsWith('Offline: ')).map((m) => m.name)).toEqual(['Offline: ghost-one', 'Offline: ghost-two']);

    await page.getByTestId('import-close').click(); // T-002: the result sheet opens first
    await page.getByTestId('offline-relink-ghost-one').click();
    await page.getByTestId('offline-source').click();
    await page.getByRole('option', { name: 'clipA.mp4' }).click();
    await page.getByTestId('offline-confirm').click();

    await expect.poll(async () => {
      const t = await light.getTimeline(seed.projectId, id);
      const items = t.document.tracks.flatMap((tr) => tr.items);
      const placed = items.find((i) => i.assetRef?.projectFileId === seed.files.clipA);
      return JSON.stringify({ at: placed?.recordIn, offline: (t.document.markers ?? []).map((m) => m.name) });
    }, { message: 'the file sits at the marker (frame 0) and only the other Offline marker is left', timeout: 30_000 }).toBe(JSON.stringify({ at: 0, offline: ['Offline: ghost-two'] }));
    await expect(page.getByTestId('offline-message')).toContainText('1 clip could not be found in this project: ghost-two');

    await page.getByTestId('undo').click();
    await expect.poll(async () => {
      const t = await light.getTimeline(seed.projectId, id);
      return (t.document.markers ?? []).filter((m) => m.name.startsWith('Offline: ')).length;
    }, { message: 'one undo brings the marker back' }).toBe(2);
  } finally {
    await api.json('DELETE', `/api/v1/projects/${seed.projectId}/edit-timelines/${id}`, undefined, [200, 204]);
  }
});
