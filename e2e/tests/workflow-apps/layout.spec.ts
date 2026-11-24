import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { PROJECT_ID, mockApi, signIn } from './fixtures';

// Desktop layout of the app run page, from a small laptop window (900) to a wide screen (1920): no piece of
// content may cover another, the sections must not overlap, and nothing may scroll sideways. 900 and 1024
// failed before the two-column layout moved to 70em (the result pane was squeezed and ran past the edge). Set LAYOUT_SHOTS=<dir> to save
// full-page screenshots (LAYOUT_TAG names them).
const WID = 'wf-layout';
const now = new Date().toISOString();
const base = `/api/v1/projects/${PROJECT_ID}`;
const json = (body: unknown, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(body) });

const workflow = { id: WID, projectId: PROJECT_ID, name: 'Product pictures', steps: [{ id: 's1', agentDefinitionId: 'a1', stepOrder: 1, label: 'Write the script', stepType: 'Agent', maxIterations: 1, agentInputContextMode: 'FullWorkflow' }], requiresUserInput: false, createdAt: now, updatedAt: now };
const appResponse = {
  workflowId: WID, name: 'Product pictures', hasExplicitManifest: true, costClass: 'Uses credits', source: 'x',
  manifest: {
    version: 2, title: 'Product pictures', tagline: 'Pictures of your product in colours', category: 'promo',
    inputs: [
      { kind: 'files', role: 'image', label: 'Your pictures', min: 1, max: 3 },
      { kind: 'brief', field: 'language', label: 'Language' },
      { kind: 'brief', field: 'format', label: 'Shape' },
      { kind: 'brief', field: 'resolution', label: 'Resolution' },
      { kind: 'input', id: 'colors', type: 'longText', label: 'Colours, one per line', help: 'One colour per line', maxLength: 600 },
      { kind: 'input', id: 'width', type: 'number', label: 'Width', min: 100, max: 4096 },
      { kind: 'input', id: 'model', type: 'model', label: 'Picture model', capability: 'ImageGeneration', more: true },
      { kind: 'input', id: 'sharp', type: 'toggle', label: 'Extra sharp', more: true },
      { kind: 'input', id: 'mood', type: 'choice', label: 'Mood', more: true, options: [{ value: 'a', label: 'Calm' }, { value: 'b', label: 'Bold' }, { value: 'c', label: 'Playful' }] },
      { kind: 'notes', label: 'Anything else?' },
    ],
    result: { kind: 'image' },
  },
};
const estimate = { billingEnabled: true, hasHistory: true, lowCredits: 8, highCredits: 14, typicalCredits: 10, fixedMaxCredits: 0, requiredCredits: 14, balanceCredits: 500, affordable: true, steps: [] };
const execution = (id: string, status: string) => ({
  id, workflowDefinitionId: WID, status, startedAt: now, completedAt: now, iterationCount: 1, resultJson: null, stepResults: [], reviewScores: [],
});
const output = (n: number) => ({
  stepResultId: `sr${n}`, workflowExecutionId: 'ex1', workflowStepId: 's1', storageKey: `o${n}`, fileName: `pic-${n}.png`,
  producedAt: now, workflowDefinitionId: WID, isFinal: true, executionStatus: 'Passed',
});
// A portrait picture (600x900): the shape that stresses a thumbnail box the most.
const PNG = readFileSync(join(__dirname, 'portrait.png'));

async function setup(page: Page, baseURL: string) {
  await signIn(page, baseURL);
  await mockApi(page);
  const re = (tail: string) => new RegExp(`${base}/${tail}(\\?.*)?$`);
  await page.route(re(`workflows/${WID}`), (r) => r.fulfill(json(workflow)));
  await page.route(re(`workflows/${WID}/app`), (r) => r.fulfill(json(appResponse)));
  await page.route(re(`workflows/${WID}/estimate`), (r) => r.fulfill(json(estimate)));
  await page.route(re(`workflows/${WID}/lint`), (r) => r.fulfill(json({ findings: [], errors: 0, warnings: 0, infos: 0 })));
  const first = execution('ex1', 'Passed');
  await page.route(re(`workflows/${WID}/executions`), (r) => r.fulfill(json([first, execution('ex2', 'Failed'), execution('ex3', 'Passed')])));
  await page.route(new RegExp(`${base}/executions/ex1$`), (r) => r.fulfill(json(first)));
  const file = (n: number) => ({
    id: `f${n}`, projectId: PROJECT_ID, originalFileName: `a-rather-long-product-photo-name-${n}.png`, category: 'userFiles', mimeType: 'image/png', sizeBytes: 100000,
    storageKey: `k${n}`, agentSummary: null, summaryStatus: 'Done', indexingStatus: 'Indexed',
  });
  await page.route(re('files'), (r) => r.fulfill(json([1, 2, 3, 4, 5, 6].map(file))));
  await page.route(/\/files\/[^/?]+\/(content|preview|download|thumbnail)/, (r) => r.fulfill({ status: 200, contentType: 'image/png', body: PNG }));
  await page.route(re('outputs'), (r) => r.fulfill(json([1, 2, 3, 4, 5, 6].map(output))));
  await page.route(/\/outputs\/[^/?]+\/(video|preview|download)/, (r) => r.fulfill({ status: 200, contentType: 'image/png', body: PNG }));
}

const SECTIONS = ['#app-run-inputs', '[data-testid="run-bar"]', '[data-testid="run-result"]'];

for (const width of (process.env.WIDTHS ? process.env.WIDTHS.split(",").map(Number) : [900, 1024, 1280, 1440, 1920])) {
  test(`desktop ${width}px: sections do not overlap`, async ({ page, baseURL }, info) => {
    test.skip(info.project.name !== 'desktop', 'desktop project only');
    await page.setViewportSize({ width, height: 900 });
    await setup(page, baseURL!);
    await page.goto(`/app/projects/${PROJECT_ID}/workflows/${WID}`);
    await expect(page.getByTestId('run-bar')).toBeVisible();
    await page.waitForTimeout(800);
    const shots = process.env.LAYOUT_SHOTS;
    if (shots) await page.screenshot({ path: `${shots}/${process.env.LAYOUT_TAG ?? 'shot'}-${width}.png`, fullPage: true });
    const rects = await page.evaluate((sels) => sels.map((s) => {
      const r = document.querySelector(s)?.getBoundingClientRect();
      return r ? { s, x: r.x, y: r.y + scrollY, w: r.width, h: r.height } : null;
    }), SECTIONS);
    const found = rects.filter((r): r is NonNullable<typeof r> => r !== null);
    expect(found.length).toBe(SECTIONS.length);
    for (let i = 0; i < found.length; i++) {
      for (let j = i + 1; j < found.length; j++) {
        const a = found[i], b = found[j];
        const overlapX = Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x);
        const overlapY = Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y);
        expect(overlapX > 1 && overlapY > 1, `${a.s} overlaps ${b.s}`).toBe(false);
      }
    }
    // Every visible piece of content (text, controls, pictures) in the main column: no two may cover each other.
    const clashes = await page.evaluate(() => {
      const root = document.querySelector('main') ?? document.body;
      const nodes = Array.from(root.querySelectorAll<HTMLElement>('*')).filter((el) => {
        const cs = getComputedStyle(el);
        if (cs.visibility === 'hidden' || cs.display === 'none' || Number(cs.opacity) === 0) return false;
        const r = el.getBoundingClientRect();
        if (r.width < 4 || r.height < 4) return false;
        if (el.closest('[aria-hidden="true"], svg')) return false;
        if (/^(IMG|BUTTON|INPUT|SELECT|TEXTAREA|VIDEO)$/.test(el.tagName)) return true;
        return Array.from(el.childNodes).some((n) => n.nodeType === 3 && (n.textContent ?? '').trim().length > 0);
      });
      const out: string[] = [];
      const name = (el: HTMLElement) => `${el.tagName.toLowerCase()}${el.id ? `#${el.id}` : ''}[${(el.textContent ?? el.getAttribute('alt') ?? '').trim().slice(0, 24)}]`;
      for (let i = 0; i < nodes.length; i++) {
        for (let j = i + 1; j < nodes.length; j++) {
          const a = nodes[i], b = nodes[j];
          if (a.contains(b) || b.contains(a)) continue;
          const ra = a.getBoundingClientRect(), rb = b.getBoundingClientRect();
          const ox = Math.min(ra.right, rb.right) - Math.max(ra.left, rb.left);
          const oy = Math.min(ra.bottom, rb.bottom) - Math.max(ra.top, rb.top);
          if (ox > 3 && oy > 3) out.push(`${name(a)} x ${name(b)} (${Math.round(ox)}x${Math.round(oy)})`);
        }
      }
      return out.slice(0, 10);
    });
    expect(clashes, 'content that covers other content').toEqual([]);
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow).toBeLessThanOrEqual(0);
  });
}
