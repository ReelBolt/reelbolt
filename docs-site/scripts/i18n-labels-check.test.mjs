import test from 'node:test';
import assert from 'node:assert/strict';
import { boldLabels, norm, flatten, index, check } from './i18n-labels-check.mjs';

test('boldLabels skips code fences', () => {
  assert.deepEqual(boldLabels('a **One** b\n```\n**no**\n```\n**Two**'), ['One', 'Two']);
});
test('norm drops placeholders, case and trailing colon', () => {
  assert.equal(norm('Salva {n}:'), 'salva');
});
test('flatten builds dotted keys', () => {
  assert.deepEqual(flatten({ a: { b: 'x' } }, 'ns'), { 'ns.a.b': 'x' });
});
test('check proposes the Italian value for the English label', () => {
  const en = { 'n.k': 'Editing room' }; const it = { 'n.k': 'Stanza di montaggio' };
  const ctx = { it, enIdx: index(en), itIdx: index(it) };
  const [r] = check('**Editing room**', '**Room di montaggio**', ctx);
  assert.equal(r.ok, false);
  assert.deepEqual(r.suggest, ['Stanza di montaggio']);
  assert.equal(check('**Editing room**', '**Stanza di montaggio**', ctx)[0].ok, true);
});
