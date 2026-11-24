import { describe, expect, it } from 'vitest';
import { describeWait, signupRateLimitMessage, SUPPORT_EMAIL } from './signup-copy';

describe('signup-copy', () => {
  it('describes waits', () => {
    expect(describeWait(3600)).toBe('about 1 hour');
    expect(describeWait(86400)).toBe('a day or more');
    expect(describeWait(9_999_999)).toBe('a day or more');
    expect(describeWait(1200)).toBe('about 20 minutes');
    expect(describeWait(30)).toBe('about a minute');
    expect(describeWait(undefined)).toBe('a little while');
  });
  it('names the retry time and support contact', () => {
    const m = signupRateLimitMessage(3600);
    expect(m).toContain('about 1 hour');
    expect(m).toContain(SUPPORT_EMAIL);
  });
});
