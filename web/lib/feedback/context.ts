import type { T } from '@/i18n/translator';
import { enT } from '@/lib/i18n-default';
import { isGuid, resolvePageInfo } from '@/components/assistant/pageInfo';
import type { AssistantThreadDetail } from '@/lib/types/assistant';

/**
 * The feedback channel's client half (T-019).
 *
 * The context a report carries is assembled here, from what the user chose to attach and nothing
 * else. The server never enriches it (see `FeedbackService` on the Inference API), so this module is
 * where the minimisation promise is actually made: a field that is not in `FeedbackCandidates`, or
 * that the user switched off in `FeedbackSelection`, is absent from the payload that leaves the
 * browser. `assembleFeedbackPayload` is pure and is asserted directly in `context.test.ts` — the
 * ticket asks for the assembled payload, not the rendered form.
 */

export type FeedbackKind = 'bug' | 'feedback';

/** One assistant turn the user may attach. Only these two roles are ever attached. */
export interface FeedbackTurn {
  role: 'user' | 'assistant';
  content: string;
}

/** Everything the current page could offer to attach. A field that is absent cannot be attached. */
export interface FeedbackCandidates {
  page?: string;
  projectId?: string;
  executionId?: string;
  assistantTurns?: FeedbackTurn[];
}

/** Which of the present candidates the user has chosen to attach. */
export interface FeedbackSelection {
  page: boolean;
  projectId: boolean;
  executionId: boolean;
  assistantTurns: boolean;
  /** Opt-in (off by default): the shell diagnostics report, collected when the box is ticked (and shown in the preview). */
  diagnostics?: boolean;
}

/** The `context` object that goes on the wire: only the attached fields. */
export interface FeedbackContext {
  page?: string;
  projectId?: string;
  executionId?: string;
  assistantTurns?: FeedbackTurn[];
  /** Shell diagnostics object (<= MAX_DIAGNOSTICS_BYTES serialized); only when the user ticked the box. */
  diagnostics?: Record<string, unknown>;
}

export interface FeedbackPayload {
  kind: FeedbackKind;
  message: string;
  context: FeedbackContext;
}

/** Matches `FeedbackService.MaxMessageLength` on the server. */
export const MAX_MESSAGE_LENGTH = 4000;

/** Matches `FeedbackService.MaxTurns` on the server. */
export const MAX_TURNS = 20;

/** How many of the newest assistant turns the launcher offers by default. */
export const DEFAULT_TURN_COUNT = 6;

/** The one field key, in display order. */
export type FeedbackFieldKey = Exclude<keyof FeedbackSelection, 'diagnostics'>;

/**
 * English field labels; components use `feedbackFieldLabel(t, key)`.
 * @deprecated Use `feedbackFieldLabel(t, key)` (translator-taking) so the text follows the active language.
 */
export const FEEDBACK_FIELD_LABELS: Record<FeedbackFieldKey, string> = {
  page: enT('libLabels.feedbackFields.page'),
  projectId: enT('libLabels.feedbackFields.projectId'),
  executionId: enT('libLabels.feedbackFields.executionId'),
  assistantTurns: enT('libLabels.feedbackFields.assistantTurns')
};

export function feedbackFieldLabel(t: T, key: FeedbackFieldKey): string {
  return t(`libLabels.feedbackFields.${key}`);
}

/**
 * The page-derived candidates: the route path, and the project/execution ids the route names. Ids
 * are only taken when they have the shape of an id, so a path segment can never be sent as one.
 */
export function candidatesFromPath(
  pathname: string | null | undefined,
  search?: string | null
): FeedbackCandidates {
  const info = resolvePageInfo(pathname, search);
  const candidates: FeedbackCandidates = { page: info.path };
  if (isGuid(info.entityIds.projectId)) candidates.projectId = info.entityIds.projectId;
  if (isGuid(info.entityIds.executionId)) candidates.executionId = info.entityIds.executionId;
  return candidates;
}

/**
 * The newest user/assistant turns of a thread, oldest first, capped. A `system`/`tool` message is
 * never attached: it is not the user's conversation.
 */
export function lastAssistantTurns(
  detail: AssistantThreadDetail | null | undefined,
  max: number = DEFAULT_TURN_COUNT
): FeedbackTurn[] {
  if (!detail?.messages?.length) return [];
  const turns: FeedbackTurn[] = [];
  for (const message of detail.messages) {
    const role = message.role === 'user' ? 'user' : message.role === 'assistant' ? 'assistant' : null;
    if (!role) continue;
    turns.push({ role, content: message.content ?? '' });
  }
  return turns.slice(-max);
}

/** Default: attach everything the page offered. The user can switch any of it off before sending. */
export function defaultSelection(candidates: FeedbackCandidates): FeedbackSelection {
  return {
    page: !!candidates.page,
    projectId: !!candidates.projectId,
    executionId: !!candidates.executionId,
    assistantTurns: !!candidates.assistantTurns?.length
  };
}

/** The candidates that are actually present, so the dialog renders only what could be attached. */
export function presentFields(candidates: FeedbackCandidates): FeedbackFieldKey[] {
  const fields: FeedbackFieldKey[] = [];
  if (candidates.page) fields.push('page');
  if (candidates.projectId) fields.push('projectId');
  if (candidates.executionId) fields.push('executionId');
  if (candidates.assistantTurns?.length) fields.push('assistantTurns');
  return fields;
}

/** A one-line, human-readable preview of one candidate field. */
export function previewOf(candidates: FeedbackCandidates, key: FeedbackFieldKey): string {
  switch (key) {
    case 'page':
      return candidates.page ?? '';
    case 'projectId':
      return candidates.projectId ?? '';
    case 'executionId':
      return candidates.executionId ?? '';
    case 'assistantTurns': {
      const turns = candidates.assistantTurns ?? [];
      return `${turns.length} turn${turns.length === 1 ? '' : 's'}`;
    }
  }
}

/**
 * Builds the request body from what the user attached. This is the single place the payload is
 * assembled, so the minimisation rule is stated once: a field is included only when it is both
 * present and selected.
 */
export function assembleFeedbackPayload(
  kind: FeedbackKind,
  message: string,
  candidates: FeedbackCandidates,
  selection: FeedbackSelection,
  diagnostics?: Record<string, unknown>
): FeedbackPayload {
  const context: FeedbackContext = {};
  if (selection.page && candidates.page) context.page = candidates.page;
  if (selection.projectId && candidates.projectId) context.projectId = candidates.projectId;
  if (selection.executionId && candidates.executionId) context.executionId = candidates.executionId;
  if (selection.assistantTurns && candidates.assistantTurns?.length) {
    context.assistantTurns = candidates.assistantTurns.slice(0, MAX_TURNS);
  }
  if (selection.diagnostics && diagnostics) context.diagnostics = diagnostics;
  return { kind, message: message.trim(), context };
}
