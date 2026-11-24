import { ApiError } from '@/lib/api/client';
import type { T } from '@/i18n/translator';

/** What the "Request changes" panel shows when the server refuses a change request. */
export interface RevisionRefusal {
  title: string;
  message: string;
  color: 'red' | 'orange' | 'yellow';
  /** True for the 402 shortfall: the alert also links to the top-up page. */
  topUp: boolean;
}

function bodyField(error: ApiError, field: string): unknown {
  const body = error.body;
  return typeof body === 'object' && body !== null ? (body as Record<string, unknown>)[field] : undefined;
}

/** "about 1 minute" / "about 4 minutes" from a `retryAfterSeconds` figure. */
export function retryAfterPhrase(seconds: number): string {
  const minutes = Math.max(1, Math.ceil(seconds / 60));
  return `about ${minutes} minute${minutes === 1 ? '' : 's'}`;
}

/**
 * Maps a refused `POST .../revisions` to an inline alert. The server's own sentence (`message`) is
 * the body whenever it sent one: it is already written for the customer, and it is the only part
 * that knows the specifics (which version, how long to wait).
 */
/**
 * `t` is the `revisions` translator. Without it the English fallback is used: that path is only for
 * callers outside this area (change requests) that have not moved to the catalogue yet.
 */
export function describeRevisionError(error: unknown, t?: T): RevisionRefusal {
  const say = (key: string, english: string, values?: Record<string, number>) => (t ? t(`errors.${key}`, values) : english);
  if (!(error instanceof ApiError)) {
    return {
      title: say('genericTitle', 'Could not request changes'),
      message: say('genericBody', 'Something went wrong while sending your request. Please try again.'),
      color: 'red',
      topUp: false,
    };
  }
  const code = bodyField(error, 'code');
  const message = error.message;

  switch (error.status) {
    case 400:
      return { title: say('checkTitle', 'Check your request'), message: message || say('checkBody', 'Describe what should change.'), color: 'orange', topUp: false };
    case 402:
      return {
        title: say('creditsTitle', 'Not enough credits'),
        message: message || say('creditsBody', 'You do not have enough credits for this change. Nothing was started and nothing was charged.'),
        color: 'orange',
        topUp: true,
      };
    case 403:
      return {
        title: say('emailTitle', 'Confirm your email address'),
        message: message || say('emailBody', 'Confirm your email address to request changes.'),
        color: 'yellow',
        topUp: false,
      };
    case 404:
      return { title: say('notFoundTitle', 'Video not found'), message: message || say('notFoundBody', 'This video may have been deleted.'), color: 'red', topUp: false };
    case 409:
      return {
        title: code === 'RUN_NOT_FINISHED' ? say('notFinishedTitle', 'This run has not finished yet') : say('cannotChangeTitle', 'This video cannot be changed'),
        message: message || (code === 'RUN_NOT_FINISHED'
          ? say('notFinishedBody', 'You can request changes once this run has finished.')
          : say('cannotChangeBody', 'We could not pick up this video where it left off.')),
        color: 'orange',
        topUp: false,
      };
    case 429: {
      const retryAfter = bodyField(error, 'retryAfterSeconds');
      const wait = typeof retryAfter === 'number' && Number.isFinite(retryAfter) && retryAfter > 0
        ? ` ${t ? t('errors.tryAgainIn', { minutes: Math.max(1, Math.ceil(retryAfter / 60)) }) : `You can try again in ${retryAfterPhrase(retryAfter)}.`}`
        : '';
      return {
        title: say('tooManyTitle', 'Too many change requests'),
        message: `${message || say('tooManyBody', 'You have asked for a lot of changes in a short time.')}${wait}`,
        color: 'yellow',
        topUp: false,
      };
    }
    default:
      return { title: say('genericTitle', 'Could not request changes'), message: message || say('tryAgain', 'Please try again.'), color: 'red', topUp: false };
  }
}
