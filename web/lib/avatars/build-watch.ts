/**
 * Follows one avatar build to its end, for the header tray (docs/background-activity.md): asks the build's own
 * route with the same growing pauses as the Avatars tab, reports each reading, and settles when the build is
 * `ready` (resolves), `failed` or `cancelled` (rejects).
 */
import { getAvatarBuild } from '../api/avatar-variants';
import type { AvatarBuild } from '../types/avatar-builds';
import { buildPollDelayMs, buildsSignature, isBuildRunning } from './builds';

/** A build that ended without an avatar. `cancelled` tells a stop from a failure. */
export class AvatarBuildEnded extends Error {
  readonly build: AvatarBuild;
  readonly cancelled: boolean;
  constructor(build: AvatarBuild) {
    super(build.message?.trim() || String(build.state));
    this.name = 'AvatarBuildEnded';
    this.build = build;
    this.cancelled = String(build.state).toLowerCase() === 'cancelled';
  }
}

/** Waits `ms`, or rejects at once when the signal is aborted. */
export function abortableDelay(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) { reject(new DOMException('Aborted', 'AbortError')); return; }
    const timer = setTimeout(() => { signal?.removeEventListener('abort', onAbort); resolve(); }, ms);
    function onAbort() { clearTimeout(timer); reject(new DOMException('Aborted', 'AbortError')); }
    signal?.addEventListener('abort', onAbort, { once: true });
  });
}

export interface WatchBuildOptions {
  signal?: AbortSignal;
  /** Called with every reading, the first one included. */
  onUpdate?: (build: AvatarBuild) => void;
  /** The build as already known, so the first wait is not wasted on asking for it again. */
  initial?: AvatarBuild;
  /** Test seams. */
  read?: (projectId: string, jobId: string) => Promise<AvatarBuild>;
  delay?: (ms: number, signal?: AbortSignal) => Promise<void>;
  /** How many readings in a row may fail before the watch gives up (the build itself is not touched). */
  maxReadFailures?: number;
}

export async function watchAvatarBuild(projectId: string, jobId: string, options: WatchBuildOptions = {}): Promise<AvatarBuild> {
  const { signal, onUpdate, read = getAvatarBuild, delay = abortableDelay, maxReadFailures = 5 } = options;
  let build = options.initial ?? null;
  let attempt = 0;
  let failures = 0;
  let signature = build ? buildsSignature([build]) : '';
  for (;;) {
    if (build) {
      onUpdate?.(build);
      if (!isBuildRunning(build)) {
        if (String(build.state).toLowerCase() === 'ready') return build;
        throw new AvatarBuildEnded(build);
      }
    }
    await delay(buildPollDelayMs(attempt), signal);
    attempt += 1;
    try {
      build = await read(projectId, jobId);
      failures = 0;
      const next = buildsSignature([build]);
      if (next !== signature) { signature = next; attempt = 0; }
    } catch (e) {
      if (signal?.aborted) throw e;
      failures += 1;
      if (failures >= maxReadFailures) throw e;
    }
  }
}
