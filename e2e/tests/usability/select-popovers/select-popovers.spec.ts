import { test, expect, type Page, type Locator } from '@playwright/test';
import { loginAs } from '../../../fixtures/stack';

/**
 * T-006: every Select / Combobox / Menu in the workflow step config sheet must open, show its dropdown inside the
 * viewport, take a pick, and leave the page and the sheet usable. The comboboxes are enumerated from the DOM of the
 * open sheet for each step type (code inventory: web/components/workflows/*StepConfig*.tsx, config-kit.tsx,
 * StepConfigSheet.tsx, AgentPicker.tsx, StepTypeSelector.tsx).
 */
const STEP_TYPES: Array<[string, string]> = [
  ['Agent', 'Agent'], ['Conditional', 'Conditional Branch'], ['ForEach', 'For Each'], ['ReviewLoop', 'Review Loop'],
  ['Parallel', 'Parallel'], ['Extract', 'Extract'], ['VideoAnalyze', 'Analyze Video'], ['VideoCompile', 'Compile Video'],
  ['VideoGenerate', 'Generate Video'], ['Voiceover', 'Voiceover'], ['EditRoom', 'Edit Room'], ['GraphicsRoom', 'Graphics Room'],
  ['ColorGradeRoom', 'Color Grade Room'], ['ThumbnailGenerate', 'Make Thumbnail'], ['Revise', 'Changes'],
  ['DesignGraphic', 'Design a graphic'], ['DailiesRoom', 'Dailies Room'], ['TimelineRender', 'Render Timeline'],
  ['DesignSystem', 'Brand & product profile'], ['ExtractFrame', 'Grab a frame'], ['AudioGenerate', 'Generate music or sound'],
  ['PinInsert', 'Put on screens'],
];

async function inViewport(page: Page, loc: Locator, what: string) {
  const box = await loc.boundingBox();
  expect(box, `${what} has a box`).not.toBeNull();
  const vp = page.viewportSize()!;
  expect(box!.x, `${what} left`).toBeGreaterThanOrEqual(-1);
  expect(box!.y, `${what} top`).toBeGreaterThanOrEqual(-1);
  expect(box!.x + box!.width, `${what} right`).toBeLessThanOrEqual(vp.width + 1);
  expect(box!.y + box!.height, `${what} bottom`).toBeLessThanOrEqual(vp.height + 1);
}

async function sheetUsable(page: Page, sheet: Locator) {
  // Layout intact: the document is not shifted sideways and the Done button is the topmost element at its centre.
  const state = await page.evaluate(() => ({
    sx: window.scrollX, bodyW: document.body.scrollWidth, vw: window.innerWidth,
  }));
  expect(state.sx).toBe(0);
  expect(state.bodyW).toBeLessThanOrEqual(state.vw + 1);
  const done = sheet.getByRole('button', { name: 'Done' });
  await expect(done).toBeVisible();
  const box = (await done.boundingBox())!;
  const top = await page.evaluate(([x, y]) => {
    const el = document.elementFromPoint(x, y);
    return el?.closest('button')?.textContent ?? el?.tagName ?? null;
  }, [box.x + box.width / 2, box.y + box.height / 2] as const);
  const dbg = await page.evaluate(() => {
    const d = document.querySelector('[role=dialog]') as HTMLElement | null;
    const pr = (e: Element | null) => (e ? JSON.stringify(e.getBoundingClientRect().toJSON()) : 'null');
    const chain: string[] = [];
    for (let e = d as HTMLElement | null; e && chain.length < 6; e = e.parentElement) chain.push(`${e.tagName}.${(e.className + '').slice(0, 30)} st=${e.scrollTop} sl=${e.scrollLeft} ${pr(e)}`);
    const doneEl = Array.from(document.querySelectorAll('[role=dialog] button')).find((b) => b.textContent === 'Done') as HTMLElement | undefined;
    const up: string[] = [];
    for (let e = doneEl ?? null; e && e !== d?.parentElement && up.length < 9; e = e.parentElement) { const cs = getComputedStyle(e); up.push(`${e.tagName}.${(e.className + '').slice(0, 24)} h=${Math.round(e.getBoundingClientRect().height)} sh=${e.scrollHeight} ch=${e.clientHeight} ov=${cs.overflowY} fl=${cs.flex} d=${cs.display}`); }
    return { up, sy: window.scrollY, vh: innerHeight, vv: [visualViewport?.height, visualViewport?.offsetTop], dialog: pr(d), chain };
  });
  expect(top, `Done not topmost at ${JSON.stringify(box)} ${JSON.stringify(dbg)}`).toBe('Done');
  // The sheet body still scrolls: find the scrollable ancestor in the sheet and move it.
  const scrolled = await sheet.evaluate((root) => {
    const els = Array.from(root.querySelectorAll<HTMLElement>('*')).filter((e) => e.scrollHeight > e.clientHeight + 4 && /auto|scroll/.test(getComputedStyle(e).overflowY));
    if (!els.length) return 'none-scrollable';
    const e = els[0];
    const before = e.scrollTop;
    e.scrollTop = before === 0 ? 40 : 0;
    return e.scrollTop !== before ? 'moved' : 'stuck';
  });
  expect(scrolled).not.toBe('stuck');
}

test.describe('select popovers in the step config sheet', () => {
  test.setTimeout(600_000);
  test.use({ actionTimeout: 10_000 });

  test('every combobox of every step type opens, picks and leaves the page usable', async ({ page, context }, testInfo) => {
    const errors: string[] = [];
    page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`));
    page.on('console', (m) => {
      if (m.type() === 'error' && !/Failed to load resource|favicon|net::ERR/.test(m.text())) errors.push(`console: ${m.text()}`);
    });

    await loginAs(context, 'admin');
    const req = context.request;
    const base = new URL(testInfo.project.use.baseURL ?? process.env.BASE_URL!).origin;
    const pr = await req.post(`${base}/api/v1/projects`, { data: { name: `select-popovers ${Date.now()}`, description: 'e2e' } });
    if (!pr.ok()) throw new Error(`project create ${pr.status()}: ${(await pr.text()).slice(0, 600)}`);
    const project = await pr.json();
    const agents = (await (await req.get(`${base}/api/v1/agents`)).json()) as Array<{ id: string; agentType: string }>;
    const agentId = (agents.find((a) => a.agentType === 'DirectorAgent') ?? agents[0]).id;

    // One Agent workflow; every other step type is reached through the Step Type select (which also seeds each type's
    // default config, since the API refuses types without a config JSON).
    const wfRes = await req.post(`${base}/api/v1/projects/${project.id}/workflows`, {
      data: { name: 'sp', steps: [{ stepType: 'Agent', label: 'S', stepOrder: 0, agentDefinitionId: agentId }] },
    });
    expect(wfRes.ok()).toBeTruthy();
    const wfId = (await wfRes.json()).id as string;
    const created: Array<[string, string, string]> = STEP_TYPES.map(([t, l]) => [t, l, wfId]);

    const report: string[] = [];
    test.info().attachments; // report printed below even on failure
    const flush = () => console.log(report.join('\n'));
    page.on('close', flush);
    for (const [type, label, wfId] of created) {
      const trig = 'input[aria-haspopup="listbox"]:not([disabled]), button[aria-haspopup="menu"]:not([disabled])';
      const scan = async (scope: Locator, where: string, sheet: Locator | null) => {
        const n = await scope.locator(trig).filter({ visible: true }).count();
        const diag = await scope.evaluate((r) => `combobox=${r.querySelectorAll('[role=combobox]').length} select=${r.querySelectorAll('select').length} haspopup=${r.querySelectorAll('[aria-haspopup]').length} inputs=${r.querySelectorAll('input').length}`);
        report.push(`${type} ${where}: ${n} popover trigger(s) (${diag})`);
        for (let i = 0; i < n; i++) {
          const t = scope.locator(trig).filter({ visible: true }).nth(i);
          const name = `${type} ${where} #${i}`;
          // A pick can legitimately remove a dependent select (conditional fields); stop when the list shrank.
          if (i >= (await scope.locator(trig).filter({ visible: true }).count())) { report.push(`${name}: gone after an earlier pick`); break; }
          await t.scrollIntoViewIfNeeded();
          const isMenu = (await t.getAttribute('aria-haspopup')) === 'menu';
          const before = isMenu ? '' : await t.inputValue();
          await t.tap().catch(() => t.click());
          // The open listbox is the one the trigger controls (closed dropdowns of other selects stay mounted).
          await expect(t, `${name}: expanded`).toHaveAttribute('aria-expanded', 'true', { timeout: 5000 });
          const controls = await t.getAttribute('aria-controls');
          const dd = controls ? page.locator(`[id="${controls}"]`) : page.locator(isMenu ? '[role="menu"]' : '[role="listbox"]').last();
          await expect(dd, `${name}: dropdown visible`).toBeVisible({ timeout: 5000 });
          await inViewport(page, dd, `${name} dropdown`);
          if (isMenu) {
            await page.keyboard.press('Escape');
          } else {
            const opts = dd.getByRole('option');
            const count = await opts.count();
            if (count > 0) {
              let target = opts.first();
              // Card-level selects (Step Type) re-render the card on a change, so re-pick the selected option there.
              for (let k = 0; k < count; k++) {
                const sel = (await opts.nth(k).getAttribute('aria-selected')) === 'true';
                if (sheet ? !sel : sel) { target = opts.nth(k); break; }
              }
              await target.tap().catch(() => target.click());
              // An action picker ("Add agent to group…") clears itself after a pick; every other select must show the new value.
              if (sheet && count > 1 && !/^Add\b/.test((await t.getAttribute('placeholder')) ?? '')) await expect(t, `${name}: value changed`).not.toHaveValue(before);
            } else {
              await page.keyboard.press('Escape');
            }
          }
          await expect(dd, `${name}: dropdown closes`).toBeHidden({ timeout: 5000 });
          if (sheet) await sheetUsable(page, sheet);
          else {
            const vw = await page.evaluate(() => [window.scrollX, document.body.scrollWidth, window.innerWidth]);
            expect(vw[0]).toBe(0);
            expect(vw[1]).toBeLessThanOrEqual(vw[2] + 1);
            await page.evaluate(() => window.scrollBy(0, 1));
          }
        }
      };

      await page.goto(`/app/projects/${project.id}/workflows/${wfId}?view=advanced`);
      await expect(page.getByRole('textbox', { name: 'Step label' }).first().or(page.getByPlaceholder('Step label').first())).toBeVisible({ timeout: 30_000 });
      // Card-level selects (Step Type, Agent) live on the page, outside the sheet.
      await scan(page.locator('main'), 'card', null);
      if (type !== 'Agent') {
        const stepType = page.locator('main').getByRole('combobox', { name: 'Step Type' }).first();
        await stepType.tap().catch(() => stepType.click());
        const pick = page.getByRole('option', { name: label, exact: true }).first();
        await page.waitForTimeout(400);
        if (!(await pick.count())) { report.push(`${type}: not offered by the Step Type select`); await page.keyboard.press('Escape'); continue; }
        await pick.tap().catch(() => pick.click());
      }
      const configure = page.locator('button', { hasText: `Configure ${label}` }).first();
      if (!(await configure.count())) { report.push(`${type}: no config sheet`); continue; }
      await configure.scrollIntoViewIfNeeded();
      await configure.click();
      const sheet = page.getByRole('dialog').first();
      await expect(sheet.getByRole('button', { name: 'Done' })).toBeVisible();
      await page.waitForTimeout(500);
      await scan(sheet, 'sheet', sheet);
      await sheet.getByRole('button', { name: 'Done' }).click();
      await expect(page.getByRole('dialog')).toHaveCount(0, { timeout: 5000 });
      // Closing the sheet must release the scroll lock: the page scrolls again and nothing is left inert.
      await page.waitForTimeout(400);
      const after = await page.evaluate(() => {
        window.scrollTo(0, 0); window.scrollBy(0, 30);
        return { moved: window.scrollY, locked: document.body.hasAttribute('data-scroll-locked') || getComputedStyle(document.body).overflow === 'hidden', inert: document.querySelectorAll('[inert],[aria-hidden="true"][data-hidden]').length };
      });
      expect(after.locked, `${type}: body still scroll-locked after closing the sheet`).toBe(false);
      expect(after.moved, `${type}: page scrolls after closing`).toBeGreaterThan(0);
    }
    console.log(report.join('\n'));
    expect(errors, 'no console/page errors').toEqual([]);
  });
});
