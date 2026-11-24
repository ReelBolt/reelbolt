/**
 * Wire types for the platform assistant's server-persisted conversations.
 *
 * Deliberately their own module rather than additions to `lib/types/workflow-assistant.ts`: that
 * file describes the workflow page's propose-then-apply payloads, and this one describes the
 * transport-level thread API. Re-exporting one from the other would make either import pull in a
 * surface it has no use for.
 *
 * Ids and timestamps are strings: ASP.NET serializes `Guid` as a string and `DateTime` as an
 * ISO-8601 string, and nothing in the web app ever needs a `Date` before it renders one.
 */

/** One row of `GET /api/v1/assistant/threads`, most recently updated first. */
export interface AssistantThreadSummary {
  id: string;
  title: string;
  /** The project the thread was opened on, when it had one. Null for a project-less page. */
  projectId: string | null;
  /** ISO-8601 timestamp of the thread's last activity. */
  updatedAt: string;
  /**
   * The newest message is the user's and nothing is answering it any more — its request died (the
   * browser left for too long, the provider failed). Shown as "No reply yet". Optional: an older
   * server omits it.
   */
  awaitingReply?: boolean;
  /** The server is still answering this thread's newest message. Shown as "Still answering…". */
  replyInProgress?: boolean;
}

/** One persisted turn. `sequence` is 1-based and unique within its thread. */
export interface AssistantThreadMessage {
  id: string;
  /** `user` / `assistant` / `system` / `tool`. Mapped to a chat role at the display boundary. */
  role: string;
  content: string;
  sequence: number;
  createdAt: string;
  /** `run_wake` marks a server-written note that a run the assistant was waiting on finished. Absent/null otherwise. */
  kind?: AssistantMessageKind | null;
  /** Present on a `run_wake` message. */
  wake?: AssistantRunWake;
}

export type AssistantMessageKind = 'run_wake';

/** How a run the assistant was waiting on ended, and its customer-facing name (e.g. "Launch teaser"). */
export interface AssistantRunWake {
  status: 'Passed' | 'Failed' | 'Cancelled';
  runLabel: string;
}

/** A run the assistant will pick up again once it finishes. */
export interface AssistantPendingRunWatch {
  runLabel: string;
  createdAt: string;
}

/** `GET /api/v1/assistant/threads/{threadId}`: the thread plus its full, ordered history. */
export interface AssistantThreadDetail {
  id: string;
  title: string;
  projectId: string | null;
  messages: AssistantThreadMessage[];
  /** The server is still answering the newest message (it may have been asked from another tab or before a reload). */
  replyInProgress?: boolean;
  /** Runs the assistant is waiting on for this thread; it answers by itself when they finish. */
  pendingRunWatches?: AssistantPendingRunWatch[];
}

/**
 * Where a thread's most recent turn stands, from `GET …/threads/{threadId}/turn`.
 *
 * `idle` means this process holds no turn for the thread — its turn never started here, it was
 * trimmed, or the process restarted (turns are process-local by design). The other four are the
 * live states of a turn the process still owns. A thread the caller does not own, or does not have,
 * is a `404` and is read by the client as "nothing to attach to".
 */
export type AssistantTurnState = 'idle' | 'running' | 'awaiting_approval' | 'completed' | 'failed';

/**
 * `GET /api/v1/assistant/threads/{threadId}/turn`. Never the turn's content — only its state and
 * the ids a reopening client needs to name. The optional fields are omitted when absent.
 */
export interface AssistantTurnStatus {
  state: AssistantTurnState;
  /** The turn to name when re-attaching. */
  turnId?: string;
  /** The assistant message the stream is keyed on. */
  messageId?: string;
  /** The open permission card, while the state is `awaiting_approval`. */
  pendingApprovalId?: string;
  /** How many runs the assistant is still waiting on for this thread. */
  pendingWatches?: number;
}

/**
 * Permission requests ("actions") and the auto-mode preference.
 *
 * The chat stream carries a `data-assistantAction` frame each time a change the assistant wants to
 * make needs the person's OK, and again as that change moves on. One id is one request; the newest
 * frame for an id wins. Titles and summaries are plain language written by the server from names it
 * read from the database — they never contain an id. Ids travel only in `refresh`, which tells the
 * app what to reload and is never shown.
 */

/** `ask`: every change waits for an OK. `auto`: changes happen straight away and are reported. */
export type AssistantApprovalMode = 'ask' | 'auto';

/** Where a request stands. `pending` is the only state a person can answer. */
export type AssistantActionState = 'pending' | 'allowed' | 'declined' | 'expired' | 'done' | 'failed';

/** What the request is about. Unknown values from a newer server are read as `other`. */
export type AssistantActionKind =
  | 'workflow'
  | 'project'
  | 'file'
  | 'style'
  | 'picture'
  | 'run'
  | 'connection'
  | 'agent'
  | 'other';

/** `destructive` cannot be undone; `paid` uses credits. Anything unknown is read as `normal`. */
export type AssistantActionRisk = 'normal' | 'destructive' | 'paid';

export interface AssistantActionChange {
  /** `info` is a neutral note; the others say what happens to the thing named in `text`. */
  kind: 'add' | 'remove' | 'change' | 'info';
  text: string;
}

/** What the app should reload once the change is made. Ids only — never shown to anyone. */
export interface AssistantActionRefresh {
  projectId?: string;
  workflowId?: string;
  documentId?: string;
}

export interface AssistantAction {
  id: string;
  state: AssistantActionState;
  mode: AssistantApprovalMode;
  kind: AssistantActionKind;
  risk: AssistantActionRisk;
  title: string;
  summary?: string;
  changes: AssistantActionChange[];
  projectName?: string;
  cost?: string;
  outcome?: string;
  /** ISO-8601; pending requests only. */
  expiresAt?: string;
  refresh?: AssistantActionRefresh;
  /** Set when auto mode still asked because the cost passed the auto-approve limit. */
  autoNote?: AssistantAutoNote;
}

/** Why a card appeared in auto mode. `costCredits` is absent when the cost could not be estimated. */
export interface AssistantAutoNote {
  limitCredits: number;
  costCredits?: number;
}

/** `GET`/`PUT /api/v1/assistant/preferences`. */
export interface AssistantPreferences {
  approvalMode: AssistantApprovalMode;
  /** Read-only, from server config: auto mode still asks above this many credits. */
  autoApproveCostLimitCredits?: number;
}

export type AssistantApprovalDecision = 'allow' | 'deny';

/** One thread the assistant is answering right now, from `GET /api/v1/assistant/activity`. */
export interface AssistantWritingItem {
  threadId: string;
  title: string;
  state: 'running' | 'awaiting_approval';
  startedAt: string;
  /** `run_wake`: the assistant picked the thread up on its own because a run finished. */
  kind: 'reply' | 'run_wake';
}

/** A reply the person has not read yet. Titles only — never the reply's text. */
export interface AssistantUnreadItem {
  threadId: string;
  title: string;
  sequence: number;
  kind: 'reply' | 'run_wake';
  at: string;
}

/** `GET /api/v1/assistant/activity`: what the assistant is doing and what is waiting to be read. */
export interface AssistantActivity {
  writing: AssistantWritingItem[];
  unread: AssistantUnreadItem[];
  pendingWatches: number;
}
