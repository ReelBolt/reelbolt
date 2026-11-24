import * as fs from 'fs';
import { test, expect, createFull, getFull, layerOf, decodeImage, saveDownload, addBadgeLayer } from '../../../fixtures/editor-v2/picture-lane';
import { picturesUrl } from '../../../fixtures/editor-v2/picture';

/** Lane D, picture editor: the document API the editor relies on (inventory rows PE-200..PE-207; PE-206 is in ai.spec.ts). Desktop only: no page. */

// The app registers a service worker; Playwright WebKit cannot route requests it handles, so route stubs need it blocked (as 09-analysis/transcript does).
test.use({ serviceWorkers: 'block' });

test.beforeEach(() => {
  test.skip(test.info().project.name !== 'desktop', 'API contract rows run once, on desktop');
});

test('PE-200 PE-201 creating a picture makes a document with the photo as layer 1; the list is newest first with layer counts', async ({ api, pics }) => {
  const a = await createFull(api, pics.projectId, pics.quad, `pe200a-${Date.now()}`);
  const b = await createFull(api, pics.projectId, pics.quad, `pe200b-${Date.now()}`);
  expect([a.canvasWidth, a.canvasHeight], 'the canvas is the photo size').toEqual([1920, 1080]);
  expect(a.layers).toHaveLength(1);
  expect(a.layers[0].kind.toLowerCase()).toBe('source');
  const list = await api.json<Array<{ id: string; layerCount: number; updatedAt: string }>>('GET', picturesUrl(pics.projectId));
  const ids = list.map((d) => d.id);
  expect(ids.indexOf(b.id), 'newest first').toBeLessThan(ids.indexOf(a.id));
  expect(list.find((d) => d.id === a.id)!.layerCount).toBe(1);
  const nonImage = await api.request.post(picturesUrl(pics.projectId), { data: { sourceProjectFileId: pics.notes, name: 'x' }, failOnStatusCode: false });
  expect([400, 409, 422], 'a text file cannot be a picture source').toContain(nonImage.status());
});

test('PE-202 the document patch: canvas, background and name; a stale baseRevision is a 409 and writes nothing', async ({ api, pics }) => {
  const pic = await createFull(api, pics.projectId, pics.quad, `pe202-${Date.now()}`);
  const url = picturesUrl(pics.projectId, pic.id);
  const ok = await api.json<{ revision: number; canvasWidth: number; name: string; backgroundColor: string | null }>(
    'PATCH', url, { canvasWidth: 1280, canvasHeight: 720, backgroundColor: '#112233', name: 'renamed', baseRevision: pic.revision });
  expect(ok.revision).toBeGreaterThan(pic.revision);
  expect([ok.canvasWidth, ok.name, ok.backgroundColor]).toEqual([1280, 'renamed', '#112233']);
  const cleared = await api.json<{ backgroundColor: string | null }>('PATCH', url, { backgroundColor: '' });
  expect(cleared.backgroundColor, 'an empty string clears the colour').toBeNull();

  const stale = await api.request.patch(url, { data: { name: 'must not be written', baseRevision: pic.revision }, failOnStatusCode: false });
  expect(stale.status()).toBe(409);
  expect(((await stale.json()) as { code?: string }).code ?? 'REVISION_CONFLICT').toBe('REVISION_CONFLICT');
  expect((await getFull(api, pics.projectId, pic.id)).name, 'nothing was written').toBe('renamed');
});

test('PE-203 layer routes: create text and image layers, patch (each bumps the revision), reorder, delete', async ({ api, pics }) => {
  const pic = await createFull(api, pics.projectId, pics.quad, `pe203-${Date.now()}`);
  const url = picturesUrl(pics.projectId, pic.id);
  const text = await api.json<{ id: string; kind: string }>('POST', `${url}/layers/text`, {
    content: { text: 'Hi', fontFamily: 'Inter', fontWeight: 700, fontSize: 100, lineHeight: 1.1, letterSpacing: 0, align: 'left', color: '#ffffff', stroke: null, shadow: null, maxWidth: null, uppercase: false },
  }, [200, 201]);
  const badge = await addBadgeLayer(api, pics.projectId, pic.id, pics);
  let rev = (await getFull(api, pics.projectId, pic.id)).revision;
  for (const patch of [{ opacity: 0.4 }, { visible: false }, { locked: true }, { label: 'renamed' }, { blendMode: 'screen' }, { role: 'subject' }, { transform: { x: 10, y: 20, scale: 2, rotation: 5 } }]) {
    await api.json('PATCH', `${url}/layers/${badge.id}`, patch);
    const now = (await getFull(api, pics.projectId, pic.id)).revision;
    expect(now, `${Object.keys(patch)[0]} bumps the revision`).toBeGreaterThan(rev);
    rev = now;
  }
  const after = layerOf(await getFull(api, pics.projectId, pic.id), badge.id);
  expect(after).toMatchObject({ opacity: 0.4, visible: false, locked: true, label: 'renamed', blendMode: 'screen', role: 'subject' });
  expect(after.transform).toMatchObject({ x: 10, y: 20, scale: 2, rotation: 5 });
  await api.json('PATCH', `${url}/layers/${badge.id}`, { zOrder: 1 });
  expect(layerOf(await getFull(api, pics.projectId, pic.id), badge.id).zOrder, 'moved to the back').toBeLessThan(layerOf(await getFull(api, pics.projectId, pic.id), text.id).zOrder);
  await api.json('DELETE', `${url}/layers/${text.id}`, undefined, [200, 204]);
  expect((await getFull(api, pics.projectId, pic.id)).layers.map((l) => l.id)).not.toContain(text.id);
  const missing = await api.request.fetch(`${url}/layers/${text.id}`, { method: 'PATCH', data: { opacity: 1 }, failOnStatusCode: false });
  expect(missing.status(), 'a deleted layer cannot be patched').toBe(404);
});

test('PE-204 PE-205 upload tickets for every export type, a real upload, and the export registry', async ({ api, pics }) => {
  const pic = await createFull(api, pics.projectId, pics.quad, `pe204-${Date.now()}`);
  const url = picturesUrl(pics.projectId, pic.id);
  const types: Record<string, string> = {
    'image/png': '.png', 'image/jpeg': '.jpg', 'image/webp': '.webp', 'image/avif': '.avif',
    'image/vnd.adobe.photoshop': '.psd', 'image/openraster': '.ora',
  };
  for (const [contentType, ext] of Object.entries(types)) {
    const ticket = await api.json<{ key: string; url: string }>('POST', `${url}/assets/upload-url`, { purpose: 'export', contentType, contentLength: 1234 });
    expect(ticket.key, `${contentType} gets a ${ext} key under the document's prefix`).toMatch(new RegExp(`/image-editor/${pic.id}/export-[0-9a-f-]{36}\\${ext}$`));
  }
  const wrong = await api.request.post(`${url}/assets/upload-url`, { data: { purpose: 'export', contentType: 'text/html', contentLength: 10 }, failOnStatusCode: false });
  expect(wrong.status(), 'a wrong type is refused').toBe(400);

  // A real upload, from Node straight to the presigned URL (needs the stack's storage reachable: editor-v2-picture-storage.sh).
  const png = fs.readFileSync('/tmp/editor-remediation/media/picture-lane/badge.png');
  const ticket = await api.json<{ key: string; url: string; headers: Record<string, string> }>('POST', `${url}/assets/upload-url`, { purpose: 'export', contentType: 'image/png', contentLength: png.length });
  const put = await fetch(ticket.url, { method: 'PUT', headers: ticket.headers, body: png }).catch((e: unknown) => {
    throw new Error(`the presigned URL ${new URL(ticket.url).host} is not reachable from here: run e2e/scripts/editor-v2-picture-storage.sh apply (${String(e)})`);
  });
  expect(put.status, 'storage accepted the upload').toBeLessThan(300);
  const made = await api.json<{ export: { projectFileId: string }; projectFile: { originalFileName: string }; projectThumbnailApplied?: boolean }>(
    'POST', `${url}/exports`, { key: ticket.key, width: 640, height: 360, fileName: 'api-export.png' }, [200, 201]);
  expect(made.projectFile.originalFileName).toBe('api-export.png');
  expect(made.projectThumbnailApplied ?? false, 'the documented no-op').toBe(false);
  const listed = await api.json<Array<{ projectFileId: string }>>('GET', `${url}/exports`);
  expect(listed.map((e) => e.projectFileId)).toContain(made.export.projectFileId);
  const again = await api.request.post(`${url}/exports`, { data: { key: ticket.key, width: 640, height: 360, fileName: 'api-export.png' }, failOnStatusCode: false });
  expect(again.status(), 'a retried registration finds the upload already moved').toBe(409);
  const bytes = await (await api.request.get(`/api/v1/projects/${pics.projectId}/files/${made.export.projectFileId}/media`)).body();
  const file = await saveDownload({ suggestedFilename: () => 'x.png', saveAs: async (p: string) => fs.writeFileSync(p, bytes) }, 'pe205');
  expect([decodeImage(file).width, decodeImage(file).height]).toEqual([640, 360]);
});

test('PE-207 the graphics list says whether designing is available; an empty description is a 400', async ({ api, pics }) => {
  const pic = await createFull(api, pics.projectId, pics.quad, `pe207-${Date.now()}`);
  const url = `${picturesUrl(pics.projectId, pic.id)}/graphics`;
  const list = await api.json<{ available: boolean; graphics: unknown[]; unavailableReason: string | null }>('GET', url);
  expect(typeof list.available).toBe('boolean');
  expect(Array.isArray(list.graphics)).toBe(true);
  if (!list.available) expect((list.unavailableReason ?? '').length).toBeGreaterThan(5);
  const bad = await api.request.post(url, { data: { description: '   ' }, failOnStatusCode: false });
  expect([400, 503], 'an empty description is refused (or the designer is unavailable)').toContain(bad.status());
});
