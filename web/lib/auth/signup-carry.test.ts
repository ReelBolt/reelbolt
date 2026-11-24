import { describe, expect, it } from 'vitest';
import {
  CAMPAIGN_PARAMS,
  EMPTY_CARRY,
  directLandingPath,
  oauthStartHref,
  readSignupCarry,
  signupMarketingFields,
  withCarry,
} from './signup-carry';

const FULL = {
  for: 'Creator',
  plan: 'creator',
  next: '/app/projects/123?tab=files',
  utm_source: ' HackerNews ',
  utm_medium: 'community',
  utm_campaign: 'launch-2026-11',
  utm_content: 'hn-launch',
  utm_term: 'video+edit',
};

describe('readSignupCarry', () => {
  it('reads and sanitizes everything the link carried', () => {
    expect(readSignupCarry(FULL)).toEqual({
      persona: 'creator',
      plan: 'creator',
      next: '/app/projects/123?tab=files',
      campaign: {
        source: 'hackernews', medium: 'community', campaign: 'launch-2026-11', content: 'hn-launch', term: 'video+edit',
      },
    });
  });

  it('is empty for a plain link', () => {
    expect(readSignupCarry({})).toEqual(EMPTY_CARRY);
    expect(readSignupCarry({ email: 'a@b.c', invite: 'tok' })).toEqual({ campaign: {} });
  });

  it('drops each invalid value on its own, never the rest', () => {
    expect(
      readSignupCarry({
        for: 'astronaut',
        plan: 'Creator Plan!',
        next: '//evil.example',
        utm_source: 'reelbolt.ai',
        utm_medium: 'has space',
        utm_campaign: 'ok',
        utm_content: 'a'.repeat(65),
        utm_term: '../x',
      }),
    ).toEqual({ campaign: { campaign: 'ok' } });
  });

  it('treats a repeated parameter as absent', () => {
    expect(readSignupCarry({ for: ['creator', 'agency'], utm_source: ['a', 'b'], next: ['/a', '/b'], plan: ['x', 'y'] }))
      .toEqual({ campaign: {} });
  });

  it('refuses every return path sign-in refuses', () => {
    for (const next of ['https://evil.example', '/\\evil.example', '/a b', 'javascript:alert(1)', '/', '/x://y']) {
      expect(readSignupCarry({ next }).next).toBeUndefined();
    }
  });

  it('accepts the whole persona set, other included, and nothing else', () => {
    for (const p of ['creator', 'business', 'educator', 'nonprofit', 'software', 'agency', 'other']) {
      expect(readSignupCarry({ for: p }).persona).toBe(p);
    }
  });
});

describe('withCarry', () => {
  it('returns the href untouched when nothing is carried', () => {
    expect(withCarry('/login', EMPTY_CARRY)).toBe('/login');
  });

  it('keeps for, utm, plan and next, in a fixed order, percent-encoded', () => {
    expect(withCarry('/login', readSignupCarry(FULL))).toBe(
      '/login?for=creator&utm_source=hackernews&utm_medium=community&utm_campaign=launch-2026-11' +
        '&utm_content=hn-launch&utm_term=video%2Bedit&plan=creator&next=%2Fapp%2Fprojects%2F123%3Ftab%3Dfiles',
    );
  });

  it('round-trips: what a link carries is read back unchanged on the other page', () => {
    const carry = readSignupCarry(FULL);
    const href = withCarry('/signup', carry);
    const params = Object.fromEntries(new URLSearchParams(href.split('?')[1]));
    expect(readSignupCarry(params)).toEqual(carry);
  });

  it('appends to an href that already has a query', () => {
    expect(withCarry('/signup?invite=tok', { persona: 'agency', campaign: {} })).toBe('/signup?invite=tok&for=agency');
  });
});

describe('oauthStartHref', () => {
  it('is the bare start endpoint when nothing is carried', () => {
    expect(oauthStartHref('google')).toBe('/api/v1/auth/oauth/google/start');
    expect(oauthStartHref('github', EMPTY_CARRY)).toBe('/api/v1/auth/oauth/github/start');
  });

  it('carries for and the five utm parameters, the names the Go start endpoint reads', () => {
    const href = oauthStartHref('google', readSignupCarry(FULL));
    expect(href).toBe(
      '/api/v1/auth/oauth/google/start?for=creator&utm_source=hackernews&utm_medium=community' +
        '&utm_campaign=launch-2026-11&utm_content=hn-launch&utm_term=video%2Bedit',
    );
    expect(CAMPAIGN_PARAMS.map(([param]) => param)).toEqual([
      'utm_source', 'utm_medium', 'utm_campaign', 'utm_content', 'utm_term',
    ]);
  });

  it('never puts the plan or the return path on a provider link', () => {
    const href = oauthStartHref('github', { campaign: {}, plan: 'pro', next: '/app/x' });
    expect(href).toBe('/api/v1/auth/oauth/github/start');
  });
});

describe('signupMarketingFields', () => {
  it('omits the objects when the link carried nothing, and sends only the plan when it had one', () => {
    expect(signupMarketingFields(EMPTY_CARRY)).toEqual({});
    expect(signupMarketingFields(EMPTY_CARRY, '/app/signup')).toEqual({});
    expect(signupMarketingFields({ campaign: {}, plan: 'pro', next: '/app/x' })).toEqual({ plan: 'pro' });
  });

  it('sends the persona and the attribution object with the contract field names', () => {
    expect(signupMarketingFields(readSignupCarry(FULL), '/app/signup')).toEqual({
      persona: 'creator',
      plan: 'creator',
      attribution: {
        source: 'hackernews', medium: 'community', campaign: 'launch-2026-11', content: 'hn-launch',
        term: 'video+edit', landingPath: '/app/signup',
      },
    });
  });

  it('sends a landing path only next to a campaign value, and only a valid one', () => {
    expect(signupMarketingFields({ persona: 'agency', campaign: {} }, '/app/signup')).toEqual({ persona: 'agency' });
    expect(signupMarketingFields({ campaign: { source: 'hn' } })).toEqual({ attribution: { source: 'hn' } });
    expect(signupMarketingFields({ campaign: { source: 'hn' } }, '//evil.example')).toEqual({ attribution: { source: 'hn' } });
  });
});

/**
 * The page the visit began on. A sign-up page reached from another page of ours cannot see it (a
 * same-origin referrer is all it has), so an account captured from the URL carried no landing page
 * unless the person opened the sign-up page directly. The link that sends them here can name it.
 */
describe('the landing page named by the link (lp)', () => {
  const link = { utm_source: 'newsletter', utm_content: 'nl-nov', lp: '/for/Creators?x=1' };

  it('is read and sanitized like every landing path', () => {
    expect(readSignupCarry(link).landingPath).toBe('/for/creators');
    for (const lp of ['//evil.example/x', 'https://evil.example/', 'for/creators', '/a b', ['/', '/pricing']]) {
      expect(readSignupCarry({ utm_source: 'hn', lp }).landingPath).toBeUndefined();
    }
  });

  it('is sent as attribution.landingPath, ahead of what this document could tell', () => {
    const carry = readSignupCarry(link);
    // Came from the site: this page knows no landing of its own, the link does.
    expect(signupMarketingFields(carry, undefined)).toEqual({
      attribution: { source: 'newsletter', content: 'nl-nov', landingPath: '/for/creators' },
    });
    expect(signupMarketingFields(carry, '/app/signup').attribution?.landingPath).toBe('/for/creators');
  });

  it('rides the provider link and the links between the signed-out pages, only next to a campaign value', () => {
    const carry = readSignupCarry(link);
    expect(oauthStartHref('google', carry)).toBe(
      '/api/v1/auth/oauth/google/start?utm_source=newsletter&utm_content=nl-nov&lp=%2Ffor%2Fcreators',
    );
    expect(withCarry('/login', carry)).toBe('/login?utm_source=newsletter&utm_content=nl-nov&lp=%2Ffor%2Fcreators');
    const params = Object.fromEntries(new URLSearchParams(withCarry('/signup', carry).split('?')[1]));
    expect(readSignupCarry(params)).toEqual(carry);
    // No campaign: a landing page alone would be a touch with no source behind it.
    const bare = readSignupCarry({ lp: '/pricing', for: 'agency' });
    expect(oauthStartHref('google', bare)).toBe('/api/v1/auth/oauth/google/start?for=agency');
    expect(signupMarketingFields(bare, '/app/signup')).toEqual({ persona: 'agency' });
  });
});

describe('directLandingPath', () => {
  const origin = 'https://reelbolt.ai';

  it('is the document path when the visitor arrived with no referrer or from another origin', () => {
    expect(directLandingPath({ referrer: '', origin, documentUrl: `${origin}/app/signup?utm_source=hn` })).toBe('/app/signup');
    expect(directLandingPath({ referrer: 'https://news.ycombinator.com/item?id=1', origin, documentUrl: `${origin}/app/signup` }))
      .toBe('/app/signup');
  });

  it('uses the URL the document was loaded with, not where client-side navigation went', () => {
    expect(directLandingPath({ referrer: '', origin, documentUrl: `${origin}/app/login?utm_source=hn` })).toBe('/app/login');
  });

  it('is unknown when the visitor came from another page of ours (the marketing site)', () => {
    expect(directLandingPath({ referrer: `${origin}/for/creators`, origin, documentUrl: `${origin}/app/signup` })).toBeUndefined();
  });

  it('is unknown rather than a guess for anything it cannot parse', () => {
    expect(directLandingPath({ referrer: 'not a url', origin, documentUrl: `${origin}/app/signup` })).toBeUndefined();
    expect(directLandingPath({ referrer: '', origin, documentUrl: 'https://other.example/x' })).toBeUndefined();
  });
});
