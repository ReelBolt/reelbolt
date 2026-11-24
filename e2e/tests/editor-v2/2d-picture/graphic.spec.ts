import type { Page } from '@playwright/test';
import {
  test, expect, openFresh, isPhone, press, addBadgeLayer, layerOf, openTab, pollPicture, getFull, type PictureSeed, type FullLayer,
} from '../../../fixtures/editor-v2/picture-lane';
import { pickAiTool, picturesUrl } from '../../../fixtures/editor-v2/picture';
import { runOn } from '../../../fixtures/editor-v2/rows';
import type { EditorApi } from '../../../fixtures/editor-v2/api';

/**
 * Lane D, picture editor: "Design a graphic" (inventory rows PE-155, PE-088). The designer is an AI agent run by the workflow engine, which needs
 * a chat AI provider the isolated stack does not have (and never gets: no paid calls). So: (1) with no provider the panel says so up front and
 * offers no "Design it" button; (2) the job list and the POST are stubbed at the browser boundary to prove the editor's own half: queue,
 * follow, adopt the finished layer (selected, one undo step, message), and "Change this graphic" for a designed layer.
 */

// The app registers a service worker; Playwright WebKit cannot route requests it handles, so route stubs need it blocked (as 09-analysis/transcript does).
test.use({ serviceWorkers: 'block' });

const jobShape = (documentId: string, over: Record<string, unknown>) => ({
  id: '11111111-1111-4111-8111-111111111111',
  documentId,
  executionId: '22222222-2222-4222-8222-222222222222',
  workflowId: '33333333-3333-4333-8333-333333333333',
  status: 'Queued',
  description: 'A big yellow title',
  frameWidth: 1920,
  frameHeight: 1080,
  placement: 'center',
  target: { x: 288, y: 270, width: 1344, height: 540 },
  revisesLayerId: null,
  layerId: null,
  layer: null,
  summary: null,
  error: null,
  createdAt: new Date().toISOString(),
  completedAt: null,
  ...over,
});

test('PE-155 with no chat AI provider the Design a graphic panel says so up front and offers no button that could only fail', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe155-${testInfo.project.name}`);
  const list = await api.json<{ available: boolean; unavailableReason: string | null }>('GET', `${picturesUrl(pics.projectId, pic.id)}/graphics`);
  await pickAiTool(page, 'Design a graphic');
  const panel = page.getByTestId('design-graphic-panel');
  await expect(panel).toBeVisible();
  const design = panel.getByRole('button', { name: 'Design it', exact: true });
  if (list.available) {
    // A deployment that does have a chat provider: the button is offered (blocked until a description is typed).
    await expect(design).toBeVisible();
    await expect(design).toBeDisabled();
    return;
  }
  await expect(panel.getByText('Not available here')).toBeVisible();
  await expect(panel.getByText(list.unavailableReason!, { exact: false }), 'with the server sentence').toBeVisible();
  await expect(panel.getByText(/chat AI provider/i), 'and the reason is the missing provider, in plain words').toBeVisible();
  await expect(design, 'no Design it button').toHaveCount(0);
  await expect(panel.getByLabel('Describe the graphic')).toBeDisabled();
  void phone;
  // The server refuses a request too, before queueing anything.
  const refused = await api.request.post(`${picturesUrl(pics.projectId, pic.id)}/graphics`, { data: { description: 'A big yellow title' }, failOnStatusCode: false });
  expect(refused.status()).toBe(503);
  expect(((await refused.json()) as { message: string }).message).toMatch(/chat AI provider/i);
  const after = await api.json<{ graphics: unknown[] }>('GET', `${picturesUrl(pics.projectId, pic.id)}/graphics`);
  expect(after.graphics, 'nothing was queued').toEqual([]);
});

async function stubGraphics(page: Page, api: EditorApi, pics: PictureSeed, docId: string) {
  // The server settles a finished job into the document when the list is read; the stub does the same: the layer
  // is made (through the REST API) at the moment the job first reads as Passed, so the editor does not have it yet.
  const state = { phase: 'idle' as 'idle' | 'queued' | 'running' | 'passed', posts: [] as Array<Record<string, unknown>>, layer: null as FullLayer | null };
  const urlRe = new RegExp(`/image-documents/${docId}/graphics$`);
  await page.route(urlRe, async (route) => {
    const method = route.request().method();
    if (method === 'POST') {
      state.posts.push(route.request().postDataJSON() as Record<string, unknown>);
      state.phase = 'queued';
      return route.fulfill({ status: 201, json: jobShape(docId, { status: 'Queued', description: String(state.posts.at(-1)!.description) }) });
    }
    let graphics: unknown[] = [];
    if (state.phase === 'queued') { state.phase = 'running'; graphics = [jobShape(docId, { status: 'Queued' })]; }
    else if (state.phase === 'running') { state.phase = 'passed'; graphics = [jobShape(docId, { status: 'Running' })]; }
    else if (state.phase === 'passed') {
      state.layer ??= await addBadgeLayer(api, pics.projectId, docId, pics);
      graphics = [jobShape(docId, { status: 'Passed', layerId: state.layer.id, layer: { ...state.layer, kind: 'Generated' }, summary: 'A bold yellow title.', completedAt: new Date().toISOString() })];
    }
    return route.fulfill({ json: { graphics, estimate: null, available: true, unavailableReason: null } });
  });
  return state;
}

test('PE-155 with the designer stubbed: Design it queues the request, the finished graphic arrives as one selected layer and one undo step', async ({ page, api, pics }, testInfo) => {
  runOn('desktop', 'pixel7');
  test.setTimeout(150_000);
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe155s-${testInfo.project.name}`);
  const state = await stubGraphics(page, api, pics, pic.id);
  await page.reload();
  await expect(page.getByRole('button', { name: 'Undo', exact: true })).toBeDisabled();
  await pickAiTool(page, 'Design a graphic');
  const panel = page.getByTestId('design-graphic-panel');
  const design = panel.getByRole('button', { name: 'Design it', exact: true });
  await expect(design).toBeDisabled();
  await panel.getByLabel('Describe the graphic').fill('A big yellow title saying HELLO');
  await expect(design).toBeEnabled();
  await press(design, phone);
  await expect(panel.getByText(/Your graphic is on its way/)).toBeVisible();
  expect(state.posts).toHaveLength(1);
  expect(state.posts[0]).toMatchObject({ description: 'A big yellow title saying HELLO', placement: 'center' });

  await expect(page.getByText(/Your graphic is ready\. A bold yellow title\./), 'the finished graphic is announced').toBeVisible({ timeout: 60_000 });
  const layer = state.layer!;
  await openTab(page, 'Layers');
  await expect(page.getByRole('list', { name: 'Layers, front first' }).getByRole('button', { name: `Select ${layer.label}` })).toHaveAttribute('aria-current', 'true');
  await press(page.getByRole('button', { name: 'Undo', exact: true }), phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => layerOf(p, layer.id).visible === false, 'one undo step hid the designed layer');
  void getFull;
});

test('PE-088 a designed layer offers "Change this graphic", which opens the Design panel in change mode', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe088-${testInfo.project.name}`);
  const layer = await addBadgeLayer(api, pics.projectId, pic.id, pics);
  // Make the stored layer look designed: the document read carries the design provenance (the engine writes this for a real run).
  const docUrl = new RegExp(`/api/v1/projects/${pics.projectId}/image-documents/${pic.id}$`);
  await page.route(docUrl, async (route) => {
    if (route.request().method() !== 'GET') return route.continue();
    const res = await route.fetch();
    const body = (await res.json()) as { layers: Array<FullLayer & { provenance?: unknown }> };
    body.layers = body.layers.map((l) => (l.id === layer.id
      ? { ...l, kind: 'Generated', provenance: { operation: 'designGraphic', prompt: 'A yellow title', workspaceVersionId: '44444444-4444-4444-8444-444444444444', graphicId: '11111111-1111-4111-8111-111111111111' } }
      : l));
    return route.fulfill({ response: res, json: body });
  });
  await page.reload();
  const plain = page.getByRole('button', { name: 'Change this graphic', exact: true });
  const list = page.getByRole('list', { name: 'Layers, front first' });
  await press(list.getByRole('button', { name: `Select ${pic.layers[0].label}` }), phone);
  await expect(plain, 'a photo layer is not a design').toHaveCount(0);
  await press(list.getByRole('button', { name: `Select ${layer.label}` }), phone);
  await press(plain, phone);
  const panel = page.getByTestId('design-graphic-panel');
  await expect(panel.getByTestId('design-graphic-revise')).toBeVisible();
  await expect(panel.getByText('It was designed from: “A yellow title”')).toBeVisible();
  await press(panel.getByRole('button', { name: 'Design a new graphic instead' }), phone);
  await expect(panel.getByLabel('Describe the graphic')).toBeVisible();
  await expect(panel.getByRole('button', { name: 'Change the selected graphic instead' })).toBeVisible();
});
