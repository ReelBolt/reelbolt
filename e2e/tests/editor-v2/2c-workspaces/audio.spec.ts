import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import { mediaClip } from '../../../fixtures/editor-v2/api';
import { downloadRender, runOn } from '../../../fixtures/editor-v2/rows';
import { audioStats, clickSlider, renderFromDeliver } from '../../../fixtures/editor-v2/ws';

/**
 * Lane C, AUDIO workspace (inventory/workspaces.md 2.6, WS-140..WS-155; timeline.md TL-400..416). The preview graph has no
 * buses or dynamics, so every control is authored through the UI, checked in the stored document, and PROVED by measuring a
 * real Deliver render with ffmpeg (volumedetect per time window and channel).
 */

type Api = import('../../../fixtures/editor-v2/api').EditorApi;
type Seed = import('../../../fixtures/editor-v2/index').Seed;

/** Picture: the still for 10 s. A1 = tone at 0-5 s, A2 = tone at 5-10 s, so each track is measured in its own window. */
async function twoToneTimeline(freshTimeline: (f: 'blank') => Promise<string>, api: Api, seed: Seed, extra?: (doc: Awaited<ReturnType<Api['getTimeline']>>['document']) => void): Promise<{ id: string; a1: string; a2: string }> {
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [mediaClip(crypto.randomUUID(), 'still.png', seed.files.still, 0, 300)];
    const a1 = doc.tracks.find((t) => t.kind === 'audio')!;
    a1.name = 'A1';
    a1.items = [mediaClip(crypto.randomUUID(), 'tone.wav', seed.files.tone, 0, 150)];
    const a2 = structuredClone(a1);
    a2.id = crypto.randomUUID();
    a2.name = 'A2';
    a2.items = [mediaClip(crypto.randomUUID(), 'tone.wav', seed.files.tone, 150, 300)];
    doc.tracks.push(a2);
    extra?.(doc);
  });
  const doc = (await api.getTimeline(seed.projectId, id)).document;
  const audio = doc.tracks.filter((t) => t.kind === 'audio');
  return { id, a1: audio[0].id, a2: audio[1].id };
}

const W1: [number, number] = [0.3, 4.7];
const W2: [number, number] = [5.3, 9.7];

async function renderOnce(ed: EditorPage, id: string, seed: Seed, api: Api): Promise<string> {
  const r = await renderFromDeliver(ed, id);
  expect(r.status, `render ${r.errorMessage ?? ''}`).toBe('Passed');
  return downloadRender(api, seed.projectId, r);
}

const peakOf = async (ed: EditorPage, trackId: string): Promise<number> => {
  const raw = (await ed.testId(`mixer-peak-${trackId}`).getAttribute('data-peak-db')) ?? '';
  const n = Number(raw);
  return Number.isFinite(n) ? n : -Infinity;
};

/** WS-140/141/144: the strip's peak meter moves while a clip plays, Mute silences it in the preview, and Mute / Solo decide what a render contains. */
test('WS-140 Mute and Solo: stored, the meter goes silent in the preview, and the render leaves the muted track out', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(420_000);
  const { id, a1, a2 } = await twoToneTimeline(freshTimeline, api, seed);
  const ed = await openEditor(id);
  await ed.workspace('audio');
  await ed.openAdvancedAudio();

  // WS-144: the meter reads the audio while A1's tone plays; paused it falls back.
  await ed.seek(30);
  await ed.tap(ed.testId('transport-play'));
  await expect.poll(() => peakOf(ed, a1), { message: 'the A1 meter moves while its tone plays' }).toBeGreaterThan(-40);
  // WS-140 in the preview: muting A1 silences its meter while the transport keeps playing.
  await ed.tap(ed.testId(`mixer-mute-${a1}`));
  await expect.poll(() => peakOf(ed, a1), { message: 'a muted track is silent in the preview' }).toBeLessThan(-70);
  await ed.tap(ed.testId('transport-play'));
  await ed.expectServer((t) => t.document.tracks.find((x) => x.id === a1)!.mute === true, 'mute is stored on A1');

  // The render leaves A1's window silent and keeps A2's tone.
  const muted = await renderOnce(ed, id, seed, api);
  expect(audioStats(muted, ...W1).maxDb, 'WS-140 render: A1 (muted) is silent').toBeLessThan(-70);
  expect(audioStats(muted, ...W2).meanDb, 'WS-140 render: A2 is still there').toBeGreaterThan(-40);

  // Un-mute (undo is also available), Solo A2: A1 must go silent in the render.
  await ed.workspace('audio');
  await ed.openAdvancedAudio();
  await ed.tap(ed.testId(`mixer-mute-${a1}`));
  await ed.expectServer((t) => !t.document.tracks.find((x) => x.id === a1)!.mute, 'A1 is un-muted');
  await ed.tap(ed.testId(`mixer-solo-${a2}`));
  await ed.expectServer((t) => t.document.tracks.find((x) => x.id === a2)!.solo === true, 'solo is stored on A2');
  const soloed = await renderOnce(ed, id, seed, api);
  expect(audioStats(soloed, ...W1).maxDb, 'WS-141 render: A1 is silent while A2 is soloed').toBeLessThan(-70);
  expect(audioStats(soloed, ...W2).meanDb, 'WS-141 render: the soloed A2 plays').toBeGreaterThan(-40);
});

/** WS-140 phone: the same Mute / Solo buttons are reachable by touch and store the flags. */
test('WS-140 phone: Mute and Solo store the track flags', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('pixel7', 'iphone14webkit');
  const { id, a1, a2 } = await twoToneTimeline(freshTimeline, api, seed);
  const ed = await openEditor(id);
  await ed.workspace('audio'); // the Audio sheet lists a Mute and a Solo button per track
  await ed.tap(ed.testId(`phone-audio-mute-${a1}`));
  await ed.tap(ed.testId(`phone-audio-solo-${a2}`));
  await ed.expectServer((t) => t.document.tracks.find((x) => x.id === a1)!.mute === true && t.document.tracks.find((x) => x.id === a2)!.solo === true, 'mute and solo are stored');
});

/** WS-142/143: the fader and the pan slider write once each, and the render level and channel balance follow. */
test('WS-142 Fader -20 dB and Pan hard left change the rendered level and channel balance', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(420_000);
  const { id, a1, a2 } = await twoToneTimeline(freshTimeline, api, seed);
  const ed = await openEditor(id);
  const base = await renderOnce(ed, id, seed, api);
  const baseA2 = audioStats(base, ...W2).meanDb;
  const baseA1 = audioStats(base, ...W1);

  await ed.workspace('audio');
  await ed.openAdvancedAudio();
  const sent = ed.patches.length;
  const faderValue = await clickSlider(ed.page, ed.testId(`mixer-fader-${a2}`), 40 / 72);
  expect(faderValue, 'the fader was set near -20 dB').toBeLessThan(-18);
  expect(faderValue).toBeGreaterThan(-22);
  const panValue = await clickSlider(ed.page, ed.testId(`mixer-pan-${a1}`), 0.0);
  expect(panValue, 'pan hard left').toBeLessThan(-0.9);
  const tl = await ed.expectServer((t) => {
    const g = t.document.tracks.find((x) => x.id === a2)!.gainDb as { value?: number } | undefined;
    return typeof g?.value === 'number' && g.value < -18 && Number(t.document.tracks.find((x) => x.id === a1)!.pan ?? 0) < -0.9;
  }, 'fader and pan are stored');
  expect(tl.document.tracks.find((x) => x.id === a2)!.gainDb).toMatchObject({ value: faderValue });
  await ed.page.waitForTimeout(1200);
  expect(ed.patches.slice(sent).flatMap((p) => p.ops).filter((o) => o === 'setTrackProperty'), 'one op per slider release').toHaveLength(2);

  const edited = await renderOnce(ed, id, seed, api);
  const dropped = baseA2 - audioStats(edited, ...W2).meanDb;
  expect(Math.abs(dropped - 20), `A2 is about 20 dB quieter in the render (was ${baseA2}, dropped ${dropped})`).toBeLessThan(2);
  const left = audioStats(edited, ...W1, 'left').meanDb;
  const right = audioStats(edited, ...W1, 'right').meanDb;
  expect(left - right, `A1 hard left: the left channel is much louder (L ${left}, R ${right})`).toBeGreaterThan(25);
  expect(left, 'the left channel keeps (about) the original level').toBeGreaterThan(baseA1.meanDb - 4);
});

/** WS-145: the EQ low cut typed like a user types it (digit by digit), stored, and a 2 kHz low cut removes the 550 Hz tone in the render. */
test('WS-145 EQ low cut typed into the mixer is stored without refusals and silences a 550 Hz tone in the render', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(420_000);
  const { id, a1 } = await twoToneTimeline(freshTimeline, api, seed);
  const ed = await openEditor(id);
  const base = await renderOnce(ed, id, seed, api);
  const baseA1 = audioStats(base, ...W1).meanDb;
  await ed.workspace('audio');
  await ed.openAdvancedAudio();
  const input = ed.testId(`mixer-lowcut-${a1}`);
  await input.click();
  await input.pressSequentially('2000', { delay: 60 });
  await input.blur();
  await ed.expectServer((t) => (t.document.tracks.find((x) => x.id === a1)!.eq as { lowCutHz?: number } | undefined)?.lowCutHz === 2000, 'the low cut is stored as typed');
  await expect(ed.page.getByText(/refus|not allowed|must be/i), 'typing digit by digit raised no refusal').toHaveCount(0);
  const cut = audioStats(await renderOnce(ed, id, seed, api), ...W1).meanDb;
  expect(baseA1 - cut, `a 2 kHz low cut takes the 550 Hz tone down by >15 dB (was ${baseA1}, now ${cut})`).toBeGreaterThan(15);
});

/** WS-150/151/153: a bus made in the UI, a track routed to it and the bus gain: the render follows the bus. */
test('WS-151 Add bus, route A2 to it, bus gain -12 dB: the render is 12 dB quieter for A2 only', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(420_000);
  const { id, a1, a2 } = await twoToneTimeline(freshTimeline, api, seed);
  const ed = await openEditor(id);
  const base = await renderOnce(ed, id, seed, api);
  const [b1, b2] = [audioStats(base, ...W1).meanDb, audioStats(base, ...W2).meanDb];

  await ed.workspace('audio');
  await ed.openAdvancedAudio();
  await expect(ed.testId('buses-render-note'), 'the panel says the final mix is heard after the render').toContainText('Use Deliver to hear the final mix');
  await ed.testId('bus-new-name').fill('Music bus');
  await ed.tap(ed.testId('bus-add'));
  let tl = await ed.expectServer((t) => (t.document.buses as unknown[] | undefined)?.length === 1, 'the bus is stored');
  const bus = (tl.document.buses as Array<{ id: string; name: string }>)[0];
  expect(bus.name).toBe('Music bus');

  await ed.page.getByTestId(`bus-${bus.id}`).getByLabel('Gain dB').fill('-12');
  await ed.expectServer((t) => ((t.document.buses as Array<{ gainDb?: { value?: number } }>)[0].gainDb?.value) === -12, 'the bus gain is stored');
  await ed.tap(ed.testId(`track-route-${a2}`));
  await ed.tap(ed.page.getByRole('option', { name: 'Music bus' }));
  tl = await ed.expectServer((t) => t.document.tracks.find((x) => x.id === a2)!.busId === bus.id, 'A2 is routed to the bus');
  await ed.reload();

  const routed = await renderOnce(ed, id, seed, api);
  expect(Math.abs(audioStats(routed, ...W1).meanDb - b1), 'A1 (not routed) is unchanged').toBeLessThan(0.6);
  const dropped = b2 - audioStats(routed, ...W2).meanDb;
  expect(Math.abs(dropped - 12), `A2 through the -12 dB bus is 12 dB quieter (dropped ${dropped})`).toBeLessThan(1.5);

  // WS-151 remove: the bus is deleted and A2 falls back to the master (stored as removed bus).
  await ed.workspace('audio');
  await ed.openAdvancedAudio();
  await ed.tap(ed.testId(`bus-remove-${bus.id}`));
  await ed.expectServer((t) => !(t.document.buses as unknown[] | undefined)?.length, 'the bus is removed');
  void a1;
});

/** WS-152: Limiter, Compressor and Noise gate authored on a track change the rendered audio the way they say; the other stages render. */
test('WS-152 Limiter caps the peak, Compressor and Gate lower the level, and the full processing chain renders', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(600_000);
  const { id, a1 } = await twoToneTimeline(freshTimeline, api, seed, (doc) => {
    // +12 dB on A1 so the limiter has something to catch (tone peaks near -6 dBFS).
    doc.tracks.find((t) => t.name === 'A1')!.gainDb = { value: 12 };
  });
  const ed = await openEditor(id);
  const base = audioStats(await renderOnce(ed, id, seed, api), ...W1);
  expect(base.maxDb, 'the loud baseline is above -12 dB').toBeGreaterThan(-9);

  const set = async (testId: string, value: string) => ed.testId(`track-${a1}-${testId}`).fill(value);
  const toggle = async (testId: string, on: boolean) => {
    const box = ed.testId(`track-${a1}-${testId}`);
    await box.scrollIntoViewIfNeeded();
    await box.setChecked(on, { force: true });
  };
  await ed.workspace('audio');
  await ed.openAdvancedAudio();

  // Limiter: ceiling -12 dB.
  await toggle('limiter', true);
  await set('limiter-ceiling', '-12');
  await ed.expectServer((t) => (t.document.tracks.find((x) => x.id === a1)!.dynamics as { limiter?: { ceilingDb: number } } | undefined)?.limiter?.ceilingDb === -12, 'the limiter is stored');
  const limited = audioStats(await renderOnce(ed, id, seed, api), ...W1);
  expect(limited.maxDb, `limiter ceiling -12: rendered peak ${limited.maxDb} dB`).toBeLessThan(-10.5);
  await ed.workspace('audio');
  await ed.openAdvancedAudio();
  await toggle('limiter', false);
  await ed.expectServer((t) => !(t.document.tracks.find((x) => x.id === a1)!.dynamics as { limiter?: unknown } | undefined)?.limiter, 'the limiter is removed');

  // Compressor: threshold -40, ratio 20.
  await toggle('compressor', true);
  await set('comp-threshold', '-40');
  await set('comp-ratio', '20');
  await ed.expectServer((t) => (t.document.tracks.find((x) => x.id === a1)!.dynamics as { compressor?: { ratio: number } } | undefined)?.compressor?.ratio === 20, 'the compressor is stored');
  const compressed = audioStats(await renderOnce(ed, id, seed, api), ...W1);
  expect(base.meanDb - compressed.meanDb, `a 20:1 compressor at -40 dB takes the loud tone down (${base.meanDb} -> ${compressed.meanDb})`).toBeGreaterThan(6);
  await ed.workspace('audio');
  await ed.openAdvancedAudio();
  await toggle('compressor', false);

  // Gate: threshold above the tone's level.
  await toggle('gate', true);
  await set('gate-threshold', '-2');
  await set('gate-ratio', '20');
  await ed.expectServer((t) => (t.document.tracks.find((x) => x.id === a1)!.dynamics as { gate?: { thresholdDb: number } } | undefined)?.gate?.thresholdDb === -2, 'the gate is stored');
  const gated = audioStats(await renderOnce(ed, id, seed, api), ...W1);
  expect(base.meanDb - gated.meanDb, `a gate above the tone's level attenuates it (${base.meanDb} -> ${gated.meanDb})`).toBeGreaterThan(6);
  await ed.workspace('audio');
  await ed.openAdvancedAudio();
  await toggle('gate', false);

  // De-esser, Denoise, Leveler: stored, and the track with every stage on still renders sound.
  await toggle('deesser', true);
  await toggle('denoise', true);
  await toggle('leveler', true);
  await toggle('limiter', true);
  await ed.expectServer((t) => {
    const a = t.document.tracks.find((x) => x.id === a1)!;
    return !!(a.dynamics as { deesser?: unknown } | undefined)?.deesser && !!a.denoise && !!a.leveler;
  }, 'de-esser, denoise and leveler are stored');
  const all = audioStats(await renderOnce(ed, id, seed, api), ...W1);
  expect(all.meanDb, 'the full chain renders audible sound').toBeGreaterThan(-60);
});

/** WS-149 + WS-146: ducking derived from dialogue writes keys on the music track that dip under dialogue; the lane opens; the render dips. */
test('WS-149 Derive ducking writes gain keys on the music track and the render is quieter under dialogue', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(420_000);
  // A2 (music) plays 0-10 s; A1 (dialogue) plays 0-5 s: the music must dip over 0-5 s.
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [mediaClip(crypto.randomUUID(), 'still.png', seed.files.still, 0, 300)];
    const a1 = doc.tracks.find((t) => t.kind === 'audio')!;
    a1.name = 'Voice';
    a1.items = [mediaClip(crypto.randomUUID(), 'tone.wav', seed.files.tone, 0, 150)];
    const music = structuredClone(a1);
    music.id = crypto.randomUUID();
    music.name = 'Music';
    music.items = [mediaClip(crypto.randomUUID(), 'tone.wav', seed.files.tone, 0, 150), mediaClip(crypto.randomUUID(), 'tone.wav', seed.files.tone, 150, 300)];
    doc.tracks.push(music);
  });
  const doc0 = (await api.getTimeline(seed.projectId, id)).document;
  const musicId = doc0.tracks.find((t) => t.name === 'Music')!.id;
  const ed = await openEditor(id);
  await ed.workspace('audio');
  await ed.openAdvancedAudio();
  // The mixer picks the track named Music and the voice tracks on its own; the voice is named Voice here.
  await ed.testId('ducking-depth').fill('24');
  await ed.tap(ed.testId('ducking-derive'));
  const tl = await ed.expectServer((t) => ((t.document.tracks.find((x) => x.id === musicId)!.gainDb as { keys?: unknown[] } | undefined)?.keys?.length ?? 0) > 0, 'ducking keys are stored on the music track');
  const keys = (tl.document.tracks.find((x) => x.id === musicId)!.gainDb as { keys: Array<{ frame: number; value: number }> }).keys;
  expect(Math.min(...keys.map((k) => k.value)), 'the curve dips by the depth').toBeLessThanOrEqual(-20);
  expect(Math.max(...keys.map((k) => k.value)), 'and returns to the bed level').toBeGreaterThanOrEqual(-1);

  // WS-146 the automation lane button appears and opens the lane.
  await ed.tap(ed.testId(`mixer-lane-${musicId}`));
  await expect(ed.page.getByTestId(`mixer-strip-${musicId}`).locator('svg').first()).toBeVisible();

  // Render: the music (A2) is much quieter while the dialogue plays than after it ends.
  const file = await renderOnce(ed, id, seed, api);
  // Both tracks play 0-5 s; only music plays 5-10 s. Compare the 0.3-4.7 mix (ducked music + dialogue) with the 5.3-9.7 music alone.
  const under = audioStats(file, ...W1).meanDb;
  const alone = audioStats(file, ...W2).meanDb;
  // Without ducking the first window would be ~6 dB LOUDER than the second (two equal tones). Ducked it is quieter or about equal.
  expect(under - alone, `under dialogue the music is ducked (mix ${under} dB vs music alone ${alone} dB)`).toBeLessThan(3);
});

/** WS-147 + WS-148: the loudness readout resets, and the last render's authoritative loudness is shown after a render. */
test('WS-147 Reset loudness meter clears the live readout; the last render’s loudness appears after a render', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(300_000);
  const { id } = await twoToneTimeline(freshTimeline, api, seed);
  const ed = await openEditor(id);
  await ed.workspace('audio');
  await ed.openAdvancedAudio();
  await ed.seek(10);
  await ed.tap(ed.testId('transport-play'));
  const master = ed.testId('mixer-master');
  await expect.poll(async () => (await master.textContent()) ?? '', { message: 'the live readout gets a number while playing' }).toMatch(/I -?\d+\.\d LUFS/);
  await ed.tap(ed.testId('transport-play'));
  await ed.tap(ed.testId('mixer-reset-meter'));
  await expect(master).toContainText('I −∞ LUFS');
  const r = await renderFromDeliver(ed, id);
  expect(r.status).toBe('Passed');
  await ed.workspace('audio');
  await ed.openAdvancedAudio();
  await expect(ed.testId('mixer-render-loudness')).toContainText(/I -?\d+(\.\d)? LUFS/, { timeout: 30_000 });
  const shown = Number(/I (-?\d+(\.\d)?) LUFS/.exec((await ed.testId('mixer-render-loudness').textContent()) ?? '')![1]);
  expect(Math.abs(shown - (r.loudness?.integratedLufs ?? 999)), 'the readout equals the render measurement').toBeLessThan(0.1);
});

/** WS-151 (Output select): a bus routed into another bus; the render follows the whole chain A2 -> Y -> X(-12 dB) -> master. */
test('WS-151 a bus’s Output select sends it into another bus: stored, and the render applies the downstream bus gain', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(420_000);
  const { id, a2 } = await twoToneTimeline(freshTimeline, api, seed);
  const ed = await openEditor(id);
  const base = audioStats(await renderOnce(ed, id, seed, api), ...W2).meanDb;

  await ed.workspace('audio');
  await ed.openAdvancedAudio();
  const stamp = Date.now();
  for (const name of [`X-${stamp}`, `Y-${stamp}`]) {
    await ed.testId('bus-new-name').fill(name);
    await ed.tap(ed.testId('bus-add'));
  }
  let tl = await ed.expectServer((t) => (t.document.buses as unknown[] | undefined)?.length === 2, 'both buses are stored');
  const buses = tl.document.buses as Array<{ id: string; name: string; outputBusId?: string | null }>;
  const x = buses.find((b) => b.name === `X-${stamp}`)!;
  const y = buses.find((b) => b.name === `Y-${stamp}`)!;

  // X gets -12 dB, Y is routed INTO X with its Output select, and A2 into Y.
  await ed.page.getByTestId(`bus-${x.id}`).getByLabel('Gain dB').fill('-12');
  await ed.tap(ed.testId(`bus-output-${y.id}`));
  await ed.tap(ed.page.getByRole('option', { name: `X-${stamp}` }));
  tl = await ed.expectServer((t) => (t.document.buses as Array<{ id: string; outputBusId?: string | null }>).find((b) => b.id === y.id)?.outputBusId === x.id, 'Y’s output is bus X');
  await ed.tap(ed.testId(`track-route-${a2}`));
  await ed.tap(ed.page.getByRole('option', { name: `Y-${stamp}` }));
  await ed.expectServer((t) => t.document.tracks.find((tr) => tr.id === a2)!.busId === y.id, 'A2 is routed to Y');
  await ed.expectServer((t) => ((t.document.buses as Array<{ id: string; gainDb?: { value?: number } }>).find((b) => b.id === x.id)?.gainDb?.value) === -12, 'X carries -12 dB');

  const routed = audioStats(await renderOnce(ed, id, seed, api), ...W2).meanDb;
  expect(Math.abs((base - routed) - 12), `the -12 dB of bus X reaches A2 through Y (was ${base}, now ${routed})`).toBeLessThan(1.5);

  // Output back to Master: the downstream gain no longer applies.
  await ed.workspace('audio');
  await ed.openAdvancedAudio();
  await ed.tap(ed.testId(`bus-output-${y.id}`));
  await ed.tap(ed.page.getByRole('option', { name: 'Master' }));
  await ed.expectServer((t) => !(t.document.buses as Array<{ id: string; outputBusId?: string | null }>).find((b) => b.id === y.id)?.outputBusId, 'Y feeds the master again');
});

/** T-003: the mixer keeps the left zone; the buses and the voice-over recorder live in the right zone. */
test('T-003 Audio splits the mixer left and the buses + voice-over right', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('blank'));
  await ed.workspace('audio');
  await expect(ed.testId('panel-left').getByTestId('mixer-panel')).toBeVisible();
  const right = ed.testId('panel-right');
  await expect(right.getByTestId('vo-recorder')).toBeVisible();
  await expect(right.getByTestId('buses-panel-collapsed').or(right.getByTestId('buses-panel'))).toBeVisible();
});
