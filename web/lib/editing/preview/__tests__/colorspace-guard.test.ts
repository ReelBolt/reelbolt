/** @vitest-environment node */
/**
 * WebKit's decoder reports a stream whose colour primaries/transfer are not signalled as
 * `"unspecified"`, a value outside the WebCodecs enums. mediabunny copies every decoded frame's
 * colour space into a `VideoSampleColorSpace`, which throws on such a value, so every frame of that
 * source failed and the viewer showed black (seeded vertical clip on iPhone WebKit, T-074).
 */

import { describe, expect, it } from 'vitest';
import { guardVideoColorSpace, KNOWN_COLOR_SPACE_VALUES } from '../colorspace-guard';

/** A stand-in for the browser's `VideoColorSpace`: read-only prototype getters over private state. */
function fakeColorSpaceClass() {
  class FakeColorSpace {
    #init: Record<string, unknown>;
    constructor(init: Record<string, unknown>) {
      this.#init = init;
    }
    get primaries() {
      return this.#init.primaries ?? null;
    }
    get transfer() {
      return this.#init.transfer ?? null;
    }
    get matrix() {
      return this.#init.matrix ?? null;
    }
    get fullRange() {
      return this.#init.fullRange ?? null;
    }
  }
  return FakeColorSpace;
}

describe('guardVideoColorSpace', () => {
  it('reads a value outside the WebCodecs enums as unknown (null), so mediabunny accepts the frame', () => {
    const Cs = fakeColorSpaceClass();
    expect(guardVideoColorSpace(Cs)).toBe(true);
    const webkit = new Cs({ primaries: 'unspecified', transfer: 'unspecified', matrix: 'smpte170m', fullRange: false });
    expect(webkit.primaries).toBeNull();
    expect(webkit.transfer).toBeNull();
    expect(webkit.matrix).toBe('smpte170m');
    expect(webkit.fullRange).toBe(false);
  });

  it('keeps every value mediabunny knows, and null', () => {
    const Cs = fakeColorSpaceClass();
    guardVideoColorSpace(Cs);
    for (const primaries of KNOWN_COLOR_SPACE_VALUES.primaries) expect(new Cs({ primaries }).primaries).toBe(primaries);
    for (const transfer of KNOWN_COLOR_SPACE_VALUES.transfer) expect(new Cs({ transfer }).transfer).toBe(transfer);
    for (const matrix of KNOWN_COLOR_SPACE_VALUES.matrix) expect(new Cs({ matrix }).matrix).toBe(matrix);
    expect(new Cs({}).primaries).toBeNull();
  });

  it('installs once, and does nothing without the class or its getters', () => {
    const Cs = fakeColorSpaceClass();
    expect(guardVideoColorSpace(Cs)).toBe(true);
    expect(guardVideoColorSpace(Cs)).toBe(false);
    expect(new Cs({ primaries: 'film' }).primaries).toBeNull();
    expect(guardVideoColorSpace(undefined)).toBe(false);
    expect(guardVideoColorSpace(class {})).toBe(false);
  });

  it('matches the values mediabunny validates against (its own maps)', async () => {
    // A mediabunny upgrade that widens or narrows its enums must update this guard too.
    const fs = await import('node:fs');
    const path = await import('node:path');
    const bundle = fs.readFileSync(path.join(process.cwd(), 'node_modules/mediabunny/dist/bundles/mediabunny.mjs'), 'utf8');
    const keysOf = (name: string) => {
      const body = new RegExp(`var ${name} = \\{([^}]*)\\}`).exec(bundle)?.[1] ?? '';
      return [...body.matchAll(/^\s*"?([a-z0-9-]+)"?\s*:/gm)].map((m) => m[1]).sort();
    };
    expect(keysOf('COLOR_PRIMARIES_MAP')).toEqual([...KNOWN_COLOR_SPACE_VALUES.primaries].sort());
    expect(keysOf('TRANSFER_CHARACTERISTICS_MAP')).toEqual([...KNOWN_COLOR_SPACE_VALUES.transfer].sort());
    expect(keysOf('MATRIX_COEFFICIENTS_MAP')).toEqual([...KNOWN_COLOR_SPACE_VALUES.matrix].sort());
  });
});
