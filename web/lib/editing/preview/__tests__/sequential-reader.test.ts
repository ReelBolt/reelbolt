import { describe, expect, it } from 'vitest';
import { SequentialReader, type TimedSample } from '../sequential-reader';

interface FakeSample extends TimedSample {
  closed: boolean;
}

/** A 30 fps clip of `count` frames; records every iterator it hands out and how many samples each yielded. */
function fakeClip(count: number, fps = 30) {
  const opens: number[] = [];
  const decoded = { total: 0 };
  const all: FakeSample[] = [];
  async function* samples(start: number): AsyncGenerator<FakeSample, void, unknown> {
    opens.push(start);
    // Like a demuxer: start at the frame covering `start`.
    for (let i = Math.max(0, Math.floor(start * fps + 1e-9)); i < count; i++) {
      decoded.total += 1;
      const sample: FakeSample = { timestamp: i / fps, closed: false, close() { this.closed = true; } };
      all.push(sample);
      yield sample;
    }
  }
  return { open: samples, opens, decoded, all };
}

const at = (frame: number, fps = 30) => (frame + 0.5) / fps;
const indexOf = (s: TimedSample) => Math.round(s.timestamp * 30);

describe('SequentialReader', () => {
  it('decodes each packet once while reading forward', async () => {
    const clip = fakeClip(120);
    const reader = new SequentialReader(clip.open);
    const got: number[] = [];
    // The pool asks for a window at once.
    await Promise.all(Array.from({ length: 60 }, (_, i) => reader.read(at(i), indexOf).then((v) => got.push(v!))));
    expect(got).toEqual(Array.from({ length: 60 }, (_, i) => i));
    expect(reader.restarts).toBe(1);
    // 60 frames read; at most one frame of lookahead was pulled beyond them.
    expect(clip.decoded.total).toBeLessThanOrEqual(61);
  });

  it('returns the same sample for two requests inside one frame', async () => {
    const reader = new SequentialReader(fakeClip(30).open);
    expect(await reader.read(0.40, indexOf)).toBe(12);
    expect(await reader.read(0.41, indexOf)).toBe(12);
  });

  it('restarts when a request lands behind the iterator', async () => {
    const clip = fakeClip(120);
    const reader = new SequentialReader(clip.open);
    expect(await reader.read(at(50), indexOf)).toBe(50);
    expect(await reader.read(at(10), indexOf)).toBe(10);
    expect(reader.restarts).toBe(2);
  });

  it('restarts instead of decoding through a long forward jump, and decodes through a short one', async () => {
    const clip = fakeClip(300);
    const reader = new SequentialReader(clip.open);
    await reader.read(at(0), indexOf);
    expect(await reader.read(at(10), indexOf)).toBe(10);
    expect(reader.restarts).toBe(1);
    expect(await reader.read(at(200), indexOf)).toBe(200);
    expect(reader.restarts).toBe(2);
  });

  it('closes every sample it no longer holds, and everything on dispose', async () => {
    const clip = fakeClip(40);
    const reader = new SequentialReader(clip.open);
    for (let i = 0; i < 20; i++) await reader.read(at(i), indexOf);
    reader.dispose();
    await reader.read(at(21), indexOf);
    expect(clip.all.every((s) => s.closed)).toBe(true);
  });

  it('holds the last frame past the end and the first frame before the start', async () => {
    const reader = new SequentialReader(fakeClip(10).open);
    expect(await reader.read(at(25), indexOf)).toBe(9);
    const early = new SequentialReader(async function* () {
      yield { timestamp: 0.1, close() {} };
    });
    expect(await early.read(0.0, (s) => s.timestamp)).toBe(0.1);
  });

  it('keeps serving after a map throws', async () => {
    const reader = new SequentialReader(fakeClip(10).open);
    await expect(reader.read(at(1), () => { throw new Error('boom'); })).rejects.toThrow('boom');
    expect(await reader.read(at(2), indexOf)).toBe(2);
  });
});
