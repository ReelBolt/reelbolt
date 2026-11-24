/**
 * The local "once per user" key (alpha-launch T-018). It must be stable for one address (so a
 * reload does not re-fire the funnel) and different for two.
 */
import { describe, expect, it } from 'vitest';
import { userKeyFromEmail } from './identity';

describe('userKeyFromEmail', () => {
  it('is empty without an address, which makes the emit a no-op', () => {
    expect(userKeyFromEmail(undefined)).toBe('');
    expect(userKeyFromEmail(null)).toBe('');
    expect(userKeyFromEmail('   ')).toBe('');
  });

  it('is stable across the spellings of one address', () => {
    expect(userKeyFromEmail('Ada@Example.com')).toBe(userKeyFromEmail(' ada@example.com '));
  });

  it('differs between two addresses', () => {
    expect(userKeyFromEmail('ada@example.com')).not.toBe(userKeyFromEmail('grace@example.com'));
  });

  it('does not carry the address itself', () => {
    expect(userKeyFromEmail('ada@example.com')).not.toContain('ada');
  });
});
