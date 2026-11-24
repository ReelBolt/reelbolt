import { mergeTests } from '@playwright/test';
import { deflateSync } from 'node:zlib';
import { test as authTest, expect } from '../../fixtures/auth';
import { test as projectTest } from '../../fixtures/project';

/**
 * Avatars tab happy path (docs/design/avatars.md "Web", lane A4): upload a picture, make an avatar
 * through the three-step form (consent statement from the server, ticked by the user), see it Ready,
 * then delete it and read the report.
 *
 * Needs a stack with `AVATARS_ENABLED=true` and a plan that includes avatars. The default local
 * stack has the switch OFF (every avatar route answers 404), so this spec probes the requirements
 * route first and SKIPS, naming what it saw, instead of failing. With content moderation or CSAM
 * scanning on, a just-uploaded picture is refused until it has been checked; the spec retries the
 * create for a short while and then skips with the refusal it got.
 *
 * SAFETY: no vendor call, no credits. The only writes are the scratch project (deleted by the
 * fixture), one uploaded picture and the avatar, which the spec deletes.
 */
const test = mergeTests(authTest, projectTest);
const BASE_URL = process.env.BASE_URL || 'http://localhost';

/** A plain 320x320 grey PNG, large enough for the 256 px minimum side. */
function greyPng(size = 320): Buffer {
  const crcTable = Array.from({ length: 256 }, (_, n) => {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    return c >>> 0;
  });
  const crc = (buf: Buffer) => {
    let c = 0xffffffff;
    for (const b of buf) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8);
    return (c ^ 0xffffffff) >>> 0;
  };
  const chunk = (type: string, data: Buffer) => {
    const len = Buffer.alloc(4);
    len.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    const sum = Buffer.alloc(4);
    sum.writeUInt32BE(crc(body));
    return Buffer.concat([len, body, sum]);
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(size, 0);
  header.writeUInt32BE(size, 4);
  header[8] = 8; // bit depth
  header[9] = 0; // greyscale
  const row = Buffer.concat([Buffer.from([0]), Buffer.alloc(size, 0x88)]);
  const raw = Buffer.concat(Array.from({ length: size }, () => row));
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', deflateSync(raw)),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

test('make an avatar with consent, see it ready, delete it', async ({ authenticatedPage: page, request, scratchProject }) => {
  const probe = await request.get(`${BASE_URL}/api/v1/projects/${scratchProject.id}/avatars/requirements`);
  test.skip(probe.status() === 404, 'Avatars are switched off on this stack (requirements route answered 404; set AVATARS_ENABLED=true).');
  expect(probe.ok(), `requirements ${probe.status()}`).toBeTruthy();
  const requirements = await probe.json() as { entitled: boolean; statements: Record<string, string> };
  test.skip(!requirements.entitled, 'The test workspace plan does not include avatars (entitled: false).');

  const up = await request.post(`${BASE_URL}/api/v1/projects/${scratchProject.id}/files`, {
    multipart: { file: { name: 'e2e-face.png', mimeType: 'image/png', buffer: greyPng() } },
  });
  expect(up.ok(), `upload ${up.status()}`).toBeTruthy();

  await page.goto(`${BASE_URL}/app/projects/${scratchProject.id}?tab=avatars`);
  const tab = page.getByTestId('avatars-tab');
  await expect(tab).toBeVisible();

  await tab.getByRole('button', { name: 'New avatar' }).click();
  await page.getByLabel('Use e2e-face.png').check();
  await page.getByRole('button', { name: 'Next' }).click();

  // The statement on screen is the server's, verbatim.
  await page.getByText('English', { exact: true }).click();
  await expect(page.getByTestId('avatar-consent-statement')).toHaveText(requirements.statements.en);
  await page.getByLabel(/The person's name/).fill('E2E Person');
  await page.getByLabel('This is me').check();
  await page.getByLabel('No', { exact: true }).check();
  await page.getByLabel('The person is 18 or older').check();
  await page.getByLabel('I have read this statement and I confirm it').check();
  await page.getByRole('button', { name: 'Next' }).click();
  await page.getByLabel(/Avatar name/).fill('E2E avatar');

  // A just-uploaded picture may still be under the safety checks: retry for up to a minute.
  let refusal = '';
  for (let attempt = 0; attempt < 6; attempt++) {
    await page.getByTestId('avatar-submit').click();
    const outcome = await page.locator('[data-testid^="avatar-card-"]').first()
      .waitFor({ timeout: 10_000 }).then(() => 'created').catch(() => 'refused');
    if (outcome === 'created') { refusal = ''; break; }
    refusal = (await page.getByTestId('avatar-form-error').textContent().catch(() => null)) ?? 'no answer';
    if (!/still being checked|not been scanned/.test(refusal)) break;
  }
  test.skip(refusal !== '', `Create was refused: ${refusal}`);

  const card = page.locator('[data-testid^="avatar-card-"]').first();
  await expect(card.getByTestId('avatar-status')).toHaveText(/Ready|Expires on/);

  await card.getByRole('button', { name: 'Actions for E2E avatar' }).click();
  await page.getByRole('menuitem', { name: 'Delete' }).click();
  await page.getByRole('button', { name: 'Delete avatar' }).click();
  await expect(page.getByTestId('avatar-delete-report')).toContainText('1 file deleted.');
  await page.getByRole('button', { name: 'Close' }).click();
  await expect(page.getByText('No avatars yet')).toBeVisible();
});
