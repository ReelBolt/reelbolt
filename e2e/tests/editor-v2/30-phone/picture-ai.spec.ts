import type { Page } from '@playwright/test';
import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { box, createPicture, getPicture, openPicture } from '../../../fixtures/editor-v2/picture';
import { knownFailure } from '../expected-failures';

/**
 * Wave 3 / T-011: the picture editor on a phone, text basics (R13.8) and AI tools as sheets (R14.2). Text outcomes are
 * read back from the SAVED picture through the API. AI tools are never really run: capabilities and operations are
 * stubbed with page.route, so no paid provider is called and nothing is charged.
 */
test.use({ serviceWorkers: 'block' });

const MIN_TARGET = 44;
const phoneOnly = () => runOn('pixel7', 'iphone14webkit');

/** Capabilities that price Edit with words at 3 credits and say the balance covers it. */
async function stubAi(page: Page, resultLayer: Record<string, unknown>) {
  const ran: unknown[] = [];
  await page.route('**/image-documents/operations/*/capabilities*', (route) => {
    const operation = route.request().url().split('/operations/')[1].split('/')[0];
    return route.fulfill({
      json: {
        operation,
        provider: { kind: 'stub', model: 'stub' },
        takesPrompt: true,
        maxPromptLength: 1000,
        estimate: { billingEnabled: true, credits: 3, balanceCredits: 100, affordable: true },
        canConfigure: false,
        expand: { geometry: 'margins', maxMarginPx: null, maxOutputPixels: null },
      },
    });
  });
  await page.route('**/image-documents/*/operations', async (route) => {
    if (route.request().method() !== 'POST') return route.fallback();
    ran.push(route.request().postDataJSON());
    return route.fulfill({
      json: { layers: [resultLayer], operation: 'instruct', provider: { kind: 'stub', model: 'stub' }, chargedCredits: 3, revision: 99 },
    });
  });
  return ran;
}

async function pickTool(page: Page, tool: string) {
  await page.getByTestId('picture-dock-ai').tap();
  await page.getByRole('button', { name: /Choose an AI tool/ }).tap();
  await page.getByTestId(`ai-tool-option-${tool}`).tap();
}

test.describe('RM.14 picture text basics (R13.8)', () => {
  test('RM.14 R13.8 text is added, edited and the outline switch is stored in the picture', async ({ page, api, seed }, testInfo) => {
    phoneOnly();
    const pic = await createPicture(api, seed.projectId, seed.files.still, `text-${testInfo.project.name}-${Date.now()}`);
    await openPicture(page, seed.projectId, pic.id);
    await page.getByTestId('picture-dock-text').tap();
    await page.getByRole('button', { name: 'Add text' }).tap();
    await expect.poll(async () => (await getPicture(api, seed.projectId, pic.id)).layers.length, { message: 'the text layer is saved' }).toBe(2);
    const panel = page.getByTestId('text-panel-phone');
    await expect(panel).toBeVisible();
    // The basics are on the first screen; the detail is behind More.
    await expect(panel.getByText('Outline and shadow', { exact: true }).first()).toBeVisible();
    await expect(panel.getByText('Line height')).toHaveCount(0);

    const words = panel.getByLabel('Words');
    await words.fill('Big news');
    type TextBody = { text: string; stroke: unknown; shadow: unknown };
    const textLayer = async () => {
      const layer = (await getPicture(api, seed.projectId, pic.id)).layers.find((l) => l.kind.toLowerCase() === 'text')!;
      return { content: (layer as unknown as { content: TextBody | null }).content };
    };
    await expect.poll(async () => (await textLayer()).content?.text, { message: 'the new words are saved' }).toBe('Big news');

    await expect(page.getByTestId('text-decoration-switch')).toBeChecked();
    await page.getByTestId('text-decoration-switch').tap();
    await expect.poll(async () => {
      const c = (await textLayer()).content;
      return c ? [!!c.stroke, !!c.shadow] : undefined;
    }, { message: 'outline and shadow are stored as off' }).toEqual([false, false]);
    await expect(page.getByTestId('text-decoration-switch')).not.toBeChecked();
    await page.getByTestId('text-decoration-switch').tap();
    await expect.poll(async () => {
      const c = (await textLayer()).content;
      return !!c?.stroke && !!c?.shadow;
    }, { message: 'outline and shadow are stored as on again' }).toBe(true);

    await page.getByTestId('text-more').tap();
    await expect(panel.getByText('Line height')).toBeVisible();
    const toggle = await box(page.getByTestId('text-more'), 'More');
    expect(toggle.height).toBeGreaterThanOrEqual(MIN_TARGET - 0.5);
  });
});

test.describe('RM.14 picture AI tools as sheets (R14.2)', () => {
  test('RM.14 R14.2 Run is pinned in the sheet, shows its price, and a stubbed run adds the layer', async ({ page, api, seed }, testInfo) => {
    knownFailure('RM.14 R14.2 Run is pinned');
    phoneOnly();
    const pic = await createPicture(api, seed.projectId, seed.files.still, `run-${testInfo.project.name}-${Date.now()}`);
    const first = pic.layers[0] as unknown as Record<string, unknown>;
    const ran = await stubAi(page, { ...first, id: '00000000-0000-4000-8000-0000000000aa', label: 'AI result', zOrder: 9 });
    await openPicture(page, seed.projectId, pic.id);
    await page.getByTestId('picture-dock-layers').tap();
    await page.getByRole('button', { name: `Select ${pic.layers[0].label}`, exact: true }).tap();
    await pickTool(page, 'instruct');

    const footer = page.getByTestId('picture-sheet-footer');
    const run = footer.getByRole('button', { name: /Make the change, about 3 credits/ });
    await expect(run).toBeVisible();
    const sheet = await box(page.getByTestId('picture-sheet'), 'the sheet');
    const stage = await box(page.getByTestId('picture-stage'), 'the stage');
    const r = await box(run, 'Run');
    expect(r.height).toBeGreaterThanOrEqual(MIN_TARGET - 0.5);
    expect(r.y + r.height, 'Run is inside the sheet').toBeLessThanOrEqual(sheet.y + sheet.height + 1);
    expect(stage.y + stage.height, 'the picture stays visible above the sheet').toBeLessThanOrEqual(sheet.y + 1);

    await page.getByLabel('What to change').fill('make the sky pink');
    await run.tap();
    await expect.poll(() => ran.length, { message: 'the stubbed operation was called once' }).toBe(1);
    expect(ran[0]).toMatchObject({ operation: 'instruct', prompt: 'make the sky pink' });
    // The stubbed server does not store the layer, so the outcome checked is what the editor reports for the run.
    await expect(page.getByTestId('result-bar-changed')).toContainText('AI result');
  });

  test('RM.14 R14.2 T13 a prompt survives closing and re-opening the sheet', async ({ page, api, seed }, testInfo) => {
    knownFailure('RM.14 R14.2 T13');
    phoneOnly();
    const pic = await createPicture(api, seed.projectId, seed.files.still, `survive-${testInfo.project.name}-${Date.now()}`);
    await stubAi(page, pic.layers[0] as unknown as Record<string, unknown>);
    await openPicture(page, seed.projectId, pic.id);
    await page.getByTestId('picture-dock-layers').tap();
    await page.getByRole('button', { name: `Select ${pic.layers[0].label}`, exact: true }).tap();
    await pickTool(page, 'instruct');
    await page.getByLabel('What to change').fill('make the shirt red');
    await page.getByTestId('picture-sheet-close').tap();
    await expect(page.getByTestId('picture-sheet')).toBeHidden();
    await page.getByTestId('picture-dock-ai').tap();
    await expect(page.getByLabel('What to change')).toHaveValue('make the shirt red');
  });

  test('RM.14 R14.2 the cut-out tool has a visible Keep / Leave out switch', async ({ page, api, seed }, testInfo) => {
    phoneOnly();
    const pic = await createPicture(api, seed.projectId, seed.files.still, `mode-${testInfo.project.name}-${Date.now()}`);
    await stubAi(page, pic.layers[0] as unknown as Record<string, unknown>);
    await openPicture(page, seed.projectId, pic.id);
    await page.getByTestId('picture-dock-layers').tap();
    await page.getByRole('button', { name: `Select ${pic.layers[0].label}`, exact: true }).tap();
    await pickTool(page, 'subject');
    const group = page.getByRole('radiogroup', { name: 'What a click marks' });
    await expect(group).toBeVisible();
    await group.getByText('Leave this out').tap();
    await expect(group.getByLabel('Leave this out')).toBeChecked();
    await group.getByText('Cut this out').tap();
    await expect(group.getByLabel('Cut this out')).toBeChecked();
  });
});
