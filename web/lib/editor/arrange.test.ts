import { describe, expect, it } from 'vitest';
import { alignLayers, distributeLayers, fieldValues, setField, unionBox } from './arrange';
import type { PlacedLayer } from './compose';

function layer(id: string, x: number, y: number, w: number, h: number, over: Partial<PlacedLayer['transform']> = {}, locked = false): PlacedLayer {
  return {
    id, kind: 'image', role: 'overlay', label: id, zOrder: 1, visible: true, locked, opacity: 1, blendMode: 'normal',
    transform: { x, y, scale: 1, rotation: 0, flipX: false, flipY: false, ...over },
    width: w, height: h, imageUrl: '', matteUrl: null, content: null, segmentationPrompt: null, size: { w, h },
  };
}
const frame = { x: 0, y: 0, w: 1000, h: 500 };

describe('alignLayers', () => {
  const a = layer('a', 100, 50, 200, 100);
  it('aligns to the canvas edges and centres', () => {
    expect(alignLayers([a], 'left', frame)[0].transform.x).toBe(0);
    expect(alignLayers([a], 'right', frame)[0].transform.x).toBe(800);
    expect(alignLayers([a], 'center', frame)[0].transform.x).toBe(400);
    expect(alignLayers([a], 'top', frame)[0].transform.y).toBe(0);
    expect(alignLayers([a], 'bottom', frame)[0].transform.y).toBe(400);
    expect(alignLayers([a], 'middle', frame)[0].transform.y).toBe(200);
  });
  it('aligns to the selection box', () => {
    const b = layer('b', 500, 300, 100, 100);
    const box = unionBox([a, b])!;
    expect(box).toEqual({ x: 100, y: 50, w: 500, h: 350 });
    const moves = alignLayers([a, b], 'right', box);
    expect(moves.map((m) => [m.id, m.transform.x])).toEqual([['a', 400]]);
  });
  it('lines up by what is seen when a layer is turned', () => {
    const turned = layer('t', 0, 0, 200, 100, { rotation: 90 }); // box becomes 100 wide, 200 tall around (100,50)
    const move = alignLayers([turned], 'left', frame)[0];
    expect(move.transform.x).toBeCloseTo(-50);
  });
  it('leaves locked layers and no-op moves alone', () => {
    expect(alignLayers([layer('l', 5, 5, 10, 10, {}, true)], 'left', frame)).toEqual([]);
    expect(alignLayers([layer('z', 0, 0, 10, 10)], 'left', frame)).toEqual([]);
  });
});

describe('distributeLayers', () => {
  it('makes the gaps equal and keeps the outer two', () => {
    const l = [layer('a', 0, 0, 100, 10), layer('b', 150, 0, 100, 10), layer('c', 700, 0, 100, 10)];
    const moves = distributeLayers(l, 'horizontal');
    expect(moves.map((m) => [m.id, m.transform.x])).toEqual([['b', 350]]);
  });
  it('works vertically and needs three layers', () => {
    const l = [layer('a', 0, 0, 10, 100), layer('b', 0, 10, 10, 100), layer('c', 0, 400, 10, 100)];
    expect(distributeLayers(l, 'vertical').map((m) => [m.id, m.transform.y])).toEqual([['b', 200]]);
    expect(distributeLayers(l.slice(0, 2), 'vertical')).toEqual([]);
  });
});

describe('numeric fields', () => {
  const p = layer('p', 10.4, 20.6, 400, 200, { scale: 0.5 });
  it('shows canvas pixels', () => {
    expect(fieldValues(p)).toEqual({ x: 10, y: 21, width: 200, height: 100 });
  });
  it('moves and resizes keeping proportions', () => {
    expect(setField(p, 'x', 50)?.x).toBe(50);
    expect(setField(p, 'width', 400)?.scale).toBe(1);
    expect(setField(p, 'height', 50)?.scale).toBe(0.25);
  });
  it('refuses nonsense and no-ops', () => {
    expect(setField(p, 'width', 0)).toBeNull();
    expect(setField(p, 'x', Number.NaN)).toBeNull();
    expect(setField(p, 'x', 10.4)).toBeNull();
  });
});
