// @vitest-environment jsdom

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { AssistantProvider } from './AssistantProvider';
import { AssistantChatPanel } from './AssistantChatPanel';
import {
  onImageDocumentChange,
  resetAnnouncedImageDocumentChanges,
  type ImageDocumentChange,
} from '@/lib/editor/assistant-events';

/**
 * A `data-imageDocumentChanged` frame in the chat stream is announced to the page (where the open
 * picture editor listens) — once per change, however many times the frame is rendered.
 */

class MockResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

const CHANGE = {
  changeId: 'c-1',
  projectId: '12345678-1234-1234-1234-123456789012',
  documentId: '04d17aa1-84fe-4694-9b86-db1bf2d3997d',
  kind: 'layersAdded',
  layerIds: ['layer-9'],
  summary: 'Added a red title.',
};

const sse = (frames: unknown[]) =>
  [
    'data: {"type":"start","messageId":"msg_1"}',
    '',
    'data: {"type":"text-start","id":"txt_1"}',
    '',
    'data: {"type":"text-delta","textDelta":"Done."}',
    '',
    'data: {"type":"text-end"}',
    '',
    ...frames.flatMap((data) => [`data: ${JSON.stringify({ type: 'data-imageDocumentChanged', data })}`, '']),
    'data: {"type":"finish","finishReason":"stop","usage":{"inputTokens":0,"outputTokens":0}}',
    '',
    'data: [DONE]',
    '',
    '',
  ].join('\n');

describe('the assistant announces picture changes to the editor', () => {
  beforeEach(() => {
    resetAnnouncedImageDocumentChanges();
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
  });

  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  it('announces each change once, and ignores a malformed frame', { timeout: 30000 }, async () => {
    global.fetch = vi.fn<typeof fetch>(() =>
      Promise.resolve(
        new Response(sse([CHANGE, CHANGE, { kind: 'layersAdded' }, { ...CHANGE, changeId: 'c-2', kind: 'graphicQueued', layerIds: [], graphicId: 'g-1' }]), {
          status: 200,
          headers: { 'content-type': 'text/event-stream', 'x-vercel-ai-ui-message-stream': 'v1' },
        }),
      ),
    ) as unknown as typeof fetch;

    const heard: ImageDocumentChange[] = [];
    const stop = onImageDocumentChange((change) => heard.push(change));

    const user = userEvent.setup();
    render(
      <MantineProvider>
        <AssistantProvider context={{ projectId: CHANGE.projectId, imageDocumentId: CHANGE.documentId }}>
          <AssistantChatPanel />
        </AssistantProvider>
      </MantineProvider>,
    );

    const inputs = screen.getAllByPlaceholderText(/message the assistant/i);
    await user.type(inputs[inputs.length - 1], 'Add a red title');
    const sendButtons = screen.getAllByRole('button', { name: /send/i });
    await user.click(sendButtons[sendButtons.length - 1]);

    await waitFor(() => expect(heard.map((c) => c.changeId)).toEqual(['c-1', 'c-2']), { timeout: 10000 });
    expect(heard[0]).toEqual(CHANGE);
    expect(heard[1]).toMatchObject({ kind: 'graphicQueued', graphicId: 'g-1' });
    stop();
  });
});
