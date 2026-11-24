import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const footerSource = readFileSync(
  new URL('../components/layout/SiteFooter.tsx', import.meta.url),
  'utf8',
);
// The disclosure sentences live in the English catalogue; the component supplies the privacy link.
const catalogue = JSON.parse(
  readFileSync(new URL('../messages/en/layout.json', import.meta.url), 'utf8'),
) as { footer: { disclosure: string } };
const footer = footerSource + '\n' + catalogue.footer.disclosure;

// The footer copy wraps across JSX lines, so every sentence regex is whitespace-tolerant (\s+
// between words): a re-wrap of the paragraph must not break a pinned phrase.
test('the footer states the product purpose', () => {
  // The purpose sentence follows the positioning (docs/design/new-user-funnel.md section 2):
  // ReelBolt makes videos for anyone with a story to tell, so the footer no longer narrows it to
  // software products. The sentences Google's verification reads are pinned below, unchanged.
  assert.match(footer, /makes\s+finished\s+videos\s+from\s+the\s+materials\s+you\s+bring/i);
  assert.match(footer, /your\s+footage,\s+your\s+words\s+and\s+your\s+brand/i);
  assert.doesNotMatch(footer, /videos\s+for\s+software\s+products/i);
});

test('the footer describes the third-party APIs generally, with Google as one example', () => {
  assert.match(footer, /third-party\s+APIs/);
  assert.match(
    footer,
    /Google\s+is\s+one\s+of\s+them,\s+used\s+only\s+for\s+optional\s+sign-in\s+and\s+Drive\s+sync/,
  );
});

test('the footer says the privacy policy explains what is sent and why', () => {
  assert.match(footer, /describes\s+what\s+is\s+sent\s+to\s+each\s+provider\s+and\s+why/);
});

test('the footer keeps the NCII limitation Google requires on the homepage', () => {
  assert.match(
    footer,
    /do\s+not\s+use\s+Google\s+APIs\s+or\s+any\s+other\s+third-party\s+AI\s+service\s+to\s+generate\s+non-consensual\s+intimate\s+imagery\s+\(NCII\)\s+or\s+any\s+sexually\s+explicit\s+content/,
  );
});

test('the footer links the privacy policy next to the disclosure', () => {
  // Absolute, so the disclosure link matches the consent screen URL exactly.
  assert.match(footer, /href=\{`\$\{siteConfig\.url\}\/legal\/privacy`\}/);
});

test('the footer copy keeps the buyer-copy vocabulary rule', () => {
  for (const word of ['ffmpeg', 'Remotion', 'Docker', 'PostgreSQL', 'workflow-engine']) {
    assert.doesNotMatch(footer, new RegExp(word, 'i'), `the footer must not name ${word}`);
  }
});
