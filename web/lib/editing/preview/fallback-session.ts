/**
 * The §3.5 fallback session, confirmed by spike (a): without WebCodecs the viewer plays a single
 * `<video>` over the proxy of the TOP video clip under the playhead, seeked per frame, with the edit
 * applied only to the cut list. Labelled as degraded; it cannot stack layers or mix audio tracks
 * beyond the element's own audio. The dropped-frame ledger here counts seeks that did not settle
 * before the next deadline.
 */

import type { EditTimeline, MediaClip } from '../types';
import { DropFrameLedger } from './drop-frame-ledger';
import { activeLayersAt, fpsOf, sourceTimeSec, type FlagsFor } from './scheduler';
import type { MissingLayer, PlaybackStats, PreviewFault, PreviewSession } from './session';
import { effectiveGrade } from '../color/grade';
import { previewGradeOf, type PreviewGrade } from '../color/preview-grade';
import { svgGradeFilterOf, type SvgGradeFilter } from '../color/svg-grade';
import { timelineEndFrame } from '../transport/navigation';
import { OUTPUT_CHECK_MAX_FAILS, OUTPUT_CHECK_MAX_INTERVAL_MS, isTransientProbe, outputCheckDelay, probeOutput, type OutputProbe } from '@/lib/outputs/output-check';

export interface FallbackSessionOptions {
  video: HTMLVideoElement;
  getDoc(): EditTimeline;
  flags: FlagsFor;
  proxyUrlFor(clip: MediaClip): string | null;
  requestFrame?: (cb: (t: number) => void) => number;
  cancelFrame?: (handle: number) => void;
  now?: () => number;
  /** Override the default reason (set by createPreviewSession to name the actual gap). */
  reason?: string;
  /** The one cause behind this session (the viewer words the hint and picks the action from it). */
  fault?: PreviewFault;
  /** The live preview could run here, so the viewer may offer "Use live preview". */
  liveAvailable?: boolean;
  /** When it returns true the grade is not applied (the before/after toggle). */
  getGradeBypass?: () => boolean;
  /** Test seam: how a failed URL is asked about (defaults to one ranged request). */
  probe?: (url: string) => Promise<OutputProbe>;
}

export const FALLBACK_REASON =
  'WebCodecs is not available in this browser, so the viewer plays a single clip at a time over its proxy. Layers above the top clip, titles and audio mixing are not previewed.';

export function createFallbackPreviewSession(options: FallbackSessionOptions): PreviewSession {
  const now = options.now ?? (() => performance.now());
  const requestFrame = options.requestFrame ?? ((cb) => requestAnimationFrame(cb));
  const cancelFrame = options.cancelFrame ?? ((h) => cancelAnimationFrame(h));
  const ledger = new DropFrameLedger();
  const listeners = new Set<() => void>();
  const notify = () => {
    for (const l of listeners) l();
  };

  let frame = 0;
  let speed = 0;
  let rafHandle: number | null = null;
  let wallStart = 0;
  let wallStartFrame = 0;
  let lastDeadline = -1;
  let currentUrl: string | null = null;
  let disposed = false;
  let seeking = false;
  let missing: MissingLayer[] = [];
  let gradeFilter: SvgGradeFilter | null = null;

  // Building the tables is the costly part; a frame with the same folded grade as the last reuses its filter.
  let lastGradeKey: string | null = null;
  let lastGradeFilter: SvgGradeFilter | null = null;
  function filterFor(grade: PreviewGrade): SvgGradeFilter | null {
    const key = JSON.stringify(grade);
    if (key !== lastGradeKey) {
      lastGradeKey = key;
      lastGradeFilter = svgGradeFilterOf(grade);
    }
    return lastGradeFilter;
  }

  // A clip that was only just made can answer "still being prepared" (the element then errors and, left alone,
  // stays black). Each failing URL is asked about on its own back-off timer (5 s up to 60 s); a network error or
  // 5xx does not end recovery until it repeats; a ready answer sets the source again when that clip is on screen.
  const probe = options.probe ?? probeOutput;
  const rechecks = new Map<string, ReturnType<typeof setTimeout>>();
  function stopRecheck(): void {
    for (const timer of rechecks.values()) clearTimeout(timer);
    rechecks.clear();
  }
  function onVideoError(): void {
    const failedUrl = currentUrl;
    if (!failedUrl || rechecks.has(failedUrl)) return;
    let attempt = 0;
    let fails = 0;
    const schedule = (): void => {
      rechecks.set(failedUrl, setTimeout(ask, outputCheckDelay(attempt)));
    };
    const ask = (): void => {
      if (typeof document !== 'undefined' && document.visibilityState === 'hidden') { schedule(); return; }
      void probe(failedUrl).then((result) => {
        if (disposed) return;
        if (isTransientProbe(result)) {
          fails += 1;
          if (fails >= OUTPUT_CHECK_MAX_FAILS) { rechecks.delete(failedUrl); return; }
          attempt += 1;
          schedule();
          return;
        }
        fails = 0;
        if (result.kind === 'pending' || result.kind === 'unavailable') {
          attempt += 1;
          rechecks.set(failedUrl, setTimeout(ask, result.kind === 'unavailable' ? OUTPUT_CHECK_MAX_INTERVAL_MS : outputCheckDelay(attempt)));
          return;
        }
        rechecks.delete(failedUrl);
        if (result.kind === 'ok' && currentUrl === failedUrl) {
          currentUrl = null;
          if (speed === 0) show(frame, false);
        }
      });
    };
    rechecks.set(failedUrl, setTimeout(ask, 0));
  }
  options.video.addEventListener('error', onVideoError);

  function show(target: number, countDeadline: boolean): void {
    const doc = options.getDoc();
    const layers = activeLayersAt(doc, target, options.flags);
    const top = [...layers].reverse().find((l) => l.item.type === 'mediaClip');
    const video = options.video;
    if (!top || top.item.type !== 'mediaClip') {
      missing = [];
      gradeFilter = null;
      if (currentUrl !== null) {
        video.removeAttribute('src');
        video.load();
        currentUrl = null;
      }
      if (countDeadline) ledger.recordPresented(0, now(), true);
      frame = target;
      notify();
      return;
    }
    const url = options.proxyUrlFor(top.item);
    missing = url ? [] : [{ itemId: top.item.id, trackId: top.track.id, cause: 'noUrl' }];
    // The top clip's folded grade (group stack, then its own), as the live session folds it; none when bypassed or at identity.
    gradeFilter = options.getGradeBypass?.() === true ? null : filterFor(previewGradeOf(effectiveGrade(doc, top.item)));
    if (!url) {
      if (countDeadline) ledger.recordPresented(0, now(), true);
      frame = target;
      notify();
      return;
    }
    if (currentUrl !== url) {
      video.src = url;
      currentUrl = url;
    }
    const t = sourceTimeSec(top.item, target, doc.settings.rate);
    if (countDeadline && seeking) {
      ledger.recordDropped(1);
    } else {
      seeking = true;
      const started = now();
      const onSeeked = () => {
        seeking = false;
        video.removeEventListener('seeked', onSeeked);
        if (countDeadline) ledger.recordPresented(now() - started, now(), false);
        notify();
      };
      video.addEventListener('seeked', onSeeked);
      try {
        video.currentTime = t;
      } catch {
        seeking = false;
        video.removeEventListener('seeked', onSeeked);
      }
    }
    frame = target;
    notify();
  }

  function tick(): void {
    if (speed === 0) return;
    const doc = options.getDoc();
    const fps = fpsOf(doc.settings.rate);
    const end = timelineEndFrame(doc);
    const target = Math.floor(wallStartFrame + ((now() - wallStart) / 1000) * speed * fps);
    if (target < 0 || (end > 0 && target >= end)) {
      pause();
      show(Math.max(0, Math.min(end > 0 ? end - 1 : 0, target)), false);
      return;
    }
    if (lastDeadline >= 0) {
      const skipped = Math.abs(target - lastDeadline) - 1;
      if (skipped > 0) ledger.recordDropped(skipped);
    }
    if (target !== lastDeadline) {
      lastDeadline = target;
      show(target, true);
    }
    rafHandle = requestFrame(tick);
  }

  function pause(): void {
    speed = 0;
    if (rafHandle !== null) cancelFrame(rafHandle);
    rafHandle = null;
    options.video.pause();
    notify();
  }

  return {
    kind: 'fallback',
    reason: options.reason ?? FALLBACK_REASON,
    fault: options.fault ?? null,
    liveAvailable: options.liveAvailable ?? false,
    gradeFilter: () => gradeFilter,
    seek(next) {
      frame = Math.max(0, Math.trunc(next));
      // A seek during playback re-anchors the clock there, so the next tick does not jump back.
      if (speed !== 0) {
        wallStart = now();
        wallStartFrame = frame;
        lastDeadline = -1;
      }
      show(frame, false);
    },
    play(nextSpeed) {
      if (nextSpeed === 0) {
        pause();
        return;
      }
      speed = nextSpeed;
      wallStart = now();
      wallStartFrame = frame;
      lastDeadline = -1;
      options.video.muted = nextSpeed !== 1;
      rafHandle = requestFrame(tick);
      notify();
    },
    pause,
    missingLayers: () => missing,
    currentFrame: () => frame,
    isPlaying: () => speed !== 0,
    stats: (): PlaybackStats => ledger.snapshot(),
    subscribe(listener) {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
    invalidate() {
      if (speed === 0) show(frame, false);
    },
    dispose() {
      disposed = true;
      stopRecheck();
      options.video.removeEventListener('error', onVideoError);
      pause();
      listeners.clear();
    },
  };
}
