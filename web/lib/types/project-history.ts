/**
 * Mirrors the Inference API project-history contract.
 *
 * Layer 1 (list / detail / changes / item diff / content) is copied field-for-field from
 * `Controllers/Dto/ProjectHistoryDtos.cs` (contract sections 5 and 7). The restore DTOs are written
 * from contract section 8 exactly (camelCase) because their C# types are built in parallel; they live
 * here in one place so the lead can reconcile the names without touching the UI.
 */
import type { DesignDiff } from './design-system';

/** The item kinds the API stores by name (`ProjectVersionItemKind`). */
export type ProjectVersionItemKind =
  | 'File'
  | 'Workflow'
  | 'Timeline'
  | 'DesignSystem'
  | 'Pronunciation'
  | 'Project';

/** Why a version exists (`ProjectVersionCause`); stored by name server-side. */
export type ProjectVersionCause =
  | 'named'
  | 'auto'
  | 'beforeRun'
  | 'beforeAssistant'
  | 'beforeRestore'
  | 'idle';

/** One version as the list shows it (no storage keys, ETags or digests ever reach the browser). */
export interface ProjectHistoryVersion {
  id: string;
  number: number;
  name: string | null;
  cause: ProjectVersionCause | string;
  createdAt: string;
  /** Null means the version is kept until deleted; otherwise the automatic-retention deadline. */
  expiresAt: string | null;
  createdByName: string | null;
  itemCount: number;
  totalBytes: number;
}

/** `GET versions` body. */
export interface ProjectHistoryVersionsResponse {
  versions: ProjectHistoryVersion[];
  /** Bytes this project's history currently holds, counted against the storage quota. */
  historyBytes: number;
  /** The automatic-retention window in days (contract: 30). */
  retentionDays: number;
  /** The caller may edit this project (save a version, restore an item), from the server's access rule. */
  canEdit: boolean;
  /** The caller may delete this project (delete a version, restore the whole project), from the server. */
  canDelete: boolean;
}

/** One item of a version; the reason is one of the contract section 5 codes when not restorable. */
export interface ProjectHistoryItem {
  kind: ProjectVersionItemKind | string;
  key: string;
  name: string | null;
  category: string | null;
  mimeType: string | null;
  sizeBytes: number;
  restorable: boolean;
  notRestorableReason: ProjectHistoryNotRestorableReason | string | null;
}

/** `GET versions/{versionId}` body. */
export interface ProjectHistoryVersionDetail extends ProjectHistoryVersion {
  items: ProjectHistoryItem[];
}

/** Why an item of a saved version can no longer be restored (contract section 5). */
export type ProjectHistoryNotRestorableReason =
  | 'CONTENT_GONE'
  | 'ERASED'
  | 'QUARANTINED'
  | 'QUARANTINE_MARKER'
  | 'OUTPUT_HELD'
  | 'DESIGN_VERSION_GONE'
  | 'UNVERIFIED'
  | 'NOT_YET_SUPPORTED';

/** The relation of one item between the version and the comparison target. */
export type ProjectHistoryChange =
  | 'added'
  | 'removed'
  | 'changed'
  | 'moved'
  | 'changedAndMoved';

/** One item that changed between two manifests. */
export interface ProjectHistoryItemChange {
  kind: ProjectVersionItemKind | string;
  key: string;
  name: string | null;
  previousName?: string | null;
  change: ProjectHistoryChange;
}

/** `GET versions/{versionId}/changes?to=` body. */
export interface ProjectHistoryChangesResponse {
  changes: ProjectHistoryItemChange[];
  unchangedCount: number;
  /** True when the API capped the list at 5,000 changes. */
  truncated: boolean;
}

/** One unified-diff line: `" "`, `"+"` or `"-"`. */
export interface ProjectHistoryDiffLine {
  op: ' ' | '+' | '-' | string;
  text: string;
}

/** One unified-diff hunk with 3 context lines. */
export interface ProjectHistoryHunk {
  fromStart: number;
  fromCount: number;
  toStart: number;
  toCount: number;
  lines: ProjectHistoryDiffLine[];
}

/** A workflow step identified by order, type and agent name. */
export interface ProjectHistoryWorkflowStep {
  order: number;
  type: string | null;
  agentName: string | null;
}

/** A step present on both sides with its differing document fields. */
export interface ProjectHistoryWorkflowStepChange extends ProjectHistoryWorkflowStep {
  changedFields: string[];
}

/** The workflow step-list diff. */
export interface ProjectHistoryWorkflowDiff {
  added: ProjectHistoryWorkflowStep[];
  removed: ProjectHistoryWorkflowStep[];
  changed: ProjectHistoryWorkflowStepChange[];
}

/** Tracks, clips and the record length in frames. */
export interface ProjectHistoryTimelineSummary {
  tracks: number;
  clips: number;
  durationFrames: number;
}

/** The timeline revision and summary diff. */
export interface ProjectHistoryTimelineDiff {
  fromRevision: number;
  toRevision: number;
  fromSummary: ProjectHistoryTimelineSummary;
  toSummary: ProjectHistoryTimelineSummary;
}

/** Pronunciation terms added, removed and changed. */
export interface ProjectHistoryPronunciationDiff {
  added: string[];
  removed: string[];
  changed: string[];
}

/** One field's before/after value. */
export interface ProjectHistoryFieldChange {
  from: string | null;
  to: string | null;
}

/** The project item diff: only the fields that changed. */
export interface ProjectHistoryProjectDiff {
  name: ProjectHistoryFieldChange | null;
  description: ProjectHistoryFieldChange | null;
}

/** What the API put in an item diff; only the members named by `mode` are populated. */
export type ProjectHistoryItemDiffMode =
  | 'text'
  | 'binary'
  | 'tooLarge'
  /** The bytes are held by quarantine or a safety hold, so no content is shown. */
  | 'unavailable'
  | 'workflow'
  | 'timeline'
  | 'designSystem'
  | 'pronunciation'
  | 'project';

/** `GET versions/{versionId}/items/{kind}/{key}/diff?to=` body. */
export interface ProjectHistoryItemDiff {
  kind: ProjectVersionItemKind | string;
  key: string;
  name: string | null;
  mode: ProjectHistoryItemDiffMode | string;
  hunks: ProjectHistoryHunk[] | null;
  fromSize: number | null;
  toSize: number | null;
  workflow: ProjectHistoryWorkflowDiff | null;
  timeline: ProjectHistoryTimelineDiff | null;
  /** The `DesignDocumentDiffer.Diff(...).ToJson()` shape, so `VersionDiff` can render it. */
  designSystem: DesignDiff | null;
  pronunciation: ProjectHistoryPronunciationDiff | null;
  project: ProjectHistoryProjectDiff | null;
}

// ── restore (contract section 8; C# types built in parallel, names as written there) ─────────────

export type ProjectHistoryRestoreScope = 'item' | 'project';

/** One item a restore applies, by kind and key. */
export interface ProjectHistoryRestoreItemRef {
  kind: ProjectVersionItemKind | string;
  key: string;
}

/** `POST versions/{versionId}/restore/preview` and `.../restore` body. */
export interface ProjectHistoryRestoreRequest {
  scope: ProjectHistoryRestoreScope;
  /** 1..50 entries when scope is `item`; omitted for a whole-project restore. */
  items?: ProjectHistoryRestoreItemRef[];
}

/** An item the restore would skip, with the code that says why. */
export interface ProjectHistoryRestoreBlocked {
  kind: ProjectVersionItemKind | string;
  key: string;
  name: string | null;
  reason: string;
}

/** `POST .../restore/preview` body; writes nothing. */
export interface ProjectHistoryRestorePreview {
  scope: ProjectHistoryRestoreScope;
  /** Same shape as the section 7 change list. */
  changes: ProjectHistoryItemChange[];
  blocked: ProjectHistoryRestoreBlocked[];
  /** An estimate of the net bytes the restore would add to the project. */
  estimateBytes: number;
}

/** An item the restore created, put back or removed. */
export interface ProjectHistoryRestoreReference {
  kind: ProjectVersionItemKind | string;
  key: string;
  name: string | null;
}

/** An item the restore could not apply, with its reason code. */
export interface ProjectHistoryRestoreSkipped extends ProjectHistoryRestoreReference {
  reason: string;
}

/** A warning about an item that was restored with a change the caller must know about. */
export interface ProjectHistoryRestoreNotice {
  kind: ProjectVersionItemKind | string;
  key: string;
  code: string;
}

/** `POST .../restore` result. Partial success is normal; every skip carries a reason code. */
export interface ProjectHistoryRestoreResult {
  safetyVersionId: string;
  restored: ProjectHistoryRestoreReference[];
  removed: ProjectHistoryRestoreReference[];
  skipped: ProjectHistoryRestoreSkipped[];
  notices: ProjectHistoryRestoreNotice[];
}

/** Every reason code a preview block or a restore skip can carry (contract sections 5 and 8). */
export type ProjectHistoryRestoreReason =
  | ProjectHistoryNotRestorableReason
  | 'PATH_TAKEN'
  | 'STORAGE_QUOTA_EXCEEDED'
  | 'ITEM_NOT_FOUND'
  | 'ALREADY_CURRENT'
  | 'DESIGN_VERSION_INVALID'
  | 'WORKFLOW_INVALID'
  | 'WORKFLOW_RUN_ACTIVE'
  | 'TIMELINE_INVALID'
  | 'EGRESS_CONSENT_RESET'
  | 'PROJECT_TOO_LARGE_TO_VERSION'
  | 'NAMED_VERSION_LIMIT'
  | 'PROJECT_RUN_ACTIVE'
  | 'RESTORE_IN_PROGRESS'
  | 'UNKNOWN';
