import * as path from 'path';
import { test, expect } from '../../../fixtures/editor-v2';
import { runOn, uniqueCopy } from '../../../fixtures/editor-v2/rows';
import { ensureMedia } from '../../../fixtures/editor-v2/seed';
import { createPicture, getPicture, picturesUrl } from '../../../fixtures/editor-v2/picture';
import { knownFailure } from '../expected-failures';

/**
 * TH-LAYER-13 (remediation-plan.md W0.8 T4): the layer pixel endpoints answer PNG bytes or a 404, never a
 * 500. The audit (d6 api1, T4): after the source photo was deleted, GET …/layers/{id}/image answered 500.
 * With copy-on-delete (D10) the photo layer keeps its own copy and still serves its pixels; a layer that
 * has no pixels (text), no matte, or no longer exists answers 404. API only, no paid call (D9).
 */
test('TH-LAYER-13 layer image endpoints answer pixels or 404, never 500, also after the source photo is deleted', async ({ api, seed }, testInfo) => {
  runOn('desktop', 'pixel7');
  knownFailure('TH-LAYER-13');
  const name = `thlayer13-${testInfo.project.name}-${Date.now()}.png`;
  const fileId = await api.upload(seed.projectId, uniqueCopy(path.join(ensureMedia(), 'still.png')), 'image/png', name);
  const pic = await createPicture(api, seed.projectId, fileId, `thlayer13-${testInfo.project.name}`);
  const photo = pic.layers[0];
  const layerUrl = (id: string, what: 'image' | 'matte') => `${picturesUrl(seed.projectId, pic.id)}/layers/${id}/${what}`;
  const get = (url: string) => api.request.get(url, { failOnStatusCode: false });

  const before = await get(layerUrl(photo.id, 'image'));
  expect(before.status(), 'the photo layer serves its pixels').toBe(200);
  expect(before.headers()['content-type']).toMatch(/^image\//);
  expect((await before.body()).length).toBeGreaterThan(0);
  expect((await get(layerUrl(photo.id, 'matte'))).status(), 'a photo layer has no matte: 404').toBe(404);

  const text = await api.json<{ id: string }>('POST', `${picturesUrl(seed.projectId, pic.id)}/layers/text`, {
    content: {
      text: 'TH-LAYER-13', fontFamily: 'Inter', fontWeight: 700, fontSize: 96, lineHeight: 1.2, letterSpacing: 0,
      align: 'center', color: '#ffffff', stroke: null, shadow: null, maxWidth: null, uppercase: false,
    },
  }, [200, 201]);
  expect((await get(layerUrl(text.id, 'image'))).status(), 'a text layer has no pixels: 404').toBe(404);
  expect((await get(layerUrl(crypto.randomUUID(), 'image'))).status(), 'an unknown layer: 404').toBe(404);

  await api.json('DELETE', `/api/v1/projects/${seed.projectId}/files/${fileId}`, undefined, [200, 204]);
  const after = await getPicture(api, seed.projectId, pic.id);
  expect(after.layers.some((l) => l.id === photo.id), 'the photo layer is still in the picture').toBe(true);
  const gone = await get(layerUrl(photo.id, 'image'));
  expect(gone.status(), `after the source photo is deleted -> ${gone.status()} ${(await gone.text()).slice(0, 200)}`).not.toBe(500);
  expect(gone.status(), 'the picture kept its own copy of the photo (D10), so the pixels are still served').toBe(200);
  expect(gone.headers()['content-type']).toMatch(/^image\//);
});
