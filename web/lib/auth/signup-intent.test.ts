// @vitest-environment jsdom
import { beforeEach, describe, expect, it } from 'vitest';
import { userKeyFromEmail } from '@/lib/telemetry/identity';
import {
  EMAIL_INTENT_TTL_MS,
  PROVIDER_INTENT_TTL_MS,
  SIGNUP_INTENT_KEY,
  clearSignupIntent,
  readSignupIntent,
  rememberSignupIntent,
} from './signup-intent';

const ADA = userKeyFromEmail('ada@example.com');
const NOW = Date.parse('2026-11-11T10:00:00Z');
const stored = () => JSON.parse(window.localStorage.getItem(SIGNUP_INTENT_KEY) ?? 'null');

beforeEach(() => window.localStorage.clear());

describe('the plan and the return path of a sign-up link, across the round trip', () => {
  it('an email sign-up keeps both for the address it was sent for', () => {
    rememberSignupIntent({ plan: 'pro', next: '/app/marketplace/recipes/abc' }, { email: ' Ada@Example.com ' }, NOW);
    expect(readSignupIntent(ADA, NOW + 60_000)).toEqual({ plan: 'pro', next: '/app/marketplace/recipes/abc' });
    // The address itself is never stored, only the local key the onboarding mirror also uses.
    expect(window.localStorage.getItem(SIGNUP_INTENT_KEY)).not.toContain('ada');
    expect(stored().who).toBe(ADA);
  });

  it('survives the wait for the inbox, and lapses after it', () => {
    rememberSignupIntent({ plan: 'creator' }, { email: 'ada@example.com' }, NOW);
    expect(readSignupIntent(ADA, NOW + EMAIL_INTENT_TTL_MS - 1)).toEqual({ plan: 'creator' });
    expect(readSignupIntent(ADA, NOW + EMAIL_INTENT_TTL_MS + 1)).toBeNull();
    expect(stored()).toBeNull();
  });

  it('is never handed to another person who signs in on the same browser', () => {
    rememberSignupIntent({ plan: 'pro', next: '/app/projects/9' }, { email: 'ada@example.com' }, NOW);
    expect(readSignupIntent(userKeyFromEmail('someone@else.test'), NOW + 1000)).toBeNull();
    expect(stored()).toBeNull();
  });

  it('a provider sign-up is honoured for the few minutes the round trip takes, then belongs to that person', () => {
    rememberSignupIntent({ plan: 'pro' }, {}, NOW);
    expect(stored().who).toBeUndefined();
    expect(readSignupIntent(ADA, NOW + 90_000)).toEqual({ plan: 'pro' });
    // Bound on first use: later visits by the same person still see it, nobody else does.
    expect(stored().who).toBe(ADA);
    expect(readSignupIntent(ADA, NOW + 2 * 60 * 60 * 1000)).toEqual({ plan: 'pro' });
    expect(readSignupIntent(userKeyFromEmail('someone@else.test'), NOW + 2 * 60 * 60 * 1000)).toBeNull();
  });

  it('a provider sign-up nobody completed lapses within minutes', () => {
    rememberSignupIntent({ next: '/app/projects/9' }, {}, NOW);
    expect(readSignupIntent(ADA, NOW + PROVIDER_INTENT_TTL_MS + 1)).toBeNull();
    expect(stored()).toBeNull();
  });

  it('writes nothing for a link that carried neither, and leaves an earlier entry alone', () => {
    rememberSignupIntent({}, { email: 'ada@example.com' }, NOW);
    expect(stored()).toBeNull();
    rememberSignupIntent({ plan: 'team' }, { email: 'ada@example.com' }, NOW);
    rememberSignupIntent({}, {}, NOW + 1000);
    expect(readSignupIntent(ADA, NOW + 2000)).toEqual({ plan: 'team' });
  });

  it('forgets one value when it is used and keeps the other', () => {
    rememberSignupIntent({ plan: 'pro', next: '/app/projects/9' }, { email: 'ada@example.com' }, NOW);
    clearSignupIntent('next');
    expect(readSignupIntent(ADA, NOW + 1000)).toEqual({ plan: 'pro' });
    clearSignupIntent('plan');
    expect(readSignupIntent(ADA, NOW + 1000)).toBeNull();
    expect(stored()).toBeNull();
  });

  it('trusts nothing it reads back: an unsafe return path and a malformed plan are dropped', () => {
    window.localStorage.setItem(SIGNUP_INTENT_KEY, JSON.stringify({ at: NOW, who: ADA, plan: 'Pro<script>', next: '//evil.example/x' }));
    expect(readSignupIntent(ADA, NOW + 1000)).toBeNull();
    window.localStorage.setItem(SIGNUP_INTENT_KEY, JSON.stringify({ at: NOW, who: ADA, plan: 'pro', next: 'https://evil.example/' }));
    expect(readSignupIntent(ADA, NOW + 1000)).toEqual({ plan: 'pro' });
    window.localStorage.setItem(SIGNUP_INTENT_KEY, 'not json');
    expect(readSignupIntent(ADA, NOW + 1000)).toBeNull();
    window.localStorage.setItem(SIGNUP_INTENT_KEY, JSON.stringify({ who: ADA, plan: 'pro' }));
    expect(readSignupIntent(ADA, NOW + 1000)).toBeNull();
  });

  it('answers nothing for a signed-out reader, and does not consume the entry', () => {
    rememberSignupIntent({ plan: 'pro' }, { email: 'ada@example.com' }, NOW);
    expect(readSignupIntent('', NOW + 1000)).toBeNull();
    expect(readSignupIntent(ADA, NOW + 1000)).toEqual({ plan: 'pro' });
  });
});
