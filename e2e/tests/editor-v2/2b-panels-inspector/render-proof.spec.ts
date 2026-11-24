import { execFileSync } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import { test, expect } from '../../../fixtures/editor-v2/lane-b-test';
import { mediaClip } from '../../../fixtures/editor-v2/api';
import { ARTIFACTS } from '../../../fixtures/editor-v2/env';
import { channelVolumesDb, downloadRender, meanVolumeDb, probe, renderAndWait, runOn } from '../../../fixtures/editor-v2/rows';
import { uuid } from '../../../fixtures/editor-v2/lane-b';

/**
 * Lane B: what the RENDER does with the two Inspector claims the preview cannot show (inventory S5, S7).
 * Real renders on the local stack (free ffmpeg path, no provider): the Inspector's choices follow what the
 * renderer really does.
 */

/** A stereo WAV with a 440 Hz tone on the LEFT channel only and silence on the right. */
function leftOnlyWav(): string {
  const dir = path.join(ARTIFACTS, 'media', 'lane-b');
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, `left-only-${uuid()}.wav`);
  execFileSync('ffmpeg', [
    '-v', 'error', '-y',
    '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000:duration=3',
    '-f', 'lavfi', '-i', 'anullsrc=r=48000:cl=mono',
    '-filter_complex', '[0:a][1:a]amerge=inputs=2,pan=stereo|c0=c0|c1=c1[a]',
    '-map', '[a]', '-t', '3', file,
  ]);
  return file;
}

test('S5 TL-247 the channel map "left" is applied by the render: a left-only recording plays on both sides', async ({ freshTimeline, api, seed }) => {
  runOn('desktop');
  test.setTimeout(300_000);
  const wav = leftOnlyWav();
  const fileId = await api.upload(seed.projectId, wav, 'audio/wav', `left-only-${Date.now()}.wav`);
  const id = await freshTimeline('blank');
  const render = async (channelMap?: string) => {
    await api.putDocument(seed.projectId, id, (doc) => {
      const v = doc.tracks.find((t) => t.kind === 'video')!;
      v.items = [];
      const a = doc.tracks.find((t) => t.kind === 'audio')!;
      a.items = [mediaClip(uuid(), 'left-only.wav', fileId, 0, 90, channelMap ? { channelMap } : {})];
    });
    const r = await renderAndWait(api, seed.projectId, id, { preset: 'AudioOnly' });
    expect(r.status, `render ${r.status} ${r.errorMessage ?? ''}`).toBe('Passed');
    return channelVolumesDb(await downloadRender(api, seed.projectId, r, 'm4a').catch(() => downloadRender(api, seed.projectId, r, 'mp4')));
  };
  const [leftAsRecorded, rightAsRecorded] = await render();
  expect(leftAsRecorded, 'as recorded the left channel carries the tone').toBeGreaterThan(-40);
  expect(rightAsRecorded, 'as recorded the right channel is silent').toBeLessThan(-60);
  const [l, r] = await render('left');
  expect(l, 'left only: the tone is still on the left').toBeGreaterThan(-40);
  expect(r, 'left only: and now on the right as well').toBeGreaterThan(-40);
});

test('S7 TL-253 the sound of a clip on a picture track is NOT rendered (so the Inspector does not offer gain there)', async ({ freshTimeline, api, seed }) => {
  runOn('desktop');
  test.setTimeout(300_000);
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    const v = doc.tracks.find((t) => t.kind === 'video')!;
    v.items = [mediaClip(uuid(), 'clipA.mp4', seed.files.clipA, 0, 90)]; // clipA has a 440 Hz tone for its first 3 s
    for (const t of doc.tracks.filter((x) => x.kind === 'audio')) t.items = [];
  });
  const r = await renderAndWait(api, seed.projectId, id);
  expect(r.status, `render ${r.status} ${r.errorMessage ?? ''}`).toBe('Passed');
  const file = await downloadRender(api, seed.projectId, r);
  expect(probe(file).durationSec).toBeGreaterThan(2.5);
  expect(meanVolumeDb(file, 0.2, 2.5), 'the picture-track clip contributes no sound to the render').toBeLessThan(-60);
});
