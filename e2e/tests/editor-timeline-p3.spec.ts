import { expect, test } from '@playwright/test';
import type { Page, Route } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { pinEditorShell } from '../fixtures/editor-shell';

/**
 * Wave B / P3 — the browser assertion for the editable timeline shell.
 *
 * ## What this file proves, and what it does not
 *
 * It asserts **what the user SEES**: the workspace opens a v2 document, the program viewer mounts a
 * playback session FOR THE TIMELINE (its kind is stated — `live`, `fallback` or `unavailable` — never
 * a black box), the dropped-frame counter is on screen from the first frame, playing advances
 * the status-bar timecode, a marker added through the panel appears and is removed again by undo, and
 * the autosave sends exactly ONE `PATCH` carrying `baseRevision`, the full document and the ops.
 *
 * ## The backend is STUBBED, and that is a stated limit
 *
 * `e2e/` has **no CI job** and this run does **not** talk to a live Inference API: the spec starts
 * against the `web` Next server and **intercepts every `/api/v1/**` request** with hand-written
 * fixture payloads matching `EditTimeline.schema.json` and the media-pool contract. The proxy bytes
 * are real (the committed 12 s 1920x1080 H.264 fixture), but the *contract* between the components
 * and the API is asserted against a fixture rather than against the server that would produce it.
 *
 * **What that leaves UNPROVEN:** that the real controller emits exactly these shapes, that a real
 * `PATCH` advances the revision and stores the ops, and that `409`/`400` behave as the client
 * assumes. Those are `EditTimelinesControllerTests` / `EditTimelineHistoryControllerTests`' job;
 * this spec is the **rendering** half and says so rather than implying an end-to-end pass.
 *
 * ## Why the session kind is asserted as a set, not a value
 *
 * Headless Chromium on a CI box may or may not expose WebCodecs + WebGL2 (SwiftShader) and may or may
 * not decode H.264. The compositor is specified to DEGRADE in words rather than fail, so the spec
 * asserts that the kind is one of the three documented values and that whichever it is, the
 * degradation UI is coherent with it (an `unavailable` session shows its reason; the others do not).
 */

const BASE_URL = process.env.BASE_URL || 'http://localhost';
const PROJECT_ID = '11111111-1111-1111-1111-111111111111';
const TIMELINE_ID = '66666666-6666-6666-6666-666666666666';
const EDITOR_PATH = `/app/editor/${PROJECT_ID}/timeline/${TIMELINE_ID}`;

const FILE_GUID = 'aaaaaaaa-0000-0000-0000-000000000001';
const PROXY_ROW_ID = '44444444-4444-4444-4444-444444444444';

/** The committed 12 s / 1920x1080 / 30 fps spike fixture — a real, playable H.264 file. */
const VIDEO_FIXTURE = path.join(
  __dirname,
  '..',
  '..',
  'web',
  'lib',
  'editing',
  'preview',
  'spike',
  'fixtures',
  'proxy-1080p-12s.mp4',
);

// ─────────────────────────────────────────────────────────────────────────────
// Fixtures — shapes read off EditTimeline.schema.json, not invented
// ─────────────────────────────────────────────────────────────────────────────

const assetRef = { kind: 'projectFile', projectFileId: FILE_GUID };

const mediaClip = (id: string, recordIn: number, recordOut: number, sourceIn: number, rationale?: string) => ({
  id,
  type: 'mediaClip',
  name: id,
  recordIn,
  recordOut,
  assetRef,
  sourceIn,
  playbackRate: 1,
  enabled: true,
  provenance: rationale
    ? { origin: 'generated', rationale, editedByUser: false, agentName: 'VideoStoryEditor', providerName: 'vLLM' }
    : null,
});

const track = (id: string, kind: 'video' | 'audio', items: unknown[]) => ({
  id,
  kind,
  name: id,
  height: 64,
  locked: false,
  enabled: true,
  mute: false,
  solo: false,
  items,
});

/** 30 fps, 240 frames (8 s): three cuts on V1 and one continuous audio clip. */
const document = {
  schemaVersion: 2,
  id: TIMELINE_ID,
  projectId: PROJECT_ID,
  name: 'P3 e2e fixture',
  settings: {
    width: 1920,
    height: 1080,
    rate: { num: 30, den: 1 },
    sampleRate: 48000,
    pixelAspect: { num: 1, den: 1 },
    aspectPolicy: 'fit',
    startTimecodeFrames: 0,
  },
  tracks: [
    track('b1000000-0000-4000-8000-000000000005', 'video', [
      mediaClip('c1000000-0000-4000-8000-000000000001', 0, 60, 0, 'Opening shot keeps the hook.'),
      mediaClip('c2000000-0000-4000-8000-000000000002', 60, 150, 90),
      mediaClip('c3000000-0000-4000-8000-000000000003', 150, 240, 200),
    ]),
    track('b2000000-0000-4000-8000-000000000006', 'audio', [{ ...mediaClip('a1000000-0000-4000-8000-000000000004', 0, 240, 0), gainDb: { value: -6, keys: null } }]),
  ],
  markers: [{ id: 'd1000000-0000-4000-8000-000000000007', frame: 45, name: 'Hook', color: null, note: null, durationFrames: null }],
  inOut: null,
  seed: null,
};

const timelineResponse = (revision: number, doc: unknown = document) => ({
  id: TIMELINE_ID,
  projectId: PROJECT_ID,
  name: 'P3 e2e fixture',
  revision,
  document: doc,
  seedStepResultId: null,
  createdAt: '2026-10-01T10:00:00Z',
  updatedAt: '2026-10-01T10:00:00Z',
});

const derivatives = [
  {
    id: PROXY_ROW_ID,
    kind: 'proxy',
    mimeType: 'video/mp4',
    sizeBytes: 1234567,
    durationSec: 12,
    width: 960,
    height: 540,
    contentUrl: `/api/v1/projects/${PROJECT_ID}/media/derivatives/${PROXY_ROW_ID}/content`,
  },
];

interface PatchRecord {
  baseRevision: number;
  ops: unknown[];
  documentMarkerCount: number;
}

interface StubState {
  revision: number;
  patches: PatchRecord[];
  log: { method: string; pathname: string }[];
}

function fulfillVideo(route: Route) {
  const body = fs.readFileSync(VIDEO_FIXTURE);
  const range = route.request().headers()['range'];
  if (!range) {
    return route.fulfill({
      status: 200,
      contentType: 'video/mp4',
      headers: { 'Accept-Ranges': 'bytes', 'Content-Length': String(body.length) },
      body,
    });
  }
  const match = /bytes=(\d+)-(\d*)/.exec(range);
  if (!match) return route.fulfill({ status: 416, body: '' });
  const start = Number(match[1]);
  const end = match[2] ? Number(match[2]) : body.length - 1;
  const slice = body.subarray(start, end + 1);
  return route.fulfill({
    status: 206,
    contentType: 'video/mp4',
    headers: {
      'Accept-Ranges': 'bytes',
      'Content-Range': `bytes ${start}-${end}/${body.length}`,
      'Content-Length': String(slice.length),
    },
    body: slice,
  });
}

/**
 * Intercepts every `/api/v1/**` call the editor makes. The PATCH stub behaves like the real server
 * in the one respect the client depends on: it answers `200` with `revision + 1` when `baseRevision`
 * matches and `409 { currentRevision }` when it does not — and it records what was sent so the test
 * can assert the wire contract (full document + ops + baseRevision) rather than infer it.
 */
async function stubEditorApi(page: Page): Promise<StubState> {
  const state: StubState = { revision: 3, patches: [], log: [] };

  await page.route('**/api/v1/**', async (route: Route) => {
    const request = route.request();
    const pathname = new URL(request.url()).pathname;
    state.log.push({ method: request.method(), pathname });

    const json = (body: unknown, status = 200) =>
      route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });

    if (/\/media\/derivatives\/[^/]+\/content$/.test(pathname)) return fulfillVideo(route);
    if (/\/files\/[^/]+\/content$/.test(pathname)) return fulfillVideo(route);
    if (/\/media\/[^/]+\/derivatives$/.test(pathname)) return json(derivatives);
    if (pathname.endsWith('/media/bins')) return json([]);
    if (pathname.endsWith('/media')) return json([]);

    if (pathname.endsWith(`/edit-timelines/${TIMELINE_ID}/history`)) {
      return json({ id: TIMELINE_ID, currentRevision: state.revision, entries: [], restorableRevisions: [1, state.revision] });
    }

    if (pathname.endsWith(`/edit-timelines/${TIMELINE_ID}`)) {
      if (request.method() === 'GET') return json(timelineResponse(state.revision));
      if (request.method() === 'PATCH') {
        const body = JSON.parse(request.postData() ?? '{}') as {
          baseRevision: number;
          document: { markers?: unknown[] };
          ops: unknown[];
        };
        state.patches.push({
          baseRevision: body.baseRevision,
          ops: body.ops,
          documentMarkerCount: body.document?.markers?.length ?? -1,
        });
        if (body.baseRevision !== state.revision) {
          return json({ message: 'stale', currentRevision: state.revision }, 409);
        }
        state.revision += 1;
        return json(timelineResponse(state.revision, body.document));
      }
    }

    return json({}, 404);
  });

  return state;
}

async function openEditor(page: Page) {
  page.on('pageerror', (error) => {
    console.log(`[pageerror] ${error.message}`);
    console.log(error.stack ?? '(no stack)');
  });
  page.on('console', (message) => {
    if (message.type() === 'error') console.log(`[console.error] ${message.text()}`);
  });

  const user = encodeURIComponent(JSON.stringify({ email: 'qa@example.com', isAdmin: false, mustChangePassword: false }));
  await page.context().addCookies([
    { name: 'reelbolt_token', value: 'qa-stub-token', url: BASE_URL },
    { name: 'reelbolt_user', value: user, url: BASE_URL },
  ]);

  await page.goto(`${BASE_URL}${EDITOR_PATH}`);
  await expect(page.getByTestId('editor-workspace')).toBeVisible();
}

/** The left panel opens on Media (R05.8): the Markers tab is one tap away. Safe to call when it is already open. */
async function openMarkersTab(page: Page): Promise<void> {
  const tab = page.getByRole('tab', { name: 'Markers' });
  if ((await tab.getAttribute('aria-selected')) !== 'true') await tab.click();
  await expect(page.getByTestId('markers-panel')).toBeVisible();
}

// These specs are about the full editor's pages and panels.
test.beforeEach(async ({ context }) => {
  await pinEditorShell(context, 'full');
});

test.describe('P3 editable timeline shell (stubbed API)', () => {
  test.skip(({ isMobile }) => !!isMobile, 'The editor is a desktop surface; the mobile project is not a target here.');

  test('opens the document and mounts a stated playback session with the dropped-frame counter visible', async ({ page }) => {
    await stubEditorApi(page);
    await openEditor(page);

    // The document, not a stand-in: the name and revision come straight off the GET response.
    await expect(page.getByText('P3 e2e fixture')).toBeVisible();
    await expect(page.getByText('Version 3', { exact: true })).toBeVisible();

    // The viewer declares which session it got; nothing is ever an unlabelled black box.
    const viewer = page.getByTestId('program-viewer');
    await expect(viewer).toBeVisible();
    await expect(viewer).toHaveAttribute('data-session-kind', /^(live|fallback|unavailable)$/);
    const kind = await viewer.getAttribute('data-session-kind');
    // The status bar names the same kind in words — the two must agree.
    const sessionWords: Record<string, string> = { live: 'Live preview', fallback: 'Simple preview', unavailable: 'No preview' };
    await expect(page.getByTestId('session-kind')).toContainText(sessionWords[kind ?? ''] ?? 'never');
    if (kind === 'unavailable') {
      await expect(page.getByTestId('preview-unavailable')).toBeVisible();
    } else {
      await expect(page.getByTestId('preview-unavailable')).toHaveCount(0);
    }

    // The ledger is on screen from the first frame, and it starts at a measured zero.
    await expect(page.getByTestId('dropped-frames')).toContainText('Skipped frames: 0');

    // The timeline canvas painted and the markers/edit-index panels are reachable.
    await expect(page.getByTestId('timeline-canvas')).toBeVisible();
    await page.getByRole('tab', { name: 'Markers' }).click(); // R05.8: the left panel opens on Media
    await expect(page.getByTestId('markers-panel')).toBeVisible();
  });

  test('play advances the status-bar timecode and the parity badge is reported per frame', async ({ page }) => {
    await stubEditorApi(page);
    await openEditor(page);

    const timecode = page.getByTestId('status-timecode');
    await expect(timecode).toContainText('00:00:00:00');
    await expect(page.getByTestId('parity-badge')).toHaveAttribute('data-parity', /^(faithful|approximate|renderRequired)$/);

    const kind = await page.getByTestId('program-viewer').getAttribute('data-session-kind');
    await page.getByTestId('transport-play').click();
    if (kind === 'unavailable') {
      // Nothing can be driven: the session reports "not playing" at once and the transport returns
      // to Play rather than pretending. The timecode must NOT move — a moving clock over no media
      // would be exactly the invented number the spec forbids.
      await page.waitForTimeout(500);
      await expect(timecode).toContainText('00:00:00:00');
    } else {
      // A live or fallback session advances the playhead on play, so the timecode must leave zero.
      // A frame-exact value is not asserted: the clock is the machine's.
      // Generous: a shared CI box opens the demuxer and decoder under load before the first frame.
      await expect(timecode).not.toContainText('00:00:00:00', { timeout: 20000 });
      await page.keyboard.press('k');
    }

    // Marks follow the playhead: I/O at the current frame are UI state, so no PATCH goes out for them.
    await page.keyboard.press('i');
    await page.keyboard.press('o');
  });

  test('a marker added in the panel appears, undo removes it, and autosave sends one PATCH with baseRevision + document + ops', async ({ page }) => {
    const state = await stubEditorApi(page);
    await openEditor(page);
    await openMarkersTab(page);

    // The seeded marker and the rationale marker derived from c1's provenance are both listed.
    await expect(page.getByTestId('marker-row-d1000000-0000-4000-8000-000000000007')).toBeVisible();
    await expect(page.locator('[data-testid^="marker-row-rationale:"]')).toHaveCount(1);

    await page.getByTestId('marker-name-input').fill('Act two');
    await page.getByTestId('marker-add').click();
    await expect(page.getByText('Act two')).toBeVisible();
    await expect(page.locator('[data-testid^="marker-row-"][data-marker-kind="user"]')).toHaveCount(2);

    // Autosave: exactly ONE debounced PATCH for that one op, carrying the full post-edit document.
    await expect.poll(() => state.patches.length, { timeout: 5000 }).toBe(1);
    expect(state.patches[0].baseRevision).toBe(3);
    expect(state.patches[0].documentMarkerCount).toBe(2);
    expect(state.patches[0].ops).toHaveLength(1);
    expect((state.patches[0].ops[0] as { type: string }).type).toBe('addMarker');
    await expect(page.getByText('Version 4', { exact: true })).toBeVisible();

    // Undo is the inverse PROGRAM of the op, and it is itself a change that saves.
    await page.getByTestId('timeline-canvas').click({ position: { x: 10, y: 10 } });
    await page.keyboard.press('Control+z');
    await expect(page.locator('[data-testid^="marker-row-"][data-marker-kind="user"]')).toHaveCount(1);
    await expect(page.getByTestId('marker-row-d1000000-0000-4000-8000-000000000007')).toBeVisible();

    await expect.poll(() => state.patches.length, { timeout: 5000 }).toBe(2);
    expect(state.patches[1].baseRevision).toBe(4);
    expect(state.patches[1].documentMarkerCount).toBe(1);
    expect((state.patches[1].ops[0] as { type: string }).type).toBe('removeMarker');

    // No write went anywhere else.
    // The pool asks for the media's small copies on open (a POST to /media/derivatives/request): not a timeline write.
    const writes = state.log.filter((e) => e.method !== 'GET' && e.method !== 'HEAD' && !e.pathname.endsWith('/media/derivatives/request'));
    expect(writes.every((w) => w.method === 'PATCH' && w.pathname.endsWith(`/edit-timelines/${TIMELINE_ID}`))).toBe(true);
  });

  test('a stale baseRevision turns the editor read-only instead of overwriting', async ({ page }) => {
    const state = await stubEditorApi(page);
    await openEditor(page);
    await openMarkersTab(page);

    // Someone else saved meanwhile: the next PATCH's baseRevision (3) no longer matches.
    state.revision = 7;

    await page.getByTestId('marker-name-input').fill('Conflict');
    await page.getByTestId('marker-add').click();

    await expect.poll(() => state.patches.length, { timeout: 5000 }).toBe(1);
    await expect(page.getByTestId('read-only-chip')).toBeVisible();
    // The local edit is KEPT on screen (the user can still read what they did) but is never written:
    // exactly one attempt, no retry, no force-write.
    await expect(page.locator('[data-testid^="marker-row-"][data-marker-kind="user"]')).toHaveCount(2);
    await page.waitForTimeout(1500);
    expect(state.patches.length).toBe(1);

    // "Reload latest" fetches the server's revision-7 document; the unsaved marker is gone, nothing
    // was merged, and the editor is writable again.
    await page.getByTestId('conflict-use-latest').click();
    await expect(page.getByTestId('read-only-chip')).toHaveCount(0);
    await openMarkersTab(page);
    await expect(page.locator('[data-testid^="marker-row-"][data-marker-kind="user"]')).toHaveCount(1);
    await expect(page.getByText('Version 7', { exact: true })).toBeVisible();
  });
});
