import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

// The rules every comparison post follows (docs/marketing/blog-drafts/README.md and FACTCHECK.md).
// `node --test` cannot load TSX, so these read the post sources as text, like encoding.test.ts.
// A post that needs to break a rule changes the rule here first, with the reason.

const dir = join(import.meta.dirname, '..', '..', 'content', 'blog');
const files = readdirSync(dir).filter((f) => f.endsWith('.tsx'));
const comparisons = files.filter((f) => f.startsWith('reelbolt-vs-'));

function read(file: string): string {
  return readFileSync(join(dir, file), 'utf8');
}

/** The post without `//` comment lines, so a note to the next writer is not judged as copy. */
function withoutComments(source: string): string {
  return source
    .split('\n')
    .filter((line) => !line.trimStart().startsWith('//'))
    .join('\n');
}

/** The rendered part of a post without its source list, where page titles are quoted as they are. */
function copyOf(source: string): string {
  return withoutComments(source).replace(/<SourceList[\s\S]*?\n\s*\/>/g, '');
}

const TRADEMARK =
  'Product names are trademarks of their owners and are used only to identify the products.';
const CHECKED_LINE = 'checked on {CHECKED} from each vendor’s public pages; they may have changed.';

test('there are comparison posts to check', () => {
  assert.ok(comparisons.length >= 9, `expected the nine reelbolt-vs posts, found ${comparisons.length}`);
});

test('every comparison post ends with the dated disclaimer and the trademark line', () => {
  for (const file of comparisons) {
    const source = read(file);
    assert.match(source, /const CHECKED = '\d{4}-\d{2}-\d{2}';/, `${file}: needs a CHECKED date`);
    assert.ok(
      source.includes(CHECKED_LINE),
      `${file}: the disclaimer must read "... ${CHECKED_LINE}" with the date taken from CHECKED`,
    );
    assert.ok(source.includes(TRADEMARK), `${file}: the trademark line is missing or reworded`);
    assert.ok(
      source.lastIndexOf(TRADEMARK) > source.lastIndexOf('<SourceList'),
      `${file}: the disclaimer goes after the sources`,
    );
  }
});

test('a post that could not read a pricing page does not say prices were checked', () => {
  for (const file of comparisons) {
    const copy = copyOf(read(file));
    const unread = /could not read[^.]*pricing page|pricing page[^.]*could not be read/i.test(copy);
    const claims = copy.includes(`Prices and features ${CHECKED_LINE}`);
    assert.ok(!(unread && claims), `${file}: says a pricing page could not be read and that prices were checked`);
  }
});

test('no comparison post states a price by hand', () => {
  // ReelBolt figures come from catalog() and the shared constants; a competitor price is never given.
  const money = /[$€£]\s?\d|\b\d[\d.,]*\s?(?:USD|EUR|GBP|dollars?|euros?)\b/;
  for (const file of comparisons) {
    const lines = copyOf(read(file)).split('\n');
    const bad = lines.flatMap((line, i) => (money.test(line) ? [`${file}:${i + 1}`] : []));
    assert.deepEqual(bad, [], `hand-written price in ${bad.join(', ')}`);
  }
});

test('every source carries the check date, and citations and sources match one to one', () => {
  for (const file of comparisons) {
    const source = withoutComments(read(file));
    const sourceIds = [...source.matchAll(/id: 'source-(\d+)'/g)].map((m) => Number(m[1]));
    assert.ok(sourceIds.length > 0, `${file}: a comparison needs sources`);
    assert.equal(new Set(sourceIds).size, sourceIds.length, `${file}: duplicate source id`);

    const dated = [...source.matchAll(/accessed: ([^ ,}]+)/g)].map((m) => m[1]);
    assert.equal(dated.length, sourceIds.length, `${file}: every source needs an accessed date`);
    assert.deepEqual(
      dated.filter((value) => value !== 'CHECKED'),
      [],
      `${file}: every source uses accessed: CHECKED`,
    );

    const cited = new Set(
      [...source.matchAll(/<Cite n=\{\[([\d,\s]+)\]\}/g)].flatMap((m) =>
        m[1].split(',').map((part) => Number(part.trim())),
      ),
    );
    const known = new Set(sourceIds);
    assert.deepEqual(
      [...cited].filter((n) => !known.has(n)),
      [],
      `${file}: a citation points at a source that is not listed`,
    );
    assert.deepEqual(
      sourceIds.filter((n) => !cited.has(n)),
      [],
      `${file}: a listed source is never cited`,
    );
  }
});

test('every comparison post says where the other product is the better choice', () => {
  for (const file of comparisons) {
    assert.match(read(file), /<h2 id="[^"]+">Where .+ is the better choice<\/h2>/, file);
  }
});

test('consent is described once and never compared', () => {
  // The rule of record (FACTCHECK.md): no consent comparison. ReelBolt's own requirement is stated in
  // one paragraph that also says the two processes are not compared. Table cells, FAQ answers and
  // every other sentence leave the word out, on both sides.
  for (const file of comparisons) {
    const copy = copyOf(read(file));
    const mentions = [...copy.matchAll(/consent/gi)].map((m) => m.index);
    if (mentions.length === 0) continue;

    const paragraphs = [...copy.matchAll(/<p>[\s\S]*?<\/p>/g)]
      .filter((m) => /consent/i.test(m[0]))
      .map((m) => ({ start: m.index, end: m.index + m[0].length, text: m[0] }));
    assert.equal(paragraphs.length, 1, `${file}: consent belongs in exactly one paragraph`);
    const [paragraph] = paragraphs;
    assert.match(
      paragraph.text.replace(/\s+/g, ' '),
      /We do not compare the two (?:companies’ consent )?(?:processes|approaches) here\./,
      `${file}: the consent paragraph must say the two processes are not compared`,
    );
    assert.doesNotMatch(
      paragraph.text.replace(/\s+/g, ' '),
      /not (?:described|found|mentioned)[^.]*consent|consent[^.]*not (?:described|found|mentioned)/i,
      `${file}: no statement that a competitor's consent step is absent`,
    );
    const outside = mentions.filter((at) => at < paragraph.start || at >= paragraph.end);
    assert.deepEqual(outside, [], `${file}: "consent" appears outside the one consent paragraph`);
  }
});

test('posts use American spelling and the shared first-video grant figure', () => {
  for (const file of files) {
    const lines = copyOf(read(file)).split('\n');
    const british = lines.flatMap((line, i) => (/labelled|judgement/i.test(line) ? [`${file}:${i + 1}`] : []));
    assert.deepEqual(british, [], `British spelling in ${british.join(', ')}`);
    // The grant size is configurable; posts read FIRST_VIDEO_GRANT_CREDITS from lib/legal-placeholders.
    const typed = lines.flatMap((line, i) => (/\b750\s+credits\b/.test(line) ? [`${file}:${i + 1}`] : []));
    assert.deepEqual(typed, [], `hand-typed first-video grant in ${typed.join(', ')}`);
  }
});
