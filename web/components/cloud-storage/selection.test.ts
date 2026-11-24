import { describe, expect, it } from 'vitest';
import { checkStateOf, describeSelection, tick, toIncludePaths, toggle, untick, type ChildrenOf } from './selection';
import { en } from '@/components/design-system/__tests__/en';

// brand/            (complete listing)
//   logo.svg
//   fonts/          (complete listing)
//     a.ttf
//     b.ttf
// footage/          (TRUNCATED listing: more than shown)
//   clip1.mp4
// notes.txt
const listings: Record<string, { paths: string[]; complete: boolean }> = {
  '': { paths: ['brand', 'footage', 'notes.txt'], complete: true },
  brand: { paths: ['brand/logo.svg', 'brand/fonts'], complete: true },
  'brand/fonts': { paths: ['brand/fonts/a.ttf', 'brand/fonts/b.ttf'], complete: true },
  footage: { paths: ['footage/clip1.mp4'], complete: false },
};
const childrenOf: ChildrenOf = (p) => listings[p];

describe('choosing files on a tree loaded one folder at a time', () => {
  it('a ticked folder covers everything inside it, and something inside makes it partial', () => {
    expect(checkStateOf('brand/fonts/a.ttf', new Set(['brand']))).toBe('checked');
    expect(checkStateOf('brand', new Set(['brand/logo.svg']))).toBe('partial');
    expect(checkStateOf('brandnew', new Set(['brand']))).toBe('unchecked');
  });

  it('ticking a folder folds in what was chosen inside it', () => {
    expect([...tick('brand', new Set(['brand/logo.svg', 'notes.txt']), childrenOf)].sort()).toEqual(['brand', 'notes.txt']);
  });

  it('ticking the last child of a completely listed folder chooses the folder instead', () => {
    const next = tick('brand/fonts/b.ttf', new Set(['brand/fonts/a.ttf', 'brand/logo.svg']), childrenOf);
    expect([...next]).toEqual(['brand']);
  });

  it('never folds a truncated listing into its folder: the hidden items were not chosen', () => {
    expect([...tick('footage/clip1.mp4', new Set(), childrenOf)]).toEqual(['footage/clip1.mp4']);
  });

  it('unticking inside a chosen folder keeps its siblings, level by level', () => {
    const next = untick('brand/fonts/a.ttf', new Set(['brand', 'notes.txt']), childrenOf);
    expect(toIncludePaths(next)).toEqual(['brand/fonts/b.ttf', 'brand/logo.svg', 'notes.txt']);
  });

  it('toggle ticks, then unticks', () => {
    const on = toggle('notes.txt', new Set(), childrenOf);
    expect([...on]).toEqual(['notes.txt']);
    expect([...toggle('notes.txt', on, childrenOf)]).toEqual([]);
  });

  it('sends a sorted list of top-most paths and reads one back in words', () => {
    expect(toIncludePaths(new Set(['b', 'a', 'a/x']))).toEqual(['a', 'b']);
    expect(en(describeSelection(null))).toBe('Every file in the folder');
    expect(en(describeSelection([]))).toBe('Every file in the folder');
    expect(en(describeSelection(['a', 'b', 'c', 'd', 'e']))).toBe('a, b, c and 2 more');
  });
});
