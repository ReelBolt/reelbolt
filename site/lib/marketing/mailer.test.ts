import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  CLOUDFLARE_ERROR_BODY_LIMIT,
  CLOUDFLARE_SEND_TIMEOUT_MS,
  CONTACT_RECIPIENT,
  contactMail,
  contactSubject,
  mailConfigured,
  sendEndpoint,
  sendMail,
} from './mailer.ts';

const env = {
  CLOUDFLARE_EMAIL_ACCOUNT_ID: 'acct-123',
  CLOUDFLARE_EMAIL_TOKEN: 'tok-secret',
  SMTP_FROM: 'welcome@reelbolt.ai',
};

interface Call {
  url: string;
  init: RequestInit;
}

function recordingFetch(respond: (call: Call) => Promise<Response>) {
  const calls: Call[] = [];
  const fn = (async (url: string | URL | Request, init?: RequestInit) => {
    const call = { url: String(url), init: init ?? {} };
    calls.push(call);
    return respond(call);
  }) as typeof fetch;
  return { fn, calls };
}

test('configured only when the token, the account and a From address are all present', () => {
  assert.equal(mailConfigured(env), true);
  assert.equal(mailConfigured({ ...env, CLOUDFLARE_EMAIL_TOKEN: '' }), false);
  assert.equal(mailConfigured({ ...env, CLOUDFLARE_EMAIL_ACCOUNT_ID: '  ' }), false);
  assert.equal(mailConfigured({ ...env, SMTP_FROM: undefined }), false);
});

test('the send endpoint mirrors the Go mailer, and the test base overrides it', () => {
  assert.equal(sendEndpoint(env), 'https://api.cloudflare.com/client/v4/accounts/acct-123/email/sending/send');
  assert.equal(
    sendEndpoint({ ...env, CLOUDFLARE_API_BASE_URL: 'http://127.0.0.1:9999/' }),
    'http://127.0.0.1:9999/client/v4/accounts/acct-123/email/sending/send',
  );
});

test('sendMail POSTs HTML and text as separate JSON fields, never a MIME message', async () => {
  const { fn, calls } = recordingFetch(async () => new Response('', { status: 202 }));
  await sendMail(
    { to: CONTACT_RECIPIENT, from: env.SMTP_FROM, subject: 'Hi', html: '<p>a</p>', text: 'a' },
    env,
    { fetch: fn },
  );

  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, sendEndpoint(env));
  assert.equal(calls[0].init.method, 'POST');
  const headers = calls[0].init.headers as Record<string, string>;
  assert.equal(headers.authorization, 'Bearer tok-secret');
  assert.equal(headers['content-type'], 'application/json');
  const body = JSON.parse(String(calls[0].init.body));
  assert.deepEqual(Object.keys(body).sort(), ['from', 'html', 'subject', 'text', 'to']);
  assert.equal(body.html, '<p>a</p>');
  assert.equal(body.text, 'a');
  // The 15 s request timeout is armed on the fetch (mirrors cloudflareTimeout).
  assert.ok(calls[0].init.signal instanceof AbortSignal);
  assert.equal(CLOUDFLARE_SEND_TIMEOUT_MS, 15_000);
});

test('an unconfigured mailer refuses instead of accepting the enquiry', async () => {
  const { fn, calls } = recordingFetch(async () => new Response('', { status: 202 }));
  await assert.rejects(
    () => sendMail({ to: 'x', from: 'y', subject: 's', html: 'h', text: 't' }, { ...env, CLOUDFLARE_EMAIL_TOKEN: '' }, { fetch: fn }),
    /not configured/,
  );
  assert.equal(calls.length, 0);
});

test('a non-2xx throws and logs a bounded, address-redacted reason', async () => {
  const huge = 'sender not verified contact@visitor.example ' + 'x'.repeat(CLOUDFLARE_ERROR_BODY_LIMIT * 2);
  const { fn } = recordingFetch(async () => new Response(huge, { status: 403 }));
  const logged: string[] = [];
  await assert.rejects(
    () =>
      sendMail(
        { to: CONTACT_RECIPIENT, from: env.SMTP_FROM, subject: 's', html: 'h', text: 't' },
        env,
        { fetch: fn, log: (msg, detail) => logged.push(`${msg} ${String(detail ?? '')}`) },
      ),
    /cloudflare email send failed: 403/,
  );
  assert.equal(logged.length, 1);
  assert.ok(logged[0].includes('403'));
  assert.ok(!logged[0].includes('contact@visitor.example'), 'the echoed address is redacted');
  assert.ok(logged[0].length <= CLOUDFLARE_ERROR_BODY_LIMIT + 64, 'the logged body is bounded');
});

test('contactMail addresses the enquiry and escapes the visitor text', () => {
  const mail = contactMail(
    { name: 'Ada <script>', email: 'ada@example.com', company: 'Acme', topic: 'billing', message: 'Line one' },
    env,
  );
  assert.equal(mail.to, CONTACT_RECIPIENT);
  assert.equal(mail.from, 'welcome@reelbolt.ai');
  assert.equal(contactSubject({ name: 'Ada', email: 'a@b.c', topic: 'billing' }), '[ReelBolt contact] Billing — Ada');
  assert.ok(mail.text.includes('Email: ada@example.com'));
  assert.ok(mail.text.includes('Line one'));
  assert.ok(!mail.html.includes('<script>'), 'visitor text is escaped in the html body');
  assert.ok(mail.html.includes('&lt;script&gt;'));
});
