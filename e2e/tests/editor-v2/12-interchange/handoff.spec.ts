import * as fs from 'fs';
import * as path from 'path';
import * as zlib from 'zlib';
import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { lightApi, withTempProject } from '../../../fixtures/editor-v2/ws';
import { MEDIA_DIR } from '../../../fixtures/editor-v2/env';

/**
 * T-002 (Wave 4): R12.2 the hand-off package and R12.3 the import result sheet. Desktop only; the phone note
 * is asserted on pixel7. Pending run (build-only window).
 */

interface ZipEntry { name: string; size: number; data: () => Buffer }

/** A tiny zip reader: the central directory gives names and sizes (a streamed zip uses data descriptors). */
function readZip(buf: Buffer): ZipEntry[] {
  let eocd = -1;
  for (let i = buf.length - 22; i >= 0; i--) if (buf.readUInt32LE(i) === 0x06054b50) { eocd = i; break; }
  expect(eocd, 'the response is a zip').toBeGreaterThanOrEqual(0);
  const count = buf.readUInt16LE(eocd + 10);
  let p = buf.readUInt32LE(eocd + 16);
  const out: ZipEntry[] = [];
  for (let n = 0; n < count; n++) {
    expect(buf.readUInt32LE(p)).toBe(0x02014b50);
    const method = buf.readUInt16LE(p + 10);
    const comp = buf.readUInt32LE(p + 20);
    const size = buf.readUInt32LE(p + 24);
    const nameLen = buf.readUInt16LE(p + 28);
    const extraLen = buf.readUInt16LE(p + 30);
    const commentLen = buf.readUInt16LE(p + 32);
    const local = buf.readUInt32LE(p + 42);
    const name = buf.toString('utf8', p + 46, p + 46 + nameLen);
    const start = local + 30 + buf.readUInt16LE(local + 26) + buf.readUInt16LE(local + 28);
    out.push({ name, size, data: () => (method === 0 ? buf.subarray(start, start + comp) : zlib.inflateRawSync(buf.subarray(start, start + comp))) });
    p += 46 + nameLen + extraLen + commentLen;
  }
  return out;
}

const ghostOtio = (name: string, url: string, name2?: string) => ({
  OTIO_SCHEMA: 'Timeline.1', name, tracks: { OTIO_SCHEMA: 'Stack.1', children: [{ OTIO_SCHEMA: 'Track.1', kind: 'Video', children: [{
    OTIO_SCHEMA: 'Clip.1', name: name2 ?? url, media_reference: { OTIO_SCHEMA: 'ExternalReference.1', target_url: url },
    source_range: { OTIO_SCHEMA: 'TimeRange.1', start_time: { OTIO_SCHEMA: 'RationalTime.1', value: 0, rate: 30 }, duration: { OTIO_SCHEMA: 'RationalTime.1', value: 30, rate: 30 } },
  }] }] },
});

test('R12.2 downloads a hand-off zip with the timeline file, a media folder and a manifest', async ({ page, seed, freshTimeline, openEditor }) => {
  runOn('desktop');
  const id = await freshTimeline('seeded');
  const res = await page.request.get(`/api/v1/projects/${seed.projectId}/edit-timelines/${id}/handoff?format=otio&media=zip`);
  expect(res.ok(), 'the hand-off answers 200').toBeTruthy();
  const body = await res.body();
  expect(body.length).toBeGreaterThan(1000);
  const entries = readZip(body);
  const names = entries.map((e) => e.name);
  expect(names.some((n) => /\.otio$/.test(n) && !n.includes('/')), 'the timeline file is at the top').toBeTruthy();
  const media = entries.filter((e) => e.name.startsWith('media/'));
  expect(media.length, 'a media folder with the video files').toBeGreaterThan(0);
  for (const m of media) expect(m.size, `${m.name} has bytes`).toBeGreaterThan(0);
  expect(names).toContain('README.txt');
  expect(names[names.length - 1], 'the manifest is written last').toBe('manifest.json');
  const otio = entries.find((e) => e.name.endsWith('.otio'))!.data().toString('utf8');
  const urls = [...otio.matchAll(/"target_url"\s*:\s*"([^"]+)"/g)].map((m) => decodeURIComponent(m[1]));
  expect(urls.length).toBeGreaterThan(0);
  for (const u of urls) expect(media.map((m) => m.name), `${u} names a media entry`).toContain(u);

  // The same through the UI: one obvious button in Deliver, a real download.
  const ed = await openEditor(id);
  await ed.workspace('deliver');
  await expect(ed.testId('handoff-card')).toBeVisible();
  await expect(ed.testId('handoff-size')).toContainText('file');
  const [download] = await Promise.all([page.waitForEvent('download'), ed.tap(ed.testId('handoff-download'))]);
  const file = test.info().outputPath('hand-off.zip');
  await download.saveAs(file);
  expect(fs.statSync(file).size).toBeGreaterThan(1000);
  expect(download.suggestedFilename()).toMatch(/-hand-off\.zip$/);
});

test('R12.2 list-files-only has no media entries', async ({ page, seed, freshTimeline }) => {
  runOn('desktop');
  const id = await freshTimeline('seeded');
  const res = await page.request.get(`/api/v1/projects/${seed.projectId}/edit-timelines/${id}/handoff?format=otio&media=manifest`);
  expect(res.ok()).toBeTruthy();
  const entries = readZip(await res.body());
  expect(entries.filter((e) => e.name.startsWith('media/')), 'no media in the package').toHaveLength(0);
  const manifest = JSON.parse(entries.find((e) => e.name === 'manifest.json')!.data().toString('utf8')) as { mediaIncluded: boolean; files: Array<{ status: string }> };
  expect(manifest.mediaIncluded).toBe(false);
  expect(manifest.files.length).toBeGreaterThan(0);
  expect(manifest.files.every((f) => f.status === 'notIncluded')).toBe(true);
});

test('R12.2 on a phone the card says hand-off is a desktop job and offers no button', async ({ openEditor, freshTimeline }) => {
  runOn('pixel7');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.workspace('deliver');
  await expect(ed.testId('handoff-phone-note')).toContainText('desktop job');
  await expect(ed.testId('handoff-download')).toHaveCount(0);
});

test('R12.3 importing a file with an unknown name leaves an offline marker and the result sheet says why', async ({ page, seed }) => {
  runOn('desktop');
  const light = lightApi(page);
  const file = test.info().outputPath('unknown.otio');
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify(ghostOtio('unknown-name', 'no-such-file-anywhere.mp4')));
  await page.goto(`/app/editor?project=${seed.projectId}`);
  await expect(page.getByTestId('timelines-table')).toBeVisible({ timeout: 30_000 });
  const [chooser] = await Promise.all([page.waitForEvent('filechooser'), page.getByTestId('timeline-import').click()]);
  await chooser.setFiles(file);
  await expect(page).toHaveURL(new RegExp(`/app/editor/${seed.projectId}/timeline/[0-9a-f-]{36}`), { timeout: 30_000 });
  const id = page.url().split('/timeline/')[1].split(/[?#]/)[0];
  try {
    await expect(page.getByRole('dialog')).toBeVisible({ timeout: 45_000 });
    await expect(page.getByTestId('import-reason-no-such-file-anywhere.mp4')).toHaveText('No file with this name in the project');
    const stored = await light.getTimeline(seed.projectId, id);
    expect((stored.document.markers ?? []).map((m) => m.name), 'the stored document carries the offline marker').toEqual(['Offline: no-such-file-anywhere.mp4']);
    await page.getByTestId('import-close').click();
    await expect(page.getByTestId('offline-reason-no-such-file-anywhere.mp4')).toContainText('No file with this name');
  } finally {
    await light.json('DELETE', `/api/v1/projects/${seed.projectId}/edit-timelines/${id}`, undefined, [200, 204]);
  }
});

test('R12.3 ambiguous names offer a picker and relinking stores the clip', async ({ page, api }) => {
  runOn('desktop');
  test.setTimeout(180_000);
  await withTempProject(api, 'handoff-ambiguous', async (project) => {
    const a = await api.upload(project.id, path.join(MEDIA_DIR, 'clipA.mp4'), 'video/mp4', 'dup.mp4');
    const b = await api.upload(project.id, path.join(MEDIA_DIR, 'clipB.mp4'), 'video/mp4', 'dup.mp4');
    const file = test.info().outputPath('ambiguous.otio');
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, JSON.stringify(ghostOtio('ambiguous', 'dup.mp4')));
    await page.goto(`/app/editor?project=${project.id}`);
    await expect(page.getByTestId('timelines-table').or(page.getByTestId('timeline-import')).first()).toBeVisible({ timeout: 30_000 });
    const [chooser] = await Promise.all([page.waitForEvent('filechooser'), page.getByTestId('timeline-import').first().click()]);
    await chooser.setFiles(file);
    await expect(page).toHaveURL(new RegExp(`/app/editor/${project.id}/timeline/[0-9a-f-]{36}`), { timeout: 30_000 });
    const id = page.url().split('/timeline/')[1].split(/[?#]/)[0];
    await expect(page.getByTestId('import-reason-dup.mp4')).toHaveText('Two files share this name; pick one', { timeout: 45_000 });
    await page.getByTestId('import-relink-dup.mp4').click();
    await expect(page.getByTestId('offline-candidates')).toBeVisible();
    const chosen = a < b ? b : a;
    await page.getByTestId(`offline-candidate-${chosen}`).click();
    await page.getByTestId('offline-confirm').click();
    await expect.poll(async () => {
      const t = await api.getTimeline(project.id, id);
      const items = t.document.tracks.flatMap((tr) => tr.items);
      return JSON.stringify({ file: items.find((i) => i.assetRef?.projectFileId)?.assetRef?.projectFileId, offline: (t.document.markers ?? []).length });
    }, { message: 'the chosen file is stored at the marker and the Offline marker is gone', timeout: 30_000 }).toBe(JSON.stringify({ file: chosen, offline: 0 }));
  });
});
