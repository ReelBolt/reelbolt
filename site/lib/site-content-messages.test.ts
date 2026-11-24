import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { HOME_FAQS, PRICING_FAQS } from './faqs.ts';
import { FAILED_RUN_POLICY, SUPPORT_EMAIL } from './legal-placeholders.ts';
import catalog from './pricing/plans.catalog.json' with { type: 'json' };

// The page-copy namespaces owned by the site-content lane: Italian must mirror English key for key
// and placeholder for placeholder, and the English catalogue must stay equal to the typed sources
// it replaces (the FAQ arrays and the plan highlights), so nothing drifts from the visible copy.
const read = (locale: string, ns: string) =>
  JSON.parse(readFileSync(new URL(`../messages/${locale}/${ns}.json`, import.meta.url), 'utf8'));

function flatten(node: unknown, prefix = ''): Record<string, string> {
  if (typeof node === 'string') return { [prefix]: node };
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(node as Record<string, unknown>)) {
    Object.assign(out, flatten(v, prefix ? `${prefix}.${k}` : k));
  }
  return out;
}
const placeholders = (s: string) => [...s.matchAll(/\{(\w+)[,}]|<(\w+)>/g)].map((m) => m[1] ?? m[2]).sort();

const NAMESPACES = ['home', 'pricing', 'plans', 'about', 'contact', 'runner', 'features', 'faq', 'blog', 'audiences', 'proof'];

for (const ns of NAMESPACES) {
  test(`${ns}: Italian mirrors English keys and placeholders`, () => {
    const en = flatten(read('en', ns));
    const it = flatten(read('it', ns));
    assert.deepEqual(Object.keys(it).sort(), Object.keys(en).sort());
    for (const key of Object.keys(en)) {
      assert.deepEqual(placeholders(it[key]), placeholders(en[key]), `${ns}.${key}`);
    }
  });
}

test('faq: the English catalogue equals the typed FAQ arrays', () => {
  const faq = read('en', 'faq');
  const fill = (s: string) => s.replaceAll('{policy}', FAILED_RUN_POLICY).replaceAll('{email}', SUPPORT_EMAIL);
  assert.equal(faq.failedRunPolicy, FAILED_RUN_POLICY);
  HOME_FAQS.forEach((f, i) => {
    assert.equal(faq.home[i].q, f.question);
    assert.equal(fill(faq.home[i].a), f.answer);
  });
  PRICING_FAQS.forEach((f, i) => {
    assert.equal(faq.pricing[i].q, f.question);
    assert.equal(fill(faq.pricing[i].a), f.answer);
  });
});

test('plans: every highlight has a catalogue entry, and Italian keeps the same digits', () => {
  const en = read('en', 'plans').highlights;
  const it = read('it', 'plans').highlights;
  const digits = (s: string) => (s.match(/\d[\d.,]*/g) ?? []).map((d) => d.replace(/[.,]/g, ''));
  for (const plan of catalog.plans) {
    plan.highlights.forEach((h: string, i: number) => {
      assert.equal(en[plan.id][i], h);
      assert.deepEqual(digits(it[plan.id][i]), digits(h), `${plan.id}.${i}`);
    });
  }
});

test('the Italian flag file only names keys that exist', () => {
  const flags = JSON.parse(readFileSync(new URL('../messages/it/_flags.json', import.meta.url), 'utf8'));
  const files = readdirSync(new URL('../messages/it/', import.meta.url)).filter((f) => f.endsWith('.json') && !f.startsWith('_'));
  const all = new Set<string>();
  for (const f of files) for (const k of Object.keys(flatten(read('it', f.replace('.json', ''))))) all.add(`${f.replace('.json', '')}.${k}`);
  for (const key of Object.keys(flags)) {
    assert.ok([...all].some((k) => k === key || k.startsWith(`${key}.`)), `flag for unknown key ${key}`);
  }
});
