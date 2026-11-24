import type { T } from '@/i18n/translator';
import { enT } from '@/lib/i18n-default';

/**
 * Presentation facts about a paired runner (E7-UI): what its self-reported capabilities mean,
 * how to name its platform, how recently it was heard from, and how to render a relative time.
 *
 * Everything that reads a device's capabilities is reading data the API RELAYS VERBATIM from the
 * runner's own `hello` message (`capabilitiesOf` in api/handlers/runners/runners.go, which only
 * checks that the blob is valid JSON). It is untrusted and self-reported, so every reader narrows
 * it field by field and a blob of the wrong shape renders as "not reported" rather than throwing.
 */

/** The three v1 job types (sandbox/pkg/protocol/constants.go). */
export const JOB_REMOTION_WORKSPACE = 'remotion.workspace-session';
export const JOB_VIDEO_COMPILE = 'video.compile';
export const JOB_VIDEO_ANALYZE_EXTRACT = 'video.analyze.extract';

/** The container runtimes a runner may advertise. */
export const CONTAINER_NONE = 'none';
export const CONTAINER_DOCKER = 'docker';
export const CONTAINER_PODMAN = 'podman';

/**
 * A narrowed view of `hello.capabilities`. Every field is `null` when the device did not send
 * one, which is a different fact from sending an empty value — and the difference is what the
 * chips render.
 */
export interface RunnerCapabilities {
  protocolVersion: string | null;
  agentVersion: string | null;
  /** How many jobs the runner will take at once. */
  slots: number | null;
  /** `none` | `docker` | `podman`. */
  container: string | null;
  /** `rootful` | `rootless` | `vm`, when the device sent one. */
  containerMode: string | null;
  /** The digest of the Remotion runtime image it has pulled; null when it has none. */
  runtimeImageDigest: string | null;
  ffmpegVersion: string | null;
  fontsetVersion: string | null;
  jobTypes: string[];
  maxOutputBytes: number | null;
  /** Number of local models the device says it shares with the workspace; null when it shares none. */
  sharedLocalModels: number | null;
}

function asString(value: unknown): string | null {
  return typeof value === 'string' && value.trim() !== '' ? value : null;
}

function asNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

/**
 * Narrows a relayed `capabilities` blob, or returns `null` when there is nothing usable to show.
 *
 * `null` is the honest answer for both "the device has never sent a hello" (the API sends no
 * `capabilities` field at all) and "the blob is not an object" — the caller renders the same
 * "not reported yet" chip for either, because from here they are the same fact: we know nothing.
 */
export function parseRunnerCapabilities(raw: unknown): RunnerCapabilities | null {
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) return null;
  const o = raw as Record<string, unknown>;

  const jobTypes = Array.isArray(o.jobTypes)
    ? o.jobTypes.filter((t): t is string => typeof t === 'string')
    : [];

  return {
    protocolVersion: asString(o.protocolVersion),
    agentVersion: asString(o.agentVersion),
    slots: asNumber(o.slots),
    container: asString(o.container),
    containerMode: asString(o.containerMode),
    runtimeImageDigest: asString(o.runtimeImageDigest),
    ffmpegVersion: asString(o.ffmpegVersion),
    fontsetVersion: asString(o.fontsetVersion),
    jobTypes,
    maxOutputBytes: asNumber(o.maxOutputBytes),
    sharedLocalModels: sharedLocalModelCount(o.localLlm),
  };
}

/** `hello.capabilities.localLlm`, narrowed: a count only when the device says sharing is on. */
function sharedLocalModelCount(raw: unknown): number | null {
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) return null;
  const o = raw as Record<string, unknown>;
  if (o.enabled !== true) return null;
  return Array.isArray(o.models) ? o.models.filter((m) => typeof m === 'string').length : 0;
}

/**
 * Whether this runner can render Remotion. The advertised job list is the authority: the protocol
 * requires a runner with `container: "none"` to advertise only `video.compile` and
 * `video.analyze.extract`, so a device that names `remotion.workspace-session` is claiming a
 * container runtime it verified at startup. The claim is self-reported and may be wrong; the chip
 * says what the device SAID, and the container chip next to it shows the same claim's precondition.
 */
export function canRenderRemotion(caps: RunnerCapabilities | null): boolean {
  return caps !== null && caps.jobTypes.includes(JOB_REMOTION_WORKSPACE);
}

/** Whether the runner advertised any job type at all. */
export function hasJobTypes(caps: RunnerCapabilities | null): boolean {
  return caps !== null && caps.jobTypes.length > 0;
}

export interface CapabilityChip {
  label: string;
  color: string;
  /** Shown on hover. Explains what the chip is evidence of, never more than the data supports. */
  tooltip: string;
}

/**
 * The container chip: which runtime the device says it will run Remotion in. A runner with no
 * container still does useful work (ffmpeg compile and extraction), so "No container" is a
 * capability statement and not an error.
 */
export function containerChip(caps: RunnerCapabilities | null, t: T = enT): CapabilityChip | null {
  if (caps === null || caps.container === null) return null;
  switch (caps.container) {
    case CONTAINER_DOCKER:
      return {
        label: 'Docker',
        color: 'blue',
        tooltip: t('libLabels.runners.dockerTip') + (caps.containerMode ? t('libLabels.runners.containerMode', { mode: caps.containerMode }) : ''),
      };
    case CONTAINER_PODMAN:
      return {
        label: 'Podman',
        color: 'violet',
        tooltip: t('libLabels.runners.podmanTip') + (caps.containerMode ? t('libLabels.runners.containerMode', { mode: caps.containerMode }) : ''),
      };
    case CONTAINER_NONE:
      return {
        label: t('libLabels.runners.noContainer'),
        color: 'gray',
        tooltip: t('libLabels.runners.noContainerTip'),
      };
    default:
      // A runtime this build has never heard of: show the device's own word rather than
      // relabelling it as something it might not be.
      return {
        label: caps.container,
        color: 'gray',
        tooltip: t('libLabels.runners.unknownContainerTip'),
      };
  }
}

/** The Remotion capability chip, present only when the device advertised a job type. */
export function renderingChip(caps: RunnerCapabilities | null, t: T = enT): CapabilityChip | null {
  if (!hasJobTypes(caps)) return null;
  if (canRenderRemotion(caps)) {
    return {
      label: t('libLabels.runners.videoRenders'),
      color: 'green',
      tooltip: t('libLabels.runners.videoRendersTip'),
    };
  }
  return {
    label: t('libLabels.runners.compileOnly'),
    color: 'gray',
    tooltip: t('libLabels.runners.compileOnlyTip'),
  };
}

/** `slots` as a chip, when the device reported one. */
export function slotsChip(caps: RunnerCapabilities | null, t: T = enT): CapabilityChip | null {
  if (caps === null || caps.slots === null) return null;
  return {
    label: t('libLabels.runners.slots', { count: caps.slots }),
    color: 'gray',
    tooltip: t('libLabels.runners.slotsTip'),
  };
}

/** A device that says it shares a model running on that computer with the workspace. */
export function localModelChip(caps: RunnerCapabilities | null, t: T = enT): CapabilityChip | null {
  if (caps === null || caps.sharedLocalModels === null) return null;
  const n = caps.sharedLocalModels;
  return {
    label: n === 0 ? t('libLabels.runners.sharesModel') : t('libLabels.runners.sharesModels', { count: n }),
    color: 'violet',
    tooltip: t('libLabels.runners.sharesTip'),
  };
}

/** `ffmpegVersion` as a chip, when the device reported one. */
export function ffmpegChip(caps: RunnerCapabilities | null, t: T = enT): CapabilityChip | null {
  if (caps === null || caps.ffmpegVersion === null) return null;
  return {
    label: t('libLabels.runners.videoTools', { version: caps.ffmpegVersion }),
    color: 'gray',
    tooltip: t('libLabels.runners.videoToolsTip'),
  };
}

/** Every chip the device's capabilities earn, in display order. */
export function capabilityChips(caps: RunnerCapabilities | null, t: T = enT): CapabilityChip[] {
  return [renderingChip(caps, t), containerChip(caps, t), slotsChip(caps, t), ffmpegChip(caps, t), localModelChip(caps, t)].filter(
    (chip): chip is CapabilityChip => chip !== null,
  );
}

/**
 * How long a device may be silent and still be called online.
 *
 * The protocol's own numbers (sandbox/pkg/protocol/constants.go): a runner heartbeats every 15 s
 * and the gateway closes a connection that has been silent for 45 s, while
 * `runner_devices.last_seen_at` is refreshed at most once a minute (the gateway's
 * RUNNER_LAST_SEEN_INTERVAL, default 1 min). So while a device is connected its recorded heartbeat
 * is at most about a minute old, and three minutes leaves room for a slow write, a gateway deploy
 * or clock skew — without ever calling a disconnected device online, because the column is NOT
 * cleared when the socket closes.
 *
 * This is an INFERENCE from a heartbeat, not a live presence check: the gateway's authoritative
 * "is a runner online?" answer is a per-replica question asked by the engine
 * (GET /internal/v1/runners), which the dashboard cannot call. The UI says "heard from", never
 * "connected".
 */
export const RUNNER_ACTIVE_WINDOW_MS = 3 * 60 * 1000;

export type RunnerPresence = 'online' | 'offline' | 'never';

/** Whether the recorded heartbeat is recent enough to call the device online. */
export function runnerPresence(
  lastSeenAt: string | null | undefined,
  now: number = Date.now(),
): RunnerPresence {
  if (!lastSeenAt) return 'never';
  const seen = Date.parse(lastSeenAt);
  if (Number.isNaN(seen)) return 'never';
  return now - seen <= RUNNER_ACTIVE_WINDOW_MS ? 'online' : 'offline';
}

/**
 * A short, human relative time. Handles both directions, because the approval screen counts down
 * to an expiry as well as back to a request.
 *
 * Deliberately coarse: the caller is deciding "is this thing alive / do I have time", and a
 * second-by-second figure would imply a precision the heartbeat schedule does not have.
 */
export function formatRelative(iso: string | null | undefined, now: number = Date.now(), t: T = enT): string {
  if (!iso) return t('libLabels.runners.never');
  const at = Date.parse(iso);
  if (Number.isNaN(at)) return t('libLabels.runners.unknown');

  const deltaMs = at - now;
  const past = deltaMs <= 0;
  const seconds = Math.round(Math.abs(deltaMs) / 1000);

  let amount: string;
  if (seconds < 45) amount = t('libLabels.runners.relLess');
  else if (seconds < 90) amount = t('libLabels.runners.relMinutes', { count: 1 });
  else if (seconds < 3600) amount = t('libLabels.runners.relMinutes', { count: Math.round(seconds / 60) });
  else if (seconds < 5400) amount = t('libLabels.runners.relHours', { count: 1 });
  else if (seconds < 86400) amount = t('libLabels.runners.relHours', { count: Math.round(seconds / 3600) });
  else if (seconds < 172800) amount = t('libLabels.runners.relDays', { count: 1 });
  else amount = t('libLabels.runners.relDays', { count: Math.round(seconds / 86400) });

  return t(past ? 'libLabels.runners.relPast' : 'libLabels.runners.relFuture', { amount });
}

/**
 * How to name a device's platform. Decision D26 locks the runner to Windows x64 and Linux
 * x64/arm64; anything else is shown with the raw values the device sent rather than being folded
 * into a supported-looking label.
 */
export function runnerPlatformLabel(os?: string | null, arch?: string | null, t: T = enT): string {
  if (!os && !arch) return t('libLabels.runners.notReported');

  const osName =
    os === 'windows' ? 'Windows' : os === 'linux' ? 'Linux' : os === 'darwin' ? 'macOS' : os;

  const archName = arch === 'amd64' ? 'x64' : arch === 'arm64' ? 'arm64' : arch;

  if (osName && archName) return t('libLabels.runners.platform', { os: osName, arch: archName });
  return osName ?? archName ?? t('libLabels.runners.notReported');
}

/** The platforms a runner may pair from, for the help text (decision D26). */
export const SUPPORTED_RUNNER_PLATFORMS = enT('libLabels.runners.supportedPlatforms');

/** An account uuid in a table cell: enough to tell two apart, not a wall of hex. */
export function shortId(id: string): string {
  return id.length > 8 ? id.slice(0, 8) : id;
}
