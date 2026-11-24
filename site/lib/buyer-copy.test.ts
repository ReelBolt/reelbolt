// Buyer-copy rule (repo-root CLAUDE.md, "Marketing site"): no ffmpeg, Remotion, Docker, PostgreSQL or
// workflow-engine names on /, /features or /about. The catalogues feed those pages, so every
// catalogue of every language is scanned. Legal pages are not catalogued (the one sanctioned
// exception, open-source attribution in the terms, lives there).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { flatten, localeCodes, namespacesOf, readNamespace } from '../i18n/catalog-fixtures.ts';

// The rule covers /, /features and /about only. /runner is the desktop-app download page and names its
// requirements and open-source components (as it did before translation), so its namespace is out of scope.
const OUT_OF_SCOPE = new Set(['runner']);

const FORBIDDEN: [string, RegExp][] = [
  ['ffmpeg', /ffmpeg/i],
  ['Remotion', /remotion/i],
  ['Docker', /docker/i],
  ['PostgreSQL', /postgres(ql)?/i],
  ['workflow engine', /workflow[\s-]?engine/i],
  // The Italian spellings of the same internals.
  ['motore dei flussi di lavoro', /motore\s+(dei\s+|di\s+)?(flussi\s+di\s+lavoro|workflow)/i],
];

test('no site catalogue, in any language, names an internal technology', () => {
  const hits: string[] = [];
  for (const locale of localeCodes) {
    for (const ns of namespacesOf(locale)) {
      if (OUT_OF_SCOPE.has(ns)) continue;
      for (const [key, value] of flatten(readNamespace(locale, ns))) {
        for (const [name, pattern] of FORBIDDEN) {
          if (typeof value === 'string' && pattern.test(value)) hits.push(`${locale}/${ns}:${key} names ${name}`);
        }
      }
    }
  }
  assert.deepEqual(hits, []);
});
