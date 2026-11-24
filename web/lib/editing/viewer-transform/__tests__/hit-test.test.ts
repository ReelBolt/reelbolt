import { describe, expect, it } from 'vitest';
import { caption, doc, media, title, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { FONT_SIZE_MAX, FONT_SIZE_MIN, CORNER_HANDLES, captionFrame, hitTest, nearestTextPosition, pictureTargetsAt, pointInFrame, selectedPicture, textPositionOps, titleFontSizeFromDrag, titleFontSizeOps, titleFrame } from '..';

const CANVAS = { width: 1920, height: 1080 };
const noSource = () => null;
const corner = (id: string) => CORNER_HANDLES.find((h) => h.id === id)!;

describe('pictureTargetsAt and hitTest', () => {
  it('the topmost picture wins: a later track is above an earlier one', () => {
    const d = doc([track(TRACKS.v1, 'video', [media('low', 0, 100, 0)]), track(TRACKS.v2, 'video', [media('high', 0, 100, 0)])]);
    const targets = pictureTargetsAt(d, 10, noSource);
    expect(targets.map((t) => t.id)).toEqual(['high', 'low']);
    expect(hitTest(targets, { x: 960, y: 540 })?.id).toBe('high');
  });

  it('Alt-click cycles to the next item below, wraps, and starts at the top when the current one is not under the point', () => {
    const d = doc([track(TRACKS.v1, 'video', [media('low', 0, 100, 0)]), track(TRACKS.v2, 'video', [media('high', 0, 100, 0)])]);
    const targets = pictureTargetsAt(d, 10, noSource);
    const p = { x: 960, y: 540 };
    expect(hitTest(targets, p, 'high')?.id).toBe('low');
    expect(hitTest(targets, p, 'low')?.id).toBe('high');
    expect(hitTest(targets, p, 'unknown')?.id).toBe('high');
  });

  it('empty canvas hits nothing, and items off screen at the frame are not targets', () => {
    const small = media('small', 0, 50, 0, { transform: { scale: { value: 0.2, keys: null } } });
    const d = doc([track(TRACKS.v1, 'video', [small])]);
    expect(hitTest(pictureTargetsAt(d, 10, noSource), { x: 100, y: 100 })).toBeNull();
    expect(pictureTargetsAt(d, 60, noSource)).toEqual([]);
  });

  it('a scaled and moved picture is hit only inside its transformed rectangle', () => {
    const clip = media('m', 0, 100, 0, { transform: { positionX: { value: 0.25, keys: null }, scale: { value: 0.5, keys: null } } });
    const targets = pictureTargetsAt(doc([track(TRACKS.v1, 'video', [clip])]), 10, noSource);
    expect(hitTest(targets, { x: 1440, y: 540 })?.id).toBe('m');
    expect(hitTest(targets, { x: 700, y: 540 })).toBeNull();
  });

  it('a cropped and rotated picture is hit through the compositor geometry', () => {
    // crop the left half away: 960x1080 at scale 1, turned a quarter turn, so it is 1080 wide and 960 tall on screen
    const clip = media('m', 0, 100, 0, { crop: { left: 0.5 }, transform: { rotation: { value: 90, keys: null } } });
    const targets = pictureTargetsAt(doc([track(TRACKS.v1, 'video', [clip])]), 10, noSource);
    expect(hitTest(targets, { x: 960 + 500, y: 540 })?.id).toBe('m');
    expect(hitTest(targets, { x: 960, y: 540 + 500 })).toBeNull();
  });

  it('text is above pictures and is picked by its own box', () => {
    const t = title('t', 0, 100);
    const d = doc([track(TRACKS.v1, 'video', [media('m', 0, 100, 0)]), track(TRACKS.v2, 'video', [t])]);
    const targets = pictureTargetsAt(d, 10, noSource);
    expect(targets[0]).toMatchObject({ id: 't', kind: 'titleClip' });
    const box = titleFrame(t, CANVAS);
    expect(hitTest(targets, { x: box.cx, y: box.cy })?.id).toBe('t');
    expect(hitTest(targets, { x: 100, y: 100 })?.id).toBe('m');
  });

  it('pointInFrame honours rotation', () => {
    const f = { cx: 500, cy: 500, w: 400, h: 100, rotation: 90 };
    expect(pointInFrame(f, { x: 500, y: 650 })).toBe(true);
    expect(pointInFrame(f, { x: 650, y: 500 })).toBe(false);
  });
});

describe('selectedPicture', () => {
  it('returns the one selected media clip, title or caption that is on screen', () => {
    const d = doc([track(TRACKS.v1, 'video', [media('m', 0, 100, 0), title('t', 0, 100), caption('c', 0, 100)])]);
    expect(selectedPicture(d, ['m'], 10)?.kind).toBe('mediaClip');
    expect(selectedPicture(d, ['t'], 10)?.kind).toBe('titleClip');
    expect(selectedPicture(d, ['c'], 10)?.kind).toBe('caption');
    expect(selectedPicture(d, ['m'], 200)).toBeNull();
    expect(selectedPicture(d, ['m', 't'], 10)).toBeNull();
  });
});

describe('title and caption pad', () => {
  it('snaps a point to the nearest of the nine cells', () => {
    expect(nearestTextPosition({ x: 100, y: 100 }, CANVAS)).toBe('topLeft');
    expect(nearestTextPosition({ x: 960, y: 100 }, CANVAS)).toBe('topCenter');
    expect(nearestTextPosition({ x: 1900, y: 100 }, CANVAS)).toBe('topRight');
    expect(nearestTextPosition({ x: 100, y: 540 }, CANVAS)).toBe('middleLeft');
    expect(nearestTextPosition({ x: 960, y: 540 }, CANVAS)).toBe('center');
    expect(nearestTextPosition({ x: 1900, y: 1070 }, CANVAS)).toBe('bottomRight');
    expect(nearestTextPosition({ x: -50, y: 2000 }, CANVAS)).toBe('bottomLeft');
  });

  it('a pad move writes one position op, and nothing when the cell is unchanged', () => {
    const t = title('t', 0, 100);
    expect(textPositionOps(t, 'topRight')).toEqual([{ type: 'setProperty', itemId: 't', key: 'position', value: 'topRight' }]);
    expect(textPositionOps(t, 'center')).toEqual([]);
    expect(textPositionOps(caption('c', 0, 100), 'bottomCenter')).toEqual([]);
  });

  it('the ghost frame follows the pad cell', () => {
    const t = title('t', 0, 100);
    expect(titleFrame(t, CANVAS, { position: 'topLeft' }).cx).toBeLessThan(titleFrame(t, CANVAS, { position: 'topRight' }).cx);
    expect(captionFrame(caption('c', 0, 100), CANVAS, { position: 'topCenter' }).cy).toBeLessThan(captionFrame(caption('c', 0, 100), CANVAS).cy);
  });

  it('dragging a corner outwards grows the font size, inwards shrinks it, always inside the limits', () => {
    const t = title('t', 0, 100);
    const f = titleFrame(t, CANVAS);
    const br = corner('br');
    const outward = { x: f.cx + f.w, y: f.cy + f.h };
    expect(titleFontSizeFromDrag(32, f, br, outward)).toBeGreaterThan(32);
    expect(titleFontSizeFromDrag(32, f, br, { x: f.cx - f.w / 2 + 1, y: f.cy - f.h / 2 + 1 })).toBeLessThan(32);
    expect(titleFontSizeFromDrag(32, f, br, { x: f.cx - f.w * 10, y: f.cy - f.h * 10 })).toBe(FONT_SIZE_MIN);
    expect(titleFontSizeFromDrag(500, f, br, { x: f.cx + f.w * 50, y: f.cy + f.h * 50 })).toBe(FONT_SIZE_MAX);
    expect(titleFontSizeOps(t, 40)).toEqual([{ type: 'setProperty', itemId: 't', key: 'fontSize', value: 40 }]);
    expect(titleFontSizeOps(t, 32)).toEqual([]);
  });
});
