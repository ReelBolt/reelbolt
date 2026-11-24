// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook } from '@testing-library/react';

const wipe = vi.fn(async () => undefined);
vi.mock('../editing/sync/user-scope', () => ({ wipeEditorLocalData: () => wipe() }));

import { useAuth } from './use-auth';

afterEach(() => vi.unstubAllGlobals());

describe('useAuth().logout', () => {
  it('wipes the editor journal and queue before navigating, even when the request fails', async () => {
    const fetchMock = vi.fn(async () => {
      throw new Error('offline');
    });
    vi.stubGlobal('fetch', fetchMock);
    const { result } = renderHook(() => useAuth());
    await result.current.logout().catch(() => undefined);
    expect(wipe).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledWith('/api/auth/logout', { method: 'POST' });
  });
});
