import type { Locator, Page } from '@playwright/test';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import type { Item, Timeline } from '../../../fixtures/editor-v2/api';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { knownFailure } from '../expected-failures';

/**
 * 11 basics: refusals in plain words (remediation-plan.md W0.6 "Inspector refusals in plain words, field reverts
 * to the stored value; GUIDs replaced by names in all refusal toasts"; report.md §5 d1 N6/N8/N9, d3 N1/N3;
 * T-031 item 8). Each row checks what the person sees (the toast, the field) AND what the server keeps.
 */

const GUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;
/** Developer text a refusal must never show: op names, raw JSON, "[object Object]", ids. */
const DEVELOPER_TEXT = /\b(setProperty|setTrackProperty|setMarker|nudge|move|trim)\b:|\{|\}|\[object Object\]|found /;

function refusalToast(page: Page): Locator {
  return page.getByRole('alert').filter({ hasText: /Edit refused/ }).last();
}

/** The refusal toast is shown, in plain words: no GUID, no op name, no raw JSON. Returns its text. */
async function expectPlainRefusal(page: Page, what: string, wording?: RegExp): Promise<string> {
  const toast = refusalToast(page);
  await expect(toast, `${what}: an "Edit refused" toast is shown`).toBeVisible({ timeout: 10_000 });
  const text = (await toast.innerText()).replace(/\s+/g, ' ');
  expect(text, `${what}: the toast names no id`).not.toMatch(GUID);
  expect(text, `${what}: the toast has no op name, raw JSON or [object Object]`).not.toMatch(DEVELOPER_TEXT);
  if (wording) expect(text, `${what}: the toast says why`).toMatch(wording);
  return text;
}

const firstVideoItem = (tl: Timeline): Item => tl.document.tracks.find((t) => t.kind === 'video')!.items[0];

async function selectFirstClip(ed: EditorPage): Promise<void> {
  await ed.selectClipAt(30);
  await ed.openInspector();
  await expect(ed.testId('inspector-name'), 'the Inspector shows the selected clip').toHaveValue('clipA.mp4');
}

/** d1 N6 (desktop d1, phone m1-N6): clearing a clip's name is refused with a toast and the old name comes back. */
test('d1 N6 clearing a clip name is refused with a toast and the field shows the old name', async ({ freshTimeline, openEditor }) => {
  runOn('desktop', 'pixel7');
  knownFailure('d1 N6');
  const ed = await openEditor(await freshTimeline('seeded'));
  await selectFirstClip(ed);
  const name = ed.testId('inspector-name');
  await name.fill('');
  await name.blur();
  await expectPlainRefusal(ed.page, 'clearing the clip name', /needs a name/i);
  await expect(name, 'the field shows the stored name again').toHaveValue('clipA.mp4');
  await ed.page.waitForTimeout(1500);
  expect(firstVideoItem(await ed.serverTimeline()).name, 'the server keeps the name').toBe('clipA.mp4');
});

/** d1 N6 (desktop): the same for a marker row's name in the Markers panel. */
test('d1 N6 clearing a marker name is refused with a toast and the row shows the old name', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('d1 N6');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.seek(45);
  await ed.openPanelTab(/markers/i);
  await ed.testId('marker-name-input').fill('Hook');
  await ed.tap(ed.testId('marker-add'));
  await ed.expectServer((tl) => (tl.document.markers ?? []).some((m) => m.name === 'Hook'), 'the marker "Hook" is stored');
  const rename = ed.page.getByRole('textbox', { name: 'Rename Hook' });
  await rename.fill('');
  await rename.blur();
  await expectPlainRefusal(ed.page, 'clearing the marker name', /needs a name/i);
  await expect(rename, 'the row shows the stored name again').toHaveValue('Hook');
  await ed.page.waitForTimeout(1500);
  expect((await ed.serverTimeline()).document.markers?.map((m) => m.name), 'the server keeps the marker name').toContain('Hook');
});

/** d1 N8 (desktop): editing a clip on a locked track names the track in the toast, never its id. */
test('d1 N8 a refused edit on a locked track names the track, not its id', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  knownFailure('d1 N8');
  const id = await freshTimeline('seeded');
  await api.putDocument(seed.projectId, id, (doc) => {
    const v = doc.tracks.find((t) => t.kind === 'video')!;
    v.name = 'Interviews';
    v.locked = true;
  });
  const ed = await openEditor(id);
  const before = await ed.serverTimeline();
  await ed.selectClipAt(30);
  await ed.page.keyboard.press('Period');
  const text = await expectPlainRefusal(ed.page, 'nudging a clip on a locked track', /locked/i);
  expect(text, 'the toast names the track').toContain('Interviews');
  await ed.page.waitForTimeout(1500);
  expect(firstVideoItem(await ed.serverTimeline()).recordIn, 'the locked clip did not move').toBe(firstVideoItem(before).recordIn);
});

/** d1 N9 (desktop): garbage in the timecode field is refused inline and the field shows the playhead again. */
test('d1 N9 garbage typed in the timecode field is refused and the field reverts to the playhead', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('d1 N9');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.seek(300);
  const field = ed.testId('timecode-field');
  await field.click();
  await field.fill('99:99:zz');
  await field.press('Enter');
  await expect(ed.page.getByText('Not a timecode'), 'the field says the text is not a timecode').toBeVisible();
  await expect(ed.testId('status-timecode'), 'the playhead did not move').toHaveText('00:00:10:00');
  await field.blur();
  await expect(field, 'on leave the field shows the playhead timecode, not the garbage').toHaveValue('00:00:10:00');
});

/**
 * d3 N1 (desktop d3, phone m2-N2): an out-of-range Inspector value (Opacity 2) is refused in plain words
 * that name the range, and once the field is left it shows the stored value again.
 */
test('d3 N1 an out-of-range Inspector value is refused in plain words and the field reverts', async ({ freshTimeline, openEditor }) => {
  runOn('desktop', 'pixel7');
  knownFailure('d3 N1');
  const ed = await openEditor(await freshTimeline('seeded'));
  await selectFirstClip(ed);
  const opacity = ed.testId('inspector-opacity').locator('input').or(ed.page.locator('input[data-testid="inspector-opacity"]')).first();
  await opacity.fill('200'); // percent
  await opacity.press('Enter');
  await expectPlainRefusal(ed.page, 'Opacity 200%', /opacity.*between 0% and 100%/i);
  await opacity.blur();
  await expect(opacity, 'the field shows the stored opacity again').toHaveValue('100');
  await ed.page.waitForTimeout(1500);
  const transform = firstVideoItem(await ed.serverTimeline()).transform as { opacity?: { value?: number } } | null | undefined;
  expect(transform?.opacity?.value ?? 1, 'the server keeps opacity 1').toBe(1);
});

/** d3 N3 (desktop): a refused mixer EQ value says so in words: no "[object Object]", op name or id. */
test('d3 N3 a refused mixer EQ value is explained without [object Object], op names or ids', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('d3 N3');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.workspace('audio');
  await ed.openAdvancedAudio(); // the EQ number fields live under Advanced audio
  const audio = (await ed.serverTimeline()).document.tracks.find((t) => t.kind === 'audio')!;
  const lowCut = ed.page.locator(`input[data-testid="mixer-lowcut-${audio.id}"]`).or(ed.testId(`mixer-lowcut-${audio.id}`).locator('input')).first();
  await lowCut.fill('5');
  await lowCut.press('Enter');
  await lowCut.blur();
  await expectPlainRefusal(ed.page, 'EQ low cut 5 Hz');
  await ed.page.waitForTimeout(1500);
  const eq = (await ed.serverTimeline()).document.tracks.find((t) => t.id === audio.id)!.eq as { lowCutHz?: number | null } | undefined;
  expect(eq?.lowCutHz ?? null, 'the server keeps no 5 Hz low cut').not.toBe(5);
});
