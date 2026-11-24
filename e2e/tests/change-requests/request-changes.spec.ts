import { expect, test, type Page } from '@playwright/test';
import {
  NEW_EXECUTION, PROJECT_ID, WID, estimateShortfall, historyRow, runUrl, selectStep, setup,
} from './fixtures';

// The run page "Request changes" flow (docs/change-requests.md). Everything is mocked with page.route():
// no stack, no provider call. Runs on both viewports of playwright.change-requests.config.ts.
const requestButton = (page: Page) => page.getByTestId('step-request-changes');
const modal = (page: Page) => page.getByRole('dialog', { name: /^Change:/ });
// With an empty set the one-step flow is the secondary "Request this change now" button.
const confirm = (page: Page) => page.getByRole('button', { name: 'Request this change now' });

async function openStep(page: Page, baseURL: string, n: number, sc: Parameters<typeof setup>[2] = {}) {
  const posts = await setup(page, baseURL, sc);
  await page.goto(`/app${runUrl()}`);
  await selectStep(page, n);
  await requestButton(page).click();
  await expect(modal(page)).toBeVisible();
  return posts;
}

test.describe('request changes: which steps offer it', () => {
  test('button only on supported steps; the film points at the video panel', async ({ page, baseURL }) => {
    await setup(page, baseURL!);
    await page.goto(`/app${runUrl()}`);
    await selectStep(page, 1); // unsupported agent step
    await expect(page.getByText('Took').first()).toBeVisible();
    await expect(requestButton(page)).toHaveCount(0);
    for (const n of [2, 3, 4, 5]) {
      await page.keyboard.press('Escape'); // on the phone the details sit in a drawer that covers the graph
      await expect(page.getByRole('dialog', { name: 'Step details' })).toBeHidden();
      await selectStep(page, n);
      await expect(requestButton(page)).toHaveText('Request changes');
    }
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog', { name: 'Step details' })).toBeHidden();
    await selectStep(page, 6);
    await expect(requestButton(page)).toHaveText('Request changes to the video');
  });
});

test.describe('request changes: narration', () => {
  test('new wording and a respelling; price shown before confirm; exact body posted', async ({ page, baseURL }) => {
    const posts = await openStep(page, baseURL!, 2);
    const line = page.getByTestId('change-request-item-vo1');
    await line.getByRole('checkbox').check();
    await line.getByLabel('New wording').fill('Hello, dear friend.');
    await line.getByRole('button', { name: 'Add a word' }).click();
    await line.getByLabel('Word 1').fill('Novara');
    await line.getByLabel('How to say it 1').fill('Novàra');

    // The price is on screen before anything is sent.
    await expect(page.getByTestId('change-request-estimate')).toHaveText(/About 1\.00 to 2\.00 credits/);
    await expect(page.getByTestId('change-request-exact')).toHaveText(/This part: 1\.20 credits/);
    await expect(page.getByText('Will be redone')).toBeVisible();
    await expect(page.getByText('Reused (free)')).toBeVisible();
    expect(posts.create).toHaveLength(0);

    const expected = {
      stepResultId: 'sr2',
      itemIds: ['vo1'],
      overrides: { vo1: { text: 'Hello, dear friend.', pronunciation: [{ term: 'Novara', spelling: 'Novàra' }] } },
    };
    expect(posts.estimate.at(-1)).toEqual(expected);

    await confirm(page).click();
    await expect(page).toHaveURL(new RegExp(`/projects/${PROJECT_ID}/workflows/${WID}/executions/${NEW_EXECUTION}$`));
    expect(posts.create).toHaveLength(1);
    expect(posts.create[0]).toEqual(expected);
    expect(Object.keys((posts.create[0] as { overrides: object }).overrides)).toEqual(['vo1']);
  });

  test('nothing is sent until a line is picked', async ({ page, baseURL }) => {
    const posts = await openStep(page, baseURL!, 2);
    await expect(confirm(page)).toBeDisabled();
    expect(posts.estimate).toHaveLength(0);
  });
});

test.describe('request changes: clips, music, thumbnail', () => {
  test('clip retake switch posts retake: true for just that clip', async ({ page, baseURL }) => {
    const posts = await openStep(page, baseURL!, 3);
    const clip = page.getByTestId('change-request-item-v2');
    await clip.getByRole('checkbox', { name: /Waves at sunset/ }).check();
    await clip.getByLabel('Make a fresh take').check({ force: true });
    await expect(page.getByTestId('change-request-estimate')).toBeVisible();
    await confirm(page).click();
    await expect(page).toHaveURL(new RegExp(`/executions/${NEW_EXECUTION}$`));
    // The unchanged prompt is not sent.
    expect(posts.create[0]).toEqual({ stepResultId: 'sr3', itemIds: ['v2'], overrides: { v2: { retake: true } } });
  });

  test('takes sit under their clip', async ({ page, baseURL }) => {
    await openStep(page, baseURL!, 3);
    await expect(page.getByTestId('change-request-item-v1').getByTestId('change-request-item-v1t1')).toBeVisible();
    await expect(page.getByTestId('change-request-item-v1').getByTestId('change-request-item-v1t2')).toBeVisible();
  });

  test('music take can be remade as a new version', async ({ page, baseURL }) => {
    const posts = await openStep(page, baseURL!, 4);
    const take = page.getByTestId('change-request-item-a1');
    await take.getByRole('checkbox', { name: /Calm piano/ }).check();
    await take.getByLabel('Make a new version').check({ force: true });
    await expect(page.getByTestId('change-request-estimate')).toBeVisible();
    await confirm(page).click();
    await expect.poll(() => posts.create.length).toBe(1);
    expect(posts.create[0]).toEqual({ stepResultId: 'sr4', itemIds: ['a1'], overrides: { a1: { retake: true } } });
  });

  test('thumbnail form has no item picker and posts the whole-step override', async ({ page, baseURL }) => {
    const posts = await openStep(page, baseURL!, 5);
    await expect(modal(page).getByRole('checkbox')).toHaveCount(0);
    await expect(modal(page).getByLabel('Scope')).toHaveCount(0);
    await modal(page).getByLabel('Text on the picture').fill('Big launch');
    await expect(page.getByTestId('change-request-estimate')).toBeVisible();
    await confirm(page).click();
    await expect.poll(() => posts.create.length).toBe(1);
    expect(posts.create[0]).toEqual({ stepResultId: 'sr5', overrides: { '*': { overlayText: 'Big launch' } } });
  });
});

test.describe('request changes: refusals', () => {
  async function pickLine(page: Page) {
    await page.getByTestId('change-request-item-vo2').getByRole('checkbox').check();
    await page.getByTestId('change-request-item-vo2').getByLabel('New wording').fill('Welcome to the show.');
    await expect(page.getByTestId('change-request-estimate')).toBeVisible();
  }

  test('shortfall estimate warns and links to top-up before confirming', async ({ page, baseURL }) => {
    await openStep(page, baseURL!, 2, { estimate: estimateShortfall });
    await pickLine(page);
    const warn = page.getByTestId('change-request-shortfall');
    await expect(warn).toContainText('You may not have enough credits');
    await expect(warn.getByRole('link', { name: 'Top up credits' })).toHaveAttribute('href', /\/settings\/billing$/);
  });

  test('402 on confirm shows the shortfall with a top-up link and keeps the form', async ({ page, baseURL }) => {
    const posts = await openStep(page, baseURL!, 2, {
      create: { status: 402, body: { code: 'INSUFFICIENT_CREDITS', message: 'You need 20 more credits.', required: 20, balance: 0 } },
    });
    await pickLine(page);
    await confirm(page).click();
    const err = page.getByTestId('change-request-error');
    await expect(err).toContainText('Not enough credits');
    await expect(err).toContainText('You need 20 more credits.');
    await expect(err.getByRole('link', { name: 'Top up credits' })).toHaveAttribute('href', /\/settings\/billing$/);
    await expect(modal(page)).toBeVisible();
    expect(posts.create).toHaveLength(1);
  });

  test('409 shows a plain message, no top-up', async ({ page, baseURL }) => {
    await openStep(page, baseURL!, 2, {
      create: { status: 409, body: { code: 'STEP_NOT_COMPLETED', message: 'Only a step that finished can be changed.' } },
    });
    await pickLine(page);
    await confirm(page).click();
    const err = page.getByTestId('change-request-error');
    await expect(err).toContainText('This step did not finish');
    await expect(err).toContainText('Only a step that finished can be changed.');
    await expect(err.getByRole('link')).toHaveCount(0);
    await expect(modal(page)).toBeVisible();
  });

  test('429 shows a plain message with the wait, no top-up', async ({ page, baseURL }) => {
    await openStep(page, baseURL!, 2, {
      create: { status: 429, body: { message: 'You have asked for a lot of changes.', retryAfterSeconds: 120 } },
    });
    await pickLine(page);
    await confirm(page).click();
    const err = page.getByTestId('change-request-error');
    await expect(err).toContainText('Too many change requests');
    await expect(err).toContainText('about 2 minutes');
    await expect(err.getByRole('link')).toHaveCount(0);
  });
});

test.describe('request changes: history', () => {
  test('lists redone/reused, cost and links to the new run', async ({ page, baseURL }) => {
    await setup(page, baseURL!, { history: [historyRow] });
    await page.goto(`/app${runUrl()}`);
    const row = page.getByTestId('change-request-row');
    await expect(row).toContainText('Slower first line');
    await expect(row).toContainText('2 redone');
    await expect(row).toContainText('5 reused');
    await expect(row).toContainText('1.50 credits');
    await row.getByRole('link', { name: 'Open run' }).click();
    await expect(page).toHaveURL(new RegExp(`/executions/${NEW_EXECUTION}$`));
    // On the new run the same list says where it came from.
    await expect(page.getByTestId('change-request-lineage')).toContainText('This run is a change of');
  });
});

test.describe('request changes: phone', () => {
  test.beforeEach(({}, info) => test.skip(info.project.name !== 'pixel7', 'Pixel 7 project only'));

  test('modal is full screen and usable by keyboard alone', async ({ page, baseURL }) => {
    const posts = await openStep(page, baseURL!, 2);
    const vp = page.viewportSize()!;
    const box = (await page.getByRole('dialog').last().boundingBox())!;
    expect(box.width).toBeGreaterThanOrEqual(vp.width - 1);
    expect(box.height).toBeGreaterThanOrEqual(vp.height - 1);

    const first = modal(page).getByRole('checkbox', { name: /Hello there/ });
    await first.focus();
    await page.keyboard.press('Space');
    await expect(first).toBeChecked();
    await page.keyboard.press('Tab'); // the line's New wording field
    await page.keyboard.press('ControlOrMeta+A');
    await page.keyboard.type('Hi, everyone.');
    await expect(page.getByTestId('change-request-estimate')).toBeVisible();
    await confirm(page).focus();
    await page.keyboard.press('Enter');
    await expect.poll(() => posts.create.length).toBe(1);
    expect(posts.create[0]).toEqual({ stepResultId: 'sr2', itemIds: ['vo1'], overrides: { vo1: { text: 'Hi, everyone.' } } });
  });

  test('Escape closes the modal', async ({ page, baseURL }) => {
    await openStep(page, baseURL!, 2);
    await page.keyboard.press('Escape');
    await expect(modal(page)).toBeHidden();
  });
});

test.describe('request changes: several changes in one request', () => {
  test('narration + the note on the video are reviewed together with one price and sent as one request', async ({ page, baseURL }) => {
    const posts = await setup(page, baseURL!);
    // The video's history, so the panel shows the video form.
    await page.route(/\/executions\/ex1\/revisions(\?.*)?$/, (r) => r.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        workspaceId: 'ws1', availability: 'Snapshot', canRevise: true, unavailableReason: null, currentVersionId: 'v1', scenes: [],
        versions: [{
          id: 'v1', versionNumber: 1, parentVersionId: null, parentVersionNumber: null, createdAt: '2026-10-04T14:00:00Z', instruction: null,
          source: 'RenderCapture', executionId: 'ex1', workflowId: WID, renderStepResultId: 'sr6', hasRender: true, compositionId: 'Main',
          durationInFrames: 900, fps: 30, width: 1920, height: 1080, sceneCount: 3,
        }],
        revisions: [],
        estimate: { billingEnabled: true, lowCredits: 4, highCredits: 9, requiredCredits: 9, balanceCredits: 100, affordable: true },
      }),
    }));
    await page.goto(`/app${runUrl()}`);

    // 1. A narration change goes into the set instead of being sent.
    await selectStep(page, 2);
    await requestButton(page).click();
    const line = page.getByTestId('change-request-item-vo1');
    await line.getByRole('checkbox').check();
    await line.getByLabel('New wording').fill('Hello, dear friend.');
    await page.getByTestId('change-request-add').click();
    await expect(modal(page)).toBeHidden();
    expect(posts.create).toHaveLength(0);

    // 2. The video's own note joins the same set (typed in the panel; no extra button).
    await page.getByLabel('What should change?').fill('Bigger title');

    // 3. One review: both changes, one combined price. On a phone it is a bottom bar that opens a sheet.
    const sheetOpen = page.getByTestId('change-set-sheet-open');
    if (await sheetOpen.isVisible()) {
      await sheetOpen.click();
      await expect(page.getByRole('dialog', { name: 'Your changes' })).toBeVisible();
    }
    await expect(page.getByTestId('change-set-entry-2')).toContainText('Narration');
    await expect(page.getByTestId('change-set-estimate')).toHaveText(/About 1\.00 to 2\.00 credits/);
    const expected = {
      targets: [
        { stepResultId: 'sr2', itemIds: ['vo1'], overrides: { vo1: { text: 'Hello, dear friend.' } } },
        { stepOrder: 6, instruction: 'Bigger title' },
      ],
    };
    expect(posts.estimate.at(-1)).toEqual(expected);

    await page.getByTestId('change-set-submit').click();
    await expect(page).toHaveURL(new RegExp(`/executions/${NEW_EXECUTION}$`));
    expect(posts.create).toEqual([expected]);
  });
});
