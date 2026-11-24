import { expect, test, type Page } from '@playwright/test';
import { PROJECT_ID, mockApi, signIn } from './fixtures';

// The app run page (PR #549) at /projects/:id/workflows/:wid. Runs under both WA_FLAG states; each
// describe skips itself in the other state (the config picks this spec up for on and off).
const FLAG_ON = process.env.WA_FLAG !== 'off';
const WID = 'wf-run';
const now = new Date().toISOString();
const base = `/api/v1/projects/${PROJECT_ID}`;
const json = (body: unknown, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(body) });

const workflow = { id: WID, projectId: PROJECT_ID, name: 'Launch teaser', steps: [], requiresUserInput: false, createdAt: now, updatedAt: now };
const appResponse = {
  workflowId: WID, name: 'Launch teaser', hasExplicitManifest: true, costClass: 'Uses credits', source: 'promo-teaser',
  manifest: {
    version: 1, title: 'Launch teaser', tagline: 'A short promo', category: 'promo',
    inputs: [
      { kind: 'files', role: 'video', label: 'Your clips', min: 1, max: 3 },
      { kind: 'brief', field: 'language', label: 'Language' },
      { kind: 'notes', label: 'Anything else?' },
    ],
    result: { kind: 'video' },
  },
};
const estimate = {
  billingEnabled: true, hasHistory: true, lowCredits: 8, highCredits: 14, typicalCredits: 10, fixedMaxCredits: 0,
  requiredCredits: 14, balanceCredits: 500, affordable: true, steps: [],
};
const clip = {
  id: 'f1', projectId: PROJECT_ID, originalFileName: 'beach.mp4', category: 'userFiles', mimeType: 'video/mp4', sizeBytes: 1000,
  storageKey: 'k1', agentSummary: null, summaryStatus: 'Done', indexingStatus: 'Indexed',
};
const execution = (status: string, extra: Record<string, unknown> = {}) => ({
  id: 'ex1', workflowDefinitionId: WID, status, startedAt: now, completedAt: status === 'Running' ? null : now,
  iterationCount: 1, resultJson: null, stepResults: [], reviewScores: [], ...extra,
});

interface Scenario { files?: unknown[]; executions?: unknown[]; outputs?: unknown[] }

async function setup(page: Page, baseURL: string, sc: Scenario = {}) {
  await signIn(page, baseURL);
  await mockApi(page);
  const re = (tail: string) => new RegExp(`${base}/${tail}(\\?.*)?$`);
  await page.route(re(`workflows/${WID}`), (r) => r.fulfill(json(workflow)));
  await page.route(re(`workflows/${WID}/app`), (r) => r.fulfill(json(appResponse)));
  await page.route(re(`workflows/${WID}/estimate`), (r) => r.fulfill(json(estimate)));
  await page.route(re(`workflows/${WID}/lint`), (r) => r.fulfill(json({ findings: [], errors: 0, warnings: 0, infos: 0 })));
  await page.route(re(`workflows/${WID}/executions`), (r) => r.fulfill(json(sc.executions ?? [])));
  await page.route(re('files'), (r) => r.fulfill(json(sc.files ?? [])));
  await page.route(re('outputs'), (r) => r.fulfill(json(sc.outputs ?? [])));
}

const open = (page: Page) => page.goto(`/app/projects/${PROJECT_ID}/workflows/${WID}`);

test.describe('flag on: the app run page', () => {
  test.skip(!FLAG_ON, 'WA_FLAG=on only');

  test('form from the manifest, blocked readiness, empty file slot, price line', async ({ page, baseURL }, info) => {
    await setup(page, baseURL!);
    await open(page);
    await expect(page.getByTestId('run-bar')).toBeVisible();
    await expect(page.getByText('Your clips', { exact: true })).toBeVisible();
    await expect(page.getByRole('combobox', { name: 'Language' })).toBeVisible();
    await expect(page.getByRole('textbox', { name: 'Anything else?' })).toBeVisible();
    // No clip yet: the slot says so, the strip asks for one, Run is aria-disabled with the reason linked.
    await expect(page.getByText('None chosen (up to 3)')).toBeVisible();
    if (!isPhone(page)) await expect(page.getByText(/No video clips in this project yet/i)).toBeVisible();
    const strip = page.getByTestId('readiness-strip');
    await expect(strip).toContainText('Fix needed');
    await expect(strip).toContainText(/Add at least one/i);
    const run = page.getByTestId('run-button');
    await expect(run).toHaveAttribute('aria-disabled', 'true');
    await expect(page.getByTestId('run-block-reason')).toBeVisible();
    // A phone's blocked button reads the fix and performs it; a desktop's keeps the price.
    await expect(run).toContainText(isPhone(page) ? 'Add a clip' : 'Run, about 10 credits');
    await expect(page.getByText('usually 8 to 20')).toBeVisible();
    await page.screenshot({ path: `/tmp/claude-1000/run-page-blocked-${info.project.name}.png`, fullPage: true });
  });

  test('a clip in the project is picked for the slot and the empty result state is shown', async ({ page, baseURL }) => {
    await setup(page, baseURL!, { files: [clip] });
    await open(page);
    await expect(page.getByTestId('run-bar')).toBeVisible();
    if (isPhone(page)) {
      // On a phone the slot is a button that opens a bottom sheet with the list.
      await expect(page.getByText('1 chosen (up to 3)')).toBeVisible();
      await page.getByRole('button', { name: /Choose video clips/ }).click();
      await expect(page.getByRole('checkbox', { name: 'beach.mp4' })).toBeChecked();
      await page.getByRole('button', { name: 'Done' }).click();
    } else {
      await expect(page.getByRole('checkbox', { name: 'beach.mp4' })).toBeChecked();
    }
    await expect(page.getByTestId('readiness-strip')).toContainText('Ready');
    await expect(page.getByTestId('run-button')).not.toHaveAttribute('aria-disabled', 'true');
    await expect(page.getByTestId('run-result')).toContainText('press Run');
  });

  test('result state: a running run shows progress and Stop', async ({ page, baseURL }) => {
    await setup(page, baseURL!, { files: [clip], executions: [execution('Running')] });
    await page.route(new RegExp(`${base}/executions/ex1$`), (r) => r.fulfill(json(execution('Running'))));
    await open(page);
    await expect(page.getByTestId('stop-button')).toBeVisible();
    await expect(page.getByTestId('run-result')).toBeVisible();
  });

  test('result state: a failed run shows the banner with Retry', async ({ page, baseURL }) => {
    await setup(page, baseURL!, { files: [clip], executions: [execution('Failed', { errorMessage: 'boom' })] });
    await open(page);
    const banner = page.getByTestId('failed-banner');
    await expect(banner).toBeVisible();
    await expect(banner.getByRole('button', { name: 'Retry' })).toBeVisible();
    await expect(banner.getByRole('button', { name: 'Ask the assistant' })).toBeVisible();
  });

  test('result state: a finished run shows the video and Download', async ({ page, baseURL }) => {
    const output = {
      stepResultId: 'sr1', workflowExecutionId: 'ex1', workflowStepId: 's1', storageKey: 'o1', fileName: 'teaser.mp4',
      producedAt: now, workflowDefinitionId: WID, isFinal: true, executionStatus: 'Passed',
    };
    await setup(page, baseURL!, { files: [clip], executions: [execution('Passed')], outputs: [output] });
    await open(page);
    await expect(page.getByLabel('The finished video')).toBeVisible();
    await expect(page.getByRole('link', { name: 'Download' })).toHaveAttribute('href', /sr1.*download/);
  });

  test('phone layout: one column, no horizontal scroll, targets >= 44 px', async ({ page, baseURL }, info) => {
    test.skip(info.project.name !== 'pixel7', 'phone project only');
    await setup(page, baseURL!, { executions: [execution('Failed', { errorMessage: 'boom' })] });
    await open(page);
    await expect(page.getByTestId('run-bar')).toBeVisible();
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow).toBeLessThanOrEqual(0);
    // One column: the inputs and the result pane share a left edge (the result stacks below).
    const inputs = await page.locator('#app-run-inputs').boundingBox();
    const result = await page.getByTestId('run-result').boundingBox();
    expect(Math.abs(inputs!.x - result!.x)).toBeLessThan(2);
    expect(result!.y).toBeGreaterThan(inputs!.y);
    // Buttons inside the page content (not the shell chrome) are at least 44 px tall.
    const targets = page.locator('#app-run-inputs button, [data-testid="run-bar"] button, [data-testid="run-result"] button');
    const n = await targets.count();
    expect(n).toBeGreaterThan(0);
    const small: string[] = [];
    for (let i = 0; i < n; i++) {
      const el = targets.nth(i);
      if (!(await el.isVisible())) continue;
      const box = await el.boundingBox();
      if (box!.height < 43.5) small.push(`${(await el.innerText()).trim()} (${Math.round(box!.height)}px)`);
    }
    expect(small).toEqual([]);
    await page.screenshot({ path: '/tmp/claude-1000/run-page-phone.png', fullPage: true });
  });
});

test.describe('flag off: the workflow route keeps the graph builder', () => {
  test.skip(FLAG_ON, 'WA_FLAG=off only');

  test('no app run page is rendered', async ({ page, baseURL }) => {
    await setup(page, baseURL!);
    await open(page);
    await page.waitForLoadState('networkidle');
    await expect(page.getByTestId('run-bar')).toHaveCount(0);
    await expect(page.getByTestId('run-button')).toHaveCount(0);
    await expect(page.getByTestId('app-run-skeleton')).toHaveCount(0);
  });
});

function isPhone(page: Page): boolean {
  return (page.viewportSize()?.width ?? 1280) < 768;
}
