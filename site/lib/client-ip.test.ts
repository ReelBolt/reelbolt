import { test } from 'node:test';
import assert from 'node:assert/strict';

import { clientIpKey } from './client-ip.ts';

const req = (headers: Record<string, string>) =>
  new Request('http://site.test/api/contact', { method: 'POST', headers });

test('X-Real-IP is preferred over X-Forwarded-For', () => {
  assert.equal(
    clientIpKey(req({ 'x-real-ip': '203.0.113.7', 'x-forwarded-for': '198.51.100.1, 203.0.113.7' })),
    '203.0.113.7',
  );
});

test('a spoofed leading X-Forwarded-For entry is ignored when X-Real-IP is set', () => {
  assert.equal(clientIpKey(req({ 'x-real-ip': '203.0.113.7', 'x-forwarded-for': '1.2.3.4' })), '203.0.113.7');
});

test('without X-Real-IP the rightmost X-Forwarded-For entry is used, never the first', () => {
  assert.equal(clientIpKey(req({ 'x-forwarded-for': '1.2.3.4, 5.6.7.8,  203.0.113.7 ' })), '203.0.113.7');
});

test('rotating the spoofed leading entries does not change the key', () => {
  const a = clientIpKey(req({ 'x-forwarded-for': '10.0.0.1, 203.0.113.7' }));
  const b = clientIpKey(req({ 'x-forwarded-for': '10.0.0.2, 203.0.113.7' }));
  assert.equal(a, b);
});

test('blank headers fall back to a shared key', () => {
  assert.equal(clientIpKey(req({ 'x-real-ip': '  ', 'x-forwarded-for': ' , ' })), 'unknown');
  assert.equal(clientIpKey(req({})), 'unknown');
});
