'use client';

/* eslint-disable i18next/no-literal-string -- action ids and test ids, not copy */

/**
 * The Source monitor: the one viewer's second program (Cut and Edit). A clip from the source tape or
 * the media pool plays here, is marked here and is placed from here, with the same transport keys.
 *
 * ## Session lifecycle (what the timeline's preview session does meanwhile)
 *
 * `ProgramViewer` creates ONE preview session when it mounts (and rebuilds it only when the preview
 * quality or the Simple / live choice changes); the session owns the canvas, the decoders and the clock,
 * and is disposed only on unmount. Playback is driven by the store's `playbackSpeed`; a seek is driven
 * by `playheadFrame`; an edit re-plans it with `invalidate()`. So the Source monitor never unmounts or
 * rebuilds it: it stops playback (speed 0), and `ProgramViewer`'s `suspended` prop makes the seek and
 * re-plan effects do nothing, so no decode work runs while Source shows. Going back clears `suspended`,
 * and the same effects seek to the playhead and re-present the frame - no reload, no remount.
 *
 * ## Nothing here writes to a document
 *
 * Playback, marks and the position are view state (the marks reducer is `lib/editing/media/in-out.ts`,
 * through `useSourcePlayback`, the same code the Media pool's source viewer runs). Placing goes through
 * the editor's one `placePoolItem` path via the caller's `onPlace`.
 */

import React, { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Box, Group, Image, SegmentedControl, Slider, Text, Tooltip } from '@mantine/core';
import { IconPlayerPause, IconPlayerPlay, IconPlayerSkipBack, IconPlayerSkipForward, IconPlayerTrackNext, IconPlayerTrackPrev, IconX } from '@tabler/icons-react';

import { EditorIcon } from '../ui/EditorIcon';
import { PlaceMenu } from '../place/PlaceMenu';
import { useSourcePlayback } from '../media/use-source-playback';
import { isMarkable, markSpanFrames, secondsFromFrames, type Marks } from '@/lib/editing/media/in-out';
import type { MediaPoolItem } from '@/lib/editing/media/types';
import type { PlaceChoice } from '../place/placeChoices';
import { framesToTimecode } from '@/lib/editing/time';
import { shuttle } from '@/lib/editing/transport';
import type { SourceTransport } from '@/lib/editing/commands';
import type { KeyAction } from '@/lib/editing/keymap';
import { useT } from '@/i18n';

/** The step used when the source has no frame rate on record (a decoded-frame source): one frame at 30 fps. */
const FALLBACK_FRAME_SECONDS = 1 / 30;
/** Reverse shuttle: a media element cannot play backwards, so the position is stepped on a timer. */
const REVERSE_TICK_MS = 33;

export interface SourceMonitorController {
  item: MediaPoolItem | null;
  active: boolean;
  playback: ReturnType<typeof useSourcePlayback>;
  speed: number;
  marks: Marks;
  transport: SourceTransport;
  /** The clip's length in seconds once its metadata is known (0 before), for the scrub bar. */
  duration: number;
  onDuration: (seconds: number) => void;
  /** Seeks the source element to a time in seconds (clamped); view state only. */
  seekTo: (seconds: number) => void;
  /** One frame in seconds (the scrub bar's step). */
  frameSeconds: number;
  /** The element's current time, for the scrub bar. */
  currentSeconds: () => number;
}

export function useSourceMonitor(projectId: string, item: MediaPoolItem | null, active: boolean, onMarksChange?: (marks: Marks) => void): SourceMonitorController {
  const playback = useSourcePlayback(projectId, item, 0, onMarksChange);
  const [speed, setSpeed] = useState(0);
  const [durationEntry, setDurationEntry] = useState<{ ref: string | null; seconds: number }>({ ref: null, seconds: 0 });
  const speedRef = useRef(0);
  useEffect(() => {
    speedRef.current = speed;
  }, [speed]);

  // Leaving Source stops the clip: the element is unmounted, and the next visit starts paused.
  useEffect(() => {
    if (!active) return undefined;
    return () => setSpeed(0);
  }, [active]);

  const { videoRef, videoUrl, refreshPlayhead } = playback;
  useEffect(() => {
    const el = videoRef.current;
    if (!el || !active) return undefined;
    if (speed > 0) {
      el.playbackRate = speed;
      const started = typeof el.play === 'function' ? (el.play() as Promise<void> | undefined) : undefined;
      if (started && typeof started.catch === 'function') started.catch(() => setSpeed(0));
      return undefined;
    }
    if (typeof el.pause === 'function') el.pause();
    if (speed < 0) {
      const id = window.setInterval(() => {
        if (el.currentTime <= 0) {
          setSpeed(0);
          return;
        }
        el.currentTime = Math.max(0, el.currentTime + (speed * REVERSE_TICK_MS) / 1000);
        refreshPlayhead();
      }, REVERSE_TICK_MS);
      return () => window.clearInterval(id);
    }
    return undefined;
  }, [speed, active, videoUrl, videoRef, refreshPlayhead]);

  const { domain, applyMark, handleClearMarks } = playback;
  const frameSeconds = domain.kind === 'timebase' ? secondsFromFrames(1, domain.fps) ?? FALLBACK_FRAME_SECONDS : FALLBACK_FRAME_SECONDS;
  const seekTo = useCallback(
    (seconds: number) => {
      const el = videoRef.current;
      if (!el) return;
      const end = Number.isFinite(el.duration) ? el.duration : seconds;
      el.currentTime = Math.min(Math.max(0, seconds), end);
      refreshPlayhead();
    },
    [videoRef, refreshPlayhead],
  );
  const transport = useMemo<SourceTransport>(
    () => ({
      togglePlay: () => setSpeed(shuttle(speedRef.current, 'Space')),
      shuttleReverse: () => setSpeed(shuttle(speedRef.current, 'J')),
      stop: () => setSpeed(0),
      shuttleForward: () => setSpeed(shuttle(speedRef.current, 'L')),
      step: (delta) => {
        setSpeed(0);
        const el = videoRef.current;
        if (el) seekTo(el.currentTime + delta * frameSeconds);
      },
      goToStart: () => {
        setSpeed(0);
        seekTo(0);
      },
      goToEnd: () => {
        setSpeed(0);
        const el = videoRef.current;
        if (el && Number.isFinite(el.duration)) seekTo(el.duration);
      },
      markIn: () => applyMark('in'),
      markOut: () => applyMark('out'),
      clearMarks: () => handleClearMarks(),
    }),
    [videoRef, seekTo, frameSeconds, applyMark, handleClearMarks],
  );

  const itemRef = item ? item.mediaRef : null;
  const duration = durationEntry.ref === itemRef ? durationEntry.seconds : 0;
  const onDuration = useCallback((seconds: number) => setDurationEntry({ ref: itemRef, seconds: Number.isFinite(seconds) && seconds > 0 ? seconds : 0 }), [itemRef]);
  const currentSeconds = useCallback(() => videoRef.current?.currentTime ?? 0, [videoRef]);
  return { item, active, playback, speed, marks: playback.marks, transport, duration, onDuration, seekTo, currentSeconds, frameSeconds };
}

export interface ViewerModeSwitchProps {
  mode: 'record' | 'source';
  onChange: (mode: 'record' | 'source') => void;
  /** The label with its live chord, e.g. "Switch (Shift+V)"; the workspace builds it from the running keymap. */
  hint: string;
  keyShortcuts?: string;
  /** The open source's name, shown beside the switch while Source shows. */
  sourceName?: string | null;
}

/** The Source | Record switch, laid inside the viewer's own header row (it adds no row): labelled, keyboard-operable, always the same height. */
export function ViewerModeSwitch({ mode, onChange, hint, keyShortcuts, sourceName = null }: ViewerModeSwitchProps) {
  const t = useT('editor.viewer.sourceMonitor');
  return (
    <Group gap="sm" wrap="nowrap" style={{ flex: '0 1 auto', minWidth: 0, position: 'relative', zIndex: 6 }} data-testid="viewer-mode-bar">
      <Tooltip label={hint} openDelay={300} position="bottom" events={{ hover: true, focus: true, touch: true }}>
        <SegmentedControl
          size="xs"
          value={mode}
          onChange={(value) => onChange(value === 'source' ? 'source' : 'record')}
          aria-label={t('switchLabel')}
          aria-keyshortcuts={keyShortcuts}
          data={[
            { value: 'source', label: <span data-testid="viewer-mode-source">{t('source')}</span> },
            { value: 'record', label: <span data-testid="viewer-mode-record">{t('record')}</span> },
          ]}
          data-testid="viewer-mode-switch"
          data-mode={mode}
          data-editor-shortcuts="through"
          data-editor-own-keys="radio"
        />
      </Tooltip>
      {mode === 'source' && sourceName ? (
        <Text size="xs" truncate data-testid="source-monitor-name">
          {t('name', { name: sourceName })}
        </Text>
      ) : null}
    </Group>
  );
}

function useTimecodeOf(monitor: SourceMonitorController): (frame: number | null) => string {
  const t = useT('editor.viewer.sourceMonitor');
  const { domain } = monitor.playback;
  return (frame) => {
    if (frame === null) return t('notSet');
    return domain.kind === 'timebase' ? t('timecode', { timecode: framesToTimecode(frame, domain.fps), frame }) : t('decodedFrame', { frame });
  };
}

/** The marks in words, over the bottom of the Source area: they cost the transport no height, and the picture box none. */
function SourceMarksCaption({ monitor }: { monitor: SourceMonitorController }) {
  const t = useT('editor.viewer.sourceMonitor');
  const timecodeOf = useTimecodeOf(monitor);
  const { item, marks } = monitor;
  if (!item || item.kind !== 'video') return null;
  const span = markSpanFrames(marks);
  return (
    <Box style={{ position: 'absolute', left: 8, right: 8, bottom: 4, zIndex: 1, display: 'flex', flexDirection: 'column', alignItems: 'center', pointerEvents: 'none' }}>
      <Text size="xs" ta="center" px={6} data-testid="source-monitor-marks" style={{ background: 'rgba(0, 0, 0, 0.72)', color: '#fff', borderRadius: 4 }}>
        {t('inValue', { value: timecodeOf(marks.inFrame) })} · {t('outValue', { value: timecodeOf(marks.outFrame) })} · {span === null ? t('noRange') : t('frames', { count: span })}
      </Text>
      {!isMarkable(monitor.playback.domain) ? (
        <Text size="xs" ta="center" px={6} mt={2} data-testid="source-monitor-unmarkable" style={{ background: 'rgba(0, 0, 0, 0.72)', color: '#fff', borderRadius: 4 }}>
          {t('unmarkable')}
        </Text>
      ) : null}
    </Box>
  );
}

/** The source's picture, laid over the program viewer's own box (which stays mounted underneath). */
export function SourceMonitorPicture({ monitor }: { monitor: SourceMonitorController }) {
  const t = useT('editor.viewer.sourceMonitor');
  const { item } = monitor;
  const { attachVideo, videoUrl, originalUrl, effectiveSource, refreshPlayhead, handleLoadedMetadata } = monitor.playback;
  // A failed load is remembered with the URL it failed on, so another source (or the proxy arriving) tries again.
  const [failedUrl, setFailedUrl] = useState<string | null>(null);
  // The source's picture takes the program frame's own rectangle, so switching never moves or resizes the picture box.
  const boxRef = useRef<HTMLDivElement>(null);
  const [rect, setRect] = useState<{ left: number; top: number; width: number; height: number } | null>(null);
  useLayoutEffect(() => {
    const box = boxRef.current;
    const host = box?.parentElement;
    if (!box || !host) return undefined;
    const measure = () => {
      const frame = host.querySelector<HTMLElement>('[data-testid="program-frame"]');
      if (!frame) {
        setRect(null);
        return;
      }
      const f = frame.getBoundingClientRect();
      const h = host.getBoundingClientRect();
      const next = { left: f.left - h.left, top: f.top - h.top, width: f.width, height: f.height };
      setRect((prev) => (prev && prev.left === next.left && prev.top === next.top && prev.width === next.width && prev.height === next.height ? prev : next));
    };
    measure();
    window.addEventListener('resize', measure);
    if (typeof ResizeObserver === 'undefined') return () => window.removeEventListener('resize', measure);
    const observer = new ResizeObserver(measure);
    observer.observe(host);
    const frame = host.querySelector('[data-testid="program-frame"]');
    if (frame) observer.observe(frame);
    return () => {
      observer.disconnect();
      window.removeEventListener('resize', measure);
    };
  }, []);

  let body: React.ReactNode;
  if (!item) {
    body = (
      <Text size="sm" c="dimmed" ta="center" maw={360} data-testid="source-monitor-empty">
        {t('empty')}
      </Text>
    );
  } else if (item.kind === 'audio' || item.kind === 'image') {
    const original = typeof originalUrl === 'string' && originalUrl.trim().length > 0 ? originalUrl : null;
    body =
      item.kind === 'image' && original ? (
        <Image src={original} alt={item.fileName} fit="contain" h="100%" w="100%" data-testid="source-monitor-image" />
      ) : (
        <Text size="sm" c="dimmed" ta="center" maw={360} data-testid="source-monitor-still-or-sound">
          {original ? t('stillOrSound') : t('noUrl')}
        </Text>
      );
  } else {
    const playable = typeof videoUrl === 'string' && videoUrl.trim().length > 0 ? videoUrl : null;
    body =
      playable === null ? (
        <Text size="sm" c="dimmed" ta="center" maw={360} role="status" data-testid="source-monitor-unplayable">
          {t('noUrl')}
        </Text>
      ) : failedUrl === playable ? (
        <Text size="sm" c="dimmed" ta="center" maw={360} role="status" data-testid="source-monitor-unplayable">
          {t('unplayable')}
        </Text>
      ) : (
        <video
          ref={attachVideo}
          src={playable}
          preload="metadata"
          playsInline
          muted={false}
          data-testid="source-monitor-video"
          data-playing-source={effectiveSource ?? undefined}
          onTimeUpdate={refreshPlayhead}
          onSeeked={refreshPlayhead}
          onLoadedMetadata={(event) => {
            monitor.onDuration(event.currentTarget.duration);
            handleLoadedMetadata();
          }}
          onDurationChange={(event) => monitor.onDuration(event.currentTarget.duration)}
          onError={() => setFailedUrl(playable)}
          style={{ width: '100%', height: '100%', objectFit: 'contain', background: '#000' }}
        />
      );
  }

  return (
    <Box
      ref={boxRef}
      role="region"
      aria-label={t('region')}
      data-testid="source-monitor"
      data-source={item?.mediaRef ?? undefined}
      // overflow hidden: the program frame can be taller than the picture area (its own minimum height), and the copy of its box must not paint past the area onto the transport row below.
      style={{ position: 'absolute', inset: 0, zIndex: 5, overflow: 'hidden', background: 'var(--rb-ed-studio-bg)', ...(rect ? {} : { display: 'flex', alignItems: 'center', justifyContent: 'center', padding: '8px 12px 12px' }) }}
    >
      {rect ? (
        <Box
          data-testid="source-monitor-frame"
          style={{ position: 'absolute', left: rect.left, top: rect.top, width: rect.width, height: rect.height, display: 'flex', alignItems: 'center', justifyContent: 'center', borderRadius: 4, overflow: 'hidden', background: item?.kind === 'video' ? '#000' : undefined }}
        >
          {body}
        </Box>
      ) : (
        body
      )}
      <SourceMarksCaption monitor={monitor} />
    </Box>
  );
}

export interface SourceMonitorTransportProps {
  monitor: SourceMonitorController;
  readOnly: boolean;
  onPlace: (item: MediaPoolItem, mode: PlaceChoice, marks: Marks) => void;
  /** "Label (chord)" from the running keymap. */
  chordLabel: (label: string, action: KeyAction) => string;
  chordAria: (action: KeyAction) => string | undefined;
}

/** The source's transport row: the same keys as the timeline's, marks, and the one Place control. */
export function SourceMonitorTransport({ monitor, readOnly, onPlace, chordLabel, chordAria }: SourceMonitorTransportProps) {
  const t = useT('editor.viewer.sourceMonitor');
  const { item, transport, speed, marks } = monitor;
  const { domain, playheadFrame } = monitor.playback;
  const playable = item !== null && item.kind === 'video';
  const markable = playable && isMarkable(domain) && playheadFrame !== null;
  const timecodeOf = useTimecodeOf(monitor);
  const button = (label: string, action: KeyAction | null, Icon: typeof IconX, onClick: () => void, testId: string, disabled = !playable) => (
    <Tooltip label={action ? chordLabel(label, action) : label} openDelay={300} withinPortal>
      <EditorIcon tooltip={false} icon={Icon} label={label} aria-keyshortcuts={action ? chordAria(action) : undefined} onClick={onClick} disabled={disabled} data-testid={testId} />
    </Tooltip>
  );
  return (
    <Box px="sm" py={4} data-testid="source-monitor-transport" style={{ height: '100%', boxSizing: 'border-box', display: 'flex', flexDirection: 'column', justifyContent: 'center', background: 'var(--rb-ed-header-bg)', borderTop: '1px solid var(--rb-ed-border, var(--mantine-color-default-border))' }}>
      <Group gap={6} wrap="wrap" align="center">
        {button(t('goToStart'), 'goToStart', IconPlayerTrackPrev, transport.goToStart, 'source-monitor-go-start')}
        {button(t('stepBack'), 'frameBack', IconPlayerSkipBack, () => transport.step(-1), 'source-monitor-step-back')}
        {speed !== 0
          ? button(t('pause'), 'playPause', IconPlayerPause, transport.togglePlay, 'source-monitor-play')
          : button(t('play'), 'playPause', IconPlayerPlay, transport.togglePlay, 'source-monitor-play')}
        {button(t('stepForward'), 'frameForward', IconPlayerSkipForward, () => transport.step(1), 'source-monitor-step-forward')}
        {button(t('goToEnd'), 'goToEnd', IconPlayerTrackNext, transport.goToEnd, 'source-monitor-go-end')}
        <Text size="xs" ff="monospace" aria-label={t('position')} data-testid="source-monitor-position">
          {playheadFrame === null ? t('notSet') : timecodeOf(playheadFrame)}
        </Text>
        <Slider
          size="xs"
          style={{ flex: '1 1 64px', minWidth: 64 }}
          min={0}
          max={monitor.duration > 0 ? monitor.duration : 1}
          step={monitor.frameSeconds}
          value={Math.min(monitor.currentSeconds(), monitor.duration > 0 ? monitor.duration : 1)}
          disabled={!playable || monitor.duration <= 0}
          label={null}
          thumbLabel={t('scrub')}
          onChange={(value) => {
            transport.stop();
            monitor.seekTo(value);
          }}
          data-testid="source-monitor-scrub"
          data-editor-shortcuts="through"
          data-editor-own-keys="slider"
        />
        <Tooltip label={chordLabel(t('markIn'), 'markIn')} openDelay={300} withinPortal>
          <EditorIcon tooltip={false} label={t('markIn')} aria-keyshortcuts={chordAria('markIn')} onClick={transport.markIn} disabled={!markable} data-testid="source-monitor-mark-in">
            I
          </EditorIcon>
        </Tooltip>
        <Tooltip label={chordLabel(t('markOut'), 'markOut')} openDelay={300} withinPortal>
          <EditorIcon tooltip={false} label={t('markOut')} aria-keyshortcuts={chordAria('markOut')} onClick={transport.markOut} disabled={!markable} data-testid="source-monitor-mark-out">
            O
          </EditorIcon>
        </Tooltip>
        {button(t('clearMarks'), 'clearInOut', IconX, transport.clearMarks, 'source-monitor-clear-marks', marks.inFrame === null && marks.outFrame === null)}
        <Group gap={6} wrap="nowrap" align="center" data-testid="source-monitor-place" aria-label={t('placeLabel')} role="group">
          <PlaceMenu variant="menu" disabled={readOnly || item === null} idPrefix="source-monitor-" onPlace={(mode) => item && onPlace(item, mode, marks)} />
        </Group>
      </Group>
    </Box>
  );
}
