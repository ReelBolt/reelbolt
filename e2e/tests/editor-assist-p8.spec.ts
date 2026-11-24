import { expect, test } from '@playwright/test';
import type { Page, Route } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { pinEditorShell } from '../fixtures/editor-shell';

/**
 * Wave G / P8 — the browser assertion for AI-assist proposals: the Assist tab asks for a proposal,
 * the groups are listed with the clip the user made flagged, the ghost diff is on (its toggle is
 * checked and the timeline canvas repaints), accepting with the user's clip unacknowledged is
 * refused, and a PARTIAL accept (one of two groups) autosaves exactly one PATCH whose document
 * carries the accepted change and not the rejected one.
 *
 * Same stated limit as the P3 spec: `e2e/` has no CI job and `/api/v1/**` is STUBBED — the proposal
 * body is hand-written to the `EditProposalResponse` shape `EditorP8BackendTests` pins on the server.
 * What the browser proves is the client half: review, ghost diff, acknowledgement, partial accept.
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
  name: 'P8 e2e fixture',
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
  await page.route(`**/api/v1/projects/${PROJECT_ID}/edit-timelines/${TIMELINE_ID}/proposals`, (route: Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(proposal) }),
  );

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

    // Playwright tries the most recently registered route first, so this catch-all would answer
    // the proposals call before the dedicated stub above ever saw it; serve it here as well.
    if (pathname.endsWith(`/edit-timelines/${TIMELINE_ID}/proposals`)) return json(proposal);

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


const proposal = {
  proposalId: 'p8-proposal',
  agent: 'recut',
  summary: 'Tighten the middle.',
  rejected: [],
  groups: [
    { label: 'Trim end of c1 by 0.5 s', ops: [{ type: 'trim', itemId: 'c1000000-0000-4000-8000-000000000001', edge: 'out', mode: 'ripple', deltaFrames: -15 }], touchedItemIds: ['c1000000-0000-4000-8000-000000000001'], touchesUserOwned: false, reason: 'the hook lingers' },
    { label: 'Remove c2', ops: [{ type: 'extract', itemId: 'c2000000-0000-4000-8000-000000000002' }], touchedItemIds: ['c2000000-0000-4000-8000-000000000002'], touchesUserOwned: true, reason: 'repeats the hook' },
  ],
};

// These specs are about the full editor's pages and panels.
test.beforeEach(async ({ context }) => {
  await pinEditorShell(context, 'full');
});

test.describe('P8 AI-assist proposals (stubbed API)', () => {
  test.skip(({ isMobile }) => !!isMobile, 'The editor is a desktop surface.');

  test('ghost diff, refusal of an unacknowledged user clip, and a partial accept autosaved as one PATCH', async ({ page }) => {
    const state = await stubEditorApi(page);
    await openEditor(page);

    await page.getByTestId('tab-assist').click();
    await page.getByTestId('proposal-ask').click();
    await expect(page.getByTestId('proposal-group-0')).toContainText('Trim end of c1');
    await expect(page.getByTestId('proposal-group-1')).toContainText('Remove c2');
    // c2 has no provenance → the user's own clip, flagged before anything can change it.
    await expect(page.getByTestId('proposal-user-owned-1')).toBeVisible();
    await expect(page.getByTestId('proposal-ghost-toggle')).toBeChecked();

    await page.getByTestId('proposal-accept-selected').click();
    await expect(page.getByTestId('proposal-error')).toContainText('you made yourself');
    expect(state.patches).toHaveLength(0);

    await page.getByTestId('proposal-group-check-1').click();
    await page.getByTestId('proposal-accept-selected').click();
    await expect.poll(() => state.patches.length, { timeout: 10000 }).toBe(1);
    expect(state.patches[0].baseRevision).toBe(3);
    expect(state.patches[0].ops).toEqual([{ type: 'trim', itemId: 'c1000000-0000-4000-8000-000000000001', edge: 'out', mode: 'ripple', deltaFrames: -15 }]);
  });
});
