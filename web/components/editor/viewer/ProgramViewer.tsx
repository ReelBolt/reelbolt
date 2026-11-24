'use client';

import { maskPreviewUrl, segmentationAnalysesOf } from '@/lib/editing/preview/segmentation-tracks';
import { useSegmentationTracks } from '@/lib/hooks/use-segmentation-tracks';
import React, { useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Box, Group, Stack, Text, Tooltip } from '@mantine/core';
import { IconInfoCircle } from '@tabler/icons-react';
import type { AssetRef, EditTimeline, MediaClip } from '@/lib/editing/types';
import {
  createPreviewSession,
  hasWebCodecs,
  classifyParity,
  createDropWatch,
  notReadyClipsAt,
  parityAtFrame,
  type StabilizeLookup,
  type PreviewQuality,
  type ReadinessStore,
  type FlagsFor,
  type MissingLayer,
  type PlaybackStats,
  type ContextLoss,
  type PreviewSession,
  type PreviewSessionKind,
  type ProxyStatus,
} from '@/lib/editing/preview';
import { rangeEnded, rangeStep, readPlayhead, rememberReported, type PlayRange } from '@/lib/editing/transport';
import type { HdrKindLookup } from '@/lib/editing/media/hdr';
import type { SvgGradeFilter } from '@/lib/editing/color/svg-grade';
import type { ViewingLutSpec } from '@/lib/editing/color/viewer-color';
import type { GradeLutResolver } from '@/lib/editing/preview/grade-pass-plan';
import { ViewingLutBadge } from './ViewingLutMenu';
import { RenderRequiredBadge } from './RenderRequiredBadge';
import { createRecentDrops } from '@/lib/editing/preview/recent-drops';
import { ToolDockContext } from './tool-dock';
import { previewText } from './preview-text';
import { REDUCED_QUALITY_ADAPTIVE } from '@/lib/editing/preview/live-session';
import { GradeFilterDefs } from './GradeFilterDefs';
import { ContextLossOverlay, DroppedFramesChip, LivePreviewAction, ReadinessChip, SimplePreviewLabel, ViewerReasonOverlay } from './ViewerStatus';
import { useDeviceClass } from '@/lib/editing/use-device-class';
import { useFormat, useT } from '@/i18n';

export interface ProgramViewerProps {
  doc: EditTimeline;
  /** Called on every tick; must return the CURRENT document (so edits show on the next frame). */
  getDoc: () => EditTimeline;
  flags: FlagsFor;
  proxyUrlFor: (clip: MediaClip) => string | null;
  /** The picture's url when it is not the sound's (the stabilised proxy); see CreatePreviewSessionOptions.pictureUrlFor. */
  pictureUrlFor?: (clip: MediaClip) => string | null;
  /** R10.7: whether a clip's source is HDR (the parity badge says the preview shows it approximately). */
  hdrKindFor?: HdrKindLookup;
  /** Whether a stabilised clip's stabilised proxy is ready (parity wording); see StabilizeLookup. */
  stabilizeFor?: StabilizeLookup;
  /** The clip's source width in pixels from the media probe, or null when unknown (the mask feather then stays unscaled). */
  sourceWidthFor?: (clip: MediaClip) => number | null;
  playheadFrame: number;
  speed: number;
  /** The size the live preview decodes at (never `auto`: the caller resolves Auto by device). */
  playbackResolution: Exclude<PreviewQuality, 'auto'>;
  /**
   * Auto quality: the live preview draws no larger than the viewer shows the picture, with its long side capped at
   * this many device pixels. Null/absent = `playbackResolution` is an exact fraction of the timeline.
   */
  fitMaxLongSide?: number | null;
  forceFallback: boolean;
  /** Why the simple preview was chosen for the user (Auto on a low-budget device), for the label's tooltip. */
  simpleReason?: string | null;
  /** R04.1: the derivative pipeline's progress per source; absent = no readiness line. */
  readiness?: ReadinessStore;
  /** Ask again for these sources (Try again). */
  onRetryPreview?: (mediaRefs: string[]) => void;
  /** Prepare every picture source of the timeline. */
  onPrepareAll?: () => void;
  /** A badge or readiness line about a clip was chosen: select it and bring it into view. */
  onJumpToClip?: (itemId: string) => void;
  /** R04.5: playback is dropping frames and the user took the offer: run the simple preview. */
  onSwitchToSimple?: () => void;
  /** The session advanced the playhead (during playback). */
  onFrame: (frame: number) => void;
  onStats: (stats: PlaybackStats, kind: PreviewSessionKind) => void;
  /** The session stopped on its own (end of timeline). */
  onEnded: () => void;
  /** P5: the live session, for the mixer's meters (null when none). */
  onSession?: (session: PreviewSession | null) => void;
  /** P6: the compositor's canvas, for saving a (preview-quality) still of the current frame. */
  onCanvas?: (canvas: HTMLCanvasElement | null) => void;
  /** P7: bypass every grade in the preview (before/after). Read per frame, so no session restart. */
  gradeBypass?: boolean;
  /** P7: a gallery still drawn over the left `wipe` fraction of the picture (the reference wipe). */
  referenceOverlay?: { dataUrl: string; wipe: number } | null;
  /** Bumped whenever a source's URL changes (a proxy arrived): the paused frame is re-presented. */
  proxyVersion?: number;
  /** Where each clip's bytes come from, so a hole says why and the badge can say "not ready". */
  proxyStatusFor?: (clip: MediaClip) => ProxyStatus;
  /** PA-7: the Loop / Play in to out range, read on every presented frame (null = play to the end). */
  getPlayRange?: () => PlayRange | null;
  /** R07.2: the handle layer drawn over the picture (its own DOM layer; absent = none). */
  overlay?: React.ReactNode;
  /** R07.2: the Fit / Fill / position menu, shown in the viewer's header row. */
  tools?: React.ReactNode;
  /** Leads the viewer's header row (the Source | Record switch): shares the row, adds none. */
  lead?: React.ReactNode;
  /**
   * The Simple shell: the header says nothing while the preview is exact (no size readout, no badge), and skipped
   * frames are counted on the picture only while they are being skipped.
   */
  quiet?: boolean;
  /**
   * The Source monitor is showing: the session (kept alive, no remount) is paused and does no work -
   * no seek, no re-plan, no decode - until this clears, when it seeks to the playhead and re-presents.
   */
  suspended?: boolean;
  /** The clip's camera conversion for the compositor (`inputStageOf`); absent = no input stage. */
  inputStageFor?: (clip: MediaClip) => { profile: string; baked: boolean } | null;
  /** The viewing LUT (preview only), read at every present; null = off. */
  getViewingLut?: () => ViewingLutSpec | null;
  /** The parsed .cube of a grade node's LUT (null while it loads); absent = LUT nodes do not preview. */
  gradeLutFor?: GradeLutResolver;
  /** Whether a grade LUT file has loaded (the parity badge says "still loading" otherwise). */
  gradeLutStatus?: (file: AssetRef) => 'loading' | 'ready' | 'failed';
  /** Identity of the viewing LUT on screen (null = off): a change re-presents the paused frame and the badge is shown while it is set. */
  viewingLutVersion?: string | null;
}

const NO_MISSING: readonly MissingLayer[] = [];
const PENDING_STATUS = (): ProxyStatus => ({ state: 'pending' });
const SKELETON_BACKGROUND = '#14171c';
const sameMissing = (a: readonly MissingLayer[], b: readonly MissingLayer[]) =>
  a.length === b.length && a.every((m, i) => m.itemId === b[i].itemId && m.cause === b[i].cause);

/**
 * Hosts the preview session. The viewer plays THE TIMELINE: the session composites whatever the
 * document says is on screen at the playhead, and the parity badge says how faithfully.
 */
export function ProgramViewer(props: ProgramViewerProps) {
  const deviceClass = useDeviceClass();
  const t = useT('editor.viewer.programViewer');
  const nf = useFormat();
  const { doc, getDoc, flags, proxyUrlFor, pictureUrlFor, hdrKindFor, stabilizeFor, playheadFrame, speed, playbackResolution, fitMaxLongSide = null, forceFallback, simpleReason = null, readiness, onRetryPreview, onPrepareAll, onJumpToClip, onSwitchToSimple, onFrame, onStats, onEnded, onSession, onCanvas, gradeBypass = false, referenceOverlay = null, proxyVersion = 0, proxyStatusFor, getPlayRange, overlay = null, tools = null, lead = null, suspended = false, quiet = false } = props;
  const bypassRef = useRef(gradeBypass);
  const inputStageRef = useRef(props.inputStageFor);
  const viewingLutRef = useRef(props.getViewingLut);
  const gradeLutRef = useRef(props.gradeLutFor);
  const viewingLutVersion = props.viewingLutVersion ?? null;
  useEffect(() => {
    inputStageRef.current = props.inputStageFor;
    viewingLutRef.current = props.getViewingLut;
    gradeLutRef.current = props.gradeLutFor;
  }, [props.inputStageFor, props.getViewingLut, props.gradeLutFor]);
  // Tracked boxes for the segmentation masks the document names (no request when it has none).
  const analysisIds = useMemo(() => segmentationAnalysesOf(doc), [doc]);
  const { lookup: tracksFor, version: tracksVersion } = useSegmentationTracks(doc.projectId, analysisIds);
  const sourceWidthRef = useRef(props.sourceWidthFor);
  useEffect(() => {
    sourceWidthRef.current = props.sourceWidthFor;
  }, [props.sourceWidthFor]);
  const tracksRef = useRef(tracksFor);
  useEffect(() => {
    tracksRef.current = tracksFor;
  }, [tracksFor]);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const onCanvasRef = useRef(onCanvas);
  useEffect(() => {
    onCanvasRef.current = onCanvas;
  }, [onCanvas]);
  useEffect(() => {
    onCanvasRef.current?.(canvasRef.current);
    return () => onCanvasRef.current?.(null);
  }, []);
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const sessionRef = useRef<PreviewSession | null>(null);
  // Auto quality: the long side (device pixels) the picture is shown at, measured from the frame; the session draws no larger.
  const frameRef = useRef<HTMLDivElement | null>(null);
  const displayLongSideRef = useRef<number | null>(null);
  useEffect(() => {
    const frame = frameRef.current;
    if (!frame || typeof ResizeObserver === 'undefined') return;
    let timer: ReturnType<typeof setTimeout> | null = null;
    const measure = () => {
      timer = null;
      const rect = frame.getBoundingClientRect();
      const long = Math.max(rect.width, rect.height);
      if (!(long > 0)) return;
      // More than two device pixels per CSS pixel is not visible in moving pictures and quadruples the fill.
      const px = Math.round(long * Math.min(2, window.devicePixelRatio || 1));
      displayLongSideRef.current = px;
      sessionRef.current?.setDisplayLongSide?.(px);
    };
    const observer = new ResizeObserver(() => {
      // A panel being dragged reports many sizes: the session is told once it settles.
      if (timer !== null) clearTimeout(timer);
      timer = setTimeout(measure, 120);
    });
    observer.observe(frame);
    measure();
    return () => {
      observer.disconnect();
      if (timer !== null) clearTimeout(timer);
    };
  }, []);
  const [adaptiveScale, setAdaptiveScale] = useState(1);
  const [kind, setKind] = useState<PreviewSessionKind>('unavailable');
  const [reason, setReason] = useState<string | null>(null);
  const [reducedQuality, setReducedQuality] = useState<string | null>(null);
  const [missing, setMissing] = useState<readonly MissingLayer[]>(NO_MISSING);
  // VF12: until the first session exists the frame shows a neutral skeleton, not "No preview" or black.
  const [booted, setBooted] = useState(false);
  // What the session says about itself beyond its kind: the cause of a Simple preview, whether live could run, a lost graphics context, the Simple preview's grade.
  const [fault, setFault] = useState<PreviewSession['fault']>(null);
  const [liveAvailable, setLiveAvailable] = useState(false);
  const [contextLoss, setContextLoss] = useState<ContextLoss | null>(null);
  const [gradeFilter, setGradeFilter] = useState<SvgGradeFilter | null>(null);
  // "Try live preview again" bumps `attempt` (the session is rebuilt, and WebGL2 is probed afresh); "Use live preview" lifts the Simple preview for as long as the Quality menu's choice stays as it is.
  const [toolDock, setToolDock] = useState<HTMLElement | null>(null);
  const [attempt, setAttempt] = useState(0);
  const [liveOverride, setLiveOverride] = useState(false);
  const [seenForce, setSeenForce] = useState(forceFallback);
  if (seenForce !== forceFallback) {
    setSeenForce(forceFallback);
    setLiveOverride(false);
  }
  const wantSimple = forceFallback && !liveOverride;
  // R04.5: sustained dropped frames while playing live -> offer the simple preview.
  const dropWatch = useRef(createDropWatch());
  const [dropping, setDropping] = useState(false);
  const recentDrops = useRef(createRecentDrops());
  const [recentlyDropped, setRecentlyDropped] = useState(0);
  const quietRef = useRef(quiet);
  useEffect(() => {
    quietRef.current = quiet;
  }, [quiet]);
  const lastSpeed = useRef(0);
  // Frames the session itself reported while playing: a playhead change that is NOT one of these is
  // a user seek (a marker click, Home, an edit jump…) and must reach the session (PA-8).
  const recentFrames = useRef<number[]>([]);
  // Set while the viewer drives the session itself, so its own pause/seek is not read as "ended".
  const steering = useRef(false);
  const getPlayRangeRef = useRef(getPlayRange);
  const onFrameRef = useRef(onFrame);
  const onEndedRef = useRef(onEnded);

  const flagsRef = useRef(flags);
  const proxyRef = useRef(proxyUrlFor);
  const pictureRef = useRef(pictureUrlFor);
  const getDocRef = useRef(getDoc);
  const onSessionRef = useRef(onSession);
  useEffect(() => {
    flagsRef.current = flags;
    proxyRef.current = proxyUrlFor;
    pictureRef.current = pictureUrlFor;
    getDocRef.current = getDoc;
    onSessionRef.current = onSession;
    getPlayRangeRef.current = getPlayRange;
    onFrameRef.current = onFrame;
    onEndedRef.current = onEnded;
  }, [flags, proxyUrlFor, pictureUrlFor, getDoc, onSession, getPlayRange, onFrame, onEnded]);
  // PA-6: a source's URL changed (its proxy arrived): re-present the paused frame now.
  useEffect(() => {
    sessionRef.current?.invalidate();
  }, [proxyVersion, proxyUrlFor]);
  useEffect(() => {
    sessionRef.current?.invalidate();
  }, [viewingLutVersion]);
  useEffect(() => {
    bypassRef.current = gradeBypass;
    // A paused viewer re-presents the frame so the toggle is visible without scrubbing.
    sessionRef.current?.invalidate();
  }, [gradeBypass]);

  const webCodecs = typeof window !== 'undefined' && hasWebCodecs();

  // (Re)create the session when the hosting elements or the degradation settings change.
  useEffect(() => {
    sessionRef.current?.dispose();
    const session = createPreviewSession({
      canvas: canvasRef.current,
      video: videoRef.current,
      getDoc: () => getDocRef.current(),
      flags: (track) => flagsRef.current(track),
      proxyUrlFor: (clip) => proxyRef.current(clip),
      pictureUrlFor: (clip) => (pictureRef.current ? pictureRef.current(clip) : proxyRef.current(clip)),
      playbackResolution,
      fitDisplay: fitMaxLongSide ? { maxLongSide: fitMaxLongSide } : undefined,
      forceKind: wantSimple ? 'fallback' : undefined,
      reprobe: attempt > 0,
      live: { getGradeBypass: () => bypassRef.current, inputStageFor: (clip) => inputStageRef.current?.(clip) ?? null, getViewingLut: () => viewingLutRef.current?.() ?? null, gradeLutFor: (file) => gradeLutRef.current?.(file) ?? null, segmentationTracksFor: (id) => tracksRef.current(id), maskPreviewUrlFor: (analysis, trackId) => maskPreviewUrl(doc.projectId, analysis, trackId), sourceWidthFor: (clip) => sourceWidthRef.current?.(clip) ?? null },
    });
    sessionRef.current = session;
    session.setDisplayLongSide?.(displayLongSideRef.current);
    setAdaptiveScale(session.adaptiveQuality?.scale ?? 1);
    onSessionRef.current?.(session);
    setKind(session.kind);
    setReason(session.reason);
    setReducedQuality(session.reducedQuality ?? null);
    setFault(session.fault ?? null);
    setLiveAvailable(session.liveAvailable === true);
    setContextLoss(session.contextLoss ?? null);
    setGradeFilter(session.gradeFilter?.() ?? null);
    setBooted(true);
    dropWatch.current.reset();
    setDropping(false);

    const steer = (drive: () => void) => {
      steering.current = true;
      try {
        drive();
      } finally {
        steering.current = false;
      }
    };
    const report = (frame: number) => {
      recentFrames.current = rememberReported(recentFrames.current, frame);
      onFrameRef.current(frame);
    };
    const stop = (frame: number | null) => {
      lastSpeed.current = 0;
      steer(() => {
        session.pause();
        if (frame !== null) session.seek(frame);
      });
      report(frame ?? session.currentFrame());
      onEndedRef.current();
    };
    const unsubscribe = session.subscribe(() => {
      setReducedQuality(session.reducedQuality ?? null);
      const loss = session.contextLoss ?? null;
      setContextLoss((prev) => (prev?.state === loss?.state && prev?.message === loss?.message ? prev : loss));
      const nextFilter = session.gradeFilter?.() ?? null;
      setGradeFilter((prev) => (prev?.signature === nextFilter?.signature ? prev : nextFilter));
      const stats = session.stats();
      onStats(stats, session.kind);
      const scaleNow = session.adaptiveQuality?.scale ?? 1;
      setAdaptiveScale((prev) => (prev === scaleNow ? prev : scaleNow));
      if (session.kind === 'live' && session.isPlaying()) {
        // The Simple preview is only offered once the live preview has no smaller size left to try.
        const sustained = dropWatch.current.push({ at: performance.now(), deadlines: stats.deadlines, dropped: stats.droppedFrames }) && (session.adaptiveQuality?.atFloor ?? true);
        setDropping((prev) => (prev === sustained ? prev : sustained));
        if (quietRef.current) {
          const recent = recentDrops.current.push({ at: performance.now(), dropped: stats.droppedFrames });
          setRecentlyDropped((prev) => (prev === recent ? prev : recent));
        }
      } else {
        dropWatch.current.reset();
        setDropping((prev) => (prev ? false : prev));
        recentDrops.current.reset();
        setRecentlyDropped((prev) => (prev === 0 ? prev : 0));
      }
      const nextMissing = session.missingLayers?.() ?? NO_MISSING;
      setMissing((prev) => (sameMissing(prev, nextMissing) ? prev : nextMissing));
      const playingAt = lastSpeed.current;
      if (playingAt === 0 || steering.current) return;
      const frame = session.currentFrame();
      if (session.isPlaying()) {
        // PA-7: Loop wraps and Play in to out stops at the Out mark.
        const action = rangeStep(frame, playingAt, getPlayRangeRef.current?.() ?? null);
        if (action.kind === 'loop') steer(() => session.seek(action.frame));
        else if (action.kind === 'stop') stop(action.frame);
        else report(frame);
        return;
      }
      // The session stopped by itself at a timeline edge: loop back, or surface "ended" so the
      // store's speed goes back to 0 and the transport shows Play again.
      const ended = rangeEnded(getPlayRangeRef.current?.() ?? null, playingAt);
      if (ended.kind === 'loop') {
        steer(() => {
          session.seek(ended.frame);
          session.play(playingAt);
        });
        return;
      }
      stop(null);
    });
    session.seek(playheadFrame);
    onStats(session.stats(), session.kind);
    return () => {
      unsubscribe();
      session.dispose();
      sessionRef.current = null;
      onSessionRef.current?.(null);
    };
    // playheadFrame intentionally excluded: seeks are driven by the effect below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [playbackResolution, fitMaxLongSide, wantSimple, attempt]);

  // Scrub when paused; while playing, a playhead the session did not report is a user seek (PA-8).
  useEffect(() => {
    const session = sessionRef.current;
    if (!session || suspended) return;
    if (speed === 0) {
      if (session.currentFrame() !== playheadFrame) session.seek(playheadFrame);
      return;
    }
    // A speed change is pending: the effect below seeks and plays.
    if (lastSpeed.current !== speed) return;
    // An echo is consumed, so a later seek back to a frame already passed still reaches the session.
    const read = readPlayhead(recentFrames.current, playheadFrame);
    recentFrames.current = read.rest;
    if (read.echo) return;
    steering.current = true;
    try {
      session.seek(playheadFrame);
    } finally {
      steering.current = false;
    }
  }, [playheadFrame, speed, suspended]);

  // The Source monitor took the picture: stop any playback now (the session stays alive, so coming back is instant).
  useEffect(() => {
    if (!suspended) return;
    const session = sessionRef.current;
    if (!session || !session.isPlaying()) return;
    steering.current = true;
    try {
      session.pause();
    } finally {
      steering.current = false;
    }
  }, [suspended]);

  // Play/pause/speed changes.
  useEffect(() => {
    const session = sessionRef.current;
    if (!session) return;
    if (speed === lastSpeed.current) return;
    // The Source monitor has the picture: the timeline does not play behind it.
    if (suspended && speed !== 0) return;
    lastSpeed.current = speed;
    recentFrames.current = [];
    steering.current = true;
    try {
      if (speed === 0) {
        session.pause();
      } else {
        session.seek(playheadFrame);
        session.play(speed);
      }
    } finally {
      steering.current = false;
    }
    // playheadFrame read at the moment speed changes, by design.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [speed, suspended]);

  // Edits: re-plan. The document is immutable, so its identity changes on every op.
  useEffect(() => {
    if (suspended) return;
    sessionRef.current?.invalidate();
  }, [doc, tracksVersion, suspended]);

  const parity = useMemo(() => parityAtFrame(doc, playheadFrame, { hdrKindFor, tracksFor, gradeLutStatus: props.gradeLutStatus, stabilizeFor }), [doc, playheadFrame, hdrKindFor, tracksFor, props.gradeLutStatus, stabilizeFor]);
  // proxyVersion is the signal that a status changed.
  const notReady = useMemo(
    () => (proxyStatusFor ? notReadyClipsAt(doc, playheadFrame, flags, proxyStatusFor) : []),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [doc, playheadFrame, flags, proxyStatusFor, proxyVersion],
  );
  const documentParity = useMemo(() => classifyParity(doc, { hdrKindFor, tracksFor, gradeLutStatus: props.gradeLutStatus, stabilizeFor }), [doc, hdrKindFor, tracksFor, props.gradeLutStatus, stabilizeFor]);
  const statusFor = proxyStatusFor ?? PENDING_STATUS;
  // Auto on a weak desktop starts live at quarter size: its sentence (the plan's) joins the session's own reduced-quality line.
  const adaptiveNote = kind === 'live' && reducedQuality === REDUCED_QUALITY_ADAPTIVE;
  const reducedLine = kind === 'live' ? [adaptiveNote ? null : reducedQuality, !forceFallback ? simpleReason : null].filter((part): part is string => Boolean(part)).map((part) => previewText(part, t.msg)).join(' ') || null : null;
  const retryLive = () => setAttempt((n) => n + 1);
  // While the Source monitor shows, the program viewer's picture, tools and notes are inert; only the switch in `lead` stays live.
  const inertWhen = suspended ? { inert: true } : {};
  // The phone has no Source monitor, and its stylesheet addresses the viewer's direct children: it gets no wrappers at all.
  const inertZone = (children: React.ReactNode) => (phone ? children : <div style={{ display: 'contents' }} {...inertWhen}>{children}</div>);
  const aspect = doc.settings.height > 0 ? doc.settings.width / doc.settings.height : 16 / 9;

  // The way back from the Simple preview. A phone's picture is too small to carry it, so there it sits in the header.
  const phone = deviceClass === 'phone';
  const liveAction =
    booted && kind === 'fallback' && (fault === 'userChoice' ? liveAvailable : fault === 'noWebGl2' || fault === 'compositorFailed') ? (
      fault === 'userChoice' ? <LivePreviewAction mode="use" compact={phone} onClick={() => setLiveOverride(true)} /> : <LivePreviewAction mode="retry" onClick={retryLive} />
    ) : null;

  return (
    <ToolDockContext.Provider value={toolDock}>
    <Stack gap={0} style={{ flex: 1, minHeight: 0, overflow: 'hidden' }} data-testid="program-viewer" data-session-kind={kind}>
      {/* 14 px under the controls: the picture's corner handles reach 12 px above the picture and must clear them. */}
      <Group justify="space-between" px="sm" pt={lead ? 0 : 6} pb={14} wrap={lead ? 'nowrap' : 'wrap'} gap="xs" mih={44} style={{ flexShrink: 0 }} data-testid="viewer-header">
        {lead}
        {inertZone(<>
        {!phone && !quiet && (
          <Text size="12px" c="dimmed" ff="monospace" visibleFrom={lead ? 'lg' : 'sm'} style={{ whiteSpace: 'nowrap', flex: '0 0 auto' }}>
          {doc.settings.width}×{doc.settings.height} · {nf.number(doc.settings.rate.num / doc.settings.rate.den, { maximumFractionDigits: 3 })} {t('fps')}
        </Text>
        )}
        {tools}
        {phone && liveAction}
        <RenderRequiredBadge
          parity={parity}
          documentParity={documentParity}
          conditions={{ sessionKind: kind, sessionReason: reason, missing, notReady, reducedQuality: reducedLine, contextLoss: contextLoss?.message ?? null, simpleGrade: kind === 'fallback' && gradeFilter !== null }}
          onJumpToClip={onJumpToClip}
          quiet={quiet}
        />
        </>)}
      </Group>
      {inertZone(<>
      {/* A picture tool's compact bar docks here, above the frame, so it never covers the picture. */}
      <div ref={setToolDock} data-testid="viewer-tool-dock" style={{ flexShrink: 0 }} />
      <Box
        data-testid="program-canvas-area"
        style={{
          position: 'relative',
          width: '100%',
          // A definite basis (not the `flex: 1` percentage basis): Chromium otherwise resolves this size container's
          // `cqh` to 0 when the height comes from flex-grow, and the frame below (sized in cq units) collapses to 0x0.
          flex: '1 1 0px',
          height: 0,
          minHeight: 120,
          padding: '8px 12px 12px',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          overflow: 'hidden',
          // The picture is fitted inside whatever box the layout gives it, letterboxed in the studio
          // background rather than stretched: the box's own size is the query container.
          containerType: 'size',
        }}
      >
        <Box
          ref={frameRef}
          data-testid="program-frame"
          data-adaptive-scale={adaptiveScale}
          data-booted={booted}
          tabIndex={-1}
          style={{
            position: 'relative',
            width: `min(100cqw, calc(100cqh * ${aspect}))`,
            aspectRatio: `${aspect}`,
            // VF12: a neutral skeleton (never pure black) until the session exists or while a hole is explained.
            background: !booted || missing.length > 0 ? SKELETON_BACKGROUND : '#000',
            borderRadius: 4,
            // R07.2: the handle layer's corner handles may sit on the picture's edge, so the frame itself does not clip.
            overflow: 'visible',
          }}
        >
          <canvas
            ref={canvasRef}
            data-testid="program-canvas"
            aria-label={t('programMonitor')}
            style={{ width: '100%', height: '100%', display: kind === 'live' ? 'block' : 'none' }}
          />
          {referenceOverlay && (
            // eslint-disable-next-line @next/next/no-img-element
            <img
              src={referenceOverlay.dataUrl}
              alt={t('referenceStill')}
              data-testid="reference-wipe"
              style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', objectFit: 'fill', pointerEvents: 'none', clipPath: `inset(0 ${Math.round((1 - Math.min(1, Math.max(0, referenceOverlay.wipe))) * 100)}% 0 0)`, borderRight: '1px solid #fff' }}
            />
          )}
          <video
            ref={videoRef}
            data-testid="program-video"
            playsInline
            preload="auto"
            style={{ width: '100%', height: '100%', display: kind === 'fallback' ? 'block' : 'none', objectFit: 'contain', filter: kind === 'fallback' && gradeFilter ? `url(#${gradeFilter.id})` : undefined }}
          />
          {gradeFilter && <GradeFilterDefs filter={gradeFilter} />}
          {overlay}
          {viewingLutVersion !== null && kind === 'live' && <ViewingLutBadge />}
          {!booted && <Box data-testid="viewer-skeleton" aria-hidden style={{ position: 'absolute', inset: 0, background: SKELETON_BACKGROUND }} />}
          {booted && kind !== 'unavailable' && (
            <ViewerReasonOverlay doc={doc} missing={missing} readiness={readiness ?? null} proxyStatusFor={statusFor} proxyVersion={proxyVersion} onRetry={onRetryPreview} />
          )}
          {booted && (readiness || kind === 'fallback' || adaptiveNote || (quiet && recentlyDropped > 0)) && (
            // Status pills sit ON the picture, so they appear and disappear without moving the viewer (VF12).
            <Group gap={6} wrap="wrap" style={{ position: 'absolute', top: 6, left: 6, right: 6, zIndex: 2, pointerEvents: 'none' }} data-testid="viewer-pills">
              {readiness && (
                <Box style={{ pointerEvents: 'auto', background: 'rgba(15,17,21,0.82)', borderRadius: 6 }}>
                  <ReadinessChip doc={doc} readiness={readiness} proxyStatusFor={statusFor} proxyVersion={proxyVersion} onRetry={onRetryPreview} onPrepareAll={onPrepareAll} onJumpToClip={onJumpToClip} />
                </Box>
              )}
              {kind === 'fallback' && (
                <Box px={8} py={4} style={{ background: 'rgba(15,17,21,0.82)', borderRadius: 6 }}>
                  <SimplePreviewLabel reason={reason ?? simpleReason} />
                </Box>
              )}
              {adaptiveNote && (
                <Tooltip label={previewText(REDUCED_QUALITY_ADAPTIVE, t.msg)} multiline w={280} openDelay={200}>
                  <Box px={8} py={4} role="status" data-testid="adaptive-size-pill" style={{ pointerEvents: 'auto', background: 'rgba(15,17,21,0.82)', borderRadius: 6 }}>
                    <Text size="12px" c="gray.3">{t('smallerWhilePlaying')}</Text>
                  </Box>
                </Tooltip>
              )}
              {quiet && recentlyDropped > 0 && (
                <Box px={8} py={4} role="status" data-testid="recent-drops-pill" data-dropped={recentlyDropped} style={{ background: 'rgba(15,17,21,0.82)', borderRadius: 6 }}>
                  <Text size="12px" c="yellow.4">{t('framesSkippedLately', { count: recentlyDropped })}</Text>
                </Box>
              )}
            </Group>
          )}
          {booted && kind === 'live' && contextLoss && <ContextLossOverlay loss={contextLoss} onRetry={retryLive} />}
          {!phone && liveAction && <Box style={{ position: 'absolute', left: 6, bottom: 6, zIndex: 2 }}>{liveAction}</Box>}
          {dropping && kind === 'live' && onSwitchToSimple && (
            <Box style={{ position: 'absolute', left: 6, right: 6, bottom: 6, zIndex: 2 }}>
              <DroppedFramesChip onSwitch={onSwitchToSimple} />
            </Box>
          )}
          {booted && kind === 'unavailable' && (
            <Box style={{ position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 16 }}>
              <Alert icon={<IconInfoCircle size={16} />} color="gray" variant="light" title={t('noPreview')} data-testid="preview-unavailable">
                {reason ? previewText(reason, t.msg) : t('thePreviewSessionIs')}
                {!webCodecs && t('webcodecsIsNotAvailable')}
              </Alert>
            </Box>
          )}
        </Box>
      </Box>
      {kind === 'live' && reducedLine && (
        <Tooltip label={reducedLine} multiline w={340} openDelay={200}>
          <Text size="12px" c="dimmed" px="sm" pb={lead ? 2 : 6} truncate="end" data-testid="preview-reduced-note" style={{ cursor: 'help' }}>
            {t('reducedQuality', { reducedQuality: reducedLine })}
          </Text>
        </Tooltip>
      )}
      {kind === 'fallback' && reason && (
        <Tooltip label={previewText(reason, t.msg)} multiline w={340} openDelay={200}>
          <Text size="12px" c="dimmed" px="sm" pb={6} truncate="end" data-testid="preview-degraded-note" style={{ cursor: 'help' }}>
            <IconInfoCircle size={12} style={{ verticalAlign: '-2px', marginRight: 4, color: 'var(--mantine-color-orange-6)' }} />
            {t('simplePreview', { reason: previewText(reason, t.msg) })}
          </Text>
        </Tooltip>
      )}
      </>)}
    </Stack>
    </ToolDockContext.Provider>
  );
}
