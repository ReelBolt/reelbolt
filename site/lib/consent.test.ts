import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import { CONSENT_STORAGE_KEY, readStoredConsent } from './consent.ts';

// Acceptance bullet 2: "the consent banner's declined state suppresses the analytics category". The
// gate is two parts — `lib/consent.ts` reads the stored choice, and `lib/gtag.ts` refuses to fire an
// event unless it reads `'granted'`. `gtag.ts` imports through the `@/` alias, which `node --test`
// does not resolve, so its half is pinned from source while `readStoredConsent` is exercised for
// real: together they show a declined visitor sends nothing.

const gtag = readFileSync(new URL('./gtag.ts', import.meta.url), 'utf8');
// The banner copy lives in the English catalogue (the Italian one is held to the same shape by
// catalog.test.ts).
const banner: string = (
  JSON.parse(readFileSync(new URL('../messages/en/consent.json', import.meta.url), 'utf8')) as { banner: string }
).banner;

type GtagWindow = { localStorage: { getItem: (key: string) => string | null } };

function installWindow(stored: string | null | (() => never)): void {
  const getItem =
    typeof stored === 'function'
      ? stored
      : (key: string) => (key === CONSENT_STORAGE_KEY ? stored : null);
  (globalThis as { window?: GtagWindow }).window = { localStorage: { getItem } };
}

test('a declined choice is read back as "denied"', () => {
  installWindow('denied');
  assert.equal(readStoredConsent(), 'denied');
});

test('an accepted choice is read back as "granted"', () => {
  installWindow('granted');
  assert.equal(readStoredConsent(), 'granted');
});

test('no choice, an unknown value, or unreadable storage reads as null — never as consent', () => {
  installWindow(null);
  assert.equal(readStoredConsent(), null);
  installWindow('yes');
  assert.equal(readStoredConsent(), null);
  installWindow(() => {
    throw new Error('storage disabled');
  });
  assert.equal(readStoredConsent(), null);
});

test('the event gate fires only on "granted", so a declined visitor sends no event', () => {
  // The early return is what makes declining suppress the analytics category: with `'denied'` (or
  // `null`), `trackEvent` returns before it can reach `window.gtag`.
  assert.match(gtag, /if \(readStoredConsent\(\) !== 'granted'\) return;/);
});

test('the consent banner says declining does not change what the visitor can do', () => {
  assert.match(banner, /declining changes nothing about what you can do here/i);
});

test('the consent banner says what runs before a choice: a visit signal with the IP address, no cookie', () => {
  const flat = banner.replace(/\s+/g, ' ');
  assert.match(flat, /receives a basic page-visit signal \(including your IP address\) but sets no cookie/);
  assert.match(flat, /if you decline, it receives nothing more/);
  assert.doesNotMatch(flat, /anonymous/i);
});

test('the banner says what Accept starts for our own measurement, in both languages', () => {
  // Accept is the consent the Privacy Policy rests the visit and sign-up source data on. Besides the
  // Google cookies it starts the first-touch cookie, the random visitor id, and the page, button
  // and sign-up records tied to that id (components/analytics/AttributionTracker.tsx,
  // lib/site-events.ts, and the app's sign-up page). The banner names each of them.
  assert.match(
    banner,
    /a cookie and a random visitor ID of our own that remember which link brought you here and record which pages you open, which sign-up buttons you press and whether you start to sign up/,
  );
  assert.match(banner, /They’re only set if you accept\./);
  const italian = (
    JSON.parse(readFileSync(new URL('../messages/it/consent.json', import.meta.url), 'utf8')) as { banner: string }
  ).banner;
  assert.match(
    italian,
    /un cookie e un ID visitatore casuale nostri che ricordano da quale link sei arrivato e registrano quali pagine apri, quali pulsanti di registrazione premi e se inizi a registrarti/,
  );
  assert.match(italian, /Vengono impostati solo se accetti\./);
  // The Italian draft stays flagged for review until counsel has read it.
  const flags = JSON.parse(readFileSync(new URL('../messages/it/_flags.json', import.meta.url), 'utf8')) as Record<string, string>;
  assert.match(flags['consent.banner'], /not yet read by counsel/);
});

// First-screen layout (phone/tablet): the bar is one line, the full wording sits behind Details,
// and Reject and Accept keep identical size classes. Source-read because the runner cannot load .tsx.
test('the cookie banner collapses behind Details and keeps Reject and Accept equal', () => {
  const src = readFileSync(new URL('../components/consent/CookieBanner.tsx', import.meta.url), 'utf8');
  assert.match(src, /aria-expanded=\{open\}/);
  assert.match(src, /hidden=\{!open\}/);
  assert.match(src, /t\.rich\('banner'/);
  // First layer: purposes, privacy link, Details, Reject and Accept side by side.
  assert.match(src, /t\('summary'\)/);
  assert.match(src, /t\('privacyLink'\)/);
  assert.ok(src.indexOf('onClick={reject}') < src.indexOf('onClick={accept}'));
  assert.match(src, /min-h-11/);
  assert.match(src, /!px-3 sm:!px-5 \$\{buttonClass\(\{ variant: 'outline' \}\)\}/);
  assert.match(src, /!px-3 sm:!px-5 \$\{buttonClass\(\{ variant: 'accent' \}\)\}/);
});
