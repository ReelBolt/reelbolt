/** Mirrors `ChangeRequestDtos.cs` (Inference API). Camel-cased on the wire. */

import type { AppManifestInput } from './workflow';

export type ReplayDisposition = 'Reuse' | 'Revise' | 'Run' | 'KeepIfCompatible' | 'Skip';

export interface StepChangeItem {
  id: string;
  status: string | null;
  durationSec: number | null;
  text: string | null;
}

/** `ChangeOverrideType` names on the wire. A newer server may add a type; the form ignores one it does not know. */
export type StepChangeOverrideType = 'Text' | 'Number' | 'PronunciationList' | 'Flag' | 'Enum';

export interface StepChangeOverrideSpec {
  key: string;
  /** A `ChangeOverrideType` name (kept a string so an unknown future type still parses). */
  type: StepChangeOverrideType | (string & {});
  min: number | null;
  max: number | null;
  maxLength: number | null;
  maxEntries: number | null;
  /** The accepted names of an `Enum` key; null or absent for every other type. */
  values?: string[] | null;
  /** A `Text` key that may carry line breaks (at most `maxEntries` lines): drawn as a text area, sent as given. */
  multiLine?: boolean;
}

export interface StepChangeStepCapability {
  stepOrder: number;
  stepResultId: string;
  stepType: string;
  label: string;
  supported: boolean;
  notSupportedReason: string | null;
  /** `composition-revision` for the film step: changed through the existing revision panel. */
  route: string | null;
  itemKind: string;
  items: StepChangeItem[];
  overrides: StepChangeOverrideSpec[];
  /** A standing cost warning key (`script`: everything after the script is made again); absent for most steps. */
  costNote?: string | null;
  /** True when an instruction naming at least one item is a request by itself (the script by scene). */
  allowsInstructionOnly?: boolean;
  /** True when this step may be one of several targets of one request (a supported film step included). */
  combinable?: boolean;
}

export interface StepChangeCapabilities {
  executionId: string;
  runFinished: boolean;
  /** Most targets one request may carry. */
  maxTargets?: number;
  steps: StepChangeStepCapability[];
  /** An app run's own fields (docs/design/app-inputs.md 25); absent unless app inputs are on and the run is an app run. */
  appInputs?: StepChangeAppInputs | null;
}

/** Why a field cannot be changed as a change request. */
export type AppInputChangeReason = 'APP_INPUT_NOT_USED' | 'INPUT_CHANGES_BRANCH' | 'APP_INPUTS_STALE' | 'RUN_NOT_FINISHED' | (string & {});

/** One app field of a finished app run, as a change request can change it. */
export interface StepChangeAppInputField {
  inputId: string;
  label: string;
  type: string;
  /** The run's value (null when it had none). */
  value: string | number | boolean | null;
  valueText: string;
  supported: boolean;
  notSupportedReason: string | null;
  reasonCode: AppInputChangeReason | null;
  /** The steps a change re-makes (before what depends on them). */
  usedBy: string[];
  decidesBranch: boolean;
  /** The values that would pick another branch (refused); null when they cannot be listed. */
  branchChangingValues: (string | number | boolean)[] | null;
  /** The field as the run's own app page declared it. */
  input: AppManifestInput;
}

export interface StepChangeAppInputs {
  /** The app the run was made from (for the model options route). */
  workflowId: string | null;
  fields: StepChangeAppInputField[];
  /** The run settings the run's page shows (docs/design/app-inputs.md 29); absent when it shows none. */
  briefFields?: StepChangeBriefField[] | null;
}

/** Why a run setting cannot be changed as a change request. */
export type BriefChangeReason = 'BRIEF_FIELD_NOT_CHANGEABLE' | 'BRIEF_FIELD_NOT_USED' | 'RUN_NOT_FINISHED' | (string & {});

/** One run setting (a brief field, the notes or a files slot) of a finished app run, as a change request can change it. */
export interface StepChangeBriefField {
  /** `brief:language`, `brief:notes`, `brief:files:Video`. */
  key: string;
  kind: 'brief' | 'notes' | 'files' | (string & {});
  /** The brief field (kind `brief`). */
  field: string | null;
  /** The files role on the wire (`Video`), kind `files`. */
  role: string | null;
  label: string;
  /** The run's value in the body's own shape: a language code, seconds, a voice object, file ids, the notes. */
  value: unknown;
  valueText: string;
  supported: boolean;
  notSupportedReason: string | null;
  reasonCode: BriefChangeReason | null;
  usedBy: string[];
  input: AppManifestInput;
}

/** The run settings a change request may set (docs/design/app-inputs.md 29): the fields set are the new values. */
export interface RunBriefChange {
  language?: string;
  sourceLanguage?: string;
  lengthSec?: number;
  voice?: { mode: 'None' | 'Narrated'; projectVoiceId?: string };
  designSystem?: 'project' | 'none';
  overlayCardAudio?: 'agent' | 'keep' | 'mute';
  avatar?: { avatarId: string };
  notes?: string;
  sourceFiles?: { role: string; fileIds: string[] }[];
}

/** One changed field of an app-input change: before and after in words. */
export interface StepChangeInputChange {
  inputId: string;
  label: string;
  type: string;
  before: string;
  after: string;
  value: string | number | boolean | null;
}

export interface StepChangeEstimate {
  billingEnabled: boolean;
  lowCredits: number;
  highCredits: number;
  requiredCredits: number;
  balanceCredits: number | null;
  affordable: boolean;
  /** The exact price of just the redone part, when it can be computed from held prices; absent otherwise. */
  targetExactCredits?: number | null;
  targetExactNote?: string | null;
}

export interface StepChangeReplayStep {
  stepOrder: number;
  label: string;
  disposition: ReplayDisposition;
  reason: string;
}

export interface StepChangeReplay {
  steps: StepChangeReplayStep[];
}

export interface StepChangePlanSummary {
  redone: number;
  reused: number;
  skipped: number;
}

export interface StepChangeRequest {
  id: string;
  executionId: string;
  workflowDefinitionId: string;
  /** The source run's own workflow id (the request's `workflowDefinitionId` is a hidden snapshot). */
  sourceWorkflowDefinitionId?: string;
  sourceExecutionId: string;
  targetStepOrder: number;
  targetStepResultId: string | null;
  stepType: string;
  instruction: string;
  itemIds: string[];
  status: string;
  costCredits: number | null;
  createdAt: string;
  estimate?: StepChangeEstimate | null;
  replay?: StepChangeReplay | null;
  plan?: StepChangePlanSummary | null;
  /** Every target of the request, ordered by step order; absent on an older server. */
  targets?: StepChangeTarget[];
  /** The fields an app-input change changed, before and after in words (also in the list). */
  inputChanges?: StepChangeInputChange[];
}

/** One target of a request as the server echoes it. */
export interface StepChangeTarget {
  stepOrder: number;
  stepLabel: string;
  stepType: string;
  itemIds: string[];
  instruction: string;
  /** `inputs`: a step re-made because an app field it uses changed. */
  kind: 'step' | 'film' | 'inputs';
}

export interface StepChangeRequestList {
  requests: StepChangeRequest[];
}

export interface StepChangeEstimatePreview {
  estimate: StepChangeEstimate | null;
  replay: StepChangeReplay;
  targets?: StepChangeTarget[];
  /** Each changed field of an app-input change. */
  inputChanges?: StepChangeInputChange[];
}

export interface PronunciationEntry {
  term: string;
  spelling: string;
}

/** The typed override values for one item (or `"*"`). Only the keys of the capability's catalogue. */
export type OverrideValues = Record<string, string | number | boolean | PronunciationEntry[]>;

export interface CreateStepChangeRequest {
  stepOrder?: number;
  stepResultId?: string;
  instruction?: string;
  itemIds?: string[];
  overrides?: Record<string, OverrideValues>;
}

/** The multi form: 1..6 entries; a film entry carries `stepOrder` and `instruction` only. */
export interface CreateMultiStepChangeRequest {
  targets: CreateStepChangeRequest[];
}

/**
 * An app-input change: new values for an app run's own fields (null clears an optional one) and/or run settings
 * (docs/design/app-inputs.md 29), optionally with step `targets` (one plan, one price, one run).
 */
export interface CreateAppInputChangeRequest {
  inputs?: Record<string, string | number | boolean | null>;
  brief?: RunBriefChange;
  targets?: CreateStepChangeRequest[];
}

export type ChangeRequestBody = CreateStepChangeRequest | CreateMultiStepChangeRequest | CreateAppInputChangeRequest;
