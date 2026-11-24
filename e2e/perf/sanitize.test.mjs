// Run with: node --test e2e/perf/sanitize.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { redactUrl, sanitizeHar, assertSafeOutDir } from './sanitize.mjs';

const SECRET = 'super-secret-value';

test('redactUrl removes an output-link capability path and presigned parameters', () => {
  const absolute = 'https://reelbolt.ai/api/v1/output-links/abc123?X-Amz-Signature=deadbeef&x=1';
  const redacted = redactUrl(absolute);
  assert.ok(!redacted.includes('abc123'), redacted);
  assert.ok(redacted.includes('/api/v1/output-links/{token}'), redacted);
  assert.ok(!redacted.includes('deadbeef'), redacted);

  const relative = '/api/v1/output-links/abc123?token=deadbeef';
  const redactedRelative = redactUrl(relative);
  assert.equal(redactedRelative, '/api/v1/output-links/{token}?token=REDACTED');
  assert.ok(redactedRelative.startsWith('/api/v1/'), 'a relative URL must stay relative');
});

test('a sanitized HAR keeps none of the credentials, capability paths or bodies', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'perf-sanitize-'));
  const file = path.join(dir, 'run-1.har');
  const har = {
    log: {
      entries: [
        {
          request: {
            url: 'https://reelbolt.ai/api/v1/output-links/abc123',
            headers: [
              { name: 'Cookie', value: `reelbolt_token=${SECRET}` },
              { name: 'Authorization', value: `Bearer ${SECRET}` },
              { name: 'Accept', value: 'application/json' },
            ],
            cookies: [{ name: 'reelbolt_token', value: SECRET }],
            postData: { text: JSON.stringify({ password: SECRET }) },
            queryString: [{ name: 'X-Amz-Signature', value: SECRET }],
          },
          response: {
            status: 200,
            headers: [
              { name: 'Set-Cookie', value: `reelbolt_token=${SECRET}` },
              { name: 'X-Amz-Credential', value: SECRET },
              { name: 'Content-Type', value: 'video/mp4' },
            ],
            cookies: [{ name: 'reelbolt_token', value: SECRET }],
            redirectURL: 'https://reelbolt.ai/api/v1/output-links/abc123',
            content: { text: SECRET },
          },
        },
      ],
    },
  };
  fs.writeFileSync(file, JSON.stringify(har), { mode: 0o600 });

  sanitizeHar(file);

  const raw = fs.readFileSync(file, 'utf8');
  assert.ok(!raw.includes(SECRET), 'no secret may survive sanitisation');
  assert.ok(!raw.includes('abc123'), 'no output-link token may survive sanitisation');
  const parsed = JSON.parse(raw);
  const entry = parsed.log.entries[0];
  assert.deepEqual(entry.request.headers.map((h) => h.name), ['Accept']);
  assert.deepEqual(entry.response.headers.map((h) => h.name), ['Content-Type']);
  assert.equal(entry.request.postData, undefined);
  assert.equal(entry.request.cookies, undefined);
  assert.equal(entry.response.cookies, undefined);
  assert.ok(entry.request.url.endsWith('/api/v1/output-links/{token}'));
  assert.ok(entry.response.redirectURL.endsWith('/api/v1/output-links/{token}'));
  assert.equal(entry.request.queryString[0].value, 'REDACTED');
});

test('sanitizeHar deletes the HAR rather than leaving it unsanitized', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'perf-sanitize-bad-'));
  const file = path.join(dir, 'broken.har');
  fs.writeFileSync(file, '{not json');
  assert.throws(() => sanitizeHar(file));
  assert.equal(fs.existsSync(file), false, 'an unparseable HAR must not stay on disk');
});

test('assertSafeOutDir refuses the repository and a symlink into it', () => {
  const repo = fs.mkdtempSync(path.join(os.tmpdir(), 'perf-repo-'));
  assert.throws(() => assertSafeOutDir(path.join(repo, 'scratch'), repo));
  assert.throws(() => assertSafeOutDir(repo, repo));

  const outside = fs.mkdtempSync(path.join(os.tmpdir(), 'perf-outside-'));
  assert.doesNotThrow(() => assertSafeOutDir(path.join(outside, 'run'), repo));

  const link = path.join(outside, 'link-to-repo');
  fs.symlinkSync(repo, link);
  assert.throws(() => assertSafeOutDir(path.join(link, 'run'), repo), /repository/);
});
