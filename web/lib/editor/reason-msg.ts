import type { Msg } from '@/i18n/msg';
import { sentenceMsg } from '@/lib/editing/sentence';
import { REASON_TABLES } from '@/lib/editing/reason-tables';

/** validateTextContent(c) returns English problems; show each one as `textProblemMsg(problem)` (unknown text shows as it is). */
export const textProblemMsg = (problem: string): Msg => sentenceMsg(REASON_TABLES.textProblems, problem);

/** plainGraphicError(error): the lib's own empty/infrastructure cases become keys; any other run message shows as it is. */
export function plainGraphicErrorMsg(error: string | null | undefined): Msg {
  const text = (error ?? '').trim();
  if (!text) return { $msg: 'editor-lib.graphicPanel.error.empty' };
  if (/[a-z0-9-]+:\d{2,5}\b|temporarily unavailable|connection refused|econnrefused|name or service not known|no such host|socket|http ?request|timed out|timeout/i.test(text)) {
    return { $msg: 'editor-lib.graphicPanel.error.infrastructure' };
  }
  return { $msg: 'editor-lib.common.raw', values: { text } };
}
