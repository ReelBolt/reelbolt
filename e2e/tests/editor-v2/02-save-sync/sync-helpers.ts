import { createHash } from 'node:crypto';
import { expect, type Page } from '@playwright/test';
import type { Item, Timeline } from '../../../fixtures/editor-v2/api';
import { discriminatorFirst } from '../../../fixtures/editor-v2/api';
import type { EditorPage } from '../../../fixtures/editor-v2';
import { credentials } from '../../../fixtures/editor-v2/env';

/** Shared by the Wave 1 sync specs (T-004: R02.1 conflict recovery, R02.2 never lose an edit, R02.3 chip, R02.5 view-only). */

/** The app's storage scope (T-006): sha256 of the lower-cased email, first 16 hex characters. Never the email itself. */
const userScope = (email: string): string => createHash('sha256').update(email.trim().toLowerCase(), 'utf8').digest('hex').slice(0, 16);

/** Journal entries are per user (T-014): the key carries the signed-in user's scope (T-006), here the suite's own account. */
export const journalKey = (timelineId: string): string => {
  const base = (process.env.EDITOR_E2E_BASE_URL ?? 'http://localhost:18080').replace(/\/+$/, '');
  return `reelbolt:editor:journal:${userScope(credentials(base).email)}:${timelineId}`;
};

export const items = (tl: Timeline): Item[] => tl.document.tracks.flatMap((t) => t.items);
export const videoItems = (tl: Timeline): Item[] => tl.document.tracks.find((t) => t.kind === 'video')!.items;
export const itemCount = (tl: Timeline): number => items(tl).length;

/** True when a clip boundary sits at `frame` on the video track (a split made there). */
export const hasCutAt = (tl: Timeline, frame: number): boolean => videoItems(tl).some((i) => i.recordIn === frame);

/** Splits the clip under the playhead (the visible Split button: the toolbar scissors on desktop and tablet, the action bar's Split on a phone). */
export async function bladeAt(ed: EditorPage, frame: number): Promise<void> {
  await ed.seek(frame);
  await ed.selectClipUnderPlayhead();
  await ed.page.evaluate(() => {
    const visible = [...document.querySelectorAll<HTMLElement>('[data-testid="blade-at-playhead"], [data-testid="phone-action-split"]')].find((e) => e.offsetParent !== null);
    if (!visible) throw new Error('no visible Split button');
    visible.click();
  });
}

/** Writes through the REST API the way another person's tab would: real ops AND the matching document. */
export async function otherWriterAddsMarker(ed: EditorPage, name: string, frame: number): Promise<Timeline> {
  const cur = await ed.serverTimeline();
  const doc = structuredClone(cur.document);
  const marker = { id: crypto.randomUUID(), frame, name, color: null, note: null, durationFrames: null };
  doc.markers = [...(doc.markers ?? []), marker];
  return ed.seed.api.json<Timeline>('PATCH', ed.seed.api.timelinesUrl(ed.seed.projectId, ed.timelineId), {
    baseRevision: cur.revision,
    ops: [{ type: 'addMarker', marker }],
    document: discriminatorFirst(doc),
  });
}

/** Another writer renames a video clip (touches that clip only). */
export async function otherWriterRenames(ed: EditorPage, itemIndex: number, name: string): Promise<Timeline> {
  const cur = await ed.serverTimeline();
  const doc = structuredClone(cur.document);
  const clip = doc.tracks.find((t) => t.kind === 'video')!.items[itemIndex];
  clip.name = name;
  return ed.seed.api.json<Timeline>('PATCH', ed.seed.api.timelinesUrl(ed.seed.projectId, ed.timelineId), {
    baseRevision: cur.revision,
    ops: [{ type: 'setProperty', itemId: clip.id, key: 'name', value: name }],
    document: discriminatorFirst(doc),
  });
}

/** Phone tap targets are at least 44 px (the Wave 1 bar). */
export async function expectTapTarget(locator: ReturnType<Page['getByTestId']>, what: string): Promise<void> {
  const box = await locator.boundingBox();
  expect(box, `${what} is laid out`).not.toBeNull();
  expect(box!.height, `${what} is at least 44 px tall`).toBeGreaterThanOrEqual(44);
}

/** What a stubbed PATCH answers: an HTTP answer, or a dead network (`fetch` itself rejects). */
export type PatchAnswer = { status: number; body?: unknown } | 'network-failure';

/**
 * Answers this timeline's PATCHes from inside the page, in order (the first PATCH gets `answers[0]`, ...); once the
 * list is used up - or `heal()` was called - PATCHes go through to the server. `repeatLast` keeps answering with the
 * last entry instead. It stubs `fetch` itself rather than using Playwright routes for two reasons: the keepalive
 * flush a closing page makes bypasses routes, and Playwright WebKit does not run a route handler for the editor's
 * own PATCH. Only the page that called this is affected; a page opened afterwards has a working network.
 */
export async function stubPatches(page: Page, timelineId: string, answers: PatchAnswer[], options: { repeatLast?: boolean } = {}): Promise<{ heal: () => Promise<void>; count: () => Promise<number> }> {
  await page.evaluate(({ id, list, repeatLast }) => {
    const w = window as unknown as { __patchStub?: { n: number; healed: boolean } };
    const state = (w.__patchStub = { n: 0, healed: false });
    const original = window.fetch.bind(window);
    window.fetch = (input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method !== 'PATCH' || !String(input).includes(`/edit-timelines/${id}`) || state.healed) return original(input, init);
      const index = state.n++;
      const answer = index < list.length ? list[index] : repeatLast ? list[list.length - 1] : undefined;
      if (!answer) return original(input, init);
      if (answer === 'network-failure') return Promise.reject(new TypeError('Failed to fetch'));
      return Promise.resolve(new Response(JSON.stringify(answer.body ?? {}), { status: answer.status, headers: { 'Content-Type': 'application/json' } }));
    };
  }, { id: timelineId, list: answers, repeatLast: options.repeatLast === true });
  return {
    heal: () => page.evaluate(() => { (window as unknown as { __patchStub: { healed: boolean } }).__patchStub.healed = true; }),
    count: () => page.evaluate(() => (window as unknown as { __patchStub: { n: number } }).__patchStub.n),
  };
}

/** Closes the tab the way a killed tab goes: no `beforeunload` prompt, nothing to wait for. */
export async function killTab(page: Page): Promise<void> {
  await page.close({ runBeforeUnload: false });
}
