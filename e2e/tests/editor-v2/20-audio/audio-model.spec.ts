import { test, expect } from '../../../fixtures/editor-v2';
import type { EditorPage } from '../../../fixtures/editor-v2';
import { audioTimeline, audioTrack } from '../../../fixtures/editor-v2/audio-lane';
import { downloadRender, meanVolumeDb, renderAndWait, runOn } from '../../../fixtures/editor-v2/rows';
import { SEEDED_LAYOUT } from '../../../fixtures/editor-v2';

/**
 * R08.3-R08.6 (T-010): the audio model in the Audio workspace. Every assertion reads the STORED document
 * (or a real output), never "the button exists".
 */

const VOICE = { name: 'Voice', clips: [{ name: 'voice-1', recordIn: 30, recordOut: 120 }] };

async function stripId(ed: EditorPage, name: string): Promise<string> {
  const tl = await ed.serverTimeline();
  return audioTrack(tl, name)!.id;
}

test('R08.3 typing a dB value into the fader stores gainDb; reset goes back to 0 dB', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  const ed = await openEditor(await audioTimeline(freshTimeline, api, seed, [VOICE]));
  const id = await stripId(ed, 'Voice');
  await ed.workspace('audio');
  const field = ed.testId(`mixer-fader-${id}-db`);
  await field.scrollIntoViewIfNeeded();
  await field.click();
  await field.fill('-6');
  await field.press('Enter');
  await ed.expectServer((tl) => audioTrack(tl, 'Voice')?.gainDb?.value === -6, 'the fader stored -6 dB');
  await field.click();
  await field.fill('+3.5');
  await field.press('Enter');
  await ed.expectServer((tl) => audioTrack(tl, 'Voice')?.gainDb?.value === 3.5, 'typed +3.5 dB stored');
  await ed.tap(ed.testId(`mixer-fader-${id}-reset`));
  await ed.expectServer((tl) => audioTrack(tl, 'Voice')?.gainDb?.value === 0, 'reset stores 0 dB');
  // The scale: the bottom reads minus infinity, and the field is a finger-sized target on a touch device.
  await field.click();
  await field.fill('-inf');
  await field.press('Enter');
  await ed.expectServer((tl) => audioTrack(tl, 'Voice')?.gainDb?.value === -60, 'minus infinity stores the bottom of the scale');
  await expect(field).toHaveValue('−∞');
  if (ed.isPhone) {
    const box = await field.boundingBox();
    expect(box!.height, 'the dB field is a 44 px target on a touch screen').toBeGreaterThanOrEqual(43);
  }
});

test('R08.3 double-clicking the fader resets it to 0 dB', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await audioTimeline(freshTimeline, api, seed, [{ ...VOICE }]));
  const id = await stripId(ed, 'Voice');
  await ed.workspace('audio');
  const field = ed.testId(`mixer-fader-${id}-db`);
  await field.fill('-9');
  await field.press('Enter');
  await ed.expectServer((tl) => audioTrack(tl, 'Voice')?.gainDb?.value === -9, 'stored -9 dB');
  await ed.testId(`mixer-fader-${id}-wrap`).dblclick();
  await ed.expectServer((tl) => audioTrack(tl, 'Voice')?.gainDb?.value === 0, 'double-click stored 0 dB');
});

test('R08.4 Match target moves the mix to the target and the measured loudness lands within 1 LU', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(300_000);
  const ed = await openEditor(await audioTimeline(freshTimeline, api, seed, [{ name: 'Voice', clips: [{ name: 'voice-1', recordIn: 0, recordOut: 150 }] }]));
  await ed.workspace('audio');
  await ed.tap(ed.testId('loudness-measure'));
  const measured = ed.testId('loudness-measured');
  await expect(measured).toHaveAttribute('data-lufs', /-?\d/, { timeout: 120_000 });
  const before = Number(await measured.getAttribute('data-lufs'));
  expect(before, 'a real measurement of a tone').toBeLessThan(-5);
  await expect(ed.testId('loudness-match')).toBeEnabled();
  await ed.tap(ed.testId('loudness-match'));
  // the faders moved (stored), then the mix is measured again
  const tl = await ed.expectServer((t) => (audioTrack(t, 'Voice')?.gainDb?.value ?? 0) !== 0, 'Match target stored a gain on the audio track');
  const gain = audioTrack(tl, 'Voice')!.gainDb!.value!;
  expect(gain, 'the fader moved by about the shortfall').toBeCloseTo(-14 - before, 0);
  await expect(measured).toHaveAttribute('data-verdict', 'on', { timeout: 120_000 });
  const after = Number(await measured.getAttribute('data-lufs'));
  expect(Math.abs(after - -14), `measured ${after} LUFS against the -14 target`).toBeLessThanOrEqual(1);
});

test('R08.5 Duck music under voice stores keyframes, shows Ducking: on, and re-derives after a voice clip moves', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await audioTimeline(freshTimeline, api, seed, [
    { name: 'Voice', clips: [{ name: 'voice-1', recordIn: 20, recordOut: 70 }] },
    { name: 'Music', clips: [{ name: 'music-1', recordIn: 0, recordOut: 150 }] },
  ]));
  await ed.workspace('audio');
  await expect(ed.testId('ducking-state')).toHaveAttribute('data-on', 'false');
  await ed.tap(ed.testId('ducking-derive'));
  await expect(ed.testId('ducking-state')).toHaveAttribute('data-on', 'true');
  const keysOf = (tl: Awaited<ReturnType<EditorPage['serverTimeline']>>) => audioTrack(tl, 'Music')?.gainDb?.keys ?? [];
  const first = await ed.expectServer((tl) => keysOf(tl).length > 0, 'ducking wrote keyframes on the music track');
  const k1 = keysOf(first);
  expect(Math.min(...k1.map((k) => k.value)), 'the music is lowered under the voice').toBeLessThanOrEqual(-11);
  const voiceStart = k1.find((k) => k.value <= -11)!.frame;
  expect(voiceStart, 'the dip starts around the voice clip (frame 20)').toBeLessThanOrEqual(20);
  // move the voice clip 10 frames later (Shift+Period nudge on the selected clip) and come back to Audio
  await ed.workspace('edit');
  await ed.selectClipAt(45, 1);
  await ed.page.keyboard.press('Shift+Period');
  await ed.expectServer((tl) => (tl.document.tracks.find((t) => t.name === 'Voice')?.items[0] as { recordIn: number }).recordIn === 30, 'the voice clip moved');
  await ed.workspace('audio');
  const second = await ed.expectServer((tl) => JSON.stringify(keysOf(tl)) !== JSON.stringify(k1), 'the ducking keyframes followed the moved clip');
  expect(keysOf(second).find((k) => k.value <= -11)!.frame, 'the dip moved later').toBeGreaterThan(voiceStart);
  await ed.tap(ed.testId('ducking-remove'));
  await ed.expectServer((tl) => (keysOf(tl).length === 0), 'Remove ducking clears the keyframes');
  await expect(ed.testId('ducking-state')).toHaveAttribute('data-on', 'false');
});

test('R08.6 Clean voice stores denoise and leveler; Audition renders playable audio around the playhead', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(300_000);
  const ed = await openEditor(await audioTimeline(freshTimeline, api, seed, [{ name: 'Voice', clips: [{ name: 'voice-1', recordIn: 0, recordOut: 150 }] }]));
  const id = await stripId(ed, 'Voice');
  await ed.workspace('audio');
  await ed.testId(`clean-voice-${id}`).check({ force: true });
  const tl = await ed.expectServer((t) => Boolean(audioTrack(t, 'Voice')?.denoise) && Boolean(audioTrack(t, 'Voice')?.leveler), 'Clean voice stored denoise and leveler');
  expect(audioTrack(tl, 'Voice')!.denoise).toMatchObject({ kind: 'fft' });
  expect(audioTrack(tl, 'Voice')!.leveler).toMatchObject({ kind: 'speech' });
  await expect(ed.testId('audio-processing-note')).toBeVisible();
  await ed.tap(ed.testId('audition-run'));
  const player = ed.testId('audition-player');
  await expect(player).toBeVisible({ timeout: 180_000 });
  const src = (await player.getAttribute('src'))!;
  const res = await ed.page.request.get(src);
  expect(res.status()).toBeLessThan(300);
  expect((await res.body()).length, 'the audition is a non-empty file').toBeGreaterThan(1000);
  await expect.poll(() => player.evaluate((el: HTMLAudioElement) => el.duration), { message: 'the browser can play it', timeout: 30_000 }).toBeGreaterThan(0.5);
  // it is not a project file: the renders list (Deliver) does not carry it
  const renders = await api.json<Array<{ outputFileName?: string }>>('GET', `${api.timelinesUrl(seed.projectId, ed.timelineId)}/renders`);
  expect(renders.filter((r) => /audition|loudness/i.test(r.outputFileName ?? '')), 'audio jobs stay out of the Deliver list').toHaveLength(0);
  await ed.tap(ed.testId(`clean-voice-${id}`));
  await ed.expectServer((t) => !audioTrack(t, 'Voice')?.denoise && !audioTrack(t, 'Voice')?.leveler, 'Clean voice off clears both');
});

test('R08.2 a multicam made with a chosen sound source stores it and renders with audio', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(360_000);
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  const doc = (await ed.serverTimeline()).document;
  const tone = doc.tracks.find((t) => t.kind === 'audio')!.items[0] as { name: string };
  await ed.selectClipAt(60, 0);
  await ed.selectClipAt(SEEDED_LAYOUT.tone[0] + 60, 1, ['Control']);
  await ed.openPanelTab('tab-structure');
  await ed.tap(ed.testId('multicam-audio-source'));
  await ed.page.getByRole('option', { name: tone.name, exact: true }).click();
  await ed.tap(ed.testId('multicam-create'));
  const tl = await ed.expectServer((t) => t.document.tracks.some((tr) => tr.items.some((i) => i.type === 'multicamClip')), 'the multicam was stored');
  const mc = tl.document.tracks.flatMap((t) => t.items).find((i) => i.type === 'multicamClip') as unknown as { angles: Array<{ id: string; name: string }>; audioSource?: string };
  const toneAngle = mc.angles.find((a) => a.name === tone.name)!;
  expect(mc.audioSource, 'the chosen angle supplies the sound').toBe(toneAngle.id);
  const r = await renderAndWait(api, seed.projectId, id);
  expect(r.status, `render ${r.errorMessage ?? ''}`).toBe('Passed');
  const file = await downloadRender(api, seed.projectId, r);
  expect(meanVolumeDb(file, 0.5, 3), 'the multicam render carries the tone').toBeGreaterThan(-45);
});
