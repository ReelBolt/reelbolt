'use client';

import React, { useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { Badge, Button, Checkbox, Divider, Group, NumberInput, SegmentedControl, Select, Slider, Stack, Text, TextInput, Tooltip } from '@mantine/core';
import { IconPlus, IconTrash } from '@tabler/icons-react';
import { AUDIO_RANGES, type EditOp } from '@/lib/editing/engine';
import type { AudioBus, AudioDenoise, AudioDynamics, AudioLeveler, EditTimeline, Track } from '@/lib/editing/types';
import { compileLatchPass, valueAt, type AutomationMode, type AutomationSample } from '@/lib/editing/audio/automation';
import { NeedsLargerScreen } from '../shell/LargerScreen';
import { useDeviceClass } from '@/lib/editing/use-device-class';
import { useAdvancedAudio } from './useAdvancedAudio';
import { FADER_MAX_DB } from '@/lib/editing/audio/fader';
import { EditorIcon } from '../ui';
import { useFormat, useT } from '@/i18n';
import { BUS_FALLBACK_NOTE, detectDspSupport, DSP_STAGE_LABEL, NO_DSP_SUPPORT, PREVIEW_NOTE, planBusRouting, previewDifferenceNote, stageStatusText, type DspStage, type DspSupport } from '@/lib/editing/audio/dsp-preview';

/** The processing a track or a bus carries — one editor for both. */
export interface Processing {
  dynamics?: AudioDynamics | null;
  denoise?: AudioDenoise | null;
  leveler?: AudioLeveler | null;
}

export interface ProcessingFieldsProps {
  value: Processing;
  readOnly: boolean;
  testIdPrefix: string;
  onChange: (key: 'dynamics' | 'denoise' | 'leveler', value: AudioDynamics | AudioDenoise | AudioLeveler | null, label: string) => void;
  /** What the preview can run; detected in the browser when omitted. */
  support?: DspSupport;
}

const noopSubscribe = () => () => undefined;
const serverSupport = () => NO_DSP_SUPPORT;
let lastSupport: DspSupport = NO_DSP_SUPPORT;
/** A stable snapshot of the browser's DSP support (a new object only when the answer changes). */
function supportSnapshot(): DspSupport {
  const d = detectDspSupport();
  if (d.nodes !== lastSupport.nodes || d.worklet !== lastSupport.worklet) lastSupport = d;
  return lastSupport;
}

const range = (r: readonly [number, number]) => ({ min: r[0], max: r[1] });

/**
 * Dynamics (compressor, limiter, gate, de-esser), denoise and leveler. Each stage is a checkbox that
 * adds it at its documented defaults (the C# record defaults) and number fields clamped to the
 * validator's ranges; every change is one `setTrackProperty`/`setBus` — never a filter string.
 */
export function ProcessingFields({ value, readOnly, testIdPrefix, onChange, support: given }: ProcessingFieldsProps) {
  const t = useT('editor-audio.processing');
  const detected = useSyncExternalStore(noopSubscribe, supportSnapshot, serverSupport);
  const support = given ?? detected;
  // R08.6: whether this stage is heard in the viewer (the same feature detect the parity badge uses).
  const status = (stage: DspStage) => (
    <Tooltip label={previewDifferenceNote(stage)} multiline w={260}>
      <Text size="xs" c="dimmed" data-testid={`${testIdPrefix}-${stage}-status`} aria-label={`${DSP_STAGE_LABEL[stage]}: ${stageStatusText(stage, support)}`}>
        {stageStatusText(stage, support)}
      </Text>
    </Tooltip>
  );
  const dyn = value.dynamics ?? {};
  const setDyn = (patch: Partial<AudioDynamics>, label: string) => {
    const next: AudioDynamics = { ...dyn, ...patch };
    const empty = !next.compressor && !next.limiter && !next.gate && !next.deesser;
    onChange('dynamics', empty ? null : next, label);
  };
  const num = (label: string, current: number | undefined, fallback: number, r: readonly [number, number], step: number, apply: (v: number) => void, testId: string) => (
    <NumberInput size="xs" label={label} value={current ?? fallback} {...range(r)} step={step} clampBehavior="strict" disabled={readOnly} onChange={(v) => typeof v === 'number' && apply(v)} w={92} data-testid={testId} />
  );
  return (
    <Stack gap={6}>
      <Text size="xs" c="dimmed">
        {t('note', { note: PREVIEW_NOTE })}
      </Text>
      <Group gap={8} align="center" wrap="wrap">
        <Checkbox size="xs" label={t('compressor')} checked={!!dyn.compressor} disabled={readOnly} onChange={(e) => setDyn({ compressor: e.currentTarget.checked ? { thresholdDb: -18, ratio: 3, attackMs: 20, releaseMs: 250, makeupDb: 0 } : null }, t('undo.toggleCompressor'))} data-testid={`${testIdPrefix}-compressor`} />
        {status('compressor') /* eslint-disable-line i18next/no-literal-string -- ids and key hints, not copy */}
      </Group>
      {dyn.compressor && (
        <Group gap={4} wrap="wrap">
          {num(t('thresholdDb'), dyn.compressor.thresholdDb, -18, AUDIO_RANGES.compressor.thresholdDb, 1, (v) => setDyn({ compressor: { ...dyn.compressor, thresholdDb: v } }, t('undo.compressorThreshold')), `${testIdPrefix}-comp-threshold`)}
          {num(t('ratio'), dyn.compressor.ratio, 3, AUDIO_RANGES.compressor.ratio, 0.5, (v) => setDyn({ compressor: { ...dyn.compressor, ratio: v } }, t('undo.compressorRatio')), `${testIdPrefix}-comp-ratio`)}
          {num(t('attackMs'), dyn.compressor.attackMs, 20, AUDIO_RANGES.compressor.attackMs, 5, (v) => setDyn({ compressor: { ...dyn.compressor, attackMs: v } }, t('undo.compressorAttack')), `${testIdPrefix}-comp-attack`)}
          {num(t('releaseMs'), dyn.compressor.releaseMs, 250, AUDIO_RANGES.compressor.releaseMs, 10, (v) => setDyn({ compressor: { ...dyn.compressor, releaseMs: v } }, t('undo.compressorRelease')), `${testIdPrefix}-comp-release`)}
          {num(t('makeupDb'), dyn.compressor.makeupDb, 0, AUDIO_RANGES.compressor.makeupDb, 0.5, (v) => setDyn({ compressor: { ...dyn.compressor, makeupDb: v } }, t('undo.compressorMakeup')), `${testIdPrefix}-comp-makeup`)}
        </Group>
      )}
      <Group gap={8} align="center" wrap="wrap">
        <Checkbox size="xs" label={t('limiter')} checked={!!dyn.limiter} disabled={readOnly} onChange={(e) => setDyn({ limiter: e.currentTarget.checked ? { ceilingDb: -1 } : null }, t('undo.toggleLimiter'))} data-testid={`${testIdPrefix}-limiter`} />
        {status('limiter') /* eslint-disable-line i18next/no-literal-string -- ids and key hints, not copy */}
      </Group>
      {dyn.limiter && num(t('ceilingDb'), dyn.limiter.ceilingDb, -1, AUDIO_RANGES.limiter.ceilingDb, 0.5, (v) => setDyn({ limiter: { ceilingDb: v } }, t('undo.limiterCeiling')), `${testIdPrefix}-limiter-ceiling`)}
      <Group gap={8} align="center" wrap="wrap">
        <Checkbox size="xs" label={t('gate')} checked={!!dyn.gate} disabled={readOnly} onChange={(e) => setDyn({ gate: e.currentTarget.checked ? { thresholdDb: -40, ratio: 2, attackMs: 20, releaseMs: 250 } : null }, t('undo.toggleGate'))} data-testid={`${testIdPrefix}-gate`} />
        {status('gate') /* eslint-disable-line i18next/no-literal-string -- ids and key hints, not copy */}
      </Group>
      {dyn.gate && (
        <Group gap={4}>
          {num(t('thresholdDb'), dyn.gate.thresholdDb, -40, AUDIO_RANGES.gate.thresholdDb, 1, (v) => setDyn({ gate: { ...dyn.gate, thresholdDb: v } }, t('undo.gateThreshold')), `${testIdPrefix}-gate-threshold`)}
          {num(t('ratio'), dyn.gate.ratio, 2, AUDIO_RANGES.gate.ratio, 0.5, (v) => setDyn({ gate: { ...dyn.gate, ratio: v } }, t('undo.gateRatio')), `${testIdPrefix}-gate-ratio`)}
        </Group>
      )}
      <Group gap={8} align="center" wrap="wrap">
        <Checkbox size="xs" label={t('deesser')} checked={!!dyn.deesser} disabled={readOnly} onChange={(e) => setDyn({ deesser: e.currentTarget.checked ? { intensity: 0.5 } : null }, t('undo.toggleDeesser'))} data-testid={`${testIdPrefix}-deesser`} />
        {status('deesser') /* eslint-disable-line i18next/no-literal-string -- ids and key hints, not copy */}
      </Group>
      {dyn.deesser && num(t('intensity'), dyn.deesser.intensity, 0.5, AUDIO_RANGES.deesser.intensity, 0.05, (v) => setDyn({ deesser: { intensity: v } }, t('undo.deesserIntensity')), `${testIdPrefix}-deesser-intensity`)}
      <Checkbox size="xs" label={t('denoise')} checked={!!value.denoise} disabled={readOnly} onChange={(e) => onChange('denoise', e.currentTarget.checked ? { kind: 'fft', strength: 0.5 } : null, t('undo.toggleDenoise'))} data-testid={`${testIdPrefix}-denoise`} />
      {value.denoise && num(t('strength'), value.denoise.strength, 0.5, AUDIO_RANGES.denoise.strength, 0.05, (v) => onChange('denoise', { kind: 'fft', strength: v }, t('undo.denoiseStrength')), `${testIdPrefix}-denoise-strength`) /* eslint-disable-line i18next/no-literal-string -- ids and key hints, not copy */}
      <Checkbox size="xs" label={t('leveler')} checked={!!value.leveler} disabled={readOnly} onChange={(e) => onChange('leveler', e.currentTarget.checked ? { kind: 'speech', targetDb: -18 } : null, t('undo.toggleLeveler'))} data-testid={`${testIdPrefix}-leveler`} />
      {value.leveler && (
        <Group gap={4} align="end">
          <Select size="xs" label={t('kind')} data={[{ value: 'speech', label: t('speech') }, { value: 'dynamic', label: t('dynamic') }]} value={value.leveler.kind ?? 'speech'} disabled={readOnly} onChange={(k) => k && onChange('leveler', { ...value.leveler, kind: k as AudioLeveler['kind'] }, t('undo.levelerKind'))} w={110} allowDeselect={false} />
          {num(t('targetDb'), value.leveler.targetDb, -18, AUDIO_RANGES.leveler.targetDb, 1, (v) => onChange('leveler', { ...value.leveler, targetDb: v }, t('undo.levelerTarget')), `${testIdPrefix}-leveler-target`) /* eslint-disable-line i18next/no-literal-string -- ids and key hints, not copy */}
        </Group>
      )}
    </Stack>
  );
}

export interface BusesPanelProps {
  doc: EditTimeline;
  readOnly: boolean;
  playheadFrame: number;
  playbackSpeed: number;
  newId: () => string;
  onOp: (op: EditOp, label?: string) => void;
  onApply: (ops: readonly EditOp[], label: string) => void;
}

/**
 * The Audio workspace's routing half (P8): submix buses (route a track to a bus, a bus to another
 * bus, a bus with no output feeds the master), per-track and per-bus processing, and an automation
 * lane per track with off / read / latch. Latch records the fader while the transport plays and
 * writes the pass as ONE program when it stops.
 */
export function BusesPanel({ doc, readOnly, playheadFrame, playbackSpeed, newId, onOp, onApply }: BusesPanelProps) {
  const t = useT('editor-audio.buses');
  const buses = doc.buses ?? [];
  const busPlan = planBusRouting(doc);
  const audioTracks = doc.tracks.filter((x) => x.kind === 'audio');
  const [newName, setNewName] = useState('');
  const busOptions = [{ value: '', label: t('master') }, ...buses.map((b) => ({ value: b.id, label: b.name }))];

  const setBus = (bus: AudioBus, label: string) => onOp({ type: 'setBus', busId: bus.id, bus }, label);
  // Buses, latch automation and the processing numbers are cut on a phone (the mixer keeps gain).
  const phone = useDeviceClass() === 'phone';
  const [advanced, setAdvanced] = useAdvancedAudio();

  if (phone) {
    return (
      <Stack gap="sm" p="xs" data-testid="buses-panel">
        <Text size="sm" fw={600}>
          {t('title')}
        </Text>
        <NeedsLargerScreen feature="buses-automation" what={t('phoneWhat')} hint={t('phoneHint')} />
      </Stack>
    );
  }

  if (!advanced) {
    return (
      <Stack gap="xs" p="xs" data-testid="buses-panel-collapsed">
        <Text size="sm" fw={600}>
          {t('advancedTitle')}
        </Text>
        <Text size="xs" c="dimmed">
          {t('advancedHint')}
        </Text>
        <Button size="compact-xs" fz="xs" variant="light" onClick={() => setAdvanced(true)} style={{ alignSelf: 'flex-start' }} data-testid="advanced-audio-open">
          {t('showAdvanced')}
        </Button>
      </Stack>
    );
  }
  return (
    <Stack gap="sm" p="xs" data-testid="buses-panel">
      <Text size="sm" fw={600}>
        {t('title')}
      </Text>
      <Text size="xs" c="dimmed" data-testid="buses-render-note">
        {busPlan.ok ? t('routingNoteOk', { note: PREVIEW_NOTE }) : t('routingNoteFallback', { note: BUS_FALLBACK_NOTE, reason: busPlan.reason })}
      </Text>
      <Group gap={4} align="end">
        <TextInput size="xs" label={t('newBus')} placeholder={t('newBusPlaceholder')} value={newName} onChange={(e) => setNewName(e.currentTarget.value)} disabled={readOnly} data-testid="bus-new-name" />
        <Button
          size="compact-xs" fz="xs"
          leftSection={<IconPlus size={12} />}
          disabled={readOnly || newName.trim().length === 0 || buses.length >= 16}
          onClick={() => {
            const id = newId();
            onOp({ type: 'setBus', busId: id, bus: { id, name: newName.trim(), pan: 0, outputBusId: null } }, t('undo.addBus', { name: newName.trim() }));
            setNewName('');
          }}
          data-testid="bus-add"
        >
          {t('addBus')}
        </Button>
      </Group>
      {buses.map((bus) => (
        <Stack key={bus.id} gap={4} p={6} style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 4 }} data-testid={`bus-${bus.id}`}>
          <Group justify="space-between">
            <TextInput size="xs" defaultValue={bus.name} key={`${bus.id}:${bus.name}`} disabled={readOnly} aria-label={t('busName')} onBlur={(e) => { const name = e.currentTarget.value.trim(); if (name && name !== bus.name) setBus({ ...bus, name }, t('undo.renameBus')); }} />
            <EditorIcon icon={IconTrash} size="sm" color="red" disabled={readOnly} label={t('removeBus', { name: bus.name })} onClick={() => onOp({ type: 'setBus', busId: bus.id, bus: undefined }, t('undo.removeBus', { name: bus.name }))} data-testid={`bus-remove-${bus.id}`} />
          </Group>
          <Group gap={4} align="end">
            <Select size="xs" label={t('output')} data={busOptions.filter((o) => o.value !== bus.id)} value={bus.outputBusId ?? ''} disabled={readOnly} allowDeselect={false} onChange={(v) => setBus({ ...bus, outputBusId: v ? v : null }, t('undo.routeBus'))} w={130} data-testid={`bus-output-${bus.id}`} />
            <NumberInput size="xs" label={t('gainDb')} value={bus.gainDb?.value ?? 0} min={-60} max={20} step={0.5} clampBehavior="strict" disabled={readOnly} onChange={(v) => typeof v === 'number' && setBus({ ...bus, gainDb: { ...(bus.gainDb ?? {}), value: v } }, t('undo.busGain'))} w={90} />
            <NumberInput size="xs" label={t('pan')} value={bus.pan ?? 0} min={-1} max={1} step={0.1} clampBehavior="strict" disabled={readOnly} onChange={(v) => typeof v === 'number' && setBus({ ...bus, pan: v }, t('undo.busPan'))} w={80} />
          </Group>
          <ProcessingFields value={bus} readOnly={readOnly} testIdPrefix={`bus-${bus.id}`} onChange={(key, value, label) => setBus({ ...bus, [key]: value }, t('undo.withName', { label, name: bus.name }))} />
        </Stack>
      ))}
      <Divider label={t('tracks')} labelPosition="left" />
      {audioTracks.length === 0 && (
        <Text size="xs" c="dimmed">
          {t('noTracks')}
        </Text>
      )}
      {audioTracks.map((track) => (
        <TrackStrip key={track.id} track={track} doc={doc} readOnly={readOnly} busOptions={busOptions} playheadFrame={playheadFrame} playbackSpeed={playbackSpeed} onOp={onOp} onApply={onApply} />
      ))}
    </Stack>
  );
}

function TrackStrip({ track, readOnly, busOptions, playheadFrame, playbackSpeed, onOp, onApply }: { track: Track; doc: EditTimeline; readOnly: boolean; busOptions: { value: string; label: string }[]; playheadFrame: number; playbackSpeed: number; onOp: (op: EditOp, label?: string) => void; onApply: (ops: readonly EditOp[], label: string) => void }) {
  const t = useT('editor-audio.buses');
  const nf = useFormat();
  const [mode, setMode] = useState<AutomationMode>('off');
  const [fader, setFader] = useState<number>(track.gainDb?.value ?? 0);
  const samples = useRef<AutomationSample[]>([]);
  const [writing, setWriting] = useState(false);
  const routedToMissingBus = !!track.busId && !busOptions.some((o) => o.value === track.busId);
  const shown = mode === 'read' || (mode === 'latch' && playbackSpeed === 0) ? valueAt(track.gainDb, playheadFrame, 0) : fader;

  // Latch: when the transport stops, the recorded pass becomes one program.
  useEffect(() => {
    if (playbackSpeed !== 0 || samples.current.length === 0) return;
    const pass = samples.current;
    samples.current = [];
    setWriting(false);
    const ops = compileLatchPass({ trackId: track.id }, track.gainDb, pass);
    if (ops.length > 0) onApply(ops, t('undo.automationPass', { name: track.name }));
  }, [playbackSpeed, track.id, track.gainDb, track.name, onApply, t]);

  return (
    <Stack gap={4} p={6} style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 4 }} data-testid={`track-strip-${track.id}`}>
      <Group justify="space-between">
        <Text size="xs" fw={600}>
          {track.name}
        </Text>
        {routedToMissingBus && (
          <Tooltip label={t('busRemovedTip')}>
            <Badge size="xs" fz="xs" color="yellow">{t('busRemoved')}</Badge>
          </Tooltip>
        )}
      </Group>
      <Group gap={4} align="end">
        <Select size="xs" label={t('routeTo')} data={busOptions} value={track.busId && !routedToMissingBus ? track.busId : ''} disabled={readOnly} allowDeselect={false} onChange={(v) => onOp({ type: 'setTrackProperty', trackId: track.id, key: 'busId', value: v ? v : null }, t('undo.routeTrack', { name: track.name }))} w={130} data-testid={`track-route-${track.id}`} />
        <SegmentedControl size="xs" value={mode} onChange={(v) => setMode(v as AutomationMode)} data={[{ value: 'off', label: t('modes.off') }, { value: 'read', label: t('modes.read') }, { value: 'latch', label: t('modes.latch') }]} aria-label={t('automationMode')} data-testid={`automation-mode-${track.id}`} />
      </Group>
      <Group gap={6} align="center">
        <Text size="xs" c="dimmed" w={60}>
          {writing ? t('writing') : t('fader')}
        </Text>
        <Slider
          size="xs"
          style={{ flex: 1 }}
          thumbLabel={t('automationFader')}
          min={-60}
          max={FADER_MAX_DB}
          step={0.5}
          value={Math.min(FADER_MAX_DB, shown)}
          disabled={readOnly || mode === 'read'}
          label={(v) => t('db', { value: nf.number(v, { minimumFractionDigits: 1, maximumFractionDigits: 1 }) })}
          onChange={(v) => {
            setFader(v);
            if (mode === 'latch' && playbackSpeed !== 0) {
              samples.current.push({ frame: playheadFrame, value: v });
              if (!writing) setWriting(true);
            }
          }}
          onChangeEnd={(v) => {
            if (mode === 'off') onOp({ type: 'setTrackProperty', trackId: track.id, key: 'gainDb', value: { ...(track.gainDb ?? {}), value: v } }, t('undo.fader', { name: track.name }));
          }}
          data-testid={`automation-fader-${track.id}`}
        />
      </Group>
      <ProcessingFields value={track} readOnly={readOnly} testIdPrefix={`track-${track.id}`} onChange={(key, value, label) => onOp({ type: 'setTrackProperty', trackId: track.id, key, value }, t('undo.withName', { label, name: track.name }))} />
    </Stack>
  );
}
