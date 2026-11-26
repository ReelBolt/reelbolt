import { test, expect, type EditorPage, type Seed } from '../../../fixtures/editor-v2';

/** R08.1 (T-009): a pool insert makes a linked companion on "Camera audio"; a source without sound gets none; the sound is audible in the preview. */

const INSTRUMENT = `(() => {
  const log = (window.__e2eAudio = { started: [] });
  const AC = window.AudioContext;
  if (AC) window.AudioContext = class extends AC {
    constructor(...a) { super(...a); const make = this.createBufferSource.bind(this);
      this.createBufferSource = () => { const n = make(); const s = n.start.bind(n); n.start = (...x) => { log.started.push(n.buffer ? n.buffer.duration : -1); return s(...x); }; return n; }; }
  };
  const play = HTMLMediaElement.prototype.play;
  HTMLMediaElement.prototype.play = function (...a) { if (!this.muted && this.volume > 0) log.started.push(this.duration); return play.apply(this, a); };
})();`;
const started = (ed: EditorPage) => ed.page.evaluate(() => (window as unknown as { __e2eAudio: { started: number[] } }).__e2eAudio.started.slice());

async function waitProbe(seed: Seed, file: string): Promise<void> {
  const ref = `file:${file}`;
  await expect.poll(async () => {
    const media = await seed.api.json<Array<{ mediaRef: string; probe: unknown }>>('GET', `/api/v1/projects/${seed.projectId}/media`);
    return media.find((m) => m.mediaRef === ref)?.probe != null;
  }, { message: 'the source carries its ingest probe', timeout: 120_000, intervals: [2000] }).toBe(true);
}

async function insertFromPool(ed: EditorPage, ref: string): Promise<void> {
  await ed.workspace('media');
  if (ed.isPhone) {
    // The phone's Add media sheet places a source from the tile's menu.
    await ed.tap(ed.page.locator(`[data-testid="media-tile-menu"][data-for-media-ref="file:${ref}"]`).first());
    await ed.tap(ed.testId('tile-place-insert'));
    return;
  }
  const tile = ed.page.locator(`[data-testid="media-clip-tile"][data-media-ref="file:${ref}"]`).first();
  await ed.tap(tile);
  await ed.tap(ed.testId('media-insert-at-playhead'));
}

test('R08.1 inserting footage with sound gives a video clip and a companion with the same link on Camera audio, and it is audible', async ({ page, seed, openEditor, freshTimeline }) => {
  test.setTimeout(240_000);
  await page.addInitScript(INSTRUMENT);
  await waitProbe(seed, seed.files.clipA);
  const ed = await openEditor(await freshTimeline('blank'));
  await insertFromPool(ed, seed.files.clipA);
  const tl = await ed.expectServer((t) => t.document.tracks.some((x) => x.kind === 'audio' && x.items.length > 0), 'the companion was stored');
  const video = tl.document.tracks.find((t) => t.kind === 'video' && t.items.length > 0)!;
  const audio = tl.document.tracks.find((t) => t.kind === 'audio' && t.items.length > 0)!;
  const v = video.items[0] as unknown as { linkId?: string; recordIn: number; recordOut: number };
  const a = audio.items[0] as unknown as { linkId?: string; recordIn: number; recordOut: number };
  expect(audio.name).toBe('Camera audio');
  expect(v.linkId, 'the picture is linked').toBeTruthy();
  expect(a.linkId).toBe(v.linkId);
  expect([a.recordIn, a.recordOut]).toEqual([v.recordIn, v.recordOut]);
  await ed.workspace('edit');
  await ed.showTimeline();
  await ed.seek(a.recordIn + 15);
  await ed.tap(ed.testId('transport-play'));
  try {
    await expect.poll(async () => (await started(ed)).some((d) => Math.abs(d - 12) < 0.5), { message: 'the companion sound starts (a 12 s source)', timeout: 10_000 }).toBe(true);
  } finally {
    await ed.tap(ed.testId('transport-play'));
  }
});

test('R08.1 a picture without sound gets no companion and no link', async ({ seed, openEditor, freshTimeline }) => {
  const ed = await openEditor(await freshTimeline('blank'));
  await insertFromPool(ed, seed.files.still);
  const tl = await ed.expectServer((t) => t.document.tracks.some((x) => x.kind === 'video' && x.items.length > 0), 'the still was placed');
  const items = tl.document.tracks.flatMap((t) => t.items as unknown as Array<{ linkId?: string | null }>);
  expect(items).toHaveLength(1);
  expect(items[0].linkId ?? null).toBeNull();
  expect(tl.document.tracks.filter((t) => t.kind === 'audio').every((t) => t.items.length === 0)).toBe(true);
});
