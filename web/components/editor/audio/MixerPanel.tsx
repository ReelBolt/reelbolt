'use client';

import React, { useEffect, useRef, useState } from 'react';
import { Badge, Box, Button, Divider, Group, NumberInput, Stack, Switch, Text, Tooltip } from '@mantine/core';
import { LiveSlider } from '../shell/LiveSlider';
import { IconHeadphones, IconRefresh, IconVolume, IconVolumeOff } from '@tabler/icons-react';
import { PageEmptyState } from '../ui/PageEmptyState';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline, Track, TrackEq } from '@/lib/editing/types';
import type { AudioMeterSnapshot } from '@/lib/editing/preview';
import { LufsMeter, type LoudnessReadout } from '@/lib/editing/audio/lufs';
import { RenderChecks } from './RenderChecks';
import type { EditTimelineRenderLoudness } from '@/lib/editing/sync/api';
import { KeyframeLane } from '../panels/KeyframeLane';
import { formatPan } from '@/lib/editing/audio/fader';
import type { FlushResult } from '../deliver/DeliverWorkspace';
import { Fader } from './Fader';
import { CleanVoiceSwitch } from './CleanVoice';
import { CleanupPanel } from './CleanupPanel';
import { DuckSection } from './DuckSection';
import { LoudnessTargetSection } from './LoudnessTargetSection';
import { AuditionSection } from './AuditionSection';
import { useAdvancedAudio } from './useAdvancedAudio';
import { PREVIEW_NOTE } from '@/lib/editing/audio/dsp-preview';
import { EditorPanel } from '../ui/EditorPanel';
import { EditorIcon } from '../ui';
import { useFormat, useT } from '@/i18n';

export interface MixerPanelProps {
  doc: EditTimeline;
  readOnly: boolean;
  playheadFrame: number;
  flags: (track: Track) => { enabled: boolean; mute: boolean; solo: boolean; locked: boolean };
  /** The live session's meters, or null when there is no running audio graph. */
  getMeters: () => AudioMeterSnapshot | null;
  /** The newest finished render of this revision, for the authoritative loudness. */
  lastRenderLoudness: EditTimelineRenderLoudness | null;
  onOp: (op: EditOp, label?: string) => void;
  /** A batch of ops as ONE undo step (Clean voice, Match target, ducking). */
  onApply?: (ops: readonly EditOp[], label: string) => boolean | void;
  /** The project, for the loudness measurement and the audition (T-011). */
  projectId?: string;
  revision?: number;
  /** Saves the timeline first so the server mixes what is on screen. */
  flushSave?: () => Promise<FlushResult>;
  onToggle: (trackId: string, flag: 'mute' | 'solo') => void;
  /** The selected clips: the "Clean voice" panel cleans the clip you picked. */
  selection?: readonly string[];
}

/** Machine-readable dB for data attributes (never shown); the shown numbers go through `useFormat`. */
const fmt = (v: number, digits = 1) => (Number.isFinite(v) ? v.toFixed(digits) : '−∞');

/**
 * The Audio workspace (P5): one strip per audio track — fader (the track's `gainDb`, static or
 * automated; the lane is the editable automation), pan, mute/solo, a small EQ — plus the master
 * bus with a live LUFS/true-peak readout beside the last render's `loudnorm` measurement, and the
 * ducking derivation that writes the music track's automation from the dialogue tracks.
 *
 * Every control is an op (`setTrackProperty`), so the mix is in the document, undoable, and the
 * render plays exactly what the compositor previews. The live meter is SAMPLED (analyser windows),
 * which is why the render's number is shown next to it as the authority.
 */
export function MixerPanel({ doc, readOnly, playheadFrame, flags, getMeters, lastRenderLoudness, onOp, onApply: onApplyProp, projectId, revision = 0, flushSave, onToggle, selection = [] }: MixerPanelProps) {
  const t = useT('editor-audio.mixer');
  const nf = useFormat();
  const shown = (v: number) => (Number.isFinite(v) ? nf.number(v, { minimumFractionDigits: 1, maximumFractionDigits: 1 }) : '−∞');
  const [advanced, setAdvanced] = useAdvancedAudio();
  const onApply = onApplyProp ?? ((ops: readonly EditOp[], label: string) => ops.forEach((op, i) => onOp(op, i === 0 ? label : undefined)));
  const audioTracks = doc.tracks.filter((t) => t.kind === 'audio');
  const [peaks, setPeaks] = useState<Record<string, number>>({});
  const [readout, setReadout] = useState<LoudnessReadout | null>(null);
  const meterRef = useRef<LufsMeter | null>(null);
  const lastWindowRef = useRef<string>('');
  const [lane, setLane] = useState<string | null>(null);

  // Poll the meters while mounted: peaks per track and the master's LUFS from its sampled windows.
  useEffect(() => {
    let handle = 0;
    let disposed = false;
    const tick = () => {
      if (disposed) return;
      const snapshot = getMeters();
      if (snapshot) {
        const next: Record<string, number> = {};
        for (const m of snapshot.tracks) next[m.trackId] = m.peak;
        setPeaks(next);
        // A paused graph hands back the same analyser window every frame; feeding it again would rebuild the
        // integrated reading right after Reset, so a window identical to the last one is not measured twice.
        const print = windowFingerprint(snapshot.master);
        if (snapshot.master.length > 0 && print !== lastWindowRef.current) {
          lastWindowRef.current = print;
          if (!meterRef.current || meterRef.current.sampleRate !== snapshot.sampleRate) meterRef.current = new LufsMeter(snapshot.sampleRate, 2);
          meterRef.current.process(snapshot.master);
          setReadout(meterRef.current.readout());
        }
      }
      handle = window.requestAnimationFrame(tick);
    };
    handle = window.requestAnimationFrame(tick);
    return () => {
      disposed = true;
      window.cancelAnimationFrame(handle);
    };
  }, [getMeters]);

  const setTrack = (trackId: string, key: 'gainDb' | 'pan' | 'eq', value: unknown, label: string) =>
    onOp({ type: 'setTrackProperty', trackId, key, value: value as never }, label);

  return (
    <EditorPanel title={t('title')} actions={<Switch size="xs" label={t('advanced')} checked={advanced} onChange={(e) => setAdvanced(e.currentTarget.checked)} data-testid="advanced-audio" />} testId="mixer-panel" scroll={false}>
      <Stack gap="sm">
        {audioTracks.length === 0 && (
          <PageEmptyState page="audio" hint={t('noTracksHint')} testId="mixer-empty" compact />
        )}

        {audioTracks.map((track) => {
          const f = flags(track);
          const gain = track.gainDb ?? null;
          const hasKeys = Array.isArray(gain?.keys) && gain!.keys!.length > 0;
          const staticDb = typeof gain?.value === 'number' ? gain.value : 0;
          const peak = peaks[track.id] ?? 0;
          const peakDb = peak > 0 ? 20 * Math.log10(peak) : Number.NEGATIVE_INFINITY;
          const eq: TrackEq = track.eq ?? {};
          const band = eq.bands?.[0];
          return (
            <Box key={track.id} p={6} style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 6 }} data-testid={`mixer-strip-${track.id}`}>
              <Group justify="space-between" mb={4}>
                <Text size="xs" fw={600}>
                  {track.name}
                </Text>
                <Group gap={2}>
                  <Tooltip label={f.mute ? t('unmute') : t('mute')}>
                    <EditorIcon icon={f.mute ? IconVolumeOff : IconVolume} size="sm" tooltip={false} variant={f.mute ? 'filled' : 'subtle'} color="orange" label={t('muteTrack', { track: track.name })} onClick={() => onToggle(track.id, 'mute')} data-testid={`mixer-mute-${track.id}`} />
                  </Tooltip>
                  <Tooltip label={f.solo ? t('unsolo') : t('solo')}>
                    <EditorIcon icon={IconHeadphones} size="sm" tooltip={false} variant={f.solo ? 'filled' : 'subtle'} color="yellow" label={t('soloTrack', { track: track.name })} onClick={() => onToggle(track.id, 'solo')} data-testid={`mixer-solo-${track.id}`} />
                  </Tooltip>
                </Group>
              </Group>
              <Group gap="xs" align="flex-end" wrap="wrap">
                <Fader
                  value={staticDb}
                  automated={hasKeys}
                  disabled={readOnly}
                  label={track.name}
                  testId={`mixer-fader-${track.id}`}
                  onCommit={(v) => setTrack(track.id, 'gainDb', { value: v, keys: gain?.keys ?? null }, t('volumeLabel', { track: track.name }))}
                />
                <Stack gap={2} style={{ flex: '1 1 110px', minWidth: 110 }}>
                  <Text size="xs" c="dimmed">
                    {t('pan', { value: formatPan(track.pan) })}
                  </Text>
                  <div onDoubleClick={() => !readOnly && track.pan && setTrack(track.id, 'pan', undefined, t('centreLabel', { track: track.name }))} title={t('centreTip')}>
                  <LiveSlider size="xs" thumbLabel={t('panTrack', { track: track.name })} min={-1} max={1} step={0.05} value={track.pan ?? 0} disabled={readOnly} label={null} onChangeEnd={(v) => setTrack(track.id, 'pan', v === 0 ? undefined : v, t('panLabel', { track: track.name }))} data-testid={`mixer-pan-${track.id}`} />
                  </div>
                </Stack>
                <Box w={10} h={44} style={{ background: 'var(--mantine-color-default-hover)', borderRadius: 2, position: 'relative' }} role="meter" aria-label={t('peakMeter')} aria-valuemin={-60} aria-valuemax={0} aria-valuenow={Number.isFinite(peakDb) ? Math.max(-60, Math.min(0, peakDb)) : -60} aria-valuetext={t('db', { value: shown(peakDb) })} data-testid={`mixer-peak-${track.id}`} data-peak-db={fmt(peakDb)}>
                  <Box style={{ position: 'absolute', bottom: 0, left: 0, right: 0, height: `${Math.round(Math.min(1, Math.max(0, (peakDb + 60) / 60)) * 100)}%`, background: peakDb > -1 ? 'var(--mantine-color-red-6)' : peakDb > -12 ? 'var(--mantine-color-yellow-6)' : 'var(--mantine-color-teal-6)' }} />
                </Box>
              </Group>
              <Stack gap={4} mt={6}>
                <CleanVoiceSwitch track={track} disabled={readOnly} onApply={onApply} />
                <Switch size="xs" label={t('rumble')} description={t('rumbleHint', { hz: 80 })} styles={{ description: { fontSize: 12 } }} checked={typeof eq.lowCutHz === 'number'} disabled={readOnly} onChange={(e) => setTrack(track.id, 'eq', cleanEq({ ...eq, lowCutHz: e.currentTarget.checked ? 80 : null }), t('rumble'))} data-testid={`mixer-rumble-${track.id}`} />
              </Stack>
              {advanced && (
                <Group gap={4} mt={4} align="flex-end" wrap="wrap">
                <NumberInput size="xs" w={78} label={t('lowCutHz')} value={eq.lowCutHz ?? ''} min={20} max={20000} disabled={readOnly} onChange={(v) => setTrack(track.id, 'eq', cleanEq({ ...eq, lowCutHz: typeof v === 'number' ? v : null }), t('eqLowCut'))} data-testid={`mixer-lowcut-${track.id}`} />
                <NumberInput size="xs" w={78} label={t('highCutHz')} value={eq.highCutHz ?? ''} min={20} max={20000} disabled={readOnly} onChange={(v) => setTrack(track.id, 'eq', cleanEq({ ...eq, highCutHz: typeof v === 'number' ? v : null }), t('eqHighCut'))} />
                <NumberInput size="xs" w={78} label={t('bandHz')} value={band?.frequencyHz ?? ''} min={20} max={20000} disabled={readOnly} onChange={(v) => setTrack(track.id, 'eq', cleanEq({ ...eq, bands: typeof v === 'number' ? [{ frequencyHz: v, gainDb: band?.gainDb ?? 0, q: band?.q ?? 1 }] : null }), t('eqBand'))} />
                <NumberInput size="xs" w={78} label={t('bandDb')} value={band?.gainDb ?? 0} min={-24} max={24} step={0.5} disabled={readOnly || !band} onChange={(v) => band && typeof v === 'number' && setTrack(track.id, 'eq', cleanEq({ ...eq, bands: [{ ...band, gainDb: v }] }), t('eqBandGain'))} />
              </Group>
              )}
              {hasKeys && (
                <Stack gap={2} mt={4}>
                  <Button size="compact-xs" fz="xs" variant={lane === track.id ? 'filled' : 'subtle'} onClick={() => setLane(lane === track.id ? null : track.id)} data-testid={`mixer-lane-${track.id}`}>
                    {t('automationPoints', { count: gain!.keys!.length })}
                  </Button>
                  {lane === track.id && (
                    <KeyframeLane target={{ trackId: track.id }} param="gainDb" channel={gain} startFrame={0} endFrame={Math.max(1, timelineEnd(doc))} playheadFrame={playheadFrame} readOnly={readOnly} onOp={onOp} height={56} />
                  )}
                </Stack>
              )}
            </Box>
          );
        })}

        {advanced && (
          <Text size="xs" c="dimmed" data-testid="mixer-dsp-preview-note">
            {PREVIEW_NOTE}
          </Text>
        )}
        {projectId ? (
          <>
            <Divider />
            <CleanupPanel doc={doc} selection={selection} projectId={projectId} readOnly={readOnly} onApply={onApply} />
          </>
        ) : null}
        <Divider label={t('loudness')} labelPosition="left" />
        {projectId ? (
          <LoudnessTargetSection doc={doc} readOnly={readOnly} projectId={projectId} revision={revision} flushSave={flushSave} onApply={onApply} />
        ) : null}
        <Group gap="md" wrap="wrap" data-testid="mixer-master">
          <Stack gap={0}>
            <Text size="xs" c="dimmed">
              {t('livePreview')}
            </Text>
            <Text size="xs" ff="monospace">
              {t('liveReadout', { m: shown(readout?.momentaryLufs ?? Number.NEGATIVE_INFINITY), s: shown(readout?.shortTermLufs ?? Number.NEGATIVE_INFINITY), i: shown(readout?.integratedLufs ?? Number.NEGATIVE_INFINITY) })}
            </Text>
            <Text size="xs" ff="monospace">
              {t('truePeak', { value: shown(readout?.truePeakDbtp ?? Number.NEGATIVE_INFINITY) })}
            </Text>
          </Stack>
          <Tooltip label={t('resetTip')}>
            <EditorIcon tooltip={false} icon={IconRefresh} size="sm" label={t('resetMeter')} onClick={() => { meterRef.current?.reset(); setReadout(null); }} data-testid="mixer-reset-meter" />
          </Tooltip>
          <Stack gap={0}>
            <Text size="xs" c="dimmed">
              {t('lastRender')}
            </Text>
            {lastRenderLoudness ? (
              <Text size="xs" ff="monospace" data-testid="mixer-render-loudness">
                {t('renderReadout', { i: shown(lastRenderLoudness.integratedLufs), tp: shown(lastRenderLoudness.truePeakDbtp), lra: shown(lastRenderLoudness.loudnessRange) })}
                {lastRenderLoudness.normalised && <Badge size="xs" fz="xs" ml={4} variant="light">{t('normalised')}</Badge>}
              </Text>
            ) : (
              <Text size="xs" c="dimmed">
                {t('noRender')}
              </Text>
            )}
            {lastRenderLoudness && <RenderChecks checks={lastRenderLoudness.checks} />}
          </Stack>
        </Group>
        <Text size="xs" c="dimmed">
          {t('meterNote')}
        </Text>

        <Divider label={t('ducking')} labelPosition="left" />
        <DuckSection doc={doc} readOnly={readOnly} advanced={advanced} onApply={onApply} />

        {projectId ? (
          <>
            <Divider label={t('audition')} labelPosition="left" />
            <AuditionSection doc={doc} projectId={projectId} revision={revision} playheadFrame={playheadFrame} flushSave={flushSave} />
          </>
        ) : null}
      </Stack>
    </EditorPanel>
  );
}

/** A cheap identity for one analyser window: its length, its energy and its first and last samples, per channel. */
function windowFingerprint(master: readonly Float32Array[]): string {
  return master
    .map((channel) => {
      let energy = 0;
      for (let i = 0; i < channel.length; i += 7) energy += channel[i] * channel[i];
      return `${channel.length}:${energy.toFixed(9)}:${channel[0] ?? 0}:${channel[channel.length - 1] ?? 0}`;
    })
    .join('|');
}

function cleanEq(eq: TrackEq): TrackEq | undefined {
  const out: TrackEq = {};
  if (typeof eq.lowCutHz === 'number') out.lowCutHz = eq.lowCutHz;
  if (typeof eq.highCutHz === 'number') out.highCutHz = eq.highCutHz;
  if (eq.bands && eq.bands.length > 0) out.bands = eq.bands;
  return Object.keys(out).length === 0 ? undefined : out;
}

function timelineEnd(doc: EditTimeline): number {
  let end = 0;
  for (const track of doc.tracks) for (const item of track.items) if (item.recordOut > end) end = item.recordOut;
  return end;
}
