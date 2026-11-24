import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { createPicture, openPicture, pickAiTool, picturesUrl } from '../../../fixtures/editor-v2/picture';
import { knownFailure } from '../expected-failures';

/**
 * T16 (TH-SUBJ-01, remediation-plan.md W0.8): "Cut out a subject" says up front whether it can run here,
 * probed like the other AI tools (it runs on the same segmentation service as "Remove background").
 * The audit (static T16, d6): no probe for the cut-out, so without a provider the user filled in marks,
 * pressed "Cut out" and only then got a red 409. The spec reads the free capabilities probe and checks the
 * panel agrees with it; it never presses "Cut out" (D9).
 */
test('T16 TH-SUBJ-01 "Cut out a subject" shows its availability before anything is run', async ({ page, api, seed }, testInfo) => {
  runOn('desktop', 'pixel7');
  knownFailure('T16');
  const probe = await api.request.get(`${picturesUrl(seed.projectId)}/operations/removeBackground/capabilities`, { failOnStatusCode: false });
  expect([200, 409], `capabilities probe -> ${probe.status()}`).toContain(probe.status());
  const available = probe.status() === 200;
  testInfo.annotations.push({ type: 'cut-out service', description: available ? 'set up' : 'not set up (409)' });

  const pic = await createPicture(api, seed.projectId, seed.files.still, `t16-${testInfo.project.name}-${Date.now()}`);
  await openPicture(page, seed.projectId, pic.id);
  await pickAiTool(page, 'Cut out a subject');
  const panel = page.getByTestId('ai-tool-subject');
  await expect(panel).toBeVisible();

  const notHere = panel.getByText('Not available here');
  const cutOut = panel.getByRole('button', { name: 'Cut out', exact: true });
  if (available) {
    await expect(cutOut, 'with a cut-out service the run button is offered').toBeVisible();
    await expect(notHere).toHaveCount(0);
  } else {
    await expect(notHere, 'without a cut-out service the panel says so before any mark is made').toBeVisible({ timeout: 10_000 });
    await expect(cutOut, 'no paid-looking "Cut out" button when it cannot run').toHaveCount(0);
    await panel.getByRole('button', { name: 'Change tool' }).click();
    const option = page.getByRole('dialog', { name: 'Choose an AI tool' }).getByTestId('ai-tool-option-subject');
    await expect(option, 'the tool list marks it like the other tools').toContainText('Not set up in this workspace');
  }
});
