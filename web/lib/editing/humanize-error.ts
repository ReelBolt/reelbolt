/**
 * VF3: the one place an error becomes a sentence for a person. Every error text in the editor passes
 * through {@link humanizeError}: no raw server JSON, no status codes, no exception names, no GUIDs, no op
 * names. A message that is already plain is kept; one that is not is replaced by what the status means in
 * words, or by the caller's own `fallback`. Pure: no UI, no store.
 */

import { ApiError } from '@/lib/api/client';
import { refusalSentence } from './refusal';
import type { EditTimeline } from './types';

const GUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;
const OP_PREFIX =
  /^(?:addMarker|addTrack|addTransition|append|blade|closeGaps|deleteGap|extract|fitToFill|insert|join|lift|move|moveKeyframe|moveTrack|nudge|overwrite|placeOnTop|removeKeyframe|removeMarker|removeTrack|removeTransition|replace|retime|setBus|setColorGroup|setInOut|setKeyframe|setMarker|setProperty|setSequence|setTimelineSettings|setTrackProperty|trim|restore): /;

/** Text that is not a sentence for people: JSON/HTML, a stack trace, an exception or type name, a URL, a status code. */
export function looksRaw(message: string): boolean {
  const text = message.trim();
  return (
    text.length === 0 ||
    /^[{[<]/.test(text) ||
    /"\w+"\s*:/.test(text) ||
    /\bJSON\b/.test(text) ||
    /\b(?:System|Microsoft|Npgsql)\.[A-Z]/.test(text) ||
    /\b[A-Z][A-Za-z0-9_]*(?:\.[A-Z][A-Za-z0-9_`]*)+\b/.test(text) ||
    /\b[A-Z][a-z]+[A-Z][A-Za-z]*(?:Exception|Error)\b/.test(text) ||
    /^\s*at\s+\S+\(/m.test(text) ||
    /https?:\/\//.test(text) ||
    /\b(?:HTTP|status|code)\s*:?\s*[1-5]\d\d\b/i.test(text) ||
    /^[1-5]\d\d\b/.test(text) ||
    /\bPath:\s*\$|\bLineNumber\b|\bBytePositionInLine\b/.test(text) ||
    /^The \S+ field is required\.?$/.test(text) ||
    /\bundefined\b|\bnull\b|\[object Object\]/.test(text)
  );
}

/** What a failed request means in words, by status. */
export function statusSentence(status: number): string | null {
  if (status === 401) return 'Your session has ended. Sign in again to continue.';
  if (status === 402) return 'You do not have enough credits for that.';
  if (status === 403) return 'You do not have access to that.';
  if (status === 404) return 'That no longer exists. It may have been deleted.';
  if (status === 409) return 'This changed somewhere else. Reload and try again.';
  if (status === 413) return 'That is too large.';
  if (status === 429) return 'Too many requests. Wait a moment and try again.';
  if (status >= 500) return 'Something went wrong on our side. Try again in a moment.';
  return null;
}

const NETWORK = /failed to fetch|networkerror|network request failed|load failed|fetch failed|the user aborted/i;

function sentenceCase(text: string): string {
  const t = text.replace(/\s+/g, ' ').trim();
  const s = t.charAt(0).toUpperCase() + t.slice(1);
  return /[.!?…]$/.test(s) ? s : `${s}.`;
}

/** Refusal codes with a sentence of their own (guest review links). */
const CODED_SENTENCES: Record<string, string> = {
  NO_RENDER_TO_SHARE: 'Make a full render first.',
  CONTENT_MODERATION_PENDING: 'Your video is still being checked. Try again in a moment.',
  SEED_WOULD_REVEAL_CENSORED: 'This video hides part of the picture, and the editor cannot carry that over, so no timeline was made.',
  REVIEW_LINK_LIMIT: 'This timeline has too many share links. Turn one off, then try again.',
};

/** One violation of a seed-invalid 500 body; each part is kept only when it is plain words. */
export interface SeedViolation {
  path: string | null;
  message: string | null;
}

/** The rule a timeline seed was refused for: T-008's 500 body `{ message, violations }`, cleaned. */
export interface SeedRule {
  message: string;
  violations: SeedViolation[];
}

/** `value` when it is a non-empty string that reads as plain words, else null. */
function plainText(value: unknown): string | null {
  const text = typeof value === 'string' ? value.trim() : '';
  return text.length > 0 && !looksRaw(text) ? text : null;
}

/**
 * The timeline seeder's refusal: a 500 whose decoded body names the rule it broke and the violations
 * that broke it. Null for every other error or body, so callers keep their generic sentence for them.
 */
export function seedRule(error: unknown): SeedRule | null {
  if (!(error instanceof ApiError) || error.status !== 500) return null;
  const body = error.body;
  if (typeof body !== 'object' || body === null) return null;
  const fields = body as Record<string, unknown>;
  const message = plainText(fields.message);
  const violations = Array.isArray(fields.violations) ? fields.violations : [];
  if (!message || violations.length === 0) return null;
  return {
    message,
    violations: violations.map((violation) => {
      const parts = typeof violation === 'object' && violation !== null ? (violation as Record<string, unknown>) : {};
      return { path: plainText(parts.path), message: plainText(parts.message) };
    }),
  };
}

/**
 * The seed rule as one sentence, with the first violation's path and message appended when they
 * survived cleaning; null when `error` is not a seed-invalid 500. The direct-open page shows this
 * instead of its generic sentence.
 */
export function seedRuleSentence(error: unknown, doc?: EditTimeline | null): string | null {
  const rule = seedRule(error);
  if (!rule) return null;
  const head = clean(rule.message, doc);
  const first = rule.violations.find((v) => v.path !== null || v.message !== null);
  if (!first) return head;
  const detail = first.path && first.message ? `${first.path}: ${first.message}` : (first.path ?? first.message);
  return detail ? `${head} (${detail})` : head;
}

/**
 * The sentence to show for `error`. `fallback` is the caller's own plain sentence ("The timeline could not be
 * renamed.") for anything that is not safe to show; `doc`, when the caller has it, turns ids into names.
 */
export function humanizeError(error: unknown, fallback: string, doc?: EditTimeline | null): string {
  if (error instanceof ApiError) {
    const coded = error.code ? CODED_SENTENCES[error.code] : undefined;
    if (coded) return coded;
    const seed = seedRule(error);
    if (seed) return clean(seed.message, doc);
    const text = error.message;
    if (error.status >= 400 && !looksRaw(text) && error.status < 500) return clean(text, doc);
    return statusSentence(error.status) ?? fallback;
  }
  if (error instanceof Error || typeof error === 'string') {
    const text = typeof error === 'string' ? error : error.message;
    if (NETWORK.test(text)) return 'Could not reach the server. Check your connection and try again.';
    if (looksRaw(text)) return fallback;
    return clean(text, doc);
  }
  return fallback;
}

function clean(text: string, doc?: EditTimeline | null): string {
  if (GUID.test(text) || OP_PREFIX.test(text)) return refusalSentence(text, doc);
  return sentenceCase(text);
}
