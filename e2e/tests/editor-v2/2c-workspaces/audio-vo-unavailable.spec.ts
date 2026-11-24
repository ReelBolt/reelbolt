import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * WS-156 / TL-422 on WebKit. Playwright's WebKit has no MediaRecorder (and no fake microphone), so the Voiceover panel must
 * state that plainly instead of showing a Record button that cannot work. Recording itself is proved on Chromium
 * (audio-vo.spec.ts, fake microphone); on a real iPhone it is a manual check.
 */
test('WS-156 phone WebKit: the Voiceover panel says the browser cannot record and offers no dead Record button', async ({ freshTimeline, openEditor }) => {
  runOn('iphone14webkit');
  const ed = await openEditor(await freshTimeline('blank'));
  await ed.workspace('audio');
  const hasRecorder = await ed.page.evaluate(() => typeof (window as unknown as { MediaRecorder?: unknown }).MediaRecorder !== 'undefined');
  expect(hasRecorder, 'Playwright WebKit has no MediaRecorder; if this changes, move the project to the recording spec').toBe(false);
  await expect(ed.testId('vo-unavailable')).toBeVisible();
  await expect(ed.testId('vo-unavailable')).toContainText(/Recording is not available in this browser. Upload a voice file instead./);
  await expect(ed.testId('vo-record'), 'no Record button that cannot work').toHaveCount(0);
});
