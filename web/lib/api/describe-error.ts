import type { T } from '@/i18n/translator';
import { SESSION_ENDED_MESSAGE } from '../auth/session-expiry';
import { friendlyError } from './error-messages';
import { isNetworkFailure } from './network-error';
import { sourceRefusalMessage } from './source-limits';

const CODE_SHAPE = /^[A-Z][A-Z0-9_]*$/;

function codeOf(err: unknown): string | null {
  if (typeof err !== 'object' || err === null) return null;
  const code = (err as { code?: unknown }).code;
  return typeof code === 'string' && CODE_SHAPE.test(code.trim().toUpperCase()) ? code.trim().toUpperCase() : null;
}

function statusOf(err: unknown): number | null {
  if (typeof err !== 'object' || err === null) return null;
  const status = (err as { status?: unknown }).status;
  return typeof status === 'number' ? status : null;
}

/** The HTTP reason phrase or a ProblemDetails default title: says nothing a person can act on. */
const GENERIC_SERVER_TITLE =
  /^(?:internal server error|bad gateway|service unavailable|gateway time-?out|server error|an error occurred while processing your request\.?|<(?:!doctype|html)[\s\S]*)$/i;

/** The HTTP reason phrases of the client-error statuses the generic sentences cover. */
const GENERIC_CLIENT_TITLE = /^(?:unauthorized|forbidden|not found|conflict|too many requests|gone)\.?$/i;

function isGenericTitle(message: string, status: number | null): boolean {
  if (status === null) return false;
  if (status >= 500) return isGenericServerTitle(message);
  return [401, 403, 404, 409, 410, 429].includes(status) && GENERIC_CLIENT_TITLE.test(message);
}

function isGenericServerTitle(message: string): boolean {
  return GENERIC_SERVER_TITLE.test(message);
}

/**
 * The server's own message of a failed call, or null when it is empty or generic (an HTTP reason
 * phrase, a ProblemDetails default title, an HTML error page). t-free: for helpers that keep their
 * own fallback wording.
 */
export function readableServerMessage(err: unknown): string | null {
  if (!(err instanceof Error)) return null;
  const message = err.message.trim();
  if (!message) return null;
  return isGenericTitle(message, statusOf(err)) ? null : err.message;
}
/**
 * The sentence for a failed API call, in the active language. `t` is an UNSCOPED translator
 * (`useT()` or `await getT()`), because keys are absolute (`errors.<CODE>`).
 * Order: a mapped machine code (`errors.<CODE>`), the network case, a known server phrase
 * (wrong password, expired link ...), the server's own message (an unmapped code shows it as is),
 * the generic sentence for the HTTP status, then `fallback`, then "Something went wrong".
 */
export function describeError(t: T, err: unknown, fallback?: string): string {
  const code = codeOf(err);
  const footage = sourceRefusalMessage(err, t);
  if (footage) return footage;
  if (code && t.has(`errors.${code}`)) return t(`errors.${code}`);
  if (isNetworkFailure(err)) return t('errors.generic.network');
  const friendly = friendlyError(err, ' ', t);
  if (friendly.kind !== 'other') return friendly.message;
  const status = statusOf(err);
  const message = friendly.message.trim();
  if (message === SESSION_ENDED_MESSAGE) return t('errors.generic.unauthorized');
  if (message && !isGenericTitle(message, status)) return message;
  if (status === 401) return t('errors.generic.unauthorized');
  if (status === 403) return t('errors.generic.forbidden');
  if (status === 404) return t('errors.generic.notFound');
  if (status === 409) return t('errors.generic.conflict');
  if (status === 429) return t('errors.generic.rateLimited');
  if (status !== null && status >= 500) return t('errors.generic.server');
  return fallback ?? t('errors.generic.unknown');
}

/**
 * The plain-language sentence for a step's `failureCode`. A code without a catalogue entry shows the
 * server's own text when it is short, else the generic sentence. `t` is unscoped.
 */
export function describeFailure(t: T, code: string | null | undefined, serverText?: string | null): string {
  const key = code?.trim().toUpperCase() ?? '';
  if (CODE_SHAPE.test(key) && t.has(`failures.${key}`)) return t(`failures.${key}`);
  const text = serverText?.trim();
  if (text && text.length <= 160) return text;
  return t('failures.default');
}
