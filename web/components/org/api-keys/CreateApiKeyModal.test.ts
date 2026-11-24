import { describe, expect, it } from 'vitest';
import { clampExpiry, expiryInstantFromDate, maxExpiryInstant } from './CreateApiKeyModal';

describe('custom expiry clamp', () => {
  it('never sends an instant after the server maximum when the last offered day is chosen', () => {
    const now = Date.now();
    const max = maxExpiryInstant(now);
    const pad = (n: number) => String(n).padStart(2, '0');
    const lastDay = `${max.getFullYear()}-${pad(max.getMonth() + 1)}-${pad(max.getDate())}`;
    const instant = expiryInstantFromDate(lastDay);
    expect(instant).not.toBeNull();
    expect(clampExpiry(instant as Date, now).getTime()).toBeLessThanOrEqual(max.getTime());
  });

  it('leaves an earlier instant untouched', () => {
    const now = Date.now();
    const early = new Date(now + 1000);
    expect(clampExpiry(early, now)).toBe(early);
  });
});
