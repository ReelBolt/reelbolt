/** @vitest-environment node */
import { describe, expect, it, vi } from 'vitest';
import { doc, media, track } from '@/lib/editing/__tests__/fixtures';
import type { ChromaKeyer, ClipEffect } from '@/lib/editing/types';
import { adjustmentsMenuItems, badgeMenuItems, type BadgeMenuDeps } from '../badgeMenu';

const keyer = { color: '#00ff00' } as unknown as ChromaKeyer;
const effects: ClipEffect[] = [{ type: 'blur', amount: 0.5 }];
const d = doc([track('v1', 'video', [media('c1', 0, 120, 0, { keyer, effects })])]);
const item = d.tracks[0]!.items[0]!;

function deps(over: Partial<BadgeMenuDeps> = {}): BadgeMenuDeps & { open: ReturnType<typeof vi.fn>; apply: ReturnType<typeof vi.fn> } {
  return { t: (key: string, values?: Record<string, unknown>) => (values?.name ? `${key}:${values.name}` : key), readOnly: false, open: vi.fn(), apply: vi.fn(), ...over } as never;
}

describe('speed badge menu', () => {
  const sped = doc([track('v1', 'video', [media('c1', 0, 60, 0, { playbackRate: 2 })])]);
  it('Remove is "Reset to normal speed" with a note that the clip changes length', () => {
    const x = deps();
    const items = badgeMenuItems(sped.tracks[0]!.items[0]!, sped, 'speed', x);
    const remove = items.find((i) => i.id === 'badge-remove-speed')!;
    expect(remove.label).toBe('resetSpeed:speed');
    expect(remove.note).toBe('resetSpeedNote');
    remove.run();
    expect(x.apply).toHaveBeenCalledWith([expect.objectContaining({ type: 'retime', playbackRate: 1, recordOut: 120 })], 'resetSpeed:speed');
  });
  it('other badges keep the plain Remove and have no note', () => {
    const remove = badgeMenuItems(item, d, 'keyer', deps()).find((i) => i.id === 'badge-remove-keyer')!;
    expect(remove.label).toBe('remove:keyer');
    expect(remove.note).toBeUndefined();
  });
});

describe('badge menu', () => {
  it('a badge with an on/off state lists open, turn off, remove; the actions run', () => {
    const x = deps();
    const items = badgeMenuItems(item, d, 'effects', x);
    expect(items.map((i) => i.id)).toEqual(['badge-open-effects', 'badge-toggle-effects', 'badge-remove-effects']);
    items[0]!.run();
    expect(x.open).toHaveBeenCalledWith('c1', 'effects');
    items[1]!.run();
    expect(x.apply).toHaveBeenLastCalledWith([expect.objectContaining({ type: 'setProperty', key: 'effects' })], 'turnOffLabel:effects');
    items[2]!.run();
    // remove is one call (one undo step) with a clear label
    expect(x.apply).toHaveBeenLastCalledWith([{ type: 'setProperty', itemId: 'c1', key: 'effects', value: undefined }], 'removeLabel:effects');
  });
  it('a badge with no on/off state has no turn-off entry (never a disabled one)', () => {
    const ids = badgeMenuItems(item, d, 'keyer', deps()).map((i) => i.id);
    expect(ids).toEqual(['badge-open-keyer', 'badge-remove-keyer']);
  });
  it('read-only lists only open', () => {
    expect(badgeMenuItems(item, d, 'effects', deps({ readOnly: true })).map((i) => i.id)).toEqual(['badge-open-effects']);
  });
  it('the clip menu group lists every badge with the same actions, divider before the first', () => {
    const items = adjustmentsMenuItems(item, d, deps());
    expect(items.map((i) => i.id)).toEqual(['badge-open-keyer', 'badge-remove-keyer', 'badge-open-effects', 'badge-toggle-effects', 'badge-remove-effects']);
    expect(items[0]!.dividerBefore).toBe(true);
    expect(items[2]!.dividerBefore).toBe(false);
  });
  it('a clip without adjustments adds nothing to the menu', () => {
    const plain = doc([track('v1', 'video', [media('c1', 0, 60)])]);
    expect(adjustmentsMenuItems(plain.tracks[0]!.items[0]!, plain, deps())).toEqual([]);
  });
});
