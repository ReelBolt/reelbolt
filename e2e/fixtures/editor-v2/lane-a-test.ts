import { expect, request as pwRequest } from '@playwright/test';
import * as fs from 'fs';
import { EditorApi } from './api';
import { ignoreHTTPSErrorsFor, storageStatePath } from './env';
import { test as baseTest } from './index';
import { createSeed, type Seed } from './seed';

export { expect };
export type { EditorPage } from './editor-page';

/**
 * The lane specs' `test`: the same fixtures as `./index`, but the per-worker seed project is KEPT and
 * reused by name (`lane-seed-<project>-w<parallelIndex>`) instead of being created from scratch for every
 * run and every restarted worker. Building the seed (five uploads, proxies, a derush workflow) is by far
 * the slowest step on a busy machine; tests never edit the seed timelines (they duplicate them), so a
 * kept project is safe to share between runs. A missing or incomplete project is created afresh.
 */
export const test = baseTest.extend<object, { seed: Seed }>({
  seed: [
    async ({}, use, workerInfo) => {
      const baseURL = workerInfo.project.use.baseURL as string;
      const state = storageStatePath(baseURL);
      expect(fs.existsSync(state), 'global setup wrote the session').toBeTruthy();
      const request = await pwRequest.newContext({ baseURL, storageState: state, ignoreHTTPSErrors: ignoreHTTPSErrorsFor(baseURL) });
      const api = new EditorApi(request);
      const name = `lane-seed-${workerInfo.project.name}-w${workerInfo.parallelIndex}`;
      const seed = (await reuse(api, name)) ?? (await createSeed(api, name));
      await use(seed);
      await request.dispose();
    },
    { scope: 'worker', timeout: 900_000 },
  ],
});

async function reuse(api: EditorApi, name: string): Promise<Seed | null> {
  const project = await api.findProjectByName(name);
  if (!project) return null;
  const timelines = await api.listTimelines(project.id);
  const byName = (n: string) => timelines.find((t) => t.name === n)?.id ?? '';
  const files = await api.listFiles(project.id);
  const file = (n: string) => files.find((f) => f.originalFileName === n)?.id ?? '';
  const seed: Seed = {
    projectId: project.id,
    projectName: project.name,
    files: { clipA: file('clipA.mp4'), clipB: file('clipB.mp4'), vertical: file('vertical.mp4'), still: file('still.png'), tone: file('tone.wav') },
    blankTimelineId: byName('remediation-blank'),
    seededTimelineId: byName('remediation-seeded'),
    derushTimelineId: byName('remediation-derush'),
    api,
    readonly: false,
  };
  const complete = Object.values(seed.files).every(Boolean) && seed.blankTimelineId && seed.seededTimelineId && seed.derushTimelineId;
  if (complete) return seed;
  // A half-built project of an interrupted run: remove it so the name is free for a clean one.
  await api.json('DELETE', `/api/v1/projects/${project.id}`, undefined, [200, 202, 204, 404]);
  return null;
}
