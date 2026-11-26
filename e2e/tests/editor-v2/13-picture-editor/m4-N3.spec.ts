import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { box, createPicture, openPanel, openPicture, picturesUrl } from '../../../fixtures/editor-v2/picture';
import { knownFailure } from '../expected-failures';

/**
 * m4 N3 (T15, remediation-plan.md W0.8): on a phone the layer buttons (show/hide, lock, move, delete) and
 * the zoom buttons are 44 px targets. The audit (m4, measured): 22x22 layer buttons, 28x28 zoom buttons.
 * A touch-only row: desktop keeps its compact mouse controls, so it runs on the phone.
 */
test('m4-N3 T15 layer and zoom buttons are 44 px touch targets on a phone', async ({ page, api, seed }, testInfo) => {
  runOn('pixel7');
  knownFailure('m4-N3');
  const pic = await createPicture(api, seed.projectId, seed.files.still, `m4n3-${testInfo.project.name}-${Date.now()}`);
  // A second layer so the move forward/back arrows are shown too.
  await api.json('POST', `${picturesUrl(seed.projectId, pic.id)}/layers/text`, {
    content: {
      text: 'm4-N3', fontFamily: 'Inter', fontWeight: 700, fontSize: 96, lineHeight: 1.2, letterSpacing: 0,
      align: 'center', color: '#ffffff', stroke: null, shadow: null, maxWidth: null, uppercase: false,
    },
  }, [200, 201]);
  const stage = await openPicture(page, seed.projectId, pic.id);

  await openPanel(page, 'Layers');
  const list = page.getByRole('list', { name: 'Layers, front first' });
  await list.scrollIntoViewIfNeeded();
  // The phone's layer rows keep the select and show/hide buttons and put lock, move and delete in the row's menu: measure every button in the list.
  const buttons = list.getByRole('button');
  const n = await buttons.count();
  expect(n, 'select, show/hide and the row menu for two layers').toBeGreaterThanOrEqual(4);
  const small: string[] = [];
  for (let i = 0; i < n; i++) {
    const b = buttons.nth(i);
    if (!(await b.isVisible())) continue;
    const r = await box(b, 'a layer button');
    if (r.width < 44 - 0.5 || r.height < 44 - 0.5) small.push(`${await b.getAttribute('aria-label')} ${Math.round(r.width)}x${Math.round(r.height)}`);
  }
  for (const name of ['Zoom out', 'Zoom in', 'Fit the picture on screen']) {
    const r = await box(stage.getByRole('button', { name, exact: true }), name);
    if (r.width < 44 - 0.5 || r.height < 44 - 0.5) small.push(`${name} ${Math.round(r.width)}x${Math.round(r.height)}`);
  }
  expect(small, 'every layer and zoom button is at least 44x44 px').toEqual([]);
});
