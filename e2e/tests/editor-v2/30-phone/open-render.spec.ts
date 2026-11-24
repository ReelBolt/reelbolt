import * as fs from 'fs';
import type { BrowserContext, Page } from '@playwright/test';
import { test, expect } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline, seekAndBlur } from '../../../fixtures/editor-v2/lane-a';
import { expectPictureWhole, runOn } from '../../../fixtures/editor-v2/rows';

/**
 * The phone journey's first step, on a real run output: the hub lists the finished render of the seeded run (VideoAnalyze ->
 * VideoCompile, no model and no paid provider), "Edit" makes a timeline from it and the phone editor opens on that timeline
 * with its picture on screen. English and Italian. Screenshots go to /tmp/editor-remediation/round3/.
 */
test.use({ serviceWorkers: 'block' });
test.beforeEach(() => { runOn('pixel7', 'iphone14webkit'); });

const OUT = '/tmp/editor-remediation/round3';

async function useLocale(context: BrowserContext, baseURL: string, locale: string): Promise<void> {
  const cookies = await context.cookies();
  const user = cookies.find((c) => c.name === 'reelbolt_user');
  if (user) {
    const parsed = JSON.parse(decodeURIComponent(user.value)) as Record<string, unknown>;
    parsed.locale = locale;
    await context.addCookies([{ ...user, value: encodeURIComponent(JSON.stringify(parsed)) }]);
  }
  await context.addCookies([{ name: 'rb_locale', value: locale, domain: new URL(baseURL).hostname, path: '/' }]);
}

const shot = async (page: Page, tag: string): Promise<void> => {
  fs.mkdirSync(OUT, { recursive: true });
  await page.screenshot({ path: `${OUT}/${tag}.png` });
};

for (const locale of ['en', 'it'] as const) {
  test(`open a render from the hub and land in the phone editor (${locale})`, async ({ page, context, seed, api, baseURL }, info) => {
    const tag = `${info.project.name}-${locale}`;
    // The hub offers only renders with no timeline yet, and the seed made one from its first run: run the free workflow again.
    const workflows = await api.json<Array<{ id: string; name: string }>>('GET', `/api/v1/projects/${seed.projectId}/workflows`);
    const derush = workflows.find((w) => w.name.startsWith('remediation derush'));
    expect(derush, 'the seeded workflow').toBeTruthy();
    const run = await api.runWorkflow(seed.projectId, derush!.id);
    const output = run.stepResults.find((s) => /\.mp4$/i.test(s.outputStorageKey ?? ''));
    expect(output, 'the run has a finished video').toBeTruthy();
    await useLocale(context, baseURL!, locale);
    await page.goto(`/app/editor?project=${seed.projectId}`);
    if (locale === 'it') await expect(page.locator('html')).toHaveAttribute('lang', /^it/);

    const edit = page.getByTestId(`render-edit-${output!.id}`);
    await edit.scrollIntoViewIfNeeded();
    await expect(edit, 'the run output is listed with its Edit button').toBeVisible();
    const box = (await edit.boundingBox())!;
    const vw = page.viewportSize()!.width;
    expect(box.x, 'Edit starts on screen').toBeGreaterThanOrEqual(0);
    expect(box.x + box.width, 'Edit ends on screen').toBeLessThanOrEqual(vw + 0.5);
    expect(box.height, 'Edit is a thumb-sized target').toBeGreaterThanOrEqual(43.5);
    expect(await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth), 'the hub does not scroll sideways').toBeLessThanOrEqual(0);
    await shot(page, `${tag}-01-hub`);

    await edit.tap();
    await expect(page, 'the render opened as a timeline').toHaveURL(/\/app\/editor\/[^/]+\/timeline\/[^/?]+/, { timeout: 45_000 });
    await expect(page.getByTestId('phone-header')).toBeVisible({ timeout: 45_000 });
    await expect(page.getByTestId('phone-action-bar')).toBeVisible();
    await expect(page.getByTestId('save-state-chip')).toHaveAttribute('data-status', 'saved', { timeout: 30_000 });
    await expectPlayAndPicture(page, 'as opened');
    await shot(page, `${tag}-02-editor`);

    // Phones as they really measure with their browser bars, from an iPhone SE to a tall Android.
    for (const size of [{ width: 375, height: 553 }, { width: 360, height: 640 }, { width: 412, height: 641 }, { width: 390, height: 664 }, { width: 393, height: 680 }, { width: 412, height: 780 }, { width: 640, height: 360 }, { width: 780, height: 360 }]) {
      await page.setViewportSize(size);
      await expectPlayAndPicture(page, `at ${size.width}x${size.height}`);
      await shot(page, `${tag}-03-${size.width}x${size.height}`);
    }
  });
}

test('the picture keeps its room in Italian with a clip selected and with a sheet open', async ({ freshTimeline, openEditor, api, seed, context, baseURL }) => {
  const id = await clipsTimeline(freshTimeline, api, seed, PAIR);
  await useLocale(context, baseURL!, 'it');
  const ed = await openEditor(id);
  await seekAndBlur(ed, 30);
  await ed.selectClipAt(30);
  for (const size of [{ width: 360, height: 741 }, { width: 360, height: 800 }, { width: 390, height: 664 }, { width: 390, height: 844 }, { width: 393, height: 780 }]) {
    const when = `at ${size.width}x${size.height}`;
    await ed.page.setViewportSize(size);
    await expect(ed.testId('phone-action-split')).toBeVisible();
    await expectPictureWhole(ed.page, 60, `clip selected ${when}`);
    await ed.tap(ed.testId('dock-look'));
    await expect(ed.testId('look-sheet')).toBeVisible();
    await expectPictureWhole(ed.page, 60, `sheet open ${when}`);
    await ed.page.keyboard.press('Escape');
    await expect(ed.testId('look-sheet')).toHaveCount(0);
  }
});

test('landscape: the picture keeps its room with a clip selected in the Simple preview, when the header is at its fullest', async ({ page, freshTimeline, openEditor }) => {
  // Auto starts on the live preview on a phone too; the Simple preview is the user's own switch, and it puts the way back in the header.
  await page.addInitScript(() => localStorage.setItem('reelbolt:editor:preview-quality', 'auto'));
  const ed = await openEditor(await freshTimeline('derush'));
  await expect(ed.page.getByTestId('program-viewer')).toHaveAttribute('data-session-kind', 'live', { timeout: 30_000 });
  await ed.tap(ed.page.getByRole('button', { name: 'Preview options' }));
  await ed.page.getByTestId('degraded-toggle').check({ force: true });
  await ed.page.keyboard.press('Escape');
  await seekAndBlur(ed, 30);
  await ed.selectClipAt(30);
  await expect(ed.testId('preview-use-live'), 'the Simple preview is on, so the header holds its way back').toBeVisible();
  for (const size of [{ width: 568, height: 320 }, { width: 640, height: 360 }, { width: 780, height: 360 }, { width: 844, height: 390 }]) {
    await ed.page.setViewportSize(size);
    await expect(ed.testId('phone-header')).toBeVisible();
    await expectPictureWhole(ed.page, 60, `at ${size.width}x${size.height}`);
    // The phone stylesheet puts the chip and the way back first through the header's direct children: nothing may wrap them.
    await expect(ed.page.locator("[data-testid='viewer-header'] > [data-testid='parity-badge']")).toHaveCount(1);
    // On one row the header's controls can slide away: the way back and the chip must stay where a finger can reach them.
    for (const id of ['preview-use-live', 'parity-badge']) {
      const reachable = await ed.testId(id).evaluate((el) => {
        const r = el.getBoundingClientRect();
        const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
        return !!hit && (hit === el || el.contains(hit));
      });
      expect(reachable, `${id} can be tapped at ${size.width}x${size.height}`).toBe(true);
    }
  }
});

async function expectPlayAndPicture(page: Page, when: string): Promise<void> {
  await expect.poll(async () => page.getByTestId('transport-play').evaluate((el) => {
    const r = el.getBoundingClientRect();
    const top = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return !!top && (top === el || el.contains(top));
  }), { message: `Play can be tapped without scrolling first ${when}` }).toBe(true);
  // 59.5, not 60: at 375x553 the picture lays out at 59.984 px; the half pixel is the same sub-pixel tolerance the other phone specs use.
  await expectPictureWhole(page, 59.5, when);
}
