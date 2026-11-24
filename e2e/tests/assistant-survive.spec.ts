import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http';
import type { AddressInfo } from 'node:net';
import { test as base, expect, type Page } from '@playwright/test';

/**
 * Assistant replies and permission cards survive a screen change on a phone.
 *
 * What it does: the browser (a Pixel 5 viewport) talks to a mock of the assistant API that behaves like the
 * fixed server — it keeps a turn and its permission card alive when the connection drops, reports the turn on
 * `GET …/turn`, replays the card on `POST …/chat {attach: true}`, and runs the "tool" only when
 * `POST …/approvals/{id}` says allow. Everything else under `/api/v1` is answered 404, which the pages tolerate.
 *
 * How the mock is reached: the page's own `/api/v1/assistant/**` requests are redirected to the mock with
 * `route.continue({ url })` (the mock sends the CORS headers), so the real client code — the runtime, the
 * re-attach, the resume-on-return — runs unchanged against it.
 *
 * Run:   cd web && npx next build && npx next start -p 3210   (separate shell)
 *        cd e2e && npx playwright test -c playwright.assistant-survive.config.ts
 *
 * Use a PRODUCTION build: under `next dev` React StrictMode mounts effects twice and the re-attach's one-shot
 * `startRun` (TurnReattach) is lost, so the re-attach never fires in dev (a dev-only artifact, not the shipped behaviour).
 */

const THREAD = '0f0f0f0f-1111-4222-8333-444444444444';
const CARD_ID = 'c0ffee00-1111-4222-8333-555555555555';
const TITLE = 'Delete the file “notes.txt”';

const FIXTURE_USER = { email: 'qa-e2e-fixture@example.invalid', isAdmin: true, mustChangePassword: false };
const cookies = ['127.0.0.1', 'localhost'].flatMap((domain) => [
  { name: 'reelbolt_token', value: 'qa-e2e-fixture-not-a-real-jwt', domain, path: '/', expires: -1, httpOnly: false, secure: false, sameSite: 'Lax' as const },
  { name: 'reelbolt_user', value: encodeURIComponent(JSON.stringify(FIXTURE_USER)), domain, path: '/', expires: -1, httpOnly: false, secure: false, sameSite: 'Lax' as const },
]);

const card = (state: string, extra: Record<string, unknown> = {}) => ({
  type: 'data-assistantAction',
  data: {
    id: CARD_ID,
    state,
    mode: 'ask',
    kind: 'file',
    risk: 'destructive',
    title: TITLE,
    summary: 'It will be removed from this project.',
    changes: [{ kind: 'remove', text: 'notes.txt' }],
    ...extra,
  },
});
const sse = (payload: unknown) => `data: ${JSON.stringify(payload)}\n\n`;

/** An in-process stand-in for the server side of the fix: one turn that outlives its connections. */
class MockAssistant {
  server!: Server;
  port = 0;
  /** What `GET …/turn` reports. */
  turnState: 'idle' | 'awaiting_approval' | 'running' | 'completed' = 'idle';
  /** The stored history (`GET …/threads/{id}`). */
  messages: Array<{ id: string; role: string; content: string; sequence: number; createdAt: string }> = [];
  /** Open chat streams, so the test can drop them like a phone switching screens does. */
  private streams = new Set<ServerResponse>();
  approvals: Array<{ id: string; decision: string }> = [];
  chatBodies: Array<Record<string, unknown>> = [];
  toolRan = false;

  async start(): Promise<void> {
    this.server = createServer((req, res) => void this.handle(req, res));
    await new Promise<void>((resolve) => this.server.listen(0, '127.0.0.1', resolve));
    this.port = (this.server.address() as AddressInfo).port;
  }

  async stop(): Promise<void> {
    this.dropConnections();
    await new Promise<void>((resolve) => this.server.close(() => resolve()));
  }

  /** The phone's connection dies; the turn (and its card) live on in the mock, as they do on the server. */
  dropConnections(): void {
    for (const res of this.streams) res.destroy();
    this.streams.clear();
  }

  private cors(req: IncomingMessage, res: ServerResponse): void {
    res.setHeader('Access-Control-Allow-Origin', String(req.headers.origin ?? '*'));
    res.setHeader('Access-Control-Allow-Credentials', 'true');
    res.setHeader('Access-Control-Allow-Headers', 'content-type, authorization');
    res.setHeader('Access-Control-Allow-Methods', 'GET, POST, PUT, DELETE, OPTIONS');
  }

  private json(req: IncomingMessage, res: ServerResponse, body: unknown, status = 200): void {
    this.cors(req, res);
    res.writeHead(status, { 'content-type': 'application/json' });
    res.end(JSON.stringify(body));
  }

  private async readBody(req: IncomingMessage): Promise<string> {
    const chunks: Buffer[] = [];
    for await (const chunk of req) chunks.push(chunk as Buffer);
    return Buffer.concat(chunks).toString('utf8');
  }

  private async handle(req: IncomingMessage, res: ServerResponse): Promise<void> {
    const url = new URL(req.url ?? '/', 'http://mock');
    const path = url.pathname;
    if (req.method === 'OPTIONS') {
      this.cors(req, res);
      res.writeHead(204);
      res.end();
      return;
    }

    if (path === '/api/v1/assistant/chat' && req.method === 'POST') {
      const body = JSON.parse((await this.readBody(req)) || '{}') as Record<string, unknown>;
      this.chatBodies.push(body);
      this.cors(req, res);
      res.writeHead(200, { 'content-type': 'text/event-stream', 'x-vercel-ai-ui-message-stream': 'v1', 'x-assistant-thread-id': THREAD });
      this.streams.add(res);
      res.on('close', () => this.streams.delete(res));
      res.write(sse({ type: 'start', messageId: 'm2' }));
      res.write(sse({ type: 'text-start', id: 't1' }));
      // A new turn and an attach both show the open card; the card is held until it is answered.
      res.write(sse(card('pending')));
      this.turnState = 'awaiting_approval';
      if (!this.messages.some((m) => m.role === 'user')) {
        this.messages.push({ id: 'u1', role: 'user', content: String(extractText(body) ?? 'Delete notes.txt'), sequence: 1, createdAt: '' });
      }
      return;
    }

    if (path === `/api/v1/assistant/approvals/${CARD_ID}` && req.method === 'POST') {
      const body = JSON.parse((await this.readBody(req)) || '{}') as { decision?: string };
      this.approvals.push({ id: CARD_ID, decision: String(body.decision) });
      if (this.turnState !== 'awaiting_approval') return this.json(req, res, { error: 'gone' }, 404);
      const allow = body.decision === 'allow';
      this.toolRan = allow;
      this.turnState = 'completed';
      const text = allow ? 'I deleted notes.txt.' : 'Okay, I left it as it was.';
      this.messages.push({ id: 'a1', role: 'assistant', content: text, sequence: 2, createdAt: '' });
      for (const stream of this.streams) {
        stream.write(sse(card(allow ? 'allowed' : 'declined')));
        if (allow) stream.write(sse(card('done', { outcome: 'Deleted.' })));
        stream.write(sse({ type: 'text-delta', textDelta: text }));
        stream.write(sse({ type: 'text-end' }));
        stream.write(sse({ type: 'finish', finishReason: 'stop', usage: { inputTokens: 0, outputTokens: 0 } }));
        stream.write('data: [DONE]\n\n');
        stream.end();
      }
      this.streams.clear();
      this.cors(req, res);
      res.writeHead(204);
      res.end();
      return;
    }

    if (path === '/api/v1/assistant/threads' && req.method === 'POST') return this.json(req, res, { id: THREAD }, 201);
    if (path === '/api/v1/assistant/threads') {
      return this.json(req, res, [{ id: THREAD, title: 'Delete notes.txt', projectId: null, updatedAt: new Date().toISOString(), messageCount: this.messages.length }]);
    }
    if (path === `/api/v1/assistant/threads/${THREAD}/turn`) {
      return this.json(req, res, this.turnState === 'awaiting_approval'
        ? { state: 'awaiting_approval', turnId: 't1', messageId: 'm2', pendingApprovalId: CARD_ID }
        : { state: this.turnState });
    }
    if (path === `/api/v1/assistant/threads/${THREAD}`) {
      return this.json(req, res, {
        id: THREAD,
        title: 'Delete notes.txt',
        projectId: null,
        messages: this.messages,
        replyInProgress: this.turnState === 'awaiting_approval',
      });
    }
    if (path === '/api/v1/assistant/preferences') return this.json(req, res, { approvalMode: 'ask' });
    if (path === '/api/v1/assistant/activity') return this.json(req, res, { turns: [], unread: 0, pendingApprovals: 0 });
    return this.json(req, res, { error: 'not mocked' }, 404);
  }
}

function extractText(body: Record<string, unknown>): string | undefined {
  const messages = body.messages as Array<{ role?: string; parts?: Array<{ text?: string }>; content?: unknown }> | undefined;
  const last = messages?.filter((m) => m.role === 'user').at(-1);
  return last?.parts?.map((p) => p.text).filter(Boolean).join('') ?? (typeof last?.content === 'string' ? last.content : undefined);
}

const test = base.extend<{ mock: MockAssistant }>({
  mock: async ({ page }, use) => {
    const mock = new MockAssistant();
    await mock.start();
    // The page's own assistant calls go to the mock; the mock sets the CORS headers the browser asks for.
    await page.route('**/api/v1/assistant/**', (route) => {
      const original = new URL(route.request().url());
      return route.continue({ url: `http://127.0.0.1:${mock.port}${original.pathname}${original.search}` });
    });
    await page.route(/\/api\/v1\/(?!assistant\/)/, (route) => route.fulfill({ status: 404, contentType: 'application/json', body: '{}' }));
    await use(mock);
    await mock.stop();
  },
});

test.use({ storageState: { cookies, origins: [] } });

async function openAssistantAndAsk(page: Page): Promise<void> {
  await page.goto('/app/projects', { waitUntil: 'domcontentloaded' });
  const dialog = page.getByRole('dialog');
  for (let attempt = 0; attempt < 4; attempt += 1) {
    await page.getByRole('button', { name: 'Open assistant', exact: true }).click();
    try {
      await expect(dialog).toBeVisible({ timeout: 5_000 });
      break;
    } catch {
      // hydration race: the click landed before React attached the handler; try again
    }
  }
  const composer = dialog.getByRole('textbox', { name: 'Message the workflow assistant' });
  await expect(composer).toBeEnabled({ timeout: 20_000 });
  await composer.fill('Delete notes.txt');
  await composer.press('Enter');
  await expect(page.getByRole('button', { name: 'Allow', exact: true })).toBeVisible();
}

/** Backgrounds the page and brings it back, like switching apps on a phone. */
async function backgroundThenReturn(page: Page): Promise<void> {
  await page.evaluate(() => {
    const set = (hidden: boolean) => {
      Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden });
      Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => (hidden ? 'hidden' : 'visible') });
      document.dispatchEvent(new Event('visibilitychange'));
    };
    (window as unknown as { __setHidden: (h: boolean) => void }).__setHidden = set;
    set(true);
  });
  await page.waitForTimeout(3_500);
}

test('a card left open by a dropped connection comes back and can still be answered', async ({ page, mock }) => {
  await openAssistantAndAsk(page);
  expect(mock.chatBodies).toHaveLength(1);

  // The phone switches screens: the connection is dropped (the server keeps the turn and the card).
  await backgroundThenReturn(page);
  mock.dropConnections();
  await page.evaluate(() => (window as unknown as { __setHidden: (h: boolean) => void }).__setHidden(false));

  // The screen reconnects by itself, without sending the question again, and the same card is open again.
  await expect.poll(() => mock.chatBodies.length, { timeout: 20_000 }).toBeGreaterThanOrEqual(2);
  expect(mock.chatBodies[1].attach).toBe(true);
  expect(mock.chatBodies[1].messages).toEqual([]);
  const allow = page.getByRole('button', { name: 'Allow', exact: true });
  await expect(allow).toBeVisible();
  await expect(page.getByText(/timed out|ran out of time/i)).toHaveCount(0);

  // Answering it from the re-attached screen runs the tool and the reply arrives.
  await allow.click();
  await expect.poll(() => mock.approvals.map((a) => a.decision)).toEqual(['allow']);
  await expect(page.getByText('I deleted notes.txt.', { exact: true })).toBeVisible();
  expect(mock.toolRan).toBe(true);
});

test('navigating away and back (a full page load) re-attaches to the open card', async ({ page, mock }) => {
  await openAssistantAndAsk(page);

  // A full navigation: the old page and its stream are gone; the server still holds the turn.
  mock.dropConnections();
  await page.goto('/app/projects', { waitUntil: 'domcontentloaded' });

  const dialog = page.getByRole('dialog');
  for (let attempt = 0; attempt < 4; attempt += 1) {
    await page.getByRole('button', { name: 'Open assistant', exact: true }).click();
    try {
      await expect(dialog).toBeVisible({ timeout: 5_000 });
      break;
    } catch {
      // hydration race, as above
    }
  }

  const allow = page.getByRole('button', { name: 'Allow', exact: true });
  await expect(allow).toBeVisible({ timeout: 20_000 });
  expect(mock.chatBodies.at(-1)?.attach).toBe(true);
  await allow.click();
  await expect.poll(() => mock.approvals.length).toBe(1);
  expect(mock.toolRan).toBe(true);
});
