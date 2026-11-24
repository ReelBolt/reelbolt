import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  decodeFirstTouch,
  encodeFirstTouch,
  parseAttribution,
  sanitizeLandingPath,
  sanitizeReferrerHost,
  sanitizeUtm,
  sourceLabel,
} from './attribution.ts';

describe('own-host and path rules', () => {
  it('drops reelbolt.ai and its subdomains as referrers', () => {
    assert.equal(sanitizeReferrerHost('https://reelbolt.ai/x'), undefined);
    assert.equal(sanitizeReferrerHost('https://app.reelbolt.ai/x'), undefined);
    assert.equal(sanitizeReferrerHost('https://notreelbolt.ai/x'), 'notreelbolt.ai');
  });
  it('rejects landing paths starting with //', () => {
    assert.equal(sanitizeLandingPath('//evil.com/x'), undefined);
    assert.equal(sanitizeLandingPath('/ok'), '/ok');
  });
});

describe('sanitizers', () => {
  it('lowercases and trims utm values', () => {
    assert.equal(sanitizeUtm('  Newsletter '), 'newsletter');
  });
  it('drops invalid utm values', () => {
    assert.equal(sanitizeUtm('bad value!'), undefined);
    assert.equal(sanitizeUtm('-lead'), undefined);
    assert.equal(sanitizeUtm('a'.repeat(65)), undefined);
    assert.equal(sanitizeUtm(''), undefined);
  });
  it('extracts referrer host only, never own host', () => {
    assert.equal(sanitizeReferrerHost('https://News.Example.com:8080/a?b=1', 'reelbolt.ai'), 'news.example.com');
    assert.equal(sanitizeReferrerHost('https://reelbolt.ai/x', 'reelbolt.ai'), undefined);
    assert.equal(sanitizeReferrerHost('not a url'), undefined);
  });
  it('strips query and fragment from the landing path', () => {
    assert.equal(sanitizeLandingPath('/Pricing?x=1#y'), '/pricing');
    assert.equal(sanitizeLandingPath('/a b'), undefined);
    assert.equal(sanitizeLandingPath('/' + 'a'.repeat(120)), undefined);
  });
});

describe('parseAttribution / sourceLabel', () => {
  it('prefers utm_source, then referrer, then direct', () => {
    const base = { pathname: '/', ownHost: 'reelbolt.ai' };
    const a = parseAttribution({ ...base, search: '?utm_source=X&utm_medium=cpc', referrer: 'https://g.com/' });
    assert.deepEqual(a, { source: 'x', medium: 'cpc', referrerHost: 'g.com', landingPath: '/' });
    assert.equal(sourceLabel(a), 'x');
    assert.equal(sourceLabel(parseAttribution({ ...base, search: '', referrer: 'https://g.com/' })), 'g.com');
    assert.equal(sourceLabel(parseAttribution({ ...base, search: '', referrer: '' })), 'direct');
  });
});

describe('rb_ft codec', () => {
  it('round-trips with contract keys', () => {
    const ft = { visitorId: '11111111-1111-4111-8111-111111111111', ts: 1700000000, source: 'x', landingPath: '/p' };
    const enc = encodeFirstTouch(ft)!;
    assert.match(enc, /^[A-Za-z0-9_-]+$/);
    assert.deepEqual(JSON.parse(atob(enc.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - (enc.length % 4)) % 4))), {
      v: 1, s: 'x', l: '/p', vid: ft.visitorId, ts: 1700000000,
    });
    assert.deepEqual(decodeFirstTouch(enc), ft);
  });
  it('rejects garbage', () => {
    assert.equal(decodeFirstTouch('!!!'), null);
  });
});
