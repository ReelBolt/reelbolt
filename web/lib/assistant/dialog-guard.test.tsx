// @vitest-environment jsdom
import { describe, expect, it } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { holdAssistantDialog, useAssistantDialogOpen } from './dialog-guard';

describe('the assistant dialog guard', () => {
  it('is open while any dialog holds it, and closes when the last one lets go', () => {
    const { result } = renderHook(() => useAssistantDialogOpen());
    expect(result.current).toBe(false);

    let releaseA = () => {};
    let releaseB = () => {};
    act(() => {
      releaseA = holdAssistantDialog();
      releaseB = holdAssistantDialog();
    });
    expect(result.current).toBe(true);

    act(() => releaseA());
    expect(result.current).toBe(true);
    act(() => releaseB());
    expect(result.current).toBe(false);
  });

  it('releasing twice cannot release someone else\'s hold', () => {
    const { result } = renderHook(() => useAssistantDialogOpen());
    let releaseA = () => {};
    let releaseB = () => {};
    act(() => {
      releaseA = holdAssistantDialog();
      releaseB = holdAssistantDialog();
    });

    act(() => {
      releaseA();
      releaseA();
    });

    expect(result.current).toBe(true);
    act(() => releaseB());
    expect(result.current).toBe(false);
  });
});
