import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import {
  GOOGLE_API_SERVICES_USER_DATA_POLICY_URL,
  GOOGLE_DRIVE_READONLY_SCOPE,
  GOOGLE_LIMITED_USE_STATEMENT,
  GOOGLE_NO_MODEL_TRAINING_STATEMENT,
} from './google-user-data.ts';

const privacyPage = readFileSync(new URL('../app/(en)/legal/privacy/page.tsx', import.meta.url), 'utf8');

test('the Limited Use sentence is verbatim, with an ASCII apostrophe and a trailing period', () => {
  assert.equal(
    GOOGLE_LIMITED_USE_STATEMENT,
    "ReelBolt's use and transfer of information received from Google APIs adheres to the Google " +
      'API Services User Data Policy, including the Limited Use requirements.',
  );
  assert.doesNotMatch(GOOGLE_LIMITED_USE_STATEMENT, /[‘’“”]/);
});

test('the Limited Use link and the Drive scope are the exact Google strings', () => {
  assert.equal(
    GOOGLE_API_SERVICES_USER_DATA_POLICY_URL,
    'https://developers.google.com/terms/api-services-user-data-policy',
  );
  assert.equal(GOOGLE_DRIVE_READONLY_SCOPE, 'https://www.googleapis.com/auth/drive.readonly');
});

test('the no-AI/ML-training affirmation is a negative statement about Google Workspace APIs', () => {
  // Pin the sentence Google's checklist requires, whitespace-tolerant so re-wrapping cannot break it.
  assert.match(GOOGLE_NO_MODEL_TRAINING_STATEMENT, /Google\s+Workspace\s+APIs/);
  assert.match(GOOGLE_NO_MODEL_TRAINING_STATEMENT, /develop,\s+improve\s+or\s+train/);
  // It states what we do not do, and must never read as a positive claim about using the data.
  assert.match(GOOGLE_NO_MODEL_TRAINING_STATEMENT, /^We do not use\b/);
  assert.doesNotMatch(GOOGLE_NO_MODEL_TRAINING_STATEMENT, /[‘’“”]/);
  for (const positive of [
    /\bwe use\b/i,
    /\bwe may use\b/i,
    /\bwe train\b/i,
    /\bwe sell\b/i,
    /\bwe share\b/i,
    /\badvertis/i,
    /\bdata broker/i,
    /\bcredit[- ]worth/i,
    /\bretarget/i,
  ]) {
    assert.doesNotMatch(
      GOOGLE_NO_MODEL_TRAINING_STATEMENT,
      positive,
      `the affirmation must not be a positive claim (${positive})`,
    );
  }
});

test('the privacy policy renders a Google section from the module', () => {
  assert.match(privacyPage, /from '@\/lib\/google-user-data'/);
  assert.match(privacyPage, /\{GOOGLE_LIMITED_USE_STATEMENT\}/);
  assert.match(privacyPage, /\{GOOGLE_NO_MODEL_TRAINING_STATEMENT\}/);
  assert.match(privacyPage, /\{GOOGLE_API_SERVICES_USER_DATA_POLICY_URL\}/);
  assert.match(privacyPage, /\{GOOGLE_DRIVE_READONLY_SCOPE\}/);
  // The heading names Google, so a reviewer searching the page finds the section.
  assert.match(privacyPage, /<h2 className=\{h2\}>7\. Google user data<\/h2>/);
});

test('the privacy policy describes sign-in, Drive sync, retention and revocation', () => {
  for (const phrase of [
    /sign in with Google/i,
    /read-only/i,
    /one-way copy/i,
    /never modified or deleted/i,
    /disconnect/i,
    /revoke/i,
  ]) {
    assert.match(privacyPage, phrase, `the Google section must mention ${phrase}`);
  }
});

test('every "Section N" cross-reference names a section that exists', () => {
  const headings = new Set(
    [...privacyPage.matchAll(/<h2 className=\{h2\}>(\d+)\./g)].map((match) => match[1]),
  );
  assert.ok(headings.size >= 14, 'the policy should have at least 14 numbered sections');
  const refs = [...privacyPage.matchAll(/Section (\d+)/g)].map((match) => match[1]);
  assert.ok(refs.length > 0, 'the policy should cross-reference its sections');
  for (const ref of refs) {
    assert.ok(headings.has(ref), `Section ${ref} is referenced but no such section exists`);
  }
});

test('the renumbering moved every reference to a moved section', () => {
  // Inserting Section 7 pushed two sections the policy names by number: "Your rights" from 10 to 11
  // and "How to contact us" from 13 to 14. Every sentence that named them must say the new number.
  assert.match(privacyPage, /as Section 11 explains/);
  assert.match(privacyPage, /sooner under Section 11/);
  assert.match(privacyPage, /details in Section 14/);
  assert.doesNotMatch(privacyPage, /Section 13/);
  // The headings themselves carry the new numbers.
  assert.match(privacyPage, /<h2 className=\{h2\}>7\. Google user data<\/h2>/);
  assert.match(privacyPage, /<h2 className=\{h2\}>10\. Retention<\/h2>/);
  assert.match(privacyPage, /<h2 className=\{h2\}>11\. Your rights<\/h2>/);
  assert.match(privacyPage, /<h2 className=\{h2\}>14\. How to contact us<\/h2>/);
});
