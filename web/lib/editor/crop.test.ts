import { describe, expect, it } from 'vitest';
import { clampCrop, cropToRect, croppedTransform, dragCrop, naturalSize, rectToCrop } from './crop';
import type { PlacedLayer } from './compose';

const layer = (over: Partial<PlacedLayer['transform']> = {}): PlacedLayer => ({
  id: 'p', kind: 'image', role: 'overlay', label: 'p', zOrder: 1, visible: true, locked: false, opacity: 1, blendMode: 'normal',
  transform: { x: 100, y: 50, scale: 0.5, rotation: 0, flipX: false, flipY: false, ...over },
  width: 400, height: 200, imageUrl: '', matteUrl: null, content: null, segmentationPrompt: null, size: { w: 400, h: 200 },
});

describe('crop geometry', () => {
  it('keeps the rectangle inside the picture and at least a few pixels big', () => {
    expect(clampCrop({ x: -5, y: 190, w: 500, h: 50 }, 400, 200)).toEqual({ x: 0, y: 150, w: 400, h: 50 });
    expect(clampCrop({ x: 10, y: 10, w: 0, h: 0 }, 400, 200)).toEqual({ x: 10, y: 10, w: 8, h: 8 });
  });
  it('drags an edge, a corner and the whole box', () => {
    const r = { x: 100, y: 50, w: 100, h: 100 };
    expect(dragCrop(r, 'e', 50, 0, 400, 200)).toEqual({ x: 100, y: 50, w: 150, h: 100 });
    expect(dragCrop(r, 'nw', -500, -500, 400, 200)).toEqual({ x: 0, y: 0, w: 200, h: 150 });
    expect(dragCrop(r, 'move', 1000, 0, 400, 200)).toEqual({ x: 300, y: 50, w: 100, h: 100 });
    expect(dragCrop(r, 'w', 500, 0, 400, 200).w).toBe(8);
  });
  it('places the cropped layer so the kept part does not move on the canvas', () => {
    const t = croppedTransform(layer(), { x: 100, y: 40, w: 200, h: 100 });
    expect(t).toMatchObject({ x: 150, y: 70, scale: 0.5 });
  });
  it('is right for a flipped layer too', () => {
    const t = croppedTransform(layer({ flipX: true }), { x: 0, y: 0, w: 100, h: 200 });
    // flipped: local x 0..100 shows at the right-hand end of the layer (canvas x 250..300)
    expect(t.x).toBe(250);
  });
});

describe('non-destructive crop (fractions of the stored picture)', () => {
  const nat = { w: 400, h: 200 };
  it('turns a box into stored fractions and back, and the whole picture into no crop', () => {
    expect(rectToCrop({ x: 100, y: 50, w: 200, h: 100 }, nat)).toEqual({ x: 0.25, y: 0.25, w: 0.5, h: 0.5 });
    expect(cropToRect({ x: 0.25, y: 0.25, w: 0.5, h: 0.5 }, nat)).toEqual({ x: 100, y: 50, w: 200, h: 100 });
    expect(rectToCrop({ x: 0, y: 0, w: 400, h: 200 }, nat)).toBeNull();
    expect(cropToRect(null, nat)).toEqual({ x: 0, y: 0, w: 400, h: 200 });
  });
  it('reads the stored picture size off a layer that is already cropped', () => {
    const cropped = { ...layer(), size: { w: 200, h: 100 }, crop: { x: 0.25, y: 0.25, w: 0.5, h: 0.5 } };
    expect(naturalSize(cropped)).toEqual({ w: 400, h: 200 });
  });
  it('widening a crop keeps the visible part where it is', () => {
    // cropped to the middle half; its top-left sits at canvas (150, 75) at scale 0.5
    const cropped: PlacedLayer = {
      ...layer({ x: 150, y: 75 }),
      size: { w: 200, h: 100 },
      crop: { x: 0.25, y: 0.25, w: 0.5, h: 0.5 },
    };
    // widen back to the whole picture: the picture's top-left moves out by the cropped margin (100, 50) * 0.5
    expect(croppedTransform(cropped, { x: 0, y: 0, w: 400, h: 200 })).toMatchObject({ x: 100, y: 50, scale: 0.5 });
  });
});

