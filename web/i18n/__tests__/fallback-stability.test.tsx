import { describe, expect, it } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useT } from '@/i18n';

describe('test fallback translator', () => {
  it('keeps the same identity across renders, so `t` is safe in dependency arrays', () => {
    const { result, rerender } = renderHook(() => useT('common'));
    const first = result.current;
    rerender();
    rerender();
    expect(result.current).toBe(first);
  });
});
