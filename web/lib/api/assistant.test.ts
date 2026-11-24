/**
 * The assistant's transport surface: the chat stream URL, and the thread CRUD behind the
 * conversation history. `fetch` is stubbed in every test — nothing here needs the backend running.
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  createThread,
  deleteThread,
  getAssistantChatStreamUrl,
  getAssistantPreferences,
  getThread,
  getTurnStatus,
  listThreads,
  renameThread,
  respondToApproval,
  setAssistantPreferences,
} from './assistant';
import { ApiError } from './client';

describe('getAssistantChatStreamUrl', () => {
  it('returns the context-first endpoint with no arguments', () => {
    expect(getAssistantChatStreamUrl()).toBe('/api/v1/assistant/chat');
  });

  it('takes no arguments at all — the project is context, not route state', () => {
    expect(getAssistantChatStreamUrl.length).toBe(0);
  });

  it('never throws, so a project-less page can open the assistant', () => {
    expect(() => getAssistantChatStreamUrl()).not.toThrow();
  });

  it('A1: has exactly one caller left in web/, and it is the runtime provider', () => {
    const webRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');

    const callers = sourceFiles(webRoot)
      .filter((file) => readFileSync(file, 'utf8').includes('getAssistantChatStreamUrl'))
      .map((file) => path.relative(webRoot, file).split(path.sep).join('/'));

    // The definition, a doc comment in this file's subject, and the one place that builds the
    // runtime. No page, and no path helper, may reach for it.
    expect(callers).toEqual([
      'components/assistant/AssistantProvider.tsx',
      'lib/api/assistant.ts',
    ]);
  });
});

/** Every .ts/.tsx file under `dir`, minus the tests themselves and the build output. */
function sourceFiles(dir: string): string[] {
  const out: string[] = [];
  for (const entry of readdirSync(dir)) {
    if (entry === 'node_modules' || entry === '.next' || entry === 'out') continue;
    const full = path.join(dir, entry);
    if (statSync(full).isDirectory()) out.push(...sourceFiles(full));
    else if (/\.tsx?$/.test(entry) && !/\.test\.tsx?$/.test(entry)) out.push(full);
  }
  return out;
}

interface RecordedCall {
  url: string;
  method: string;
  body: unknown;
}

const THREADS_URL = '/api/v1/assistant/threads';

let calls: RecordedCall[] = [];
let respond: (call: RecordedCall) => unknown = () => ({});

function installFetch() {
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input, init) => {
      const call: RecordedCall = {
        url: String(input),
        method: init?.method ?? 'GET',
        body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined,
      };
      calls.push(call);
      return Promise.resolve(
        new Response(JSON.stringify(respond(call) ?? null), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        }),
      );
    }),
  );
}

beforeEach(() => {
  calls = [];
  respond = () => ({});
  installFetch();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('assistant thread client', () => {
  it('A2: listThreads GETs the caller’s threads', async () => {
    respond = () => [
      { id: 'thread-1', title: 'First', projectId: null, updatedAt: '2024-01-01T00:00:00Z' },
    ];

    const threads = await listThreads();

    expect(calls).toEqual([{ url: THREADS_URL, method: 'GET', body: undefined }]);
    expect(threads).toEqual([
      { id: 'thread-1', title: 'First', projectId: null, updatedAt: '2024-01-01T00:00:00Z' },
    ]);
  });

  it('A2: createThread POSTs an empty body when it is given no context', async () => {
    respond = () => ({ id: 'thread-2' });

    await expect(createThread()).resolves.toEqual({ id: 'thread-2' });

    expect(calls).toEqual([{ url: THREADS_URL, method: 'POST', body: {} }]);
  });

  it('A2: createThread POSTs only the context it was given', async () => {
    respond = () => ({ id: 'thread-3' });

    await createThread({ projectId: '12345678-1234-1234-1234-123456789012', title: 'Kickoff' });

    expect(calls).toEqual([
      {
        url: THREADS_URL,
        method: 'POST',
        body: { projectId: '12345678-1234-1234-1234-123456789012', title: 'Kickoff' },
      },
    ]);
  });

  it('A2: getThread GETs one thread with its history', async () => {
    respond = () => ({ id: 'thread-4', title: 'Four', projectId: null, messages: [] });

    await expect(getThread('thread-4')).resolves.toEqual({
      id: 'thread-4',
      title: 'Four',
      projectId: null,
      messages: [],
    });

    expect(calls).toEqual([{ url: `${THREADS_URL}/thread-4`, method: 'GET', body: undefined }]);
  });

  it('A2: renameThread PATCHes { title } and nothing else', async () => {
    respond = () => ({});

    await renameThread('thread-5', 'Renamed');

    expect(calls).toEqual([
      { url: `${THREADS_URL}/thread-5`, method: 'PATCH', body: { title: 'Renamed' } },
    ]);
  });

  it('A2: deleteThread DELETEs the thread', async () => {
    await deleteThread('thread-6');

    expect(calls).toEqual([
      { url: `${THREADS_URL}/thread-6`, method: 'DELETE', body: undefined },
    ]);
  });
});

describe('getTurnStatus', () => {
  it('GETs the thread’s turn state', async () => {
    respond = () => ({ state: 'running', turnId: 'turn-1', messageId: 'msg-1' });

    await expect(getTurnStatus('thread-7')).resolves.toEqual({
      state: 'running',
      turnId: 'turn-1',
      messageId: 'msg-1',
    });

    expect(calls).toEqual([{ url: `${THREADS_URL}/thread-7/turn`, method: 'GET', body: undefined }]);
  });

  const answer = (status: number) =>
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>(() =>
        Promise.resolve(
          new Response(JSON.stringify({ error: 'no turn' }), {
            status,
            headers: { 'content-type': 'application/json' },
          }),
        ),
      ),
    );

  it.each([404, 500])(
    'reads a %i as "no live turn" (null), never a rejection — the stored history is the answer',
    async (status) => {
      answer(status);

      await expect(getTurnStatus('thread-8')).resolves.toBeNull();
    },
  );

  it('reads a transport failure the same way — nothing to attach to', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.reject(new Error('offline'))));

    await expect(getTurnStatus('thread-9')).resolves.toBeNull();
  });
});

describe('assistant preferences client', () => {
  const PREFS_URL = '/api/v1/assistant/preferences';

  it('GETs the stored mode', async () => {
    respond = () => ({ approvalMode: 'auto' });

    await expect(getAssistantPreferences()).resolves.toEqual({ approvalMode: 'auto' });

    expect(calls).toEqual([{ url: PREFS_URL, method: 'GET', body: undefined }]);
  });

  it.each([[{}], [{ approvalMode: 'sometimes' }], [null]])(
    'reads anything but an explicit auto as ask, so a bad answer never switches the checks off (%j)',
    async (answer) => {
      respond = () => answer;

      await expect(getAssistantPreferences()).resolves.toEqual({ approvalMode: 'ask' });
    },
  );

  it('PUTs { approvalMode } and answers with what the server stored, not what was asked for', async () => {
    respond = () => ({ approvalMode: 'ask' });

    await expect(setAssistantPreferences({ approvalMode: 'auto' })).resolves.toEqual({ approvalMode: 'ask' });

    expect(calls).toEqual([{ url: PREFS_URL, method: 'PUT', body: { approvalMode: 'auto' } }]);
  });
});

describe('respondToApproval', () => {
  const answer = (status: number, body?: unknown) =>
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>((input, init) => {
        calls.push({
          url: String(input),
          method: init?.method ?? 'GET',
          body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined,
        });
        return Promise.resolve(
          status === 204
            ? new Response(null, { status })
            : new Response(JSON.stringify(body ?? {}), { status, headers: { 'content-type': 'application/json' } }),
        );
      }),
    );

  it('POSTs the decision to the request, and resolves on 204', async () => {
    answer(204);

    await expect(respondToApproval('req-1', 'allow')).resolves.toBeUndefined();

    expect(calls).toEqual([
      { url: '/api/v1/assistant/approvals/req-1', method: 'POST', body: { decision: 'allow' } },
    ]);
  });

  it('sends alwaysAllow only when it is true', async () => {
    answer(204);

    await respondToApproval('req-2', 'allow', { alwaysAllow: true });
    await respondToApproval('req-3', 'deny', { alwaysAllow: false });

    expect(calls.map((c) => c.body)).toEqual([{ decision: 'allow', alwaysAllow: true }, { decision: 'deny' }]);
  });

  it('puts the id in the path as one segment', async () => {
    answer(204);

    await respondToApproval('a/b?c', 'deny');

    expect(calls[0].url).toBe('/api/v1/assistant/approvals/a%2Fb%3Fc');
  });

  it.each([404, 409, 400])('rejects with an ApiError carrying the %i', async (status) => {
    answer(status, { message: 'nope' });

    const failure = await respondToApproval('req-4', 'allow').catch((e: unknown) => e);

    expect(failure).toBeInstanceOf(ApiError);
    expect((failure as ApiError).status).toBe(status);
  });
});
