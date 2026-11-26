import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import type { Timeline } from '../../../fixtures/editor-v2/api';
import { knownFailure } from '../expected-failures';

/**
 * T0.4 flush guards (audit top-10 #9, eng D8/D9/D10): an edit followed by navigation within 300 ms
 * must still reach the server, and autosave must run during playback.
 */

const itemCount = (tl: Timeline) => tl.document.tracks.reduce((n, t) => n + t.items.length, 0);

/**
 * Blade at the playhead, then leave ("All timelines" at once, reload 100 ms later), from one page script: the
 * gap is exact whatever the input round trip costs on the device (a phone tap alone can exceed 300 ms).
 */
async function bladeThenLeave(ed: EditorPage, leave: 'back' | 'reload'): Promise<void> {
  await ed.selectClipUnderPlayhead();
  await ed.page.evaluate((how) => {
    const visible = (id: string) => [...document.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`)].find((e) => e.offsetParent !== null)!;
    // (the toolbar scissors on desktop and tablet, the action bar's Split on a phone)
    const w = window as unknown as { __e2eLeftMs?: number; __e2ePatchMs?: number; __e2eFetch?: typeof fetch };
    delete w.__e2eLeftMs;
    delete w.__e2ePatchMs;
    // When the first PATCH after the edit LEAVES the page (the client route change keeps this document):
    // one sent before the autosave debounce could fire can only come from the leave path.
    w.__e2eFetch ??= window.fetch;
    const realFetch = w.__e2eFetch;
    window.fetch = (input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'PATCH' && w.__e2ePatchMs === undefined) w.__e2ePatchMs = performance.now() - t0;
      return realFetch(input, init);
    };
    const canvas = visible('timeline-canvas');
    const t0 = performance.now();
    (visible('blade-at-playhead') ?? visible('phone-action-split')).click();
    if (how === 'back') {
      // Record when the editor really unmounts: a client route change keeps it mounted until the next route's
      // payload arrives, which on a loaded machine can outlast the 500 ms autosave debounce.
      const obs = new MutationObserver(() => {
        if (!canvas.isConnected) { w.__e2eLeftMs = performance.now() - t0; obs.disconnect(); }
      });
      obs.observe(document.body, { childList: true, subtree: true });
      (visible('back-to-timelines') ?? visible('phone-back')).click(); // the phone header's back arrow
    } else {
      setTimeout(() => location.reload(), 100);
    }
  }, leave);
}

test.describe('guards: edits survive leaving the page', () => {
  test('GUARD-NAV-300MS P3-SYNC-10 edit then "All timelines" within 300 ms keeps the edit', async ({ freshTimeline, openEditor }) => {
    knownFailure('GUARD-NAV-300MS');
    const ed = await openEditor(await freshTimeline('seeded'));
    // Only an attempt whose edit was saved BY LEAVING decides the guard: the editor unmounted before the
    // 500 ms autosave debounce fired, so the first PATCH left earlier than the debounce could send one
    // (under 450 ms after the edit). On desktop and Pixel 7 that is the 300 ms unmount the row names. Linux
    // WebKit took 385-3544 ms to switch routes on the shared stack (T-073): an attempt it unmounts in time
    // proves the same flush; one the debounce saved first is retried (fresh load, a new cut point) and must
    // still have stored the edit, but never decides the guard.
    const tries: string[] = [];
    for (const frame of [75, 45, 105, 135]) {
      if (tries.length) await ed.goto();
      const before = await ed.serverTimeline();
      await ed.seek(frame);
      await bladeThenLeave(ed, 'back');
      await expect(ed.page).not.toHaveURL(new RegExp(`/timeline/${ed.timelineId}`));
      await expect.poll(() => ed.page.evaluate(() => (window as unknown as { __e2eLeftMs?: number }).__e2eLeftMs ?? -1)).toBeGreaterThanOrEqual(0);
      const { left, patch } = await ed.page.evaluate(() => {
        const w = window as unknown as { __e2eLeftMs: number; __e2ePatchMs?: number };
        return { left: w.__e2eLeftMs, patch: w.__e2ePatchMs ?? null };
      });
      tries.push(`unmounted ${Math.round(left)} ms, first PATCH ${patch === null ? 'none' : `${Math.round(patch)} ms`}`);
      const stored = (tl: Timeline) => itemCount(tl) === itemCount(before) + 1;
      if (patch !== null && patch >= 450) {
        // The autosave debounce sent it: the edit must still be stored, but this attempt does not prove the flush.
        await ed.expectServer(stored, `the edit made before leaving (${Math.round(left)} ms) is stored`, 10_000);
        continue;
      }
      await ed.expectServer(stored, `the edit made ${Math.round(left)} ms before leaving is stored`, 10_000);
      return;
    }
    throw new Error(`the editor never left before the autosave debounce saved the edit (${tries.join('; ')}): the guard could not run`);
  });

  test('GUARD-RELOAD-300MS P3-SYNC-10 edit then reload within 300 ms keeps the edit', async ({ freshTimeline, openEditor }) => {
    knownFailure('GUARD-RELOAD-300MS');
    const ed = await openEditor(await freshTimeline('seeded'));
    const before = await ed.serverTimeline();
    await ed.seek(75);
    await Promise.all([ed.page.waitForEvent('load'), bladeThenLeave(ed, 'reload')]);
    await expect(ed.canvas).toBeVisible({ timeout: 45_000 });
    await ed.expectServer((tl) => itemCount(tl) === itemCount(before) + 1, 'the edit made just before the reload is stored', 10_000);
  });
});

test.describe('guards: autosave during playback', () => {
  test('GUARD-PLAYBACK-AUTOSAVE P3-SYNC-05 an edit saves during 11 s of playback', async ({ freshTimeline, openEditor }) => {
    knownFailure('GUARD-PLAYBACK-AUTOSAVE');
    const ed = await openEditor(await freshTimeline('seeded'));
    const before = await ed.serverTimeline();
    // Audit d1 D9: the edit is made WHILE playing (playhead ticks reset `dirty`), so start playback first.
    await ed.seek(30);
    await ed.tap(ed.testId('transport-play'));
    await expect(ed.testId('status-timecode'), 'playback is running').not.toHaveText('00:00:01:00', { timeout: 5000 });
    const started = Date.now();
    try {
      await ed.splitAtPlayhead();
      await ed.expectServer((tl) => itemCount(tl) === itemCount(before) + 1, 'the edit is stored while playback runs', 11_000);
    } finally {
      if (Date.now() - started < 11_000) await ed.page.waitForTimeout(Math.max(0, 11_000 - (Date.now() - started)));
      await ed.tap(ed.testId('transport-play'));
    }
  });
});
