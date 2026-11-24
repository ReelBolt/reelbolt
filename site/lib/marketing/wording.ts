// The marketing-consent wording the contact form shows and the subscribe call records.
// `marketing-consent.wording.json` is a byte-identical vendored copy of
// inference/src/ReelBolt.Inference.Api/Marketing/marketing-consent.wording.json, the registry the
// API checks `wordingVersion` against. Never edit a published version's text here: copy the
// canonical file over this one, and CI's "Marketing consent wording drift" step diffs the two.
// Next and node --test entry point. Not imported by anything under site/functions: the Pages
// bundler (wrangler 3) rejects the import attribute below, so functions import the JSON plainly and
// use wording-core.ts.
import registry from './marketing-consent.wording.json' with { type: 'json' };
import { currentWording, type MarketingWording, type WordingRegistry } from './wording-core.ts';

export { currentWording, splitPrivacyLink } from './wording-core.ts';
export type { MarketingWording, WordingRegistry } from './wording-core.ts';

export const MARKETING_WORDING: MarketingWording = currentWording(registry as WordingRegistry);
