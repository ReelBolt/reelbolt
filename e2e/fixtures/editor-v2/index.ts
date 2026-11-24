import { test as base, expect, request as pwRequest, type TestInfo } from '@playwright/test';
import * as fs from 'fs';
import { EditorApi } from './api';
import { READONLY, ignoreHTTPSErrorsFor, storageStatePath } from './env';
import { EditorPage } from './editor-page';
import { createSeed, findReadonlySeed, type Seed } from './seed';
import { pinEditorShell, type EditorShellChoice } from '../editor-shell';

export { expect };
export { EditorPage } from './editor-page';
export { SEEDED_LAYOUT } from './seed';
export type { Seed } from './seed';

type WorkerFixtures = { seed: Seed };
type TestFixtures = {
  /** Which editor shell the test runs in (default: the full editor). A spec about the Simple shell sets `test.use({ editorShell: 'simple' })`. */
  editorShell: EditorShellChoice;
  /** Auto: stores the shell choice before any page loads. */
  pinnedEditorShell: void;
  /** Auto: the suite only runs under playwright.editor-v2.config.ts (the shared config also globs tests/). */
  editorSuiteOnly: void;
  api: EditorApi;
  /** Duplicates one of the seed timelines for this test only (tests never edit the seed itself). */
  freshTimeline: (from: 'seeded' | 'derush' | 'blank') => Promise<string>;
  /** Opens a timeline in the editor and returns the page helper (PATCH log, geometry, input). */
  openEditor: (timelineId: string) => Promise<EditorPage>;
};

export const test = base.extend<TestFixtures, WorkerFixtures>({
  editorSuiteOnly: [
    async ({}, use) => {
      test.skip(process.env.EDITOR_E2E_SUITE !== '1', 'editor-v2 suite: run with -c playwright.editor-v2.config.ts');
      await use();
    },
    { auto: true },
  ],

  storageState: async ({ baseURL }, use) => {
    await use(storageStatePath(baseURL!));
  },

  editorShell: ['full', { option: true }],
  pinnedEditorShell: [
    async ({ context, editorShell }, use) => {
      await pinEditorShell(context, editorShell);
      await use();
    },
    { auto: true },
  ],

  seed: [
    async ({}, use, workerInfo) => {
      if (process.env.EDITOR_E2E_SUITE !== '1') {
        // Under the shared config: nothing to seed, every test skips itself (editorSuiteOnly).
        await use(undefined as unknown as Seed);
        return;
      }
      const baseURL = workerInfo.project.use.baseURL as string;
      const state = storageStatePath(baseURL);
      expect(fs.existsSync(state), 'global setup wrote the session').toBeTruthy();
      const request = await pwRequest.newContext({ baseURL, storageState: state, ignoreHTTPSErrors: ignoreHTTPSErrorsFor(baseURL) });
      const api = new EditorApi(request);
      const run = process.env.EDITOR_E2E_RUN ?? 'run';
      const seed = READONLY
        ? await findReadonlySeed(api)
        : await createSeed(api, `remediation-${run}-${workerInfo.project.name}-w${workerInfo.workerIndex}`);
      await use(seed);
      await request.dispose();
    },
    { scope: 'worker', timeout: 420_000 },
  ],

  api: async ({ seed }, use) => {
    await use(seed.api);
  },

  freshTimeline: async ({ seed }, use, testInfo) => {
    await use(async (from) => {
      if (seed.readonly) throw new Error('freshTimeline writes; not available in read-only mode');
      const source = from === 'seeded' ? seed.seededTimelineId : from === 'derush' ? seed.derushTimelineId : seed.blankTimelineId;
      const tl = await seed.api.duplicateTimeline(seed.projectId, source, `rem-${shortTitle(testInfo)}`);
      return tl.id;
    });
  },

  openEditor: async ({ page, seed }, use, testInfo) => {
    await use(async (timelineId) => EditorPage.open(page, seed, timelineId, testInfo.project.name));
  },
});

function shortTitle(info: TestInfo): string {
  return `${info.title.split(' ')[0]}-${info.project.name}`.slice(0, 60);
}
