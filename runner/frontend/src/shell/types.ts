// The wire contract, mirroring runner/internal/ui/view.go field for field.
//
// It is written by hand because the alternative is a code generator the
// repository does not run, and a hand-written mirror is checked: contract.test.ts
// asserts every field the screens read is one the Go side actually sends, and
// the Go side is the side that defines them. A field renamed in Go without this
// file fails that test rather than rendering "undefined" to a user.

export type TrayState =
  | "idle"
  | "working"
  | "paused"
  | "error"
  | "unpaired"
  | "stopped";

export type RemedyKind =
  | "none"
  | "pair"
  | "start"
  | "resume"
  | "schedule"
  | "runtime"
  | "update";

export interface Remedy {
  kind: RemedyKind;
  label: string;
  detail?: string;
}

// JobOutcome is how the runner's own part of a job ended, as this machine saw
// it. "finished" means ReelBolt closed the session normally after the work it
// asked for was done; it does not claim the whole run succeeded.
export type JobOutcome =
  | "finished"     // ReelBolt closed the session normally
  | "cancelled"    // ReelBolt (or the person) stopped it
  | "timed-out"    // it ran past the time ReelBolt allowed
  | "failed"       // it could not start, or broke on this machine
  | "interrupted"; // the connection dropped or the runner stopped mid-job

export interface JobView {
  id: string;
  type: string;
  // title is a plain-language name for the work ("Rendering a video").
  title: string;
  orgId?: string;
  attempt?: number;
  // The names ReelBolt sends with a job, for display only. Any may be absent.
  workspaceName?: string;
  projectName?: string;
  workflowName?: string;
  startedAt: string;
  endedAt?: string;
  durationSeconds: number;
  stage?: string;
  percent?: number;
  running: boolean;
  // outcome is set once running is false.
  outcome?: JobOutcome;
  // outcomeDetail is one plain sentence, e.g. why it failed to start.
  outcomeDetail?: string;
  // dashboardUrl opens this job's run in the browser, when known.
  dashboardUrl?: string;
}

export interface DeviceView {
  paired: boolean;
  deviceId?: string;
  name?: string;
  workspaceId?: string;
  // workspaceName and pairedByEmail come from ReelBolt (the device's own
  // "who am I" call) and are absent until it has answered once.
  workspaceName?: string;
  pairedByEmail?: string;
  gatewayUrl?: string;
  keyStore?: string;
  revoked?: boolean;
}

export interface RuntimeView {
  image?: string;
  imageOrigin?: string;
  containerCli?: string;
  ffmpegVersion?: string;
  jobTypes: string[];
  remotionReady: boolean;
  failure?: string;
  detail?: string;
  candidates?: string[];
  slots: number;
  known?: boolean;
  probing?: boolean;
  probeError?: string;
  // imagePresent is whether the video toolkit image is already downloaded on
  // this machine. Absent when it could not be checked.
  imagePresent?: boolean;
  // imageBytes is its size on disk once present, when the runtime reports it.
  imageBytes?: number;
  // imageDownloadable is true only for a toolkit pinned to a published
  // version; an unpinned development image cannot be downloaded.
  imageDownloadable?: boolean;
  // download is the in-app download of the image ("download-runtime").
  download?: RuntimeDownload;
}

export interface RuntimeDownload {
  active: boolean;
  // percent is layers finished / layers total, 0..100. Absent until the
  // runtime has listed the layers.
  percent?: number;
  layersDone: number;
  layersTotal: number;
  startedAt?: string;
  finishedAt?: string;
  // error is one plain sentence when the download failed.
  error?: string;
}

export interface RunnerView {
  running: boolean;
  inProcess: boolean;
  pid?: number;
  startedAt?: string;
  governorKnown: boolean;
  allowed: boolean;
  reason?: string;
  reasonDetail?: string;
  held: boolean;
  heldBy?: string;
  heldReason?: string;
  nextChange?: string;
  degraded?: boolean;
  scheduleSummary?: string;
  signalsSummary?: string;
}

export interface GovernorStatus {
  allowed: boolean;
  reason: string;
  detail?: string;
  degraded?: boolean;
  held: boolean;
  heldBy?: string;
  heldReason?: string;
  mode?: string;
  schedule?: string;
  signals?: string;
  idle?: string;
  power?: string;
  nextChange?: string;
}

export interface GovernorView {
  status: GovernorStatus;
  known: boolean;
  why?: string;
  resumesAt?: string;
  canPause: boolean;
}

export interface Limits {
  memoryBytes: number;
  cpus: number;
  pids: number;
  ffmpegThreads: number;
  priority: string;
  diskQuotaBytes: number;
  notes?: string[];
}

export interface Schedule {
  mode: string;
  idleMinutes?: number;
  windows?: { days?: string[]; start: string; end: string }[];
  onBattery?: string;
}

// TelemetryView is E11's privacy panel, mirroring ui.TelemetryView.
//
// The disclosure text and the list of things that are never sent come from the
// Go side rather than being written here, so the screen cannot promise
// something the payload does not do.
export interface TelemetryView {
  enabled: boolean;
  asked: boolean;
  available: boolean;
  endpoint?: string;
  consentVersion?: string;
  disclosure: string;
  sent: number;
  unsent: number;
  dropped: number;
  failed: number;
  lastError?: string;
  logsDir?: string;
  note?: string;
}

// TelemetryPreview is GET /api/telemetry/preview: the exact documents that
// would be sent, built from this machine.
export interface TelemetryFieldNote {
  name: string;
  note: string;
}

export interface TelemetryPreview {
  schema: string;
  consentVersion: string;
  disclosure: string;
  enabled: boolean;
  available: boolean;
  endpoint?: string;
  crash: Record<string, unknown>;
  jobFailure: Record<string, unknown>;
  fields: TelemetryFieldNote[];
  neverSent: string[];
  notes: string[];
}

// LimitsConfig is the editable per-job caps, exactly runner.json's "limits"
// block. Byte values are numbers of bytes.
export interface LimitsConfig {
  memory: number;
  cpus: number;
  pids: number;
  ffmpegThreads: number;
  priority: "normal" | "below-normal" | "low";
  diskQuotaBytes: number;
}

// HostView is what this machine has, so the editors can offer sensible ranges.
export interface HostView {
  cpus: number;
  memoryBytes: number;
  diskFreeBytes?: number;
}

export interface SettingsView {
  slots: number;
  limits: LimitsConfig;
  schedule: Schedule;
  host?: HostView;
  // mediaCacheMaxBytes is the cap on the downloaded-media cache.
  mediaCacheMaxBytes?: number;
  updateChannel: string;
  autoUpdate: boolean;
  trustedKeys?: string;
  autostart: boolean;
  autostartKnown: boolean;
  configPath?: string;
  telemetry: TelemetryView;
}

export interface CacheView {
  scratchRoot?: string;
  workspaceRoot?: string;
  // bytes / entries are leftover job folders (scratch + workspaces).
  bytes: number;
  entries: number;
  known: boolean;
  capped: boolean;
  note?: string;
  // The downloaded-media cache: source files kept so a repeat job does not
  // download them again. Present once the cache exists.
  media?: MediaCacheView;
}

export interface MediaCacheView {
  root?: string;
  bytes: number;
  entries: number;
  maxBytes: number;
  // hits / misses since the runner started.
  hits: number;
  misses: number;
  // savedBytes is how much was NOT downloaded again thanks to the cache.
  savedBytes: number;
}

export interface UpdateView {
  checkedAt?: string;
  currentVersion: string;
  channel: string;
  action: string;
  releaseVersion?: string;
  reason?: string;
  restartRequired?: boolean;
  stopTakingJobs?: boolean;
}

export interface View {
  state: TrayState;
  headline: string;
  detail?: string;
  remedy?: Remedy;
  runner: RunnerView;
  device: DeviceView;
  currentJob?: JobView;
  jobs: JobView[] | null;
  runtime: RuntimeView;
  governor: GovernorView;
  limits: Limits;
  schedule: Schedule;
  settings: SettingsView;
  cache: CacheView;
  update?: UpdateView;
  version: string;
  warnings?: string[];
}

export interface MenuItem {
  id?: string;
  label: string;
  enabled: boolean;
  checked?: boolean;
  separator?: boolean;
  submenu?: MenuItem[];
}

// SettingsPatch is POST /api/settings. Every field is optional; the shell
// validates the whole result before writing runner.json.
export interface SettingsPatch {
  slots?: number;
  updateChannel?: string;
  autoUpdate?: boolean;
  autostart?: boolean;
  telemetry?: boolean;
  limits?: Partial<LimitsConfig>;
  schedule?: Schedule;
  mediaCacheMaxBytes?: number;
}

export interface ActionResult {
  ok: boolean;
  message?: string;
  screen?: string;
  view: View;
}

export interface PairingView {
  active: boolean;
  code?: string;
  url?: string;
  error?: string;
  message?: string;
}

// ---- computer folder sync ----
//
// The Folders screen's contract. It mirrors internal/ui/folders.go field for
// field, and the same rule applies: a field renamed in Go without this file
// fails contract.test.ts rather than rendering "undefined" to a person.

// FolderState is the engine's own word for a link. The window does not invent
// one: "paused" here means the engine said so.
export type FolderState = "uptodate" | "syncing" | "paused" | "attention";

// FolderPausedBy decides which button the window offers. A dashboard pause is
// resumed in the dashboard, so the window offers nothing for it.
export type FolderPausedBy = "computer" | "guard" | "dashboard";

export interface FolderSkippedGroup {
  reason: string;
  count: number;
  examples?: string[];
}

export interface FolderLink {
  linkId: string;
  localPath: string;
  projectId: string;
  projectName?: string;
  targetFolder: string;
  projectUrl?: string;
  state: FolderState;
  headline: string;
  reason?: string;
  pausedBy?: FolderPausedBy;
  canConfirmDelete?: boolean;
  fileCount: number;
  pending?: number;
  lastChangeAt?: string;
  lastSyncAt?: string;
  skipped?: FolderSkippedGroup[];
  note?: string;
}

export interface FolderProject {
  id: string;
  name: string;
}

// FoldersView is GET /api/folders.
export interface FoldersView {
  ready: boolean;
  problem?: string;
  running: boolean;
  summary?: string;
  links: FolderLink[] | null;
  projects: FolderProject[] | null;
  statusPath?: string;
  pickerAvailable: boolean;
}
