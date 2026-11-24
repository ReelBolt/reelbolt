// @vitest-environment jsdom

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, cleanup, render, waitFor } from '@testing-library/react';
import { SWRConfig } from 'swr';
import useSWR from 'swr';
import type { AssistantAction } from '@/lib/types/assistant';
import { announceAssistantActionDone, resetAnnouncedAssistantActions } from '@/lib/assistant/events';
import { AssistantActionRefresh } from './AssistantActionSync';

/** A mounted reader of one cached url, counting how often it was fetched. */
function Reader({ url, fetcher }: { url: string; fetcher: (url: string) => Promise<string> }) {
  useSWR(url, fetcher, { revalidateOnFocus: false, dedupingInterval: 0 });
  return null;
}

const done = (over: Partial<AssistantAction> = {}): AssistantAction => ({
  id: 'a1',
  state: 'done',
  mode: 'ask',
  kind: 'workflow',
  risk: 'normal',
  title: 'Add a step',
  changes: [],
  ...over,
});

beforeEach(() => resetAnnouncedAssistantActions());
afterEach(() => cleanup());

describe('AssistantActionRefresh', () => {
  it("re-reads the project's lists when the assistant finishes a change, and nothing unrelated", async () => {
    const fetcher = vi.fn((url: string) => Promise.resolve(url));
    render(
      <SWRConfig value={{ provider: () => new Map() }}>
        <AssistantActionRefresh />
        <Reader url="/api/v1/projects" fetcher={fetcher} />
        <Reader url="/api/v1/projects/p1/files" fetcher={fetcher} />
        <Reader url="/api/v1/projects/p1/workflows" fetcher={fetcher} />
        <Reader url="/api/v1/agents" fetcher={fetcher} />
      </SWRConfig>,
    );
    await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(4));
    fetcher.mockClear();

    act(() => {
      announceAssistantActionDone(done());
    });

    await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(3));
    expect(fetcher.mock.calls.map(([url]) => url).sort()).toEqual([
      '/api/v1/projects',
      '/api/v1/projects/p1/files',
      '/api/v1/projects/p1/workflows',
    ]);
  });

  it('re-reads an action kind\'s own lists as well', async () => {
    const fetcher = vi.fn((url: string) => Promise.resolve(url));
    render(
      <SWRConfig value={{ provider: () => new Map() }}>
        <AssistantActionRefresh />
        <Reader url="/api/v1/agents" fetcher={fetcher} />
      </SWRConfig>,
    );
    await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(1));

    act(() => {
      announceAssistantActionDone(done({ id: 'a2', kind: 'agent' }));
    });

    await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(2));
  });

  it('does nothing for the same action twice', async () => {
    const fetcher = vi.fn((url: string) => Promise.resolve(url));
    render(
      <SWRConfig value={{ provider: () => new Map() }}>
        <AssistantActionRefresh />
        <Reader url="/api/v1/projects" fetcher={fetcher} />
      </SWRConfig>,
    );
    await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(1));

    act(() => {
      announceAssistantActionDone(done());
      announceAssistantActionDone(done());
    });

    await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(2));
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(fetcher).toHaveBeenCalledTimes(2);
  });
});
