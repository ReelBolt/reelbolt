import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

import { CONSENT_STORAGE_KEY } from './consent.ts';
import {
  disableFlagName,
  gaInitScript,
  installHitGuard,
  isMeasurementId,
  GA_CONSENT_DEFAULT,
} from './ga-consent.ts';

// The init script is the thing that must put the denied default ahead of `config`. These tests run
// the exact string the page ships in a bare VM context and read back the queue it builds, so the
// order is pinned by behaviour, not by a regex over the source.

type Call = unknown[];

function run(stored: string | null | 'throws', gaId = 'G-TEST1234AB'): Call[] {
  const localStorage = {
    getItem(key: string): string | null {
      if (stored === 'throws') throw new Error('storage disabled');
      return key === CONSENT_STORAGE_KEY ? stored : null;
    },
  };
  const window: Record<string, unknown> = { localStorage };
  const context = vm.createContext({ window, Date });
  vm.runInContext(gaInitScript(gaId), context);
  const queue = window.dataLayer as ArrayLike<unknown>[];
  // Copy into this realm's arrays: the VM's own Array prototype would fail deepStrictEqual.
  // JSON round trip also leaves the VM's plain objects behind.
  return Array.from(queue, (entry) => JSON.parse(JSON.stringify(Array.from(entry))) as Call);
}

test('with no stored choice, consent default (all denied) is queued before config', () => {
  const calls = run(null);
  assert.deepEqual(
    calls.map((c) => c[0]),
    ['consent', 'js', 'config'],
  );
  assert.deepEqual(calls[0], [
    'consent',
    'default',
    {
      ad_storage: 'denied',
      ad_user_data: 'denied',
      ad_personalization: 'denied',
      analytics_storage: 'denied',
      wait_for_update: 500,
    },
  ]);
  assert.equal(calls[2][1], 'G-TEST1234AB');
  // The site runs no ads: Google signals and ad personalization are off in the config itself.
  assert.deepEqual(calls[2][2], {
    send_page_view: true,
    allow_google_signals: false,
    allow_ad_personalization_signals: false,
  });
});

test('a stored rejection or unreadable storage stays denied: no update is queued', () => {
  for (const stored of ['denied', 'yes', 'throws'] as const) {
    const calls = run(stored);
    assert.deepEqual(
      calls.map((c) => (c[0] === 'js' ? 'js' : `${c[0]}:${c[1]}`)),
      ['consent:default', 'js', 'config:G-TEST1234AB'],
      String(stored),
    );
  }
});

test('a stored acceptance grants analytics storage only, still ahead of config', () => {
  const calls = run('granted');
  assert.deepEqual(
    calls.map((c) => c[0]),
    ['consent', 'consent', 'js', 'config'],
  );
  assert.deepEqual(calls[0].slice(0, 2), ['consent', 'default']);
  assert.deepEqual(calls[1], ['consent', 'update', { analytics_storage: 'granted' }]);
});

test('the default never grants ads purposes and no update does either', () => {
  assert.equal(GA_CONSENT_DEFAULT.ad_storage, 'denied');
  assert.equal(GA_CONSENT_DEFAULT.ad_user_data, 'denied');
  assert.equal(GA_CONSENT_DEFAULT.ad_personalization, 'denied');
  const source = readFileSync(new URL('./ga-consent.ts', import.meta.url), 'utf8');
  assert.doesNotMatch(source, /ad_(storage|user_data|personalization)['"]?:\s*['"]granted/);
});

test('only a GA4 measurement ID produces a tag; empty or odd values do not', () => {
  assert.equal(isMeasurementId('G-NZBSB8BZPG'), true);
  for (const bad of ['', undefined, 'UA-1234-1', "G-X'); alert(1);//", 'G-', 'g-abcd1234']) {
    assert.equal(isMeasurementId(bad), false, String(bad));
  }
  assert.throws(() => gaInitScript(''));
  assert.throws(() => gaInitScript("G-X'); alert(1);//"));
});

test('the component loads the tag unless a rejection is on record, and never grants ads', () => {
  const component = readFileSync(
    new URL('../components/analytics/GoogleAnalytics.tsx', import.meta.url),
    'utf8',
  );
  // Undecided and accepted visitors get the tag; a rejection (stored, or made on this page) does not.
  assert.match(component, /if \(!rejected\.current\) loadTag\(gaId\);/);
  assert.match(component, /if \(choice === 'denied'\) rejected\.current = true;/);
  assert.match(component, /if \(choice === 'granted'\) rejected\.current = false;/);
  // The first render's server `null` is resolved against localStorage, not trusted.
  assert.match(component, /consent \?\? readStoredConsent\(\)/);
  // A rejection or withdrawal arms Google's opt-out flag; an acceptance clears it.
  assert.match(component, /disableFlagName\(gaId\)\] = true/);
  assert.match(component, /disableFlagName\(gaId\)\] = false/);
  assert.match(component, /'consent', 'update'/);
  assert.doesNotMatch(component, /ad_(storage|user_data|personalization)/);
});

test('the opt-out flag is Google\'s own per-property name', () => {
  assert.equal(disableFlagName('G-TEST1234AB'), 'ga-disable-G-TEST1234AB');
});

test('the hit guard drops Google measurement requests only while the opt-out flag is true', async () => {
  const sent: string[] = [];
  class FakeResponse {
    init: { status: number };
    constructor(_body: null, init: { status: number }) {
      this.init = init;
    }
  }
  const win: Record<string, unknown> = {
    location: { href: 'https://reelbolt.ai/pricing' },
    Response: FakeResponse,
    fetch: async (input: unknown) => {
      sent.push(`fetch:${input}`);
      return 'real';
    },
    navigator: {
      sendBeacon: (url: unknown) => {
        sent.push(`beacon:${url}`);
        return true;
      },
    },
  };
  const hit = 'https://region1.google-analytics.com/g/collect?v=2';
  const other = 'https://reelbolt.ai/api/contact';
  const nav = win.navigator as { sendBeacon: (url: string) => boolean };
  const fetchWrapped = () => win.fetch as (input: string) => Promise<unknown>;

  installHitGuard(win as Parameters<typeof installHitGuard>[0], 'G-TEST1234AB');

  // Flag unset or false: everything passes through.
  await fetchWrapped()(hit);
  nav.sendBeacon(hit);
  win['ga-disable-G-TEST1234AB'] = false;
  await fetchWrapped()(hit);
  assert.deepEqual(sent, [`fetch:${hit}`, `beacon:${hit}`, `fetch:${hit}`]);

  // Flag true: Google hits are swallowed (fetch answers 204), other requests still go out.
  win['ga-disable-G-TEST1234AB'] = true;
  sent.length = 0;
  const swallowed = (await fetchWrapped()(hit)) as FakeResponse;
  assert.equal(swallowed.init.status, 204);
  assert.equal(nav.sendBeacon(hit), true);
  assert.deepEqual(sent, []);
  await fetchWrapped()(other);
  assert.deepEqual(sent, [`fetch:${other}`]);
});
