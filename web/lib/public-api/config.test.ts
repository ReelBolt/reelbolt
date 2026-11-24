import { describe, expect, it } from 'vitest';
import { publicApiEnabled } from './config';

describe('publicApiEnabled', () => {
  it('is off by default and for anything but the literal "true"', () => {
    expect(publicApiEnabled({})).toBe(false);
    expect(publicApiEnabled({ PUBLIC_API_ENABLED: '' })).toBe(false);
    expect(publicApiEnabled({ PUBLIC_API_ENABLED: 'false' })).toBe(false);
    expect(publicApiEnabled({ PUBLIC_API_ENABLED: '1' })).toBe(false);
    expect(publicApiEnabled({ PUBLIC_API_ENABLED: 'TRUE' })).toBe(false);
  });

  it('is on only for the literal "true"', () => {
    expect(publicApiEnabled({ PUBLIC_API_ENABLED: 'true' })).toBe(true);
  });
});
