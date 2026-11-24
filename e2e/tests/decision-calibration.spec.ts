import { expect, test } from '../fixtures/auth';
import type { Locator, Page } from '@playwright/test';

/** The shape `Locator.boundingBox()` resolves to. `@playwright/test` does not re-export its type. */
interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * `/app/admin/calibration` — the decision-model calibration view.
 *
 * SAFETY (hard rules for this file):
 *  - never contacts a live Decision provider, never enters a real key, never spends money;
 *  - configures no provider and collects no observation. With no `Decision` provider configured
 *    the database holds zero `decision_observations`, and that zero-observation state is the state
 *    this stack can actually reach — the spec asserts the page renders it honestly rather than
 *    drawing a chart of nothing (the populated branch is written to run the moment a stack has rows);
 *  - creates nothing, and the last test proves it: the view is checked to issue no write request.
 *
 * Every assertion is about what the user sees. Copy and accessible names are read off the page and
 * its components: `web/app/(app)/admin/calibration/page.tsx`,
 * `web/components/calibration/ReliabilityChart.tsx`, `web/components/shared/EmptyState.tsx`.
 *
 * The page is: a header, a "How this page works" panel, one card with three controls (the
 * Accept-at confidence slider, the Minimum margin slider, a Provider select), then either the empty
 * state or the report (totals, per-agent cards, a reliability chart for one question).
 */

const BASE_URL = process.env.BASE_URL || 'http://localhost';
const CALIBRATION_PATH = '/app/admin/calibration';

/** `PageHeader title="Decision Calibration"` → `<h2>`. */
const PAGE_TITLE = 'Decision Calibration';

/** `EmptyState title` / `description` props, verbatim. */
const EMPTY_TITLE = 'No decision observations recorded';
const EMPTY_NAMES_PROVIDER =
  'A Decision provider must be configured and the decision gate set to Shadow mode';
const EMPTY_REFUSES_CHART = 'this page will not draw an empty chart';

/** The page's own failure copy (`!report`, not loading) — asserted absent so a 403/500 is loud. */
const LOAD_ERROR_COPY = 'Could not load the decision calibration report.';

/** `ReliabilityChart`'s `aria-label` — the svg is a `role="img"`. */
const DIAGRAM_LABEL = 'Reliability diagram: observed agreement against predicted confidence';

type ReportState = 'empty' | 'populated';

/**
 * Confirms the session this spec drives is an admin one, **before** any admin page is opened.
 * `/app/admin/calibration` is admin-only on the backend (403 otherwise). Credentials come from the
 * environment exactly as `e2e/fixtures/auth.ts` reads them.
 */
async function assertAdminSession(page: Page): Promise<void> {
  const email = process.env.ADMIN_EMAIL || process.env.TEST_EMAIL || 'test@example.com';
  const password = process.env.ADMIN_PASSWORD || process.env.TEST_PASSWORD || 'password123';

  const login = await page.request.post(`${BASE_URL}/api/v1/auth/token`, { data: { email, password } });
  expect(login.status(), `admin login as ${email} failed`).toBe(200);

  const body = (await login.json()) as { isAdmin?: boolean; mustChangePassword?: boolean };
  expect(
    body.isAdmin === true,
    `User ${email} is not an admin: the calibration view is admin-only. ` +
      'Set ADMIN_EMAIL/ADMIN_PASSWORD to an admin account.'
  ).toBe(true);
  expect(
    body.mustChangePassword === true,
    `Admin ${email} must change its password before it can use the admin UI.`
  ).toBe(false);
}

const acceptAtSlider = (page: Page) => page.getByRole('slider', { name: 'Accept-at confidence' });
const minMarginSlider = (page: Page) => page.getByRole('slider', { name: 'Minimum margin' });

/** The three controls the page's control card always mounts, whatever the report turns out to hold. */
function calibrationControls(page: Page): { name: string; locator: Locator }[] {
  return [
    { name: 'Accept-at confidence', locator: acceptAtSlider(page) },
    { name: 'Minimum margin', locator: minMarginSlider(page) },
    { name: 'Provider', locator: page.getByRole('combobox', { name: 'Provider', exact: true }) },
  ];
}

const diagram = (page: Page) => page.getByRole('img', { name: DIAGRAM_LABEL });

/**
 * Which of the page's two report states is on screen, read from the DOM rather than the API.
 */
async function waitForReportState(page: Page): Promise<ReportState> {
  const emptyState = page.getByText(EMPTY_TITLE, { exact: true });
  const populatedTile = page.getByText('Observations', { exact: true }).first();
  await expect(emptyState.or(populatedTile).first()).toBeVisible({ timeout: 30000 });
  return (await emptyState.count()) > 0 ? 'empty' : 'populated';
}

/**
 * Moves the Accept-at slider one step from the keyboard (a drag is not needed: the slider commits
 * a new report request on key release) and returns the value it now carries. The label above the
 * slider reads "Accept-at confidence: 0.90" once the value has been applied.
 */
async function bumpAcceptAt(page: Page): Promise<string> {
  const slider = acceptAtSlider(page);
  await expect(slider).toBeVisible();
  const before = Number.parseFloat((await slider.getAttribute('aria-valuenow')) ?? '');
  const key = before >= 1 ? 'ArrowLeft' : 'ArrowRight';
  await slider.focus();
  await slider.press(key);
  const next = (before + (key === 'ArrowRight' ? 0.05 : -0.05)).toFixed(2);
  await expect(page.getByText(`Accept-at confidence: ${next}`, { exact: true })).toBeVisible();
  return next;
}

test.describe('decision-calibration', () => {
  test.describe.configure({ mode: 'parallel' });

  test('renders the calibration heading, its report controls and the loaded report (desktop + mobile)', async ({
    authenticatedPage: page,
  }, testInfo) => {
    test.setTimeout(90000);
    await assertAdminSession(page);

    await page.goto(CALIBRATION_PATH);
    await expect(page).toHaveURL(/\/app\/admin\/calibration$/);

    // The heading is the page's own `<h2>` from `PageHeader` — proof we are on the calibration
    // view and not on an admin area that happens to also be reachable.
    await expect(page.getByRole('heading', { name: PAGE_TITLE, level: 2 })).toBeVisible();

    // The controls are mounted unconditionally, including while the report is in flight.
    const controls = calibrationControls(page);
    for (const { locator } of controls) {
      await expect(locator).toBeVisible();
    }

    // The controls prove nothing about the report: wait for the report itself. Either the
    // populated view or the honest empty state must arrive, and the failure copy must not.
    await waitForReportState(page);
    await expect(page.getByText(LOAD_ERROR_COPY, { exact: true })).toHaveCount(0);

    // Layout branch: a three-column grid from the `md` breakpoint, a single column below it.
    const boxes = await Promise.all(controls.map(({ locator }) => locator.boundingBox()));
    const unlaidOut = controls.filter((_, i) => boxes[i] === null).map(({ name }) => name);
    expect(unlaidOut, 'every control must have a layout box').toEqual([]);
    const [acceptAtBox, minMarginBox, providerBox] = boxes as Box[];

    if (testInfo.project.name === 'mobile') {
      expect(minMarginBox.y).toBeGreaterThan(acceptAtBox.y + acceptAtBox.height);
      expect(providerBox.y).toBeGreaterThan(minMarginBox.y);
    } else {
      await expect(page.getByRole('link', { name: 'Calibration', exact: true })).toBeVisible();
      // Side by side: three columns, left to right, none wrapped under another (the controls differ
      // in height, so a shared top edge is not the test).
      expect(minMarginBox.x).toBeGreaterThan(acceptAtBox.x + acceptAtBox.width - 1);
      expect(providerBox.x).toBeGreaterThan(minMarginBox.x + minMarginBox.width - 1);
      expect(Math.abs(providerBox.y - acceptAtBox.y)).toBeLessThan(150);
    }
  });

  test('shows the reliability diagram when the report has buckets and the honest empty state when it does not', async ({
    authenticatedPage: page,
  }) => {
    test.setTimeout(90000);
    await assertAdminSession(page);

    await page.goto(CALIBRATION_PATH);
    const state = await waitForReportState(page);

    if (state === 'empty') {
      // Zero observations. The page must say so plainly, must name what is missing, and must NOT
      // draw a chart of nothing.
      await expect(page.getByText(EMPTY_TITLE, { exact: true })).toBeVisible();
      await expect(page.getByText(new RegExp(EMPTY_NAMES_PROVIDER))).toBeVisible();
      await expect(page.getByText(new RegExp(EMPTY_REFUSES_CHART))).toBeVisible();
      await expect(diagram(page)).toHaveCount(0);

      // Changing a threshold re-queries and re-renders. With no observations the honest answer
      // does not change: no fabricated chart appears, and the view stays readable.
      await bumpAcceptAt(page);
      await expect(page.getByText(EMPTY_TITLE, { exact: true })).toBeVisible();
      await expect(diagram(page)).toHaveCount(0);
      await expect(page.getByText(LOAD_ERROR_COPY, { exact: true })).toHaveCount(0);
      return;
    }

    // Populated: at least one question to chart, and therefore a diagram.
    await expect(diagram(page).first()).toBeVisible();
    expect(await diagram(page).count()).toBeGreaterThan(0);

    // "Tokens saved" is a projection computed at the thresholds in force, and the tile says so
    // rather than presenting it as a measurement.
    await expect(page.getByText('Projected at these thresholds', { exact: true })).toBeVisible();

    // Changing a threshold re-renders the escalation tile: the rate is recomputed at the
    // thresholds now in force, and the tile quotes them.
    const target = await bumpAcceptAt(page);
    await expect(page.getByText(new RegExp(`At accept-at ${target.replace('.', '\\.')} / margin`))).toBeVisible();
    await expect(diagram(page).first()).toBeVisible();
  });

  test('is a read-only view: nothing is created and no write request is issued', async ({
    authenticatedPage: page,
  }) => {
    test.setTimeout(90000);
    await assertAdminSession(page);

    // `decision_observations` can only be written by an executing workflow; this page reports on
    // it and must not mutate anything. Watching the page's own traffic is how "creates nothing"
    // is evidenced rather than asserted.
    const writes: string[] = [];
    page.on('request', (request) => {
      const path = new URL(request.url()).pathname;
      if (path.startsWith('/api/v1/') && request.method() !== 'GET') {
        writes.push(`${request.method()} ${path}`);
      }
    });

    await page.goto(CALIBRATION_PATH);
    await waitForReportState(page);

    // Exercise both threshold controls: the only interactive state on the page, and the only
    // place a write could hide.
    await bumpAcceptAt(page);
    const margin = minMarginSlider(page);
    await margin.focus();
    await margin.press('ArrowRight');
    await waitForReportState(page);

    expect(writes, 'the calibration view must issue no write request').toEqual([]);
  });
});
