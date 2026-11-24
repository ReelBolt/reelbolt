export type TimelineTrackType =
  | 'video'
  | 'inserts'
  | 'censor'
  | 'graphics'
  | 'dialogue'
  | 'sfx'
  | 'music';

export interface TimelineDuckingPoint {
  timeSec: number;
  gainDb: number;
}

export interface VideoInsertQuadKeyframe {
  timeSec: number;
  x0: number;
  y0: number;
  x1: number;
  y1: number;
  x2: number;
  y2: number;
  x3: number;
  y3: number;
  bows?: unknown;
  surfaceCorners?: unknown;
}

export interface VideoInsertTuningCandidate {
  parameters: Record<string, number>;
  score: number;
  measurements: Record<string, number>;
}

export interface VideoInsertTrackTuning {
  objective: string;
  parameters: Record<string, number>;
  score: number;
  candidates: VideoInsertTuningCandidate[];
  /** The winning parameters in words. */
  summary?: string | null;
}

export interface TimelineItem {
  // Common / Identifiers
  id?: string;
  timelineStartSec: number;
  timelineEndSec?: number;

  // Cuts
  shotId?: string;
  sourceIndex?: number;
  sourceStartSec?: number;
  sourceEndSec?: number;
  label?: string;
  agentRationale?: string;
  lookGroup?: string;
  color?: string;
  colorGrade?: string;
  transitionOut?: string;

  // Screen Inserts
  regionId?: string;
  assetStorageKey?: string;
  surface?: string;
  confidence?: number;
  solvedColorKey?: string;
  /** Analyzed SOURCE time/frame coordinates (what the EditTimeline v2 seeder reads). */
  quadKeyframes?: VideoInsertQuadKeyframe[];
  /** The corners the encode used: OUTPUT timeline seconds, 0..1 of the OUTPUT picture. */
  outputQuadKeyframes?: VideoInsertQuadKeyframe[];
  /** Clipped to the plate's keyed silhouette (rounded corners, notch, occluders). */
  matted?: boolean;
  /** How the tracker tuned itself for this footage. */
  tuning?: VideoInsertTrackTuning;

  // Overlays & Dialogue
  placementId?: string;
  text?: string;
  subtext?: string;
  style?: string;

  // Dialogue
  rmsDbfs?: number;

  // SFX
  sfxId?: string;
  clipName?: string;
  durationSec?: number;
  gainDb?: number;
  timing?: string;

  // Music
  trackId?: string;
  trackName?: string;
  bedDbfs?: number;
  duckedDbfs?: number;
  duckingEnvelope?: TimelineDuckingPoint[];

  // AI Provenance & Metadata
  // All three are null together for deterministic / non-agent-authored items.
  agentName?: string;
  providerName?: string;
  stepResultId?: string;
}

export interface TimelineTrack {
  id: string;
  type: TimelineTrackType;
  name: string;
  index: number;
  items: TimelineItem[];
}

export interface TimelineOutputVideo {
  width: number;
  height: number;
  fps: number;
  durationSec: number;
  storageKey: string;
}

export interface TimelineSource {
  index: number;
  storageKey: string;
  durationSec: number;
  width: number;
  height: number;
  fps: number;
}

export interface TimelineManifest {
  version: number;
  projectId: string;
  executionId: string;
  outputVideo: TimelineOutputVideo;
  sources: TimelineSource[];
  tracks: TimelineTrack[];
}

export interface ProjectTimelineSummary {
  stepResultId: string;
  workflowExecutionId: string;
  workflowStepId: string;
  timelineStorageKey?: string | null;
  outputVideoStorageKey?: string | null;
  fileName: string;
  durationSec?: number | null;
  createdAt: string;
  /** The run's last output-producing render (one per Passed run). */
  isFinal?: boolean;
  workflowName?: string | null;
  runNumber?: number | null;
  stepLabel?: string | null;
}
