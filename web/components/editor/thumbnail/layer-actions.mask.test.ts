import { describe, expect, it } from 'vitest';
import { maskMenuItems } from './layer-actions';

const idle = { busy: false, painting: false };
const layer = (over: Partial<{ kind: string; matteUrl: string | null; locked: boolean }> = {}) => ({
  kind: 'source',
  matteUrl: null,
  locked: false,
  ...over,
});

describe('maskMenuItems', () => {
  it('offers nothing on a text layer', () => {
    expect(maskMenuItems(layer({ kind: 'text' }), idle)).toEqual([]);
  });

  it('offers only Add mask when there is no mask, on any bitmap kind', () => {
    for (const kind of ['source', 'cutout', 'generated', 'image']) {
      expect(maskMenuItems(layer({ kind }), idle).map((i) => i.action)).toEqual(['add']);
    }
  });

  it('offers edit, invert, delete and show-only once there is a mask', () => {
    expect(maskMenuItems(layer({ matteUrl: '/m' }), idle).map((i) => i.action)).toEqual(['edit', 'invert', 'delete', 'only']);
  });

  it('disables edits while busy or painting, keeps the view toggle when only painting', () => {
    const painting = maskMenuItems(layer({ matteUrl: '/m' }), { busy: false, painting: true });
    expect(painting.filter((i) => i.disabled).map((i) => i.action)).toEqual(['edit', 'invert', 'delete']);
    const busy = maskMenuItems(layer({ matteUrl: '/m' }), { busy: true, painting: false });
    expect(busy.every((i) => i.disabled)).toBe(true);
  });

  it('a locked layer cannot be changed but its mask can still be viewed', () => {
    const items = maskMenuItems(layer({ matteUrl: '/m', locked: true }), idle);
    expect(items.find((i) => i.action === 'only')?.disabled).toBe(false);
    expect(items.find((i) => i.action === 'edit')?.disabled).toBe(true);
    expect(maskMenuItems(layer({ locked: true }), idle)[0].disabled).toBe(true);
  });
});
