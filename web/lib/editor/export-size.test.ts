import { describe, expect, it } from 'vitest';
import { estimateSizeText, fitQuality, UNDER_2_MB } from './export-size';

// A made-up encoder: size grows with quality.
const encoder = (full: number) => async (q: number) => Math.round(full * q * q);

describe('fitQuality', () => {
  it('keeps full quality when it already fits', async () => {
    expect(await fitQuality(encoder(1_000_000), UNDER_2_MB)).toMatchObject({ quality: 1, fits: true });
  });
  it('finds the highest quality that fits', async () => {
    const fit = await fitQuality(encoder(4_000_000), UNDER_2_MB);
    expect(fit.fits).toBe(true);
    expect(fit.bytes).toBeLessThanOrEqual(UNDER_2_MB);
    expect(fit.quality).toBeGreaterThan(0.6);
    expect(fit.quality).toBeLessThan(0.75);
  });
  it('says so when even the lowest quality is too big', async () => {
    const fit = await fitQuality(encoder(50_000_000), UNDER_2_MB);
    expect(fit).toMatchObject({ quality: 0.4, fits: false });
  });
});

describe('estimateSizeText', () => {
  it('reads plainly', () => {
    expect(estimateSizeText(1_450_000)).toBe('about 1.4 MB');
    expect(estimateSizeText(320_000)).toBe('about 320 KB');
  });
});
