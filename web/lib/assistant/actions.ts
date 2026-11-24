/**
 * Reads the assistant's permission requests out of a chat message.
 *
 * The server sends a `data-assistantAction` frame each time a request appears or moves on, and the
 * chat runtime appends every frame to the message's `unstable_data` — it does not merge them. This
 * module is the merge: one entry per request id, the newest frame winning, in the order each id first
 * appeared. It is pure so that the rules (which frame wins, what a late frame may not undo, what is
 * read from a frame that is not quite what we expected) are pinned by tests rather than by the UI.
 *
 * Everything here is defensive on purpose: a frame came over the network, from a server that may be
 * newer or older than this page. A frame we cannot make sense of is ignored; it never throws and it
 * never takes the conversation down with it.
 */

import type { T } from '@/i18n/translator';
import { enT } from '../i18n-default';
import { plainText } from './plain-text';
import type {
  AssistantAction,
  AssistantAutoNote,
  AssistantActionChange,
  AssistantActionKind,
  AssistantActionRefresh,
  AssistantActionRisk,
  AssistantActionState,
  AssistantApprovalMode,
} from '../types/assistant';

/** The name the chat runtime files a `data-assistantAction` frame under in `unstable_data`. */
export const ASSISTANT_ACTION_DATA_NAME = 'assistantAction';

/** The progress caption the server streams while a request waits for an answer. */
export const WAITING_FOR_OK_CAPTION = 'Waiting for your OK';

/**
 * Whether a progress caption still says something true. "Waiting for your OK" is only so while a
 * request IS waiting; once it is answered the server's next caption may be a while coming, and the
 * old one would claim the assistant is waiting for something it already has.
 */
export function captionStillTrue(caption: string, actions: readonly AssistantAction[]): boolean {
  if (caption.trim().toLowerCase() !== WAITING_FOR_OK_CAPTION.toLowerCase()) return true;
  return actions.some((action) => action.state === 'pending');
}

const STATES: readonly AssistantActionState[] = ['pending', 'allowed', 'declined', 'expired', 'done', 'failed'];
const KINDS: readonly AssistantActionKind[] = [
  'workflow',
  'project',
  'file',
  'style',
  'picture',
  'run',
  'connection',
  'agent',
];
const RISKS: readonly AssistantActionRisk[] = ['normal', 'destructive', 'paid'];
const CHANGE_KINDS: readonly AssistantActionChange['kind'][] = ['add', 'remove', 'change', 'info'];

/** A request never needs more rows than this to be understood; the rest is not read. */
const MAX_CHANGES = 50;

/** How far along a request is. A frame that would move one backwards is a late arrival, not news. */
const RANK: Record<AssistantActionState, number> = {
  pending: 0,
  allowed: 1,
  declined: 2,
  expired: 2,
  done: 2,
  failed: 2,
};

/** Whether nothing more will happen to a request in this state. */
export function isSettled(state: AssistantActionState): boolean {
  return RANK[state] === 2;
}

function text(value: unknown): string | undefined {
  if (typeof value !== 'string') return undefined;
  const cleaned = plainText(value).trim();
  return cleaned.length > 0 ? cleaned : undefined;
}

function oneOf<T extends string>(value: unknown, allowed: readonly T[]): T | undefined {
  return typeof value === 'string' && (allowed as readonly string[]).includes(value) ? (value as T) : undefined;
}

function parseChanges(value: unknown): AssistantActionChange[] | undefined {
  if (!Array.isArray(value)) return undefined;
  const out: AssistantActionChange[] = [];
  for (const entry of value) {
    if (out.length >= MAX_CHANGES) break;
    if (typeof entry !== 'object' || entry === null) continue;
    const row = entry as Record<string, unknown>;
    const rowText = text(row.text);
    if (!rowText) continue;
    out.push({ kind: oneOf(row.kind, CHANGE_KINDS) ?? 'info', text: rowText });
  }
  return out;
}

function parseRefresh(value: unknown): AssistantActionRefresh | undefined {
  if (typeof value !== 'object' || value === null) return undefined;
  const raw = value as Record<string, unknown>;
  const id = (v: unknown) => (typeof v === 'string' && v.trim().length > 0 ? v : undefined);
  const refresh: AssistantActionRefresh = {};
  const projectId = id(raw.projectId);
  const workflowId = id(raw.workflowId);
  const documentId = id(raw.documentId);
  if (projectId) refresh.projectId = projectId;
  if (workflowId) refresh.workflowId = workflowId;
  if (documentId) refresh.documentId = documentId;
  return Object.keys(refresh).length > 0 ? refresh : undefined;
}

/** The fields one frame actually carried. Everything but `id` and `state` may be absent. */
interface Frame {
  id: string;
  state: AssistantActionState;
  mode?: AssistantApprovalMode;
  kind?: AssistantActionKind;
  risk?: AssistantActionRisk;
  title?: string;
  summary?: string;
  changes?: AssistantActionChange[];
  projectName?: string;
  cost?: string;
  outcome?: string;
  expiresAt?: string;
  refresh?: AssistantActionRefresh;
  autoNote?: AssistantAutoNote;
}

function parseAutoNote(raw: unknown): AssistantAutoNote | undefined {
  if (typeof raw !== 'object' || raw === null) return undefined;
  const r = raw as Record<string, unknown>;
  if (typeof r.limitCredits !== 'number' || !Number.isFinite(r.limitCredits)) return undefined;
  const note: AssistantAutoNote = { limitCredits: r.limitCredits };
  if (typeof r.costCredits === 'number' && Number.isFinite(r.costCredits)) note.costCredits = r.costCredits;
  return note;
}

function parseFrame(raw: unknown): Frame | null {
  if (typeof raw !== 'object' || raw === null) return null;
  const r = raw as Record<string, unknown>;
  const id = typeof r.id === 'string' && r.id.trim().length > 0 ? r.id : null;
  const state = oneOf(r.state, STATES);
  // A request we cannot name, or whose state we do not know, cannot be drawn honestly.
  if (!id || !state) return null;
  return {
    id,
    state,
    mode: oneOf(r.mode, ['ask', 'auto'] as const),
    kind: oneOf(r.kind, KINDS),
    risk: oneOf(r.risk, RISKS),
    title: text(r.title),
    summary: text(r.summary),
    changes: parseChanges(r.changes),
    projectName: text(r.projectName),
    cost: text(r.cost),
    outcome: text(r.outcome),
    expiresAt: typeof r.expiresAt === 'string' ? r.expiresAt : undefined,
    refresh: parseRefresh(r.refresh),
    autoNote: parseAutoNote(r.autoNote),
  };
}

/** What the card says when a request arrives with no words of its own. */
const UNTITLED = enT('assistant.actions.untitled');

function create(frame: Frame): AssistantAction {
  return {
    id: frame.id,
    state: frame.state,
    mode: frame.mode ?? 'ask',
    kind: frame.kind ?? 'other',
    risk: frame.risk ?? 'normal',
    title: frame.title ?? UNTITLED,
    summary: frame.summary,
    changes: frame.changes ?? [],
    projectName: frame.projectName,
    cost: frame.cost,
    outcome: frame.outcome,
    expiresAt: frame.expiresAt,
    refresh: frame.refresh,
    autoNote: frame.autoNote,
  };
}

/**
 * The newest frame wins, field by field: a field the newer frame leaves out keeps its earlier value,
 * so a terminal frame that only says `state` + `outcome` still has its title and its changes. A frame
 * that would move the request backwards (a `pending` after a `done`) is ignored whole.
 */
function merge(previous: AssistantAction, frame: Frame): AssistantAction {
  if (RANK[frame.state] < RANK[previous.state]) return previous;
  return {
    id: previous.id,
    state: frame.state,
    mode: frame.mode ?? previous.mode,
    kind: frame.kind ?? previous.kind,
    risk: frame.risk ?? previous.risk,
    title: frame.title ?? previous.title,
    summary: frame.summary ?? previous.summary,
    changes: frame.changes ?? previous.changes,
    projectName: frame.projectName ?? previous.projectName,
    cost: frame.cost ?? previous.cost,
    outcome: frame.outcome ?? previous.outcome,
    expiresAt: frame.expiresAt ?? previous.expiresAt,
    refresh: frame.refresh ?? previous.refresh,
    autoNote: frame.autoNote ?? previous.autoNote,
  };
}

/**
 * One entry per request in a message's `unstable_data`, in the order each request first appeared.
 * Accepts whatever the runtime holds — including nothing — and ignores every item that is not an
 * `assistantAction` frame it can read.
 */
export function reduceAssistantActions(unstableData: unknown): AssistantAction[] {
  if (!Array.isArray(unstableData)) return [];
  // A Map keeps the position of an id's FIRST frame when a later one replaces it.
  const byId = new Map<string, AssistantAction>();
  for (const item of unstableData as Array<{ name?: unknown; data?: unknown } | null>) {
    if (item?.name !== ASSISTANT_ACTION_DATA_NAME) continue;
    const frame = parseFrame(item.data);
    if (!frame) continue;
    const previous = byId.get(frame.id);
    byId.set(frame.id, previous ? merge(previous, frame) : create(frame));
  }
  return [...byId.values()];
}

/**
 * A request the stream stopped answering for. `pending` and `allowed` are the only states with
 * something still to come, and the server promises each is followed by a terminal frame unless the
 * turn itself was abandoned — so, once the message is no longer running, they are abandoned too.
 */
export function isAbandoned(action: AssistantAction, messageRunning: boolean): boolean {
  return !messageRunning && !isSettled(action.state);
}

/** The one line a settled request collapses to. */
export function settledLine(action: AssistantAction, t: T = enT): string {
  switch (action.state) {
    case 'allowed':
      return t('assistant.actions.working');
    case 'done':
      return action.outcome ?? t('assistant.actions.done', { title: actionTitle(action, t) });
    case 'declined':
      return t('assistant.actions.declined');
    case 'expired':
      return t('assistant.actions.expired');
    case 'failed':
      return action.outcome ?? t('assistant.actions.failed');
    case 'pending':
      return actionTitle(action, t);
  }
}

/** The card title; a request that arrived without one reads as a generic change. */
export function actionTitle(action: Pick<AssistantAction, 'title'>, t: T = enT): string {
  return action.title === UNTITLED ? t('assistant.actions.untitled') : action.title;
}
