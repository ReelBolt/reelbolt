/**
 * Usability T-004: the run page. The stack has no engine, so the run, its workflow, its steps and its
 * output are served by page.route fixtures (read-only; nothing is started, nothing is charged).
 *
 *   B4  plain step list is the default, the failed step is in view, skipped is "Skipped" not "Waiting",
 *       Diagram is a secondary toggle
 *   B3/C11  plain failure message, raw text behind Details, "1 attempt", one banner, no "Time left: None",
 *       Resume vs Run again help lines
 *   B5  finished run: player + Download at the top
 */
import { expect, test, type Page } from '@playwright/test';
import { loginAs } from '../../../fixtures/stack';

const P = '11111111-1111-4111-8111-111111111111';
const W = '22222222-2222-4222-8222-222222222222';
const NAMES = ['Brief', 'Script', 'Storyboard', 'Casting', 'Voiceover', 'Shots', 'Music', 'Edit', 'Grade', 'DesignSystem', 'Captions', 'Render'];
const sid = (i: number) => `00000000-0000-4000-8000-0000000000${String(i).padStart(2, '0')}`;
const rid = (i: number) => `00000000-0000-4000-9000-0000000000${String(i).padStart(2, '0')}`;
const steps = NAMES.map((label, i) => ({
  id: sid(i + 1), agentDefinitionId: sid(90), stepOrder: i + 1, label, stepType: 'Agent',
}));
const result = (i: number, status: string, extra: Record<string, unknown> = {}) => ({
  id: rid(i), workflowStepId: sid(i), output: '', detailOmitted: true, tokensUsed: 10, durationMs: 12_000,
  executedAt: '2026-10-27T10:00:00Z', completedAt: '2026-10-27T10:00:12Z', status, ...extra,
});
const base = {
  workflowDefinitionId: W, iterationCount: 1, resultJson: null, reviewScores: [], brief: null, steps,
  startedAt: '2026-10-27T10:00:00Z', completedAt: '2026-10-27T10:05:00Z',
};
const RAW = 'Step 10 (DesignSystem) failed after 1 attempts. Last error: PM_V05 routingStrategy: value is 150 characters; the limit is 120.';
const done = (n: number) => Array.from({ length: n }, (_, k) => result(k + 1, 'Completed'));

const RUNS = {
  failedRaw: {
    ...base, id: 'aaaaaaaa-0000-4000-8000-000000000001', status: 'Failed', errorMessage: RAW, canResume: true,
    designSystemWarnings: ['The pinned design has no colour tokens.'],
    stepResults: [...done(9), result(10, 'Failed', { failureCode: 'CONFIG_INVALID' })],
  },
  failedPlain: {
    ...base, id: 'aaaaaaaa-0000-4000-8000-000000000002', status: 'Failed', canResume: false,
    errorMessage: 'The narration service was not available.',
    stepResults: [...done(4), result(5, 'Failed')],
  },
  passed: {
    ...base, id: 'aaaaaaaa-0000-4000-8000-000000000003', status: 'Passed',
    stepResults: [...done(3), result(5, 'Skipped'), ...done(12).slice(5)],
  },
  skipped: {
    ...base, id: 'aaaaaaaa-0000-4000-8000-000000000004', status: 'Passed',
    // steps 4 and 5 never ran (branch not taken): no result; step 6 has an explicit Skipped
    stepResults: [...done(3), result(6, 'Skipped'), ...done(12).slice(6)],
  },
} as const;
type RunKey = keyof typeof RUNS;

async function serve(page: Page, key: RunKey) {
  const run = RUNS[key];
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  await page.route(new RegExp(`/api/v1/projects/${P}/executions/${run.id}(\\?.*)?$`), (r) => r.fulfill(json(run)));
  await page.route(new RegExp(`/api/v1/projects/${P}/executions/${run.id}/step-results/.*`), (r) =>
    r.fulfill(json({ ...run.stepResults[0], detailOmitted: false })));
  await page.route(new RegExp(`/api/v1/projects/${P}/workflows/${W}(\\?.*)?$`), (r) =>
    r.fulfill(json({ id: W, name: 'Launch film', createdAt: run.startedAt, updatedAt: run.startedAt, requiresUserInput: false, steps })));
  await page.route(new RegExp(`/api/v1/projects/${P}/outputs(\\?.*)?$`), (r) =>
    r.fulfill(json(key === 'passed'
      ? [{ stepResultId: rid(12), workflowExecutionId: run.id, workflowStepId: sid(12), storageKey: 'k', fileName: 'launch-film.mp4', producedAt: run.completedAt, isFinal: true }]
      : [])));
  await page.route(new RegExp(`/api/v1/projects/${P}/outputs/.+/download`), (r) =>
    r.fulfill({ status: 200, contentType: 'video/mp4', body: '' }));
  await page.goto(`/app/projects/${P}/workflows/${W}/executions/${run.id}`);
}

const touch = (name: string) => name === 'pixel7' || name === 'iphone14webkit';

async function noOverflow(page: Page) {
  const o = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(o).toBeLessThanOrEqual(1);
}

test.beforeEach(async ({ page }) => {
  await loginAs(page, 'creator');
});

test('B4: failed run opens on the plain step list with the failed step in view', async ({ page }, info) => {
  await serve(page, 'failedRaw');
  const list = page.getByTestId('run-step-list');
  await expect(list).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('.react-flow')).toHaveCount(0);
  await expect(page.getByTestId('run-step-row')).toHaveCount(12);
  // Diagram is a secondary toggle: List is first and selected.
  await expect(page.getByRole('radio', { name: 'List' })).toBeChecked();
  await expect(page.getByRole('radio', { name: 'Diagram', exact: true })).not.toBeChecked();

  const failedRow = page.locator('[data-testid=run-step-row][data-state=Failed]');
  await expect(failedRow).toHaveCount(1);
  await expect(failedRow).toContainText('DesignSystem');
  await expect(failedRow).toContainText('Failed');
  await expect(failedRow).toContainText('10');
  await expect.poll(async () => {
    const b = await failedRow.boundingBox();
    const vh = page.viewportSize()!.height;
    return !!b && b.y >= 0 && b.y + b.height <= vh;
  }, { timeout: 10_000, message: 'failed row inside the viewport on load' }).toBe(true);

  // Steps 11 and 12 never ran on a failed run: Not run, never Waiting.
  await expect(page.locator('[data-testid=run-step-row][data-state=NotRun]')).toHaveCount(2);
  await expect(list.getByText('Waiting')).toHaveCount(0);
  await expect(list.getByText('Not run')).toHaveCount(2);

  // Rows: >=44px on touch, text >=12px everywhere in the list, no sideways scroll.
  const rows = await page.getByTestId('run-step-row').evaluateAll((els) => els.map((e) => e.querySelector('button')!.getBoundingClientRect().height));
  if (touch(info.project.name)) for (const h of rows) expect(h).toBeGreaterThanOrEqual(44);
  const small = await list.evaluate((root) => [...root.querySelectorAll('*')]
    .filter((e) => [...e.childNodes].some((n) => n.nodeType === 3 && n.textContent!.trim()))
    .map((e) => ({ text: (e.textContent ?? '').trim().slice(0, 20), px: parseFloat(getComputedStyle(e).fontSize) }))
    .filter((s) => s.px < 12));
  expect(small).toEqual([]);
  await noOverflow(page);
  await page.screenshot({ path: info.outputPath('b4-failed.png') });

  // Keyboard: a row is a button that can be focused and activated.
  await failedRow.locator('button').focus();
  await expect(failedRow.locator('button')).toBeFocused();

  // Diagram on request.
  await page.getByText('Diagram', { exact: true }).click();
  await expect(page.locator('.react-flow')).toBeVisible();
  await expect(list).toHaveCount(0);
});

test('B4: a finished run with a branch not taken says Skipped, never Waiting', async ({ page }) => {
  await serve(page, 'skipped');
  const list = page.getByTestId('run-step-list');
  await expect(list).toBeVisible({ timeout: 30_000 });
  await expect(list.getByText('Waiting')).toHaveCount(0);
  await expect(page.locator('[data-testid=run-step-row][data-state=Skipped]')).toHaveCount(3);
  await expect(page.locator('[data-testid=run-step-row][data-state=Pending]')).toHaveCount(0);
  await noOverflow(page);
});

test('B3/C11: failure is plain, raw text sits behind Details, one banner, no Time left', async ({ page }, info) => {
  await serve(page, 'failedRaw');
  const box = page.getByTestId('run-failure');
  await expect(box).toBeVisible({ timeout: 30_000 });
  await expect(box).toContainText('What went wrong');
  // Plain sentence only: no raw engine text in the visible summary.
  // innerText: the collapsed Details text is still in the DOM, but is not visible.
  const visible = await box.innerText();
  expect(visible).not.toContain('Step 10 (DesignSystem)');
  expect(visible).not.toContain('PM_V05');
  expect(visible).not.toContain('1 attempts');
  await expect(page.getByTestId('run-failure-details')).toBeHidden();
  await box.getByRole('button', { name: 'Details' }).click();
  const details = page.getByTestId('run-failure-details');
  await expect(details).toBeVisible();
  await expect(details).toContainText('PM_V05 routingStrategy');
  await expect(details).toContainText('1 attempt.');
  await expect(details).not.toContainText('1 attempts');
  expect(await page.locator('body').innerText()).not.toContain('1 attempts');
  // One banner: no stacked warning or attention alert above the failure.
  await expect(page.getByTestId('run-failure')).toHaveCount(1);
  await expect(page.getByTestId('needs-attention-alert')).toHaveCount(0);
  await expect(page.getByText('The pinned design has no colour tokens.')).toHaveCount(0);
  // No "Time left" tile on a failed run.
  await expect(page.getByText('Time left')).toHaveCount(0);
  const body = await page.locator('body').innerText();
  expect(body).not.toMatch(/Time left:?\s*None/i);
  await noOverflow(page);
  await page.screenshot({ path: info.outputPath('b3-failed.png'), fullPage: true });
});

test('B3: a message already in plain words is shown as is, with no Details', async ({ page }) => {
  await serve(page, 'failedPlain');
  const box = page.getByTestId('run-failure');
  await expect(box).toContainText('The narration service was not available.', { timeout: 30_000 });
  await expect(box.getByRole('button', { name: 'Details' })).toHaveCount(0);
  await expect(page.getByText('Time left')).toHaveCount(0);
  await expect(page.getByText(/Resume.*continues from the step that stopped/)).toHaveCount(0);
  await expect(page.getByText(/Run again.*starts over from step 1/)).toBeVisible();
  const fail = page.locator('[data-testid=run-step-row][data-state=Failed]');
  await expect.poll(async () => {
    const b = await fail.boundingBox();
    return !!b && b.y >= 0 && b.y + b.height <= page.viewportSize()!.height;
  }, { timeout: 10_000 }).toBe(true);
});

test('B3: Resume and Run again each have one help line about credits', async ({ page }) => {
  await serve(page, 'failedRaw');
  await expect(page.getByTestId('run-failure')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole('button', { name: 'Resume', exact: true })).toBeVisible();
  const resume = page.getByText(/Resume.*continues from the step that stopped/);
  const again = page.getByText(/Run again.*starts over from step 1/);
  await expect(resume).toHaveCount(1);
  await expect(again).toHaveCount(1);
  await expect(resume).toContainText('not charged again');
  await expect(again).toContainText('uses credits for every step again');
  await expect(again).toContainText('returned automatically');
});

test('B5: a finished run shows the player and Download at the top', async ({ page }, info) => {
  await serve(page, 'passed');
  const card = page.getByTestId('run-result-player');
  await expect(card).toBeVisible({ timeout: 30_000 });
  await expect(card.locator('video')).toBeVisible();
  const dl = card.getByRole('link', { name: 'Download' });
  await expect(dl).toBeVisible();
  await expect(dl).toHaveAttribute('href', /\/outputs\/.+\/download\?saveAs=launch-film\.mp4$/);
  const dlBox = (await dl.boundingBox())!;
  if (touch(info.project.name)) expect(dlBox.height).toBeGreaterThanOrEqual(44);
  // Above the status card.
  const playerY = (await card.boundingBox())!.y;
  const summaryY = (await page.getByTestId('run-summary').boundingBox())!.y;
  expect(playerY).toBeLessThan(summaryY);
  // No failure box and no Resume help on a passed run; no stacked warnings.
  await expect(page.getByTestId('run-failure')).toHaveCount(0);
  await expect(page.getByText('Time left')).toHaveCount(0);
  await noOverflow(page);
  await page.screenshot({ path: info.outputPath('b5-passed.png') });
});

test('C11: home failed-run card shows the plain message and a 44px dismiss on touch', async ({ page }, info) => {
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  const exec = {
    id: RUNS.failedRaw.id, projectId: P, projectName: 'Launch', workflowDefinitionId: W, workflowName: 'Launch film',
    status: 'Failed', startedAt: RUNS.failedRaw.startedAt, completedAt: RUNS.failedRaw.completedAt,
    errorMessage: RAW, userRequest: null, outputStepResultId: null, outputFileName: null,
  };
  await page.route(/\/api\/v1\/projects(\?.*)?$/, (r) => r.fulfill(json([{ id: P, name: 'Launch', createdAt: base.startedAt, updatedAt: base.startedAt }])));
  await page.route(/\/api\/v1\/dashboard\/overview(\?.*)?$/, (r) => r.fulfill(json({
    projects: [], activeExecutions: [], recentExecutions: [exec], needsAttention: [exec], recentOutputs: [],
    totals: { projects: 1, sourceFiles: 0, renders: 0, passedLast7Days: 0, failedLast7Days: 1 },
  })));
  await page.goto('/app/dashboard');
  const panel = page.locator('section[aria-labelledby=needs-attention]');
  await expect(panel).toBeVisible({ timeout: 30_000 });
  const text = await panel.innerText();
  expect(text).not.toContain('Step 10 (DesignSystem)');
  expect(text).not.toContain('PM_V05');
  expect(text).not.toContain('1 attempts');
  const dismiss = panel.getByRole('button', { name: 'Dismiss these failed run warnings' });
  await expect(dismiss).toBeVisible();
  const b = (await dismiss.boundingBox())!;
  if (touch(info.project.name)) {
    expect(b.height).toBeGreaterThanOrEqual(44);
    expect(b.width).toBeGreaterThanOrEqual(44);
  }
  await noOverflow(page);
  await page.screenshot({ path: info.outputPath('c11-home.png') });
});
