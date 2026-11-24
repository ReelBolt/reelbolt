import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { createPicture, picturesUrl } from '../../../fixtures/editor-v2/picture';
import { knownFailure } from '../expected-failures';

/**
 * d6 N2 (TH-CANVAS / mask upload, remediation-plan.md W0.8): asking to upload an empty mask is a 400 with a
 * plain message, not a 413 "too large". The audit (d6 api1/api2): `contentLength: 0` answered
 * `413 FILE_TOO_LARGE`. Only the upload URL is requested; nothing is uploaded or run (D9).
 */
test('d6-N2 an empty mask upload is refused with 400 and a message, not 413', async ({ api, seed }, testInfo) => {
  runOn('desktop', 'pixel7');
  knownFailure('d6-N2');
  const pic = await createPicture(api, seed.projectId, seed.files.still, `d6n2-${testInfo.project.name}-${Date.now()}`);
  const url = `${picturesUrl(seed.projectId, pic.id)}/assets/upload-url`;

  const empty = await api.request.post(url, { data: { purpose: 'mask', contentType: 'image/png', contentLength: 0 }, failOnStatusCode: false });
  const body = await empty.text();
  expect(empty.status(), `empty mask -> ${empty.status()} ${body.slice(0, 300)}`).toBe(400);
  const parsed = JSON.parse(body) as { message?: string; code?: string };
  expect(parsed.code, 'not the too-large code').not.toBe('FILE_TOO_LARGE');
  expect(parsed.message ?? '', 'a plain sentence saying the mask is empty').toMatch(/empty/i);
  expect(parsed.message ?? '', 'no "limited to N bytes" wording').not.toMatch(/limited to/i);

  // The limit itself still answers 413 (the fix only splits empty from too large).
  const huge = await api.request.post(url, { data: { purpose: 'mask', contentType: 'image/png', contentLength: 1024 * 1024 * 1024 }, failOnStatusCode: false });
  expect(huge.status(), 'a 1 GiB mask is still too large').toBe(413);
});
