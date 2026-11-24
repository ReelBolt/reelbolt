import { describe, expect, it } from 'vitest';
import { friendlyError, friendlyMessage } from './error-messages';

describe('friendlyError', () => {
  it.each([
    ['invalid credentials', 'invalid-credentials'],
    ['Invalid credentials.', 'invalid-credentials'],
    ['current password is incorrect', 'wrong-current-password'],
    ['invalid or expired token', 'link-expired'],
    ['that person is already a member', 'already-member'],
    ['Unauthorized', 'session'],
    ['missing or invalid authorization header', 'session'],
  ])('maps %j to %s', (raw, kind) => {
    const out = friendlyError(new Error(raw));
    expect(out.kind).toBe(kind);
    expect(out.message).not.toBe(raw);
    expect(out.message).toMatch(/^[A-Z].*\.$/);
  });
  it('passes unknown messages through and uses the fallback for empty ones', () => {
    expect(friendlyMessage(new Error('Disk is full'))).toBe('Disk is full');
    expect(friendlyMessage(new Error(''), 'Nope')).toBe('Nope');
  });
});
