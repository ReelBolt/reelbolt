import { describe, it, expect } from 'vitest';
import {
  decodeFirstTouch,
  encodeFirstTouch,
  parseAttribution,
  sanitizeHost,
  sanitizeLandingPath,
  sanitizeReferrerHost,
  sanitizeSource,
  sanitizeUtm,
  sourceLabel,
} from './index';

describe('own-host and path rules', () => {
  it('drops reelbolt.ai and its subdomains as referrers', () => {
    expect(sanitizeReferrerHost('https://reelbolt.ai/x')).toEqual(undefined);
    expect(sanitizeReferrerHost('https://app.reelbolt.ai/x')).toEqual(undefined);
    expect(sanitizeReferrerHost('https://notreelbolt.ai/x')).toEqual('notreelbolt.ai');
  });
  it('rejects landing paths starting with //', () => {
    expect(sanitizeLandingPath('//evil.com/x')).toEqual(undefined);
    expect(sanitizeLandingPath('/ok')).toEqual('/ok');
  });
});

describe('sanitizers', () => {
  it('lowercases and trims utm values', () => {
    expect(sanitizeUtm('  Newsletter ')).toEqual('newsletter');
  });
  it('drops invalid utm values', () => {
    expect(sanitizeUtm('bad value!')).toEqual(undefined);
    expect(sanitizeUtm('-lead')).toEqual(undefined);
    expect(sanitizeUtm('a'.repeat(65))).toEqual(undefined);
    expect(sanitizeUtm('')).toEqual(undefined);
  });
  it('extracts referrer host only, never own host', () => {
    expect(sanitizeReferrerHost('https://News.Example.com:8080/a?b=1', 'reelbolt.ai')).toEqual('news.example.com');
    expect(sanitizeReferrerHost('https://reelbolt.ai/x', 'reelbolt.ai')).toEqual(undefined);
    expect(sanitizeReferrerHost('not a url')).toEqual(undefined);
  });
  it('strips query and fragment from the landing path', () => {
    expect(sanitizeLandingPath('/Pricing?x=1#y')).toEqual('/pricing');
    expect(sanitizeLandingPath('/a b')).toEqual(undefined);
    expect(sanitizeLandingPath('/' + 'a'.repeat(120))).toEqual(undefined);
  });
});

describe('sources and bare hosts', () => {
  it('keeps a valid utm_source unless it names one of our own hosts', () => {
    expect(sanitizeSource(' HackerNews ')).toEqual('hackernews');
    expect(sanitizeSource('reelbolt.ai')).toEqual(undefined);
    expect(sanitizeSource('app.reelbolt.ai')).toEqual(undefined);
    expect(sanitizeSource('selfhost.example', 'SelfHost.example')).toEqual(undefined);
    expect(sanitizeSource('bad value!')).toEqual(undefined);
  });
  it('accepts a bare host, never a URL, a port or an own host', () => {
    expect(sanitizeHost('News.Example.com')).toEqual('news.example.com');
    expect(sanitizeHost('https://news.example.com')).toEqual(undefined);
    expect(sanitizeHost('news.example.com:8080')).toEqual(undefined);
    expect(sanitizeHost('reelbolt.ai')).toEqual(undefined);
    expect(sanitizeHost('my.host', 'my.host')).toEqual(undefined);
    expect(sanitizeHost('')).toEqual(undefined);
  });
});

describe('parseAttribution / sourceLabel', () => {
  it('prefers utm_source, then referrer, then direct', () => {
    const base = { pathname: '/', ownHost: 'reelbolt.ai' };
    const a = parseAttribution({ ...base, search: '?utm_source=X&utm_medium=cpc', referrer: 'https://g.com/' });
    expect(a).toEqual({ source: 'x', medium: 'cpc', referrerHost: 'g.com', landingPath: '/' });
    expect(sourceLabel(a)).toEqual('x');
    expect(sourceLabel(parseAttribution({ ...base, search: '', referrer: 'https://g.com/' }))).toEqual('g.com');
    expect(sourceLabel(parseAttribution({ ...base, search: '', referrer: '' }))).toEqual('direct');
  });
});

describe('rb_ft codec', () => {
  it('round-trips with contract keys', () => {
    const ft = { visitorId: '11111111-1111-4111-8111-111111111111', ts: 1700000000, source: 'x', landingPath: '/p' };
    const enc = encodeFirstTouch(ft)!;
    expect(enc).toMatch(/^[A-Za-z0-9_-]+$/);
    expect(JSON.parse(atob(enc.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - (enc.length % 4)) % 4)))).toEqual({
      v: 1, s: 'x', l: '/p', vid: ft.visitorId, ts: 1700000000,
    });
    expect(decodeFirstTouch(enc)).toEqual(ft);
  });
  it('rejects garbage', () => {
    expect(decodeFirstTouch('!!!')).toEqual(null);
  });
});
