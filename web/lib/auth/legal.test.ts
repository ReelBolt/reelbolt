// @vitest-environment node

import { describe, expect, it } from 'vitest';
import { legalChangeFor, legalVersionsFromRefusal, parseLegalVersions } from './legal';

// F-7: these two parse the versions OUT of what the server says, and nothing here may ever acquire
// a fallback constant — a guessed version is the bug this change exists to remove.

describe('parseLegalVersions', () => {
  it('reads the two versions the API serves', () => {
    expect(parseLegalVersions({ termsVersion: '2026-11', privacyVersion: '2026-11' })).toEqual({
      termsVersion: '2026-11',
      privacyVersion: '2026-11',
    });
  });

  it('trims padding rather than comparing padded strings', () => {
    expect(parseLegalVersions({ termsVersion: ' 2026-11 ', privacyVersion: '2026-11\n' })).toEqual({
      termsVersion: '2026-11',
      privacyVersion: '2026-11',
    });
  });

  it('refuses a half-answer: one document is not enough to compare with', () => {
    expect(parseLegalVersions({ termsVersion: '2026-11' })).toBeNull();
    expect(parseLegalVersions({ privacyVersion: '2026-11' })).toBeNull();
    expect(parseLegalVersions({ termsVersion: '2026-11', privacyVersion: '' })).toBeNull();
    expect(parseLegalVersions({ termsVersion: '2026-11', privacyVersion: '   ' })).toBeNull();
  });

  it('refuses anything that is not a string or not an object', () => {
    expect(parseLegalVersions(null)).toBeNull();
    expect(parseLegalVersions(undefined)).toBeNull();
    expect(parseLegalVersions('2026-11')).toBeNull();
    expect(parseLegalVersions({ termsVersion: 202611, privacyVersion: '2026-11' })).toBeNull();
    expect(parseLegalVersions({})).toBeNull();
  });
});

describe('legalVersionsFromRefusal', () => {
  it('reads the current versions out of the 409 the API answers a stale acceptance with', () => {
    expect(
      legalVersionsFromRefusal({
        error: 'the terms have been updated since this page was loaded; reload and accept the current version',
        currentTermsVersion: '2026-11',
        currentPrivacyVersion: '2026-12',
      }),
    ).toEqual({ termsVersion: '2026-11', privacyVersion: '2026-12' });
  });

  it('is null for any other error body, so a plain failure keeps its own message', () => {
    expect(legalVersionsFromRefusal(undefined)).toBeNull();
    expect(legalVersionsFromRefusal({ error: 'service unavailable' })).toBeNull();
    expect(legalVersionsFromRefusal({ currentTermsVersion: '2026-11' })).toBeNull();
  });
});

describe('legalChangeFor', () => {
  const served = { termsVersion: '2026-10', privacyVersion: '2026-11' };

  it('is current when both accepted versions match what is served', () => {
    expect(legalChangeFor({ termsVersion: '2026-10', privacyVersion: '2026-11' }, served)).toBeNull();
  });

  it('names a Privacy-only move as an acknowledgement, not a terms change', () => {
    expect(legalChangeFor({ termsVersion: '2026-10', privacyVersion: '2026-10' }, served)).toBe('privacy');
  });

  it('names a Terms move as a terms change whatever Privacy did', () => {
    expect(legalChangeFor({ termsVersion: '2026-09', privacyVersion: '2026-10' }, served)).toBe('terms');
    expect(legalChangeFor({ termsVersion: '2026-09', privacyVersion: '2026-11' }, served)).toBe('terms');
  });

  it('never sends a session without privacyVersion back for Privacy alone', () => {
    expect(legalChangeFor({ termsVersion: '2026-10' }, served)).toBeNull();
  });

  it('leaves alone a session without termsVersion, and a deployment that did not answer', () => {
    expect(legalChangeFor({ privacyVersion: '2026-01' }, served)).toBeNull();
    expect(legalChangeFor({ termsVersion: '2020-01', privacyVersion: '2020-01' }, null)).toBeNull();
  });
});
