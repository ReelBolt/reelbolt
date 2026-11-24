import * as fs from 'fs';
import type { Locator, Page } from '@playwright/test';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline, countItems, seekAndBlur } from '../../../fixtures/editor-v2/lane-a';
import { expectPictureWhole, runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Mobile editor usability round 2: the phone journey (open, split, trim, undo, play, add media, add a title, Deliver)
 * walked on pixel7 and iphone14webkit in English and Italian. One screenshot per step goes to
 * /tmp/editor-remediation/round2/<device>-<locale>-<nn>-<step>.png. After each step a cheap audit records, as test
 * annotations, controls under 44 px, clipped labels, off-screen controls and English words in the Italian run.
 * Does not touch the "Open full view" mode switch.
 */
test.use({ serviceWorkers: 'block' });
test.beforeEach(() => { runOn('pixel7', 'iphone14webkit'); });

const OUT = '/tmp/editor-remediation/round2';

for (const locale of ['en', 'it'] as const) {
  test(`J2 phone journey (${locale})`, async ({ freshTimeline, openEditor, api, seed, context, baseURL }, info) => {
    test.setTimeout(240_000);
    fs.mkdirSync(OUT, { recursive: true });
    const device = info.project.name;
    let n = 0;
    const found: string[] = [];
    const step = async (ed: EditorPage, name: string): Promise<void> => {
      n += 1;
      const tag = `${device}-${locale}-${String(n).padStart(2, '0')}-${name}`;
      await ed.page.screenshot({ path: `${OUT}/${tag}.png` });
      const issues = await audit(ed.page, locale);
      for (const i of issues) { found.push(`${tag}: ${i}`); info.annotations.push({ type: 'finding', description: `${tag}: ${i}` }); }
    };

    let ed!: EditorPage;
    const vp = () => ed.page.viewportSize()!;
    const inView = async (loc: Locator, what: string): Promise<void> => {
      await expect(loc, what).toBeVisible();
      const b = (await loc.boundingBox())!;
      expect(b.x, `${what} left edge`).toBeGreaterThanOrEqual(-0.5);
      expect(b.y, `${what} top edge`).toBeGreaterThanOrEqual(-0.5);
      expect(b.x + b.width, `${what} right edge`).toBeLessThanOrEqual(vp().width + 0.5);
      expect(b.y + b.height, `${what} bottom edge`).toBeLessThanOrEqual(vp().height + 0.5);
    };
    const notClipped = async (loc: Locator, what: string): Promise<void> => {
      const [sw, cw] = await loc.evaluate((el) => [el.scrollWidth, el.clientWidth]);
      expect(sw, `${what} is not clipped`).toBeLessThanOrEqual(cw + 1);
    };
    const playAndPicture = async (when: string): Promise<void> => {
      const play = ed.testId('transport-play');
      await inView(play, `Play ${when}`);
      await expectOnTop(play, `Play ${when}`);
      const picture = ed.page.locator('[data-testid="program-canvas"], [data-testid="simple-preview-image"], [data-testid="phone-viewer"] img').first();
      const pb = (await picture.boundingBox())!;
      const vb = (await ed.testId('phone-viewer').boundingBox())!;
      await expectPictureWhole(ed.page, 60, when);
      // F2 guard: the regression drew the picture as a ~48 px sliver (about 12% of the viewer). Headless runs use software
      // rendering, whose "reduced quality" note takes a line under the picture, so a healthy 16:9 picture measures about
      // 47% of the viewer width here (192 px of 412); 40% and 90 px catch the sliver without tracking that note.
      expect(pb.height, `picture height ${when}`).toBeGreaterThanOrEqual(90);
      expect(pb.width, `picture is at least 40% of the viewer ${when}`).toBeGreaterThanOrEqual(vb.width * 0.4);
    };

    const id = await clipsTimeline(freshTimeline, api, seed, PAIR);
    // The app picks the language: saved user locale (reelbolt_user cookie) -> rb_locale cookie -> Accept-Language.
    const cookies = await context.cookies();
    const user = cookies.find((c) => c.name === 'reelbolt_user');
    if (user) {
      const parsed = JSON.parse(decodeURIComponent(user.value)) as Record<string, unknown>;
      parsed.locale = locale;
      await context.addCookies([{ ...user, value: encodeURIComponent(JSON.stringify(parsed)) }]);
    }
    const origin = new URL(baseURL!);
    await context.addCookies([{ name: 'rb_locale', value: locale, domain: origin.hostname, path: '/' }]);

    // eslint-disable-next-line prefer-const
    ed = await openEditor(id);
    await expect(ed.testId('phone-action-bar')).toBeVisible();
    if (locale === 'it') await expect(ed.page.locator('html')).toHaveAttribute('lang', /^it/);
    await playAndPicture('after open');
    await notClipped(ed.testId('save-state-chip'), 'save-state chip');
    // D2+: the phone says "Early preview" in words, on the status line, in view and unclipped.
    const early = ed.testId('phone-status-row').getByTestId('early-preview-label');
    await expect(early).toHaveText(locale === 'it' ? 'Anteprima anticipata' : 'Early preview');
    await inView(early, 'Early preview label');
    await notClipped(early.locator('.mantine-Badge-root'), 'Early preview badge');
    await notClipped(early.locator('.mantine-Badge-label'), 'Early preview label');
    {
      const [badge, label] = [await early.locator('.mantine-Badge-root').boundingBox(), await early.locator('.mantine-Badge-label').boundingBox()];
      expect(label!.x, 'label starts inside its badge').toBeGreaterThanOrEqual(badge!.x - 0.5);
      expect(label!.x + label!.width, 'label ends inside its badge').toBeLessThanOrEqual(badge!.x + badge!.width + 0.5);
    }
    await step(ed, 'open');

    // Split
    await seekAndBlur(ed, 30);
    await ed.selectClipAt(15);
    await playAndPicture('with a clip selected');
    await step(ed, 'selected');
    await ed.tap(ed.testId('phone-action-split'));
    await ed.expectServer((t) => countItems(t) === 3, 'the clip was split');
    await step(ed, 'split');

    // Trim
    await ed.selectClipAt(45);
    await ed.tap(ed.testId('phone-action-trim'));
    await expect(ed.testId('trim-sheet')).toBeVisible();
    await ed.tap(ed.testId('trim-end-1'));
    await step(ed, 'trim-sheet');
    await ed.tap(ed.testId('trim-done'));
    await step(ed, 'trimmed');

    // Undo
    await ed.tap(ed.testId('phone-undo'));
    await step(ed, 'undo');

    // Play
    await ed.tap(ed.testId('transport-play'));
    await ed.page.waitForTimeout(800);
    await step(ed, 'playing');
    await ed.tap(ed.testId('transport-play'));

    // Add media
    await ed.workspace('media');
    await expect(ed.testId('phone-sheet')).toBeVisible();
    await expect(ed.testId('media-pool')).toBeVisible({ timeout: 30_000 });
    await step(ed, 'add-media');
    await ed.page.keyboard.press('Escape');

    // Add a title
    await ed.tap(ed.testId('dock-look'));
    await ed.testId('look-title-text').scrollIntoViewIfNeeded();
    await ed.testId('look-title-text').fill(locale === 'it' ? 'Presto in arrivo' : 'Coming soon');
    await step(ed, 'title-typing');
    await ed.tap(ed.testId('look-title-add'));
    await ed.expectServer((t) => t.document.tracks.some((tr) => tr.items.some((i) => (i as { type?: string }).type === 'titleClip')), 'the title is stored');
    await step(ed, 'title-added');
    await ed.page.keyboard.press('Escape');

    // Deliver / render (local ffmpeg, no paid provider)
    await ed.tap(ed.testId('phone-export'));
    await expect(ed.testId('deliver-workspace')).toBeVisible();
    const preset = ed.testId('render-preset');
    await inView(preset, 'Format select');
    await notClipped(preset, 'Format select');
    const quality = ed.testId('render-quality');
    await inView(quality, 'Quality control');
    for (const label of await quality.locator('label').all()) await inView(label, 'Quality option');
    await step(ed, 'deliver');
    await ed.tap(ed.testId('render-start'));
    await ed.page.waitForTimeout(1500);
    await step(ed, 'render-started');
    await expect.poll(async () => {
      const list = await api.json<Array<{ status: string; hasOutput: boolean }>>('GET', `${api.timelinesUrl(seed.projectId, id)}/renders`);
      return list.length > 0;
    }, { message: 'a render was queued', timeout: 60_000 }).toBe(true);
    await step(ed, 'render-queued');
    info.annotations.push({ type: 'findings-count', description: String(found.length) });
  });
}

/** A box inside the viewport can still be scrolled away or covered: what the finger would hit at its centre must be the control. */
async function expectOnTop(loc: Locator, what: string): Promise<void> {
  const hit = await loc.evaluate((el) => {
    const r = el.getBoundingClientRect();
    const top = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return !!top && (top === el || el.contains(top));
  });
  expect(hit, `${what} can be tapped where it is drawn`).toBe(true);
}

/** Cheap UX audit of what is on screen: tap-size, clipping, off-screen, untranslated English (it only). */
async function audit(page: Page, locale: string): Promise<string[]> {
  return page.evaluate((loc) => {
    const out: string[] = [];
    const vw = window.innerWidth, vh = window.innerHeight;
    const seen = new Set<string>();
    document.querySelectorAll<HTMLElement>('button, [role=button], a[href], input, [role=tab]').forEach((el) => {
      const r = el.getBoundingClientRect();
      const cs = getComputedStyle(el);
      if (r.width === 0 || r.height === 0 || cs.visibility === 'hidden' || cs.display === 'none') return;
      if (r.bottom < 0 || r.top > vh) return;
      const label = (el.getAttribute('aria-label') || el.textContent || el.getAttribute('data-testid') || el.tagName).trim().slice(0, 30);
      if (seen.has(label)) return;
      seen.add(label);
      if (r.height < 43.5 || r.width < 43.5) out.push(`small target ${Math.round(r.width)}x${Math.round(r.height)} "${label}"`);
      if (r.right > vw + 1 || r.left < -1) out.push(`off-screen horizontally "${label}"`);
      if (el.scrollWidth > el.clientWidth + 1 && cs.overflow !== 'visible' && (el.textContent ?? '').trim()) out.push(`clipped label "${label}"`);
      if (loc === 'it') {
        const t = (el.textContent ?? '').trim();
        if (t && /^[A-Za-z ]+$/.test(t) && /^(Add|Added|Cancel|Close|Delete|Done|Export|Split|Trim|Undo|Redo|Play|Pause|Title|Text|Captions|Deliver|Save|Apply|More|Edit|Select|Draft|Quality|Download|Share)\b/.test(t)) out.push(`English in it: "${t.slice(0, 40)}"`);
      }
    });
    return out;
  }, locale);
}

test('a sheet opened from another sheet does not reload the editor: selection and undo survive', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await seekAndBlur(ed, 30);
  await ed.selectClipAt(15);
  await ed.tap(ed.testId('phone-action-split'));
  await ed.expectServer((t) => countItems(t) === 3, 'the clip was split');
  await ed.selectClipAt(45);
  await ed.page.evaluate(() => { (window as unknown as { __sameDocument: boolean }).__sameDocument = true; });
  await ed.tap(ed.testId('phone-action-more'));
  await ed.tap(ed.testId('phone-more-clip'));
  await expect(ed.testId('clip-sheet'), 'the clip sheet opened from the More sheet').toBeVisible();
  expect(await ed.page.evaluate(() => (window as unknown as { __sameDocument?: boolean }).__sameDocument), 'the page did not reload').toBe(true);
  await expect(ed.testId('phone-undo'), 'undo is still there').toBeEnabled();
});
