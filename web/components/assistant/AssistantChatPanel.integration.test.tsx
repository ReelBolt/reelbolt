// @vitest-environment jsdom

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { AssistantProvider } from './AssistantProvider';
import { AssistantChatPanel } from './AssistantChatPanel';
import type { WorkflowEditDiff } from '@/lib/types/workflow-assistant';

class MockResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

describe('AssistantChatPanel Integration - UI Message Stream Protocol', () => {
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

    // Mock scrollTo on Element prototype
    Element.prototype.scrollTo = vi.fn();
  });

  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  it('types and submits a message without workflowId, receives SSE stream with ui-message-stream protocol, renders assistant response', async () => {
    // Create mock SSE stream with confirmed wire protocol
    const sseData = [
      'data: {"type":"start","messageId":"msg_1"}',
      '',
      'data: {"type":"text-start","id":"txt_1"}',
      '',
      'data: {"type":"text-delta","textDelta":"Hello world"}',
      '',
      'data: {"type":"text-end"}',
      '',
      'data: {"type":"finish","finishReason":"stop","usage":{"inputTokens":0,"outputTokens":0}}',
      '',
      'data: [DONE]',
      '',
      '', // SSE dispatches an event on a blank line: end with "\n\n"
    ].join('\n');

    // Mock fetch to return SSE data before rendering
    const mockFetch = vi.fn<typeof fetch>(() => {
      return Promise.resolve(
        new Response(sseData, {
          status: 200,
          headers: {
            'content-type': 'text/event-stream',
            'x-vercel-ai-ui-message-stream': 'v1',
          },
        })
      );
    });

    global.fetch = mockFetch as unknown as typeof fetch;

    // Render the provider and panel
    const user = userEvent.setup();
    render(
      <MantineProvider>
        <AssistantProvider context={{ projectId: '12345678-1234-1234-1234-123456789012' }}>
          <AssistantChatPanel />
        </AssistantProvider>
      </MantineProvider>
    );

    // Type a message
    const inputs = screen.getAllByPlaceholderText(/message the assistant/i);
    const input = inputs[inputs.length - 1]; // Get the last input (most recent render)
    expect(input).toBeDefined();
    await user.type(input, 'Test message');

    // Submit the message
    const sendButtons = screen.getAllByRole('button', { name: /send/i });
    const sendButton = sendButtons[sendButtons.length - 1];
    await user.click(sendButton);

    // Wait for the assistant response to appear
    await waitFor(
      () => {
        const assistantText = screen.queryByText('Hello world');
        expect(assistantText).toBeTruthy();
      },
      { timeout: 5000 }
    );

    // Verify the assistant text is rendered
    const assistantResponse = screen.getByText('Hello world');
    expect(assistantResponse).toBeTruthy();
  });

  it('types and submits a message with workflowId, receives SSE stream with ui-message-stream protocol, renders assistant response', async () => {
    // Create mock SSE stream with confirmed wire protocol
    const sseData = [
      'data: {"type":"start","messageId":"msg_1"}',
      '',
      'data: {"type":"text-start","id":"txt_1"}',
      '',
      'data: {"type":"text-delta","textDelta":"Hello world"}',
      '',
      'data: {"type":"text-end"}',
      '',
      'data: {"type":"finish","finishReason":"stop","usage":{"inputTokens":0,"outputTokens":0}}',
      '',
      'data: [DONE]',
      '',
      '', // SSE dispatches an event on a blank line: end with "\n\n"
    ].join('\n');

    // Mock fetch to return SSE data before rendering
    const mockFetch = vi.fn<typeof fetch>(() => {
      return Promise.resolve(
        new Response(sseData, {
          status: 200,
          headers: {
            'content-type': 'text/event-stream',
            'x-vercel-ai-ui-message-stream': 'v1',
          },
        })
      );
    });

    global.fetch = mockFetch as unknown as typeof fetch;

    // Render the provider and panel with workflowId
    const user = userEvent.setup();
    render(
      <MantineProvider>
        <AssistantProvider context={{ projectId: '12345678-1234-1234-1234-123456789012', workflowId: '87654321-4321-4321-4321-210987654321' }}>
          <AssistantChatPanel />
        </AssistantProvider>
      </MantineProvider>
    );

    // Type a message
    const inputs = screen.getAllByPlaceholderText(/message the assistant/i);
    const input = inputs[inputs.length - 1]; // Get the last input (most recent render)
    expect(input).toBeDefined();
    await user.type(input, 'Test message');

    // Submit the message
    const sendButtons = screen.getAllByRole('button', { name: /send/i });
    const sendButton = sendButtons[sendButtons.length - 1];
    await user.click(sendButton);

    // Wait for the assistant response to appear
    await waitFor(
      () => {
        const assistantText = screen.queryByText('Hello world');
        expect(assistantText).toBeTruthy();
      },
      { timeout: 5000 }
    );

    // Verify the assistant text is rendered
    const assistantResponse = screen.getByText('Hello world');
    expect(assistantResponse).toBeTruthy();
  });

  it(
    'fires onWorkflowDiff callback once when SSE stream includes data-workflowDiff chunk without workflowId',
    { timeout: 30000 },
    async () => {
      // Create mock SSE stream with data-workflowDiff chunk
      const mockDiff: WorkflowEditDiff = {
        additions: [
          {
            stepOrder: 0,
            agentDefinitionId: 'agent-1',
            stepType: 'transform',
            summary: 'Added transform step',
          },
        ],
        removals: [],
        modifications: [],
        confirmationToken: 'token-123',
      };

      const sseData = [
        'data: {"type":"start","messageId":"msg_1"}',
        '',
        'data: {"type":"text-start","id":"txt_1"}',
        '',
        'data: {"type":"text-delta","textDelta":"I propose these changes:"}',
        '',
        'data: {"type":"text-end"}',
        '',
        `data: {"type":"data-workflowDiff","data":${JSON.stringify(mockDiff)}}`,
        '',
        'data: {"type":"finish","finishReason":"stop","usage":{"inputTokens":0,"outputTokens":0}}',
        '',
        'data: [DONE]',
        '',
      ].join('\n');

      // Mock fetch to return SSE data before rendering
      const mockFetch = vi.fn<typeof fetch>(() => {
        return Promise.resolve(
          new Response(sseData, {
            status: 200,
            headers: {
              'content-type': 'text/event-stream',
              'x-vercel-ai-ui-message-stream': 'v1',
            },
          })
        );
      });

      global.fetch = mockFetch as unknown as typeof fetch;

      // Create callback mock
      const onWorkflowDiff = vi.fn<(diff: WorkflowEditDiff) => void>();

      // Render the provider and panel with callback
      const user = userEvent.setup();
      render(
        <MantineProvider>
          <AssistantProvider context={{ projectId: '12345678-1234-1234-1234-123456789012' }}>
            <AssistantChatPanel onWorkflowDiff={onWorkflowDiff} />
          </AssistantProvider>
        </MantineProvider>
      );

      // Type a message
      const inputs = screen.getAllByPlaceholderText(/message the assistant/i);
      const input = inputs[inputs.length - 1]; // Get the last input (most recent render)
      expect(input).toBeDefined();
      await user.type(input, 'Propose some changes');

      // Submit the message
      const sendButtons = screen.getAllByRole('button', { name: /send/i });
      const sendButton = sendButtons[sendButtons.length - 1];
      await user.click(sendButton);

      // Wait for the callback to be called
      await waitFor(
        () => {
          expect(onWorkflowDiff).toHaveBeenCalledOnce();
        },
        { timeout: 10000 }
      );

      // Verify callback was called with exact diff
      expect(onWorkflowDiff).toHaveBeenCalledWith(mockDiff);
    }
  );

  it(
    'fires onWorkflowDiff callback once when SSE stream includes data-workflowDiff chunk with workflowId',
    { timeout: 30000 },
    async () => {
      // Create mock SSE stream with data-workflowDiff chunk
      const mockDiff: WorkflowEditDiff = {
        additions: [
          {
            stepOrder: 0,
            agentDefinitionId: 'agent-1',
            stepType: 'transform',
            summary: 'Added transform step',
          },
        ],
        removals: [],
        modifications: [],
        confirmationToken: 'token-123',
      };

      const sseData = [
        'data: {"type":"start","messageId":"msg_1"}',
        '',
        'data: {"type":"text-start","id":"txt_1"}',
        '',
        'data: {"type":"text-delta","textDelta":"I propose these changes:"}',
        '',
        'data: {"type":"text-end"}',
        '',
        `data: {"type":"data-workflowDiff","data":${JSON.stringify(mockDiff)}}`,
        '',
        'data: {"type":"finish","finishReason":"stop","usage":{"inputTokens":0,"outputTokens":0}}',
        '',
        'data: [DONE]',
        '',
      ].join('\n');

      // Mock fetch to return SSE data before rendering
      const mockFetch = vi.fn<typeof fetch>(() => {
        return Promise.resolve(
          new Response(sseData, {
            status: 200,
            headers: {
              'content-type': 'text/event-stream',
              'x-vercel-ai-ui-message-stream': 'v1',
            },
          })
        );
      });

      global.fetch = mockFetch as unknown as typeof fetch;

      // Create callback mock
      const onWorkflowDiff = vi.fn<(diff: WorkflowEditDiff) => void>();

      // Render the provider and panel with callback and workflowId
      const user = userEvent.setup();
      render(
        <MantineProvider>
          <AssistantProvider context={{ projectId: '12345678-1234-1234-1234-123456789012', workflowId: '87654321-4321-4321-4321-210987654321' }}>
            <AssistantChatPanel onWorkflowDiff={onWorkflowDiff} />
          </AssistantProvider>
        </MantineProvider>
      );

      // Type a message
      const inputs = screen.getAllByPlaceholderText(/message the assistant/i);
      const input = inputs[inputs.length - 1]; // Get the last input (most recent render)
      expect(input).toBeDefined();
      await user.type(input, 'Propose some changes');

      // Submit the message
      const sendButtons = screen.getAllByRole('button', { name: /send/i });
      const sendButton = sendButtons[sendButtons.length - 1];
      await user.click(sendButton);

      // Wait for the callback to be called
      await waitFor(
        () => {
          expect(onWorkflowDiff).toHaveBeenCalledOnce();
        },
        { timeout: 10000 }
      );

      // Verify callback was called with exact diff
      expect(onWorkflowDiff).toHaveBeenCalledWith(mockDiff);
    }
  );
});
