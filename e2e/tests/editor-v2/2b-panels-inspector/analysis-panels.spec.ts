import { test, expect } from '../../../fixtures/editor-v2/lane-b-test';
import { type EditorPage } from '../../../fixtures/editor-v2';
import type { Item, Timeline } from '../../../fixtures/editor-v2/api';
import { extendAnalysis, TRANSCRIPT, desktopLayoutOnly } from '../../../fixtures/editor-v2/rows';
import { addVideoTrack, mediaClipOnNewTrack, uuid } from '../../../fixtures/editor-v2/lane-b';

/**
 * Lane B: the analysis-gated panels (S11) - Transcript, Subtitles, Takes, Remove silence, Cut > boring shots
 * (inventory timeline.md 2.I/2.K/2.L, TL-165..188, TL-195/196, TL-335/336, TL-460..466).
 *
 * Proof method: the `derush` fixture timeline is made from a real VideoAnalyze+VideoCompile run (no LLM);
 * what that free analysis cannot produce (a transcript, duplicate groups) is given to the VideoAnalyze
 * artifact only by `extendAnalysis` (route stub, no paid call). On a timeline that was NOT made from a run
 * every one of these panels must say so and point to the next step (the actionable empty state).
 */

// WebKit sends API requests through the dashboard's service worker, where page.route never sees them.
test.use({ serviceWorkers: 'block' });

const ARTIFACT = { segments: TRANSCRIPT.segments, words: TRANSCRIPT.words };
const captions = (tl: Timeline): Item[] => tl.document.tracks.flatMap((t) => t.items).filter((i) => i.type === 'caption').sort((a, b) => a.recordIn - b.recordIn);

async function derush(page: import('@playwright/test').Page, freshTimeline: (f: 'derush') => Promise<string>, openEditor: (id: string) => Promise<EditorPage>): Promise<EditorPage> {
  await extendAnalysis(page, ARTIFACT);
  return openEditor(await freshTimeline('derush'));
}

test('S11 on a timeline that was not made from an analysed run every analysis panel says so and offers the next step', async ({ freshTimeline, openEditor, seed }) => {
  const ed = await openEditor(await freshTimeline('blank'));
  const workflows = new RegExp(`/projects/${seed.projectId}/workflows$`);

  await ed.openPanelTab('tab-transcript');
  await expect(ed.testId('transcript-panel')).toContainText(/No transcript to read/i);
  await expect(ed.testId('transcript-fillers-toggle'), 'nothing to find fillers in').toBeDisabled();
  await expect(ed.testId('analysis-open-workflows')).toHaveAttribute('href', workflows);

  await ed.openPanelTab('tab-subtitles');
  await expect(ed.testId('subtitles-panel')).toContainText(/No transcript to generate from/i);
  await expect(ed.testId('subtitles-generate'), 'no cues, so Generate is off').toBeDisabled();
  await expect(ed.page.getByTestId('analysis-open-workflows').first()).toHaveAttribute('href', workflows);

  // Takes and the Cut workspace are not on a phone (PHONE_HIDDEN_TOOL_TABS; docs/editor.md "Not on a phone").
  if (!ed.isPhone) {
    await ed.openPanelTab('tab-takes');
    await expect(ed.page.getByText(/not made from|Seed the timeline from an analysed run/i).first()).toBeVisible();
    await expect(ed.page.getByTestId('analysis-open-workflows').first()).toBeVisible();

    await ed.workspace('cut');
    await expect(ed.testId('cut-boring-no-analysis')).toContainText(/not made from an analysed run/i);
    await expect(ed.testId('cut-boring-no-analysis').getByTestId('analysis-open-workflows')).toBeVisible();
    await ed.workspace('edit');
  }

  await ed.openRemoveSilence();
  const dialog = ed.page.getByRole('dialog');
  await expect(dialog).toContainText(/No analysis to read from/i);
  await expect(dialog.getByTestId('analysis-open-workflows')).toBeVisible();
  await ed.tap(ed.testId('remove-silence-cancel'));
  await expect(dialog).toHaveCount(0);
});

test('S11 the "Open this project\'s workflows" link goes to the project\'s workflows page', async ({ freshTimeline, openEditor, seed }) => {
  const ed = await openEditor(await freshTimeline('blank'));
  await ed.openPanelTab('tab-transcript');
  await ed.tap(ed.testId('analysis-open-workflows'));
  await expect(ed.page).toHaveURL(new RegExp(`/app/projects/${seed.projectId}/workflows`));
});

test('TL-465 TL-466 Remove silence: Cancel closes the dialog without a write', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('derush'));
  await ed.openRemoveSilence();
  await expect(ed.page.getByRole('dialog')).toBeVisible();
  await expect(ed.page.locator('[data-testid^="remove-silence-span-"]')).toHaveCount(2);
  const patches = ed.patches.length;
  await ed.tap(ed.testId('remove-silence-cancel'));
  await expect(ed.page.getByRole('dialog')).toHaveCount(0);
  await ed.page.waitForTimeout(1500);
  expect(ed.patches.length, 'Cancel writes nothing').toBe(patches);
});

test('TL-165 the transcript follows the track chosen: words of a clip that is not there are dimmed', async ({ page, freshTimeline, openEditor, api, seed }) => {
  await extendAnalysis(page, ARTIFACT);
  const id = await freshTimeline('derush');
  await addVideoTrack(api, seed.projectId, id, 'Other', [mediaClipOnNewTrack('clipB.mp4', seed.files.clipB, 0, 120)]);
  const ed = await openEditor(id);
  await ed.openPanelTab('tab-transcript');
  const w0 = ed.page.getByTestId('transcript-word-w0');
  await expect(w0).toHaveAttribute('data-on-track', 'true');
  await ed.tap(ed.testId('transcript-track'));
  await ed.tap(ed.page.getByRole('option', { name: 'Other' }));
  await expect(w0, 'clipA\'s words are not on a track that shows clipB').toHaveAttribute('data-on-track', 'false');
});

test('TL-172 moving a paragraph UP with its arrow reorders the clips, and one undo restores', async ({ page, freshTimeline, openEditor }) => {
  const ed = await derush(page, freshTimeline, openEditor);
  await ed.openPanelTab('tab-transcript');
  await expect(ed.page.getByTestId('transcript-word-w0')).toBeVisible();
  const before = (await ed.serverTimeline()).document.tracks[0].items.length;
  await ed.tap(ed.page.getByTestId('transcript-move-up-p1'));
  const moved = await ed.expectServer((tl) => tl.document.tracks[0].items.length > before, 'the clip was cut into pieces and re-ordered');
  expect(moved.document.tracks[0].items.some((i) => i.sourceIn !== undefined && i.sourceIn > 60), 'paragraph 1 now plays first').toBe(true);
  await ed.page.keyboard.press('Control+z');
  await ed.expectServer((tl) => tl.document.tracks[0].items.length === before, 'one undo restores the single clip');
});

test('TL-181 TL-183 TL-184 TL-185 TL-186 TL-187 TL-188 Subtitles: captions land on the chosen track; edit, disable, split, merge, export', async ({ page, freshTimeline, openEditor, api, seed }) => {
  await extendAnalysis(page, ARTIFACT);
  const id = await freshTimeline('derush');
  await addVideoTrack(api, seed.projectId, id, 'Captions here', []);
  const ed = await openEditor(id);
  await ed.openPanelTab('tab-subtitles');
  await expect(ed.page.getByTestId('subtitles-preview-count')).not.toContainText(/^\s*0 cues/);
  await ed.tap(ed.testId('subtitles-target-track'));
  await ed.tap(ed.page.getByRole('option', { name: 'Captions here' }));
  await ed.tap(ed.testId('subtitles-generate'));
  const made = await ed.expectServer((tl) => captions(tl).length > 0, 'captions are stored');
  const onTrack = made.document.tracks.find((t) => t.name === 'Captions here')!;
  expect(onTrack.items.filter((i) => i.type === 'caption').length, 'all captions landed on the chosen track').toBe(captions(made).length);

  // Edit the text of the first caption.
  const first = captions(made)[0];
  const text = ed.testId(`subtitles-text-${first.id}`);
  await text.fill('hello there');
  await text.blur();
  await ed.expectServer((tl) => captions(tl)[0].text === 'hello there', 'the caption text is stored');

  // Disable it.
  const row = ed.page.getByTestId(`subtitles-row-${first.id}`);
  await row.getByRole('switch').first().click({ force: true });
  await ed.expectServer((tl) => captions(tl)[0].enabled === false, 'the caption is stored as disabled');
  await row.getByRole('switch').first().click({ force: true });
  await ed.expectServer((tl) => captions(tl)[0].enabled !== false, 'and enabled again');

  // Export links serve what is SAVED.
  const srt = await api.request.get(await ed.testId('subtitles-srt').getAttribute('href') as string);
  expect(srt.ok()).toBe(true);
  expect(await srt.text()).toContain('hello there');
  const vtt = await api.request.get(await ed.testId('subtitles-vtt').getAttribute('href') as string);
  expect((await vtt.text()).startsWith('WEBVTT')).toBe(true);

  // Merge the first caption with the next one, then split it back at the playhead.
  const count = captions(await ed.serverTimeline()).length;
  await ed.tap(ed.testId(`subtitles-merge-${first.id}`));
  await ed.expectServer((tl) => captions(tl).length === count - 1, 'merge joins two captions into one');
  const merged = captions(await ed.serverTimeline())[0];
  await ed.seek(Math.floor((merged.recordIn + merged.recordOut) / 2));
  await ed.tap(ed.testId(`subtitles-split-${merged.id}`));
  await ed.expectServer((tl) => captions(tl).length === count, 'split cuts it into two again');
});

test('TL-180 the Speech-from track decides which words are captioned', async ({ page, freshTimeline, openEditor, api, seed }) => {
  await extendAnalysis(page, ARTIFACT);
  const id = await freshTimeline('derush');
  await addVideoTrack(api, seed.projectId, id, 'Other', [mediaClipOnNewTrack('clipB.mp4', seed.files.clipB, 0, 120)]);
  const ed = await openEditor(id);
  await ed.openPanelTab('tab-subtitles');
  const count = async () => Number(/(\d+) cues?/.exec((await ed.page.getByTestId('subtitles-preview-count').textContent()) ?? '')?.[1] ?? -1);
  await expect.poll(count, { message: 'cues come from the words' }).toBeGreaterThan(0);
  await ed.tap(ed.testId('subtitles-source-track'));
  await ed.tap(ed.page.getByRole('option', { name: 'Other' }));
  await expect.poll(count, { message: 'clipB has no transcript, so no cues' }).toBe(0);
});

test('TL-195 TL-196 Takes: choose the track, jump to a clip', async ({ page, freshTimeline, openEditor, api, seed }) => {
  desktopLayoutOnly('the Takes tab is hidden on a phone (PHONE_HIDDEN_TOOL_TABS in phone/captions-logic.ts; docs/editor.md "Not on a phone": takes)');
  await extendAnalysis(page, {
    shots: [
      { id: 's0', startSec: 0, endSec: 4.5, visual: { motionMean: 0.02, motionClass: 'Subtle', duplicateGroupId: 'k0', isBestTake: false } },
      { id: 's1', startSec: 8, endSec: 12, visual: { motionMean: 0.02, motionClass: 'Subtle', duplicateGroupId: 'k0', isBestTake: true } },
    ],
    duplicateGroups: [{ id: 'k0', shotIds: ['s0', 's1'], bestShotId: 's1', meanSimilarity: 0.93 }],
  });
  const id = await freshTimeline('derush');
  const tl = await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks[0].items[0].recordOut = 120;
  });
  const clipId = tl.document.tracks[0].items[0].id;
  const ed = await openEditor(id);
  await ed.openPanelTab('tab-takes');
  await expect(ed.testId('takes-track')).toBeVisible();
  await expect(ed.page.getByTestId(`take-stack-${clipId}`)).toBeVisible();
  await ed.seek(100);
  await ed.tap(ed.page.getByTestId(`take-stack-${clipId}`).getByRole('button').first());
  await expect(ed.testId('status-timecode'), 'the clip name button jumps to the clip start').toHaveText('00:00:00:00');
  void uuid;
});
