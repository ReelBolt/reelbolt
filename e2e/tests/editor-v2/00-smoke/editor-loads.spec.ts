import { test, expect } from '../../../fixtures/editor-v2';

/**
 * editor-crash-hotfix: the production build of the editor must load, in English and Italian, on the timeline
 * (Edit) page and on the Colour page, without an uncaught client error.
 *
 * Prod 5862f47a2 threw "ReferenceError: Cannot access 'p' before initialization" on every editor page as soon
 * as the workspace's activity feed held a job (the Preview 3 s overlay read a `const` inside a `useJobFor`
 * predicate before declaring it), and Next showed "This page couldn't load". The unit suite runs with an empty
 * feed and could not see it; this stack has runs in its feed, like a real workspace.
 */
for (const locale of ['en', 'it'] as const) {
  for (const query of ['', '?page=colour'] as const) {
    test(`the editor loads (${locale}, ${query ? 'Colour page' : 'timeline'})`, async ({ page, seed, baseURL }) => {
      const uncaught: string[] = [];
      page.on('pageerror', (e) => uncaught.push(`${e.name}: ${e.message}`));
      await page.context().addCookies([{ name: 'rb_locale', value: locale, url: new URL(baseURL!).origin }]);
      await page.goto(`/app/editor/${seed.projectId}/timeline/${seed.seededTimelineId}${query}`);
      await expect(page.getByTestId('editor-workspace')).toBeVisible({ timeout: 45_000 });
      // Desktop and tablet show the Colour page itself; the phone editor has no page tabs and opens its own shell.
      if (query && test.info().project.name === 'desktop') await expect(page.getByTestId('editor-workspace')).toHaveAttribute('data-workspace', 'color');
      // Give late mounts (activity feed, overlays) a moment to run with real data.
      await page.waitForTimeout(3_000);
      await expect(page.getByText(/This page couldn.t load/)).toHaveCount(0);
      expect(uncaught).toEqual([]);
    });
  }
}
