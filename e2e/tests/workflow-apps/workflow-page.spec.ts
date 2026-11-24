import { expect, test, type Page } from '@playwright/test';
import { PROJECT_ID, mockApi, signIn } from './fixtures';

// Lane 3d: the workflow page defaults to the App view (real AppRunPage) with an App | Advanced radiogroup,
// an "App inputs" panel and the restyled builder. Under WA_FLAG=off it is today's builder, no toggle.
const FLAG_ON = process.env.WA_FLAG !== 'off';
const WID = 'wf-run';
const now = new Date().toISOString();
const base = `/api/v1/projects/${PROJECT_ID}`;
const json = (body: unknown, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(body) });

const step = {
  id: 's1', agentDefinitionId: 'a1', stepOrder: 1, label: 'Write the script', stepType: 'Agent',
  maxIterations: 1, agentInputContextMode: 'FullWorkflow',
};
const workflow = { id: WID, projectId: PROJECT_ID, name: 'Launch teaser', steps: [step], requiresUserInput: false, createdAt: now, updatedAt: now };
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

interface Captured { puts: Record<string, unknown>[] }
interface Scenario { putStatus?: number; putBody?: unknown }

async function setup(page: Page, baseURL: string, sc: Scenario = {}): Promise<Captured> {
  const cap: Captured = { puts: [] };
  await signIn(page, baseURL);
  await mockApi(page);
  const re = (tail: string) => new RegExp(`${base}/${tail}(\\?.*)?$`);
  await page.route(re(`workflows/${WID}`), (r) => {
    if (r.request().method() === 'PUT') {
      cap.puts.push(r.request().postDataJSON());
      return r.fulfill(json(sc.putBody ?? workflow, sc.putStatus ?? 200));
    }
    return r.fulfill(json(workflow));
  });
  await page.route(re(`workflows/${WID}/app`), (r) => r.fulfill(json(appResponse)));
  await page.route(re(`workflows/${WID}/estimate`), (r) => r.fulfill(json(estimate)));
  await page.route(re(`workflows/${WID}/lint`), (r) => r.fulfill(json({ findings: [], errors: 0, warnings: 0, infos: 0 })));
  await page.route(re(`workflows/${WID}/executions`), (r) => r.fulfill(json([])));
  await page.route(re('files'), (r) => r.fulfill(json([])));
  await page.route(re('outputs'), (r) => r.fulfill(json([])));
  return cap;
}

const url = `/app/projects/${PROJECT_ID}/workflows/${WID}`;
const isPhone = (info: { project: { name: string } }) => info.project.name === 'pixel7';

async function openPanel(page: Page) {
  await page.getByRole('button', { name: 'App inputs' }).click();
  await expect(page.getByTestId('app-inputs-panel')).toBeVisible();
  await expect(page.getByRole('switch', { name: /Your clips/ })).toBeVisible();
}

test.describe('flag on: the workflow page', () => {
  test.skip(!FLAG_ON, 'WA_FLAG=on only');

  test('no ?view shows the app view (run page form) by default', async ({ page, baseURL }) => {
    await setup(page, baseURL!);
    await page.goto(url);
    await expect(page.getByText('Your clips').first()).toBeVisible();
    await expect(page.getByRole('group', { name: 'Choices' })).toBeVisible();
    await expect(page.getByRole('status', { name: 'Readiness check' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Back to the app' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'App inputs' })).toHaveCount(0);
  });

  test('the radiogroup switches to Advanced and back; the choice is remembered', async ({ page, baseURL }, info) => {
    test.skip(isPhone(info), 'desktop control');
    await setup(page, baseURL!);
    await page.goto(url);
    const group = page.getByRole('radiogroup', { name: 'Workflow view' });
    await expect(group).toBeVisible();
    await expect(group.getByRole('radio', { name: 'App' })).toBeChecked();

    await group.getByText('Advanced', { exact: true }).click();
    await expect(page).toHaveURL(/[?&]view=advanced/);
    await expect(page.getByRole('link', { name: 'Back to the app' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'App inputs' })).toBeVisible();

    // Remembered: the bare URL (no ?view) reopens Advanced.
    await page.goto(url);
    await expect(page.getByRole('link', { name: 'Back to the app' })).toBeVisible();
    await page.reload();
    await expect(page.getByRole('link', { name: 'Back to the app' })).toBeVisible();

    await page.getByRole('link', { name: 'Back to the app' }).click();
    await expect(page).toHaveURL(/[?&]view=app/);
    await expect(page.getByRole('link', { name: 'Back to the app' })).toHaveCount(0);
    await expect(page.getByRole('radio', { name: 'App' })).toBeChecked();
    await page.goto(url);
    await expect(page.getByRole('radio', { name: 'App' })).toBeChecked();
    await expect(page.getByRole('link', { name: 'Back to the app' })).toHaveCount(0);
  });

  test('keyboard: arrow keys move within the radiogroup', async ({ page, baseURL }, info) => {
    test.skip(isPhone(info), 'desktop control');
    await setup(page, baseURL!);
    await page.goto(url, { waitUntil: 'networkidle' });
    const app = page.getByRole('radio', { name: 'App' });
    const adv = page.getByRole('radio', { name: 'Advanced' });
    // Wait for hydration to finish: Next re-syncs the address bar once after hydration, and a view
    // change made before that point has its ?view= overwritten.
    await expect(app).toBeChecked();
    await page.waitForTimeout(500);
    await app.focus();
    await page.keyboard.press('ArrowRight');
    await expect(adv).toBeChecked();
    await expect(adv).toBeFocused();
    await expect(page).toHaveURL(/[?&]view=advanced/);
    await page.keyboard.press('ArrowLeft');
    await expect(app).toBeChecked();
    await expect(page).toHaveURL(/[?&]view=app/);
    await page.keyboard.press('ArrowDown');
    await expect(adv).toBeChecked();
  });

  test('App inputs panel: hide, reorder, relabel, save sends appManifest', async ({ page, baseURL }, info) => {
    test.skip(isPhone(info), 'phone has its own test');
    const cap = await setup(page, baseURL!);
    await page.goto(`${url}?view=advanced`);
    await openPanel(page);

    await page.getByRole('switch', { name: /Anything else\?/ }).click({ force: true });
    await page.getByRole('button', { name: 'Move Language up' }).click();
    await expect(page.getByText(/Moved Language to position 1 of 3/)).toBeAttached();
    const label = page.getByRole('textbox', { name: 'Name in the app for Language' });
    await label.fill('Spoken language');
    await page.getByRole('button', { name: 'Save app inputs' }).click();

    await expect.poll(() => cap.puts.length).toBe(1);
    const m = (cap.puts[0] as { appManifest: { inputs: { kind: string; label?: string }[] } }).appManifest;
    expect(cap.puts[0]).not.toHaveProperty('clearAppManifest');
    expect(m.inputs.map((i) => i.label)).toEqual(['Spoken language', 'Your clips']);
  });

  test('a mocked 400 APP_MANIFEST_INVALID shows the alert naming the input', async ({ page, baseURL }) => {
    await setup(page, baseURL!, {
      putStatus: 400,
      putBody: { code: 'APP_MANIFEST_INVALID', message: 'The app is invalid.', errors: ['The input "Language" is not read by any step.'] },
    });
    await page.goto(`${url}?view=advanced`);
    await openPanel(page);
    await page.getByRole('button', { name: 'Save app inputs' }).click();
    const alert = page.getByTestId('app-inputs-error');
    await expect(alert).toBeVisible();
    await expect(alert).toContainText('Language');
    await expect(alert).toHaveAttribute('role', 'alert');
  });

  test('Reset to automatic inputs with confirm sends clearAppManifest', async ({ page, baseURL }) => {
    const cap = await setup(page, baseURL!);
    await page.goto(`${url}?view=advanced`);
    await openPanel(page);
    await page.getByRole('button', { name: 'Reset to automatic inputs' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    expect(cap.puts).toHaveLength(0); // nothing sent before confirming
    await dialog.getByRole('button', { name: 'Reset', exact: true }).click();
    await expect.poll(() => cap.puts.length).toBe(1);
    expect(cap.puts[0]).toMatchObject({ clearAppManifest: true });
    expect(cap.puts[0]).not.toHaveProperty('appManifest');
  });

  test('phone: no radiogroup, status line, Edit steps (advanced), 44 px targets, no horizontal scroll', async ({ page, baseURL }, info) => {
    test.skip(!isPhone(info), 'phone only');
    await setup(page, baseURL!);
    await page.goto(url);
    await expect(page.getByRole('radiogroup')).toHaveCount(0);
    await expect(page.getByTestId('phone-view-state')).toHaveText('Showing the app');
    await expect(page.getByText('Your clips').first()).toBeVisible();
    const noHScroll = () => page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth);
    expect(await noHScroll()).toBe(true);

    await page.getByRole('button', { name: 'More options' }).click();
    await page.getByRole('menuitem', { name: 'Edit steps (advanced)' }).click();
    await expect(page).toHaveURL(/[?&]view=advanced/);
    await expect(page.getByTestId('phone-view-state')).toHaveText('Editing the steps (advanced)');
    await expect(page.getByRole('radiogroup')).toHaveCount(0);
    await expect(page.getByRole('textbox', { name: 'Step label' })).toHaveValue('Write the script');

    await openPanel(page);
    const panel = page.getByTestId('app-inputs-panel');
    const small: string[] = [];
    const controls = panel.locator('button:visible, input:visible');
    const n = await controls.count();
    expect(n).toBeGreaterThan(5);
    for (let i = 0; i < n; i++) {
      const c = controls.nth(i);
      const type = await c.getAttribute('type');
      // A switch/checkbox input is covered by its label row; judge the row's hit area instead.
      const box = type === 'checkbox'
        ? await c.locator('xpath=ancestor::*[contains(@class,"mantine-Switch-root")][1]').boundingBox()
        : await c.boundingBox();
      if (box && box.height < 43.5) small.push(`${(await c.getAttribute('aria-label')) ?? (await c.innerText().catch(() => '')) ?? type}: ${Math.round(box.width)}x${Math.round(box.height)}`);
      if (box && type !== 'checkbox' && box.width < 43.5 && (await c.evaluate((e) => e.tagName)) === 'BUTTON') small.push(`${await c.getAttribute('aria-label')}: w${Math.round(box.width)}`);
    }
    expect(small, `controls under 44 px: ${small.join('; ')}`).toEqual([]);
    expect(await noHScroll()).toBe(true);
  });
});

test.describe('flag off: today\'s builder', () => {
  test.skip(FLAG_ON, 'WA_FLAG=off only');

  test('no toggle, no App inputs, no back link; the builder is the page', async ({ page, baseURL }) => {
    await setup(page, baseURL!);
    await page.goto(url);
    await expect(page.getByRole('textbox', { name: 'Workflow Name' })).toBeVisible();
    // canvas node on desktop, step-label textbox in the phone list
    await expect(page.getByRole('button', { name: /Write the script/ }).or(page.getByRole('textbox', { name: 'Step label' })).first()).toBeVisible();
    await expect(page.getByRole('radiogroup', { name: 'Workflow view' })).toHaveCount(0);
    await expect(page.getByTestId('phone-view-state')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'App inputs' })).toHaveCount(0);
    await expect(page.getByRole('link', { name: 'Back to the app' })).toHaveCount(0);
    await page.goto(`${url}?view=app`);
    await expect(page.getByRole('textbox', { name: 'Workflow Name' })).toBeVisible();
  });
});
