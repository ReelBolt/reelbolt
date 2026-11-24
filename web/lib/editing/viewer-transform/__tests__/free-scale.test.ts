import { describe, expect, it } from 'vitest';
import { doc, media, title, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import type { MediaClip, TitleClip } from '@/lib/editing/types';
import {
  CORNER_HANDLES,
  EDGE_HANDLES,
  IDENTITY_TRANSFORM,
  applyDraft,
  boxAsValues,
  frameFor,
  localToCanvas,
  padMagnet,
  scaleFromCorner,
  scaleFromEdge,
  titleBoxFor,
  titleFrame,
  titleValuesForBox,
  transformAt,
  transformOps,
  type TransformValues,
} from '..';

const canvas = { width: 1920, height: 1080 };
const base = { w: 1920, h: 1080 };
const id: TransformValues = { ...IDENTITY_TRANSFORM, scale: 0.5 };
const start = frameFor(id, base, canvas);
const corner = (n: string) => CORNER_HANDLES.find((h) => h.id === n)!;
const edge = (n: string) => EDGE_HANDLES.find((h) => h.id === n)!;

describe('free corner drag', () => {
  it('changes the aspect and writes a vertical scale', () => {
    const p = localToCanvas(start, { x: 1.5, y: 0.5 }); // br pulled right only
    const next = scaleFromCorner(id, start, corner('br'), p, canvas);
    expect(next.scale).toBeCloseTo(1);
    expect(next.scaleY).toBeCloseTo(0.5);
  });

  it('a diagonal drag stays uniform with no scaleY', () => {
    const next = scaleFromCorner(id, start, corner('br'), localToCanvas(start, { x: 1.5, y: 1.5 }), canvas);
    expect(next.scale).toBeCloseTo(1);
    expect(next.scaleY).toBeUndefined();
  });

  it('Shift keeps the aspect: no scaleY on a uniform clip, and a clip with a height keeps its ratio', () => {
    const p = localToCanvas(start, { x: 1.5, y: 0.5 });
    const uniform = scaleFromCorner(id, start, corner('br'), p, canvas, { keepAspect: true });
    expect(uniform.scaleY).toBeUndefined();
    const tall: TransformValues = { ...id, scaleY: 0.25 };
    const f = frameFor(tall, base, canvas);
    const locked = scaleFromCorner(tall, f, corner('br'), localToCanvas(f, { x: 1.5, y: 1.5 }), canvas, { keepAspect: true });
    expect(locked.scaleY! / locked.scale).toBeCloseTo(0.25 / 0.5);
  });

  it('the opposite corner stays put, and Alt keeps the centre', () => {
    const p = localToCanvas(start, { x: 1.2, y: 2 });
    const next = scaleFromCorner(id, start, corner('br'), p, canvas);
    const tl0 = localToCanvas(start, { x: -0.5, y: -0.5 });
    const tl1 = localToCanvas(frameFor(next, base, canvas), { x: -0.5, y: -0.5 });
    expect(tl1.x).toBeCloseTo(tl0.x);
    expect(tl1.y).toBeCloseTo(tl0.y);
    const centred = scaleFromCorner(id, start, corner('br'), p, canvas, { fromCentre: true });
    expect(centred.positionX).toBeCloseTo(0);
    expect(centred.positionY).toBeCloseTo(0);
  });
});

describe('edge stretch', () => {
  it('a side handle stretches one axis and writes the height so it does not follow', () => {
    const next = scaleFromEdge(id, start, edge('r'), localToCanvas(start, { x: 1.5, y: 0 }), canvas);
    expect(next.scale).toBeCloseTo(1);
    expect(next.scaleY).toBeCloseTo(0.5);
  });

  it('the bottom handle changes only the height and anchors the top edge', () => {
    const next = scaleFromEdge(id, start, edge('b'), localToCanvas(start, { x: 0, y: 1.5 }), canvas);
    expect(next.scale).toBeCloseTo(0.5);
    expect(next.scaleY).toBeCloseTo(1);
    const top0 = localToCanvas(start, { x: 0, y: -0.5 });
    const top1 = localToCanvas(frameFor(next, base, canvas), { x: 0, y: -0.5 });
    expect(top1.y).toBeCloseTo(top0.y);
  });

  it('is limited to the scale range', () => {
    expect(scaleFromEdge(id, start, edge('r'), { x: 1e7, y: 0 }, canvas).scale).toBe(8);
  });
});

describe('ops and drafts with scaleY', () => {
  const clip = (extra: Partial<MediaClip> = {}): MediaClip => media('c', 0, 100, 0, extra);

  it('writes scaleY in the one transform write, even when it equals the old uniform scale', () => {
    const c = clip({ transform: { scale: { value: 2, keys: null } } });
    const ops = transformOps(c, { positionX: 0, positionY: 0, scale: 3, scaleY: 2, rotation: 0 }, 10);
    expect(ops).toHaveLength(1);
    expect(ops[0]).toMatchObject({ type: 'setProperty', key: 'transform' });
    const value = (ops[0] as { value: { scale: { value: number }; scaleY: { value: number } } }).value;
    expect(value.scale.value).toBe(3);
    expect(value.scaleY.value).toBe(2);
  });

  it('a uniform change writes no scaleY, and a keyed scaleY writes a key', () => {
    const uniform = transformOps(clip(), { positionX: 0, positionY: 0, scale: 2, rotation: 0 }, 10);
    expect((uniform[0] as { value: Record<string, unknown> }).value.scaleY).toBeUndefined();
    const keyed = clip({ transform: { scaleY: { value: 1, keys: [{ frame: 0, value: 1, interp: 'linear' }] } } });
    const ops = transformOps(keyed, { positionX: 0, positionY: 0, scale: 1, scaleY: 2, rotation: 0 }, 10);
    expect(ops).toEqual([{ type: 'setKeyframe', target: { itemId: 'c' }, param: 'scaleY', frame: 10, value: 2, interp: 'linear' }]);
  });

  it('transformAt carries scaleY only when the clip has one', () => {
    expect(transformAt(clip(), 0).scaleY).toBeUndefined();
    expect(transformAt(clip({ transform: { scaleY: { value: 0.5, keys: null } } }), 0).scaleY).toBe(0.5);
  });
});

describe('title transform', () => {
  const t = (extra: Partial<TitleClip> = {}): TitleClip => ({ ...title('t', 0, 100, 'Hello'), ...extra });

  it('the box is the text box with no transform and follows move, scale about the canvas centre and rotation', () => {
    const baseBox = titleFrame(t(), canvas);
    expect(titleBoxFor(baseBox, IDENTITY_TRANSFORM, canvas)).toMatchObject({ cx: baseBox.cx, cy: baseBox.cy, w: baseBox.w, h: baseBox.h });
    const moved = titleBoxFor(baseBox, { ...IDENTITY_TRANSFORM, positionX: 0.1 }, canvas);
    expect(moved.cx).toBeCloseTo(baseBox.cx + 192);
    const doubled = titleBoxFor(baseBox, { ...IDENTITY_TRANSFORM, scale: 2 }, canvas);
    expect(doubled.w).toBeCloseTo(baseBox.w * 2);
    expect(doubled.cx - 960).toBeCloseTo((baseBox.cx - 960) * 2);
  });

  it('values and box convert back and forth', () => {
    const baseBox = titleFrame(t(), canvas);
    const v: TransformValues = { positionX: 0.07, positionY: -0.03, scale: 1.4, scaleY: 0.9, rotation: 20 };
    const box = titleBoxFor(baseBox, v, canvas);
    const back = titleValuesForBox(baseBox, box, v, canvas);
    for (const k of ['positionX', 'positionY', 'scale', 'scaleY', 'rotation'] as const) expect(back[k]).toBeCloseTo(v[k]!, 6);
    const virtual = boxAsValues(box, { w: baseBox.w, h: baseBox.h }, canvas, true);
    expect(frameFor(virtual, { w: baseBox.w, h: baseBox.h }, canvas).cx).toBeCloseTo(box.cx);
  });

  it('a title draft previews into the document and commits as a transform', () => {
    const d = doc([track(TRACKS.v1, 'video', [t()])]);
    const next = applyDraft(d, { itemId: 't', values: { positionX: 0.1, positionY: 0, scale: 1.5, rotation: 10 } }, 5);
    const item = next.tracks[0].items[0] as TitleClip;
    expect(item.transform?.scale).toEqual({ value: 1.5, keys: null });
    const ops = transformOps(t(), { positionX: 0.1, positionY: 0, scale: 1.5, rotation: 10 }, 5);
    expect(ops[0]).toMatchObject({ type: 'setProperty', itemId: 't', key: 'transform' });
  });

  it('the pad magnet pulls a nearby box onto an anchor and leaves a far one alone', () => {
    const item = t();
    const anchor = titleFrame(item, canvas, { position: 'topLeft' });
    const near = padMagnet(item, { ...anchor, cx: anchor.cx + 5, cy: anchor.cy - 4 }, canvas, 12);
    expect(near.position).toBe('topLeft');
    expect(near.dx).toBeCloseTo(-5);
    expect(padMagnet(item, { ...anchor, cx: 960, cy: 300 }, canvas, 12).position).toBeNull();
  });
});
