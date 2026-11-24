'use client';

import React, { useState } from 'react';
import { Group, TextInput, Tooltip } from '@mantine/core';
import { EditorIcon } from '../ui/EditorIcon';
import {
  IconArrowBarToLeft,
  IconArrowBarToRight,
  IconBookmark,
  IconChevronLeft,
  IconChevronRight,
  IconPlayerPause,
  IconPlayerPlay,
  IconPlayerSkipBack,
  IconPlayerSkipForward,
  IconPlayerTrackNext,
  IconPlayerTrackPrev,
  IconRepeat,
} from '@tabler/icons-react';
import { ariaChord, displayChord, type KeyAction } from '@/lib/editing/keymap';
import { framesToTimecode } from '@/lib/editing/time';
import { parseTimecodeEntry } from '@/lib/editing/transport';
import type { Rational } from '@/lib/editing/types';
import { useT } from '@/i18n';

export interface TransportBarProps {
  playheadFrame: number;
  rate: Rational;
  endFrame: number;
  speed: number;
  loop: boolean;
  inFrame: number | null;
  outFrame: number | null;
  onSeek: (frame: number) => void;
  onSpeed: (speed: number) => void;
  onStep: (delta: number) => void;
  onPrevEdit: () => void;
  onNextEdit: () => void;
  onPrevMarker: () => void;
  onNextMarker: () => void;
  onMarkIn: () => void;
  onMarkOut: () => void;
  onToggleLoop: () => void;
  onPlayInToOut: () => void;
  /** Phone layout: the same controls, finger-sized, wrapping onto a second row instead of being dropped. */
  compact?: boolean;
  /** The Simple shell: play, the frame steps, start, end and the timecode. Marks, edit and marker jumps and loop keep their chords. */
  simple?: boolean;
  /** Rendered at the end of the control row, so on the phone layout it shares the second row instead of taking a third. */
  trailing?: React.ReactNode;
  /** T-007: the live first chord of each shortcut-bearing control, so a remap updates the tooltip and aria. */
  chords?: Partial<Record<KeyAction, string>>;
}

/**
 * The transport. Every control writes ONLY UI state (playhead, speed, in/out) through the callbacks
 * — nothing here produces a document op. The timecode field accepts absolute and relative entry
 * (`parseTimecodeEntry`) and commits on Enter; a refused entry leaves the playhead where it is and
 * shows the refusal inline rather than jumping to zero.
 */
export function TransportBar(props: TransportBarProps) {
  const t = useT('editor.viewer.transportBar');
  const { playheadFrame, rate, endFrame, speed, loop, inFrame, outFrame, compact = false, simple = false } = props;
  const [entry, setEntry] = useState<string | null>(null);
  const [refused, setRefused] = useState(false);

  const displayed = entry ?? framesToTimecode(playheadFrame, rate);

  const commit = () => {
    if (entry === null) return;
    const frame = parseTimecodeEntry(entry, { rate, currentFrame: playheadFrame, maxFrame: Math.max(0, endFrame - 1) });
    if (frame === null) {
      setRefused(true);
      return;
    }
    props.onSeek(frame);
    setEntry(null);
    setRefused(false);
  };

  const speedLabel = speed === 0 ? '' : `${speed > 0 ? '▶' : '◀'} ${Math.abs(speed)}×`;
  const isMac = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform ?? '');
  /** The visible hint: the plain label plus the chord the keymap holds right now (never a baked-in one). */
  const tipFor = (label: string, action: KeyAction): string => {
    const chord = props.chords?.[action];
    return chord ? `${label} (${displayChord(chord, isMac)})` : label;
  };
  const ariaFor = (action: KeyAction): string | undefined => {
    const chord = props.chords?.[action];
    return chord ? ariaChord(chord, isMac) : undefined;
  };

  // The phone layout packs the transport onto wrapped rows, so its icons keep the full touch target and the row scrolls sideways instead of wrapping.
  const icon = (label: string, _tip: string, onClick: () => void, glyph: React.ReactNode, extra?: { disabled?: boolean; action?: KeyAction }) => (
    <EditorIcon dense={false} label={label} tooltip={extra?.action ? tipFor(label, extra.action) : _tip} aria-keyshortcuts={extra?.action ? ariaFor(extra.action) : undefined} onClick={onClick} disabled={extra?.disabled}>
      {glyph}
    </EditorIcon>
  );

  const markIn = (
      <EditorIcon dense={false} variant={inFrame !== null ? 'light' : 'subtle'} color={inFrame !== null ? 'yellow' : 'gray'} label={t('markIn')} tooltip={tipFor(t('markIn'), 'markIn')} /* eslint-disable-line i18next/no-literal-string -- action ids, not copy */ aria-keyshortcuts={ariaFor('markIn')} onClick={props.onMarkIn} data-testid="mark-in">
        <IconArrowBarToRight size={16} stroke={1.7} />
      </EditorIcon>
  );
  const markOut = (
      <EditorIcon dense={false} variant={outFrame !== null ? 'light' : 'subtle'} color={outFrame !== null ? 'yellow' : 'gray'} label={t('markOut')} tooltip={tipFor(t('markOut'), 'markOut')} /* eslint-disable-line i18next/no-literal-string -- action ids, not copy */ aria-keyshortcuts={ariaFor('markOut')} onClick={props.onMarkOut} data-testid="mark-out">
        <IconArrowBarToLeft size={16} stroke={1.7} />
      </EditorIcon>
  );
  const goStart = icon(t('goToStart'), '', () => props.onSeek(0), <IconPlayerSkipBack size={16} />, { action: 'goToStart' });
  const prevEdit = icon(t('previousEdit'), '', props.onPrevEdit, <IconPlayerTrackPrev size={16} />, { action: 'previousEdit' });
  const back = icon(t('stepBackOneFrame'), '', () => props.onStep(-1), <IconChevronLeft size={16} />, { action: 'frameBack' });
  const play = (
      <Tooltip label={tipFor(speed === 0 ? t('play') : t('pause'), 'playPause')} /* eslint-disable-line i18next/no-literal-string -- action id, not copy */ openDelay={300}>
        <EditorIcon tooltip={false} icon={speed === 0 ? IconPlayerPlay : IconPlayerPause} size="lg"
          variant="filled"
          color="violet"
          radius="xl"
          mx={4}
          label={speed === 0 ? t('play') : t('pause')}
          aria-pressed={speed !== 0}
          aria-keyshortcuts={ariaFor('playPause')}
          onClick={() => props.onSpeed(speed === 0 ? 1 : 0)}
          data-testid="transport-play" />
      </Tooltip>
  );
  const fwd = icon(t('stepForwardOneFrame'), '', () => props.onStep(1), <IconChevronRight size={16} />, { action: 'frameForward' });
  const nextEdit = icon(t('nextEdit'), '', props.onNextEdit, <IconPlayerTrackNext size={16} />, { action: 'nextEdit' });
  const goEnd = icon(t('goToEnd'), '', () => props.onSeek(Math.max(0, endFrame - 1)), <IconPlayerSkipForward size={16} />, { action: 'goToEnd' });
  const timecode = (
      <TextInput
        size="xs"
        w={compact ? 100 : 116}
        miw={compact ? 100 : undefined}
        style={{ flexShrink: 0 }}
        ml={6}
        variant="filled"
        value={displayed}
        error={refused ? t('notATimecode') : undefined}
        onFocus={() => setEntry(framesToTimecode(playheadFrame, rate))}
        onChange={(e) => {
          setEntry(e.currentTarget.value);
          setRefused(false);
        }}
        onBlur={() => {
          setEntry(null);
          setRefused(false);
        }}
        onKeyDown={(e) => {
          if (e.key === 'Enter') {
            e.preventDefault();
            commit();
          } else if (e.key === 'Escape') {
            setEntry(null);
            setRefused(false);
            (e.currentTarget as HTMLInputElement).blur();
          }
          e.stopPropagation();
        }}
        aria-label={t('timecode')}
        // A phone opens its number pad: digits alone are a full timecode (`1215` = 00:00:12:15).
        inputMode="numeric"
        autoComplete="off"
        data-testid="timecode-field"
        styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)', textAlign: 'center', fontVariantNumeric: 'tabular-nums', fontWeight: 600 } }}
      />
  );
  const shuttle = speedLabel ? (
        <span data-testid="shuttle-speed" style={{ fontSize: 12, minWidth: 40, textAlign: 'center', fontVariantNumeric: 'tabular-nums', color: 'var(--mantine-color-violet-filled)', fontWeight: 600 }}>
          {speedLabel}
        </span>
      ) : null;
  const extras = (
      <>
          {icon(t('previousMarker'), '', props.onPrevMarker, <IconBookmark size={14} style={{ transform: 'scaleX(-1)' }} />, { action: 'previousMarker' } /* eslint-disable-line i18next/no-literal-string -- action id, not copy */)}
          {icon(t('nextMarker'), '', props.onNextMarker, <IconBookmark size={14} />, { action: 'nextMarker' } /* eslint-disable-line i18next/no-literal-string -- action id, not copy */)}
          {icon(t('playInToOut'), '', props.onPlayInToOut, <IconPlayerPlay size={14} />, { disabled: inFrame === null || outFrame === null, action: 'playInToOut' } /* eslint-disable-line i18next/no-literal-string -- action id, not copy */)}
          <EditorIcon dense={false} variant={loop ? 'light' : 'subtle'} color={loop ? 'violet' : 'gray'} label={t('loop')} aria-pressed={loop} onClick={props.onToggleLoop}>
            <IconRepeat size={16} stroke={1.7} />
          </EditorIcon>
      </>
  );

  if (simple && !compact) {
    return (
      <Group gap={2} wrap="nowrap" data-testid="transport-bar" data-simple="true" align="center" style={{ minWidth: 0 }}>
        {goStart}
        {back}
        {play}
        {fwd}
        {goEnd}
        {timecode}
        {shuttle}
        {props.trailing}
      </Group>
    );
  }

  if (compact) {
    // Phone: Play and the two frame steps are a fixed cluster that is always on screen; every other control scrolls sideways
    // inside its own region (min-width 0), so the row never grows past the viewport and never drags the viewer sideways.
    return (
      <Group gap={2} wrap="nowrap" align="center" data-testid="transport-bar" style={{ minWidth: 0, width: '100%' }}>
        <Group gap={2} wrap="nowrap" align="center" style={{ flex: '0 0 auto' }} data-testid="transport-core">
          {back}
          {play}
          {fwd}
        </Group>
        <Group gap={2} wrap="nowrap" align="center" data-testid="transport-more" style={{ flex: '1 1 0', minWidth: 0, overflowX: 'auto', overscrollBehaviorX: 'contain', scrollbarWidth: 'none' }}>
          {timecode}
          {shuttle}
          {markIn}
          {markOut}
          {goStart}
          {prevEdit}
          {nextEdit}
          {goEnd}
          {extras}
        </Group>
        {props.trailing}
      </Group>
    );
  }

  return (
    <Group gap={2} wrap="wrap" data-testid="transport-bar" align="center" style={{ minWidth: 0 }}>
      {markIn}
      {markOut}
      {goStart}
      {prevEdit}
      {back}
      {play}
      {fwd}
      {nextEdit}
      {goEnd}
      {timecode}
      {shuttle}
      {extras}
      {props.trailing}
    </Group>
  );
}
