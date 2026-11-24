import { describe, expect, it } from 'vitest';
import { DEFAULT_NEW_PICTURE_SHAPE_ID, defaultPictureName, NEW_PICTURE_SHAPES, newPictureSize } from './new-picture';

const shape = (id: string) => NEW_PICTURE_SHAPES.find((s) => s.id === id)!;

describe('new picture shapes', () => {
  it('a YouTube thumbnail is 1280 x 720 and is the default', () => {
    expect(newPictureSize(shape('youtube'), { width: '', height: '' })).toEqual({ width: 1280, height: 720 });
    expect(DEFAULT_NEW_PICTURE_SHAPE_ID).toBe('youtube');
    expect(defaultPictureName(shape('youtube'))).toBe('Untitled thumbnail');
  });
  it('a post is 1080 x 1350', () => {
    expect(newPictureSize(shape('post'), { width: '', height: '' })).toEqual({ width: 1080, height: 1350 });
  });
  it('custom sizes are clamped to what the server takes', () => {
    expect(newPictureSize(shape('custom'), { width: 5, height: 99999 })).toEqual({ width: 16, height: 8192 });
    expect(newPictureSize(shape('custom'), { width: '', height: 500 })).toBeNull();
  });
  it('names a blank picture sensibly', () => {
    expect(defaultPictureName(shape('story'))).toBe('Untitled story');
    expect(defaultPictureName(shape('custom'))).toBe('Untitled picture');
  });
});
