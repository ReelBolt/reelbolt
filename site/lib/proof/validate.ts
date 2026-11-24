// Pure checks and selectors for the proof registry. No framework imports, so `node --test` loads
// this file directly; `registry.ts` calls `proofProblems` at module load, so an entry that lacks
// its provenance fails `next build` instead of shipping.
import { PERSONAS, type Persona } from '../attribution-persona.ts';
import type { ProofRegistry, SampleMedia, SampleOutput, Testimonial, FounderNote } from './registry.ts';

const ID = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const DATE = /^\d{4}-\d{2}-\d{2}$/;

function isRealDate(value: unknown): value is string {
  if (typeof value !== 'string' || !DATE.test(value)) return false;
  const parsed = new Date(`${value}T00:00:00Z`);
  return !Number.isNaN(parsed.getTime()) && parsed.toISOString().slice(0, 10) === value;
}

/** A real date that is not in the future: nothing is signed off tomorrow. */
function isPastDate(value: unknown, today: string): boolean {
  return isRealDate(value) && value <= today;
}

const filled = (value: unknown): value is string => typeof value === 'string' && value.trim().length > 0;

/**
 * A media address the site can serve: an `https://` URL (the CDN), or a file under `site/public`
 * written as a path from the site root. Never `http://`, a protocol-relative URL or a data URI.
 */
export function isMediaSrc(value: unknown): boolean {
  if (typeof value !== 'string') return false;
  if (value.startsWith('/')) return !value.startsWith('//') && !/\s/.test(value) && value.length > 1;
  try {
    return new URL(value).protocol === 'https:';
  } catch {
    return false;
  }
}

function mediaProblems(where: string, media: Partial<SampleMedia> | undefined): string[] {
  const problems: string[] = [];
  if (!isMediaSrc(media?.videoSrc)) problems.push(`${where}: videoSrc must be an https URL or a site path`);
  if (!isMediaSrc(media?.posterSrc)) problems.push(`${where}: posterSrc must be an https URL or a site path`);
  return problems;
}

const positive = (n: unknown) => typeof n === 'number' && Number.isFinite(n) && n > 0;

/**
 * Every reason an entry cannot ship, as human-readable lines. Empty when the registry is empty or
 * every entry is complete. `today` (`YYYY-MM-DD`) is a parameter so the tests are not tied to the clock.
 */
export function proofProblems(registry: ProofRegistry, today: string = new Date().toISOString().slice(0, 10)): string[] {
  const problems: string[] = [];
  const seen = new Set<string>();

  for (const sample of registry.samples) {
    const where = `sample "${sample.id}"`;
    if (!filled(sample.id) || !ID.test(sample.id)) problems.push(`${where}: id must be kebab-case`);
    if (seen.has(sample.id)) problems.push(`${where}: duplicate id`);
    seen.add(sample.id);
    if (!filled(sample.title)) problems.push(`${where}: title is empty`);
    if (!filled(sample.recipe)) problems.push(`${where}: recipe is empty`);
    if (!(PERSONAS as readonly string[]).includes(sample.persona)) problems.push(`${where}: unknown persona "${sample.persona}"`);
    problems.push(...mediaProblems(where, sample));
    if (sample.before !== undefined) problems.push(...mediaProblems(`${where} before`, sample.before));
    if (sample.facts !== undefined) {
      const { sourceSeconds, outputSeconds, cuts } = sample.facts;
      if (!positive(sourceSeconds)) problems.push(`${where}: facts.sourceSeconds must be a positive number`);
      if (!positive(outputSeconds)) problems.push(`${where}: facts.outputSeconds must be a positive number`);
      if (!Number.isInteger(cuts) || cuts < 0) problems.push(`${where}: facts.cuts must be a whole number, zero or more`);
    }
    if (!filled(sample.signedOffBy)) problems.push(`${where}: signedOffBy is missing (who approved it for the public site)`);
    if (!isPastDate(sample.signedOffOn, today)) problems.push(`${where}: signedOffOn must be a real YYYY-MM-DD, not in the future`);
  }

  registry.testimonials.forEach((testimonial, index) => {
    const where = `testimonial ${index + 1}`;
    if (!filled(testimonial.quote)) problems.push(`${where}: quote is empty`);
    if (!filled(testimonial.name)) problems.push(`${where}: name is empty`);
    if (!filled(testimonial.role)) problems.push(`${where}: role is empty`);
    if (!filled(testimonial.permissionRef)) problems.push(`${where}: permissionRef is missing (where the written permission is kept)`);
    if (!isPastDate(testimonial.date, today)) problems.push(`${where}: date must be a real YYYY-MM-DD, not in the future`);
  });

  const note = registry.founderNote;
  if (note !== null) {
    if (!filled(note.text)) problems.push('founder note: text is empty');
    if (!filled(note.approvedBy)) problems.push('founder note: approvedBy is missing');
    if (!isPastDate(note.approvedOn, today)) problems.push('founder note: approvedOn must be a real YYYY-MM-DD, not in the future');
  }

  return problems;
}

/** The samples a page shows: all of them, or those of one persona. Keeps registry order. */
export function samplesFor(registry: ProofRegistry, persona?: Persona): SampleOutput[] {
  return registry.samples.filter((sample) => persona === undefined || sample.persona === persona);
}

/**
 * The sample a before/after section shows as a real pair: the first one that carries its raw
 * footage, of the given persona when one is asked for. Undefined means the section stays in words.
 */
export function beforeAfterSample(
  registry: ProofRegistry,
  persona?: Persona,
): (SampleOutput & { before: SampleMedia }) | undefined {
  return samplesFor(registry, persona).find(
    (sample): sample is SampleOutput & { before: SampleMedia } => sample.before !== undefined,
  );
}

export function testimonialsOf(registry: ProofRegistry): Testimonial[] {
  return [...registry.testimonials];
}

export function founderNoteOf(registry: ProofRegistry): FounderNote | undefined {
  return registry.founderNote ?? undefined;
}

/** `95` -> `1:35`. Whole seconds, rounded down: a fact is shown as measured, never rounded up. */
export function clockTime(seconds: number): string {
  const whole = Math.max(0, Math.floor(seconds));
  return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, '0')}`;
}
