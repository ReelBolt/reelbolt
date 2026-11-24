import type { Page, Route } from '@playwright/test';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { clipsTimeline, PAIR, type ClipSpec } from '../../../fixtures/editor-v2/lane-a';
import { meanLuma, sampleElement } from '../../../fixtures/editor-v2/pixels';
import type { Seed } from '../../../fixtures/editor-v2/seed';

/**
 * R04.1 preview readiness and R04.2 the truthful, reachable parity badge (T-010).
 *
 * The seeded sources already have their proxies, so "proxies are not ready" is staged at the two seams the
 * viewer reads: the derivative lookup (GET .../media/{ref}/derivatives) is HELD until the test releases it,
 * and the progress feed (SSE /api/v1/workflows/events) serves the pipeline's current stage. Everything the
 * assertions check is what the user sees: the reason line, the percent, Try again, the picture's pixels.
 */

interface Staged {
  /** Lets the held derivative lookups through (the viewer then finds the real proxy). */
  release(): void;
  /** The stage the progress feed reports for `file` (all staged files when omitted), and the percent. Call after the editor opened. */
  progress(stage: string, percent: number | null, file?: string): Promise<void>;
  requests: Array<{ mediaRef?: string; requestedBy?: string }>;
}

/**
 * Stages the two seams the viewer reads. The progress feed is a stand-in EventSource for /api/v1/workflows/events
 * (installed before the page runs, so it works in every engine - WebKit does not route EventSource through
 * page.route); the derivative lookups are held until released.
 */
async function stage(page: Page, seed: Seed, files: string[]): Promise<Staged> {
  let release: () => void = () => undefined;
  const released = new Promise<void>((resolve) => {
    release = resolve;
  });
  const requests: Staged['requests'] = [];
  await page.addInitScript((projectId: string) => {
    const w = window as unknown as { EventSource: typeof EventSource; __rbProgress: { set(file: string, stage: string, percent: number | null): void } };
    const RealEventSource = w.EventSource;
    const state = new Map<string, { stage: string; percent: number | null }>();
    const open = new Set<EventTarget>();
    const frame = (file: string, e: { stage: string; percent: number | null }) =>
      new MessageEvent('media.derivative.progress', {
        data: JSON.stringify({ type: 'media.derivative.progress', projectId, data: { projectId, sourceProjectFileId: file, stage: e.stage, percentComplete: e.percent } }),
      });
    class FakeFeed extends EventTarget {
      readyState = 1;
      constructor(public url: string) {
        super();
        open.add(this);
        // A late subscriber hears the current stage of every source, as a reconnecting feed would replay it.
        setTimeout(() => state.forEach((e, file) => this.dispatchEvent(frame(file, e))), 0);
      }
      close() {
        this.readyState = 2;
        open.delete(this);
      }
    }
    w.EventSource = function (url: string | URL, init?: EventSourceInit) {
      return String(url).includes('/api/v1/workflows/events') ? (new FakeFeed(String(url)) as unknown as EventSource) : new RealEventSource(url, init);
    } as unknown as typeof EventSource;
    w.__rbProgress = {
      set(file, stg, percent) {
        const e = { stage: stg, percent };
        state.set(file, e);
        open.forEach((feed) => feed.dispatchEvent(frame(file, e)));
      },
    };
  }, seed.projectId);
  await page.route(/\/media\/[^/]+\/derivatives(\?.*)?$/, async (route: Route) => {
    if (route.request().method() !== 'GET') return route.continue();
    await released;
    await route.continue();
  });
  page.on('request', (request) => {
    if (request.method() !== 'POST' || !/\/media\/derivatives\/request$/.test(new URL(request.url()).pathname)) return;
    try {
      requests.push(JSON.parse(request.postData() ?? '{}') as { mediaRef?: string; requestedBy?: string });
    } catch {
      /* not JSON */
    }
  });
  return {
    release,
    progress: async (st, percent, file) => {
      await page.evaluate(
        ([targets, stg, pct]) => {
          const w = window as unknown as { __rbProgress: { set(file: string, stage: string, percent: number | null): void } };
          for (const f of targets as string[]) w.__rbProgress.set(f, stg as string, pct as number | null);
        },
        [file ? [file] : files, st, percent] as const,
      );
    },
    requests,
  };
}

const startOf = async (ed: EditorPage) => {
  await ed.seek(30);
  await expect(ed.page.getByTestId('program-frame')).toHaveAttribute('data-booted', 'true', { timeout: 30_000 });
};

test('R04.1 a timeline whose previews are not ready says why with its progress, then becomes a picture', async ({ freshTimeline, openEditor, api, seed, page }) => {
  test.setTimeout(180_000);
  const staged = await stage(page, seed, [seed.files.clipA, seed.files.clipB]);
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await startOf(ed);
  await staged.progress('Proxy', 42);

  // Not ready: the viewer is not a silent black box. It says what is happening, with the percent and a bar.
  await expect(ed.testId('preview-notice')).toHaveText('Preparing preview 42%', { timeout: 30_000 });
  await expect(ed.testId('preview-progress')).toBeVisible();
  await expect(ed.testId('readiness-label')).toHaveText('Preparing preview 42%');
  const notReady = meanLuma(await sampleElement(ed.page, ed.viewer));
  expect(notReady, 'nothing but the explanation is drawn yet (its white text lifts a phone-sized viewer a little)').toBeLessThan(26);

  // The pipeline finishes: the lookups answer, the reason goes, a real picture is there.
  await staged.progress('Done', 100);
  staged.release();
  await expect(ed.testId('preview-notice')).toHaveCount(0, { timeout: 60_000 });
  await expect.poll(async () => meanLuma(await sampleElement(ed.page, ed.viewer)), { message: 'the picture is not black', timeout: 60_000, intervals: [1000] }).toBeGreaterThan(Math.max(15, notReady + 3));
  await expect(ed.testId('readiness-chip')).toHaveCount(0);
});

test('R04.1 a failed preparation says why and Try again asks the pipeline again, then plays', async ({ freshTimeline, openEditor, api, seed, page }) => {
  test.setTimeout(180_000);
  const staged = await stage(page, seed, [seed.files.clipA, seed.files.clipB]);
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await startOf(ed);
  await staged.progress('Failed', null);

  await expect(ed.testId('preview-notice')).toContainText(/^Preview failed: the file could not be converted/, { timeout: 30_000 });
  await expect(ed.testId('readiness-label')).toHaveText('Preview failed for 2 clips');
  const before = staged.requests.length;
  await ed.tap(ed.testId('preview-retry'));
  await staged.progress('Queued', null); // the pipeline starts a fresh run
  await expect.poll(() => staged.requests.slice(before).filter((r) => r.requestedBy === 'preview-retry').map((r) => r.mediaRef), { message: 'Try again asked for the clip under the playhead' }).toEqual([`file:${seed.files.clipA}`]);
  await expect(ed.testId('preview-notice')).toHaveText(/^Preparing preview/, { timeout: 30_000 });

  await staged.progress('Done', 100);
  staged.release();
  await expect.poll(async () => meanLuma(await sampleElement(ed.page, ed.viewer)), { message: 'after Try again the picture is there', timeout: 60_000, intervals: [1000] }).toBeGreaterThan(15);
});

test('R04.1 Prepare all lists every clip and asks once per source', async ({ freshTimeline, openEditor, api, seed, page }) => {
  test.setTimeout(180_000);
  const staged = await stage(page, seed, [seed.files.clipA, seed.files.clipB]);
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await startOf(ed);
  await staged.progress('Proxy', 10, seed.files.clipA);
  await staged.progress('Failed', null, seed.files.clipB);

  await ed.tap(ed.testId('readiness-chip'));
  const rows = ed.page.getByTestId('readiness-row');
  await expect(rows).toHaveCount(2);
  await expect(rows.nth(0)).toContainText('clipA.mp4');
  await expect(rows.nth(0)).toContainText('Preparing preview 10%');
  await expect(rows.nth(1)).toContainText('Preview failed');
  const before = staged.requests.length;
  await ed.tap(ed.testId('prepare-all'));
  await expect
    .poll(() => staged.requests.slice(before).filter((r) => r.requestedBy === 'preview-retry').map((r) => r.mediaRef).sort(), { message: 'one request per source' })
    .toEqual([`file:${seed.files.clipA}`, `file:${seed.files.clipB}`].sort());
});

const SLOW_AND_FAST: ClipSpec[] = [
  { name: 'Interview', file: 'clipA', recordIn: 0, recordOut: 60, extra: { playbackRate: 0.5 } },
  { name: 'Broll', file: 'clipB', recordIn: 60, recordOut: 120, extra: { playbackRate: 2 } },
];

test('R04.2 the badge opens its reasons by tap and a reason jumps to its clip', async ({ freshTimeline, openEditor, api, seed }) => {
  test.setTimeout(120_000);
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, SLOW_AND_FAST));
  await ed.seek(10);
  const badge = ed.testId('parity-badge');
  await expect(badge, 'a speed change is never "faithful"').not.toHaveAttribute('data-parity', 'faithful', { timeout: 30_000 });
  await ed.tap(badge);
  const reasons = ed.page.getByTestId('parity-reasons');
  await expect(reasons).toBeVisible();
  await expect(reasons).toContainText('Clip "Interview": Slow motion');
  await expect(reasons).toContainText('exact');
  await expect(ed.page.getByTestId('parity-elsewhere')).toContainText('Clip "Broll": Fast motion');

  // Tap the line about the OTHER clip: the playhead goes to it and it is the selection.
  await ed.tap(ed.page.getByTestId('parity-elsewhere').getByTestId('parity-reason').first());
  await expect(ed.testId('status-timecode')).toHaveText('00:00:02:00');
  await ed.openInspector();
  await expect(ed.testId('inspector-parity-reason')).toContainText(/fast motion/i, { timeout: 15_000 });
  expect(ed.patches, 'looking at reasons writes nothing').toEqual([]);
});
