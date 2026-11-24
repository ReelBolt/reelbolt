import { describe, expect, it } from 'vitest';
import { defaultPresetFor } from '../deliver-model';

describe('defaultPresetFor', () => {
  it('matches the timeline shape', () => {
    expect(defaultPresetFor({ width: 1080, height: 1920 }, false)).toBe('Vertical9x16');
    expect(defaultPresetFor({ width: 1920, height: 1080 }, false)).toBe('Hd1080p16x9');
    expect(defaultPresetFor({ width: 1080, height: 1080 }, false)).toBe('Square1x1');
    expect(defaultPresetFor({ width: 1080, height: 1350 }, false)).toBe('Portrait4x5');
  });
  it('takes the nearest shape for odd sizes (3:4 is Portrait 4:5)', () => {
    expect(defaultPresetFor({ width: 1500, height: 2000 }, false)).toBe('Portrait4x5');
    expect(defaultPresetFor({ width: 1366, height: 768 }, false)).toBe('Hd1080p16x9');
    expect(defaultPresetFor({ width: 0, height: 0 }, false)).toBe('Hd1080p16x9');
  });
  it('a phone always starts vertical, as before', () => {
    expect(defaultPresetFor({ width: 1920, height: 1080 }, true)).toBe('Vertical9x16');
  });
  it('falls back by device when the size is unknown', () => {
    expect(defaultPresetFor(null, true)).toBe('Vertical9x16');
    expect(defaultPresetFor(null, false)).toBe('Hd1080p16x9');
  });
});
