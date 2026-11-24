// @vitest-environment jsdom

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, cleanup } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { AssistantProvider } from './AssistantProvider';
import { AssistantChatPanel } from './AssistantChatPanel';

class MockResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

describe('AssistantChatPanel', () => {
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
  });

  afterEach(() => {
    cleanup();
  });

  it('renders composer and empty thread without workflowId', () => {
    render(
      <MantineProvider>
        <AssistantProvider context={{ projectId: '12345678-1234-1234-1234-123456789012' }}>
          <AssistantChatPanel />
        </AssistantProvider>
      </MantineProvider>
    );

    const input = screen.getByPlaceholderText(/message|type|chat/i);
    expect(input).toBeDefined();

    const sendButton = screen.getByRole('button', { name: /send/i });
    expect(sendButton).toBeDefined();

    const threadContainer = screen.getByTestId('assistant-thread');
    expect(threadContainer).toBeDefined();
  });

  it('renders composer and empty thread with workflowId', () => {
    render(
      <MantineProvider>
        <AssistantProvider context={{ projectId: '12345678-1234-1234-1234-123456789012', workflowId: '87654321-4321-4321-4321-210987654321' }}>
          <AssistantChatPanel />
        </AssistantProvider>
      </MantineProvider>
    );

    const input = screen.getByPlaceholderText(/message|type|chat/i);
    expect(input).toBeDefined();

    const sendButton = screen.getByRole('button', { name: /send/i });
    expect(sendButton).toBeDefined();

    const threadContainer = screen.getByTestId('assistant-thread');
    expect(threadContainer).toBeDefined();
  });

  it('does not cause maximum update depth exceeded error when parent re-renders with same props', () => {
    // Spy on console.error to catch the "Maximum update depth exceeded" error
    const consoleErrorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const consoleWarnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});

    // Component that can be re-rendered from outside
    const ParentComponent = ({ projectId, workflowId }: { projectId: string; workflowId: string }) => (
      <MantineProvider>
        <AssistantProvider context={{ projectId, workflowId }}>
          <AssistantChatPanel />
        </AssistantProvider>
      </MantineProvider>
    );

    // Render with initial props
    const { rerender } = render(
      <ParentComponent
        projectId="12345678-1234-1234-1234-123456789012"
        workflowId="87654321-4321-4321-4321-210987654321"
      />
    );

    // Force multiple re-renders with the same props to test for infinite loop
    for (let i = 0; i < 5; i++) {
      rerender(
        <ParentComponent
          projectId="12345678-1234-1234-1234-123456789012"
          workflowId="87654321-4321-4321-4321-210987654321"
        />
      );
    }

    // Assert that no maximum update depth exceeded error was logged
    const maxDepthErrors = consoleErrorSpy.mock.calls.filter((call) =>
      call[0]?.toString().includes('Maximum update depth exceeded')
    );
    const maxDepthWarnings = consoleWarnSpy.mock.calls.filter((call) =>
      call[0]?.toString().includes('Maximum update depth exceeded')
    );

    expect(maxDepthErrors.length).toBe(0);
    expect(maxDepthWarnings.length).toBe(0);

    consoleErrorSpy.mockRestore();
    consoleWarnSpy.mockRestore();
  });
});
