import { apiFetch } from './client';
import type {
  AssistantActivity,
  AssistantApprovalDecision,
  AssistantApprovalMode,
  AssistantPreferences,
  AssistantThreadDetail,
  AssistantThreadSummary,
  AssistantTurnStatus,
} from '../types/assistant';

/**
 * The assistant's chat transport is context-first: the caller's project/workflow travel in the
 * request BODY, not in the path, because the assistant is mounted once for the whole platform and
 * a page may have no project at all. The old `getAssistantChatStreamUrl(projectId)` threw on a
 * non-GUID and could not be called from a project-less page.
 */
export function getAssistantChatStreamUrl(): string {
  return '/api/v1/assistant/chat';
}

/**
 * Caller-scoped, not project-scoped: the conversation list belongs to the signed-in user, and the
 * assistant is mountable from a page with no project to hang it off.
 */
const ASSISTANT_THREADS_URL = '/api/v1/assistant/threads';

/** The caller's non-archived threads, most recently updated first. */
export function listThreads(): Promise<AssistantThreadSummary[]> {
  return apiFetch<AssistantThreadSummary[]>(ASSISTANT_THREADS_URL);
}

/**
 * Creates a thread and returns its id. Both body fields are optional, so a thread can be opened
 * from a page that has no project context; an absent field is omitted rather than sent as null.
 */
export function createThread(input?: { projectId?: string; title?: string }): Promise<{ id: string }> {
  const body: { projectId?: string; title?: string } = {};
  // Only an id-shaped value: anything else makes the API refuse the whole request with 400.
  if (input?.projectId && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(input.projectId)) body.projectId = input.projectId;
  if (input?.title) body.title = input.title;
  return apiFetch<{ id: string }>(ASSISTANT_THREADS_URL, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

/** One thread with its full, ordered history. */
export function getThread(id: string): Promise<AssistantThreadDetail> {
  return apiFetch<AssistantThreadDetail>(`${ASSISTANT_THREADS_URL}/${id}`);
}

/**
 * Where the thread's most recent turn stands, so a reopening chat can tell an idle thread from one
 * whose turn is still running and re-attach to it. `null` — never a rejection — when the server has
 * nothing to say: a `404` for a thread the caller does not own or one this process forgot after a
 * restart, or a transport failure. Both mean the same thing to the caller, "there is no live turn",
 * and the stored history is the answer — not an error state.
 */
export async function getTurnStatus(id: string): Promise<AssistantTurnStatus | null> {
  try {
    return await apiFetch<AssistantTurnStatus>(`${ASSISTANT_THREADS_URL}/${id}/turn`);
  } catch {
    return null;
  }
}

/** Renames a thread. `{ title }` is the whole body — the endpoint leaves omitted fields alone. */
export function renameThread(id: string, title: string): Promise<void> {
  return apiFetch<void>(`${ASSISTANT_THREADS_URL}/${id}`, {
    method: 'PATCH',
    body: JSON.stringify({ title }),
  });
}

export function deleteThread(id: string): Promise<void> {
  return apiFetch<void>(`${ASSISTANT_THREADS_URL}/${id}`, { method: 'DELETE' });
}

/** The SWR key and URL of the caller's assistant preferences. */
export const ASSISTANT_PREFERENCES_URL = '/api/v1/assistant/preferences';

/** Anything but an explicit `auto` is `ask`: a missing or unknown value must never switch checks off. */
function toApprovalMode(value: unknown): AssistantApprovalMode {
  return value === 'auto' ? 'auto' : 'ask';
}

/** Whether the assistant asks before it changes things (`ask`, the default) or just does it (`auto`). */
export async function getAssistantPreferences(): Promise<AssistantPreferences> {
  const stored = await apiFetch<Partial<AssistantPreferences> | null>(ASSISTANT_PREFERENCES_URL);
  return withLimit(stored);
}

/** Saves the mode and answers with what the server stored, read back from its own row. */
export async function setAssistantPreferences(next: AssistantPreferences): Promise<AssistantPreferences> {
  const stored = await apiFetch<Partial<AssistantPreferences> | null>(ASSISTANT_PREFERENCES_URL, {
    method: 'PUT',
    body: JSON.stringify({ approvalMode: next.approvalMode }),
  });
  return withLimit(stored);
}

function withLimit(stored: Partial<AssistantPreferences> | null): AssistantPreferences {
  const limit = stored?.autoApproveCostLimitCredits;
  return {
    approvalMode: toApprovalMode(stored?.approvalMode),
    ...(typeof limit === 'number' && limit > 0 ? { autoApproveCostLimitCredits: limit } : {}),
  };
}

/**
 * Answers a permission request. `alwaysAllow` with `allow` also switches the caller to auto mode
 * before the change runs. Resolves on `204`; `404` means the request is gone (answered, finished or
 * not theirs) and `409` that it was already decided — both surface as an `ApiError`.
 */
export function respondToApproval(
  id: string,
  decision: AssistantApprovalDecision,
  options?: { alwaysAllow?: boolean },
): Promise<void> {
  const body: { decision: AssistantApprovalDecision; alwaysAllow?: boolean } = { decision };
  if (options?.alwaysAllow) body.alwaysAllow = true;
  return apiFetch<void>(`/api/v1/assistant/approvals/${encodeURIComponent(id)}`, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

/** What the assistant is writing and which replies are unread, across all of the caller's threads. */
export async function getAssistantActivity(): Promise<AssistantActivity> {
  const raw = await apiFetch<Partial<AssistantActivity> | null>('/api/v1/assistant/activity');
  return {
    writing: Array.isArray(raw?.writing) ? raw.writing : [],
    unread: Array.isArray(raw?.unread) ? raw.unread : [],
    pendingWatches: typeof raw?.pendingWatches === 'number' ? raw.pendingWatches : 0,
  };
}

/** Marks a thread's replies as read, up to `throughSequence` when given, else all of them. */
export function markThreadRead(id: string, throughSequence?: number): Promise<void> {
  return apiFetch<void>(`${ASSISTANT_THREADS_URL}/${id}/read`, {
    method: 'POST',
    body: JSON.stringify(throughSequence === undefined ? {} : { throughSequence }),
  });
}
