/**
 * App flows (/app/*) of the security-headers harvest: auth, dashboard, project, upload, editor, assistant,
 * billing (Paddle mock), guest review, links, marketplace, admin, service worker. See harness.ts.
 */
import { test, expect, type Page } from '@playwright/test';
import * as path from 'path';
import { flow, settle } from './harness';
import { EditorApi, mediaClip } from '../../fixtures/editor-v2/api';
import { ensureMedia } from '../../fixtures/editor-v2/seed';
import { user, loadStack } from '../../fixtures/stack';

const uniq = Date.now().toString(36);

/** A project with one video and one tone on a timeline, built through the REST API. */
async function seedProject(page: Page) {
  const api = new EditorApi(page.context().request);
  const dir = ensureMedia();
  const project = await api.createProject(`sechdr-${uniq}-${Math.random().toString(36).slice(2, 6)}`);
  const clip = await api.upload(project.id, path.join(dir, 'clipA.mp4'), 'video/mp4');
  const tone = await api.upload(project.id, path.join(dir, 'tone.wav'), 'audio/wav');
  const tl = await api.createTimeline(project.id, 'sechdr');
  await api.putDocument(project.id, tl.id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [mediaClip(crypto.randomUUID(), 'clipA.mp4', clip, 0, 150)];
    doc.tracks.find((t) => t.kind === 'audio')!.items = [mediaClip(crypto.randomUUID(), 'tone.wav', tone, 0, 150)];
  });
  return { api, projectId: project.id, timelineId: tl.id, clip };
}

async function skipTour(page: Page) {
  const skip = page.getByRole('button', { name: /skip tour/i });
  if (await skip.isVisible({ timeout: 3000 }).catch(() => false)) await skip.click();
}

test.describe('app auth', () => {
  flow('app: signup form submit', async (page) => {
    await page.goto('/app/signup');
    await settle(page);
    const boxes = page.locator('input:not([type="password"]):not([type="checkbox"]):not([type="hidden"])');
    await boxes.nth(0).fill('Header Probe');
    await boxes.nth(1).fill(`sechdr-${uniq}@example.com`);
    await page.locator('input[type="password"]').fill('Sech-dr-Probe-9921!');
    for (const c of await page.locator('input[type="checkbox"]').all()) await c.check();
    await page.locator('button[type="submit"]').click();
    await page.waitForTimeout(4000);
  });
  flow('app: login form, dashboard, welcome', async (page) => {
    const u = user('creator');
    await page.goto('/app/login');
    await settle(page);
    await page.locator('input:not([type="password"]):not([type="hidden"])').first().fill(u.email);
    await page.locator('input[type="password"]').fill(u.password);
    await page.locator('button[type="submit"]').click();
    await page.waitForURL(/\/app\/(?!login)/, { timeout: 30_000 });
    await settle(page);
    await page.goto('/app/dashboard');
    await settle(page);
    await page.goto('/app/welcome');
    await settle(page);
  });
  flow('app: forgot password, change password page', async (page) => {
    await page.goto('/app/forgot-password');
    await settle(page);
    await page.locator('input:not([type="hidden"])').first().fill('nobody@example.com');
    await page.locator('button[type="submit"]').click();
    await page.waitForTimeout(2000);
    await page.goto('/app/change-password');
    await settle(page);
  });
  flow('app: OAuth start redirect', async (page) => {
    await page.goto('/app/login');
    await settle(page);
    const oauth = page.getByRole('button', { name: /google|github|microsoft/i }).first();
    if (await oauth.isVisible().catch(() => false)) {
      await Promise.all([page.waitForEvent('popup', { timeout: 4000 }).catch(() => undefined), oauth.click()]);
    }
    const r = await page.request.get('/api/v1/auth/oauth/google/start', { maxRedirects: 0 }).catch(() => undefined);
    test.info().annotations.push({ type: 'oauth-start', description: String(r?.status()) });
  });
});

test.describe('app signed in', () => {
  flow('app: projects list, create project, files, upload', async (page) => {
    await page.goto('/app/projects');
    await settle(page);
    const { projectId } = await seedProject(page);
    await page.goto(`/app/projects/${projectId}`);
    await settle(page);
    await page.goto(`/app/projects/${projectId}/files`);
    await settle(page);
    // Browser upload through the UI: the presigned PUT (or multipart) goes from the page.
    const input = page.locator('input[type="file"]').first();
    await input.setInputFiles(path.join(ensureMedia(), 'still.png'));
    await page.waitForTimeout(6000);
    const uploads = [...(await page.evaluate(() => performance.getEntriesByType('resource').map((e) => e.name))).filter((n) => /upload|PUT|s3|garage|X-Amz/i.test(n))];
    test.info().annotations.push({ type: 'upload-requests', description: uploads.join(' ').slice(0, 400) });
  }, { login: 'creator' });

  flow('app: editor open (viewer, workers, audio, fonts), play', async (page) => {
    const { projectId, timelineId } = await seedProject(page);
    await page.goto(`/app/editor/${projectId}/timeline/${timelineId}`);
    await settle(page);
    await page.waitForTimeout(6000);
    await page.keyboard.press('Space');
    await page.waitForTimeout(2500);
    await page.keyboard.press('Space');
    // Text clip: loads the font stylesheet from fonts.googleapis.com.
    const add = page.getByRole('button', { name: /text|title/i }).first();
    if (await add.isVisible().catch(() => false)) await add.click().catch(() => undefined);
    await page.waitForTimeout(2500);
    await page.goto(`/app/editor/${projectId}/timelines`);
    await settle(page);
    await page.goto(`/app/editor/${projectId}/media`);
    await settle(page);
  }, { login: 'creator' });

  flow('app: thumbnail editor (text fonts from fonts.googleapis.com)', async (page) => {
    const { api, projectId } = await seedProject(page);
    const files = await api.listFiles(projectId);
    const still = await api.upload(projectId, path.join(ensureMedia(), 'still.png'), 'image/png');
    void files;
    const doc = await api.json<{ id: string }>('POST', `/api/v1/projects/${projectId}/image-documents`, { sourceProjectFileId: still, name: 'sechdr' }, [200, 201]);
    await page.goto(`/app/editor/thumbnails/${doc.id}?project=${projectId}`);
    await settle(page);
    await skipTour(page);
    await page.getByRole('button', { name: /^text$/i }).first().click();
    const canvas = page.locator('canvas').first();
    const box = (await canvas.boundingBox())!;
    await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
    await page.waitForTimeout(500);
    await page.keyboard.type('Header probe');
    await page.waitForTimeout(4000);
  }, { login: 'creator' });

  flow('app: assistant panel opens, message sent', async (page) => {
    await page.goto('/app/dashboard');
    await settle(page);
    await skipTour(page);
    const launcher = page.getByRole('button', { name: /assistant/i }).first();
    if (await launcher.isVisible().catch(() => false)) await launcher.click();
    const box = page.getByRole('textbox').last();
    if (await box.isVisible().catch(() => false)) {
      await box.fill('hello');
      await box.press('Enter');
    }
    await page.waitForTimeout(5000);
  }, { login: 'creator' });

  flow('app: billing page, Paddle checkout (mock) and return', async (page) => {
    await page.goto('/app/settings/billing');
    await settle(page);
    const topup = page.getByTestId('topup-card').getByRole('button').first();
    if (await topup.isVisible().catch(() => false)) {
      const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/checkout'));
      await topup.click();
      await resp;
      await page.waitForURL(/\/checkout\/txn_/, { timeout: 20_000 });
      await settle(page);
      await page.getByTestId('mock-pay').click();
      await page.waitForURL(/\/app\/checkout\?_ptxn=txn_/, { timeout: 20_000 });
      await settle(page);
    }
    await page.goto('/app/settings/usage');
    await settle(page);
  }, { login: 'creator' });

  flow('app: settings pages', async (page) => {
    for (const p of ['/app/settings/notifications', '/app/settings/data', '/app/org/members', '/app/org/api-keys', '/app/agents', '/app/community/mine', '/app/runners']) {
      await page.goto(p);
      await settle(page);
    }
  }, { login: 'creator' });

  flow('app: marketplace public gallery', async (page) => {
    await page.goto('/app/community');
    await settle(page);
    const first = page.locator('a[href^="/app/community/"]').first();
    if (await first.isVisible().catch(() => false)) {
      await first.click();
      await settle(page);
    }
  }, { login: 'creator' });

  flow('app: admin pages', async (page) => {
    for (const p of ['/app/admin/users', '/app/admin/billing', '/app/admin/links', '/app/admin/promotions', '/app/admin/moderation', '/app/admin/campaigns', '/app/admin/marketplace', '/app/admin/skills']) {
      await page.goto(p);
      await settle(page);
    }
  }, { login: 'admin' });

  flow('app: service worker /app/sw.js registers and serves', async (page) => {
    await page.goto('/app/dashboard');
    await settle(page);
    const r = await page.evaluate(async () => {
      const res = await fetch('/app/sw.js');
      const reg = await navigator.serviceWorker?.getRegistrations?.();
      return { status: res.status, regs: reg?.length ?? -1 };
    });
    expect(r.status).toBe(200);
    await page.goto('/app/offline');
    await settle(page);
  }, { login: 'creator' });

  flow('links: tracked link /l/<code> redirect', async (page) => {
    const r = await page.request.get('/l/does-not-exist', { maxRedirects: 0 });
    test.info().annotations.push({ type: 'tracked-link', description: String(r.status()) });
    await page.goto('/l/does-not-exist');
    await settle(page);
  });
});

test.describe('review and output links', () => {
  flow('review: guest link page with video playback', async (page, base) => {
    const { api, projectId, timelineId } = await seedProject(page);
    const started = await api.json<{ executionId: string }>('POST', `${api.timelinesUrl(projectId, timelineId)}/render`, { preset: 'Hd1080p16x9', quality: 'draft' }, [200, 201, 202]);
    await expect
      .poll(async () => {
        const list = await api.json<Array<{ executionId: string; status: string }>>('GET', `${api.timelinesUrl(projectId, timelineId)}/renders`);
        return list.find((r) => r.executionId === started.executionId)?.status;
      }, { timeout: 150_000, intervals: [2000] })
      .toBe('Passed');
    const link = await api.json<{ id: string; url: string }>('POST', `${api.timelinesUrl(projectId, timelineId)}/review-links`, { label: 'sechdr', expiresInDays: 1 }, [201]);
    const guest = await page.context().browser()!.newContext({ baseURL: base });
    const gp = await guest.newPage();
    await (await import('./harness')).attachGuest(guest, gp, base);
    await gp.goto(new URL(link.url).pathname);
    await expect(gp.getByTestId('guest-video')).toBeVisible({ timeout: 30_000 });
    await gp.getByTestId('guest-video').evaluate((v: HTMLVideoElement) => v.play().catch(() => undefined));
    await gp.waitForTimeout(3000);
    await guest.close();
  }, { login: 'creator' });
});
