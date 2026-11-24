// Fixture ../__fixtures__/rotate-90.mp4 (4 KB): `ffmpeg -f lavfi -i testsrc=size=160x96:rate=10 -t 1 -pix_fmt yuv420p
// -c:v libx264 plain.mp4`, then `ffmpeg -display_rotation 90 -i plain.mp4 -c copy rotate-90.mp4` (Display Matrix side data).
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Input, ALL_FORMATS, BufferSource } from 'mediabunny';
import { planOrientation } from '../mediabunny-reader';

describe('rotate-90 fixture', () => {
  it('is parsed as a portrait track with a rotation', async () => {
    const data = readFileSync(join(__dirname, '../__fixtures__/rotate-90.mp4'));
    const input = new Input({ source: new BufferSource(new Uint8Array(data)), formats: ALL_FORMATS });
    const track = await input.getPrimaryVideoTrack();
    expect(track).not.toBeNull();
    expect([90, 270]).toContain(track!.rotation);
    expect(track!.displayWidth).toBe(96);
    expect(track!.displayHeight).toBe(160);
    input.dispose();
  });
});

describe('planOrientation', () => {
  it('returns null for zero rotation, no flip, no extra', () => {
    expect(planOrientation(0, false, 0, 3840, 2160, 1280)).toBeNull();
  });
  it('keeps the display size (already rotated by mediabunny) for container rotations', () => {
    expect(planOrientation(180, false, 0, 1920, 1080)).toMatchObject({ width: 1920, height: 1080 });
    expect(planOrientation(90, false, 0, 2160, 3840)).toMatchObject({ width: 2160, height: 3840 });
    expect(planOrientation(270, false, 0, 2160, 3840)).toMatchObject({ width: 2160, height: 3840 });
  });
  it('flip alone needs a redraw', () => {
    expect(planOrientation(0, true, 0, 100, 50)).toMatchObject({ width: 100, height: 50 });
  });
  it('extra rotation swaps the output size', () => {
    expect(planOrientation(0, false, 90, 1920, 1080)).toMatchObject({ width: 1080, height: 1920, drawWidth: 1920, drawHeight: 1080 });
    expect(planOrientation(0, false, 180, 1920, 1080)).toMatchObject({ width: 1920, height: 1080 });
  });
  it('scaleToWidth targets the rotated width', () => {
    expect(planOrientation(90, false, 0, 2160, 3840, 540)).toMatchObject({ width: 540, height: 960 });
    expect(planOrientation(0, false, 90, 3840, 2160, 540)).toMatchObject({ width: 540, height: 960, drawWidth: 960, drawHeight: 540 });
    expect(planOrientation(90, false, 0, 600, 800, 580)).toMatchObject({ width: 600, height: 800 });
  });
});

interface FakeSample {
  timestamp: number;
  rotation: 0 | 90 | 180 | 270;
  flip: boolean;
  displayWidth: number;
  displayHeight: number;
  toVideoFrame: () => unknown;
  draw: ReturnType<typeof vi.fn>;
  close: () => void;
}

function installFakes(sample: FakeSample, withCanvas = true) {
  vi.stubGlobal('VideoDecoder', class {});
  vi.doMock('mediabunny', () => ({
    Input: class {
      dispose() {}
      async getPrimaryVideoTrack() {
        return { canDecode: async () => true };
      }
    },
    UrlSource: class {},
    ALL_FORMATS: [],
    VideoSampleSink: class {
      async *samples() {
        yield sample;
      }
    },
  }));
  const created: Array<{ w: number; h: number }> = [];
  if (withCanvas) {
    vi.stubGlobal(
      'OffscreenCanvas',
      class {
        constructor(
          public w: number,
          public h: number,
        ) {
          created.push({ w, h });
        }
        getContext() {
          return { translate() {}, rotate() {} };
        }
        transferToImageBitmap() {
          return { width: this.w, height: this.h, close() {} };
        }
      },
    );
  } else {
    vi.stubGlobal('OffscreenCanvas', undefined);
  }
  return created;
}

function sample(over: Partial<FakeSample>, frame: unknown): FakeSample {
  return { timestamp: 0, rotation: 0, flip: false, displayWidth: 3840, displayHeight: 2160, toVideoFrame: () => frame, draw: vi.fn(), close() {}, ...over };
}

describe('openMediabunnyReader orientation', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.doUnmock('mediabunny');
    vi.resetModules();
  });

  async function open() {
    vi.resetModules();
    const mod = await import('../mediabunny-reader');
    return mod.openMediabunnyReader('http://x/y.mp4');
  }

  it('zero rotation returns the original VideoFrame untouched', async () => {
    const frame = { displayWidth: 3840, displayHeight: 2160, close() {} };
    const s = sample({}, frame);
    installFakes(s);
    const r = await open();
    expect(await r!.read(0)).toBe(frame);
    expect(s.draw).not.toHaveBeenCalled();
  });

  it('rotation 90 yields an oriented bitmap scaled to the rotated width', async () => {
    const s = sample({ rotation: 90, displayWidth: 2160, displayHeight: 3840 }, {});
    const created = installFakes(s);
    const r = await open();
    const out = await r!.read(0, 540);
    expect(created).toEqual([{ w: 540, h: 960 }]);
    expect(out).toMatchObject({ width: 540, height: 960 });
    expect(s.draw).toHaveBeenCalledTimes(1);
  });

  it('extraRotation rotates the output after the container orientation', async () => {
    const s = sample({ displayWidth: 1920, displayHeight: 1080 }, {});
    const created = installFakes(s);
    const r = await open();
    expect(await r!.read(0, undefined, 90)).toMatchObject({ width: 1080, height: 1920 });
    expect(created).toEqual([{ w: 1080, h: 1920 }]);
  });

  it('falls back to the unrotated frame without OffscreenCanvas', async () => {
    const frame = {};
    const s = sample({ rotation: 90 }, frame);
    installFakes(s, false);
    vi.spyOn(console, 'warn').mockImplementation(() => {});
    const r = await open();
    expect(await r!.read(0)).toBe(frame);
  });
});
