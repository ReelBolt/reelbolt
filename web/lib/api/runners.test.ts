import { describe, it, expect, afterEach, vi } from 'vitest';
import { ApiError } from './client';
import {
  describePairingError,
  describeRevokeError,
  formatUserCode,
  normalizeUserCode,
  runnerErrorCode,
} from './runners';

afterEach(() => vi.unstubAllGlobals());

// The API's own alphabet (api/handlers/runners/runners.go): 0/O and 1/I are absent because users
// transcribe them wrongly, so a code containing them is a typo and never a lookup.
describe('normalizeUserCode', () => {
  it('accepts what a user types: lower case, with a dash or spaces or neither', () => {
    expect(normalizeUserCode('7hqd-4m2r')).toBe('7HQD4M2R');
    expect(normalizeUserCode('7HQD 4M2R')).toBe('7HQD4M2R');
    expect(normalizeUserCode('  7hqd4m2r ')).toBe('7HQD4M2R');
  });

  it('refuses a code that is the wrong length', () => {
    expect(normalizeUserCode('7HQD-4M2')).toBeNull();
    expect(normalizeUserCode('7HQD-4M2RR')).toBeNull();
    expect(normalizeUserCode('')).toBeNull();
  });

  it('refuses the ambiguous characters the alphabet leaves out', () => {
    expect(normalizeUserCode('0HQD-4M2R')).toBeNull();
    expect(normalizeUserCode('OHQD-4M2R')).toBeNull();
    expect(normalizeUserCode('1HQD-4M2R')).toBeNull();
    expect(normalizeUserCode('IHQD-4M2R')).toBeNull();
  });
});

describe('formatUserCode', () => {
  it('groups a normalized code the way the device shows it', () => {
    expect(formatUserCode('7HQD4M2R')).toBe('7HQD-4M2R');
  });

  it('leaves anything it did not normalize alone', () => {
    expect(formatUserCode('7HQD-4M2R')).toBe('7HQD-4M2R');
  });
});

function apiError(status: number, code: string): ApiError {
  return new ApiError(code, status, [], { error: code });
}

describe('runnerErrorCode', () => {
  it('reads the single-key error body the Go handlers write', () => {
    expect(runnerErrorCode(apiError(409, 'already_paired'))).toBe('already_paired');
  });

  it('is null for anything else', () => {
    expect(runnerErrorCode(new Error('boom'))).toBeNull();
    expect(runnerErrorCode(new ApiError('boom', 500))).toBeNull();
    expect(runnerErrorCode(new ApiError('boom', 500, [], { error: 7 }))).toBeNull();
  });
});

// The rule these assertions protect (D5): an unknown code, a malformed one, an expired one, one that
// already settled, a workspace the caller is not a member of, a workspace that does not exist and the
// platform's own workspace are all ONE answer on the wire, and the UI must not invent a distinction
// the server refuses to make. In particular nothing here says 'you do not have permission' for a 404.
describe('describePairingError', () => {
  it('gives the same one-answer sentence for every 404 cause', () => {
    const pairing = describePairingError(apiError(404, 'pairing request not found'));
    const org = describePairingError(apiError(404, 'organization not found'));
    const bare = describePairingError(new ApiError('Not Found', 404));
    expect(pairing).toBe(org);
    expect(bare).toBe(pairing);
    expect(pairing).toMatch(/not available/);
    expect(pairing).not.toMatch(/permission/i);
  });

  it('names the role refusal for a 403, which is the answer for a member INSIDE the workspace', () => {
    expect(describePairingError(apiError(403, 'insufficient_role'))).toMatch(/Owner or Admin/);
  });

  it('explains a settled request and an already-paired machine', () => {
    expect(describePairingError(apiError(409, 'pairing_request_settled'))).toMatch(/already been approved or denied/);
    expect(describePairingError(apiError(409, 'already_paired'))).toMatch(/already paired/);
  });

  it('falls back to the error message, then to a generic sentence', () => {
    expect(describePairingError(new ApiError('Server exploded', 500))).toBe('Server exploded');
    expect(describePairingError(undefined)).toBe('Something went wrong. Try again.');
  });
});

describe('describeRevokeError', () => {
  it('treats a 404 as a device that is no longer there, whatever the body says', () => {
    const message = describeRevokeError(apiError(404, 'device not found'));
    expect(message).toMatch(/no longer in this workspace/);
    expect(describeRevokeError(new ApiError('Not Found', 404))).toBe(message);
  });

  // Revoke is allowed for an Owner/Admin OR the user who paired it; the server cannot say which of
  // those the caller is, so the sentence names both rather than guessing.
  it('names both routes to authority for a 403', () => {
    const message = describeRevokeError(apiError(403, 'insufficient_role'));
    expect(message).toMatch(/Owner or Admin/);
    expect(message).toMatch(/paired the device/);
  });
});
