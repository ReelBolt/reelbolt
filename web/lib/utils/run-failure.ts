import type { T } from '@/i18n/translator';
import { failureSentence, isOldLimitText, MAX_PLAIN_FAILURE_CHARS } from '@/components/workflow-apps/run-model';
import { describeFailure } from '@/lib/api/describe-error';
import { enT } from '@/lib/i18n-default';

/** Internal engine wording: "Step N (Type) failed ...", "Interrupted: engine instance lost ...". */
const RAW_FAILURE = /^(?:Step\s+\d+\s*\(|Interrupted:)/i;

export interface RunFailureText {
  /** One plain sentence for the banner. */
  summary: string;
  /** The raw text, kept behind a "Details" disclosure; null when the message is already plain. */
  details: string | null;
}

/** "1 attempt", "2 attempts". */
export function pluralAttempts(n: number, t: T = enT): string {
  return t('libLabels.run.attempts', { count: n });
}

/** Fixes "1 attempts" in any text the engine stored. */
export function fixAttemptWording(text: string): string {
  return text.replace(/\b1 attempts\b/g, '1 attempt');
}

/**
 * The failure message in plain language. A message the engine already wrote for people (engine PR
 * #688 stores one in `errorMessage`) is shown as is; the old raw "Step N (Type) failed after ..."
 * text is replaced by a sentence for the failure code, with the raw text kept as details.
 */
export function runFailureText(errorMessage: string | null | undefined, failureCode?: string | null, t?: T): RunFailureText {
  // With a translator the sentence comes from the `failures` catalogue in the active language.
  // A code with a catalogue sentence keeps it (translated); a code without one (e.g. NARRATION_DOES_NOT_FIT)
  // shows the stored plain message, which is more specific than any generic sentence.
  const sentence = () => (t ? describeFailure(t, failureCode) : failureSentence(failureCode));
  const raw = errorMessage?.trim() ?? '';
  if (!raw) return { summary: sentence(), details: null };
  // An old row that still speaks in bytes and setting names: the plain sentence for the code, never the stored text.
  if (isOldLimitText(raw)) {
    const code = failureCode ?? 'INPUT_TOO_LARGE';
    return { summary: t ? describeFailure(t, code) : failureSentence(code), details: null };
  }
  if (RAW_FAILURE.test(raw) || raw.length > MAX_PLAIN_FAILURE_CHARS) {
    return { summary: sentence(), details: fixAttemptWording(raw) };
  }
  return { summary: fixAttemptWording(raw), details: failureCode ? (t ?? enT)('libLabels.run.code', { code: failureCode }) : null };
}
