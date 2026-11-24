'use client';

import React, { useState } from 'react';
import { Button, Slider, Text } from '@mantine/core';
import { IconHeadphones, IconVolume, IconVolumeOff } from '@tabler/icons-react';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline, Track } from '@/lib/editing/types';
import { FADER_MAX_DB, FADER_MIN_DB, FADER_RESET_DB, FADER_STEP_DB, formatFaderDb } from '@/lib/editing/audio/fader';
import { loadDuckConfig, saveDuckConfig } from '@/lib/editing/audio/duck-state';
import { ClipVolumeSection } from './ClipSheet';
import type { PhoneActions } from './phone-actions';
import { allClean, cleanVoiceTargets, compileCleanVoiceTap, compileDuckOff, compileDuckTap, duckState } from './audio-sheet-logic';
import classes from './phone.module.css';
import { useT } from '@/i18n';
import { useHelperText } from './helper-text';
import { CleanupPanel } from '../audio/CleanupPanel';

export interface AudioSheetProps {
  doc: EditTimeline;
  readOnly: boolean;
  actions: PhoneActions;
  flags: (track: Track) => { enabled: boolean; mute: boolean; solo: boolean; locked: boolean };
  onToggle: (trackId: string, flag: 'mute' | 'solo') => void;
  /** A batch of ops as ONE undo step. */
  onApply: (ops: readonly EditOp[], label: string) => boolean | void;
  /** The track fader: one op, one undo step. */
  onGain: (trackId: string, db: number) => void;
  /** The project and the selected clips, for the clip cleanup section. */
  projectId?: string;
  selection?: readonly string[];
  /** The voiceover recorder (the desktop panel in its phone variant). */
  voiceover: React.ReactNode;
}

function TrackRow({ track, readOnly, flags, onToggle, onGain }: { track: Track; readOnly: boolean; flags: { mute: boolean; solo: boolean }; onToggle: AudioSheetProps['onToggle']; onGain: AudioSheetProps['onGain'] }) {
  const tx = useT('editor.phone.audioSheet');
  const stored = typeof track.gainDb?.value === 'number' ? track.gainDb.value : 0;
  const automated = Array.isArray(track.gainDb?.keys) && (track.gainDb?.keys?.length ?? 0) > 0;
  const [draft, setDraft] = useState<number | null>(null);
  const shown = draft ?? stored;
  return (
    <div className={classes.audioTrack} data-testid={`phone-audio-track-${track.id}`}>
      <div className={classes.sheetRow}>
        <Text size="sm" fw={600} className={classes.audioTrackName}>
          {track.name}
        </Text>
        <Button variant={flags.mute ? 'filled' : 'default'} color="orange" h={44} miw={44} px={10} disabled={readOnly} aria-pressed={flags.mute} aria-label={tx('mute', { name: track.name })} onClick={() => onToggle(track.id, 'mute')} data-testid={`phone-audio-mute-${track.id}`}>
          {flags.mute ? <IconVolumeOff size={20} aria-hidden /> : <IconVolume size={20} aria-hidden />}
        </Button>
        <Button variant={flags.solo ? 'filled' : 'default'} color="yellow" h={44} miw={44} px={10} disabled={readOnly} aria-pressed={flags.solo} aria-label={tx('solo', { name: track.name })} onClick={() => onToggle(track.id, 'solo')} data-testid={`phone-audio-solo-${track.id}`}>
          <IconHeadphones size={20} aria-hidden />
        </Button>
      </div>
      <Text size="xs" c="dimmed" data-testid={`phone-audio-gain-readout-${track.id}`}>
        {tx('volumeReadout', { value: formatFaderDb(shown), automated: automated ? 'yes' : 'no' })}
      </Text>
      <Slider
        min={FADER_MIN_DB}
        max={FADER_MAX_DB}
        step={FADER_STEP_DB}
        value={Math.max(FADER_MIN_DB, Math.min(FADER_MAX_DB, shown))}
        disabled={readOnly}
        onChange={setDraft}
        onChangeEnd={(v) => {
          setDraft(null);
          if (v !== stored) onGain(track.id, v);
        }}
        label={(v) => `${formatFaderDb(v)} dB`}
        thumbLabel={`${track.name} volume`}
        size="lg"
        styles={{ thumb: { width: 28, height: 28 }, root: { paddingBlock: 8 } }}
        data-testid={`phone-audio-gain-${track.id}`}
      />
      {stored !== FADER_RESET_DB && (
        <Button variant="subtle" h={44} disabled={readOnly} onClick={() => onGain(track.id, FADER_RESET_DB)} data-testid={`phone-audio-gain-reset-${track.id}`}>
          {tx('backTo0Db')}
        </Button>
      )}
    </div>
  );
}

/**
 * The Audio sheet (RM.11): what a phone user fixes before sending. Per-track volume, mute and solo; the
 * selected clip's volume; one-tap Duck music and Clean voice (one undo step each); and the voiceover recorder.
 * No buses, no DSP numbers, no automation lanes: those stay on a larger screen.
 */
export function AudioSheet({ doc, readOnly, actions, flags, onToggle, onApply, onGain, voiceover, projectId, selection = [] }: AudioSheetProps) {
  const tx = useT('editor.phone.audioSheet');
  const show = useHelperText();
  const audioTracks = doc.tracks.filter((t) => t.kind === 'audio');
  const [savedDuck, setSavedDuck] = useState(() => loadDuckConfig(doc.id));
  const duck = duckState(doc, savedDuck);
  const selectedId = actions.selection[0] ?? null;
  const targets = cleanVoiceTargets(doc, selectedId);
  const clean = allClean(targets);

  const tapDuck = () => {
    if (!duck.next) return;
    saveDuckConfig(doc.id, duck.next);
    setSavedDuck(duck.next);
    const plan = compileDuckTap(doc, duck.next);
    onApply(plan.ops, plan.label);
  };
  const tapDuckOff = () => {
    if (!duck.live) return;
    const plan = compileDuckOff(doc, duck.live);
    onApply(plan.ops, plan.label);
    saveDuckConfig(doc.id, null);
    setSavedDuck(null);
  };
  const tapClean = () => {
    if (targets.length === 0) return;
    const plan = compileCleanVoiceTap(targets);
    onApply(plan.ops, plan.label);
  };

  return (
    <div className={classes.sheetStack} data-testid="phone-audio-sheet">
      <section className={classes.sheetStack} style={{ padding: 0 }} aria-label={tx('quickFixes')}>
        <Text size="sm" fw={600}>
          {tx('quickFixes2')}
        </Text>
        <Button h={44} variant={duck.live ? 'light' : 'filled'} disabled={readOnly || duck.next === null || (duck.live !== null && !duck.stale)} onClick={tapDuck} data-testid="phone-audio-duck">
          {duck.live ? (duck.stale ? tx('updateDucking') : tx('musicIsDuckedUnder')) : tx('duckMusicUnderThe')}
        </Button>
        {duck.live && (
          <Button h={44} variant="default" disabled={readOnly} onClick={tapDuckOff} data-testid="phone-audio-duck-off">
            {tx('stopDucking')}
          </Button>
        )}
        {duck.reason && !duck.live && (
          <Text size="xs" c="dimmed" data-testid="phone-audio-duck-reason">
            {show(duck.reason)}
          </Text>
        )}
        <Button h={44} variant={clean ? 'light' : 'filled'} disabled={readOnly || targets.length === 0} aria-pressed={clean} onClick={tapClean} data-testid="phone-audio-clean-voice">
          {clean ? tx('cleanVoiceIsOn') : tx('cleanVoice')}
        </Button>
        <Text size="xs" c="dimmed" data-testid="phone-audio-clean-note">
          {targets.length === 0 ? tx('putAVoiceClip') : tx('removesBackgroundNoiseAnd', { v1: targets.map((t) => t.name).join(', ') })}
        </Text>
      </section>

      {projectId && (
        <section aria-label={tx('cleanVoice')} data-testid="phone-audio-cleanup">
          <CleanupPanel doc={doc} selection={selection} projectId={projectId} readOnly={readOnly} onApply={onApply} phone />
        </section>
      )}

      {actions.clip && actions.clip.volumePercent !== null && (
        <section aria-label={tx('selectedClip')}>
          <Text size="sm" fw={600} data-testid="phone-audio-clip-title">
            {tx('selectedClip2', { name: actions.clip.name })}
          </Text>
          <ClipVolumeSection actions={actions} />
        </section>
      )}

      <section className={classes.sheetStack} style={{ padding: 0 }} aria-label={tx('tracks')}>
        <Text size="sm" fw={600}>
          {tx('tracks2')}
        </Text>
        {audioTracks.length === 0 ? (
          <Text size="xs" c="dimmed" data-testid="phone-audio-empty">
            {tx('thereIsNoAudio')}
          </Text>
        ) : (
          audioTracks.map((track) => <TrackRow key={track.id} track={track} readOnly={readOnly} flags={flags(track)} onToggle={onToggle} onGain={onGain} />)
        )}
      </section>

      <section aria-label={tx('voiceover')} data-testid="phone-audio-voiceover">
        {voiceover}
      </section>
    </div>
  );
}
