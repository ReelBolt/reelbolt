import { afterEach, describe, expect, it, vi } from 'vitest';
import { registerSheet, resetSheetHistory } from '../sheet-history';

afterEach(() => {
  vi.useRealTimers();
  resetSheetHistory();
});

describe('registerSheet unregistering', () => {
  it('takes its own history entry off when the sheet closes in place', () => {
    vi.useFakeTimers();
    const back = vi.spyOn(window.history, 'back').mockImplementation(() => undefined);
    const off = registerSheet('export', () => undefined);
    off();
    vi.runAllTimers();
    expect(back).toHaveBeenCalledTimes(1);
    back.mockRestore();
  });

  it('does not go back when the page already moved on (a link inside the sheet left the editor)', () => {
    vi.useFakeTimers();
    const back = vi.spyOn(window.history, 'back').mockImplementation(() => undefined);
    const off = registerSheet('export', () => undefined);
    const here = window.location.href;
    window.history.pushState({}, '', '/projects/p?tab=files');
    off();
    vi.runAllTimers();
    expect(back).not.toHaveBeenCalled();
    window.history.replaceState(null, '', here);
    back.mockRestore();
  });

  it('still takes its entry off when another sheet was pushed on top in the same commit (a replaced sheet)', () => {
    vi.useFakeTimers();
    const back = vi.spyOn(window.history, 'back').mockImplementation(() => undefined);
    const offFirst = registerSheet('more', () => undefined);
    registerSheet('clip', () => undefined);
    offFirst();
    vi.runAllTimers();
    expect(back).toHaveBeenCalledTimes(1);
    back.mockRestore();
  });
});
