import { describe, expect, it } from 'vitest';
import { digitStreamPreview, parseTimecodeInput } from '../time';

const r30 = { num: 30, den: 1 };
const r24 = { num: 24, den: 1 };

describe('R01.5 parseTimecodeInput', () => {
  it('reads a digit stream from the right: 1215 is 00:00:12:15', () => {
    expect(parseTimecodeInput('1215', r30)).toEqual({ ok: true, frames: 12 * 30 + 15, timecode: '00:00:12:15' });
  });
  it('pads short streams and accepts all eight digits', () => {
    expect(parseTimecodeInput('100', r30)).toMatchObject({ ok: true, timecode: '00:00:01:00' });
    expect(parseTimecodeInput('01020304', r30)).toMatchObject({ ok: true, timecode: '01:02:03:04' });
  });
  it('reads colon form right-aligned', () => {
    expect(parseTimecodeInput('12:15', r30)).toMatchObject({ ok: true, timecode: '00:00:12:15' });
    expect(parseTimecodeInput('1:02:03:04', r30)).toMatchObject({ ok: true, timecode: '01:02:03:04' });
  });
  it('refuses frames at or above the frame rate, with a reason naming the limit', () => {
    expect(parseTimecodeInput('1224', r24)).toEqual({ ok: false, reason: 'Frames go up to 23 at this frame rate.' });
    expect(parseTimecodeInput('1224', r30)).toMatchObject({ ok: true });
  });
  it('refuses minutes and seconds past 59', () => {
    expect(parseTimecodeInput('6000', r30)).toEqual({ ok: false, reason: 'Seconds go up to 59.' });
    expect(parseTimecodeInput('600000', r30)).toEqual({ ok: false, reason: 'Minutes go up to 59.' });
  });
  it('refuses garbage, drop-frame, empty and too many digits without throwing', () => {
    for (const bad of ['', '  ', 'abc', '12x4', '-5', '1.5', '00;00;01;00', '123456789', '1:2:3:4:5', '::', '1:']) {
      const result = parseTimecodeInput(bad, r30);
      expect(result.ok).toBe(false);
      if (!result.ok) expect(result.reason.length).toBeGreaterThan(0);
    }
  });
});

describe('R01.5 digitStreamPreview', () => {
  it('right-aligns typed digits into a timecode', () => {
    expect(digitStreamPreview('')).toBe('00:00:00:00');
    expect(digitStreamPreview('1')).toBe('00:00:00:01');
    expect(digitStreamPreview('1215')).toBe('00:00:12:15');
    expect(digitStreamPreview('1a2')).toBe('00:00:00:12');
    expect(digitStreamPreview('123456789')).toBe('23:45:67:89');
  });
});
