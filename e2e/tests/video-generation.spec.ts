import { mergeTests, Locator, Page, TestInfo } from '@playwright/test';
import { test as authTest, expect } from '../fixtures/auth';
import { test as projectTest } from '../fixtures/project';

/**
 * Phase-1 generative video (VideoGenerate / MiniMax) UI coverage.
 *
 * SAFETY (hard rules for this file):
 *  - never clicks any provider "Test connection" / "Test" button;
 *  - never executes or runs a workflow;
 *  - the provider it creates uses an obviously fake API key, is never marked default, and is
 *    deleted via the API in a `finally`.
 * No paid vendor call is reachable from anything this spec does.
 */
const test = mergeTests(authTest, projectTest);

const BASE_URL = process.env.BASE_URL || 'http://localhost';
const FAKE_API_KEY = 'fake-e2e-key-not-real';
const MINIMAX_DEFAULT_ENDPOINT = 'https://api.minimax.io';
/** web/lib/utils/video-generate-validation.ts MAX_SPEND_ERROR. */
const MAX_SPEND_ERROR = 'Max Spend (credits) must be greater than 0';
/** web/lib/utils/video-generate-validation.ts PLAN_SOURCE_STEP_REF_REQUIRED_ERROR. */
const PLAN_REF_REQUIRED_ERROR = 'Plan source requires a planner step reference';
/** findInvalidVideoGenerateStep's `Step N: <first error>` for a default (Max Spend 0) step 1. */
const STEP_1_MAX_SPEND_ERROR = `Step 1: ${MAX_SPEND_ERROR}`;

/** The slice of the provider tree (`GET/POST /api/v1/inference-providers`) this spec reads. */
interface ProviderTree {
  id: string;
  name: string;
  kind: string;
  endpoint: string;
  hasApiKey: boolean;
  apiKeyLastFour: string | null;
  isEnabled: boolean;
  timeoutSeconds: number | null;
  models: Array<{ modelCode: string; capabilities: Array<{ capability: string; isDefault: boolean; isEnabled: boolean }> }>;
}

/**
 * Moves a Mantine Select's keyboard highlight onto the option with this exact text (the list must be
 * open already). `aria-activedescendant` names the highlighted option; the caller presses Enter.
 */
async function highlightOption(combobox: Locator, optionText: string): Promise<void> {
  for (let i = 0; i < 40; i++) {
    const active = await combobox.evaluate((el) => {
      const id = el.getAttribute('aria-activedescendant');
      return id ? (document.getElementById(id)?.textContent ?? '').trim() : '';
    });
    if (active === optionText) return;
    await combobox.press('ArrowDown');
  }
  throw new Error(`option '${optionText}' never became the highlighted one`);
}

/**
 * The desktop settings panel moves focus to its first field 200 ms after it opens
 * (StepConfigPanel's focus management), which closes any select a test has just opened. Wait for
 * the panel to settle before touching a field.
 */
async function settleSettingsPanel(page: Page, testInfo: TestInfo): Promise<void> {
  if (testInfo.project.name !== 'mobile') await page.waitForTimeout(500);
}

/** Unique per run AND per Playwright project so desktop + mobile can run in parallel. */
function uniqueSuffix(testInfo: TestInfo): string {
  return `${testInfo.project.name}-${Date.now()}-${Math.floor(Math.random() * 1e6)}`;
}

/** The step config drawer/sheet. Mobile's sheet has no dialog title, so filter by its header text. */
function generateVideoDialog(page: Page): Locator {
  // Desktop: the settings panel titled "Step 1 settings: Generates new video clips"; mobile: the
  // "Configure Generate Video" sheet.
  return page.getByRole('dialog').filter({ hasText: /Configure Generate Video|Step 1 settings: Generates new video clips/ });
}

/**
 * Adds one Generate Video step as step 1 of an empty builder (desktop canvas via AddStepModal,
 * mobile step list via the Step Type select).
 */
async function addGenerateVideoStep(page: Page, testInfo: TestInfo): Promise<void> {
  if (testInfo.project.name === 'mobile') {
    await page.getByRole('button', { name: 'Add Step' }).click();
    const stepType = page.getByRole('combobox', { name: 'Step Type' });
    await expect(stepType).toBeVisible();
    await stepType.click();
    await page.getByRole('option', { name: 'Generate Video', exact: true }).click();
  } else {
    await page.getByRole('button', { name: 'Add Step' }).click();
    const pickerButton = page.getByRole('button', { name: /^Generate Video/ });
    await expect(pickerButton).toBeVisible();
    await pickerButton.click();
  }
  await expect(page.getByRole('button', { name: /^Step 1, Generate Video:/ }).first()).toBeVisible();
}

/**
 * Opens the "Configure Generate Video" dialog for step 1. On desktop the canvas node is activated
 * from the keyboard: a mouse click would first hover the node, which expands its inline config
 * and moves the click target.
 */
async function openGenerateVideoDialog(page: Page, testInfo: TestInfo): Promise<Locator> {
  const trigger = page.getByRole('button', { name: /^Step 1, Generate Video:/ }).first();
  await expect(trigger).toBeVisible();
  if (testInfo.project.name === 'mobile') {
    await trigger.click();
  } else {
    await trigger.focus();
    await page.keyboard.press('Enter');
  }
  const dialog = generateVideoDialog(page);
  await expect(dialog).toBeVisible();
  await settleSettingsPanel(page, testInfo);
  await expect(dialog.getByRole('textbox', { name: 'Prompt' })).toBeVisible();
  return dialog;
}

async function closeDialog(page: Page, dialog: Locator): Promise<void> {
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
}

/** The Compile Video step config drawer/sheet (STEP_TYPE_LABELS.VideoCompile). */
function compileVideoDialog(page: Page): Locator {
  return page.getByRole('dialog').filter({ hasText: /Configure Compile Video|Step 1 settings: Puts the final video together/ });
}

/**
 * Adds one Compile Video step as step 1 of an empty builder (desktop canvas via AddStepModal,
 * mobile step list via the Step Type select).
 */
async function addCompileVideoStep(page: Page, testInfo: TestInfo): Promise<void> {
  if (testInfo.project.name === 'mobile') {
    await page.getByRole('button', { name: 'Add Step' }).click();
    const stepType = page.getByRole('combobox', { name: 'Step Type' });
    await expect(stepType).toBeVisible();
    await stepType.click();
    await page.getByRole('option', { name: 'Compile Video', exact: true }).click();
  } else {
    await page.getByRole('button', { name: 'Add Step' }).click();
    const pickerButton = page.getByRole('button', { name: /^Compile Video/ });
    await expect(pickerButton).toBeVisible();
    await pickerButton.click();
  }
  await expect(page.getByRole('button', { name: /^Step 1, Compile Video:/ }).first()).toBeVisible();
}

/** Opens the "Configure Compile Video" dialog for step 1, same keyboard-activation rule as above. */
async function openCompileVideoDialog(page: Page, testInfo: TestInfo): Promise<Locator> {
  const trigger = page.getByRole('button', { name: /^Step 1, Compile Video:/ }).first();
  await expect(trigger).toBeVisible();
  if (testInfo.project.name === 'mobile') {
    await trigger.click();
  } else {
    await trigger.focus();
    await page.keyboard.press('Enter');
  }
  const dialog = compileVideoDialog(page);
  await expect(dialog).toBeVisible();
  await settleSettingsPanel(page, testInfo);
  await expect(dialog.getByRole('combobox', { name: 'Which clips to keep' })).toBeVisible();
  return dialog;
}

test.describe('video-generation (phase 1)', () => {
  test.describe.configure({ mode: 'parallel' });

  test('admin creates a MiniMax VideoGeneration provider via the New Provider form', async ({ page }, testInfo) => {
    // Dedicated admin login on this test's own (fresh) context. Deliberately does NOT use the
    // `authenticatedPage` fixture, so the shared e2e/.auth/user.json cache is neither read nor written.
    const email = process.env.ADMIN_EMAIL || process.env.TEST_EMAIL || 'test@example.com';
    const password = process.env.ADMIN_PASSWORD || process.env.TEST_PASSWORD || 'password123';
    const login = await page.request.post(`${BASE_URL}/api/v1/auth/token`, { data: { email, password } });
    expect(login.status(), `admin login as ${email} failed`).toBe(200);
    const loginBody = (await login.json()) as { isAdmin?: boolean; mustChangePassword?: boolean };
    if (loginBody.isAdmin !== true) {
      throw new Error(
        `User ${email} is not an admin: inference-provider management is admin-only. ` +
          'Set ADMIN_EMAIL/ADMIN_PASSWORD to an admin account.'
      );
    }
    if (loginBody.mustChangePassword === true) {
      throw new Error(`Admin ${email} must change its password before it can use the admin UI.`);
    }

    const providerName = `e2e MiniMax ${uniqueSuffix(testInfo)}`;
    let providerId: string | undefined;

    try {
      await page.goto('/app/admin/inference-providers');
      await expect(page).toHaveURL(/\/app\/admin\/inference-providers$/);
      await page.getByRole('button', { name: 'New Provider' }).click();

      const modal = page.getByRole('dialog', { name: 'New Provider' });
      await expect(modal).toBeVisible();

      await modal.getByLabel('Name', { exact: true }).fill(providerName);

      // Pick from the keyboard. In the full-screen phone dialog the list is positioned while the
      // dialog is still sliding in, lands partly off screen, and a click on it closes it again, so
      // an option cannot be clicked reliably there; ArrowDown/Enter works on both layouts.
      const kind = modal.getByRole('combobox', { name: 'Kind' });
      await kind.focus();
      await kind.press('ArrowDown');
      // The Kind list is the only place a provider kind is discoverable at all, so Higgsfield has to
      // be listed here. Presence only — this provider row is still the MiniMax one below.
      await expect(page.getByRole('option', { name: 'Higgsfield', exact: true })).toHaveCount(1);
      // Issue #93 phase 5: the self-hosted ComfyUI kind is listed too (presence only, same reason).
      await expect(page.getByRole('option', { name: 'ComfyUI (self-hosted)', exact: true })).toHaveCount(1);
      await highlightOption(kind, 'MiniMax');
      await kind.press('Enter');
      await expect(modal.getByRole('combobox', { name: 'Kind' })).toHaveValue('MiniMax');

      // Choosing MiniMax must auto-switch Capability to Video Generation and prefill the endpoint.
      await expect(modal.getByRole('combobox', { name: 'Capability' })).toHaveValue('Video Generation');
      const baseUrl = modal.getByLabel('Base URL');
      await expect(baseUrl).toHaveValue(MINIMAX_DEFAULT_ENDPOINT);

      await modal.getByLabel('Model', { exact: true }).fill('MiniMax-H3');
      await modal.getByLabel('API Key').fill(FAKE_API_KEY);
      await modal.getByLabel('Timeout (seconds)').fill('120');

      // Never default; stays enabled.
      const defaultSwitch = modal.getByRole('switch', { name: /^Set as default/ });
      await expect(defaultSwitch).not.toBeChecked();
      await expect(modal.getByRole('switch', { name: 'Enabled' })).toBeChecked();

      const createResponsePromise = page.waitForResponse(
        (r) => r.request().method() === 'POST' && /\/api\/v1\/inference-providers$/.test(new URL(r.url()).pathname)
      );
      await modal.getByRole('button', { name: 'Create', exact: true }).click();
      const createResponse = await createResponsePromise;
      expect(createResponse.status(), await createResponse.text()).toBe(201);
      // The model catalog made the provider an ACCOUNT: kind, endpoint, key and timeout live on it,
      // while the model code and its capabilities (and their default/enabled flags) live under it.
      const created = (await createResponse.json()) as ProviderTree;
      providerId = created.id;
      expect(providerId).toMatch(/^[0-9a-f-]{36}$/i);
      expect(created.kind).toBe('MiniMax');
      expect(created.isEnabled).toBe(true);
      expect(created.hasApiKey).toBe(true);
      expect(created.apiKeyLastFour).toBe(FAKE_API_KEY.slice(-4));
      expect(created.endpoint).toBe(MINIMAX_DEFAULT_ENDPOINT);
      expect(created.models.map((m) => m.modelCode)).toEqual(['MiniMax-H3']);
      expect(created.models[0].capabilities.map((c) => c.capability)).toEqual(['VideoGeneration']);
      expect(created.models[0].capabilities[0].isDefault).toBe(false);
      expect(created.models[0].capabilities[0].isEnabled).toBe(true);

      await expect(modal).toBeHidden();

      const row = page.getByRole('row').filter({ hasText: providerName });
      await expect(row).toHaveCount(1);
      await expect(row.getByText('MiniMax', { exact: true })).toBeVisible();
      await expect(row.getByText('VideoGeneration', { exact: true })).toBeVisible();
      await expect(row.getByText('Default', { exact: true })).toHaveCount(0);

      // The persisted row agrees with what the table shows.
      const getResponse = await page.request.get(`${BASE_URL}/api/v1/inference-providers/${providerId}`);
      expect(getResponse.status()).toBe(200);
      const persisted = (await getResponse.json()) as ProviderTree;
      expect(persisted.name).toBe(providerName);
      expect(persisted.kind).toBe('MiniMax');
      expect(persisted.models[0].capabilities[0].capability).toBe('VideoGeneration');
      expect(persisted.models[0].capabilities[0].isDefault).toBe(false);
      expect(persisted.timeoutSeconds).toBe(120);
    } finally {
      if (!providerId) {
        // Create response was never observed (e.g. the form errored late): find it by its unique name.
        const list = await page.request.get(`${BASE_URL}/api/v1/inference-providers`);
        if (list.ok()) {
          const providers = (await list.json()) as Array<{ id: string; name: string }>;
          providerId = providers.find((p) => p.name === providerName)?.id;
        }
      }
      if (providerId) {
        const del = await page.request.delete(`${BASE_URL}/api/v1/inference-providers/${providerId}`);
        expect(del.ok(), `teardown: DELETE inference provider ${providerId} returned ${del.status()}`).toBe(true);
      }
    }
  });

  test('builder persists a Generate Video step config (UI reload + API read-back)', async ({
    authenticatedPage: page,
    scratchProject,
    request,
  }, testInfo) => {
    test.setTimeout(90000);
    const projectId = scratchProject.id;
    const suffix = uniqueSuffix(testInfo);
    const workflowName = `e2e Generate Video ${suffix}`;
    const prompt = `A slow aerial shot over misty mountains at sunrise (${suffix})`;

    await page.goto(`/app/projects/${projectId}/workflows/new`);
    await page.waitForLoadState('networkidle');
    // The template list renders once the page has hydrated and loaded; acting before that can be lost.
    await expect(page.getByRole('radiogroup', { name: 'Templates' })).toBeVisible({ timeout: 20000 });
    await page.getByPlaceholder('My Workflow').fill(workflowName);

    await addGenerateVideoStep(page, testInfo);
    const dialog = await openGenerateVideoDialog(page, testInfo);

    await dialog.getByRole('textbox', { name: 'Prompt' }).fill(prompt);

    // The Model select is searchable: type the model and confirm with Enter. Clicking the option is
    // not reliable here, because the settings panel moves focus to the step label as it settles and
    // closes the dropdown under the click.
    const model = dialog.getByRole('combobox', { name: 'Model' });
    await model.fill('MiniMax-H3-Max');
    // Typing normally opens the list; ArrowDown opens it if the settling panel closed it again.
    await model.press('ArrowDown');
    await expect(page.getByRole('option', { name: 'MiniMax-H3-Max', exact: true })).toHaveCount(1);
    await highlightOption(model, 'MiniMax-H3-Max');
    await model.press('Enter');
    await expect(dialog.getByRole('combobox', { name: 'Model' })).toHaveValue('MiniMax-H3-Max');

    // Max Clips before Takes: Takes is clamped to Max Clips.
    await dialog.getByLabel('Max Clips', { exact: true }).fill('3');
    await dialog.getByLabel('Takes', { exact: true }).fill('2');
    await dialog.getByLabel('Max spend (credits)').fill('2.5');
    await dialog.getByLabel('Max spend (credits)').blur();

    await expect(dialog.getByLabel('Max Clips', { exact: true })).toHaveValue('3');
    await expect(dialog.getByLabel('Takes', { exact: true })).toHaveValue('2');
    await expect(dialog.getByLabel('Max spend (credits)')).toHaveValue('2.5');
    await expect(dialog.getByText('Takes cannot exceed Max Clips')).toHaveCount(0);

    await closeDialog(page, dialog);

    // A name typed before the page finished hydrating can be reset by it; type it again.
    await page.getByPlaceholder('My Workflow').fill(workflowName);
    await page.getByRole('button', { name: 'Create Workflow' }).click();
    // The saved workflow opens on its App view; the URL may carry ?view=.
    await page.waitForURL(/\/app\/projects\/[^/]+\/workflows\/[0-9a-f-]{36}(\?.*)?$/i);
    const workflowId = page.url().match(/\/workflows\/([0-9a-f-]{36})/i)![1];

    // API read-back: the step's persisted VideoGenerate config, 1-based stepOrder.
    const getResponse = await request.get(`${BASE_URL}/api/v1/projects/${projectId}/workflows/${workflowId}`);
    expect(getResponse.status()).toBe(200);
    const workflow = (await getResponse.json()) as {
      name: string;
      steps: Array<{ stepType: string; stepOrder: number; videoGenerateConfigJson: string | null }>;
    };
    expect(workflow.name).toBe(workflowName);
    expect(workflow.steps).toHaveLength(1);
    const step = workflow.steps[0];
    expect(step.stepType).toBe('VideoGenerate');
    expect(step.stepOrder).toBe(1);
    expect(step.videoGenerateConfigJson).toBeTruthy();
    const config = JSON.parse(step.videoGenerateConfigJson!) as Record<string, unknown>;
    expect(config).toMatchObject({
      planSource: 'Inline',
      inlinePrompt: prompt,
      model: 'MiniMax-H3-Max',
      maxClips: 3,
      takes: 2,
      maxSpendCredits: 2.5,
      // Issue #93 phases 4-5: the editor persists the five appended knobs at their C# defaults, so
      // a step saved from the dialog is byte-equivalent to one the backend would default itself —
      // look matching and camera continuity off, no GPU-minute cap, no conform pass.
      lookMatching: false,
      cameraContinuity: false,
      maxGpuMinutes: 0,
      conformFps: 0,
      conformHeight: 0,
    });

    // UI reload: the dialog shows the saved values.
    // The saved workflow opens on its App view; the step dialog lives in the builder (?view=advanced).
    await page.goto(`/app/projects/${projectId}/workflows/${workflowId}?view=advanced`);
    await page.waitForLoadState('networkidle');
    const reloaded = await openGenerateVideoDialog(page, testInfo);
    await expect(reloaded.getByRole('textbox', { name: 'Prompt' })).toHaveValue(prompt);
    await expect(reloaded.getByRole('combobox', { name: 'Model' })).toHaveValue('MiniMax-H3-Max');
    await expect(reloaded.getByLabel('Max Clips', { exact: true })).toHaveValue('3');
    await expect(reloaded.getByLabel('Takes', { exact: true })).toHaveValue('2');
    await expect(reloaded.getByLabel('Max spend (credits)')).toHaveValue('2.5');
    await closeDialog(page, reloaded);
  });

  test('Generate Video editor offers the plan source and its step-reference picker', async ({
    authenticatedPage: page,
    scratchProject,
  }, testInfo) => {
    test.setTimeout(90000);

    await page.goto(`/app/projects/${scratchProject.id}/workflows/new`);
    await page.waitForLoadState('networkidle');
    // The template list renders once the page has hydrated and loaded; acting before that can be lost.
    await expect(page.getByRole('radiogroup', { name: 'Templates' })).toBeVisible({ timeout: 20000 });
    await page.getByPlaceholder('My Workflow').fill(`e2e Generate Video plan source ${uniqueSuffix(testInfo)}`);

    await addGenerateVideoStep(page, testInfo);
    const dialog = await openGenerateVideoDialog(page, testInfo);

    // List + type-check assertions only: this test never saves, runs or costs anything.
    const planSource = dialog.getByRole('combobox', { name: 'What to generate' });
    await expect(planSource).toBeVisible();
    await expect(planSource).toHaveValue('Inline prompt');

    // Inline is the default, so neither reference picker is offered yet.
    await expect(dialog.getByRole('combobox', { name: 'Plan step' })).toHaveCount(0);
    await expect(dialog.getByRole('combobox', { name: 'Analysis step (optional)' })).toHaveCount(0);

    await planSource.click();
    await page.getByRole('option', { name: 'Plan from a planner step', exact: true }).click();
    await expect(planSource).toHaveValue('Plan from a planner step');

    // Plan-sourced: the required plan picker (with its required-field error) and the optional
    // analysis picker both appear.
    await expect(dialog.getByRole('combobox', { name: 'Plan step' })).toBeVisible();
    await expect(dialog.getByText(PLAN_REF_REQUIRED_ERROR, { exact: true })).toBeVisible();
    await expect(dialog.getByRole('combobox', { name: 'Analysis step (optional)' })).toBeVisible();

    await closeDialog(page, dialog);
  });

  test('Compile Video editor offers the generated-clips switch and reveals its picker', async ({
    authenticatedPage: page,
    scratchProject,
  }, testInfo) => {
    test.setTimeout(90000);

    await page.goto(`/app/projects/${scratchProject.id}/workflows/new`);
    await page.waitForLoadState('networkidle');
    // The template list renders once the page has hydrated and loaded; acting before that can be lost.
    await expect(page.getByRole('radiogroup', { name: 'Templates' })).toBeVisible({ timeout: 20000 });
    await page.getByPlaceholder('My Workflow').fill(`e2e Compile Video generated clips ${uniqueSuffix(testInfo)}`);

    await addCompileVideoStep(page, testInfo);
    const dialog = await openCompileVideoDialog(page, testInfo);

    // List + type-check assertions only: this test never saves, runs or costs anything.
    const generatedClips = dialog.getByRole('switch', { name: 'Add generated clips' });
    await expect(generatedClips).toBeVisible();
    await expect(generatedClips).not.toBeChecked();

    // Off by default, so the picker it governs is not offered yet.
    await expect(dialog.getByRole('combobox', { name: 'Generated clips from' })).toHaveCount(0);

    await generatedClips.click();
    await expect(generatedClips).toBeChecked();

    // On: the step-reference picker (defaulting to the previous step's output, same as graphicsPlan)
    // and the applied-clip cap both appear.
    const plan = dialog.getByRole('combobox', { name: 'Generated clips from' });
    await expect(plan).toBeVisible();
    await expect(plan).toHaveValue('Previous step output');
    await expect(dialog.getByLabel('Max generated clips')).toHaveValue('8');

    await closeDialog(page, dialog);
  });

  test('builder rejects a Generate Video step left at the Max Spend 0 default and persists nothing', async ({
    authenticatedPage: page,
    scratchProject,
    request,
  }, testInfo) => {
    const projectId = scratchProject.id;
    const workflowName = `e2e Generate Video invalid ${uniqueSuffix(testInfo)}`;

    // Any workflow write for this project counts as "persisted".
    const workflowWrites: string[] = [];
    page.on('request', (r) => {
      const path = new URL(r.url()).pathname;
      if (['POST', 'PUT'].includes(r.method()) && path.startsWith(`/api/v1/projects/${projectId}/workflows`)) {
        workflowWrites.push(`${r.method()} ${path}`);
      }
    });

    await page.goto(`/app/projects/${projectId}/workflows/new`);
    await page.waitForLoadState('networkidle');
    // The template list renders once the page has hydrated and loaded; acting before that can be lost.
    await expect(page.getByRole('radiogroup', { name: 'Templates' })).toBeVisible({ timeout: 20000 });
    await page.getByPlaceholder('My Workflow').fill(workflowName);

    await addGenerateVideoStep(page, testInfo);
    const dialog = await openGenerateVideoDialog(page, testInfo);
    await dialog.getByRole('textbox', { name: 'Prompt' }).fill('A prompt that must never reach MiniMax');
    // Max Spend is left at the new-step default of 0, which the backend's Validate() rejects.
    await expect(dialog.getByLabel('Max spend (credits)')).toHaveValue('0');
    // The field itself flags the 0 while the dialog is open.
    await expect(dialog.getByText(MAX_SPEND_ERROR, { exact: true })).toBeVisible();
    await closeDialog(page, dialog);

    // A name typed before the page finished hydrating can be reset by it; type it again.
    await page.getByPlaceholder('My Workflow').fill(workflowName);
    await page.getByRole('button', { name: 'Create Workflow' }).click();

    // Save is blocked client-side by a red Mantine notification (title "Error").
    const notification = page
      .getByRole('alert')
      .filter({ has: page.getByText(STEP_1_MAX_SPEND_ERROR, { exact: true }) });
    await expect(notification).toHaveCount(1, { timeout: 10000 });
    await expect(notification).toBeVisible();
    await expect(notification.getByText('Error', { exact: true })).toBeVisible();

    await expect(page).toHaveURL(new RegExp(`/app/projects/${projectId}/workflows/new$`));
    await expect(page.getByRole('alert').filter({ hasText: 'Created' })).toHaveCount(0);
    expect(workflowWrites).toEqual([]);

    const list = await request.get(`${BASE_URL}/api/v1/projects/${projectId}/workflows`);
    expect(list.status()).toBe(200);
    const workflows = (await list.json()) as Array<{ name: string }>;
    expect(workflows.map((w) => w.name)).not.toContain(workflowName);
  });

  test('API rejects a VideoGenerate step with maxSpendCredits 0 (400) and persists nothing', async ({
    scratchProject,
    request,
  }, testInfo) => {
    const projectId = scratchProject.id;
    const workflowName = `e2e Generate Video API invalid ${uniqueSuffix(testInfo)}`;

    // VideoGenerate steps carry the non-LLM VideoTransform placeholder agent (FlowchartBuilder does the same).
    const agentsResponse = await request.get(`${BASE_URL}/api/v1/agents`);
    expect(agentsResponse.status()).toBe(200);
    const agents = (await agentsResponse.json()) as Array<{ id: string; agentType: string }>;
    const videoTransformAgentId = agents.find((a) => a.agentType === 'VideoTransform')?.id;
    expect(videoTransformAgentId, 'built-in VideoTransform agent must be seeded').toBeTruthy();

    const create = await request.post(`${BASE_URL}/api/v1/projects/${projectId}/workflows`, {
      data: {
        name: workflowName,
        steps: [
          {
            agentDefinitionId: videoTransformAgentId,
            stepOrder: 1,
            label: 'Step 1',
            stepType: 'VideoGenerate',
            videoGenerateConfigJson: JSON.stringify({
              planSource: 'Inline',
              inlinePrompt: 'A prompt that must never reach MiniMax',
              model: 'MiniMax-H3',
              takes: 1,
              maxClips: 1,
              maxSpendCredits: 0,
            }),
          },
        ],
      },
    });
    const bodyText = await create.text();
    expect(create.status(), bodyText).toBe(400);
    const body = JSON.parse(bodyText) as { message?: unknown; errors?: unknown };
    expect(typeof body.message).toBe('string');
    expect((body.message as string).length).toBeGreaterThan(0);
    expect(Array.isArray(body.errors)).toBe(true);
    const errors = body.errors as unknown[];
    expect(errors.length).toBeGreaterThan(0);
    // StepConfigSaveValidator prefixes each error with the step order and type.
    expect(errors).toContain('Step 1 (VideoGenerate): MaxSpendCredits must be positive (greater than 0)');
    expect(body.message as string).toContain('MaxSpendCredits must be positive (greater than 0)');

    const list = await request.get(`${BASE_URL}/api/v1/projects/${projectId}/workflows`);
    expect(list.status()).toBe(200);
    const workflows = (await list.json()) as Array<{ name: string }>;
    expect(workflows.map((w) => w.name)).not.toContain(workflowName);
  });
});
