import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline, seekAndBlur } from '../../../fixtures/editor-v2/lane-a';
import { audioTimeline, audioTrack } from '../../../fixtures/editor-v2/audio-lane';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Wave 3 / T-008: the Audio sheet (RM.11; x1 acceptance criterion 7: Duck music and Clean voice are ONE undo step each).
 * Every step is a tap, and every outcome is read back from the SAVED document or the media pool, never from the control.
 * Playwright WebKit has no MediaRecorder; the Chromium project stubs it so the happy path runs without a microphone.
 */
test.use({ serviceWorkers: 'block' });
test.beforeEach(() => { runOn('pixel7', 'iphone14webkit'); });

const VOICE = 'Voice';
const MUSIC = 'Music';

const mixTimeline = (freshTimeline: Parameters<typeof audioTimeline>[0], api: Parameters<typeof audioTimeline>[1], seed: Parameters<typeof audioTimeline>[2]) =>
  audioTimeline(freshTimeline, api, seed, [
    { name: MUSIC, clips: [{ name: 'music.wav', recordIn: 0, recordOut: 140 }, { name: 'music.wav', recordIn: 140, recordOut: 240 }] },
    { name: VOICE, clips: [{ name: 'voice-a.wav', recordIn: 60, recordOut: 120 }, { name: 'voice-b.wav', recordIn: 150, recordOut: 210 }] },
  ]);

const tap = (ed: EditorPage, testId: string) => ed.tap(ed.testId(testId));

/** A one second mono 8 kHz WAV, so a chosen "voice file" is real audio the browser can measure. */
const wavBytes = (seconds = 1): Buffer => {
  const samples = 8000 * seconds;
  const buffer = Buffer.alloc(44 + samples * 2);
  buffer.write('RIFF', 0);
  buffer.writeUInt32LE(36 + samples * 2, 4);
  buffer.write('WAVEfmt ', 8);
  buffer.writeUInt32LE(16, 16);
  buffer.writeUInt16LE(1, 20);
  buffer.writeUInt16LE(1, 22);
  buffer.writeUInt32LE(8000, 24);
  buffer.writeUInt32LE(16000, 28);
  buffer.writeUInt16LE(2, 32);
  buffer.writeUInt16LE(16, 34);
  buffer.write('data', 36);
  buffer.writeUInt32LE(samples * 2, 40);
  for (let i = 0; i < samples; i++) buffer.writeInt16LE(Math.round(Math.sin((i / 8000) * 2 * Math.PI * 440) * 8000), 44 + i * 2);
  return buffer;
};

test.describe('RM.11 Audio sheet', () => {
  test('RM.11 the sheet lists every audio track with a 44 px mute and solo, and Mute is stored in the document', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await mixTimeline(freshTimeline, api, seed));
    await ed.workspace('audio');
    const voice = audioTrack(await ed.serverTimeline(), VOICE)!;
    await expect(ed.testId('phone-audio-sheet')).toBeVisible();
    for (const id of [`phone-audio-mute-${voice.id}`, `phone-audio-solo-${voice.id}`]) {
      const box = (await ed.testId(id).boundingBox())!;
      expect(Math.min(box.width, box.height), `${id} touch target`).toBeGreaterThanOrEqual(43.5);
    }
    await tap(ed, `phone-audio-mute-${voice.id}`);
    await ed.expectServer((tl) => audioTrack(tl, VOICE)?.mute === true, 'Mute is stored on the Voice track');
    await tap(ed, `phone-audio-solo-${voice.id}`);
    await ed.expectServer((tl) => audioTrack(tl, VOICE)?.solo === true, 'Solo is stored on the Voice track');
  });

  test('RM.11 a track volume is shown in dB and one tap on "Back to 0 dB" stores 0', async ({ freshTimeline, openEditor, api, seed }) => {
    const id = await mixTimeline(freshTimeline, api, seed);
    await api.putDocument(seed.projectId, id, (doc) => {
      const music = doc.tracks.find((t) => t.name === MUSIC)!;
      music.gainDb = { value: -6, keys: null };
    });
    const ed = await openEditor(id);
    await ed.workspace('audio');
    const music = audioTrack(await ed.serverTimeline(), MUSIC)!;
    await expect(ed.testId(`phone-audio-gain-readout-${music.id}`)).toContainText('-6');
    await tap(ed, `phone-audio-gain-reset-${music.id}`);
    await ed.expectServer((tl) => (audioTrack(tl, MUSIC)?.gainDb as { value?: number } | undefined)?.value === 0, 'the volume is back at 0 dB');
  });

  test('AC 7 / RM.11 Duck music is one tap and one undo step', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await mixTimeline(freshTimeline, api, seed));
    await ed.workspace('audio');
    await expect(ed.testId('phone-audio-duck')).toBeEnabled();
    await tap(ed, 'phone-audio-duck');
    const ducked = await ed.expectServer((tl) => (audioTrack(tl, MUSIC)?.gainDb?.keys?.length ?? 0) > 0, 'the music fader holds a ducking curve');
    expect(audioTrack(ducked, VOICE)?.gainDb?.keys ?? null, 'the voice track is untouched').toBeFalsy();
    // ONE undo step: a single tap on Undo takes the whole curve away.
    await tap(ed, 'phone-undo');
    await ed.expectServer((tl) => !(audioTrack(tl, MUSIC)?.gainDb?.keys?.length), 'one Undo removed the ducking');
  });

  test('AC 7 / RM.11 Clean voice is one tap and one undo step', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await mixTimeline(freshTimeline, api, seed));
    await ed.workspace('audio');
    await tap(ed, 'phone-audio-clean-voice');
    const cleaned = await ed.expectServer((tl) => Boolean(audioTrack(tl, VOICE)?.denoise) && Boolean(audioTrack(tl, VOICE)?.leveler), 'noise removal and the leveler are on the Voice track');
    expect(audioTrack(cleaned, MUSIC)?.denoise ?? null, 'the music is not treated').toBeFalsy();
    await tap(ed, 'phone-undo');
    await ed.expectServer((tl) => !audioTrack(tl, VOICE)?.denoise && !audioTrack(tl, VOICE)?.leveler, 'one Undo took both back');
  });

  test('RM.11 Duck music says what is missing when there is no voice to duck under', async ({ freshTimeline, openEditor, api, seed }) => {
    const id = await audioTimeline(freshTimeline, api, seed, [{ name: MUSIC, clips: [{ name: 'music.wav', recordIn: 0, recordOut: 140 }, { name: 'music.wav', recordIn: 140, recordOut: 240 }] }]);
    const ed = await openEditor(id);
    await ed.workspace('audio');
    await expect(ed.testId('phone-audio-duck')).toBeDisabled();
    await expect(ed.testId('phone-audio-duck-reason')).toContainText(/voice/i);
  });
});

test.describe('RM.11 Voiceover on the phone', () => {
  test('RM.11 without MediaRecorder the sheet says so and an uploaded voice file lands at the playhead', async ({ page, freshTimeline, openEditor, api, seed }) => {
    // Playwright WebKit has none; on Chromium it is removed so both projects take the same path.
    await page.addInitScript(() => {
      delete (window as unknown as { MediaRecorder?: unknown }).MediaRecorder;
    });
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await seekAndBlur(ed, 15);
    await ed.workspace('audio');
    await expect(ed.testId('vo-unavailable')).toContainText('Recording is not available in this browser. Upload a voice file instead.');
    await expect(ed.testId('vo-record'), 'no Record button that cannot work').toHaveCount(0);
    await expect(ed.testId('vo-upload')).toBeVisible();

    await ed.page.locator('[data-testid="phone-audio-voiceover"] input[type="file"]').setInputFiles({ name: 'voice-note.wav', mimeType: 'audio/wav', buffer: wavBytes() });
    const tl = await ed.expectServer((t) => t.document.tracks.some((tr) => tr.kind === 'audio' && tr.items.length === 1), 'the voice file is placed on an audio track', 60_000);
    const clip = tl.document.tracks.find((tr) => tr.kind === 'audio' && tr.items.length === 1)!.items[0];
    expect(clip.recordIn, 'at the playhead').toBe(15);
    expect(clip.recordOut - clip.recordIn, 'about one second at 30 fps').toBeGreaterThanOrEqual(25);
    const pool = await api.json<Array<{ fileName: string; sizeBytes: number }>>('GET', `/api/v1/projects/${seed.projectId}/media`);
    const file = pool.find((p) => p.fileName === 'voice-note.wav');
    expect(file, 'the file is in the project').toBeTruthy();
    expect(file!.sizeBytes, 'its bytes were stored').toBeGreaterThan(0);
  });

  test('RM.11 recorder happy path with a stubbed MediaRecorder: record, stop, place at the playhead, one undo removes it', async ({ page, freshTimeline, openEditor, api, seed }) => {
    test.skip(test.info().project.name === 'iphone14webkit', 'the stub needs a real getUserMedia/AudioContext shape; recording itself is a manual check on a real iPhone (D8)');
    test.setTimeout(180_000);
    await page.addInitScript((wav: number[]) => {
      let shift = 0;
      const realNow = performance.now.bind(performance);
      performance.now = () => realNow() + shift;
      const bytes = new Uint8Array(wav);
      class FakeRecorder {
        static isTypeSupported(type: string) { return type.startsWith('audio/webm'); }
        state = 'inactive';
        ondataavailable: ((e: { data: Blob }) => void) | null = null;
        onstop: (() => void) | null = null;
        constructor(public stream: unknown, public options: { mimeType: string }) {}
        start() { this.state = 'recording'; shift += 2000; this.ondataavailable?.({ data: new Blob([bytes], { type: this.options.mimeType }) }); }
        stop() { this.state = 'inactive'; this.ondataavailable?.({ data: new Blob([bytes], { type: this.options.mimeType }) }); this.onstop?.(); }
      }
      (window as unknown as { MediaRecorder: unknown }).MediaRecorder = FakeRecorder;
      const fakeStream = { getTracks: () => [{ stop() {} }] };
      const media = navigator.mediaDevices ?? ({} as MediaDevices);
      Object.defineProperty(navigator, 'mediaDevices', { value: { ...media, getUserMedia: async () => fakeStream, enumerateDevices: async () => [] }, configurable: true });
    }, [...wavBytes(3)]);
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await seekAndBlur(ed, 30);
    await ed.workspace('audio');
    await expect(ed.testId('vo-unavailable'), 'the stubbed browser can record').toHaveCount(0);
    await tap(ed, 'vo-record');
    await expect(ed.testId('vo-stop')).toBeVisible({ timeout: 15_000 });
    await tap(ed, 'vo-stop');
    const tl = await ed.expectServer((t) => t.document.tracks.some((tr) => tr.kind === 'audio' && tr.items.length === 1), 'the take is placed', 60_000);
    const clip = tl.document.tracks.find((tr) => tr.kind === 'audio' && tr.items.length === 1)!.items[0];
    expect(clip.recordIn, 'at the playhead where recording started').toBe(30);
    expect(clip.recordOut - clip.recordIn, 'about two seconds at 30 fps').toBeGreaterThanOrEqual(55);
    const pool = await api.json<Array<{ fileName: string; sizeBytes: number }>>('GET', `/api/v1/projects/${seed.projectId}/media`);
    expect(pool.some((p) => /^vo-/.test(p.fileName) && p.sizeBytes > 0), 'the take is a project file with bytes').toBe(true);
    await tap(ed, 'phone-undo');
    await ed.expectServer((t) => !t.document.tracks.some((tr) => tr.kind === 'audio' && tr.items.length > 0), 'one Undo removes the voiceover');
  });
});
