import { test } from 'node:test';
import assert from 'node:assert/strict';

import { readContactBody, MALFORMED_CONTACT_BODY } from './contact-body.ts';
import { validateContact } from './validation.ts';

// A request whose body is not valid JSON: request.json() rejects, exactly as a real
// malformed POST does.
function throwingRequest(): { json(): Promise<unknown> } {
  return { json: async () => { throw new SyntaxError('Unexpected token < in JSON'); } };
}

test('a malformed body answers 400 with the validator-shaped error body', async () => {
  const body = await readContactBody(throwingRequest());
  assert.equal(body.ok, false);
  if (body.ok) return; // narrow for TS
  assert.equal(body.status, 400);
  assert.deepEqual(body.response, { ok: false, errors: [{ field: '_form', message: 'Invalid request.' }] });
});

test('the malformed answer has the same shape a validator failure uses', async () => {
  const body = await readContactBody(throwingRequest());
  assert.equal(body.ok, false);
  if (body.ok) return;
  const invalid = validateContact({ name: '', email: '' }); // a genuinely invalid body
  assert.deepEqual(Object.keys(body.response).sort(), ['errors', 'ok']);
  assert.deepEqual(Object.keys(body.response.errors[0]).sort(), Object.keys(invalid[0]).sort());
});

test('a well-formed body is handed back as the payload', async () => {
  const request = { json: async () => ({ name: 'Ada Lovelace', email: 'ada@example.com' }) };
  const body = await readContactBody(request);
  assert.deepEqual(body, { ok: true, payload: { name: 'Ada Lovelace', email: 'ada@example.com' } });
});

// A contact body is a JSON object. Valid JSON that is not one must not reach the handler (which
// reads fields off it): it is answered like any other malformed body, so a client error never
// becomes a 500 or a bogus success.
const nonObjectBodies: [string, unknown][] = [
  ['null', null],
  ['a bare string', 'not an object'],
  ['an array', ['not', 'an', 'object']],
];

for (const [label, value] of nonObjectBodies) {
  test(`a body that is JSON ${label} is treated as malformed (400)`, async () => {
    const body = await readContactBody({ json: async () => value });
    assert.equal(body.ok, false);
    if (body.ok) return;
    assert.equal(body.status, 400);
    assert.deepEqual(body.response, MALFORMED_CONTACT_BODY);
  });
}
