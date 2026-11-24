// @vitest-environment jsdom

/**
 * The shared dismissal memory for failed-run warnings. What matters: a dismissal survives a remount
 * (client-side navigation), a run the viewer has NOT dismissed still surfaces, and blocked or corrupt
 * storage degrades to "nothing dismissed" instead of breaking the dashboard.
 */
import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  __resetDismissedRunFailuresForTests,
  dismissFailedRuns,
  DISMISSED_RUN_FAILURES_KEY,
  useDismissedRunFailures,
} from './use-dismissed-runs';

beforeEach(() => {
  window.localStorage.clear();
  __resetDismissedRunFailuresForTests();
});

afterEach(() => {
  __resetDismissedRunFailuresForTests();
  window.localStorage.clear();
});

describe('useDismissedRunFailures', () => {
  it('starts with nothing dismissed when the browser has no memory of one', () => {
    const { result } = renderHook(() => useDismissedRunFailures());
    expect(result.current.dismissed.size).toBe(0);
  });

  it('hides a dismissed run and persists it under the shared key', () => {
    const { result } = renderHook(() => useDismissedRunFailures());

    act(() => result.current.dismiss(['run-a']));

    expect(result.current.dismissed.has('run-a')).toBe(true);
    expect(JSON.parse(window.localStorage.getItem(DISMISSED_RUN_FAILURES_KEY) ?? '[]')).toEqual(['run-a']);
  });

  it('keeps hiding a dismissed run after a remount, as after navigating between pages', () => {
    const first = renderHook(() => useDismissedRunFailures());
    act(() => first.result.current.dismiss(['run-a']));
    first.unmount();

    const second = renderHook(() => useDismissedRunFailures());
    expect(second.result.current.dismissed.has('run-a')).toBe(true);
  });

  it('still surfaces a failure the viewer has not dismissed', () => {
    const { result } = renderHook(() => useDismissedRunFailures());

    act(() => result.current.dismiss(['run-a']));

    expect(result.current.dismissed.has('run-a')).toBe(true);
    expect(result.current.dismissed.has('run-b')).toBe(false);
  });

  it('can be called outside a component and still reaches a mounted hook', () => {
    const { result } = renderHook(() => useDismissedRunFailures());

    act(() => dismissFailedRuns(['run-a']));

    expect(result.current.dismissed.has('run-a')).toBe(true);
  });

  it('degrades to "nothing dismissed" when storage holds junk', () => {
    window.localStorage.setItem(DISMISSED_RUN_FAILURES_KEY, 'not json');
    const { result } = renderHook(() => useDismissedRunFailures());
    expect(result.current.dismissed.size).toBe(0);
  });

  it('keeps the stored list bounded, dropping the oldest dismissals first', () => {
    const ids = Array.from({ length: 205 }, (_, i) => `run-${i}`);
    for (const id of ids) dismissFailedRuns([id]);

    const stored = JSON.parse(window.localStorage.getItem(DISMISSED_RUN_FAILURES_KEY) ?? '[]') as string[];
    expect(stored).toHaveLength(200);
    expect(stored).not.toContain('run-0');
    expect(stored).toContain('run-204');
  });
});
