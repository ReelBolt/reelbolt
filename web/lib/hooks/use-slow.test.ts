// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { useSlow } from './use-slow';

afterEach(() => vi.useRealTimers());

describe('useSlow', () => {
  it('turns true after the delay and false when inactive', () => {
    vi.useFakeTimers();
    const { result, rerender } = renderHook(({ a }) => useSlow(a), { initialProps: { a: true } });
    expect(result.current).toBe(false);
    act(() => { vi.advanceTimersByTime(9999); });
    expect(result.current).toBe(false);
    act(() => { vi.advanceTimersByTime(1); });
    expect(result.current).toBe(true);
    rerender({ a: false });
    expect(result.current).toBe(false);
  });
  it('never fires while inactive', () => {
    vi.useFakeTimers();
    const { result } = renderHook(() => useSlow(false));
    act(() => { vi.advanceTimersByTime(60000); });
    expect(result.current).toBe(false);
  });
});
