/**
 * Public surface of the preview compositor.
 *
 * `createPreviewSession` picks the best session the current browser can run:
 *   1. `live` when `VideoDecoder` (WebCodecs) and WebGL2 are both available;
 *   2. `fallback` (single `<video>` over the proxy) when WebCodecs is missing but a video element
 *      can be mounted;
 *   3. `unavailable` otherwise, with the reason.
 *
 * Headless-safe: nothing here touches the DOM until a canvas/video element is handed in, and the
 * mediabunny demuxer is imported lazily inside the live path only.
 */

import type { EditTimeline, MediaClip } from '../types';
import { createFallbackPreviewSession, FALLBACK_REASON } from './fallback-session';
import { createLivePreviewSession, type LiveSessionOptions } from './live-session';
import { probeGl, resetGlProbe } from './gl-probe';
import { REASON_COMPOSITOR_FAILED, REASON_NO_WEBGL2, REASON_USER_CHOICE } from './fault-reasons';
import type { FlagsFor } from './scheduler';
import { unavailablePreviewSession, type PreviewFault, type PreviewSession } from './session';

export * from './session';
export * from './parity';
export * from './scheduler';
export * from './drop-frame-ledger';
export * from './decoder-pool';
export * from './proxy-urls';
export * from './stabilize-proxy';
export * from './honesty';
export * from './readiness';
export * from './device-budget';
export * from './resolution-governor';
export * from './fault-reasons';
export { resetGlProbe } from './gl-probe';
export { createLivePreviewSession } from './live-session';
export { createFallbackPreviewSession, FALLBACK_REASON } from './fallback-session';

export interface CreatePreviewSessionOptions {
  canvas: HTMLCanvasElement | null;
  video: HTMLVideoElement | null;
  getDoc(): EditTimeline;
  flags: FlagsFor;
  proxyUrlFor(clip: MediaClip): string | null;
  /** The picture's url when it differs from the sound's (a stabilised clip plays the stabilised proxy's picture, the source's sound). Live preview only. */
  pictureUrlFor?(clip: MediaClip): string | null;
  playbackResolution?: 'full' | 'half' | 'quarter';
  /** Auto quality: draw no larger than the viewer shows the picture, capped at this long side (see `LiveSessionOptions.fitDisplay`). */
  fitDisplay?: { maxLongSide: number };
  /** Force a kind (the viewer's "degraded mode" toggle and the tests use this). */
  forceKind?: 'live' | 'fallback';
  /** "Try live preview again": ask the browser afresh whether WebGL2 works (the user may have turned graphics acceleration on). */
  reprobe?: boolean;
  live?: Partial<Pick<LiveSessionOptions, 'openSource' | 'poolConfig' | 'audioContext' | 'requestFrame' | 'cancelFrame' | 'now' | 'getGradeBypass' | 'segmentationTracksFor' | 'maskPreviewUrlFor' | 'sourceWidthFor' | 'inputStageFor' | 'getViewingLut' | 'gradeLutFor' | 'adaptive'>>;
}

export function hasWebCodecs(): boolean {
  return typeof globalThis !== 'undefined' && typeof (globalThis as { VideoDecoder?: unknown }).VideoDecoder === 'function';
}

/** Whether this browser can make the compositor's WebGL2 context. Answered by the page's one shared probe (gl-probe.ts), which releases its context at once. */
export function hasWebGl2(_canvas: HTMLCanvasElement | null): boolean {
  return probeGl().webGl2;
}

/** Test-only: forget the probe's answer so a case can re-probe against a fresh stub. */
export function __resetGlProbeForTests(): void {
  resetGlProbe();
}

export function createPreviewSession(options: CreatePreviewSessionOptions): PreviewSession {
  if (options.reprobe) resetGlProbe();
  const webCodecs = hasWebCodecs();
  const webGl2 = hasWebGl2(options.canvas);
  const wantLive = options.forceKind !== 'fallback' && webCodecs && options.canvas && webGl2;

  if (wantLive && options.canvas) {
    const live = createLivePreviewSession({
      canvas: options.canvas,
      getDoc: options.getDoc,
      flags: options.flags,
      proxyUrlFor: options.proxyUrlFor,
      pictureUrlFor: options.pictureUrlFor,
      playbackResolution: options.playbackResolution,
      fitDisplay: options.fitDisplay,
      ...options.live,
    });
    if (live) return live;
  }

  if (options.forceKind === 'live') {
    return unavailablePreviewSession('The live compositor needs WebCodecs and WebGL2, and this browser offers neither.');
  }

  if (options.video) {
    // The cause is known where it happens, so the badge, the note and the action say the true thing.
    const fault: PreviewFault =
      !webCodecs ? 'noWebCodecs' : !webGl2 ? 'noWebGl2' : options.forceKind === 'fallback' ? 'userChoice' : 'compositorFailed';
    const reason =
      fault === 'userChoice' ? REASON_USER_CHOICE : fault === 'noWebCodecs' ? FALLBACK_REASON : fault === 'noWebGl2' ? REASON_NO_WEBGL2 : REASON_COMPOSITOR_FAILED;
    if (fault === 'compositorFailed') {
      // WebGL2 + WebCodecs are present, so reaching here means createGlCompositor itself failed - a genuine
      // failure worth surfacing in production too. The compositor logs the exact failing step.
      console.warn('[preview] the live compositor failed to initialise although WebCodecs + WebGL2 are available; the compositor error above names the exact step', { hasCanvas: !!options.canvas });
    } else if (process.env.NODE_ENV !== 'production') {
      console.debug('[preview] simple preview:', { fault, webCodecs, webGl2, hasCanvas: !!options.canvas });
    }
    return createFallbackPreviewSession({
      video: options.video,
      getDoc: options.getDoc,
      flags: options.flags,
      proxyUrlFor: options.proxyUrlFor,
      requestFrame: options.live?.requestFrame,
      cancelFrame: options.live?.cancelFrame,
      now: options.live?.now,
      getGradeBypass: options.live?.getGradeBypass,
      reason,
      fault,
      liveAvailable: webCodecs && webGl2 && !!options.canvas,
    });
  }

  return unavailablePreviewSession(
    webCodecs
      ? 'The viewer needs a WebGL2 canvas to composite, and none could be created.'
      : 'This browser has neither WebCodecs nor a video element to fall back to.',
  );
}
