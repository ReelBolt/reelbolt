import { describe, expect, it } from 'vitest';
import { media } from '@/lib/editing/__tests__/fixtures';
import type { Item, MediaClip } from '@/lib/editing/types';
import { fullEditorWork, fullWorkPage } from '../full-editor-work';

const clip = (patch: Partial<MediaClip> = {}): MediaClip => ({ ...(media('c1', 0, 60, 0) as MediaClip), ...patch });
const node = (name: string) => ({ id: name, name, enabled: true, primaries: null, curves: null, lut: null });

describe('work the Simple shell shows but does not edit', () => {
  it('a plain clip has none', () => {
    expect(fullEditorWork(clip())).toEqual([]);
  });

  it('the Simple shell\'s own colour steps (a look, the five fixes, Fix exposure) are not full-editor work', () => {
    const own = clip({ grade: { nodes: [node('Look: Warm'), node('Fix'), node('Auto fix')] } } as Partial<MediaClip>);
    expect(fullEditorWork(own)).toEqual([]);
    const mixed = clip({ grade: { nodes: [node('Look: Warm'), node('Skin'), node('Sky')] } } as Partial<MediaClip>);
    expect(fullEditorWork(mixed)).toEqual([{ kind: 'colour', count: 2 }]);
  });

  it('names each kind once, with a count where one makes sense', () => {
    const busy = clip({ mask: { shape: 'rect' }, effects: [{ type: 'blur' }, { type: 'grain' }], stabilize: true, reverse: true, transform: { scale: { value: 1, keys: [{ frame: 0, value: 1 }] } } } as unknown as Partial<MediaClip>);
    expect(fullEditorWork(busy)).toEqual([
      { kind: 'mask', count: 1 },
      { kind: 'effects', count: 2 },
      { kind: 'stabilize', count: 1 },
      { kind: 'reverse', count: 1 },
      { kind: 'animation', count: 1 },
    ]);
  });

  it('other item types report nothing here (the panel sends the whole item to the full editor)', () => {
    expect(fullEditorWork({ id: 't', type: 'titleClip', name: 'T', recordIn: 0, recordOut: 10, text: 'x' } as unknown as Item)).toEqual([]);
  });

  it('opens the page where that work is done', () => {
    expect(fullWorkPage('colour')).toBe('color');
    expect(fullWorkPage('mask')).toBe('compose');
    expect(fullWorkPage('effects')).toBe('edit');
  });
});
