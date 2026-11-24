import { describe, expect, it } from 'vitest';
import type { AdjustmentClip, InsertClip, MediaClip } from '../types';
import { composeEntries, resolveComposeEntry, viewerToolForEntry } from './entries';

const media = (over: Partial<MediaClip> = {}) => ({ id: 'c', type: 'mediaClip', name: 'A', ...over }) as unknown as MediaClip;

describe('composeEntries', () => {
  it('lists a media clip with every entry off', () => {
    const e = composeEntries(media());
    expect(e.map((x) => x.id)).toEqual(['mask', 'reframe', 'keyer', 'pin', 'effects', 'motion', 'chain']);
    expect(e.filter((x) => x.on).map((x) => x.id)).toEqual(['chain']);
  });
  it('states shape, object, effects count and motion', () => {
    const e = composeEntries(media({ mask: { kind: 'ellipse', x: 0, y: 0, w: 1, h: 1 } as never, effects: [{ type: 'blur' }, { type: 'blur' }, { type: 'blur' }] as never, stabilize: true }));
    expect(e[0].state).toEqual({ kind: 'shape', shape: 'ellipse' });
    expect(e[4].state).toEqual({ kind: 'count', count: 3 });
    expect(e[5].on).toBe(true);
    expect(composeEntries(media({ mask: { kind: 'segmentation' } as never }))[0].state).toEqual({ kind: 'object' });
  });
  it('lists adjustment and insert clips, and nothing for other items', () => {
    expect(composeEntries({ id: 'a', type: 'adjustmentClip', grade: null, effects: null } as unknown as AdjustmentClip).map((x) => x.id)).toEqual(['grade', 'effects', 'chain']);
    expect(composeEntries({ id: 'i', type: 'insertClip', matte: null, quad: null } as unknown as InsertClip).map((x) => x.id)).toEqual(['matte', 'quad']);
    expect(composeEntries({ id: 't', type: 'caption' } as never)).toEqual([]);
    expect(composeEntries(null)).toEqual([]);
  });
});

describe('resolveComposeEntry', () => {
  it('keeps an entry that applies, else falls back to the first', () => {
    const e = composeEntries(media());
    expect(resolveComposeEntry(e, 'keyer')).toBe('keyer');
    expect(resolveComposeEntry(e, 'matte')).toBe('mask');
    expect(resolveComposeEntry([], 'mask')).toBeNull();
  });
});

describe('viewerToolForEntry', () => {
  const e = composeEntries(media({ mask: { kind: 'rect' } as never }));
  it('enters the tool only for an entry that is on', () => {
    expect(viewerToolForEntry(e[0], 'select')).toBe('mask');
    expect(viewerToolForEntry(e[1], 'select')).toBeNull();
  });
  it('returns a shape tool to select for any other entry', () => {
    expect(viewerToolForEntry(e[1], 'mask')).toBe('select');
    expect(viewerToolForEntry(e[2], 'reframe')).toBe('select');
    expect(viewerToolForEntry(e[2], 'window')).toBeNull();
  });
});
