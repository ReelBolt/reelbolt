import { test } from 'node:test';
import assert from 'node:assert/strict';

import { validateContact } from './validation.ts';

const valid = {
  name: 'Ada Lovelace',
  email: 'ada@example.com',
  message: 'I would like a demo of the video templates for my team.',
};

test('an enquiry is valid without the newsletter opt-in', () => {
  assert.deepEqual(validateContact(valid), []);
});

test('marketingOptIn accepts true and false', () => {
  assert.deepEqual(validateContact({ ...valid, marketingOptIn: true }), []);
  assert.deepEqual(validateContact({ ...valid, marketingOptIn: false }), []);
});

test('a non-boolean marketingOptIn is rejected', () => {
  const errors = validateContact({ ...valid, marketingOptIn: 'yes' as unknown as boolean });
  assert.deepEqual(errors, [{ field: 'marketingOptIn', message: 'Invalid submission.' }]);
});

// WEB-7: a field of the wrong JSON type is a validation error (400), never a thrown `.trim()` (500).
for (const field of ['name', 'email', 'company', 'message', 'website'] as const) {
  for (const bad of [42, true, {}, ['x']]) {
    test(`a non-string ${field} (${JSON.stringify(bad)}) is rejected without throwing`, () => {
      const errors = validateContact({ ...valid, [field]: bad as unknown as string });
      assert.deepEqual(errors, [{ field, message: 'Invalid submission.' }]);
    });
  }
}

test('null optional text fields still read as absent', () => {
  const input = { ...valid, company: null, website: null } as unknown as Parameters<typeof validateContact>[0];
  assert.deepEqual(validateContact(input), []);
});
