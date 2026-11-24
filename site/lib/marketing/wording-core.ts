// Pure helpers for the marketing-consent wording registry. No JSON import here: this module is
// reachable from the Cloudflare Pages Functions, whose bundler (wrangler 3) rejects JSON import
// attributes, while node --test requires them. Callers that need the registry import it themselves
// (wording.ts for Next and tests; a plain JSON import in site/functions).
export interface WordingRegistry {
  current: string;
  versions: Record<string, string>;
}

export interface MarketingWording {
  version: string;
  text: string;
}

/** Resolves the registry's `current` entry, throwing if the file is inconsistent. */
export function currentWording(source: WordingRegistry): MarketingWording {
  const text = source.versions[source.current];
  if (typeof text !== 'string' || text.length === 0) {
    throw new Error(`marketing-consent.wording.json: no text for current version "${source.current}"`);
  }
  return { version: source.current, text };
}

const LINK_TEXT = 'Privacy Policy';

/**
 * Splits the wording around its "Privacy Policy" mention so the form can render that phrase as a
 * link while every other character stays exactly as registered. `link` is null when the phrase is
 * absent, in which case the whole text goes in `before`.
 */
export function splitPrivacyLink(text: string): { before: string; link: string | null; after: string } {
  const at = text.lastIndexOf(LINK_TEXT);
  if (at < 0) return { before: text, link: null, after: '' };
  return { before: text.slice(0, at), link: LINK_TEXT, after: text.slice(at + LINK_TEXT.length) };
}
