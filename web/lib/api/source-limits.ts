import { ApiError } from './client';
import type { T } from '@/i18n/translator';
import { enT } from '../i18n-default';

/**
 * The run gate's footage refusals (422 `SOURCE_TOO_LARGE` / `SOURCE_TOO_LONG`): a video the platform cannot
 * edit, refused before anything is spent. The body carries the file name and whole-GB / whole-minute figures
 * so the sentence is written here, in the active language, never as the server's English.
 */
export const SOURCE_TOO_LARGE = 'SOURCE_TOO_LARGE';
export const SOURCE_TOO_LONG = 'SOURCE_TOO_LONG';
/** 429: the organization has had several refunded too-demanding runs today; a heavy source is refused before credits. */
export const TOO_DEMANDING_LIMIT = 'TOO_DEMANDING_LIMIT';

function num(body: Record<string, unknown>, key: string): number | null {
  const v = body[key];
  return typeof v === 'number' && Number.isFinite(v) && v >= 0 ? v : null;
}

/** One too-big or too-long video, as the lint check and the run gate report it. */
export interface SourceProblem {
  code: string;
  fileName?: string | null;
  limitGb?: number | null;
  limitMinutes?: number | null;
  fileGb?: number | null;
  fileMinutes?: number | null;
}

/** The sentence for a footage problem; the same words on the run page's readiness list and on a refused Run. */
export function sourceProblemMessage(problem: SourceProblem, t: T = enT): string {
  if (problem.code === TOO_DEMANDING_LIMIT) return t('errors.tooDemandingLimit');
  const file = problem.fileName?.trim() || '';
  if (problem.code === SOURCE_TOO_LARGE) {
    if (!file || problem.limitGb == null || problem.fileGb == null) return t('errors.sourceLimitFallbackLarge');
    return t('errors.sourceTooLarge', { file, size: problem.fileGb, limit: problem.limitGb });
  }
  if (!file || problem.limitMinutes == null || problem.fileMinutes == null) return t('errors.sourceLimitFallbackLong');
  return t('errors.sourceTooLong', { file, minutes: problem.fileMinutes, limit: problem.limitMinutes });
}

/** The footage problem a refused run start carries, or null when the error is something else. */
export function sourceProblemOf(error: unknown): SourceProblem | null {
  if (!(error instanceof ApiError) || (error.code !== SOURCE_TOO_LARGE && error.code !== SOURCE_TOO_LONG && error.code !== TOO_DEMANDING_LIMIT)) return null;
  const body = typeof error.body === 'object' && error.body !== null ? (error.body as Record<string, unknown>) : {};
  return {
    code: error.code,
    fileName: typeof body.fileName === 'string' ? body.fileName : null,
    limitGb: num(body, 'limitGb'),
    limitMinutes: num(body, 'limitMinutes'),
    fileGb: num(body, 'fileGb'),
    fileMinutes: num(body, 'fileMinutes'),
  };
}

/** The plain sentence for a refused run start that was about the footage, else null. */
export function sourceRefusalMessage(error: unknown, t: T = enT): string | null {
  const problem = sourceProblemOf(error);
  return problem ? sourceProblemMessage(problem, t) : null;
}
