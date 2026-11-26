import { afterEach, describe, expect, it } from 'vitest';
import { dialogMutesDelete } from './useArrange';

afterEach(() => {
  document.body.innerHTML = '';
});

describe('dialogMutesDelete', () => {
  it('is false with no dialog', () => {
    expect(dialogMutesDelete(document)).toBe(false);
  });
  it('ignores the phone non-modal sheet, open or hidden', () => {
    document.body.innerHTML = '<section role="dialog" data-side="bottom"></section><section role="dialog" data-side="right" aria-hidden="true"></section>';
    expect(dialogMutesDelete(document)).toBe(false);
  });
  it('is true for a modal dialog', () => {
    document.body.innerHTML = '<div role="dialog" aria-modal="true"></div>';
    expect(dialogMutesDelete(document)).toBe(true);
  });
  it('is true for a modal sheet (with a backdrop) and for a plain dialog beside the sheet', () => {
    document.body.innerHTML = '<section role="dialog" data-side="bottom" aria-modal="true"></section>';
    expect(dialogMutesDelete(document)).toBe(true);
    document.body.innerHTML = '<section role="dialog" data-side="bottom"></section><div role="dialog"></div>';
    expect(dialogMutesDelete(document)).toBe(true);
  });
});
