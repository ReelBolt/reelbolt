// @vitest-environment node

import { describe, expect, it } from 'vitest';
import { authPageConfig, singleParam } from './config';
import { MAX_PASSWORD_BYTES, MIN_PASSWORD_LENGTH, validatePassword } from './password';

describe('authPageConfig', () => {
  it('keeps signup off and the challenge unrendered when nothing is configured (D12 default)', () => {
    // No legal versions here any more: they are not configuration, they are a fact the API owns
    // (F-7, legal-server.ts). A build-time copy is what made the recorded acceptance unauditable.
    expect(authPageConfig({})).toEqual({
      signupEnabled: false,
      turnstileSiteKey: '',
      oauthProviders: [],
    });
  });

  it('reads the plain server variables the compose service passes', () => {
    const config = authPageConfig({ SIGNUP_ENABLED: 'true', TURNSTILE_SITE_KEY: '0xSITE' });
    expect(config.signupEnabled).toBe(true);
    expect(config.turnstileSiteKey).toBe('0xSITE');
  });

  it('accepts the NEXT_PUBLIC_ spelling for a baked build or a dev shell', () => {
    const config = authPageConfig({ NEXT_PUBLIC_SIGNUP_ENABLED: 'true', NEXT_PUBLIC_TURNSTILE_SITE_KEY: '0xSITE' });
    expect(config.signupEnabled).toBe(true);
    expect(config.turnstileSiteKey).toBe('0xSITE');
  });

  it('is strict about the flag: anything but the literal "true" keeps signups off', () => {
    for (const value of ['1', 'TRUE', 'yes', 'on', '', 'false']) {
      expect(authPageConfig({ SIGNUP_ENABLED: value }).signupEnabled).toBe(false);
    }
  });

  it('trims a site key padded by a .env file', () => {
    expect(authPageConfig({ TURNSTILE_SITE_KEY: '  0xSITE  ' }).turnstileSiteKey).toBe('0xSITE');
  });

  it('offers no OAuth provider by default, and only the ones flagged on', () => {
    expect(authPageConfig({}).oauthProviders).toEqual([]);
    expect(authPageConfig({ OAUTH_GITHUB_ENABLED: 'true' }).oauthProviders).toEqual(['github']);
    // Display order is fixed (Google first), whatever order the environment sets.
    expect(
      authPageConfig({ OAUTH_GITHUB_ENABLED: 'true', OAUTH_GOOGLE_ENABLED: 'true' }).oauthProviders,
    ).toEqual(['google', 'github']);
  });

  it('is strict about the OAuth flags too, and accepts the NEXT_PUBLIC_ spelling', () => {
    for (const value of ['1', 'TRUE', 'yes', 'on', '', 'false']) {
      expect(authPageConfig({ OAUTH_GOOGLE_ENABLED: value }).oauthProviders).toEqual([]);
    }
    expect(authPageConfig({ NEXT_PUBLIC_OAUTH_GOOGLE_ENABLED: 'true' }).oauthProviders).toEqual([
      'google',
    ]);
  });
});

describe('singleParam', () => {
  it('keeps a single value and treats anything else as absent', () => {
    expect(singleParam('tok')).toBe('tok');
    expect(singleParam(['tok', 'other'])).toBe('');
    expect(singleParam(undefined)).toBe('');
  });
});

describe('validatePassword', () => {
  it('mirrors the API floor and ceiling in bytes', () => {
    expect(MIN_PASSWORD_LENGTH).toBe(10);
    expect(MAX_PASSWORD_BYTES).toBe(72);
    expect(validatePassword('nine char')).toBe('Password must be at least 10 characters');
    expect(validatePassword('ten chars!')).toBeNull();
    expect(validatePassword('a'.repeat(72))).toBeNull();
    expect(validatePassword('a'.repeat(73))).toBe('Password must be at most 72 bytes');
  });

  it('counts bytes, not characters, exactly like the Go handler', () => {
    // 24 multi-byte characters are 72 bytes: accepted by Go's len(), so accepted here.
    expect(validatePassword('é'.repeat(36))).toBeNull();
    expect(validatePassword('é'.repeat(37))).toBe('Password must be at most 72 bytes');
  });
});
