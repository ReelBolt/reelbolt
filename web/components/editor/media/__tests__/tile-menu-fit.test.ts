import { describe, expect, it } from 'vitest';
import { TILE_MENU_FIT } from '../ClipGrid';

describe('the tile menu fits a short window', () => {
  it('limits its height to the room it has and scrolls inside it', () => {
    const floating = document.createElement('div');
    TILE_MENU_FIT.size.apply({ availableHeight: 420.7, elements: { floating } });
    expect(floating.style.maxHeight).toBe('420px');
    expect(floating.style.overflowY).toBe('auto');
  });

  it('never shrinks below a usable height', () => {
    const floating = document.createElement('div');
    TILE_MENU_FIT.size.apply({ availableHeight: 20, elements: { floating } });
    expect(floating.style.maxHeight).toBe('160px');
  });
});
