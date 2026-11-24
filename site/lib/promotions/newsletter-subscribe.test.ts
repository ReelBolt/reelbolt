// Tests for the footer newsletter request handling in lib/marketing/subscribe.ts. The handler takes an
// injected forward function, so no server or network is involved.
import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  handleFooterSubscribe,
  validateFooterSubscribe,
  type ForwardSubscribeFn,
} from '../marketing/subscribe.ts';
import { MARKETING_WORDING } from '../marketing/wording.ts';
import { MIN_FILL_TIME_MS } from '../bot-checks.ts';

const NOW = Date.parse('2026-10-17T12:00:00.000Z');

function validBody(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    email: '  Visitor@Example.com ',
    consent: true,
    wordingVersion: MARKETING_WORDING.version,
    startedAt: NOW - 10 * MIN_FILL_TIME_MS,
    website: '',
    ...overrides,
  };
}

function recordingForward() {
  const calls: { email: string; source: string; apiUrl: string; siteToken?: string; clientIp?: string }[] = [];
  const versions: (string | undefined)[] = [];
  const forward: ForwardSubscribeFn = async (email, source, apiUrl, deps) => {
    calls.push({ email, source, apiUrl, siteToken: deps?.siteToken, clientIp: deps?.clientIp });
    versions.push(deps?.wordingVersion);
  };
  return { forward, calls, versions };
}

test('a filled honeypot is rejected with 400 and nothing is forwarded', () => {
  const { forward, calls } = recordingForward();
  const outcome = handleFooterSubscribe(
    { body: validBody({ website: 'http://spam.example' }), now: NOW, wordingVersion: MARKETING_WORDING.version, apiUrl: 'http://api' },
    forward,
  );
  assert.equal(outcome.status, 400);
  assert.equal(outcome.forwarded, null);
  assert.equal(calls.length, 0);
});

test('a form submitted too fast is rejected with 400', () => {
  const check = validateFooterSubscribe(validBody({ startedAt: NOW - 500 }), NOW, MARKETING_WORDING.version);
  assert.deepEqual(check, { ok: false, status: 400, error: 'rejected' });
});

test('an unticked consent box is rejected with 400 and nothing is forwarded', () => {
  const { forward, calls } = recordingForward();
  const outcome = handleFooterSubscribe(
    { body: validBody({ consent: false }), now: NOW, wordingVersion: MARKETING_WORDING.version, apiUrl: 'http://api' },
    forward,
  );
  assert.equal(outcome.status, 400);
  assert.deepEqual(outcome.payload, { ok: false, error: 'consent_required' });
  assert.equal(calls.length, 0);
});

test('a stale wording version is rejected with 400', () => {
  assert.deepEqual(validateFooterSubscribe(validBody({ wordingVersion: '2025-01-old' }), NOW, MARKETING_WORDING.version), {
    ok: false,
    status: 400,
    error: 'stale_wording',
  });
});

test('a malformed email is rejected with 400', () => {
  for (const email of ['', 'not-an-email', 'a@b', 42, 'x'.repeat(250) + '@example.com']) {
    assert.deepEqual(validateFooterSubscribe(validBody({ email }), NOW, MARKETING_WORDING.version), {
      ok: false,
      status: 400,
      error: 'invalid_email',
    });
  }
});

test('a non-object body is rejected with 400', () => {
  for (const body of [null, 'text', 7, undefined]) {
    assert.deepEqual(validateFooterSubscribe(body, NOW, MARKETING_WORDING.version), { ok: false, status: 400, error: 'invalid_request' });
  }
});

test('a valid request is answered 202 checkEmail and forwards with source siteFooter', async () => {
  const { forward, calls, versions } = recordingForward();
  const outcome = handleFooterSubscribe(
    { body: validBody(), now: NOW, wordingVersion: MARKETING_WORDING.version, apiUrl: 'http://api:8080', siteToken: 'tok', clientIp: '203.0.113.9' },
    forward,
  );
  assert.equal(outcome.status, 202);
  assert.deepEqual(outcome.payload, { status: 'checkEmail' });
  assert.ok(outcome.forwarded instanceof Promise, 'the forward call is handed back for after()/waitUntil');
  await outcome.forwarded;

  assert.deepEqual(calls, [
    {
      email: 'Visitor@Example.com',
      source: 'siteFooter',
      apiUrl: 'http://api:8080',
      siteToken: 'tok',
      clientIp: '203.0.113.9',
    },
  ]);
  assert.deepEqual(versions, [MARKETING_WORDING.version], 'the injected wording version is forwarded');
});

test('a valid request with no API URL is still 202 and forwards nothing', () => {
  const { forward, calls } = recordingForward();
  const outcome = handleFooterSubscribe({ body: validBody(), now: NOW, wordingVersion: MARKETING_WORDING.version, apiUrl: undefined }, forward);
  assert.equal(outcome.status, 202);
  assert.deepEqual(outcome.payload, { status: 'checkEmail' });
  assert.equal(outcome.forwarded, null);
  assert.equal(calls.length, 0);
});
