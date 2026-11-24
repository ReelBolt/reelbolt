import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render } from '@testing-library/react';

let mockPath = '/projects/3f2b9c1e-1111-2222-3333-444455556666/edit';
vi.mock('next/navigation', () => ({ usePathname: () => mockPath }));

import { GoogleAnalytics } from './GoogleAnalytics';
import { runSignupCapture, sendSignupSubmitted } from './SignupTracker';
import { decodeFirstTouch, encodeFirstTouch } from '@/lib/attribution';

type W = Window & { dataLayer?: unknown[][]; gtag?: unknown } & Record<string, unknown>;

function reset() {
  localStorage.clear();
  document.head.innerHTML = '';
  document.cookie = 'rb_ft=; Max-Age=0; Path=/';
  const w = window as unknown as W;
  delete w.dataLayer;
  delete w.gtag;
  delete w['ga-disable-G-TEST'];
}
const gtagScript = () => document.getElementById('ga4-gtag-js');
/** The JSON body of the most recent beacon. */
const lastBody = () =>
  JSON.parse((vi.mocked(fetch).mock.calls.at(-1)![1] as RequestInit).body as string) as Record<string, unknown>;
/** A visitor id an earlier visit left in localStorage. */
const OLD_VID = '99999999-9999-4999-8999-999999999999';
const cookieValue = (name: string) =>
  document.cookie.split('; ').find((c) => c.startsWith(`${name}=`))?.slice(name.length + 1);

describe('GoogleAnalytics', () => {
  beforeEach(reset);

  it('adds no tag when consent is null', () => {
    render(<GoogleAnalytics measurementId="G-TEST" />);
    expect(gtagScript()).toBeNull();
  });
  it('adds no tag when denied, and sets ga-disable', () => {
    localStorage.setItem('rf-cookie-consent', 'denied');
    render(<GoogleAnalytics measurementId="G-TEST" />);
    expect(gtagScript()).toBeNull();
    expect((window as unknown as W)['ga-disable-G-TEST']).toBe(true);
  });
  it('adds no tag without a measurement id', () => {
    localStorage.setItem('rf-cookie-consent', 'granted');
    render(<GoogleAnalytics measurementId="" />);
    expect(gtagScript()).toBeNull();
  });
  it('loads the tag when granted, privacy-config, with a redacted page_view', () => {
    localStorage.setItem('rf-cookie-consent', 'granted');
    render(<GoogleAnalytics measurementId="G-TEST" />);
    expect(gtagScript()).not.toBeNull();
    const dl = (window as unknown as W).dataLayer as unknown as { 0: string; 1: unknown; 2: Record<string, string> }[];
    const cfg = dl.find((a) => a[0] === 'config')!;
    expect(cfg[2]).toMatchObject({
      send_page_view: false,
      allow_google_signals: false,
      allow_ad_personalization_signals: false,
    });
    const pv = dl.find((a) => a[0] === 'event')!;
    expect(pv[2].page_path).toBe('/projects/:id/:id');
    expect(JSON.stringify(pv[2])).not.toMatch(/3f2b9c1e/);
  });
  it('loads on a later storage event, and disables after denial', () => {
    render(<GoogleAnalytics measurementId="G-TEST" />);
    expect(gtagScript()).toBeNull();
    localStorage.setItem('rf-cookie-consent', 'granted');
    window.dispatchEvent(new StorageEvent('storage', { key: 'rf-cookie-consent' }));
    expect(gtagScript()).not.toBeNull();
    localStorage.setItem('rf-cookie-consent', 'denied');
    window.dispatchEvent(new StorageEvent('storage', { key: 'rf-cookie-consent' }));
    expect((window as unknown as W)['ga-disable-G-TEST']).toBe(true);
  });
});

describe('signup fallback capture', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    reset();
    window.history.pushState({}, '', '/signup?utm_source=Newsletter&utm_medium=email');
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(null))));
  });

  it('writes a session rb_ft from the URL utm params, with no expiry', () => {
    localStorage.setItem('rf-cookie-consent', 'granted');
    const setter = vi.spyOn(document, 'cookie', 'set');
    runSignupCapture();
    const written = setter.mock.calls.map((c) => String(c[0])).find((c) => c.startsWith('rb_ft='))!;
    expect(written).toBeDefined();
    expect(written).not.toMatch(/Max-Age|Expires/i);
    setter.mockRestore();
  });
  it('does not overwrite a readable rb_ft', () => {
    localStorage.setItem('rf-cookie-consent', 'granted');
    const existing = encodeFirstTouch({ visitorId: OLD_VID, ts: 1_700_000_000, source: 'hackernews' })!;
    document.cookie = `rb_ft=${existing}; Path=/`;
    const setter = vi.spyOn(document, 'cookie', 'set');
    runSignupCapture();
    expect(setter).not.toHaveBeenCalled();
    setter.mockRestore();
    expect(cookieValue('rb_ft')).toBe(existing);
  });
  it('writes no cookie and sends nothing without consent granted', () => {
    localStorage.setItem('rf-visitor-id', OLD_VID);
    for (const v of [null, 'denied']) {
      if (v) localStorage.setItem('rf-cookie-consent', v);
      const setter = vi.spyOn(document, 'cookie', 'set');
      runSignupCapture();
      expect(setter).not.toHaveBeenCalled();
      setter.mockRestore();
    }
    expect(fetch).not.toHaveBeenCalled();
  });
  it('reads and stores nothing without consent granted: no visitor id is created', () => {
    document.cookie = 'rb_ft=existing; Path=/';
    for (const v of [null, 'denied']) {
      if (v) localStorage.setItem('rf-cookie-consent', v);
      const cookieRead = vi.spyOn(document, 'cookie', 'get');
      const stored = vi.spyOn(Storage.prototype, 'setItem');
      const read = vi.spyOn(Storage.prototype, 'getItem');
      runSignupCapture();
      sendSignupSubmitted();
      expect(cookieRead).not.toHaveBeenCalled();
      expect(stored).not.toHaveBeenCalled();
      // The consent choice itself is the only thing looked at.
      expect(read.mock.calls.map((c) => c[0])).toEqual(['rf-cookie-consent', 'rf-cookie-consent']);
      cookieRead.mockRestore();
      stored.mockRestore();
      read.mockRestore();
      expect(localStorage.getItem('rf-visitor-id')).toBeNull();
    }
    expect(fetch).not.toHaveBeenCalled();
  });
  it('beacons signup.started only with consent', () => {
    localStorage.setItem('rf-visitor-id', OLD_VID);
    runSignupCapture();
    expect(fetch).not.toHaveBeenCalled();
    localStorage.setItem('rf-cookie-consent', 'granted');
    runSignupCapture();
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(vi.mocked(fetch).mock.calls[0][0]).toBe('/api/v1/telemetry/visit');
    expect(lastBody()).toMatchObject({ event: 'signup.started', source: 'newsletter' });
  });
  it('no longer needs a visitor id from the site: with consent it creates and stores one', () => {
    localStorage.setItem('rf-cookie-consent', 'granted');
    runSignupCapture();
    expect(fetch).toHaveBeenCalledTimes(1);
    const id = lastBody().visitorId as string;
    expect(id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    expect(localStorage.getItem('rf-visitor-id')).toBe(id);
    // The session cookie written in the same pass names the same visitor.
    expect(decodeFirstTouch(cookieValue('rb_ft')!)?.visitorId).toBe(id);
    // And the next beacon reuses it.
    runSignupCapture();
    expect(lastBody().visitorId).toBe(id);
  });
});

/**
 * The site's identity rule, on the signup page: a readable rb_ft names the visitor, and a fresh
 * rb_ft is a fresh visitor. An id left in localStorage by an earlier visit already has a first
 * touch on the server (first write wins), so writing a new cookie next to it would count the same
 * person under one link as a visitor and under another as an account.
 */
describe('signup visitor identity', () => {
  const NEW_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
  const COOKIE_VID = '22222222-2222-4222-8222-222222222222';

  beforeEach(() => {
    vi.restoreAllMocks();
    reset();
    localStorage.setItem('rf-cookie-consent', 'granted');
    window.history.pushState({}, '', '/signup?utm_source=newsletter&utm_content=nl-nov');
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(null))));
  });

  it('mints a new visitor id with a fresh rb_ft instead of reusing the stored one', () => {
    localStorage.setItem('rf-visitor-id', OLD_VID);
    runSignupCapture();
    const id = lastBody().visitorId as string;
    expect(id).toMatch(NEW_ID);
    expect(id).not.toBe(OLD_VID);
    // One fact: the cookie the account will be attributed from, the beacon and localStorage agree.
    const cookie = decodeFirstTouch(cookieValue('rb_ft')!)!;
    expect(cookie).toMatchObject({ visitorId: id, source: 'newsletter', content: 'nl-nov' });
    expect(localStorage.getItem('rf-visitor-id')).toBe(id);
    expect(lastBody()).toMatchObject({ event: 'signup.started', source: 'newsletter', content: 'nl-nov' });
  });

  it('keeps naming that new visitor for the submit beacon, and after the API cleared the cookie', () => {
    localStorage.setItem('rf-visitor-id', OLD_VID);
    runSignupCapture();
    const id = lastBody().visitorId as string;
    sendSignupSubmitted();
    expect(lastBody()).toMatchObject({ event: 'signup.submitted', visitorId: id, content: 'nl-nov' });
    document.cookie = 'rb_ft=; Max-Age=0; Path=/';
    sendSignupSubmitted();
    expect(lastBody().visitorId).toBe(id);
    // A beacon never starts a first touch: only the page opening does.
    expect(cookieValue('rb_ft')).toBeUndefined();
  });

  it('takes the id from a readable rb_ft, and localStorage follows the cookie', () => {
    localStorage.setItem('rf-visitor-id', OLD_VID);
    const existing = encodeFirstTouch({ visitorId: COOKIE_VID, ts: 1_700_000_000, source: 'hackernews' })!;
    document.cookie = `rb_ft=${existing}; Path=/`;
    runSignupCapture();
    expect(lastBody()).toMatchObject({ visitorId: COOKIE_VID, source: 'hackernews' });
    expect(localStorage.getItem('rf-visitor-id')).toBe(COOKIE_VID);
    expect(cookieValue('rb_ft')).toBe(existing);
  });

  it('replaces an rb_ft that cannot be read, as the site does, and starts a new visitor', () => {
    localStorage.setItem('rf-visitor-id', OLD_VID);
    document.cookie = 'rb_ft=existing; Path=/';
    runSignupCapture();
    const id = lastBody().visitorId as string;
    expect(id).not.toBe(OLD_VID);
    expect(decodeFirstTouch(cookieValue('rb_ft')!)).toMatchObject({ visitorId: id, source: 'newsletter' });
  });

  it('treats a cookie whose visitor id is not an id as unreadable', () => {
    document.cookie = `rb_ft=${encodeFirstTouch({ visitorId: 'someone', ts: 1, source: 'hackernews' })}; Path=/`;
    runSignupCapture();
    const id = lastBody().visitorId as string;
    expect(id).toMatch(NEW_ID);
    expect(decodeFirstTouch(cookieValue('rb_ft')!)).toMatchObject({ visitorId: id, source: 'newsletter' });
    expect(lastBody()).toMatchObject({ source: 'newsletter', content: 'nl-nov' });
  });

  it('keeps the stored id when it writes no cookie: an address without campaign parameters', () => {
    window.history.pushState({}, '', '/signup');
    localStorage.setItem('rf-visitor-id', OLD_VID);
    runSignupCapture();
    expect(lastBody()).toEqual({ event: 'signup.started', visitorId: OLD_VID });
    expect(cookieValue('rb_ft')).toBeUndefined();
    // A second load does not create a second visitor either.
    runSignupCapture();
    expect(lastBody().visitorId).toBe(OLD_VID);
  });

  it('keeps the stored id when the fresh cookie cannot be written (cookies blocked)', () => {
    localStorage.setItem('rf-visitor-id', OLD_VID);
    const setter = vi.spyOn(document, 'cookie', 'set').mockImplementation(() => undefined);
    runSignupCapture();
    setter.mockRestore();
    expect(lastBody()).toMatchObject({ visitorId: OLD_VID, source: 'newsletter' });
    expect(localStorage.getItem('rf-visitor-id')).toBe(OLD_VID);
  });

  it('replaces a stored value that is not an id', () => {
    window.history.pushState({}, '', '/signup');
    localStorage.setItem('rf-visitor-id', 'v-1');
    runSignupCapture();
    const id = lastBody().visitorId as string;
    expect(id).toMatch(NEW_ID);
    expect(localStorage.getItem('rf-visitor-id')).toBe(id);
  });
});

describe('signup.started carries the real first touch', () => {
  const VID = '11111111-1111-4111-8111-111111111111';
  const firstTouch = (ft: Partial<Parameters<typeof encodeFirstTouch>[0]>) =>
    encodeFirstTouch({ visitorId: VID, ts: 1_700_000_000, ...ft })!;

  beforeEach(() => {
    vi.restoreAllMocks();
    reset();
    localStorage.setItem('rf-cookie-consent', 'granted');
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(null))));
  });

  it('reports the stored first touch, not the campaign link the visitor followed today', () => {
    document.cookie = `rb_ft=${firstTouch({
      source: 'hackernews', medium: 'community', campaign: 'launch-2026-11', content: 'hn-launch', term: 'video',
      landingPath: '/for/software-teams',
    })}; Path=/`;
    window.history.pushState({}, '', '/signup?utm_source=newsletter&utm_medium=email');
    runSignupCapture();
    expect(lastBody()).toEqual({
      event: 'signup.started',
      visitorId: VID,
      source: 'hackernews',
      medium: 'community',
      campaign: 'launch-2026-11',
      content: 'hn-launch',
      term: 'video',
      landingPath: '/for/software-teams',
    });
    // The id inside the cookie became the stored visitor id, so the account row and the beacons agree.
    expect(localStorage.getItem('rf-visitor-id')).toBe(VID);
  });
  it('reports a referrer first touch as its host, with no utm_source invented for it', () => {
    document.cookie = `rb_ft=${firstTouch({ referrerHost: 'news.ycombinator.com', landingPath: '/pricing' })}; Path=/`;
    window.history.pushState({}, '', '/signup?utm_source=newsletter');
    runSignupCapture();
    expect(lastBody()).toEqual({
      event: 'signup.started', visitorId: VID, referrerHost: 'news.ycombinator.com', landingPath: '/pricing',
    });
  });
  it('falls back to the URL utm parameters, content and term included, when no first touch is stored', () => {
    window.history.pushState(
      {}, '', '/signup?utm_source=Reddit&utm_medium=community&utm_campaign=launch&utm_content=r-videoediting&utm_term=Edit',
    );
    runSignupCapture();
    expect(lastBody()).toMatchObject({
      event: 'signup.started', source: 'reddit', medium: 'community', campaign: 'launch',
      content: 'r-videoediting', term: 'edit',
    });
  });
  it('sends no source at all for a direct visit (the API labels it direct), never a made-up one', () => {
    window.history.pushState({}, '', '/signup');
    runSignupCapture();
    const body = lastBody();
    // No landing page either: no first touch starts on this page without a campaign parameter.
    expect(Object.keys(body).sort()).toEqual(['event', 'visitorId']);
    expect(body.event).toBe('signup.started');
  });
  it('drops invalid stored values instead of sending them', () => {
    document.cookie = `rb_ft=${firstTouch({ source: 'Bad Value!', medium: 'email', referrerHost: 'app.reelbolt.ai', landingPath: '//evil.example' })}; Path=/`;
    window.history.pushState({}, '', '/signup');
    runSignupCapture();
    expect(lastBody()).toEqual({ event: 'signup.started', visitorId: VID, medium: 'email' });
  });
  it('treats an unreadable cookie as no stored touch, and leaves it alone on an address without campaign parameters', () => {
    document.cookie = 'rb_ft=existing; Path=/';
    window.history.pushState({}, '', '/signup');
    const setter = vi.spyOn(document, 'cookie', 'set');
    runSignupCapture();
    expect(setter).not.toHaveBeenCalled();
    setter.mockRestore();
    expect(Object.keys(lastBody()).sort()).toEqual(['event', 'visitorId']);
  });
});

/**
 * A first touch that starts on the signup page (a campaign link, no readable rb_ft) says where the
 * visit began, like one the marketing site wrote: the page the address names (`lp`), else the
 * signup page itself when the browser opened it from outside. The cookie is what the account's
 * attribution row is written from, so without this a consented sign-up had no landing page while
 * the same link without consent (URL capture) had one.
 */
describe('signup first touch landing page', () => {
  const setReferrer = (value: string) =>
    Object.defineProperty(document, 'referrer', { value, configurable: true });
  const cookieTouch = () => decodeFirstTouch(cookieValue('rb_ft')!)!;

  beforeEach(() => {
    vi.restoreAllMocks();
    reset();
    setReferrer('');
    localStorage.setItem('rf-cookie-consent', 'granted');
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(null))));
  });

  it('writes the lp of the address into the fresh cookie and both beacons', () => {
    setReferrer(`${window.location.origin}/for/creators`);
    window.history.pushState({}, '', '/signup?utm_source=newsletter&utm_content=nl-nov&lp=%2FFor%2FCreators%3Fx%3D1');
    runSignupCapture();
    expect(cookieTouch()).toMatchObject({ source: 'newsletter', content: 'nl-nov', landingPath: '/for/creators' });
    expect(lastBody()).toMatchObject({ event: 'signup.started', content: 'nl-nov', landingPath: '/for/creators' });
    sendSignupSubmitted();
    expect(lastBody()).toMatchObject({ event: 'signup.submitted', content: 'nl-nov', landingPath: '/for/creators' });
  });
  it('names the signup page itself when the browser opened it from outside with a campaign link', () => {
    setReferrer('https://news.ycombinator.com/item?id=1');
    window.history.pushState({}, '', '/signup?utm_source=hackernews');
    runSignupCapture();
    expect(cookieTouch().landingPath).toBe('/signup');
    expect(lastBody()).toMatchObject({ source: 'hackernews', landingPath: '/signup' });
  });
  it('leaves the page unknown when another page of ours sent the visitor without naming it', () => {
    setReferrer(`${window.location.origin}/pricing`);
    window.history.pushState({}, '', '/signup?utm_source=newsletter');
    runSignupCapture();
    expect(cookieTouch().landingPath).toBeUndefined();
    expect(lastBody()).toEqual({ event: 'signup.started', visitorId: cookieTouch().visitorId, source: 'newsletter' });
  });
  it('ignores an lp that is not a path of ours, or that appears twice', () => {
    for (const lp of ['lp=%2F%2Fevil.example%2Fx', 'lp=https%3A%2F%2Fevil.example', 'lp=%2Fa&lp=%2Fb']) {
      reset();
      localStorage.setItem('rf-cookie-consent', 'granted');
      window.history.pushState({}, '', `/signup?utm_source=newsletter&${lp}`);
      runSignupCapture();
      expect(cookieTouch().landingPath).toBe('/signup');
    }
  });
  it('ignores an lp with no campaign parameter next to it: no first touch starts, no page is reported', () => {
    window.history.pushState({}, '', '/signup?lp=%2Fpricing');
    runSignupCapture();
    expect(cookieValue('rb_ft')).toBeUndefined();
    expect(lastBody()).toEqual({ event: 'signup.started', visitorId: expect.any(String) });
  });
  it('never replaces the landing page of a stored first touch', () => {
    const existing = encodeFirstTouch({ visitorId: OLD_VID, ts: 1_700_000_000, source: 'reddit', landingPath: '/blog' })!;
    document.cookie = `rb_ft=${existing}; Path=/`;
    window.history.pushState({}, '', '/signup?utm_source=newsletter&lp=%2Fpricing');
    runSignupCapture();
    expect(cookieValue('rb_ft')).toBe(existing);
    expect(lastBody()).toMatchObject({ source: 'reddit', landingPath: '/blog' });
  });
  it('keeps the same landing page for a submit sent after the API used and cleared the cookie', () => {
    window.history.pushState({}, '', '/signup?utm_source=newsletter&lp=%2Fpricing');
    runSignupCapture();
    document.cookie = 'rb_ft=; Max-Age=0; Path=/';
    sendSignupSubmitted();
    expect(lastBody()).toMatchObject({ event: 'signup.submitted', source: 'newsletter', landingPath: '/pricing' });
  });
  it('still sends the campaign and the landing page when the cookie cannot be written', () => {
    window.history.pushState({}, '', '/signup?utm_source=newsletter&lp=%2Fpricing');
    const setter = vi.spyOn(document, 'cookie', 'set').mockImplementation(() => undefined);
    runSignupCapture();
    setter.mockRestore();
    expect(cookieValue('rb_ft')).toBeUndefined();
    expect(lastBody()).toMatchObject({ source: 'newsletter', landingPath: '/pricing' });
  });
});

describe('signup.submitted', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    reset();
    window.history.pushState({}, '', '/signup?utm_source=Newsletter&utm_medium=email');
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(null))));
  });

  it('is sent to the visit endpoint with the same visitor and first touch, and no detail', () => {
    localStorage.setItem('rf-cookie-consent', 'granted');
    localStorage.setItem('rf-visitor-id', OLD_VID);
    sendSignupSubmitted();
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(vi.mocked(fetch).mock.calls[0][0]).toBe('/api/v1/telemetry/visit');
    expect(lastBody()).toEqual({
      event: 'signup.submitted', visitorId: OLD_VID, source: 'newsletter', medium: 'email', landingPath: '/signup',
    });
  });
  it('sends nothing without consent granted', () => {
    localStorage.setItem('rf-visitor-id', OLD_VID);
    sendSignupSubmitted();
    localStorage.setItem('rf-cookie-consent', 'denied');
    sendSignupSubmitted();
    expect(fetch).not.toHaveBeenCalled();
  });
  it('never throws, even when the request cannot be made', () => {
    localStorage.setItem('rf-cookie-consent', 'granted');
    vi.stubGlobal('fetch', vi.fn(() => { throw new Error('blocked'); }));
    expect(() => sendSignupSubmitted()).not.toThrow();
  });
});

describe('GoogleAnalytics on token routes', () => {
  beforeEach(() => {
    reset();
    localStorage.setItem('rf-cookie-consent', 'granted');
  });
  for (const url of ['/app/reset-password?token=SECRET', '/app/verify-email?token=SECRET', '/app/invite/SECRET']) {
    it(`does not load GA on ${url}`, () => {
      window.history.pushState({}, '', url);
      mockPath = url.split('?')[0];
      render(<GoogleAnalytics measurementId="G-TEST" />);
      expect(gtagScript()).toBeNull();
      expect(JSON.stringify((window as unknown as W).dataLayer ?? [])).not.toContain('SECRET');
    });
  }
  it('never passes a query string or token to gtag on an allowed route', () => {
    window.history.pushState({}, '', '/signup?utm_source=x&token=SECRET');
    mockPath = '/signup';
    render(<GoogleAnalytics measurementId="G-TEST" />);
    const dump = JSON.stringify((window as unknown as W).dataLayer);
    expect(dump).not.toContain('SECRET');
    expect(dump).not.toContain('?');
    const dl = (window as unknown as W).dataLayer as unknown as { 0: string; 1: unknown; 2?: Record<string, string> }[];
    expect(dl.find((a) => a[0] === 'config')![2]).toMatchObject({ page_referrer: '' });
    expect(dl.some((a) => a[0] === 'set')).toBe(true);
  });
});

describe('GoogleAnalytics withdrawal and route changes', () => {
  beforeEach(() => {
    reset();
    localStorage.setItem('rf-cookie-consent', 'granted');
    window.history.pushState({}, '', '/signup?invite=SECRET');
    mockPath = '/signup';
  });
  const storage = () => window.dispatchEvent(new StorageEvent('storage', { key: 'rf-cookie-consent' }));
  it('never leaks a /signup query', () => {
    render(<GoogleAnalytics measurementId="G-TEST" />);
    expect(JSON.stringify((window as unknown as W).dataLayer)).not.toContain('SECRET');
  });
  it('disables on withdrawal to null and re-enables on grant', () => {
    render(<GoogleAnalytics measurementId="G-TEST" />);
    expect(gtagScript()).not.toBeNull();
    localStorage.removeItem('rf-cookie-consent');
    storage();
    expect((window as unknown as W)['ga-disable-G-TEST']).toBe(true);
    localStorage.setItem('rf-cookie-consent', 'granted');
    storage();
    expect((window as unknown as W)['ga-disable-G-TEST']).toBe(false);
  });
  it('disables on a token route and re-enables off it', () => {
    const { rerender } = render(<GoogleAnalytics measurementId="G-TEST" />);
    window.history.pushState({}, '', '/app/reset-password?token=SECRET');
    mockPath = '/reset-password';
    rerender(<GoogleAnalytics measurementId="G-TEST" />);
    expect((window as unknown as W)['ga-disable-G-TEST']).toBe(true);
    window.history.pushState({}, '', '/signup');
    mockPath = '/signup';
    rerender(<GoogleAnalytics measurementId="G-TEST" />);
    expect((window as unknown as W)['ga-disable-G-TEST']).toBe(false);
    expect(JSON.stringify((window as unknown as W).dataLayer)).not.toContain('SECRET');
  });
});
