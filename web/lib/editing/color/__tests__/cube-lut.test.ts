import { describe, expect, it } from 'vitest';
import { CubeLutError, parseCubeLut, sampleCubeLut, toCubeText, type CubeLutErrorCode } from '../cube-lut';
import { disposeViewingLut, floatToHalf, loadViewingLut, toRgba16fTexture3dData, viewingLutCacheSize } from '../cube-lut-loader';

function identityCube(n: number, header = ''): string {
  const rows: string[] = [];
  for (let b = 0; b < n; b += 1) for (let g = 0; g < n; g += 1) for (let r = 0; r < n; r += 1) rows.push(`${r / (n - 1)} ${g / (n - 1)} ${b / (n - 1)}`);
  return `${header}LUT_3D_SIZE ${n}\n${rows.join('\n')}\n`;
}

function code(fn: () => unknown): CubeLutErrorCode | 'none' {
  try {
    fn();
  } catch (e) {
    return e instanceof CubeLutError ? e.code : 'none';
  }
  return 'none';
}

describe('parseCubeLut', () => {
  it('parses 2^3 and 33^3 tables, R fastest', () => {
    const a = parseCubeLut(identityCube(2));
    expect(a.size).toBe(2);
    expect(Array.from(a.data.slice(3, 6))).toEqual([1, 0, 0]);
    const b = parseCubeLut(identityCube(33));
    expect(b.data.length).toBe(33 ** 3 * 3);
  });
  it('handles CRLF, comments, TITLE, trailing blanks', () => {
    const t = `# hi\r\nTITLE "My Look"\r\n${identityCube(2).replace(/\n/g, '\r\n')}\r\n\r\n\r\n`;
    const lut = parseCubeLut(t);
    expect(lut.title).toBe('My Look');
    expect(lut.data.length).toBe(24);
  });
  it('normalises a non-0..1 domain and clamps', () => {
    const t = 'DOMAIN_MIN -1 -1 -1\nDOMAIN_MAX 3 3 3\nLUT_3D_SIZE 2\n' + Array.from({ length: 8 }, () => '-5 1 9').join('\n');
    const lut = parseCubeLut(t);
    expect(lut.domainMin[0]).toBe(-1);
    expect(Array.from(lut.data.slice(0, 3))).toEqual([0, 0.5, 1]);
  });
  it('refuses with stable codes', () => {
    expect(code(() => parseCubeLut('LUT_1D_SIZE 4\n0 0 0'))).toBe('wrong_row_count');
    expect(code(() => parseCubeLut('LUT_3D_SIZE 2\nFOO 1\n'))).toBe('unsupported_keyword');
    expect(code(() => parseCubeLut('LUT_3D_SIZE 1\n0 0 0'))).toBe('size_out_of_range');
    expect(code(() => parseCubeLut('LUT_3D_SIZE 66\n'))).toBe('size_out_of_range');
    expect(code(() => parseCubeLut('LUT_3D_SIZE x\n'))).toBe('size_out_of_range');
    expect(code(() => parseCubeLut('0 0 0\n'))).toBe('size_missing');
    expect(code(() => parseCubeLut('LUT_3D_SIZE 2\n0 0 0\n'))).toBe('wrong_row_count');
    expect(code(() => parseCubeLut(identityCube(2).replace('0 0 0', '0 0 nan')))).toBe('not_finite');
    expect(code(() => parseCubeLut(identityCube(2).replace('0 0 0', '0 0 1e999')))).toBe('not_finite');
    expect(code(() => parseCubeLut(identityCube(2).replace('0 0 0', '0 0')))).toBe('malformed');
    expect(code(() => parseCubeLut(identityCube(2).replace('0 0 0', '0 0 abc')))).toBe('malformed');
    expect(code(() => parseCubeLut(identityCube(2, 'DOMAIN_MIN 1 1 1\nDOMAIN_MAX 0 0 0\n')))).toBe('bad_domain');
  });
  it('refuses >16 MiB before parsing', () => {
    expect(code(() => parseCubeLut(' '.repeat(16 * 1024 * 1024 + 1)))).toBe('too_large');
  });
  it('round-trips through the canonical writer', () => {
    const lut = parseCubeLut(identityCube(3, 'TITLE "x"\n'));
    const text = toCubeText(lut);
    expect(text.startsWith('TITLE "x"\nLUT_3D_SIZE 3\nDOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\n0 0 0\n0.5 0 0\n')).toBe(true);
    const again = parseCubeLut(text);
    expect(Array.from(again.data)).toEqual(Array.from(lut.data));
  });
});

describe('half float + loader', () => {
  it('converts known values', () => {
    expect(floatToHalf(0)).toBe(0);
    expect(floatToHalf(1)).toBe(0x3c00);
    expect(floatToHalf(0.5)).toBe(0x3800);
    expect(floatToHalf(-2)).toBe(0xc000);
    expect(floatToHalf(2 ** -24)).toBe(1);
    expect(floatToHalf(2 ** -14)).toBe(0x0400);
    expect(floatToHalf(1e9)).toBe(0x7bff);
    expect(floatToHalf(65504)).toBe(0x7bff);
    expect(floatToHalf(1e-10)).toBe(0);
    expect(floatToHalf(Number.NaN)).toBe(0);
  });
  it('packs RGBA with alpha 1', () => {
    const out = toRgba16fTexture3dData(parseCubeLut(identityCube(2)));
    expect(out.length).toBe(32);
    expect(Array.from(out.slice(4, 8))).toEqual([0x3c00, 0, 0, 0x3c00]);
  });
  it('caches in an LRU of 4 and disposes', async () => {
    disposeViewingLut();
    const text = identityCube(2);
    let fetches = 0;
    const f = async (): Promise<string> => {
      fetches += 1;
      return text;
    };
    for (let i = 0; i < 5; i += 1) await loadViewingLut(f, { key: `k${i}` });
    expect(viewingLutCacheSize()).toBe(4);
    await loadViewingLut(f, { key: 'k4' });
    expect(fetches).toBe(5);
    await loadViewingLut(f, { key: 'k0' });
    expect(fetches).toBe(6);
    disposeViewingLut('k4');
    expect(viewingLutCacheSize()).toBe(3);
    disposeViewingLut();
    expect(viewingLutCacheSize()).toBe(0);
  });
  it('refuses a huge fetched text', async () => {
    await expect(loadViewingLut(async () => ' '.repeat(16 * 1024 * 1024 + 1))).rejects.toMatchObject({ code: 'too_large' });
  });
});

describe('1D and shaper + 3D', () => {
  const oneD = 'LUT_1D_SIZE 3\n0 0 0\n0.8 0.5 0.2\n1 1 1\n';
  it('parses a 1D file and samples it linearly', () => {
    const lut = parseCubeLut(oneD);
    expect(lut.kind).toBe('1d');
    expect(lut.oneD?.size).toBe(3);
    const out = sampleCubeLut(lut, [0.25, 0.5, 0.75]);
    expect(out[0]).toBeCloseTo(0.4, 5);
    expect(out[1]).toBeCloseTo(0.5, 5);
    expect(out[2]).toBeCloseTo(0.2 + 0.8 * 0.5, 5);
    expect(sampleCubeLut(lut, [0.25, 0.5, 0.75], 0)[0]).toBeCloseTo(0.25, 5);
  });
  it('folds a 1D input range into the canonical table', () => {
    const lut = parseCubeLut('LUT_1D_SIZE 2\nLUT_1D_INPUT_RANGE 0 2\n0 0 0\n1 1 1\n');
    expect(sampleCubeLut(lut, [0.5, 1, 0])[0]).toBeCloseTo(0.25, 5);
    expect(lut.oneD?.data[3]).toBeCloseTo(0.5, 5);
  });
  it('reports row counts with numbers', () => {
    try {
      parseCubeLut('LUT_3D_SIZE 33\n0 0 0\n');
      expect.unreachable();
    } catch (err) {
      expect((err as CubeLutError).details).toEqual({ expected: 35937, found: 1 });
    }
  });
  it('shaper + 3D chains, and bakes below full intensity', () => {
    const cube = `LUT_1D_SIZE 2\nLUT_3D_SIZE 2\n0 0 0\n1 1 1\n${identityCube(2).split('\n').slice(1).join('\n')}`;
    const lut = parseCubeLut(cube);
    expect(lut.kind).toBe('shaper3d');
    const out = sampleCubeLut(lut, [0.2, 0.6, 0.9]);
    expect(out[1]).toBeCloseTo(0.6, 4);
    expect(sampleCubeLut(lut, [0.2, 0.6, 0.9], 0.5)[2]).toBeCloseTo(0.9, 4);
  });
  it('refuses DOMAIN_* in a combined file', () => {
    const cube = `LUT_1D_SIZE 2\nDOMAIN_MIN 0 0 0\n0 0 0\n1 1 1\n${identityCube(2)}`;
    expect(code(() => parseCubeLut(cube))).toBe('unsupported_keyword');
  });
});

describe('LUT reasons that reach the UI', () => {
  it('reads the engine render-failure message format', async () => {
    const { lutReasonFromFailureMessage } = await import('../lut-reason');
    const rows = 'LUT_INVALID: The LUT file cannot be used (wrong_row_count, expected 35937, found 1000): a 33³ LUT needs 35937 entries, found 1000.';
    expect(lutReasonFromFailureMessage(rows)).toEqual({ code: 'wrong_row_count', expected: 35937, found: 1000 });
    const kw = 'Step 1 (TimelineRender) failed. Last error: LUT_UNSUPPORTED: The LUT file cannot be used (unsupported_keyword, keyword LUT_IN_VIDEO_RANGE): the keyword LUT_IN_VIDEO_RANGE is not supported.';
    expect(lutReasonFromFailureMessage(kw)).toEqual({ code: 'unsupported_keyword', keyword: 'LUT_IN_VIDEO_RANGE' });
    expect(lutReasonFromFailureMessage('RENDER_FAILED: ffmpeg exited 1')).toBeNull();
  });

  it('bounds an unknown keyword before it is echoed', () => {
    const long = 'X'.repeat(500) + '<script>';
    try {
      parseCubeLut(`${long} 1\nLUT_3D_SIZE 2\n`);
      throw new Error('should refuse');
    } catch (err) {
      expect((err as CubeLutError).code).toBe('unsupported_keyword');
      expect((err as CubeLutError).details?.keyword).toBe('X'.repeat(40));
    }
  });
});

describe('TITLE is a whole keyword', () => {
  it('refuses TITLEfoo as an unknown keyword, like the engine', () => {
    expect(() => parseCubeLut('TITLEfoo\nLUT_3D_SIZE 2\n')).toThrow(CubeLutError);
    try {
      parseCubeLut('TITLEfoo\nLUT_3D_SIZE 2\n');
    } catch (err) {
      expect((err as CubeLutError).code).toBe('unsupported_keyword');
    }
  });
});
