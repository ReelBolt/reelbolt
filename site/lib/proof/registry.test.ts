import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';

import { FOUNDER_NOTE, PROOF, SAMPLE_OUTPUTS, TESTIMONIALS, type ProofRegistry, type SampleOutput, type Testimonial } from './registry.ts';
import {
  beforeAfterSample,
  clockTime,
  founderNoteOf,
  isMediaSrc,
  proofProblems,
  samplesFor,
  testimonialsOf,
} from './validate.ts';

// The proof registry is how the site shows that ReelBolt works: real outputs, real quotes, the
// founder's own note. An entry that is not real and signed off is a false claim, so these tests
// guard two things: what ships today is empty or complete, and an incomplete entry cannot pass.

const TODAY = '2026-11-11';

const sample = (over: Partial<SampleOutput> = {}): SampleOutput => ({
  id: 'trip-recap',
  title: 'Trip recap',
  recipe: 'Highlight edit from footage',
  persona: 'creator',
  videoSrc: 'https://cdn.example.test/samples/trip-recap.mp4',
  posterSrc: '/samples/trip-recap.jpg',
  signedOffBy: 'Founder',
  signedOffOn: '2026-11-01',
  ...over,
});
const testimonial = (over: Partial<Testimonial> = {}): Testimonial => ({
  quote: 'It cut my footage.',
  name: 'A. Person',
  role: 'Yoga teacher',
  permissionRef: 'permissions/2026-11-a-person.pdf',
  date: '2026-11-02',
  ...over,
});
const registry = (over: Partial<ProofRegistry> = {}): ProofRegistry => ({ samples: [], testimonials: [], founderNote: null, ...over });

test('the shipped registry is empty or complete: nothing in it lacks its sign-off', () => {
  assert.deepEqual(proofProblems(PROOF), []);
  assert.equal(PROOF.samples, SAMPLE_OUTPUTS);
  assert.equal(PROOF.testimonials, TESTIMONIALS);
  assert.equal(PROOF.founderNote, FOUNDER_NOTE);
});

test('today the three registries ship empty, so the proof components render nothing', () => {
  // No real, signed-off sample, testimonial or founder note exists yet. When the first real entry
  // lands, change this test in the same commit as the entry and its sign-off (docs/marketing-site.md,
  // "Proof registry"). It exists so an entry cannot arrive unnoticed.
  assert.deepEqual(SAMPLE_OUTPUTS, []);
  assert.deepEqual(TESTIMONIALS, []);
  assert.equal(FOUNDER_NOTE, null);
  assert.deepEqual(samplesFor(PROOF), []);
  assert.equal(beforeAfterSample(PROOF), undefined);
  assert.deepEqual(testimonialsOf(PROOF), []);
  assert.equal(founderNoteOf(PROOF), undefined);
});

test('a complete entry of each kind passes', () => {
  const full = registry({
    samples: [
      sample({
        before: { videoSrc: 'https://cdn.example.test/samples/trip-raw.mp4', posterSrc: 'https://cdn.example.test/samples/trip-raw.jpg' },
        facts: { sourceSeconds: 184.2, outputSeconds: 41, cuts: 12 },
      }),
    ],
    testimonials: [testimonial()],
    founderNote: { text: 'Why I built this.', approvedBy: 'The founder', approvedOn: '2026-11-03' },
  });
  assert.deepEqual(proofProblems(full, TODAY), []);
});

test('a sample without its provenance cannot ship', () => {
  const cases: [string, Partial<SampleOutput>, RegExp][] = [
    ['no signer', { signedOffBy: '' }, /signedOffBy is missing/],
    ['blank signer', { signedOffBy: '   ' }, /signedOffBy is missing/],
    ['no date', { signedOffOn: '' }, /signedOffOn/],
    ['impossible date', { signedOffOn: '2026-02-30' }, /signedOffOn/],
    ['signed off in the future', { signedOffOn: '2026-11-12' }, /signedOffOn/],
    ['wrong date shape', { signedOffOn: '11/01/2026' }, /signedOffOn/],
    ['no title', { title: '' }, /title is empty/],
    ['no recipe', { recipe: ' ' }, /recipe is empty/],
    ['bad id', { id: 'Trip Recap' }, /id must be kebab-case/],
    ['unknown persona', { persona: 'everyone' as never }, /unknown persona/],
    ['http video', { videoSrc: 'http://cdn.example.test/a.mp4' }, /videoSrc/],
    ['protocol-relative poster', { posterSrc: '//cdn.example.test/a.jpg' }, /posterSrc/],
    ['empty poster', { posterSrc: '' }, /posterSrc/],
    ['raw footage without a poster', { before: { videoSrc: 'https://cdn.example.test/raw.mp4', posterSrc: '' } }, /before: posterSrc/],
    ['zero-length source', { facts: { sourceSeconds: 0, outputSeconds: 30, cuts: 3 } }, /sourceSeconds/],
    ['negative output', { facts: { sourceSeconds: 60, outputSeconds: -1, cuts: 3 } }, /outputSeconds/],
    ['fractional cuts', { facts: { sourceSeconds: 60, outputSeconds: 30, cuts: 2.5 } }, /cuts/],
    ['not-a-number fact', { facts: { sourceSeconds: Number.NaN, outputSeconds: 30, cuts: 2 } }, /sourceSeconds/],
  ];
  for (const [name, over, expected] of cases) {
    const problems = proofProblems(registry({ samples: [sample(over)] }), TODAY);
    assert.ok(problems.some((p) => expected.test(p)), `${name}: expected ${expected}, got ${JSON.stringify(problems)}`);
  }
  // A missing field (an entry typed past the compiler) is caught the same way as an empty one.
  const missing = { ...sample() } as Partial<SampleOutput>;
  delete missing.signedOffBy;
  delete missing.signedOffOn;
  assert.equal(proofProblems(registry({ samples: [missing as SampleOutput] }), TODAY).length, 2);
});

test('two samples cannot share an id', () => {
  const problems = proofProblems(registry({ samples: [sample(), sample()] }), TODAY);
  assert.ok(problems.some((p) => /duplicate id/.test(p)));
});

test('a testimonial without written permission on file cannot ship', () => {
  const cases: [string, Partial<Testimonial>, RegExp][] = [
    ['no permission reference', { permissionRef: '' }, /permissionRef is missing/],
    ['no name', { name: '' }, /name is empty/],
    ['no role', { role: '' }, /role is empty/],
    ['no quote', { quote: '  ' }, /quote is empty/],
    ['no date', { date: '' }, /date must be/],
    ['future date', { date: '2027-01-01' }, /date must be/],
  ];
  for (const [name, over, expected] of cases) {
    const problems = proofProblems(registry({ testimonials: [testimonial(over)] }), TODAY);
    assert.ok(problems.some((p) => expected.test(p)), `${name}: ${JSON.stringify(problems)}`);
  }
});

test('a founder note needs its text, who approved it and when', () => {
  const ok = { text: 'A note.', approvedBy: 'The founder', approvedOn: '2026-11-03' };
  assert.deepEqual(proofProblems(registry({ founderNote: ok }), TODAY), []);
  assert.match(proofProblems(registry({ founderNote: { ...ok, text: '' } }), TODAY).join('\n'), /text is empty/);
  assert.match(proofProblems(registry({ founderNote: { ...ok, approvedBy: '' } }), TODAY).join('\n'), /approvedBy is missing/);
  assert.match(proofProblems(registry({ founderNote: { ...ok, approvedOn: '2026-13-01' } }), TODAY).join('\n'), /approvedOn/);
});

test('media addresses are https or a site path, nothing else', () => {
  for (const good of ['https://cdn.reelbolt.ai/samples/a.mp4', '/samples/a.jpg']) assert.equal(isMediaSrc(good), true, good);
  for (const bad of ['http://x.test/a.mp4', '//x.test/a.mp4', 'a.mp4', '', '/', '/a b.jpg', 'data:video/mp4;base64,AAAA', 'javascript:alert(1)', undefined, null, 5]) {
    assert.equal(isMediaSrc(bad), false, String(bad));
  }
});

test('the selectors pick by persona and keep registry order', () => {
  const pairSample = sample({ id: 'b', persona: 'business', before: { videoSrc: 'https://x.test/raw.mp4', posterSrc: '/raw.jpg' } });
  const reg = registry({ samples: [sample({ id: 'a' }), pairSample, sample({ id: 'c', persona: 'business' })] });
  assert.deepEqual(samplesFor(reg).map((s) => s.id), ['a', 'b', 'c']);
  assert.deepEqual(samplesFor(reg, 'business').map((s) => s.id), ['b', 'c']);
  assert.deepEqual(samplesFor(reg, 'educator'), []);
  // The before/after section shows a real pair only when a sample carries its raw footage.
  assert.equal(beforeAfterSample(reg)?.id, 'b');
  assert.equal(beforeAfterSample(reg, 'business')?.id, 'b');
  assert.equal(beforeAfterSample(reg, 'creator'), undefined);
  assert.equal(beforeAfterSample(registry({ samples: [sample()] })), undefined);
});

test('a duration is shown as measured, rounded down to the second', () => {
  assert.equal(clockTime(0), '0:00');
  assert.equal(clockTime(41), '0:41');
  assert.equal(clockTime(95.9), '1:35');
  assert.equal(clockTime(184.2), '3:04');
  assert.equal(clockTime(-3), '0:00');
});

test('every proof component reads the registry through the selectors and renders nothing when it is empty', () => {
  const dir = new URL('../../components/proof/', import.meta.url);
  const source = (name: string) => readFileSync(new URL(name, dir), 'utf8');
  assert.match(source('SampleGallery.tsx'), /const samples = samplesFor\(PROOF, persona\);\s+if \(samples\.length === 0\) return null;/);
  assert.match(source('Testimonials.tsx'), /const testimonials = testimonialsOf\(PROOF\);\s+if \(testimonials\.length === 0\) return null;/);
  assert.match(source('FounderNote.tsx'), /const note = founderNoteOf\(PROOF\);\s+if \(!note\) return null;/);
  // The before/after section always shows its words; the real pair and its facts appear only with a sample.
  const beforeAfter = source('BeforeAfter.tsx');
  assert.match(beforeAfter, /const sample = beforeAfterSample\(PROOF, persona\);/);
  assert.ok((beforeAfter.match(/\{sample && \(/g) ?? []).length >= 3, 'the pair, and its facts, render only for a real sample');
  // No proof component carries an entry, a quote or a number of its own.
  for (const file of readdirSync(dir)) {
    const text = source(file);
    assert.doesNotMatch(text, /aggregateRating|ratingValue|reviewCount/, `${file}: no rating markup, ever`);
    assert.doesNotMatch(text, /signedOffBy:|permissionRef:/, `${file}: entries live in lib/proof/registry.ts only`);
  }
});

test('the registry refuses an incomplete entry at load time, which fails the build', () => {
  const text = readFileSync(new URL('./registry.ts', import.meta.url), 'utf8');
  assert.match(text, /const problems = proofProblems\(PROOF\);\s+if \(problems\.length > 0\) \{\s+throw new Error/);
});
