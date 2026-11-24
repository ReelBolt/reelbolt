// Proof the site may show: sample outputs made with ReelBolt, customer testimonials and a founder
// note. Every entry is a real, signed-off asset. The three registries SHIP EMPTY, and the
// components that read them (`components/proof/*`) render nothing while they are empty: there is
// no real, signed-off sample, quote or note today, and an invented one would be a false claim
// (docs/marketing-site.md, "No invented facts").
//
// To add an entry, follow docs/marketing-site.md "Proof registry": the output goes through
// docs/marketing/launch-2026/public-outputs-checklist.md first, and the entry carries who signed
// it off and when. `proofProblems` (./validate) runs at module load, so an entry without its
// provenance fields fails `next build` and `npm test` instead of shipping.
//
// Text here is data, like a blog post: it is shown as written in every language. A testimonial is
// the person's own words and is never translated or edited.
import type { Persona } from '../attribution-persona.ts';
import { proofProblems } from './validate.ts';

/** The real numbers of the run that made a sample. Shown only as measured, never rounded up. */
export interface SampleFacts {
  /** Total length of the footage that went in, in seconds. */
  sourceSeconds: number;
  /** Length of the finished video, in seconds. */
  outputSeconds: number;
  /** How many cuts the edit made. */
  cuts: number;
}

/** A video file and its poster. `https://` (the CDN) or a path under `site/public` starting with `/`. */
export interface SampleMedia {
  videoSrc: string;
  posterSrc: string;
}

/** A finished video made with ReelBolt that we are allowed to show. */
export interface SampleOutput extends SampleMedia {
  /** Kebab-case, unique. */
  id: string;
  title: string;
  /** The recipe that made it, as the product names it ("Highlight edit from footage"). */
  recipe: string;
  /** Whose kind of video it is; the audience pages show the samples of their own persona. */
  persona: Persona;
  /** The raw footage before the edit. With it, the before/after sections show the real pair. */
  before?: SampleMedia;
  facts?: SampleFacts;
  /** Who watched the output in full and approved it for the public site (name or role). */
  signedOffBy: string;
  /** When, as `YYYY-MM-DD`. */
  signedOffOn: string;
}

/** Something a real customer said, with their written permission to publish it. */
export interface Testimonial {
  /** Their words, verbatim. */
  quote: string;
  name: string;
  /** What they do, as they describe it ("Yoga teacher", "Founder, Example Ltd"). */
  role: string;
  /** Where the written permission is kept (a document path or a ticket id), never the permission text itself. */
  permissionRef: string;
  /** When they said it, as `YYYY-MM-DD`. */
  date: string;
}

/** A short note from the founder, in the founder's own words. */
export interface FounderNote {
  text: string;
  /** The person whose note it is and who approved the wording. */
  approvedBy: string;
  /** When, as `YYYY-MM-DD`. */
  approvedOn: string;
}

export interface ProofRegistry {
  samples: readonly SampleOutput[];
  testimonials: readonly Testimonial[];
  founderNote: FounderNote | null;
}

// Empty on purpose. Do not add an entry that is not real and signed off.
export const SAMPLE_OUTPUTS: readonly SampleOutput[] = [];

// Empty on purpose. ReelBolt has no customer testimonials yet.
export const TESTIMONIALS: readonly Testimonial[] = [];

// None yet. The founder writes and approves this; nobody else drafts it.
export const FOUNDER_NOTE: FounderNote | null = null;

export const PROOF: ProofRegistry = {
  samples: SAMPLE_OUTPUTS,
  testimonials: TESTIMONIALS,
  founderNote: FOUNDER_NOTE,
};

const problems = proofProblems(PROOF);
if (problems.length > 0) {
  throw new Error(`lib/proof/registry.ts has entries that cannot ship:\n- ${problems.join('\n- ')}`);
}
