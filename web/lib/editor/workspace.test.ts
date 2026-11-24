import { describe, expect, it } from 'vitest';
import { RecordingContext, fakeBitmap } from './__tests__/recording-context';
import { docRectToDevice, paintWorkspace, OUTSIDE_DIM, WORKSPACE_BG } from './workspace';
import type { PlacedLayer } from './compose';

const layer = (id: string, x: number): PlacedLayer =>
  ({
    id,
    kind: 'source',
    role: 'background',
    label: id,
    zOrder: 1,
    visible: true,
    locked: false,
    opacity: 1,
    blendMode: 'normal',
    transform: { x, y: 0, scale: 1, rotation: 0, flipX: false, flipY: false },
    width: 400,
    height: 300,
    imageUrl: '',
    matteUrl: null,
    content: null,
    segmentationPrompt: null,
    size: { w: 400, h: 300 },
  }) as PlacedLayer;

const surface = () => {
  const ctx = new RecordingContext();
  return { ctx, surface: { canvas: { width: 1, height: 1 } as never, ctx: ctx.as() } };
};

describe('paintWorkspace', () => {
  const frame = { width: 800, height: 600 };
  const view = { zoom: 0.5, x: 100, y: 50 };

  it('draws a layer that reaches past the frame in full — nothing clips it — then dims what is outside', () => {
    const main = new RecordingContext();
    const layers = surface();
    const outside = layer('wide', 600); // 600..1000, past the 800-wide frame
    paintWorkspace(
      main.as(),
      {
        view,
        dpr: 1,
        size: { width: 1000, height: 700 },
        frame,
        backgroundColor: null,
        layers: [outside],
        bitmaps: new Map([['wide', { image: fakeBitmap(400, 300) }]]),
        checker: '#333',
      },
      { layers: layers.surface, blur: null },
    );
    // the layer is drawn in full on the layer surface, in document space, with no clip anywhere
    const draws = layers.ctx.of('drawImage');
    expect(draws).toHaveLength(1);
    expect(draws[0].args.slice(1)).toEqual([-200, -150, 400, 300]);
    expect(layers.ctx.ops()).not.toContain('clip');
    expect(layers.ctx.of('setTransform').at(-1)?.args).toEqual([0.5, 0, 0, 0.5, 100, 50]);
    // the workspace is painted, then the outside of the frame is dimmed with an even-odd fill
    expect(main.of('fillRect')[0].fillStyle).toBe(WORKSPACE_BG);
    const dim = main.of('fill').at(-1);
    expect(dim?.args).toEqual(['evenodd']);
    expect(dim?.fillStyle).toBe(OUTSIDE_DIM);
    expect(main.of('rect')[1].args).toEqual([100, 50, 400, 300]);
  });

  it('paints a background colour inside the frame only, under the layers, so blends match the export', () => {
    const main = new RecordingContext();
    const layers = surface();
    paintWorkspace(
      main.as(),
      {
        view,
        dpr: 2,
        size: { width: 500, height: 400 },
        frame,
        backgroundColor: '#ff0000',
        layers: [],
        bitmaps: new Map(),
        checker: null,
      },
      { layers: layers.surface, blur: null },
    );
    const fills = layers.ctx.of('fillRect');
    expect(fills).toHaveLength(1);
    expect(fills[0].fillStyle).toBe('#ff0000');
    expect(fills[0].args).toEqual([0, 0, 800, 600]);
  });

  it('draws the AI effect over its region only, clipped to it', () => {
    const main = new RecordingContext();
    const layers = surface();
    const blur = surface();
    paintWorkspace(
      main.as(),
      {
        view: { zoom: 1, x: 0, y: 0 },
        dpr: 1,
        size: { width: 800, height: 600 },
        frame,
        backgroundColor: null,
        layers: [],
        bitmaps: new Map(),
        checker: null,
        effect: { region: { x: 10, y: 20, w: 100, h: 50 }, look: { blurPx: 14, alpha: 1, shimmer: 0.5, tint: 0.08, done: false } },
      },
      { layers: layers.surface, blur: blur.surface },
    );
    const clipRect = main.of('rect').at(-1);
    expect(clipRect?.args).toEqual([10, 20, 100, 50]);
    expect(main.ops()).toContain('clip');
    expect(main.ops()).toContain('createLinearGradient');
    // the region's pixels were shrunk onto the blur surface and drawn back up
    expect(blur.ctx.of('drawImage').length).toBeGreaterThan(1);
  });

  it('converts document rectangles to device pixels through the view', () => {
    expect(docRectToDevice({ zoom: 2, x: 10, y: 5 }, 2, { x: 1, y: 2, w: 3, h: 4 })).toEqual({ x: 24, y: 18, w: 12, h: 16 });
  });
});
