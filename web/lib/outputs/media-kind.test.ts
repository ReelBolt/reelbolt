import { describe, expect, it } from 'vitest';
import { outputMediaKind } from './media-kind';

describe('outputMediaKind', () => {
  it.each([
    ['projects/p/outputFiles/e/step-3-image1.png', 'image'],
    ['photo.JPG', 'image'],
    ['a.jpeg', 'image'],
    ['a.webp', 'image'],
    ['projects/p/outputFiles/e/step-2-a1.wav', 'audio'],
    ['x.mp3', 'audio'],
    ['projects/p/outputFiles/e/9f1c.mp4', 'video'],
    ['clip.webm', 'video'],
    ['9f1c', 'video'],
    ['', 'video'],
    [null, 'video'],
  ])('%s is %s', (name, kind) => {
    expect(outputMediaKind(name)).toBe(kind);
  });
});
