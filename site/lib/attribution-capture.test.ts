import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
  beaconSource,
  beaconTouch,
  planCapture,
  planIdentity,
  readFirstTouchCookie,
  resolveConsent,
  sanitizeVisitorId,
} from './attribution-capture.ts';
import { FIRST_TOUCH_COOKIE, encodeFirstTouch } from './attribution.ts';

const landing = { source: 'news', landingPath: '/' };

test('granted on first landing writes rb_ft and beacons', () => {
  const p = planCapture({ consent: 'granted', landing, hasFirstTouch: false, visitSent: false });
  assert.deepEqual(p, { clear: false, writeFirstTouch: true, beacon: true, source: 'news' });
});

test('first touch wins: existing rb_ft is not rewritten', () => {
  const p = planCapture({ consent: 'granted', landing, hasFirstTouch: true, visitSent: false });
  assert.equal(p.writeFirstTouch, false);
  assert.equal(p.beacon, true);
});

test('beacons once per session', () => {
  const p = planCapture({ consent: 'granted', landing, hasFirstTouch: true, visitSent: true });
  assert.equal(p.beacon, false);
});

test('denied writes and beacons nothing and clears', () => {
  const p = planCapture({ consent: 'denied', landing, hasFirstTouch: false, visitSent: false });
  assert.deepEqual(p, { clear: true, writeFirstTouch: false, beacon: false });
});

test('an undecided null writes nothing, beacons nothing and clears NOTHING', () => {
  // null is also every first render of a returning accepted visitor (the provider's server
  // snapshot). Clearing on it wiped rb_ft and the visitor id on each full page load.
  for (const hasFirstTouch of [false, true]) {
    const p = planCapture({ consent: null, landing, hasFirstTouch, visitSent: false });
    assert.deepEqual(p, { clear: false, writeFirstTouch: false, beacon: false });
  }
});

test('resolveConsent: the stored choice is the truth while the provider still says null', () => {
  // A returning accepted visitor, first render after a full page load.
  assert.equal(resolveConsent({ consent: null, stored: 'granted', decidedThisPage: false }), 'granted');
  assert.equal(resolveConsent({ consent: null, stored: 'denied', decidedThisPage: false }), 'denied');
  // The provider's value wins once it has one.
  assert.equal(resolveConsent({ consent: 'granted', stored: 'granted', decidedThisPage: true }), 'granted');
  assert.equal(resolveConsent({ consent: 'denied', stored: 'denied', decidedThisPage: true }), 'denied');
});

test('resolveConsent: no choice anywhere is undecided for a new visitor, a withdrawal after a choice', () => {
  assert.equal(resolveConsent({ consent: null, stored: null, decidedThisPage: false }), null);
  // "Cookie preferences" in the footer removes the stored choice: the cookie and the id must go.
  assert.equal(resolveConsent({ consent: null, stored: null, decidedThisPage: true }), 'denied');
});

test('a returning accepted visitor keeps the first touch through the hydration null', () => {
  const consent = resolveConsent({ consent: null, stored: 'granted', decidedThisPage: false });
  const p = planCapture({ consent, landing: { landingPath: '/pricing' }, hasFirstTouch: true, visitSent: true });
  assert.deepEqual(p, { clear: false, writeFirstTouch: false, beacon: false, source: undefined });
});

test('the beacon source is utm_source only, never the source label', () => {
  assert.equal(beaconSource({ source: 'news', referrerHost: 'example.org' }), 'news');
  // No utm_source: the API derives the label from the referrer host, or says direct.
  assert.equal(beaconSource({ referrerHost: 'example.org' }), undefined);
  assert.equal(beaconSource({}), undefined);
  const p = planCapture({ consent: 'granted', landing: { referrerHost: 'example.org' }, hasFirstTouch: false, visitSent: false });
  assert.equal(p.source, undefined);
});

test('own-host source label is not sent', () => {
  for (const source of ['reelbolt.ai', 'app.reelbolt.ai', 'site.test']) {
    const p = planCapture({
      consent: 'granted', landing: { source }, hasFirstTouch: false, visitSent: false, ownHost: 'site.test',
    });
    assert.equal(p.source, undefined);
  }
});

test('the tracker resolves consent from storage, clears only on a plan that says so and stores nothing for the landing', () => {
  const tracker = readFileSync(new URL('../components/analytics/AttributionTracker.tsx', import.meta.url), 'utf8');
  assert.match(tracker, /const stored = readStoredConsent\(\);/);
  assert.match(tracker, /resolveConsent\(\{ consent, stored, decidedThisPage: decided\.current \}\)/);
  // The old bug, verbatim: a null consent treated as a withdrawal.
  assert.doesNotMatch(tracker, /consent === null/);
  assert.equal(tracker.match(/clearStored\(\)/g)?.length, 2, 'declared once, called once');
  assert.match(tracker, /planCapture\(\{ consent: choice, [^}]*\}\)\.clear\) \{\s*(\/\/[^\n]*\n\s*)*clearStored\(\);/);
  // Device reads and writes live in capture(), which only the granted branch calls.
  assert.equal(tracker.match(/\bcapture\(landing\)/g)?.length, 1);
  // Granted: the landing page's view first, then the current page's. A visitor who accepts after
  // navigating did view the page the document opened on, and BI counts distinct pages.
  assert.match(
    tracker,
    /if \(choice === 'granted'\) \{\s*capture\(landing\);\s*(\/\/[^\n]*\n\s*)*sendPageView\(pageKeyForPath\(landingMemory\.landingPage\(\)\)\);\s*sendPageView\(pageKeyForPath\(basePath\)\);/,
  );
  // The visitor id comes from `planIdentity`, and a fresh first touch is written with the id it
  // will name: the tracker has no other way to make or pick an id.
  assert.equal(tracker.match(/crypto\.randomUUID\(\)/g)?.length, 1);
  assert.match(tracker, /const identity = planIdentity\(\{[^}]*freshFirstTouchWritten: plan\.writeFirstTouch && writeFirstTouch\(landing, candidate\),\s*\}\);/);
  assert.match(tracker, /if \(!plan\.beacon && !identity\.renewed\) return;/);
  // The visit reports the visitor's first touch: the cookie that was already there before this
  // page wrote anything, else this document's landing (`beaconTouch`). Never the landing blindly.
  assert.match(tracker, /const firstTouch = storedFirstTouch\(\);\s*const inCookie = firstTouch\?\.visitorId \?\? null;/);
  assert.match(
    tracker,
    /const touch = beaconTouch\(\{ stored: firstTouch, visitorId: vid, landing \}\);\s*postSiteEvent\(siteEventBody\(\{ event: 'site\.visit', visitorId: vid, landing: touch, ownHost \}\)\);/,
  );
  assert.doesNotMatch(tracker, /landing, ownHost \}\)\)/, 'no beacon is built straight from the landing');
  // Page views and CTA clicks go through the one sender, which applies the same rule.
  const events = readFileSync(new URL('./site-events.ts', import.meta.url), 'utf8');
  assert.match(events, /const touch = beaconTouch\(\{ stored, visitorId, landing: env\.landing\(\) \}\);/);
  assert.match(events, /firstTouch: \(\) =>\s*typeof document === 'undefined' \? null : readFirstTouchCookie\(document\.cookie, window\.location\.hostname\),/);
  // The audience of a page is noted through the memory's own rule, never set directly.
  assert.match(tracker, /landingMemory\.notePage\(basePath\);/);

  // The landing is memory only: neither module that holds or reads it touches storage or cookies.
  for (const file of ['./attribution-landing.ts', '../components/analytics/landing.ts', '../components/analytics/useSignupHref.ts']) {
    const text = readFileSync(new URL(file, import.meta.url), 'utf8').replace(/^\s*\/\/.*$/gm, '');
    assert.doesNotMatch(text, /localStorage|sessionStorage|document\.cookie|indexedDB/, file);
  }
});

// ── One fact: the first-touch cookie and the visitor id ────────────────────────────────────────

const A = '11111111-1111-4111-8111-111111111111';
const B = '22222222-2222-4222-8222-222222222222';
const NEW = '33333333-3333-4333-8333-333333333333';

test('a readable first touch names the visitor: nothing is minted, localStorage follows the cookie', () => {
  assert.deepEqual(
    planIdentity({ firstTouchVisitorId: A, storedVisitorId: A, candidate: NEW, freshFirstTouchWritten: false }),
    { visitorId: A, renewed: false },
  );
  // localStorage was cleared, or holds another id: the cookie is what the account will be joined on.
  assert.deepEqual(
    planIdentity({ firstTouchVisitorId: A, storedVisitorId: null, candidate: NEW, freshFirstTouchWritten: false }),
    { visitorId: A, renewed: false },
  );
  assert.deepEqual(
    planIdentity({ firstTouchVisitorId: A, storedVisitorId: B, candidate: NEW, freshFirstTouchWritten: false }),
    { visitorId: A, renewed: false },
  );
});

test('a fresh first touch is a fresh visitor, so the cookie and the visitor row cannot name different links', () => {
  // The reported case: first seen through link-a, back after the cookie's 30 days through link-b
  // (or after signup removed the cookie). The old id keeps its link-a row; the new first touch,
  // link-b, gets its own id, and the account that follows is joined to that one.
  const plan = planIdentity({ firstTouchVisitorId: null, storedVisitorId: A, candidate: NEW, freshFirstTouchWritten: true });
  assert.deepEqual(plan, { visitorId: NEW, renewed: true });
  // A first-time visitor: a new id, and nothing was replaced.
  assert.deepEqual(
    planIdentity({ firstTouchVisitorId: null, storedVisitorId: null, candidate: NEW, freshFirstTouchWritten: true }),
    { visitorId: NEW, renewed: false },
  );
});

test('when the fresh cookie cannot be written the stored id is kept, not replaced on every page', () => {
  assert.deepEqual(
    planIdentity({ firstTouchVisitorId: null, storedVisitorId: A, candidate: NEW, freshFirstTouchWritten: false }),
    { visitorId: A, renewed: false },
  );
  assert.deepEqual(
    planIdentity({ firstTouchVisitorId: null, storedVisitorId: null, candidate: NEW, freshFirstTouchWritten: false }),
    { visitorId: NEW, renewed: false },
  );
});

test('a visitor id is a lowercase UUID or nothing', () => {
  assert.equal(sanitizeVisitorId(A), A);
  assert.equal(sanitizeVisitorId(` ${A.toUpperCase()} `), A);
  for (const bad of [null, undefined, '', 'v-1', `${A}x`, '<script>']) assert.equal(sanitizeVisitorId(bad), null);
});

// ── One first touch per visitor: what a beacon reports ─────────────────────────────────────────

const cookieFor = (touch: Parameters<typeof encodeFirstTouch>[0]) => `${FIRST_TOUCH_COOKIE}=${encodeFirstTouch(touch)}`;
const linkA = { source: 'newsletter', medium: 'email', campaign: 'october', content: 'link-a', landingPath: '/pricing' };
const linkB = { source: 'instagram', medium: 'social', campaign: 'november', content: 'link-b', landingPath: '/for/creators' };

test('an existing cookie for link A plus a landing on link B reports A', () => {
  // The cookie was written weeks ago (visit recording may have been off then, so the server may
  // hold no row for this visitor). Today's document landed through link B.
  const stored = readFirstTouchCookie(`rb_locale=it; ${cookieFor({ ...linkA, visitorId: A, ts: 1 })}; _ga=GA1.1.1`);
  assert.deepEqual(stored, { visitorId: A, touch: linkA });
  assert.deepEqual(beaconTouch({ stored, visitorId: A, landing: linkB }), linkA);
  // Whatever the stored touch says: one with no campaign at all is still the first touch.
  const direct = readFirstTouchCookie(cookieFor({ landingPath: '/', visitorId: A, ts: 1 }));
  assert.deepEqual(beaconTouch({ stored: direct, visitorId: A, landing: linkB }), { landingPath: '/' });
});

test('the landing is reported only when no first touch is stored for this visitor', () => {
  // This page started the first touch (the cookie did not exist when the page read it), or the
  // cookie could not be written at all.
  assert.deepEqual(beaconTouch({ stored: null, visitorId: NEW, landing: linkB }), linkB);
  assert.equal(beaconTouch({ stored: null, visitorId: NEW, landing: null }), null);
  // A cookie that names another visitor says nothing about this one.
  const stored = readFirstTouchCookie(cookieFor({ ...linkA, visitorId: A, ts: 1 }));
  assert.deepEqual(beaconTouch({ stored, visitorId: B, landing: linkB }), linkB);
  // The id is compared after normalising, as it is stored.
  assert.deepEqual(beaconTouch({ stored, visitorId: ` ${A.toUpperCase()} `, landing: linkB }), linkA);
});

test('a cookie is whatever the browser holds: fields are sanitized again, and an unreadable one is no cookie', () => {
  const b64 = (o: unknown) => Buffer.from(JSON.stringify(o)).toString('base64url');
  const dirty = `${FIRST_TOUCH_COOKIE}=${b64({ v: 1, s: 'News Letter', m: 'EMAIL', n: '<script>', r: 'app.reelbolt.ai', l: '/pricing?x=1', vid: A.toUpperCase(), ts: 1 })}`;
  assert.deepEqual(readFirstTouchCookie(dirty), { visitorId: A, touch: { medium: 'email', landingPath: '/pricing' } });
  // The page's own host is not a referrer either.
  const own = `${FIRST_TOUCH_COOKIE}=${b64({ v: 1, r: 'site.test', vid: A, ts: 1 })}`;
  assert.deepEqual(readFirstTouchCookie(own, 'site.test'), { visitorId: A, touch: {} });
  for (const bad of [
    null,
    undefined,
    '',
    '_ga=GA1.1.1',
    `${FIRST_TOUCH_COOKIE}=`,
    `${FIRST_TOUCH_COOKIE}=not-base64-json`,
    `${FIRST_TOUCH_COOKIE}=${b64({ v: 2, vid: A, ts: 1 })}`,
    `${FIRST_TOUCH_COOKIE}=${b64({ v: 1, vid: 'v-1', ts: 1 })}`,
    `x${FIRST_TOUCH_COOKIE}=${b64({ v: 1, vid: A, ts: 1 })}`,
  ]) {
    assert.equal(readFirstTouchCookie(bad), null, String(bad));
  }
});
