import { describe, expect, it } from 'vitest';
import { refitCanvas, refitPlacement, sameShape } from './refit';
import type { EditorDocState, EditorLayer } from './model';

const layer = (id: string, x: number, y: number, scale: number): EditorLayer => ({
  id,
  kind: 'image',
  role: 'overlay',
  label: id,
  zOrder: 0,
  visible: true,
  locked: false,
  opacity: 1,
  blendMode: 'normal',
  transform: { x, y, scale, rotation: 0, flipX: false, flipY: false },
  width: 100,
  height: 100,
  imageUrl: '',
  matteUrl: null,
  content: null,
  segmentationPrompt: null,
});

const doc = (): EditorDocState => ({
  canvasWidth: 1920,
  canvasHeight: 1080,
  backgroundColor: null,
  layers: [layer('a', 0, 0, 1), layer('b', 960, 540, 2)],
});

describe('refitPlacement', () => {
  it('scales to fit and centres', () => {
    const p = refitPlacement({ width: 1920, height: 1080 }, { width: 1080, height: 1350 });
    expect(p?.factor).toBeCloseTo(1080 / 1920);
    expect(p?.offsetX).toBeCloseTo(0);
    expect(p?.offsetY).toBeCloseTo((1350 - 1080 * (1080 / 1920)) / 2);
  });
  it('is null for unusable sizes', () => {
    expect(refitPlacement({ width: 0, height: 10 }, { width: 10, height: 10 })).toBeNull();
  });
});

describe('sameShape', () => {
  it('tolerates rounding but not a new shape', () => {
    expect(sameShape({ width: 1920, height: 1080 }, { width: 1280, height: 720 })).toBe(true);
    expect(sameShape({ width: 1920, height: 1080 }, { width: 1080, height: 1080 })).toBe(false);
  });
});

describe('refitCanvas', () => {
  it('moves and scales every layer, and one undo restores everything', () => {
    const cmd = refitCanvas(doc(), { width: 960, height: 540 }, 'Fit');
    expect(cmd).not.toBeNull();
    const after = cmd!.do(doc());
    expect(after.canvasWidth).toBe(960);
    expect(after.layers[1].transform).toMatchObject({ x: 480, y: 270, scale: 1 });
    const back = cmd!.undo(after);
    expect(back).toEqual(doc());
  });
  it('does nothing when the size is unchanged', () => {
    expect(refitCanvas(doc(), { width: 1920, height: 1080 }, 'Fit')).toBeNull();
  });
});
