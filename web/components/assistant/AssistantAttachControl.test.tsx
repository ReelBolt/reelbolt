// @vitest-environment jsdom

/**
 * The composer's in-chat attach control.
 *
 * Driven through the REAL panel: the control sits in the composer bar, so the assertions that
 * matter — the turn landing in the thread, the composer surviving a failure, Enter still sending —
 * are only meaningful with the real `Composer`, the real `AssistantProvider` runtime and the shell
 * that supplies the page's project. The only things stubbed are the file upload (which is asserted
 * as a call, per the ticket) and the network.
 */

import { useEffect } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import type { ProjectFile } from '@/lib/types/project';

const mocks = vi.hoisted(() => ({ uploadFile: vi.fn() }));

// The advertised zip limit, stubbed so the control never fetches it.
vi.mock('@/lib/api/storage', () => ({ getStorageUsage: () => Promise.resolve({ zipUnpackLimits: { maxMb: 64 } }) }));

// The existing storage path, asserted rather than exercised: no test may need the backend.
vi.mock('@/lib/api/files', () => ({ uploadFile: mocks.uploadFile }));

import { AssistantProvider } from './AssistantProvider';
import { AssistantChatPanel } from './AssistantChatPanel';
import { AssistantShellProvider, useAssistant } from './AssistantShellProvider';
import {
  ATTACH_DISABLED_REASON,
  ATTACH_LABEL,
  buildAttachMessage,
  withoutAttachId,
} from './AssistantAttachControl';

const PROJECT_ID = '12345678-1234-1234-1234-123456789012';
const THREAD_ID = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
const FILE_ID = '9f8e7d6c-5b4a-4938-8271-60594a3b2c1d';
const CHAT_URL = '/api/v1/assistant/chat';

class MockResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

function makeFile(name: string): File {
  return new File(['hello'], name, { type: 'text/markdown' });
}

function projectFile(overrides: Partial<ProjectFile> = {}): ProjectFile {
  return {
    id: FILE_ID,
    projectId: PROJECT_ID,
    originalFileName: 'brief.md',
    category: 'userFiles',
    mimeType: 'text/markdown',
    sizeBytes: 5,
    storageKey: `projects/${PROJECT_ID}/userFiles/brief.md`,
    agentSummary: null,
    summaryStatus: 'Pending',
    indexingStatus: 'NotIndexed',
    uploadedAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (cause: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

const sseBody = (text: string) =>
  [
    'data: {"type":"start","messageId":"msg_1"}',
    '',
    'data: {"type":"text-start","id":"txt_1"}',
    '',
    `data: ${JSON.stringify({ type: 'text-delta', textDelta: text })}`,
    '',
    'data: {"type":"text-end"}',
    '',
    'data: {"type":"finish","finishReason":"stop","usage":{"inputTokens":0,"outputTokens":0}}',
    '',
    'data: [DONE]',
    '',
    '',
  ].join('\n');

const json = (payload: unknown) =>
  Promise.resolve(
    new Response(JSON.stringify(payload), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    }),
  );

let fetchMock: ReturnType<typeof vi.fn>;

/** Every request the runtime made to the assistant's chat stream. */
function chatCalls(): Array<[unknown, RequestInit | undefined]> {
  return fetchMock.mock.calls.filter(([input]) =>
    String(input).includes(CHAT_URL),
  ) as Array<[unknown, RequestInit | undefined]>;
}

function sentBodies(): string[] {
  return chatCalls().map(([, init]) => String(init?.body ?? ''));
}

beforeEach(() => {
  global.ResizeObserver = MockResizeObserver;
  global.matchMedia = (query: string): MediaQueryList =>
    ({
      matches: false,
      media: query,
      onchange: null,
      addListener: () => {},
      removeListener: () => {},
      addEventListener: () => {},
      removeEventListener: () => {},
      dispatchEvent: () => true,
    }) as MediaQueryList;
  Element.prototype.scrollTo = vi.fn();

  mocks.uploadFile.mockReset();

  fetchMock = vi.fn<typeof fetch>((input) => {
    const url = String(input);
    // Opening a thread loads its history; only the chat stream is an SSE response.
    if (url.includes('/api/v1/assistant/threads')) {
      return json({ id: THREAD_ID, title: 'New chat', projectId: PROJECT_ID, messages: [] });
    }
    return Promise.resolve(
      new Response(sseBody('Noted.'), {
        status: 200,
        headers: { 'content-type': 'text/event-stream', 'x-vercel-ai-ui-message-stream': 'v1' },
      }),
    );
  });
  global.fetch = fetchMock as unknown as typeof fetch;
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

/** Puts the page's project into the shell, exactly as a project page does. */
function SetProjectContext({ projectId }: { projectId?: string }) {
  const { setContext } = useAssistant();
  useEffect(() => {
    setContext(projectId ? { projectId } : {});
  }, [projectId, setContext]);
  return null;
}

function Harness({ projectId }: { projectId?: string }) {
  return (
    <MantineProvider env="test">
      <AssistantShellProvider>
        <SetProjectContext projectId={projectId} />
        <AssistantProvider
          threadId={THREAD_ID}
          context={projectId ? { projectId } : {}}
        >
          <AssistantChatPanel />
        </AssistantProvider>
      </AssistantShellProvider>
    </MantineProvider>
  );
}

const attachInput = () => screen.getByLabelText(ATTACH_LABEL) as HTMLInputElement;
const attachRoot = () => screen.getByTestId('assistant-attach');
const composerInput = () =>
  screen.getByRole('textbox', { name: 'Message the workflow assistant' });

async function attachFile(file: File) {
  fireEvent.change(attachInput(), { target: { files: [file] } });
}

describe('AssistantAttachControl', () => {
  it('a zip over the served limit opens a modal first; cancelling uploads nothing, continuing uploads it', async () => {
    const big = new File(['x'], 'code.zip', { type: 'application/zip' });
    Object.defineProperty(big, 'size', { value: 173 * 1024 * 1024 });
    mocks.uploadFile.mockResolvedValue(projectFile());
    render(<Harness projectId={PROJECT_ID} />);

    await attachFile(big);
    const modal = await screen.findByTestId('attach-zip-too-big-modal');
    expect(within(modal).getByText(/code\.zip/)).toBeTruthy();
    expect(within(modal).getByText(/64 MB/)).toBeTruthy();
    expect(mocks.uploadFile).not.toHaveBeenCalled();
    fireEvent.click(within(modal).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(mocks.uploadFile).not.toHaveBeenCalled();

    await attachFile(big);
    fireEvent.click(await screen.findByRole('button', { name: 'Attach anyway' }));
    await waitFor(() => expect(mocks.uploadFile).toHaveBeenCalledTimes(1));
    expect(mocks.uploadFile).toHaveBeenCalledWith(PROJECT_ID, big);
  });

  it('a zip is recognised by its MIME type even without a .zip name', async () => {
    const big = new File(['x'], 'code', { type: 'application/x-zip-compressed' });
    Object.defineProperty(big, 'size', { value: 100 * 1024 * 1024 });
    render(<Harness projectId={PROJECT_ID} />);
    await attachFile(big);
    await screen.findByTestId('attach-zip-too-big-modal');
    expect(mocks.uploadFile).not.toHaveBeenCalled();
  });

  it('two change events for a zip upload it once, even while the limit is being looked up', async () => {
    const small = new File(['x'], 'small.zip', { type: 'application/zip' });
    mocks.uploadFile.mockResolvedValue(projectFile());
    render(<Harness projectId={PROJECT_ID} />);

    await attachFile(small);
    await attachFile(small);
    await waitFor(() => expect(mocks.uploadFile).toHaveBeenCalledTimes(1));
    await new Promise((r) => setTimeout(r, 20));
    expect(mocks.uploadFile).toHaveBeenCalledTimes(1);
  });

  it('A1: uploads through the existing project file path — never multipart to the chat stream', async () => {
    const file = makeFile('brief.md');
    mocks.uploadFile.mockResolvedValue(projectFile());
    render(<Harness projectId={PROJECT_ID} />);

    await userEvent.setup().upload(attachInput(), file);

    await waitFor(() => expect(mocks.uploadFile).toHaveBeenCalledTimes(1));
    expect(mocks.uploadFile).toHaveBeenCalledWith(PROJECT_ID, file);

    // The follow-up turn reaches the runtime, so the chat endpoint is called — with JSON, and
    // nothing anywhere carries a multipart body.
    await waitFor(() => expect(chatCalls()).toHaveLength(1));
    for (const [, init] of chatCalls()) {
      expect(typeof init?.body).toBe('string');
    }
    expect(
      fetchMock.mock.calls.filter(([, init]) => init?.body instanceof FormData),
    ).toHaveLength(0);
  });

  it('A2: the assistant receives a user turn naming the uploaded file id and its name — the person sees only the name', async () => {
    const file = makeFile('brief.md');
    mocks.uploadFile.mockResolvedValue(projectFile());
    render(<Harness projectId={PROJECT_ID} />);

    await attachFile(file);

    const thread = screen.getByTestId('assistant-thread');
    await waitFor(() => expect(within(thread).getByText(/brief\.md/)).toBeTruthy());
    // The bubble on screen never shows the id: names, not ids.
    expect(thread.textContent).not.toContain(FILE_ID);

    // The same turn went out over the wire, through the thread runtime rather than a bespoke fetch.
    await waitFor(() => expect(chatCalls()).toHaveLength(1));
    expect(sentBodies()[0]).toContain(FILE_ID);
    expect(sentBodies()[0]).toContain('brief.md');
  });

  it('A2: buildAttachMessage names both the file and its project file id', () => {
    const message = buildAttachMessage('clip.mp4', FILE_ID);
    expect(message).toContain('clip.mp4');
    expect(message).toContain(FILE_ID);
  });

  it('A2: an attach keeps a draft already in the composer instead of discarding it', async () => {
    mocks.uploadFile.mockResolvedValue(projectFile());
    render(<Harness projectId={PROJECT_ID} />);

    await userEvent.setup().type(composerInput(), 'Summarise this');
    await attachFile(makeFile('brief.md'));

    await waitFor(() => expect(chatCalls()).toHaveLength(1));
    expect(sentBodies()[0]).toContain('Summarise this');
    expect(sentBodies()[0]).toContain(FILE_ID);
  });

  it('A3: with no project context the control is disabled, says why, and attaches nothing', async () => {
    render(<Harness />);

    expect(attachInput().disabled).toBe(true);
    expect(attachRoot().getAttribute('aria-disabled')).toBe('true');
    expect(screen.getByText(ATTACH_DISABLED_REASON)).toBeTruthy();

    // Clicking the visible affordance (the label that drives the input) starts nothing.
    const label = attachRoot().querySelector('label');
    expect(label).not.toBeNull();
    await userEvent.setup().click(label as HTMLElement);

    expect(mocks.uploadFile).not.toHaveBeenCalled();
    expect(chatCalls()).toHaveLength(0);
  });

  it('A4: a full workspace is explained with its figures and a way to Billing, not the backend wording', async () => {
    mocks.uploadFile.mockRejectedValue(
      new ApiError('storage', 413, [], {
        code: 'STORAGE_QUOTA_EXCEEDED',
        feature: 'storage',
        message: 'Your workspace is out of storage.',
        limit: 16_106_127_360,
        current: 14_820_833_280,
      }),
    );
    render(<Harness projectId={PROJECT_ID} />);

    await attachFile(makeFile('brief.md'));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('brief.md');
    expect(alert.textContent).toContain('out of storage (13.8 GB of 15 GB used)');
    expect(alert.textContent).toContain('Delete files or upgrade your plan');
    expect(within(alert).getByRole('link', { name: /See plans and storage/ }).getAttribute('href')).toBe('/settings/billing');
    // Nothing was sent to the assistant, and the composer is untouched.
    expect(chatCalls()).toHaveLength(0);
    expect((composerInput() as HTMLTextAreaElement).disabled).toBe(false);
  });

  it('A4: a rejected upload shows an inline error, keeps the composer usable and can be retried', async () => {
    mocks.uploadFile
      .mockRejectedValueOnce(new Error('Storage unavailable'))
      .mockResolvedValueOnce(projectFile());
    render(<Harness projectId={PROJECT_ID} />);

    await attachFile(makeFile('brief.md'));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('brief.md');
    expect(alert.textContent).toContain('Storage unavailable');
    // Nothing was sent, and the composer is untouched and still usable.
    expect(chatCalls()).toHaveLength(0);
    expect((composerInput() as HTMLTextAreaElement).disabled).toBe(false);
    await userEvent.setup().type(composerInput(), 'Still typing');
    expect((composerInput() as HTMLTextAreaElement).value).toBe('Still typing');

    // Retry: the same control, the same file.
    await attachFile(makeFile('brief.md'));

    await waitFor(() => expect(mocks.uploadFile).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(chatCalls()).toHaveLength(1));
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('A5: while the upload is in flight the control is busy, and a double click starts ONE upload', async () => {
    const pending = deferred<ProjectFile>();
    mocks.uploadFile.mockReturnValue(pending.promise);
    render(<Harness projectId={PROJECT_ID} />);

    const file = makeFile('clip.mp4');
    await attachFile(file);
    await attachFile(file);

    await waitFor(() => expect(attachRoot().getAttribute('aria-busy')).toBe('true'));
    expect(attachInput().disabled).toBe(true);
    expect(mocks.uploadFile).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('button', { name: 'Send' })).toBeTruthy();

    pending.resolve(projectFile({ originalFileName: 'clip.mp4' }));
    await waitFor(() => expect(attachRoot().getAttribute('aria-busy')).toBeNull());
    expect(attachInput().disabled).toBe(false);
    expect(mocks.uploadFile).toHaveBeenCalledTimes(1);
  });

  it('A6: it is a real, keyboard-reachable file input with its own name, and it clears itself', async () => {
    const file = makeFile('brief.md');
    mocks.uploadFile.mockResolvedValue(projectFile());
    render(<Harness projectId={PROJECT_ID} />);

    const input = attachInput();
    expect(input.tagName).toBe('INPUT');
    expect(input.type).toBe('file');
    expect(input.getAttribute('aria-label')).toBe(ATTACH_LABEL);
    // Distinct from the composer's own accessible name.
    expect(ATTACH_LABEL).not.toBe(composerInput().getAttribute('aria-label'));

    const user = userEvent.setup();
    let reached = false;
    for (let i = 0; i < 12 && !reached; i += 1) {
      await user.tab();
      reached = document.activeElement === input;
    }
    expect(reached).toBe(true);

    await attachFile(file);
    await waitFor(() => expect(mocks.uploadFile).toHaveBeenCalledTimes(1));

    // Dropped, so picking the same file again is a real change rather than a silent no-op.
    await waitFor(() => expect(input.value).toBe(''));

    await attachFile(file);
    await waitFor(() => expect(mocks.uploadFile).toHaveBeenCalledTimes(2));
    expect(mocks.uploadFile).toHaveBeenLastCalledWith(PROJECT_ID, file);
  });

  it('A7: the composer beside the control still submits on Enter', async () => {
    mocks.uploadFile.mockResolvedValue(projectFile());
    render(<Harness projectId={PROJECT_ID} />);

    await userEvent.setup().type(composerInput(), 'Tidy this up{Enter}');

    await waitFor(() => expect(chatCalls()).toHaveLength(1));
    expect(sentBodies()[0]).toContain('Tidy this up');
    const thread = screen.getByTestId('assistant-thread');
    await waitFor(() => expect(within(thread).getByText('Tidy this up')).toBeTruthy());
  });
});

describe('what the person sees of an attach message', () => {
  it('names the file, never its id — the id stays in the message the assistant receives', () => {
    const sent = buildAttachMessage('intro.mp4', '12345678-1234-1234-1234-123456789012');

    expect(sent).toContain('12345678-1234-1234-1234-123456789012');
    expect(withoutAttachId(sent)).toBe('I attached the file "intro.mp4". Use it as context for this conversation.');
  });

  it('leaves every other message exactly as written', () => {
    expect(withoutAttachId('Rename (my) project 12345678-1234-1234-1234-123456789012, please')).toBe(
      'Rename (my) project 12345678-1234-1234-1234-123456789012, please',
    );
  });
});
