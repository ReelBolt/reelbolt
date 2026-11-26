import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { createPicture, openPanel, openPicture, pickAiTool } from '../../../fixtures/editor-v2/picture';
import { knownFailure } from '../expected-failures';

/**
 * T13 (TH-AI, remediation-plan.md W0.8): what was typed into an AI tool survives a look at another tab.
 * The audit (static T13, d6, m4): the tab panels unmounted (`keepMounted={false}`), so typing a subject's
 * name, switching to Layers to pick a layer and coming back wiped the text. Nothing is run (D9).
 */
test('T13 the AI prompt survives a switch to the Layers tab and back', async ({ page, api, seed }, testInfo) => {
  runOn('desktop', 'pixel7');
  knownFailure('T13');
  const pic = await createPicture(api, seed.projectId, seed.files.still, `t13-${testInfo.project.name}-${Date.now()}`);
  await openPicture(page, seed.projectId, pic.id);

  await pickAiTool(page, 'Cut out a subject');
  const prompt = page.getByRole('textbox', { name: 'Or describe it' });
  await expect(prompt).toBeVisible();
  await prompt.fill('the red car on the left');

  await openPanel(page, 'Layers');
  await expect(page.getByRole('list', { name: 'Layers, front first' })).toBeVisible();
  await openPanel(page, 'AI');
  await expect(page.getByRole('textbox', { name: 'Or describe it' }), 'the typed prompt is still there').toHaveValue('the red car on the left');
});
