// @vitest-environment jsdom
import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, fireEvent } from '@testing-library/react';
import { SWRConfig } from 'swr';
import { ApiError } from '@/lib/api/client';
import { useObjectRequest } from '@/lib/editing/api/objects';

const get = vi.hoisted(() => ({ fn: vi.fn() }));
vi.mock('@/lib/api/client', async (importOriginal) => ({ ...(await importOriginal<typeof import('@/lib/api/client')>()), fetcher: get.fn }));
afterEach(cleanup);

function Probe() {
  const r = useObjectRequest('p', 'file:f', 'r1');
  return (
    <div>
      <span data-testid="stalled">{String(r.stalled)}</span>
      <button onClick={() => r.retry()} data-testid="retry" />
    </div>
  );
}
const view = () => render(<SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}><Probe /></SWRConfig>);

describe('useObjectRequest errors', () => {
  it('stops at once on a 404 and retry asks again', async () => {
    get.fn.mockReset().mockRejectedValue(new ApiError('gone', 404));
    view();
    await waitFor(() => expect(screen.getByTestId('stalled').textContent).toBe('true'));
    const n = get.fn.mock.calls.length;
    await new Promise((r) => setTimeout(r, 1800));
    expect(get.fn.mock.calls.length).toBe(n);
    fireEvent.click(screen.getByTestId('retry'));
    await waitFor(() => expect(get.fn.mock.calls.length).toBe(n + 1));
  });
});
