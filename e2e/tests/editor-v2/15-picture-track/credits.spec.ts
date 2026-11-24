import {
  test, expect, openFresh, isPhone, press, openTab,
} from '../../../fixtures/editor-v2/picture-lane';
import { pickAiTool } from '../../../fixtures/editor-v2/picture';

/**
 * Wave 1, picture track (T-011): R14.1 (a price before every paid tool runs, unaffordable state) and R14.5 (one
 * availability model, role-aware no-provider message). No paid call is made: the capabilities and graphics answers
 * are stubbed with page.route.
 */

test.use({ serviceWorkers: 'block' });

const NINE: Array<{ op: string; title: string }> = [
  { op: 'erase', title: 'Erase' },
  { op: 'fill', title: 'Fill' },
  { op: 'expand', title: 'Expand' },
  { op: 'replaceBackground', title: 'New background' },
  { op: 'relight', title: 'Relight' },
  { op: 'upscale', title: 'Upscale' },
  { op: 'instruct', title: 'Edit with words' },
  { op: 'generate', title: 'Create a picture' },
  { op: 'removeBackground', title: 'Remove background' },
];

type Estimate = { billingEnabled: boolean; credits?: number; balanceCredits?: number; affordable: boolean };

async function stubCapabilities(
  page: import('@playwright/test').Page,
  answer: (op: string) => { status: number; body: unknown },
): Promise<void> {
  await page.route('**/image-documents/operations/*/capabilities*', async (route) => {
    const op = decodeURIComponent(new URL(route.request().url()).pathname.split('/').slice(-2)[0]);
    const { status, body } = answer(op);
    await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
  });
}

const caps = (op: string, estimate: Estimate, canConfigure = false) => ({
  status: 200,
  body: { operation: op, provider: { kind: 'FalAi', model: 'stub' }, takesPrompt: true, maxPromptLength: 500, expand: null, estimate, canConfigure },
});

test('R14.1 every paid tool shows "about N credits" before it runs', async ({ page, api, pics }, testInfo) => {
  test.setTimeout(240_000);
  await stubCapabilities(page, (op) => caps(op, { billingEnabled: true, credits: 4.5, balanceCredits: 50, affordable: true }));
  await page.route('**/image-documents/*/graphics', async (route) => {
    if (route.request().method() !== 'GET') return route.continue();
    return route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        graphics: [],
        available: true,
        unavailableReason: null,
        estimate: { billingEnabled: true, lowCredits: 8, highCredits: 12, requiredCredits: 12, balanceCredits: 50, affordable: true, credits: 12 },
      }),
    });
  });
  await openFresh(page, api, pics, `r141-${testInfo.project.name}`);
  for (const { op, title } of NINE) {
    await pickAiTool(page, title);
    await expect(page.getByTestId(`credits-${op}-estimate`), `${title} shows its price before running`).toContainText('about 4.5 credits');
  }
  await pickAiTool(page, 'Cut out a subject');
  await expect(page.getByTestId('credits-subject-estimate'), 'Cut out a subject shows its price').toContainText('about 4.5 credits');
  await pickAiTool(page, 'Design a graphic');
  await expect(page.getByTestId('design-graphic-cost'), 'Design a graphic shows its price').toContainText(/about 8.12 credits/i);
});

test('R14.1 an unaffordable tool is disabled with an Add credits link; billing off shows no price', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  await stubCapabilities(page, (op) =>
    op === 'upscale'
      ? caps(op, { billingEnabled: true, credits: 9, balanceCredits: 2, affordable: false })
      : caps(op, { billingEnabled: false, affordable: true }),
  );
  await openFresh(page, api, pics, `r141b-${testInfo.project.name}`);
  await pickAiTool(page, 'Upscale');
  await expect(page.getByTestId('credits-upscale-estimate')).toContainText('Add credits');
  await expect(page.getByRole('link', { name: 'Add credits' })).toHaveAttribute('href', /\/settings\/billing$/);
  await expect(page.getByRole('button', { name: 'Upscale the picture' })).toBeDisabled();

  await pickAiTool(page, 'Relight');
  await expect(page.getByRole('button', { name: 'Relight the picture' })).toBeVisible();
  await expect(page.getByTestId('credits-relight')).toHaveCount(0);
  await expect(page.getByText(/\b0 credits\b/)).toHaveCount(0);
  void phone;
  void openTab;
  void press;
});

test('R14.5 with no provider a member is told to ask an admin; an admin gets the setup link', async ({ page, api, pics }, testInfo) => {
  const refusal = (canConfigure: boolean) => ({
    status: 409,
    body: { code: 'IMAGE_EDIT_PROVIDER_UNAVAILABLE', reason: 'NO_PROVIDER', canConfigure, message: 'No provider.' },
  });
  await stubCapabilities(page, () => refusal(false));
  await openFresh(page, api, pics, `r145-${testInfo.project.name}`);
  await pickAiTool(page, 'Upscale');
  await expect(page.getByText('Ask your workspace admin to set up picture tools.')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Set up picture tools' })).toHaveCount(0);

  await page.unroute('**/image-documents/operations/*/capabilities*');
  await stubCapabilities(page, () => refusal(true));
  await page.reload();
  await pickAiTool(page, 'Upscale');
  await expect(page.getByRole('link', { name: 'Set up picture tools' })).toHaveAttribute('href', /\/admin\/inference-providers$/);
});
