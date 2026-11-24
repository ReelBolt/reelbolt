import { describe, expect, it } from 'vitest';
import { encodeFirstTouch } from './index';
import {
  beaconFirstTouch,
  hasCampaignParams,
  planSignupIdentity,
  readStoredFirstTouch,
  sanitizeVisitorId,
  signupLandingPath,
  visitorEventBody,
  VISIT_ENDPOINT,
} from './visitor-events';

const VID = '11111111-1111-4111-8111-111111111111';

describe('readStoredFirstTouch', () => {
  it('is null for no cookie or one that cannot be decoded', () => {
    expect(readStoredFirstTouch(null)).toBeNull();
    expect(readStoredFirstTouch('')).toBeNull();
    expect(readStoredFirstTouch('existing')).toBeNull();
    expect(readStoredFirstTouch('!!!')).toBeNull();
  });

  it('returns every valid field and the visitor id', () => {
    const cookie = encodeFirstTouch({
      visitorId: VID, ts: 1_700_000_000, source: 'hn', medium: 'community', campaign: 'launch',
      content: 'hn-launch', term: 'video', referrerHost: 'news.ycombinator.com', landingPath: '/for/creators',
    })!;
    expect(readStoredFirstTouch(cookie)).toEqual({
      visitorId: VID,
      touch: {
        source: 'hn', medium: 'community', campaign: 'launch', content: 'hn-launch', term: 'video',
        referrerHost: 'news.ycombinator.com', landingPath: '/for/creators',
      },
    });
  });

  it('sanitizes again: a cookie is whatever the browser holds', () => {
    const cookie = encodeFirstTouch({
      visitorId: 'not-a-uuid', ts: 1, source: 'reelbolt.ai', medium: 'Has Space', campaign: 'ok',
      referrerHost: 'staging.example.com', landingPath: '//evil.example/x',
    })!;
    // Own hosts (reelbolt.ai, and the host the page is on) are not sources or referrers.
    expect(readStoredFirstTouch(cookie, 'staging.example.com')).toEqual({ touch: { campaign: 'ok' } });
  });

  it('keeps a touch that holds nothing but the visitor (a direct first visit)', () => {
    const cookie = encodeFirstTouch({ visitorId: VID, ts: 1 })!;
    expect(readStoredFirstTouch(cookie)).toEqual({ visitorId: VID, touch: {} });
  });
});

describe('sanitizeVisitorId', () => {
  it('accepts a version-4 UUID, lowercased, and nothing else', () => {
    expect(sanitizeVisitorId(` ${VID.toUpperCase()} `)).toBe(VID);
    expect(sanitizeVisitorId('v-1')).toBeNull();
    expect(sanitizeVisitorId('11111111-1111-1111-8111-111111111111')).toBeNull();
    expect(sanitizeVisitorId('')).toBeNull();
    expect(sanitizeVisitorId(null)).toBeNull();
  });
});

describe('planSignupIdentity', () => {
  const STORED = '22222222-2222-4222-8222-222222222222';
  const CANDIDATE = '33333333-3333-4333-8333-333333333333';
  const plan = (o: Partial<Parameters<typeof planSignupIdentity>[0]>) =>
    planSignupIdentity({
      firstTouchVisitorId: null, storedVisitorId: null, candidate: CANDIDATE, freshFirstTouchWritten: false, ...o,
    });

  it('a readable first touch names the visitor, whatever is stored or written', () => {
    expect(plan({ firstTouchVisitorId: VID, storedVisitorId: STORED })).toBe(VID);
    expect(plan({ firstTouchVisitorId: VID, storedVisitorId: STORED, freshFirstTouchWritten: true })).toBe(VID);
  });

  it('a fresh first touch is a new visitor, never the stored one', () => {
    expect(plan({ storedVisitorId: STORED, freshFirstTouchWritten: true })).toBe(CANDIDATE);
    expect(plan({ freshFirstTouchWritten: true })).toBe(CANDIDATE);
  });

  it('with no cookie read or written the stored id is kept, and one is minted only when none is stored', () => {
    expect(plan({ storedVisitorId: STORED })).toBe(STORED);
    expect(plan({})).toBe(CANDIDATE);
  });
});

describe('beaconFirstTouch', () => {
  const url = { source: 'newsletter', medium: 'email', campaign: 'c', content: 'n', term: 't', landingPath: '/app/signup' };

  it('uses the stored touch whenever there is one, even an empty (direct) one', () => {
    expect(beaconFirstTouch({ stored: { touch: { source: 'hn' } }, url })).toEqual({ source: 'hn' });
    expect(beaconFirstTouch({ stored: { touch: {} }, url })).toEqual({});
  });

  it('otherwise uses the campaign parameters of the URL, and only those', () => {
    expect(beaconFirstTouch({ stored: null, url })).toEqual({
      source: 'newsletter', medium: 'email', campaign: 'c', content: 'n', term: 't',
    });
  });

  it('adds the landing page it is given next to them, never the path inside the URL touch, never over a stored one', () => {
    expect(beaconFirstTouch({ stored: null, url, landingPath: '/For/Creators?x=1' })).toMatchObject({
      source: 'newsletter', landingPath: '/for/creators',
    });
    expect(beaconFirstTouch({ stored: null, url, landingPath: '//evil.example' }).landingPath).toBeUndefined();
    expect(beaconFirstTouch({ stored: { touch: { landingPath: '/blog' } }, url, landingPath: '/pricing' })).toEqual({
      landingPath: '/blog',
    });
    // No campaign parameter, no first touch started here: a direct visit stays empty.
    expect(beaconFirstTouch({ stored: null, url: {}, landingPath: '/app/signup' })).toEqual({});
  });

  it('is empty for a direct visit, and never names an own host as the source', () => {
    expect(beaconFirstTouch({ stored: null, url: {} })).toEqual({});
    expect(beaconFirstTouch({ stored: null, url: { source: 'app.reelbolt.ai', medium: 'email' } })).toEqual({ medium: 'email' });
    expect(beaconFirstTouch({ stored: null, url: { source: 'selfhost.example' }, ownHost: 'selfhost.example' })).toEqual({});
  });
});

describe('signupLandingPath', () => {
  const campaign = { content: 'nl-nov' };

  it('is the page the link names', () => {
    expect(signupLandingPath({ url: campaign, linkLandingPath: '/Pricing#plans', documentLandingPath: '/app/signup' }))
      .toBe('/pricing');
    // The link knows where the visit began even when this document cannot tell.
    expect(signupLandingPath({ url: campaign, linkLandingPath: '/pricing', documentLandingPath: undefined })).toBe('/pricing');
  });

  it('otherwise is the document itself, when it was the landing', () => {
    expect(signupLandingPath({ url: campaign, linkLandingPath: undefined, documentLandingPath: '/app/signup' }))
      .toBe('/app/signup');
    expect(signupLandingPath({ url: campaign, linkLandingPath: '//evil.example', documentLandingPath: '/app/signup' }))
      .toBe('/app/signup');
    expect(signupLandingPath({ url: campaign, linkLandingPath: 'https://evil.example/x', documentLandingPath: '/app/signup' }))
      .toBe('/app/signup');
  });

  it('is nothing without a campaign parameter: no first touch starts on the page then', () => {
    expect(signupLandingPath({ url: {}, linkLandingPath: '/pricing', documentLandingPath: '/app/signup' })).toBeUndefined();
    // The path and referrer of the URL touch are not campaign parameters.
    expect(signupLandingPath({
      url: { landingPath: '/app/signup', referrerHost: 'example.com' },
      linkLandingPath: '/pricing',
      documentLandingPath: '/app/signup',
    })).toBeUndefined();
  });

  it('is unknown when neither says', () => {
    expect(signupLandingPath({ url: campaign, linkLandingPath: null, documentLandingPath: null })).toBeUndefined();
  });
});

describe('hasCampaignParams', () => {
  it('is true for any one of the five, false for a path or a referrer alone', () => {
    for (const k of ['source', 'medium', 'campaign', 'content', 'term'] as const) {
      expect(hasCampaignParams({ [k]: 'x' })).toBe(true);
    }
    expect(hasCampaignParams({ landingPath: '/x', referrerHost: 'example.com' })).toBe(false);
    expect(hasCampaignParams({})).toBe(false);
  });
});

describe('visitorEventBody', () => {
  it('posts to the visit endpoint shape, omitting absent fields and never sending a detail', () => {
    expect(VISIT_ENDPOINT).toBe('/api/v1/telemetry/visit');
    expect(visitorEventBody('signup.submitted', VID, {})).toEqual({ event: 'signup.submitted', visitorId: VID });
    const body = visitorEventBody('signup.started', VID, {
      source: 'hn', medium: 'community', campaign: 'launch', content: 'hn-launch', term: 'video',
      referrerHost: 'news.ycombinator.com', landingPath: '/pricing',
    });
    expect(body).toEqual({
      event: 'signup.started', visitorId: VID, source: 'hn', medium: 'community', campaign: 'launch',
      content: 'hn-launch', term: 'video', referrerHost: 'news.ycombinator.com', landingPath: '/pricing',
    });
    expect(body).not.toHaveProperty('detail');
  });
});
