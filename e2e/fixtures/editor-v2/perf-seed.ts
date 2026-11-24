import { execFileSync } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import type { EditorApi, Item, Track } from './api';
import { mediaClip } from './api';
import { ARTIFACTS } from './env';

export const PERF_MEDIA_DIR = process.env.EDITOR_E2E_PERF_MEDIA_DIR ?? path.join(ARTIFACTS, 'media-perf');

/** Frame layout of the representative timeline (30 fps, 22 s). */
export const PERF_LAYOUT = { totalFrames: 660, transition: [300, 330] as const };

export function ensurePerfMedia(with4k = false): string {
  const names = ['perf-a.mp4', 'perf-b.mp4', 'perf-a-proxylike.mp4', 'perf-b-proxylike.mp4', 'perf-a-60.mp4', 'perf-b-60.mp4', 'perf-music.wav', ...(with4k ? ['perf-a-4k.mp4', 'perf-b-4k.mp4', 'perf-a-4k-60.mp4', 'perf-b-4k-60.mp4'] : [])];
  if (!names.every((n) => fs.existsSync(path.join(PERF_MEDIA_DIR, n)))) {
    execFileSync('bash', [path.join(__dirname, 'media', 'make-perf-media.sh'), PERF_MEDIA_DIR], {
      stdio: 'pipe',
      env: { ...process.env, PERF_4K: with4k ? '1' : '0' },
    });
  }
  return PERF_MEDIA_DIR;
}

async function waitForProxies(api: EditorApi, projectId: string, ids: string[], timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  for (const id of ids) {
    for (;;) {
      const rows = await api.json<Array<{ kind: string }>>('GET', `/api/v1/projects/${projectId}/media/file:${id}/derivatives`);
      if (rows.some((r) => r.kind === 'proxy')) break;
      if (Date.now() > deadline) throw new Error(`no proxy derivative for file ${id} after ${timeoutMs} ms (is the workflow engine's media-derivatives consumer healthy?)`);
      await new Promise((r) => setTimeout(r, 2000));
    }
  }
}

export type PerfSeed = { projectId: string; timelineId: string; fileIds: Record<string, string> };

/**
 * The representative multi-track timeline: V1 (1080p clip, 30-frame dissolve, 1080p clip), V2 (a 1080p
 * picture-in-picture at 40 % scale), V3 (one title), A1 (the V1 clips' camera audio) and a music track.
 * `derivatives`: request the real proxy derivative of every video and wait for it (the "proxy produced" case through
 * the actual pipeline; needs the workflow engine's media-derivatives consumer to work).
 * `proxyLike`: upload 720p / GOP-15 encodes (the exact shape the derivative pipeline's proxy has, see
 * docs/editor.md "Proxy") instead of the 1080p / GOP-30 originals. The viewer plays whatever URL the
 * resolver returns; uploading proxy-shaped files measures the "proxy produced" case without depending
 * on the workflow-engine derivative worker, which is not part of what this spec verifies.
 */
export async function createPerfSeed(api: EditorApi, name: string, opts: { proxyLike?: boolean; derivatives?: boolean; fps60?: boolean; res4k?: boolean }): Promise<PerfSeed> {
  const dir = ensurePerfMedia(opts.res4k === true);
  const project = await api.createProject(name);
  const fileIds: Record<string, string> = {};
  const suffix = opts.res4k ? (opts.fps60 ? '-4k-60' : '-4k') : opts.fps60 ? '-60' : opts.proxyLike ? '-proxylike' : '';
  fileIds.a = await api.upload(project.id, path.join(dir, `perf-a${suffix}.mp4`), 'video/mp4');
  fileIds.b = await api.upload(project.id, path.join(dir, `perf-b${suffix}.mp4`), 'video/mp4');
  fileIds.music = await api.upload(project.id, path.join(dir, 'perf-music.wav'), 'audio/wav');
  if (opts.derivatives) {
    // The real pipeline: ask for the proxy of every video and wait until the derivative list has one.
    const videoIds = [fileIds.a, fileIds.b];
    for (const id of videoIds) {
      await api.json('POST', `/api/v1/projects/${project.id}/media/derivatives/request`, { mediaRef: `file:${id}`, kinds: ['proxy'] }, [200, 202]);
    }
    await waitForProxies(api, project.id, videoIds, 300_000);
  }

  const timeline = await api.createTimeline(project.id, 'perf-representative');
  const k = opts.fps60 ? 2 : 1; // frames per 30-fps frame
  const [tIn, tOut] = [PERF_LAYOUT.transition[0] * k, PERF_LAYOUT.transition[1] * k];
  const end = PERF_LAYOUT.totalFrames * k;
  await api.putDocument(project.id, timeline.id, (doc) => {
    if (opts.fps60) doc.settings.rate = { num: 60, den: 1 };
    const base = doc.tracks.find((t) => t.kind === 'video')!;
    const audioBase = doc.tracks.find((t) => t.kind === 'audio')!;
    const clone = (t: Track, nameText: string, items: Item[]): Track => ({ ...structuredClone(t), id: crypto.randomUUID(), name: nameText, items });
    const first = fileIds.a;
    const second = fileIds.b;
    const v1: Item[] = [
      mediaClip(crypto.randomUUID(), 'perf-a.mp4', first, 0, tIn),
      { type: 'transition', id: crypto.randomUUID(), name: 'Dissolve', recordIn: tIn, recordOut: tOut, transitionType: 'dissolve', easing: 'linear', enabled: true },
      mediaClip(crypto.randomUUID(), 'perf-b.mp4', second, tOut, end),
    ];
    const v2: Item[] = [
      mediaClip(crypto.randomUUID(), 'perf-b.mp4 pip', second, 0, end, {
        sourceIn: 60 * k,
        transform: { positionX: { value: 0.28 }, positionY: { value: -0.25 }, scale: { value: 0.4 }, rotation: null, opacity: null },
      }),
    ];
    const v3: Item[] = [
      { type: 'titleClip', id: crypto.randomUUID(), name: 'Title', recordIn: 30 * k, recordOut: end - 30 * k, text: 'Playback perf', fontSize: 72, color: '#ffffff', enabled: true },
    ];
    base.items = v1;
    const v2Track = clone(base, 'V2', v2);
    const v3Track = clone(base, 'V3', v3);
    const a1 = audioBase;
    a1.items = [mediaClip(crypto.randomUUID(), 'perf-a.mp4 audio', fileIds.a, 0, tIn), mediaClip(crypto.randomUUID(), 'perf-b.mp4 audio', fileIds.b, tOut, end)];
    const music = clone(audioBase, 'Music', [mediaClip(crypto.randomUUID(), 'perf-music.wav', fileIds.music, 0, end, { gainDb: { value: -12 } })]);
    doc.tracks = [base, v2Track, v3Track, a1, music];
  });
  return { projectId: project.id, timelineId: timeline.id, fileIds };
}
