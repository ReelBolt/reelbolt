/**
 * States of a background item (contract of `GET /api/v1/activity`). `needsReview` = finished, but the
 * result could not be applied as-is, so the user must choose; the server does not send it today.
 */
export type ActivityState = 'waiting' | 'running' | 'done' | 'failed' | 'cancelled' | 'needsReview';

/** A client job also has a cost-estimate phase before it runs. */
export type ActivityJobState = ActivityState | 'estimating';

/** Types the server returns. */
export type ServerActivityType =
  | 'workflowRun'
  | 'designSystemBuild'
  | 'revision'
  | 'changeRequest'
  | 'graphic'
  | 'render'
  | 'preview'
  | 'loudness'
  | 'audition'
  | 'objectAnalysis'
  | 'audioCleanup'
  | 'subjectClip'
  | 'mediaAnalysis'
  | 'mediaPrep'
  | 'cloudSync'
  | 'hiddenRun';

/**
 * Types the browser tracks itself. All but `subjectClip` have no server row; a clip with avatars is tracked here from
 * its start and the server lists its hidden run too (the server row wins, as for any run with the same executionId).
 */
export type ClientActivityType = 'upload' | 'pictureOperation' | 'proposal' | 'assistantWatch' | 'avatarBuild' | 'subjectClip';

/** Any string is allowed so a future server type degrades to the generic definition. */
export type ActivityType = ServerActivityType | ClientActivityType | (string & {});

export interface ActivitySubject {
  timelineId?: string;
  documentId?: string;
  mediaRef?: string;
  sourceExecutionId?: string;
  linkId?: string;
  analysisKind?: string;
  kindNumber?: number;
}

/** Server wire shape of one item of `GET /api/v1/activity`. */
export interface ActivityItem {
  /** `<source>:<guid>`, stable. */
  id: string;
  type: ServerActivityType | (string & {});
  state: ActivityState;
  projectId: string;
  projectName: string;
  /** A user-visible name, never an id. */
  title: string | null;
  executionId: string | null;
  workflowId: string | null;
  subject: ActivitySubject;
  step: { order: number; count: number; stepType: string | null } | null;
  /** 0..1 when the row knows it. */
  progress: number | null;
  credits: { heldCredits: number | null; chargedCredits: number | null };
  failure: { code: string | null; refunded: boolean | null } | null;
  startedByYou: boolean;
  cancellable: boolean;
  createdAt: string;
  startedAt: string | null;
  endedAt: string | null;
}

export interface ActivityResponse {
  items: ActivityItem[];
  serverTime: string;
  activeCount: number;
}

/** What the tray and in-place status show: a server item or a client-tracked job. */
/** Why a job needs the user's OK, and what applying or discarding does (client jobs). */
export interface JobReview {
  summaryKey: string;
  summaryValues?: Record<string, unknown>;
  onApply?: () => void | Promise<void>;
  onDiscard?: () => void | Promise<void>;
}

export interface ActivityJob extends Omit<ActivityItem, 'state' | 'type'> {
  type: ActivityType;
  state: ActivityJobState;
  origin: 'server' | 'client';
  /** Client jobs: the workspace they were started in (only that workspace lists them). */
  orgId?: string | null;
  /** Live progress 0..1 from the stream, when fresher than `progress`. */
  liveProgress?: number | null;
  liveStage?: string | null;
  /** Explicit destination (client jobs); wins over the registry's `href`. */
  href?: string | null;
  /** Client jobs only: the title may hold user text, so notifications leave it out. */
  quietTitle?: boolean;
  /** Client jobs only: how to stop the work. */
  cancelClient?: () => Promise<void>;
  /** Set while `state === 'needsReview'`. */
  review?: JobReview;
}
