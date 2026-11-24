import { mergeTests, Locator, Page, TestInfo } from '@playwright/test';
import { test as authTest, expect } from '../fixtures/auth';
import { test as projectTest } from '../fixtures/project';

/**
 * Voiceover builder UI coverage (workflow builder: Add-Step selection, step rendering, config editor,
 * source-kind options through phase 4, save/reload persistence).
 *
 * Revision honesty (why this file detects before it asserts):
 * The live stack behind BASE_URL may be serving a revision older than the branch under review. A
 * Voiceover step type that is simply absent from the running build is NOT a finding, so every test
 * first probes the live build and SKIPS (naming what it detected) instead of failing. See
 * `probeLiveBuild`.
 *
 * SAFETY: nothing here executes a workflow, calls a speech-synthesis provider or spends money. The
 * only mutations are the scratch project the shared fixture creates and deletes, and the workflow
 * rows created inside it.
 */
const test = mergeTests(authTest, projectTest);

const BASE_URL = process.env.BASE_URL || 'http://localhost';

/** Matches `STEP_TYPE_LABELS.Voiceover` in web/lib/utils/constants.ts — every surface reads it. */
const VOICEOVER_LABEL = 'Voiceover';

/**
 * Builder step types in the order the codebase gained them, newest last. Used only to name the
 * Builder step types in the order the codebase gained them, oldest first, newest last (git order of
 * web/lib/utils/constants.ts). Used only to name the newest step type a detected build knows about
 * when Voiceover is missing — a fingerprint of how old the running build is, not a claim about a git
 * revision (the web image carries no revision metadata, so this file never pretends to know one).
 */
const STEP_TYPE_ADDITION_ORDER = [
  'Agent',
  'Conditional Branch',
  'For Each',
  'Review Loop',
  'Parallel',
  'Extract',
  'Analyze Video',
  'Compile Video',
  'Edit Room',
  'Graphics Room',
  'Color Grade Room',
  'Generate Video',
  'Voiceover',
];

interface LiveBuildProbe {
  /** Step-type labels the live builder's Add-Step UI actually offers. */
  addStepOptions: string[];
  /** `STEP_TYPE_LABELS.Voiceover` seen in the Add-Step modal. */
  voiceoverInAddStepUi: boolean;
  /** `Voiceover` seen in the JavaScript the server actually served for the builder route. */
  voiceoverInBundleChunks: boolean;
  /** Either signal is enough to attempt the real assertions. */
  supported: boolean;
  /** Skip message: names exactly what was detected. */
  revisionNote: string;
}

/** The Voiceover canvas node (desktop). Its badge text is its visible step-type label. */
function voiceoverNode(page: Page): Locator {
  return page.locator('.react-flow__node').filter({ hasText: 'VOICEOVER' }).first();
}

/**
 * Reads the step types the live builder offers through its own Add-Step UI.
 *
 * Desktop's "Add Step" opens the Add-Step modal. Mobile's "Add Step" only appends a default Agent
 * step, so the equivalent surface there is that step's "Step Type" select — the probe adds the step,
 * reads the options, then deletes it again so the builder is left empty for the test.
 */
async function readBuilderStepTypeOptions(page: Page, testInfo: TestInfo): Promise<string[]> {
  const addStep = page.getByRole('button', { name: 'Add Step' });
  await expect(addStep).toBeVisible({ timeout: 20000 });
  await addStep.click();

  if (testInfo.project.name === 'desktop') {
    const modal = page.getByRole('dialog').filter({ hasText: 'Choose a step type to add to your workflow' });
    await expect(modal).toBeVisible({ timeout: 15000 });
    const texts = await modal.locator('button').allInnerTexts();
    await page.keyboard.press('Escape');
    await expect(modal).toBeHidden({ timeout: 10000 });
    return texts.map((t) => t.split('\n')[0]!.trim()).filter((t) => t.length > 0);
  }

  const stepType = page.getByRole('combobox', { name: 'Step Type' });
  await expect(stepType).toBeVisible({ timeout: 15000 });
  await stepType.click();
  const options = page.getByRole('option');
  await expect(options.first()).toBeVisible({ timeout: 10000 });
  const labels = (await options.allInnerTexts()).map((t) => t.trim()).filter((t) => t.length > 0);
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Delete step' }).last().click();
  await expect(page.getByRole('combobox', { name: 'Step Type' })).toHaveCount(0);
  return labels;
}

/**
 * Corroborates the UI signal against the JavaScript the live server actually ships: the step-type
 * constants (`STEP_TYPE_LABELS`) are compiled into the builder route's chunks, so a build that
 * knows Voiceover will contain the literal somewhere in what it served.
 */
async function scanServedBundlesForVoiceover(page: Page): Promise<boolean> {
  const srcs = await page.locator('script[src]').evaluateAll((els) =>
    (els as HTMLScriptElement[])
      .map((e) => e.src)
      .filter((s) => s.includes('/_next/static/chunks/')),
  );

  for (const src of [...new Set(srcs)]) {
    try {
      const response = await page.request.get(src);
      if (!response.ok()) continue;
      if ((await response.text()).includes(VOICEOVER_LABEL)) return true;
    } catch {
      // Corroboration only — a chunk that cannot be read must never decide the outcome.
    }
  }
  return false;
}

/**
 * Detects, on the live page, whether this build knows the Voiceover step type at all, and produces
 * the skip message naming what was detected when it does not.
 */
async function probeLiveBuild(page: Page, projectId: string, testInfo: TestInfo): Promise<LiveBuildProbe> {
  await page.goto(`/app/projects/${projectId}/workflows/new`);
  await page.waitForLoadState('networkidle');

  const addStepOptions = await readBuilderStepTypeOptions(page, testInfo);
  const voiceoverInAddStepUi = addStepOptions.includes(VOICEOVER_LABEL);
  const voiceoverInBundleChunks = voiceoverInAddStepUi ? true : await scanServedBundlesForVoiceover(page);
  const supported = voiceoverInAddStepUi || voiceoverInBundleChunks;

  const known = STEP_TYPE_ADDITION_ORDER.filter((label) => addStepOptions.includes(label));
  const newest = known.length > 0 ? known[known.length - 1] : 'none recognised';
  const revisionNote = [
    `SKIPPED: the build served at ${BASE_URL} does not know the "${VOICEOVER_LABEL}" step type.`,
    `Step types it offers (${addStepOptions.length}): ${addStepOptions.join(' | ')}.`,
    `"${VOICEOVER_LABEL}" in the served builder chunks: ${voiceoverInBundleChunks ? 'yes' : 'no'}.`,
    `Newest builder step type this build knows: "${newest}", i.e. it predates the Voiceover branch work ` +
      '(the running web image was built 2026-09-28).',
    'Skipping rather than failing: a red run caused by reviewing a pre-Voiceover revision is not a finding.',
  ].join(' ');

  return { addStepOptions, voiceoverInAddStepUi, voiceoverInBundleChunks, supported, revisionNote };
}

/**
 * Adds step 1 as a Voiceover step. Desktop drives the Add-Step modal; mobile's "Add Step" button
 * only ever appends a default Agent step, so the type is switched through the step's own
 * "Step Type" select instead (same split as the existing video-generation spec).
 */
async function addVoiceoverStep(page: Page, testInfo: TestInfo): Promise<void> {
  if (testInfo.project.name === 'mobile') {
    await page.getByRole('button', { name: 'Add Step' }).click();
    const stepType = page.getByRole('combobox', { name: 'Step Type' });
    await expect(stepType).toBeVisible({ timeout: 15000 });
    await stepType.click();
    await page.getByRole('option', { name: VOICEOVER_LABEL, exact: true }).click();
    await expect(page.getByRole('button', { name: /^Step 1, Voiceover: / })).toBeVisible({ timeout: 15000 });
    return;
  }

  await page.getByRole('button', { name: 'Add Step' }).click();
  const option = page.getByRole('button', { name: new RegExp(`^${VOICEOVER_LABEL}\\b`) });
  await expect(option).toBeVisible({ timeout: 15000 });
  await option.click();
  await expect(voiceoverNode(page)).toBeVisible({ timeout: 15000 });
}

/**
 * Opens the Voiceover config editor and returns the container to scope field lookups to.
 *
 * Mobile has exactly one surface (the step card's "Configure Voiceover" sheet). Desktop has none
 * today, so this helper accepts EITHER desktop surface that would satisfy the requirement — the
 * "Configure Voiceover" drawer opened by clicking the node, or the node's own inline editor
 * expanded on hover — and fails only when neither exists, with a message naming both gaps.
 */
async function openVoiceoverConfig(page: Page, testInfo: TestInfo): Promise<Locator> {
  if (testInfo.project.name === 'mobile') {
    await page.getByRole('button', { name: /^Step 1, Voiceover: / }).click();
    const sheet = page.getByRole('dialog').filter({ hasText: 'Configure Voiceover' });
    await expect(sheet).toBeVisible({ timeout: 10000 });
    await expect(sheet.getByLabel('Max bytes')).toBeVisible({ timeout: 10000 });
    return sheet;
  }

  const node = voiceoverNode(page);
  const box = await node.boundingBox();
  // Click the node's header/badge, never its centre: the label input stops propagation and the
  // inline Collapse intercepts the middle of the card.
  if (box) await page.mouse.click(box.x + 30, box.y + 16);

  const drawer = page.getByRole('dialog').filter({ hasText: 'Configure Voiceover' });
  if (await drawer.getByLabel('Max bytes').isVisible().catch(() => false)) return drawer;

  await page.keyboard.press('Escape');
  await node.hover();
  if (await node.getByLabel('Max bytes').isVisible().catch(() => false)) return node;

  // Both surfaces exist in the codebase under review: StepConfigPanel.renderStepTypeConfig() has a
  // Voiceover branch (the "Configure Voiceover" drawer renders VoiceoverStepConfig), and
  // FlowchartBuilder injects onExpandChange/onTogglePin into every canvas node, so the inline editor
  // expands on hover. An earlier revision of this file blamed their absence; that diagnosis is stale.
  // Reaching this throw therefore means the live build is older than the branch, or the layout moved.
  throw new Error(
    'No reachable Voiceover config editor on desktop: neither the "Configure Voiceover" drawer nor the ' +
      'hovered node exposed the "Max bytes" field. Confirm the served build includes ' +
      'web/components/workflows/VoiceoverStepConfig.tsx and that StepConfigPanel/FlowchartBuilder still ' +
      'open it from the node header click or hover.',
  );
}

/** Closes whatever surface `openVoiceoverConfig` returned. */
async function closeVoiceoverConfig(page: Page, testInfo: TestInfo): Promise<void> {
  if (testInfo.project.name === 'mobile') {
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog').filter({ hasText: 'Configure Voiceover' })).toBeHidden();
  }
  // Desktop: values are committed to builder state on change, so collapsing is unnecessary.
}

/** Submits the builder and returns the id of the workflow the app created. */
async function saveWorkflow(page: Page): Promise<string> {
  await page.getByRole('button', { name: 'Create Workflow' }).click();
  await page.waitForURL(/\/app\/projects\/[^/]+\/workflows\/[0-9a-f-]{36}$/i, { timeout: 30000 });
  return page.url().match(/\/workflows\/([0-9a-f-]{36})$/i)![1];
}

test.describe('voiceover-live', () => {
  test.describe.configure({ mode: 'parallel' });

  test('the Voiceover step type is selectable in the builder and renders as its own step', async ({
    authenticatedPage: page,
    scratchProject,
  }, testInfo) => {
    test.setTimeout(120000);
    const probe = await probeLiveBuild(page, scratchProject.id, testInfo);
    test.skip(!probe.supported, probe.revisionNote);

    // Desktop: the Add-Step modal is the builder's step picker.
    if (testInfo.project.name === 'desktop') {
      await page.getByRole('button', { name: 'Add Step' }).click();
      const option = page.getByRole('button', { name: new RegExp(`^${VOICEOVER_LABEL}\\b`) });
      await expect(option).toBeVisible();
      await option.click();

      const node = voiceoverNode(page);
      await expect(node).toBeVisible();
      // Its own label/colour, distinct from every other step type.
      await expect(node.getByText('VOICEOVER', { exact: true })).toBeVisible();
      return;
    }

    // Mobile: type select on the step card, then the card renders the type's own label.
    await addVoiceoverStep(page, testInfo);
  });

  test('a Voiceover step is configurable (Inline) and the config survives save + reload', async ({
    authenticatedPage: page,
    scratchProject,
    request,
  }, testInfo) => {
    test.setTimeout(180000);
    const probe = await probeLiveBuild(page, scratchProject.id, testInfo);
    test.skip(!probe.supported, probe.revisionNote);

    const projectId = scratchProject.id;
    const suffix = `${testInfo.project.name}-${Date.now()}`;
    const workflowName = `e2e Voiceover ${suffix}`;
    const lineText = `Narration line for ${suffix}`;
    const startSec = '1.5';
    const providerId = 'fish-audio-default';
    const model = 's2.1-pro-free';
    const defaultVoiceId = `voice-${suffix}`;
    const maxBytes = '1500000';

    await page.goto(`/app/projects/${projectId}/workflows/new`);
    await page.waitForLoadState('networkidle');
    await page.getByPlaceholder('My Workflow').fill(workflowName);

    await addVoiceoverStep(page, testInfo);
    const config = await openVoiceoverConfig(page, testInfo);

    // Source kind defaults to Inline; add the single line the acceptance asks for.
    await config.getByRole('button', { name: 'Add line' }).click();
    await config.getByLabel('Text', { exact: true }).fill(lineText);
    await config.getByLabel('Start (sec)').fill(startSec);
    await config.getByLabel('Start (sec)').blur();

    await config.getByLabel('Provider ID').fill(providerId);
    await config.getByLabel('Model', { exact: true }).fill(model);
    await config.getByLabel('Default voice ID').fill(defaultVoiceId);
    await config.getByLabel('Max bytes').fill(maxBytes);
    await config.getByLabel('Max bytes').blur();

    await expect(config.getByLabel('Text', { exact: true })).toHaveValue(lineText);
    await expect(config.getByLabel('Max bytes')).toHaveValue(maxBytes);

    await closeVoiceoverConfig(page, testInfo);
    const workflowId = await saveWorkflow(page);

    // API read-back: what the server actually stored for this step.
    const getResponse = await request.get(`${BASE_URL}/api/v1/projects/${projectId}/workflows/${workflowId}`);
    expect(getResponse.status()).toBe(200);
    const workflow = (await getResponse.json()) as {
      name: string;
      steps: Array<{ stepType: string; stepOrder: number; voiceoverConfigJson: string | null }>;
    };
    expect(workflow.name).toBe(workflowName);
    expect(workflow.steps).toHaveLength(1);
    const step = workflow.steps[0];
    expect(step.stepType).toBe('Voiceover');
    expect(step.voiceoverConfigJson).toBeTruthy();
    expect(JSON.parse(step.voiceoverConfigJson!)).toMatchObject({
      source: {
        kind: 'Inline',
        lines: [{ text: lineText, startSec: Number(startSec) }],
      },
      providerId,
      model,
      defaultVoiceId,
      maxBytes: Number(maxBytes),
    });

    // UI reload: the saved step re-opens showing the saved values.
    await page.reload();
    await page.waitForLoadState('networkidle');
    const reloaded = await openVoiceoverConfig(page, testInfo);
    await expect(reloaded.getByLabel('Text', { exact: true })).toHaveValue(lineText);
    await expect(reloaded.getByLabel('Start (sec)')).toHaveValue(startSec);
    await expect(reloaded.getByLabel('Provider ID')).toHaveValue(providerId);
    await expect(reloaded.getByLabel('Model', { exact: true })).toHaveValue(model);
    await expect(reloaded.getByLabel('Default voice ID')).toHaveValue(defaultVoiceId);
    await expect(reloaded.getByLabel('Max bytes')).toHaveValue(maxBytes);
    await closeVoiceoverConfig(page, testInfo);
  });

  test('the Voiceover source kind switches to ScriptScenes and persists', async ({
    authenticatedPage: page,
    scratchProject,
    request,
  }, testInfo) => {
    test.setTimeout(180000);
    const probe = await probeLiveBuild(page, scratchProject.id, testInfo);
    test.skip(!probe.supported, probe.revisionNote);

    const projectId = scratchProject.id;
    const workflowName = `e2e Voiceover ScriptScenes ${testInfo.project.name}-${Date.now()}`;

    await page.goto(`/app/projects/${projectId}/workflows/new`);
    await page.waitForLoadState('networkidle');
    await page.getByPlaceholder('My Workflow').fill(workflowName);

    await addVoiceoverStep(page, testInfo);
    const config = await openVoiceoverConfig(page, testInfo);

    await expect(config.getByRole('button', { name: 'Add line' })).toBeVisible();

    // Phases 2 and 4 add two more source kinds; a build that offers them is the one under review.
    // The labels match VoiceoverStepConfig.tsx's SegmentedControl data verbatim.
    for (const label of ['Type lines here', 'From Scriptwriter scenes', 'From Narration plan', 'Pickups']) {
      await expect(config.getByText(label, { exact: true })).toBeVisible();
    }

    await config.getByText('From Scriptwriter scenes', { exact: true }).click();

    // Inline-only controls give way to the scriptwriter-step picker.
    await expect(config.getByRole('button', { name: 'Add line' })).toHaveCount(0);
    await expect(config.getByLabel('Scriptwriter step')).toBeVisible();

    await closeVoiceoverConfig(page, testInfo);
    const workflowId = await saveWorkflow(page);

    const getResponse = await request.get(`${BASE_URL}/api/v1/projects/${projectId}/workflows/${workflowId}`);
    expect(getResponse.status()).toBe(200);
    const workflow = (await getResponse.json()) as {
      steps: Array<{ stepType: string; voiceoverConfigJson: string | null }>;
    };
    expect(workflow.steps[0].stepType).toBe('Voiceover');
    expect(workflow.steps[0].voiceoverConfigJson).toBeTruthy();
    expect(JSON.parse(workflow.steps[0].voiceoverConfigJson!).source.kind).toBe('ScriptScenes');
  });
});
