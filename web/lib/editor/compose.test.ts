import { describe, expect, it } from 'vitest';
import fc from 'fast-check';
import { BLEND_MODES, type TextContent } from '@/lib/types/image-document';
import {
  BLEND_TO_COMPOSITE,
  canvasToLayerLocal,
  composeScene,
  drawOverlayOnLayer,
  fontString,
  hitTest,
  layerBounds,
  layerLocalToCanvas,
  layoutText,
  placeLayers,
  type BitmapMap,
  type PlacedLayer,
  type ScratchFactory,
} from './compose';
import type { EditorLayer } from './model';
import { defaultTextContent } from './text-content';
import { RecordingContext, fakeBitmap } from './__tests__/recording-context';

const measure = (_font: string, text: string) => text.length * 10;

function layer(overrides: Partial<PlacedLayer> & { id: string }): PlacedLayer {
  return {
    kind: 'image',
    role: 'overlay',
    label: overrides.id,
    zOrder: 1,
    visible: true,
    locked: false,
    opacity: 1,
    blendMode: 'normal',
    transform: { x: 0, y: 0, scale: 1, rotation: 0, flipX: false, flipY: false },
    width: 200,
    height: 100,
    imageUrl: `/img/${overrides.id}`,
    matteUrl: null,
    content: null,
    segmentationPrompt: null,
    size: { w: 200, h: 100 },
    ...overrides,
  };
}

function scene(layers: PlacedLayer[], bitmaps: BitmapMap, extra: { backgroundColor?: string | null } = {}) {
  return { width: 1280, height: 720, backgroundColor: extra.backgroundColor ?? null, layers, bitmaps };
}

const bitmapsFor = (ids: string[]): BitmapMap =>
  new Map(ids.map((id) => [id, { image: fakeBitmap(200, 100, id) }]));

describe('composeScene: order, visibility, background', () => {
  it('draws back to front by zOrder whatever the array order is', () => {
    const ctx = new RecordingContext();
    const layers = [
      layer({ id: 'top', zOrder: 3 }),
      layer({ id: 'back', zOrder: 1 }),
      layer({ id: 'mid', zOrder: 2 }),
    ];
    composeScene(ctx.as(), scene(layers, bitmapsFor(['top', 'back', 'mid'])));
    const names = ctx.of('drawImage').map((c) => (c.args[0] as { name: string }).name);
    expect(names).toEqual(['back', 'mid', 'top']);
  });

  it('skips hidden layers entirely and any layer named in options.skip', () => {
    const ctx = new RecordingContext();
    const layers = [
      layer({ id: 'a', zOrder: 1 }),
      layer({ id: 'hidden', zOrder: 2, visible: false }),
      layer({ id: 'skipped', zOrder: 3 }),
    ];
    composeScene(ctx.as(), scene(layers, bitmapsFor(['a', 'hidden', 'skipped'])), {
      skip: new Set(['skipped']),
    });
    expect(ctx.of('drawImage')).toHaveLength(1);
  });

  it('clears, then paints the background colour first when there is one', () => {
    const ctx = new RecordingContext();
    composeScene(ctx.as(), scene([layer({ id: 'a' })], bitmapsFor(['a']), { backgroundColor: '#112233' }));
    const ops = ctx.ops();
    expect(ops.indexOf('clearRect')).toBeLessThan(ops.indexOf('fillRect'));
    expect(ops.indexOf('fillRect')).toBeLessThan(ops.indexOf('drawImage'));
    expect(ctx.of('fillRect')[0].fillStyle).toBe('#112233');
    expect(ctx.of('fillRect')[0].args).toEqual([0, 0, 1280, 720]);
  });

  it('paints no background and no fillRect for a transparent canvas', () => {
    const ctx = new RecordingContext();
    composeScene(ctx.as(), scene([layer({ id: 'a' })], bitmapsFor(['a'])));
    expect(ctx.of('fillRect')).toHaveLength(0);
  });

  it('draws nothing for a bitmap layer whose image has not loaded yet', () => {
    const ctx = new RecordingContext();
    composeScene(ctx.as(), scene([layer({ id: 'pending' })], new Map()));
    expect(ctx.of('drawImage')).toHaveLength(0);
  });

  it('leaves the context balanced: every save has a restore', () => {
    const ctx = new RecordingContext();
    composeScene(ctx.as(), scene([layer({ id: 'a' }), layer({ id: 'b', zOrder: 2 })], bitmapsFor(['a', 'b'])));
    expect(ctx.of('save').length).toBe(ctx.of('restore').length);
    expect(ctx.globalAlpha).toBe(1);
    expect(ctx.globalCompositeOperation).toBe('source-over');
  });
});

describe('composeScene: transform', () => {
  it('translates to the centre, rotates clockwise, scales with flips, and draws around the origin', () => {
    const ctx = new RecordingContext();
    const l = layer({
      id: 'a',
      transform: { x: 100, y: 50, scale: 2, rotation: 90, flipX: true, flipY: false },
    });
    composeScene(ctx.as(), scene([l], bitmapsFor(['a'])));
    const seq = ctx.calls.filter((c) => ['translate', 'rotate', 'scale', 'drawImage'].includes(c.op));
    expect(seq.map((c) => c.op)).toEqual(['translate', 'rotate', 'scale', 'drawImage']);
    // centre = top-left + (size × scale) / 2 = (100 + 200, 50 + 100)
    expect(seq[0].args).toEqual([300, 150]);
    expect(seq[1].args[0]).toBeCloseTo(Math.PI / 2, 10);
    expect(seq[2].args).toEqual([-2, 2]);
    expect(seq[3].args.slice(1)).toEqual([-100, -50, 200, 100]);
  });

  it('flips vertically with a negative y scale', () => {
    const ctx = new RecordingContext();
    const l = layer({ id: 'a', transform: { x: 0, y: 0, scale: 1, rotation: 0, flipX: false, flipY: true } });
    composeScene(ctx.as(), scene([l], bitmapsFor(['a'])));
    expect(ctx.of('scale')[0].args).toEqual([1, -1]);
  });
});

describe('composeScene: opacity and blend mode', () => {
  it('applies opacity as globalAlpha at the moment of the draw, and resets it after', () => {
    const ctx = new RecordingContext();
    composeScene(ctx.as(), scene([layer({ id: 'a', opacity: 0.4 })], bitmapsFor(['a'])));
    expect(ctx.of('drawImage')[0].alpha).toBe(0.4);
    expect(ctx.globalAlpha).toBe(1);
  });

  it('clamps an out-of-range opacity', () => {
    const ctx = new RecordingContext();
    composeScene(ctx.as(), scene([layer({ id: 'a', opacity: 7 })], bitmapsFor(['a'])));
    expect(ctx.of('drawImage')[0].alpha).toBe(1);
  });

  it('maps every blend mode to a canvas composite operation, normal being source-over', () => {
    expect(BLEND_TO_COMPOSITE.normal).toBe('source-over');
    for (const mode of BLEND_MODES) {
      const ctx = new RecordingContext();
      composeScene(ctx.as(), scene([layer({ id: 'a', blendMode: mode })], bitmapsFor(['a'])));
      expect(ctx.of('drawImage')[0].composite).toBe(mode === 'normal' ? 'source-over' : mode);
    }
  });

  it('covers every blend mode the contract names, and only those', () => {
    expect(Object.keys(BLEND_TO_COMPOSITE).sort()).toEqual([...BLEND_MODES].sort());
  });
});

describe('composeScene: cut-out with a matte', () => {
  function scratchFactory() {
    const made: { ctx: RecordingContext; canvas: ReturnType<typeof fakeBitmap> }[] = [];
    const factory: ScratchFactory = (w, h) => {
      const ctx = new RecordingContext();
      const canvas = fakeBitmap(w, h, 'scratch');
      made.push({ ctx, canvas });
      return { canvas, ctx: ctx.as() };
    };
    return { factory, made };
  }

  const cutout = layer({ id: 'cut', kind: 'cutout', matteUrl: '/m' });
  const bitmaps: BitmapMap = new Map([
    ['cut', { image: fakeBitmap(200, 100, 'subject'), matte: fakeBitmap(200, 100, 'matte') }],
  ]);

  it('masks on a scratch canvas: bitmap first, then destination-in the matte', () => {
    const ctx = new RecordingContext();
    const { factory, made } = scratchFactory();
    composeScene(ctx.as(), scene([cutout], bitmaps), { scratch: factory });
    expect(made).toHaveLength(1);
    const drawn = made[0].ctx.of('drawImage');
    expect((drawn[0].args[0] as { name: string }).name).toBe('subject');
    expect(drawn[0].composite).toBe('source-over');
    expect((drawn[1].args[0] as { name: string }).name).toBe('matte');
    expect(drawn[1].composite).toBe('destination-in');
  });

  it('never uses destination-in on the main canvas, so lower layers survive', () => {
    const ctx = new RecordingContext();
    composeScene(ctx.as(), scene([cutout], bitmaps), { scratch: scratchFactory().factory });
    expect(ctx.calls.some((c) => c.composite === 'destination-in')).toBe(false);
    expect((ctx.of('drawImage')[0].args[0] as { name: string }).name).toBe('scratch');
  });

  it('draws the bitmap straight through when no scratch surface is available', () => {
    const ctx = new RecordingContext();
    composeScene(ctx.as(), scene([cutout], bitmaps));
    expect((ctx.of('drawImage')[0].args[0] as { name: string }).name).toBe('subject');
  });

  it.each(['source', 'image', 'generated'] as const)('a %s layer with a mask is matted too (destination-in on a scratch)', (kind) => {
    const ctx = new RecordingContext();
    const { factory, made } = scratchFactory();
    composeScene(ctx.as(), scene([layer({ id: 'cut', kind, matteUrl: '/m' })], bitmaps), { scratch: factory });
    expect(made).toHaveLength(1);
    const drawn = made[0].ctx.of('drawImage');
    expect(drawn[1].composite).toBe('destination-in');
    expect(ctx.calls.some((c) => c.composite === 'destination-in')).toBe(false);
  });

  it('a layer with no matte bitmap is drawn plainly, with no scratch', () => {
    const ctx = new RecordingContext();
    const { factory, made } = scratchFactory();
    const plain: BitmapMap = new Map([['cut', { image: fakeBitmap(200, 100, 'subject') }]]);
    composeScene(ctx.as(), scene([layer({ id: 'cut', kind: 'source' })], plain), { scratch: factory });
    expect(made).toHaveLength(0);
  });
});

describe('text layout and drawing', () => {
  const text = (over: Partial<TextContent> = {}): TextContent =>
    defaultTextContent({ text: 'aaa bbb ccc', stroke: null, shadow: null, fontSize: 20, lineHeight: 1.5, ...over });

  it('quotes the family in the font shorthand', () => {
    expect(fontString(text({ fontFamily: 'Chakra Petch', fontWeight: 700, fontSize: 96 }))).toBe(
      '700 96px "Chakra Petch", sans-serif',
    );
  });

  it('keeps hard line breaks and sizes the box from the widest line', () => {
    const layout = layoutText(text({ text: 'ab\ncdef' }), measure);
    expect(layout.lines.map((l) => l.text)).toEqual(['ab', 'cdef']);
    expect(layout.width).toBe(40);
    expect(layout.height).toBe(2 * 30);
  });

  it('wraps greedily at maxWidth and keeps an over-long word whole', () => {
    expect(layoutText(text({ maxWidth: 60 }), measure).lines.map((l) => l.text)).toEqual(['aaa', 'bbb', 'ccc']);
    expect(layoutText(text({ text: 'abcdefghij kl', maxWidth: 30 }), measure).lines.map((l) => l.text)).toEqual([
      'abcdefghij',
      'kl',
    ]);
  });

  it('uses maxWidth as the box width when it is set', () => {
    expect(layoutText(text({ text: 'ab', maxWidth: 300 }), measure).width).toBe(300);
  });

  it('upper-cases before measuring when asked', () => {
    expect(layoutText(text({ text: 'ab', uppercase: true }), measure).lines[0].text).toBe('AB');
  });

  it('adds letter spacing to every measured line', () => {
    expect(layoutText(text({ text: 'abc', letterSpacing: 2 }), measure).lines[0].width).toBe(36);
  });

  it('draws a text layer with fillText, one call per line, aligned inside its box', () => {
    const ctx = new RecordingContext();
    const content = text({ text: 'ab\ncd', align: 'center', color: '#abcdef', maxWidth: 100 });
    const placed = placeLayers(
      [{ ...(layer({ id: 't', kind: 'text', content }) as EditorLayer) }],
      new Map(),
      measure,
    );
    composeScene(ctx.as(), scene(placed, new Map()));
    const fills = ctx.of('fillText');
    expect(fills.map((c) => c.args[0])).toEqual(['ab', 'cd']);
    // centre alignment draws at half the box width, relative to the box's top-left (-w/2)
    expect(fills[0].args[1]).toBe(0);
    expect(fills[0].fillStyle).toBe('#abcdef');
    expect(ctx.of('drawImage')).toHaveLength(0);
  });

  it('draws an outer stroke first at twice its width, then the fill, with the shadow on the first pass only', () => {
    const ctx = new RecordingContext();
    const content = text({
      text: 'ab',
      stroke: { color: '#000000', width: 6 },
      shadow: { color: 'rgba(0,0,0,0.6)', blur: 18, offsetX: 0, offsetY: 8 },
    });
    const placed = placeLayers([layer({ id: 't', kind: 'text', content }) as EditorLayer], new Map(), measure);
    composeScene(ctx.as(), scene(placed, new Map()));
    const order = ctx.calls.filter((c) => c.op === 'strokeText' || c.op === 'fillText');
    expect(order.map((c) => c.op)).toEqual(['strokeText', 'fillText']);
    expect(order[0].lineWidth).toBe(12);
    expect(order[0].shadowBlur).toBe(18);
    expect(order[1].shadowBlur).toBe(0);
  });

  it('draws a plain fill (no stroke call) when there is no outline', () => {
    const ctx = new RecordingContext();
    const placed = placeLayers([layer({ id: 't', kind: 'text', content: text() }) as EditorLayer], new Map(), measure);
    composeScene(ctx.as(), scene(placed, new Map()));
    expect(ctx.of('strokeText')).toHaveLength(0);
  });
});

describe('drawOverlayOnLayer', () => {
  it('draws the overlay through the layer\'s own transform, at the given alpha, and restores', () => {
    const ctx = new RecordingContext();
    const l = layer({ id: 'o', transform: { x: 10, y: 20, scale: 2, rotation: 30, flipX: false, flipY: false } });
    drawOverlayOnLayer(ctx.as(), l, fakeBitmap(200, 100, 'tint'), 0.55);
    expect(ctx.ops()).toEqual(['save', 'translate', 'rotate', 'scale', 'drawImage', 'restore']);
    expect(ctx.of('drawImage')[0].alpha).toBe(0.55);
    expect(ctx.of('drawImage')[0].args.slice(1)).toEqual([-100, -50, 200, 100]);
    expect(ctx.globalAlpha).toBe(1);
  });
});

describe('placeLayers', () => {
  it('prefers the decoded bitmap size, falls back to the DTO size, and drops a layer with neither', () => {
    const loaded: BitmapMap = new Map([['a', { image: fakeBitmap(640, 480) }]]);
    const placed = placeLayers(
      [
        layer({ id: 'a', width: 10, height: 10 }) as EditorLayer,
        layer({ id: 'b', width: 300, height: 200 }) as EditorLayer,
        layer({ id: 'c', width: null, height: null }) as EditorLayer,
      ],
      loaded,
      measure,
    );
    expect(placed.map((p) => [p.id, p.size])).toEqual([
      ['a', { w: 640, h: 480 }],
      ['b', { w: 300, h: 200 }],
    ]);
  });

  it('sizes a text layer from its layout, and drops one with no content', () => {
    const placed = placeLayers(
      [
        layer({ id: 't', kind: 'text', content: defaultTextContent({ text: 'abcd', stroke: null, fontSize: 10, lineHeight: 1 }) }) as EditorLayer,
        layer({ id: 'empty', kind: 'text', content: null }) as EditorLayer,
      ],
      new Map(),
      measure,
    );
    expect(placed).toHaveLength(1);
    expect(placed[0].size).toEqual({ w: 40, h: 10 });
  });
});

describe('hit testing and bounds', () => {
  it('returns the top-most visible layer under the point', () => {
    const layers = [
      layer({ id: 'back', zOrder: 1 }),
      layer({ id: 'front', zOrder: 2, transform: { x: 50, y: 0, scale: 1, rotation: 0, flipX: false, flipY: false } }),
    ];
    expect(hitTest(layers, { x: 100, y: 50 })?.id).toBe('front');
    expect(hitTest(layers, { x: 10, y: 50 })?.id).toBe('back');
    expect(hitTest(layers, { x: 900, y: 50 })).toBeNull();
  });

  it('skips hidden layers unless asked, and still hits a locked one', () => {
    const layers = [
      layer({ id: 'locked', zOrder: 1, locked: true }),
      layer({ id: 'hidden', zOrder: 2, visible: false }),
    ];
    expect(hitTest(layers, { x: 10, y: 10 })?.id).toBe('locked');
    expect(hitTest(layers, { x: 10, y: 10 }, { includeHidden: true })?.id).toBe('hidden');
  });

  it('tests against the rotated rectangle, not its unrotated box', () => {
    // 200×100 at the origin, rotated 90° about its centre (100, 50): it now occupies x 50..150, y -50..150.
    const l = layer({ id: 'r', transform: { x: 0, y: 0, scale: 1, rotation: 90, flipX: false, flipY: false } });
    expect(hitTest([l], { x: 100, y: 140 })?.id).toBe('r');
    expect(hitTest([l], { x: 10, y: 50 })).toBeNull();
  });

  it('reports rotated corners (clockwise) and their bounding box', () => {
    const l = layer({ id: 'r', transform: { x: 0, y: 0, scale: 1, rotation: 90, flipX: false, flipY: false } });
    const b = layerBounds(l);
    expect(b.center).toEqual({ x: 100, y: 50 });
    // top-left (0,0) rotates 90° clockwise about (100,50) to (150,-50)
    expect(b.corners[0].x).toBeCloseTo(150, 6);
    expect(b.corners[0].y).toBeCloseTo(-50, 6);
    expect(b.aabb.x).toBeCloseTo(50, 6);
    expect(b.aabb.w).toBeCloseTo(100, 6);
    expect(b.aabb.h).toBeCloseTo(200, 6);
  });

  it('scales bounds about the top-left corner', () => {
    const l = layer({ id: 's', transform: { x: 10, y: 20, scale: 2, rotation: 0, flipX: false, flipY: false } });
    const b = layerBounds(l);
    expect(b.aabb).toEqual({ x: 10, y: 20, w: 400, h: 200 });
    expect(b.center).toEqual({ x: 210, y: 120 });
  });

  it('round-trips canvas and layer coordinates through any rotation, scale and flip', () => {
    fc.assert(
      fc.property(
        fc.record({
          x: fc.integer({ min: -500, max: 500 }),
          y: fc.integer({ min: -500, max: 500 }),
          scale: fc.double({ min: 0.1, max: 8, noNaN: true }),
          rotation: fc.integer({ min: -360, max: 360 }),
          flipX: fc.boolean(),
          flipY: fc.boolean(),
        }),
        fc.record({ x: fc.integer({ min: 0, max: 200 }), y: fc.integer({ min: 0, max: 100 }) }),
        (transform, local) => {
          const l = layer({ id: 'p', transform });
          const back = canvasToLayerLocal(l, layerLocalToCanvas(l, local));
          expect(back.x).toBeCloseTo(local.x, 5);
          expect(back.y).toBeCloseTo(local.y, 5);
        },
      ),
    );
  });

  it('maps a flipped layer so its left edge is on the right', () => {
    const l = layer({ id: 'f', transform: { x: 0, y: 0, scale: 1, rotation: 0, flipX: true, flipY: false } });
    expect(canvasToLayerLocal(l, { x: 0, y: 0 }).x).toBe(200);
    expect(canvasToLayerLocal(l, { x: 200, y: 0 }).x).toBe(0);
  });
});

describe('cropped layers', () => {
  const crop = { x: 0.25, y: 0.5, w: 0.5, h: 0.5 };
  it('occupy only the kept part', () => {
    const placed = placeLayers([layer({ id: 'c', crop })], new Map([['c', { image: fakeBitmap(200, 100, 'c') }]]), measure);
    expect(placed[0].size).toEqual({ w: 100, h: 50 });
  });
  it('draw only the kept window of the stored picture', () => {
    const ctx = new RecordingContext();
    composeScene(ctx.as(), scene([layer({ id: 'c', crop, size: { w: 100, h: 50 } })], bitmapsFor(['c'])));
    const args = ctx.of('drawImage')[0].args as number[];
    // source window in the bitmap's own pixels, then the destination box
    expect(args.slice(1)).toEqual([50, 50, 100, 50, -50, -25, 100, 50]);
  });
});

