import { test } from 'node:test';
import assert from 'node:assert/strict';

import { HOME_FAQS, PRICING_FAQS } from './faqs.ts';

// The FAQ arrays feed three places at once: the visible `<dl>` on the page, the FAQPage JSON-LD
// emitted from the same array, and the plain-text blocks in `app/llms.txt`. Google treats FAQ
// markup whose answers are not visible on the page as a violation, so the arrays are the single
// source and these tests guard the two rules the copy is written under:
//
//   1. no pricing answer carries a figure — every price, seat minimum and credit amount a reader
//      sees is derived from `lib/pricing` at render time, so a number typed in here would be a
//      second source that drifts the moment the catalog changes;
//   2. nothing describes the desktop runner as available — area E's E4 onward (pairing, daemon,
//      installers) is unstarted, so there is nothing a customer can download today.

const ALL = [...HOME_FAQS, ...PRICING_FAQS];

test('every entry is a filled-in question and answer', () => {
  for (const faq of ALL) {
    assert.equal(typeof faq.question, 'string');
    assert.equal(typeof faq.answer, 'string');
    assert.ok(faq.question.trim().length > 0, 'empty question');
    assert.ok(faq.answer.trim().length > 0, `empty answer for "${faq.question}"`);
    assert.ok(faq.question.endsWith('?'), `not a question: "${faq.question}"`);
    // The answer is injected into JSON-LD with `JSON.stringify` and into llms.txt verbatim, so it
    // has to stay plain text rather than markup.
    assert.doesNotMatch(faq.answer, /<[a-z/]/, `markup in an answer: "${faq.question}"`);
  }
});

test('no question appears twice', () => {
  const questions = ALL.map((faq) => faq.question);
  assert.equal(new Set(questions).size, questions.length);
});

test('the pricing answers carry no figures that could drift from the catalog', () => {
  for (const faq of PRICING_FAQS) {
    assert.doesNotMatch(faq.answer, /\d/, `a number in "${faq.question}"`);
  }
});

test('the runner answer does not claim the runner is available', () => {
  const runner = PRICING_FAQS.find((faq) => /render on my own computer/i.test(faq.question));
  assert.ok(runner, 'the "render on my own computer" question is missing');
  assert.doesNotMatch(runner.answer, /^\s*Yes\b/, 'the runner answer promises the runner works today');
  assert.match(runner.answer, /has not been released/);
});

test('the data answer covers the hosted service as well as self-hosting', () => {
  const data = HOME_FAQS.find((faq) => /where does my data live/i.test(faq.question));
  assert.ok(data, 'the "where does my data live" question is missing');
  // The bug this guards: the answer used to describe self-hosting only, which left a customer on
  // the hosted service with no answer at all.
  assert.match(data.answer, /hosted service/i);
  assert.match(data.answer, /your own infrastructure/i);
});
