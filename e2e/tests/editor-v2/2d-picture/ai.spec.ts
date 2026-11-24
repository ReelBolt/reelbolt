import type { Page } from '@playwright/test';
import * as path from 'path';
import {
  test, expect, openFresh, pollPicture, isPhone, press, addBadgeLayer, layerOf, openTab, stageOf, stageInView, type PictureSeed,
} from '../../../fixtures/editor-v2/picture-lane';
import { pickAiTool, picturesUrl } from '../../../fixtures/editor-v2/picture';
import { runOn } from '../../../fixtures/editor-v2/rows';
import type { EditorApi } from '../../../fixtures/editor-v2/api';

/**
 * Lane D, picture editor: the AI tab (inventory rows PE-140..PE-156, PE-160, PE-206). NO PAID PROVIDER IS EVER CALLED: the isolated stack has
 * none, so two things are proven per tool: (1) the honest "not available here" state with no stub at all, and (2) the success path with the
 * operation endpoints answered by `page.route` stubs, which hand back a layer made through the REST API beforehand. The stubs sit at the
 * browser boundary only; the editor's own flow (flush, request, refetch, select, one undo step, message) is the real one.
 */

type Tool = { id: string; title: string; op: string | null; needsMask?: boolean; prompt?: string; blocker?: string };
// The app registers a service worker; Playwright WebKit cannot route requests it handles, so route stubs need it blocked (as 09-analysis/transcript does).
test.use({ serviceWorkers: 'block' });

const TOOLS: Tool[] = [
  { id: 'subject', title: 'Cut out a subject', op: 'removeBackground' },
  { id: 'removeBackground', title: 'Remove background', op: 'removeBackground' },
  { id: 'replaceBackground', title: 'New background', op: 'replaceBackground', prompt: 'a sunny beach', blocker: 'Describe it: new background.' },
  { id: 'erase', title: 'Erase', op: 'erase', needsMask: true, blocker: 'Paint over the area on the picture first.' },
  { id: 'fill', title: 'Fill', op: 'fill', needsMask: true, prompt: 'a red balloon', blocker: 'Paint over the area on the picture first.' },
  { id: 'instruct', title: 'Edit with words', op: 'instruct', prompt: 'make it warmer', blocker: 'Describe it: what to change.' },
  { id: 'expand', title: 'Expand', op: 'expand' },
  { id: 'upscale', title: 'Upscale', op: 'upscale' },
  { id: 'relight', title: 'Relight', op: 'relight' },
  { id: 'generate', title: 'Create a picture', op: 'generate', prompt: 'a red cat', blocker: 'Describe it: describe it.' },
  { id: 'designGraphic', title: 'Design a graphic', op: null },
];

const provider = { kind: 'FalAi', model: 'stub/model' };

async function stubCapabilities(page: Page): Promise<void> {
  await page.route(/\/operations\/[A-Za-z]+\/capabilities(\?.*)?$/, (route) => {
    const op = /operations\/([A-Za-z]+)\/capabilities/.exec(route.request().url())![1];
    return route.fulfill({ json: { operation: op, provider, takesPrompt: true, maxPromptLength: 1000, expand: null } });
  });
}

test('PE-140 PE-141 PE-142 PE-143 the AI tool picker opens, lists the 11 tools, closes with Escape or its close button, and each tool opens its own form', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  await openFresh(page, api, pics, `pe140-${testInfo.project.name}`);
  await openTab(page, 'AI');
  await press(page.getByRole('button', { name: /Choose an AI tool/ }), phone);
  const dialog = page.getByRole('dialog', { name: 'Choose an AI tool' });
  await expect(dialog).toBeVisible();
  for (const t of TOOLS) await expect(dialog.getByTestId(`ai-tool-option-${t.id}`), `${t.title} is offered`).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(dialog, 'Escape closes the picker').toBeHidden();
  await press(page.getByRole('button', { name: /Choose an AI tool/ }), phone);
  await press(dialog.getByRole('button', { name: 'Close' }), phone);
  await expect(dialog, 'the close button closes it too').toBeHidden();

  for (const t of TOOLS) {
    await pickAiTool(page, t.title);
    await expect(page.getByTestId(`ai-tool-${t.id}`), `${t.title} shows its form`).toBeVisible();
    const brush = page.getByRole('button', { name: 'Brush mask', exact: true });
    const subject = page.getByRole('button', { name: 'Cut out a subject', exact: true });
    const select = page.getByRole('button', { name: 'Select and move', exact: true });
    if (t.id === 'erase' || t.id === 'fill') await expect(brush, `${t.title} arms the brush`).toHaveAttribute('aria-pressed', 'true');
    else if (t.id === 'subject') await expect(subject, 'cut-out arms the subject tool').toHaveAttribute('aria-pressed', 'true');
    else await expect(select, `${t.title} returns to Select`).toHaveAttribute('aria-pressed', 'true');
  }
  // "Change tool" reopens the picker.
  await press(page.getByRole('button', { name: 'Change tool' }), phone);
  await expect(dialog).toBeVisible();
});

test('PE-144 with no AI provider every tool says so before anything is run: "Not available here", the server sentence, no run button', async ({ page, api, pics }, testInfo) => {
  runOn('desktop', 'pixel7');
  const phone = isPhone(testInfo.project.name);
  await openFresh(page, api, pics, `pe144-${testInfo.project.name}`);
  const dialogOf = () => page.getByRole('dialog', { name: 'Choose an AI tool' });
  const runLabel: Record<string, string> = {
    subject: 'Cut out', removeBackground: 'Remove the background', replaceBackground: 'Replace the background', erase: 'Erase the painted area',
    fill: 'Fill the painted area', instruct: 'Make the change', expand: 'Expand the picture', upscale: 'Upscale the picture',
    relight: 'Relight the picture', generate: 'Create the picture',
  };
  for (const t of TOOLS.filter((x) => x.op)) {
    const probe = await api.request.get(`${picturesUrl(pics.projectId)}/operations/${t.op}/capabilities`, { failOnStatusCode: false });
    expect([200, 409], `${t.op} probe -> ${probe.status()}`).toContain(probe.status());
    const available = probe.status() === 200;
    const message = available ? '' : ((await probe.json()) as { message?: string }).message ?? '';
    if (!available) expect(message.length, `${t.op}: the server explains in a sentence`).toBeGreaterThan(10);
    await pickAiTool(page, t.title);
    const panel = page.getByTestId(`ai-tool-${t.id}`);
    const run = panel.getByRole('button', { name: runLabel[t.id], exact: true });
    if (available) {
      await expect(run, `${t.title}: with a provider the run button is offered`).toBeVisible();
    } else {
      await expect(panel.getByText('Not available here'), `${t.title}: says so`).toBeVisible({ timeout: 15_000 });
      // The editor words the refusal itself (admin: set up; member: ask an admin), never the raw server sentence.
      await expect(panel.getByText(/Picture tools are not set up in this workspace yet\.|Ask your workspace admin to set up picture tools\./), `${t.title}: with the plain-language sentence`).toBeVisible();
      await expect(run, `${t.title}: no run button that could only fail`).toHaveCount(0);
      await press(panel.getByRole('button', { name: 'Change tool' }), phone);
      await expect(dialogOf().getByTestId(`ai-tool-option-${t.id}`), `${t.title}: the picker marks it`).toContainText('Not set up in this workspace');
      await page.keyboard.press('Escape');
    }
  }
});

test('PE-206 the operation probes and a run with no provider answer 409 with a code and a plain message (never a vendor call)', async ({ api, pics }) => {
  test.skip(test.info().project.name !== 'desktop', 'API contract row runs once, on desktop');
  const doc = await api.json<{ id: string }>('POST', picturesUrl(pics.projectId), { sourceProjectFileId: pics.quad, name: `pe206-${Date.now()}` }, [200, 201]);
  const ops = ['erase', 'fill', 'expand', 'replaceBackground', 'relight', 'upscale', 'instruct', 'generate', 'removeBackground'];
  for (const op of ops) {
    const res = await api.request.get(`${picturesUrl(pics.projectId)}/operations/${op}/capabilities`, { failOnStatusCode: false });
    expect([200, 409], `${op} -> ${res.status()}`).toContain(res.status());
    if (res.status() === 409) {
      const body = (await res.json()) as { code?: string; message?: string };
      expect(body.code, `${op} has a code`).toMatch(/_PROVIDER_UNAVAILABLE$|OPERATION_UNSUPPORTED/);
      expect((body.message ?? '').length, `${op} has a message`).toBeGreaterThan(10);
    }
  }
  // Request errors are validated before any provider is resolved (free to test).
  const bad = await api.request.post(`${picturesUrl(pics.projectId, doc.id)}/operations`, { data: { operation: 'nonsense' }, failOnStatusCode: false });
  expect(bad.status()).toBe(400);
});

async function paint(page: Page, phone: boolean): Promise<void> {
  await stageInView(page);
  const box = (await stageOf(page).boundingBox())!;
  const c = { x: box.x + box.width / 2, y: box.y + box.height / 2 };
  if (phone && test.info().project.name === 'pixel7') {
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: c.x, y: c.y }] });
    for (let i = 1; i <= 8; i++) await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ x: c.x + i * 10, y: c.y + i * 4 }] });
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  } else {
    await page.mouse.move(c.x, c.y);
    await page.mouse.down();
    await page.mouse.move(c.x + 80, c.y + 30, { steps: 8 });
    await page.mouse.up();
  }
}

async function stubOperation(page: Page, api: EditorApi, pics: PictureSeed, docId: string, op: string) {
  const layer = await addBadgeLayer(api, pics.projectId, docId, pics);
  const calls: Array<Record<string, unknown>> = [];
  // The browser cannot reach this stack's presigned storage address (cross-origin), and the AI call is stubbed anyway: accept the mask PUT.
  await page.route(/\/image-editor\/.*mask-/, (route) => {
    if (route.request().method() === 'OPTIONS') return route.fulfill({ status: 200, headers: { 'access-control-allow-origin': '*', 'access-control-allow-methods': 'PUT', 'access-control-allow-headers': '*' } });
    return route.fulfill({ status: 200, headers: { 'access-control-allow-origin': '*' }, body: '' });
  });
  await page.route(/\/image-documents\/[^/]+\/operations$/, async (route) => {
    if (route.request().method() !== 'POST') return route.continue();
    calls.push(route.request().postDataJSON() as Record<string, unknown>);
    return route.fulfill({ json: { layers: [layer], operation: op, provider, usage: null, revision: 99 } });
  });
  return { layer, calls };
}

const RUN: Record<string, { run: string; id: string }> = {
  removeBackground: { run: 'Remove the background', id: 'PE-146' },
  replaceBackground: { run: 'Replace the background', id: 'PE-147' },
  erase: { run: 'Erase the painted area', id: 'PE-148' },
  fill: { run: 'Fill the painted area', id: 'PE-149' },
  instruct: { run: 'Make the change', id: 'PE-150' },
  expand: { run: 'Expand the picture', id: 'PE-151' },
  upscale: { run: 'Upscale the picture', id: 'PE-152' },
  relight: { run: 'Relight the picture', id: 'PE-153' },
  generate: { run: 'Create the picture', id: 'PE-154' },
};

for (const t of TOOLS.filter((x) => x.op && x.id !== 'subject')) {
  const { run, id } = RUN[t.id];
  test(`${id} PE-156 ${t.title}: blockers say what is missing; with the service stubbed it runs, adds ONE selected layer and ONE undo step`, async ({ page, api, pics }, testInfo) => {
    const phone = isPhone(testInfo.project.name);
    await stubCapabilities(page);
    const { pic } = await openFresh(page, api, pics, `${id.toLowerCase()}-${testInfo.project.name}`);
    const { layer, calls } = await stubOperation(page, api, pics, pic.id, t.op!);
    await page.reload();
    await expect(stageOf(page)).toBeVisible();
    // The layer the tool works on: the original photo.
    await press(page.getByRole('list', { name: 'Layers, front first' }).getByRole('button', { name: `Select ${pic.layers[0].label}` }), phone);
    await pickAiTool(page, t.title);
    const panel = page.getByTestId(`ai-tool-${t.id}`);
    const button = panel.getByRole('button', { name: run, exact: true });
    await expect(button).toBeVisible();

    if (t.blocker) {
      await expect(button, 'disabled until the missing piece is supplied').toBeDisabled();
      await expect(panel.getByText(t.blocker), 'and the reason is written beside it').toBeVisible();
    }
    if (t.prompt) await panel.getByRole('textbox').first().fill(t.prompt);
    if (t.needsMask) {
      await paint(page, phone);
      await expect(page.getByText('Mask ready.')).toBeVisible();
    }
    if (t.id === 'expand') {
      const shape = panel.getByRole('textbox', { name: 'Shape' }).or(panel.getByRole('combobox', { name: 'Shape' })).first();
      await shape.click();
      await page.getByRole('option', { name: /^16:9/ }).click();
      await expect(panel.getByText(/The picture is already 16:9/).first(), 'asking for the shape it already is: written blocker').toBeVisible();
      await expect(button).toBeDisabled();
      await shape.click();
      await page.getByRole('option', { name: /^1:1/ }).click();
      await expect(panel.getByText(/becomes about/), 'the summary says what will happen').toBeVisible();
    }
    await expect(button).toBeEnabled();
    // After a raw CDP touch stroke Chromium swallows the next synthetic tap's click, so the run button is clicked.
    if (t.needsMask && testInfo.project.name === 'pixel7') await button.click();
    else await press(button, phone);

    await expect(page.getByTestId('result-bar-changed'), 'R14.4: the result bar says what changed').toContainText(`${t.title} added a new layer`, { timeout: 30_000 });
    expect(calls.length, 'one operation request').toBe(1);
    expect(calls[0].operation).toBe(t.op);
    if (t.id !== 'generate') expect(calls[0].layerId, 'the request names the selected layer').toBe(pic.layers[0].id);
    if (t.prompt) expect(calls[0].prompt).toBe(t.prompt);
    if (t.needsMask) expect(String(calls[0].maskKey), 'the mask was uploaded to storage first').toContain(`/image-editor/${pic.id}/mask-`);
    await expect(page.getByTestId('result-bar-cost'), 'R14.4: the bar shows the cost line, and no vendor jargon').toBeVisible();
    await expect(page.getByText('Last run: FalAi')).toHaveCount(0);

    // The result layer is the selected one, and ONE undo step hides it (undo never deletes).
    await openTab(page, 'Layers');
    await expect(page.getByRole('list', { name: 'Layers, front first' }).getByRole('button', { name: `Select ${layer.label}` }).first(), 'the result is selected').toHaveAttribute('aria-current', 'true');
    await press(page.getByRole('button', { name: 'Undo', exact: true }), phone);
    await pollPicture(api, pics.projectId, pic.id, (p) => layerOf(p, layer.id).visible === false, 'undo hid the result layer');
  });
}

test('PE-145 PE-028 "Cut out a subject": marks and words go to the service, the result is a selected cut-out layer, the message can be dismissed', async ({ page, api, pics }, testInfo) => {
  runOn('desktop', 'pixel7');
  const phone = isPhone(testInfo.project.name);
  await stubCapabilities(page);
  const { pic } = await openFresh(page, api, pics, `pe145-${testInfo.project.name}`);
  const layer = await addBadgeLayer(api, pics.projectId, pic.id, pics);
  const bodies: Array<Record<string, unknown>> = [];
  await page.route(/\/image-documents\/[^/]+\/subjects$/, async (route) => {
    bodies.push(route.request().postDataJSON() as Record<string, unknown>);
    return route.fulfill({ json: [layer] });
  });
  await page.reload();
  await pickAiTool(page, 'Cut out a subject');
  const panel = page.getByTestId('ai-tool-subject');
  const cutOut = panel.getByRole('button', { name: 'Cut out', exact: true });
  await expect(cutOut, 'nothing to send yet').toBeDisabled();
  await expect(panel.getByText('Click the picture, drag a box, or type a name first.')).toBeVisible();
  await panel.getByLabel('Or describe it').fill('the red square');
  await stageInView(page);
  const box = (await stageOf(page).boundingBox())!;
  const at = { x: box.x + box.width * 0.3, y: box.y + box.height * 0.4 };
  if (phone) await page.touchscreen.tap(at.x, at.y);
  else await page.mouse.click(at.x, at.y);
  await expect(panel.getByText('Include 1', { exact: true })).toBeVisible();
  await expect(cutOut).toBeEnabled();
  await press(cutOut, phone);
  await expect(page.getByText('Added one cut-out layer.')).toBeVisible({ timeout: 30_000 });
  expect(bodies).toHaveLength(1);
  expect(bodies[0].prompt).toBe('the red square');
  expect(Array.isArray(bodies[0].pointPrompts) && (bodies[0].pointPrompts as unknown[]).length, 'the click went along as a point').toBe(1);
  expect(bodies[0].layerId, 'cut from the original').toBe(pic.layers[0].id);
  await expect(panel.getByText('Include 1')).toHaveCount(0);

  // PE-028: the message alert closes.
  const alert = page.getByRole('alert').filter({ hasText: 'Added one cut-out layer.' });
  await press(alert.getByRole('button').first(), phone);
  await expect(alert).toHaveCount(0);
  await openTab(page, 'Layers');
  await press(page.getByRole('button', { name: 'Undo', exact: true }), phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => layerOf(p, layer.id).visible === false, 'one undo hid the cut-out');
});

test('PE-160 a reference picture can be uploaded from the device, becomes a project file and is offered as the 1st reference', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  await stubCapabilities(page);
  await openFresh(page, api, pics, `pe160-${testInfo.project.name}`);
  await pickAiTool(page, 'Edit with words');
  const panel = page.getByTestId('ai-tool-instruct');
  const before = (await api.listFiles(pics.projectId)).length;
  const file = path.join('/tmp/editor-remediation/media/picture-lane', 'badge.png');
  await expect(panel.getByRole('button', { name: /Upload a (reference )?picture/ }), 'the upload control is there').toBeVisible();
  // The control opens the device's file picker; its hidden input takes the file directly (WebKit does not raise a filechooser event for it).
  await panel.locator('input[type=file]').setInputFiles(file);
  await expect.poll(async () => (await api.listFiles(pics.projectId)).length, { message: 'the upload became a project file' }).toBe(before + 1);
  await expect(panel.getByText(/1st/)).toBeVisible();
});
