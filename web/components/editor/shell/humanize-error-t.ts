import { humanizeError } from '@/lib/editing/humanize-error';
import { humanizeErrorMsg } from '@/lib/editing/labels-msg';
import type { EditTimeline } from '@/lib/editing/types';
import type { T } from '@/i18n';

/**
 * {@link humanizeError} shown in the person's language: a known English sentence is looked up in the
 * catalogue (`humanizeErrorMsg`); the translated `fallback` and anything unknown pass through unchanged.
 */
export function humanizeErrorT(t: Pick<T, 'msg'>, error: unknown, fallback: string, doc?: EditTimeline | null): string {
  return t.msg(humanizeErrorMsg(humanizeError(error, fallback, doc)));
}
