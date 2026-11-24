// Asserts that the FAQPage structured data a crawler reads is character-for-character the FAQ text
// a reader sees, on both pages that carry a FAQ block, and that the llms.txt plain-text blocks
// repeat the same questions and answers in the same order. All three are generated from
// `site/lib/faqs` (`HOME_FAQS` and `PRICING_FAQS`), so a failure here means something started
// rendering from a copy of the copy.
//
// Runs against the built Pages bundle, not the source, because that is the only place the two
// renderings actually exist side by side:
//
//   npm run build:pages && node scripts/check-faq-schema.mjs      # dir defaults to "out"
//
// Google treats FAQ markup whose answers are not visible on the page as a violation, and an
// assistant reading llms.txt is quoting us, so a drift in either direction is a real bug.

import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';

const dir = process.argv[2] ?? 'out';
const failures = [];

const ENTITIES = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: '\u00a0' };

function decode(text) {
  return text.replace(/&(#x[0-9a-fA-F]+|#[0-9]+|[a-zA-Z]+);/g, (match, entity) => {
    if (entity in ENTITIES) return ENTITIES[entity];
    if (entity.startsWith('#x')) return String.fromCodePoint(parseInt(entity.slice(2), 16));
    if (entity.startsWith('#')) return String.fromCodePoint(parseInt(entity.slice(1), 10));
    return match;
  });
}

/** The text of one HTML element, with tags removed and whitespace collapsed the way a browser does. */
function text(raw) {
  return decode(raw.replace(/<[^>]*>/g, ' ')).replace(/\s+/g, ' ').trim();
}

/** The FAQPage block of a built page, as [{ question, answer }]. */
function schemaFaqs(html, page) {
  for (const [, body] of html.matchAll(/<script type="application\/ld\+json">([\s\S]*?)<\/script>/g)) {
    let data;
    try {
      data = JSON.parse(body);
    } catch {
      failures.push(`${page}: a JSON-LD block is not valid JSON`);
      continue;
    }
    if (data['@type'] !== 'FAQPage') continue;
    return data.mainEntity.map((q) => ({
      question: q.name,
      answer: q.acceptedAnswer.text,
    }));
  }
  failures.push(`${page}: no FAQPage JSON-LD found`);
  return [];
}

/** The visible FAQ block of a built page, as [{ question, answer }]. */
function visibleFaqs(html) {
  return [...html.matchAll(/<dt[^>]*>([\s\S]*?)<\/dt>[\s\S]*?<dd[^>]*>([\s\S]*?)<\/dd>/g)].map(
    ([, question, answer]) => ({ question: text(question), answer: text(answer) }),
  );
}

/** The \`### question\` blocks of llms.txt, in order. */
function llmsFaqs(text) {
  const chunks = text.split(/\n### /).slice(1);
  return chunks.map((chunk) => {
    const newline = chunk.indexOf('\n');
    const question = chunk.slice(0, newline).trim();
    const rest = chunk.slice(newline + 1);
    const end = rest.search(/\n\n## /);
    return { question, answer: (end === -1 ? rest : rest.slice(0, end)).trim() };
  });
}

function compare(label, actual, expected) {
  if (actual.length !== expected.length) {
    failures.push(`${label}: ${actual.length} answers, expected ${expected.length}`);
  }
  const count = Math.min(actual.length, expected.length);
  for (let i = 0; i < count; i += 1) {
    if (actual[i].question !== expected[i].question) {
      failures.push(`${label}: question ${i + 1} differs\n    rendered: ${actual[i].question}\n    markup:   ${expected[i].question}`);
    }
    if (actual[i].answer !== expected[i].answer) {
      failures.push(`${label}: answer ${i + 1} ("${expected[i].question}") differs\n    rendered: ${actual[i].answer}\n    markup:   ${expected[i].answer}`);
    }
  }
}

if (!existsSync(dir)) {
  console.error(`${dir}/ does not exist — run \`npm run build:pages\` first.`);
  process.exit(1);
}

// The Pages export writes "pricing.html"; a server build writes "pricing/index.html". Accept
// either so the check runs against whichever bundle was just built.
const pages = ['/', '/pricing'];
const schema = [];

for (const page of pages) {
  const candidates = page === '/' ? ['index.html'] : [`${page.slice(1)}.html`, `${page.slice(1)}/index.html`];
  const file = candidates.map((candidate) => path.join(dir, candidate)).find(existsSync);
  if (!file) {
    failures.push(`${page}: none of ${candidates.join(', ')} exists in ${dir}`);
    continue;
  }
  const html = readFileSync(file, 'utf8');
  const fromSchema = schemaFaqs(html, page);
  const fromPage = visibleFaqs(html);
  schema.push(fromSchema);
  compare(page, fromPage, fromSchema);
  console.log(`${page}: ${fromPage.length} visible answers, ${fromSchema.length} in FAQPage markup`);
}

const llmsFile = path.join(dir, 'llms.txt');
if (!existsSync(llmsFile)) {
  failures.push(`${llmsFile} is missing — llms.txt is not in the built output`);
} else {
  const fromLlms = llmsFaqs(readFileSync(llmsFile, 'utf8'));
  compare('llms.txt', fromLlms, schema.flat());
  console.log(`llms.txt: ${fromLlms.length} answers`);
}

if (failures.length > 0) {
  console.error('\nFAQ text and markup disagree:');
  for (const failure of failures) console.error(`  ✗ ${failure}`);
  process.exit(1);
}

console.log('\n✓ rendered FAQ text, FAQPage markup and llms.txt agree on every page.');
