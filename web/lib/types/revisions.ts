/**
 * "Request changes" on a finished animated video: the composition's numbered VERSIONS and the
 * change requests ("revisions") that produced them. Mirrors the Inference API's
 * `GET/POST /api/v1/projects/{projectId}/executions/{executionId}/revisions` contract.
 */

/** `PreviewOnly` exists for a future live editor; the dashboard only ever sends `Render`. */
export type RevisionMode = 'Render' | 'PreviewOnly';

/**
 * `Snapshot`: versions exist. `Recoverable`: an older run with no saved version yet, whose project
 * the platform will try to pick up (best effort, within about 7 days of the run). `Unavailable`:
 * cannot be revised.
 */
export type RevisionAvailability = 'Snapshot' | 'Recoverable' | 'Unavailable';

export type RevisionStatus = 'Queued' | 'Running' | 'Passed' | 'Failed' | 'Cancelled';

/**
 * What a change does to the edit when the animation feeds a larger workflow (an analysis, an edit, a
 * final compile): `KeepCut` (default) keeps the approved cut when the changed animation still has the
 * same scenes at the same places; `ReEdit` re-makes the edit decisions too.
 */
export type RevisionCutPolicy = 'KeepCut' | 'ReEdit';

/** A step's fate in a change's replay of the workflow. */
export type RevisionReplayDisposition = 'Reuse' | 'Revise' | 'Run' | 'KeepIfCompatible' | 'Skip';

/** One step of the workflow a change makes again after re-making the animation. */
export interface RevisionReplayStep {
  stepOrder: number;
  label: string;
  stepType: string;
  /** With "Keep the same cut". */
  keepCut: RevisionReplayDisposition;
  /** With "Re-edit the video too". */
  reEdit: RevisionReplayDisposition;
}

/** What a change of the current version does after the animation is re-made. */
export interface RevisionReplay {
  /** False when the animation is the finished video (nothing else to redo), or its run cannot be replayed. */
  available: boolean;
  /** Plain language, when the rest of the workflow cannot be redone. */
  reason: string | null;
  /** True when the cut choice matters. */
  hasEditDecisions: boolean;
  steps: RevisionReplayStep[];
  keepCutEstimate: RevisionCostEstimate | null;
  reEditEstimate: RevisionCostEstimate | null;
}

/** A finished video a version's run delivered (play with `getOutputVideoUrl(projectId, stepResultId)`). */
export interface RevisionDeliverable {
  stepResultId: string;
  stepOrder: number;
  label: string | null;
  /** The one played by default (the last). */
  isPrimary: boolean;
}

export interface RevisionFrameRange {
  compositionId?: string | null;
  fromFrame: number;
  toFrame: number;
}

export interface RevisionTimeRegion {
  fromSeconds: number;
  toSeconds: number;
}

export interface RevisionTargets {
  scenes?: string[] | null;
  frameRanges?: RevisionFrameRange[] | null;
  region?: RevisionTimeRegion | null;
}

export interface RevisionScene {
  name: string;
  fromFrame: number;
  durationInFrames: number;
  description?: string | null;
}

export interface CompositionVersion {
  id: string;
  versionNumber: number;
  parentVersionId: string | null;
  parentVersionNumber: number | null;
  createdAt: string;
  /** Null for the original render. */
  instruction: string | null;
  source: 'RenderCapture' | 'SandboxRecovery' | 'PreviewSnapshot';
  /** The run that produced this version (link to its execution page). */
  executionId: string | null;
  workflowId: string | null;
  /** Play with `getOutputVideoUrl(projectId, renderStepResultId)`. */
  renderStepResultId: string | null;
  hasRender: boolean;
  compositionId: string | null;
  durationInFrames: number | null;
  fps: number | null;
  width: number | null;
  height: number | null;
  sceneCount: number;
  /**
   * The workflow's finished video(s) this version's run made from the animation (e.g. the final cut).
   * Empty when the animation itself is the finished video. Absent from older responses.
   */
  deliverables?: RevisionDeliverable[];
  /** The status of the run that made this version. */
  runStatus?: RevisionStatus | null;
  /** True when the run is meant to end with a finished video beyond the animation (made or not yet). */
  finalVideoExpected?: boolean;
  cutPolicy?: RevisionCutPolicy | null;
  /** For a change that kept the cut: whether it could (true) or had to re-make the edit (false). */
  cutKept?: boolean | null;
}

export interface CompositionRevision {
  id: string;
  executionId: string;
  workflowId: string;
  status: RevisionStatus;
  instruction: string;
  targets: RevisionTargets | null;
  mode: RevisionMode;
  baseVersionId: string | null;
  baseVersionNumber: number | null;
  resultVersionId: string | null;
  resultVersionNumber: number | null;
  createdAt: string;
  completedAt: string | null;
  errorMessage: string | null;
  cutPolicy?: RevisionCutPolicy | null;
}

export interface RevisionCostEstimate {
  billingEnabled: boolean;
  lowCredits: number;
  highCredits: number;
  requiredCredits: number;
  balanceCredits: number | null;
  affordable: boolean;
}

export interface RevisionHistory {
  workspaceId: string | null;
  availability: RevisionAvailability;
  /** False while the clicked run is still running, or when `Unavailable`. */
  canRevise: boolean;
  /** Plain language; shown as-is. */
  unavailableReason: string | null;
  /** The version this run produced (the default "start from"). */
  currentVersionId: string | null;
  /** The newest version of the video: what a change request builds on when no version is chosen. */
  headVersionId?: string | null;
  headVersionNumber?: number | null;
  /** Scenes of the current version, for the target chips (may be empty). */
  scenes: RevisionScene[];
  /** Ascending by `versionNumber`. */
  versions: CompositionVersion[];
  /** Newest first. */
  revisions: CompositionRevision[];
  estimate: RevisionCostEstimate | null;
  /** What a change of the current version redoes after the animation. Absent from older responses. */
  replay?: RevisionReplay | null;
}

export interface CreateRevisionRequest {
  instruction: string;
  targets?: RevisionTargets | null;
  fromVersionId?: string | null;
  mode?: RevisionMode;
  /** Only sent when the cut choice matters (`replay.hasEditDecisions`). */
  cutPolicy?: RevisionCutPolicy;
}

/** Longest instruction the API accepts (400 beyond it). */
export const REVISION_INSTRUCTION_MAX_LENGTH = 4000;
