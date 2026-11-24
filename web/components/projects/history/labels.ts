/**
 * Labels and small lookups for the project History tab.
 *
 * Everything user-facing is a catalogue `Msg`, so this module holds no English text: the component
 * resolves it with `useT().msg(...)`. The catalogue namespace is `projectHistory`.
 */
import { msg, type Msg } from '@/i18n/msg';
import type {
  ProjectHistoryChange,
  ProjectHistoryItemChange,
  ProjectHistoryVersion,
} from '@/lib/types/project-history';

export const DAY_MS = 86_400_000;

const CAUSE: Record<string, Msg> = {
  named: msg('projectHistory.cause.named'),
  auto: msg('projectHistory.cause.auto'),
  beforeRun: msg('projectHistory.cause.beforeRun'),
  beforeAssistant: msg('projectHistory.cause.beforeAssistant'),
  beforeRestore: msg('projectHistory.cause.beforeRestore'),
  idle: msg('projectHistory.cause.idle'),
};

/** The plain-language cause label ("Saved by you", "Before a run", ...). */
export function causeMessage(cause: string | null | undefined): Msg {
  return CAUSE[cause ?? ''] ?? CAUSE.auto;
}

/** What a row's title says: its own name when it has one, otherwise the cause label. */
export function versionTitle(
  version: Pick<ProjectHistoryVersion, 'name' | 'cause'>,
  translate: (m: Msg) => string,
): string {
  const name = version.name?.trim();
  return name ? name : translate(causeMessage(version.cause));
}

export type ExpiryDescriptor =
  | { kind: 'kept' }
  | { kind: 'days'; days: number }
  | { kind: 'expired' };

/** "kept until you delete it" versus "expires in N days" (whole days, rounded up). */
export function expiryDescriptor(expiresAt: string | null | undefined, now = Date.now()): ExpiryDescriptor {
  if (!expiresAt) return { kind: 'kept' };
  const at = Date.parse(expiresAt);
  if (!Number.isFinite(at)) return { kind: 'kept' };
  if (at <= now) return { kind: 'expired' };
  return { kind: 'days', days: Math.ceil((at - now) / DAY_MS) };
}

/** The resolved-retention sentence for a row. */
export function expiryMessage(expiresAt: string | null | undefined, now = Date.now()): Msg {
  const expiry = expiryDescriptor(expiresAt, now);
  if (expiry.kind === 'kept') return msg('projectHistory.expiry.kept');
  if (expiry.kind === 'expired') return msg('projectHistory.expiry.expired');
  return msg('projectHistory.expiry.days', { count: expiry.days });
}

const KIND: Record<string, Msg> = {
  Project: msg('projectHistory.kind.Project'),
  File: msg('projectHistory.kind.File'),
  Workflow: msg('projectHistory.kind.Workflow'),
  Timeline: msg('projectHistory.kind.Timeline'),
  DesignSystem: msg('projectHistory.kind.DesignSystem'),
  Pronunciation: msg('projectHistory.kind.Pronunciation'),
};

/** The display order of the grouped change list. */
export const KIND_ORDER = ['Project', 'File', 'Workflow', 'Timeline', 'DesignSystem', 'Pronunciation'] as const;

export function kindMessage(kind: string | null | undefined): Msg {
  return KIND[kind ?? ''] ?? msg('projectHistory.kind.other');
}

export interface ChangeGroup {
  kind: string;
  changes: ProjectHistoryItemChange[];
}

/** Groups a change list by item kind, in the tab's reading order; unknown kinds follow. */
export function groupChangesByKind(changes: readonly ProjectHistoryItemChange[]): ChangeGroup[] {
  const groups = new Map<string, ProjectHistoryItemChange[]>();
  for (const change of changes) {
    const list = groups.get(change.kind);
    if (list) list.push(change);
    else groups.set(change.kind, [change]);
  }
  const known = KIND_ORDER.filter((kind) => groups.has(kind));
  const unknown = [...groups.keys()].filter((kind) => !(KIND_ORDER as readonly string[]).includes(kind));
  return [...known, ...unknown].map((kind) => ({ kind, changes: groups.get(kind) ?? [] }));
}

const CHANGE: Record<ProjectHistoryChange, Msg> = {
  added: msg('projectHistory.change.added'),
  removed: msg('projectHistory.change.removed'),
  changed: msg('projectHistory.change.changed'),
  moved: msg('projectHistory.change.moved'),
  changedAndMoved: msg('projectHistory.change.changedAndMoved'),
};

/** The badge text for a change; the badge also carries a colour (never colour alone). */
export function changeMessage(change: string | null | undefined): Msg {
  return CHANGE[(change ?? '') as ProjectHistoryChange] ?? msg('projectHistory.change.changed');
}

/** The Mantine colour paired with each change label. */
export const CHANGE_COLORS: Record<string, string> = {
  added: 'green',
  removed: 'red',
  changed: 'blue',
  moved: 'orange',
  changedAndMoved: 'grape',
};

const REASON: Record<string, Msg> = {
  // Why an item of a saved version cannot be restored (contract section 5).
  CONTENT_GONE: msg('projectHistory.reason.contentGone'),
  ERASED: msg('projectHistory.reason.erased'),
  QUARANTINED: msg('projectHistory.reason.quarantined'),
  QUARANTINE_MARKER: msg('projectHistory.reason.quarantineMarker'),
  OUTPUT_HELD: msg('projectHistory.reason.outputHeld'),
  DESIGN_VERSION_GONE: msg('projectHistory.reason.designVersionGone'),
  UNVERIFIED: msg('projectHistory.reason.unverified'),
  NOT_YET_SUPPORTED: msg('projectHistory.reason.notYetSupported'),
  // Why a restore skips an item (contract section 8).
  PATH_TAKEN: msg('projectHistory.reason.pathTaken'),
  STORAGE_QUOTA_EXCEEDED: msg('projectHistory.reason.storageQuotaExceeded'),
  ITEM_NOT_FOUND: msg('projectHistory.reason.itemNotFound'),
  ALREADY_CURRENT: msg('projectHistory.reason.alreadyCurrent'),
  DESIGN_VERSION_INVALID: msg('projectHistory.reason.designVersionInvalid'),
  WORKFLOW_INVALID: msg('projectHistory.reason.workflowInvalid'),
  WORKFLOW_RUN_ACTIVE: msg('projectHistory.reason.workflowRunActive'),
  TIMELINE_INVALID: msg('projectHistory.reason.timelineInvalid'),
  // Server-side validation and skipped items (contract sections 5 and 8).
  INVALID_NAME: msg('projectHistory.reason.invalidName'),
  INVALID_SCOPE: msg('projectHistory.reason.invalidScope'),
  INVALID_ITEMS: msg('projectHistory.reason.invalidItems'),
  INVALID_ITEM_KIND: msg('projectHistory.reason.invalidItemKind'),
  INVALID_TARGET: msg('projectHistory.reason.invalidTarget'),
  NOTHING_TO_RESTORE: msg('projectHistory.reason.nothingToRestore'),
  RESTORE_FAILED: msg('projectHistory.reason.restoreFailed'),
  PRONUNCIATION_ENTRY_SKIPPED: msg('projectHistory.reason.pronunciationEntrySkipped'),
  // A notice attached to an item that was restored (contract section 8 step 7).
  EGRESS_CONSENT_RESET: msg('projectHistory.reason.egressConsentReset'),
  // Refusals the whole restore can hit (sections 5 and 8).
  PROJECT_TOO_LARGE_TO_VERSION: msg('projectHistory.reason.projectTooLarge'),
  NAMED_VERSION_LIMIT: msg('projectHistory.reason.namedVersionLimit'),
  PROJECT_RUN_ACTIVE: msg('projectHistory.reason.projectRunActive'),
  RESTORE_IN_PROGRESS: msg('projectHistory.reason.restoreInProgress'),
  FILE_IN_USE_BY_AVATAR: msg('projectHistory.reason.fileInUseByAvatar'),
};

/**
 * Every reason/notice code the API can return (`ProjectHistoryRestoreReasons` and
 * `ProjectHistoryRestoreNotices` in `Services/History/ProjectHistoryQueries.cs`). The test walks this
 * list so a new server code cannot reach the UI without a plain-language sentence.
 */
export const RESTORE_REASON_CODES = [
  'CONTENT_GONE',
  'ERASED',
  'QUARANTINED',
  'QUARANTINE_MARKER',
  'OUTPUT_HELD',
  'DESIGN_VERSION_GONE',
  'UNVERIFIED',
  'NOT_YET_SUPPORTED',
  'PATH_TAKEN',
  'STORAGE_QUOTA_EXCEEDED',
  'ITEM_NOT_FOUND',
  'ALREADY_CURRENT',
  'DESIGN_VERSION_INVALID',
  'WORKFLOW_INVALID',
  'WORKFLOW_RUN_ACTIVE',
  'TIMELINE_INVALID',
  'EGRESS_CONSENT_RESET',
  'NOTHING_TO_RESTORE',
  'RESTORE_FAILED',
  'PRONUNCIATION_ENTRY_SKIPPED',
  'FILE_IN_USE_BY_AVATAR',
] as const;

/** The plain sentence behind a block/skip/refusal code; a generic sentence for an unknown code. */
export function reasonMessage(code: string | null | undefined): Msg {
  return REASON[code ?? ''] ?? msg('projectHistory.reason.generic');
}

/**
 * The codes a save-version request can be refused with. Unlike a restore reason, an unknown code
 * here must fall back to the save sentence ("Could not save this version"), never to
 * "This item cannot be restored".
 */
const SAVE_CODES = new Set([
  'INVALID_NAME',
  'INVALID_SCOPE',
  'INVALID_ITEMS',
  'INVALID_ITEM_KIND',
  'INVALID_TARGET',
  'PROJECT_TOO_LARGE_TO_VERSION',
  'STORAGE_QUOTA_EXCEEDED',
  'NAMED_VERSION_LIMIT',
]);

/** The sentence for a failed save: the server's validation code when it is one, else the save fallback. */
export function saveErrorMessage(code: string | null | undefined): Msg {
  return code && SAVE_CODES.has(code) ? reasonMessage(code) : msg('projectHistory.saveFailed');
}

/** The refusal code of a thrown API error, when it carries one (`ApiError.code`). */
export function refusalCodeOf(error: unknown): string | null {
  if (typeof error !== 'object' || error === null) return null;
  const code = (error as { code?: unknown }).code;
  return typeof code === 'string' && code.trim() ? code : null;
}

/** The reason code to show for a failed preview/restore/save: its own code, else one implied by the status. */
export function errorReasonCode(error: unknown): string | null {
  const code = refusalCodeOf(error);
  if (code) return code;
  const status = typeof error === 'object' && error !== null ? (error as { status?: unknown }).status : null;
  if (status === 413) return 'PROJECT_TOO_LARGE_TO_VERSION';
  if (status === 402) return 'STORAGE_QUOTA_EXCEEDED';
  return null;
}
