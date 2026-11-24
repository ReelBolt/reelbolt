/**
 * P8: browser voiceover recording — MediaRecorder → upload → an audio clip at the playhead.
 *
 * The recording is an ordinary project file (uploaded through the same `uploadFile` route the file
 * manager uses), so the clip it becomes is a `projectFile` `AssetRef` the validator resolves against
 * the project like any other: no new trust surface. The placement is a program of the closed op
 * catalogue — onto the first audio track whose span at the playhead is free, or onto a fresh "VO"
 * track — and lands as one undo step.
 *
 * Feature detection is honest: `recorderSupport()` says why recording is unavailable (no
 * `MediaRecorder`, no `getUserMedia`, insecure context) and the panel shows that sentence instead
 * of a button that cannot work.
 */

import type { EditOp } from '../engine';
import type { EditTimeline, MediaClip, Track } from '../types';

export type RecorderSupport = { ok: true; mimeType: string } | { ok: false; reason: string };

/** The container/codec the browser will record, in preference order. */
export const VO_MIME_TYPES = ['audio/webm;codecs=opus', 'audio/webm', 'audio/ogg;codecs=opus', 'audio/mp4'] as const;

export function recorderSupport(env: { MediaRecorder?: { isTypeSupported?: (t: string) => boolean }; mediaDevices?: { getUserMedia?: unknown }; isSecureContext?: boolean } = defaultEnv()): RecorderSupport {
  if (env.isSecureContext === false) return { ok: false, reason: 'Recording needs a secure (HTTPS) connection.' };
  if (!env.mediaDevices?.getUserMedia) return { ok: false, reason: 'This browser does not give pages access to a microphone.' };
  if (!env.MediaRecorder) return { ok: false, reason: 'This browser cannot record audio (no MediaRecorder).' };
  const mimeType = VO_MIME_TYPES.find((t) => env.MediaRecorder?.isTypeSupported?.(t) ?? false);
  if (!mimeType) return { ok: false, reason: 'This browser cannot record any audio format the editor accepts.' };
  return { ok: true, mimeType };
}

function defaultEnv() {
  if (typeof window === 'undefined') return {};
  return {
    MediaRecorder: (window as unknown as { MediaRecorder?: { isTypeSupported?: (t: string) => boolean } }).MediaRecorder,
    mediaDevices: typeof navigator !== 'undefined' ? navigator.mediaDevices : undefined,
    isSecureContext: window.isSecureContext,
  };
}

/** The file name a take is uploaded under — timestamped so takes never collide. */
export function voFileName(timelineName: string, at: Date, mimeType: string): string {
  const ext = mimeType.startsWith('audio/mp4') ? 'm4a' : mimeType.startsWith('audio/ogg') ? 'ogg' : 'webm';
  const stamp = at.toISOString().replace(/[-:]/g, '').replace(/\..+$/, '').replace('T', '-');
  const base = timelineName.replace(/[^A-Za-z0-9 _-]+/g, '').trim().replace(/\s+/g, '-').slice(0, 40) || 'timeline';
  return `vo-${base}-${stamp}.${ext}`;
}

/**
 * The ops that place a recorded take of `durationFrames` at `atFrame`: onto the first unlocked
 * audio track free over that span, else onto a new audio track appended at the bottom.
 */
export function compilePlaceVoiceover(
  doc: EditTimeline,
  projectFileId: string,
  atFrame: number,
  durationFrames: number,
  ids: { itemId: string; trackId: string },
  name = 'Voiceover',
  /** R08.7: always a fresh "Voiceover" track (the recorder's contract); false reuses the first free audio track. */
  newTrack = false,
  /** Where the new track goes (a retake takes the place of the track it replaces). */
  trackIndex?: number,
): EditOp[] {
  const frames = Math.max(1, Math.round(durationFrames));
  const start = Math.max(0, Math.round(atFrame));
  const clip: MediaClip = {
    id: ids.itemId,
    type: 'mediaClip',
    name,
    recordIn: start,
    recordOut: start + frames,
    assetRef: { kind: 'projectFile', projectFileId, stepResultId: null },
    sourceIn: 0,
    playbackRate: 1,
    enabled: true,
    provenance: { origin: 'manual', editedByUser: true },
  };
  const free = newTrack ? undefined : doc.tracks.find((t) => t.kind === 'audio' && !t.locked && !t.items.some((i) => i.recordIn < clip.recordOut && clip.recordIn < i.recordOut));
  if (free) return [{ type: 'placeOnTop', trackId: free.id, item: clip }];
  const track: Track = { id: ids.trackId, kind: 'audio', name: newTrack ? 'Voiceover' : 'VO', height: 56, locked: false, enabled: true, mute: false, solo: false, busId: null, items: [clip] };
  return [{ type: 'addTrack', index: trackIndex ?? doc.tracks.length, track }];
}

/** A running take: `stop()` resolves with the recorded blob and its measured duration. */
export interface VoTake {
  stop(): Promise<{ blob: Blob; durationSeconds: number }>;
  cancel(): void;
  /** The input level right now, 0..1 (peak of the last audio window); absent when the take cannot meter. */
  level?(): number;
  /** Starts the recorder when the take was opened with `deferStart` (the count-in runs between open and start). */
  startRecording?(): void;
}

export interface VoStartOptions {
  deviceId?: string | null;
  /** Open the microphone and meter it, but hold the recorder until `startRecording()`. */
  deferStart?: boolean;
  now?: () => number;
  /** Called once, with a plain-words sentence, when the take reaches the size or length limit (the caller then finishes the take). */
  onLimit?: (message: string) => void;
}

/** A take is cut at 30 minutes or 200 MB, whichever comes first, so a forgotten recording cannot fill the browser or the upload. */
export const VO_MAX_SECONDS = 30 * 60;
export const VO_MAX_BYTES = 200 * 1024 * 1024;
export const VO_LIMIT_MESSAGE = 'The recording reached its limit (30 minutes or 200 MB), so it was stopped and saved. Record the rest as a new take.';

/** What a failed microphone request means in a sentence with a next step (VO-02). */
export function micErrorSentence(error: unknown, fallback = 'The microphone could not be opened.'): string {
  const name = typeof error === 'object' && error !== null && 'name' in error ? String((error as { name: unknown }).name) : '';
  switch (name) {
    case 'NotAllowedError':
    case 'SecurityError':
    case 'PermissionDeniedError':
      return 'The browser blocked the microphone. Allow it from the lock icon beside the address, or upload a recording instead.';
    case 'NotFoundError':
    case 'DevicesNotFoundError':
      return 'No microphone was found. Plug one in, or upload a recording instead.';
    case 'NotReadableError':
    case 'TrackStartError':
      return 'Another app is using the microphone. Close it and try again, or upload a recording instead.';
    case 'OverconstrainedError':
      return 'That microphone is not available any more. Pick another one.';
    case 'AbortError':
      return 'The microphone stopped responding. Try again.';
    default:
      return fallback;
  }
}

/** The audio inputs the browser lists (labels are blank until the microphone has been allowed once). */
export async function listAudioInputs(): Promise<{ id: string; label: string }[]> {
  if (typeof navigator === 'undefined' || !navigator.mediaDevices?.enumerateDevices) return [];
  const devices = await navigator.mediaDevices.enumerateDevices();
  let n = 0;
  return devices.filter((d) => d.kind === 'audioinput').map((d) => ({ id: d.deviceId, label: d.label.trim() || `Microphone ${++n}` }));
}

/** Starts recording from the chosen (or default) microphone. Browser-only. */
export async function startVoTake(mimeType: string, options: VoStartOptions = {}): Promise<VoTake> {
  const now = options.now ?? (() => performance.now());
  const audio: MediaTrackConstraints = { echoCancellation: true, noiseSuppression: false, autoGainControl: false };
  if (options.deviceId) audio.deviceId = { exact: options.deviceId };
  const stream = await navigator.mediaDevices.getUserMedia({ audio });
  const Recorder = (window as unknown as { MediaRecorder: typeof MediaRecorder }).MediaRecorder;
  const recorder = new Recorder(stream, { mimeType });
  const chunks: Blob[] = [];
  let bytes = 0;
  let limitHit = false;
  recorder.ondataavailable = (event) => {
    if (event.data.size > 0) {
      chunks.push(event.data);
      bytes += event.data.size;
    }
    if (!limitHit && running && (bytes >= VO_MAX_BYTES || (now() - started) / 1000 >= VO_MAX_SECONDS)) {
      limitHit = true;
      options.onLimit?.(VO_LIMIT_MESSAGE);
    }
  };

  // A meter that must never break recording: if the analyser cannot be built the take just has no level.
  let context: AudioContext | null = null;
  let analyser: AnalyserNode | null = null;
  let window_: Float32Array<ArrayBuffer> | null = null;
  try {
    const Ctx = window.AudioContext ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (Ctx) {
      context = new Ctx();
      analyser = context.createAnalyser();
      analyser.fftSize = 1024;
      context.createMediaStreamSource(stream).connect(analyser);
      window_ = new Float32Array(analyser.fftSize);
    }
  } catch {
    analyser = null;
  }

  let started = now();
  let running = false;
  const begin = () => {
    if (running) return;
    running = true;
    started = now();
    recorder.start(250);
  };
  if (!options.deferStart) begin();
  const release = () => {
    stream.getTracks().forEach((t) => t.stop());
    void context?.close().catch(() => undefined);
  };
  return {
    startRecording: begin,
    level: analyser && window_ ? () => {
      analyser.getFloatTimeDomainData(window_);
      let peak = 0;
      for (let i = 0; i < window_.length; i++) peak = Math.max(peak, Math.abs(window_[i]));
      return Math.min(1, peak);
    } : undefined,
    stop: () =>
      new Promise((resolve) => {
        if (!running) {
          release();
          resolve({ blob: new Blob([], { type: mimeType }), durationSeconds: 0 });
          return;
        }
        recorder.onstop = () => {
          release();
          resolve({ blob: new Blob(chunks, { type: mimeType }), durationSeconds: (now() - started) / 1000 });
        };
        recorder.stop();
      }),
    cancel: () => {
      recorder.onstop = () => release();
      if (recorder.state !== 'inactive') recorder.stop();
      else release();
    },
  };
}

/** Shows a level as a 0..1 fill of a bar, on a gentle square-root curve so quiet speech still moves it. */
export const meterFill = (level: number): number => Math.round(Math.sqrt(Math.min(1, Math.max(0, level))) * 100);

/** The count-in numbers, 3 then 2 then 1. */
export const COUNT_IN = [3, 2, 1] as const;
