// @vitest-environment jsdom
import { describe, it, expect } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { useResyncTick } from './use-resync-tick';
import type { ExecutionStreamConnectionState } from './use-execution-stream';

function setHidden(hidden: boolean) {
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden });
  document.dispatchEvent(new Event('visibilitychange'));
}

describe('useResyncTick', () => {
  it('goes up when the stream reconnects, not on other transitions', () => {
    const { result, rerender } = renderHook(
      ({ s, t }: { s: ExecutionStreamConnectionState; t: boolean }) => useResyncTick(s, t),
      { initialProps: { s: 'connecting' as ExecutionStreamConnectionState, t: false } },
    );
    rerender({ s: 'connected', t: false });
    expect(result.current).toBe(0);
    rerender({ s: 'reconnecting', t: false });
    expect(result.current).toBe(0);
    rerender({ s: 'connected', t: false });
    expect(result.current).toBe(1);
  });

  it('goes up when the tab becomes visible again, unless the run is finished', () => {
    const { result, rerender } = renderHook(
      ({ t }: { t: boolean }) => useResyncTick('connected', t),
      { initialProps: { t: false } },
    );
    act(() => setHidden(true));
    expect(result.current).toBe(0);
    act(() => setHidden(false));
    expect(result.current).toBe(1);
    rerender({ t: true });
    act(() => setHidden(false));
    expect(result.current).toBe(1);
  });

  it('does not resync a finished run on reconnect', () => {
    const { result, rerender } = renderHook(
      ({ s }: { s: ExecutionStreamConnectionState }) => useResyncTick(s, true),
      { initialProps: { s: 'reconnecting' as ExecutionStreamConnectionState } },
    );
    rerender({ s: 'connected' });
    expect(result.current).toBe(0);
  });
});
