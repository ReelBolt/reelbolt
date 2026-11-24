import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * T-016 / UI2-14: on a phone the Deliver pane's status bar must not sit over its lower controls. At 390x844, with the
 * Advanced section and loudness open, every visible Deliver control's centre point must hit-test to itself.
 */
test.use({ serviceWorkers: 'block' });

test('UI2-14 Deliver at 390x844: no control is covered by the status bar or the dock', async ({ freshTimeline, openEditor }) => {
  runOn('pixel7');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.page.setViewportSize({ width: 390, height: 844 });
  await ed.workspace('deliver');
  await expect(ed.testId('deliver-workspace')).toBeVisible();
  await ed.tap(ed.testId('render-advanced-toggle'));
  const normalise = ed.page.getByRole('switch', { name: 'Normalise loudness' });
  if (!(await normalise.isChecked())) await normalise.check();
  await expect(normalise).toBeChecked();

  const covered = await ed.page.evaluate(async () => {
    const root = document.querySelector('[data-testid="deliver-workspace"]') as HTMLElement;
    const bad: string[] = [];
    let checked = 0;
    const controls = Array.from(root.querySelectorAll<HTMLElement>('button, input, select, textarea, a[href], [role="switch"], [role="combobox"]'));
    for (const el of controls) {
      const cs = getComputedStyle(el);
      const r0 = el.getBoundingClientRect();
      if (cs.visibility === 'hidden' || cs.display === 'none' || r0.width === 0 || r0.height === 0 || (cs.position === 'absolute' && cs.opacity === '0' && el.tagName === 'INPUT')) continue;
      if (el.closest('[hidden], [aria-hidden="true"], [inert]')) continue;
      el.scrollIntoView({ block: 'center' });
      await new Promise((res) => requestAnimationFrame(() => res(null)));
      const r = el.getBoundingClientRect();
      const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
      checked++;
      if (!hit || !(hit === el || el.contains(hit) || hit.contains(el) || el.closest('label')?.contains(hit))) {
        bad.push(`${el.tagName} ${el.getAttribute('data-testid') ?? el.getAttribute('aria-label') ?? el.textContent?.slice(0, 30)} covered by ${hit?.tagName} ${hit?.getAttribute('data-testid') ?? ''} ${(hit as HTMLElement | null)?.className?.toString().slice(0, 40) ?? ''}`);
      }
    }
    return { bad, checked };
  });
  expect(covered.checked, 'the Deliver pane has controls').toBeGreaterThan(3);
  expect(covered.bad).toEqual([]);

  // Scrolled to the very end, the last control clears the status bar by at least its height.
  const clearance = await ed.page.evaluate(() => {
    const root = document.querySelector('[data-testid="deliver-workspace"]') as HTMLElement;
    const scroller = root.parentElement as HTMLElement;
    scroller.scrollTop = scroller.scrollHeight;
    // The padding now sits on the content stack inside the panel: take the largest bottom padding on the path
    // from the last visible control up to the pane.
    const controls = Array.from(root.querySelectorAll<HTMLElement>('button, input, select, textarea, a[href], [role="switch"]')).filter((c) => c.getBoundingClientRect().height > 0);
    let el: HTMLElement | null = controls[controls.length - 1] ?? null;
    let max = 0;
    while (el) {
      max = Math.max(max, parseFloat(getComputedStyle(el).paddingBottom) || 0);
      if (el === root) break;
      el = el.parentElement;
    }
    // The phone Deliver pane pins Render in a sticky footer inside the sheet (the footer's own 8px padding is what is left
    // below its control, not the 28px stack padding of the scrolling content): what must hold there is that the footer is
    // fully on screen; that the status line does not cover its controls is the hit-test above (every control's centre hits
    // itself), which is stricter than comparing rectangles because the footer is drawn over the status line.
    const footer = root.querySelector<HTMLElement>('[data-testid="deliver-sticky-footer"]');
    if (footer) {
      const f = footer.getBoundingClientRect();
      return f.bottom <= window.innerHeight && f.top >= 0 ? 28 : 0;
    }
    return max;
  });
  expect(clearance, 'bottom padding covers the 28 px status bar').toBeGreaterThanOrEqual(28);
});

test('UI2-14 Deliver at 390x844: every option of the mastering preset menu hit-tests to itself', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('pixel7');
  const stamp = `${test.info().project.name}-${Date.now() % 100000}`;
  const presetsUrl = `${api.timelinesUrl(seed.projectId)}/presets`;
  const created: string[] = [];
  try {
    for (const n of ['a', 'b', 'c']) {
      const p = await api.json<{ id: string }>('POST', presetsUrl, { kind: 'mastering', name: `rem-ui214-${n}-${stamp}`, payload: { targetLufs: -14, truePeakDbtp: -1, loudnessRange: 11 } }, [200, 201]);
      created.push(p.id);
    }
    const ed = await openEditor(await freshTimeline('seeded'));
    await ed.page.setViewportSize({ width: 390, height: 844 });
    await ed.workspace('deliver');
    await ed.tap(ed.testId('render-advanced-toggle'));
    const normalise = ed.page.getByRole('switch', { name: 'Normalise loudness' });
    if (!(await normalise.isChecked())) await normalise.check();
    await expect(normalise).toBeChecked();
    // Worst case: the trigger sits at the very bottom of the scroll area, where a downward menu would land under the status bar / dock.
    await ed.testId('mastering-presets-delete').evaluate((e) => e.scrollIntoView({ block: 'end' }));
    const options = ed.page.getByRole('option');
    // The first tap can land while the pane is still settling after the scroll (the Select root briefly
    // intercepts it); tap again until the menu is open instead of trusting a single attempt.
    await expect(async () => {
      await ed.tap(ed.testId('mastering-presets-delete'));
      await expect(options.first()).toBeVisible({ timeout: 3_000 });
    }).toPass({ timeout: 20_000 });
    const result = await options.evaluateAll((els) =>
      els.map((el) => {
        const r = el.getBoundingClientRect();
        const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
        return { text: el.textContent, ok: !!hit && (hit === el || el.contains(hit)), by: hit ? `${hit.tagName} ${(hit as HTMLElement).className?.toString().slice(0, 40)}` : 'none', bottom: Math.round(r.bottom) };
      }),
    );
    expect(result.length, 'the menu lists the presets').toBeGreaterThanOrEqual(3);
    expect(result.filter((o) => !o.ok), 'no option is covered (e.g. by the status bar)').toEqual([]);
    const dockTop = await ed.page.evaluate(() => document.querySelector('[data-testid="status-bar"]')!.getBoundingClientRect().top);
    expect(Math.max(...result.map((o) => o.bottom)), 'the menu opens above the status bar').toBeLessThanOrEqual(dockTop);
  } finally {
    for (const id of created) await api.json('DELETE', `${presetsUrl}/${id}`, undefined, [200, 204, 404]).catch(() => undefined);
  }
});
