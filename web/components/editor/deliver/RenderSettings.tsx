'use client';

import React, { useEffect, useState } from 'react';
import Link from 'next/link';
import { Anchor, Button, Collapse, Group, NumberInput, SegmentedControl, Select, Stack, Switch, Text, TextInput } from '@mantine/core';
import { createPortal } from 'react-dom';
import { IconChevronDown, IconChevronRight, IconSend } from '@tabler/icons-react';
import type { RenderEditTimelineRequest } from '@/lib/editing/sync/api';
import type { TimelineRenderEncoderSpeed, TimelineRenderPreset, TimelineRenderRateControl, TimelineRenderVideoCodec } from '@/lib/types/workflow';
import { BILLING_SETTINGS_PATH, outputCapMsg } from '@/components/styles/style-options';
import { useDeviceClass } from '@/lib/editing/use-device-class';
import { PresetPicker } from '../presets/PresetPicker';
import { MASTERING_PRESETS, clampMastering } from '@/lib/editing/presets';
import type { MasteringPresetPayload } from '@/lib/editing/api/editor-p9';
import { phoneFormatOrder } from './deliver-phone';
import { FORMAT_OPTIONS, QUALITY_STEPS, buildRenderRequest, defaultPresetFor, fitWarningMsg, type RenderQuality } from './deliver-model';
import { useT } from '@/i18n';
import { MAX_AUDIO_KBPS, MAX_VIDEO_KBPS, MIN_AUDIO_KBPS, MIN_VIDEO_KBPS, PLATFORM_SUGGESTIONS, kbpsToMbps, mbpsToKbps } from './deliver-platforms';
import { framesToTimecode } from '@/lib/editing/time';
import type { Rational } from '@/lib/editing/types';
import { EditorPanel } from '../ui/EditorPanel';
import { useLiveChord } from '../prefs/live-chords';
import { CostStatement, type CostState } from './CostStatement';
import { MyPresets } from './MyPresets';
import { PresetList } from './PresetList';
import { DEFAULT_FORM_SETTINGS, presetAvailability, presetPicture, type RenderFormSettings } from './render-presets';
import classes from './Deliver.module.css';

export { presetShortSide } from './render-presets';

export interface RenderSettingsProps {
  /** The in/out marks of the editor, offered as the "marked range" choice when both are set. */
  inOut: { inFrame: number | null; outFrame: number | null };
  /** The revision the editor is showing: what the render will be pinned to. */
  revision: number;
  readOnly: boolean;
  busy: boolean;
  /** Starts the render; the second argument is the form's settings, so the page can remember what the render was started with. */
  onRender: (request: RenderEditTimelineRequest, settings: RenderFormSettings) => void;
  /** P9: the project, for its saved mastering presets. */
  projectId?: string | null;
  /**
   * The plan's largest output short side in pixels; null when unlimited or unknown (billing off).
   * Only the explicit 4K preset above it is disabled (the server answers 402 for it); a shape
   * preset or `Master` above it stays available and the engine clamps it to the cap, which the
   * form says under the preset.
   */
  maxOutputHeight?: number | null;
  /** The short side of the timeline's own size, which `Master` renders at; null when unknown. */
  masterShortSide?: number | null;
  /** The timeline's own size, to warn when the chosen shape will crop or letterbox the picture. */
  timelineSize?: { width: number; height: number; rate?: Rational } | null;
  /**
   * Bumped by "Export only <clip>": the marks it just set must also switch the range on, otherwise the
   * Render that follows would export the whole timeline.
   */
  rangeRequest?: number;
  /** Told the request the Render button would send, whenever it changes (for the credit estimate and "Export this clip"). */
  onRequestChange?: (request: RenderEditTimelineRequest, settings: RenderFormSettings) => void;
  /** Why Render is off right now, in a sentence ("Save first: ..."); null when nothing blocks it. */
  blockedReason?: string | null;
  /** A way out of the blocked state, shown beside the reason (for "Save first": try the save again). */
  blockedAction?: { label: string; onClick: () => void } | null;
  /** "About 12 credits, about 3 min", or null (billing off, nothing computed yet). */
  estimateLine?: string | null;
  /** "You need 3 more credits": Render is off and an "Add credits" link is shown. */
  needMoreLine?: string | null;
  /** Phone: where the Render button and the estimate go (the sheet's sticky footer); absent = at the end of the form. */
  footerSlot?: HTMLElement | null;
  /** Phone: extra choices (caption delivery) shown inside "More options". */
  moreSlot?: React.ReactNode;
  /** "About 12 MB": the size the estimate expects, or null when it has none yet. */
  sizeLine?: string | null;
  /** Desktop and tablet: where the cost of the settings on screen stands, for the statement above Render. */
  cost?: CostState;
  /** Desktop and tablet: the caption choice, shown with the other settings of this render. */
  captionsSlot?: React.ReactNode;
  /** Where the captions go, in words, for the statement; null when the timeline has none. */
  captionsSummary?: string | null;
  /** The timeline's last frame, so "whole timeline" can be said in timecode; null when unknown. */
  programFrames?: number | null;
  /** True when the timeline holds nothing to render. */
  timelineEmpty?: boolean;
  /** The way out of an empty timeline: open the page where clips are placed. */
  onOpenEdit?: () => void;
  /** "Render again": fills the form with what an earlier render was started with. A new token applies it once. */
  prefill?: { token: number; settings: RenderFormSettings; useRange: boolean; fileName: string } | null;
  /** Bumped to move the keyboard focus to the preset list. */
  focusPresetsRequest?: number;
}

/** Mirrors `TimelineRenderRateControl`. */
const RATE_CONTROLS: TimelineRenderRateControl[] = ['Quality', 'Capped', 'Average'];
const CODECS: { value: TimelineRenderVideoCodec }[] = [{ value: 'H264' }, { value: 'H265' }];
const SPEEDS: { value: TimelineRenderEncoderSpeed }[] = [{ value: 'Fast' }, { value: 'Medium' }, { value: 'Slow' }];

/**
 * The Deliver workspace's settings: a format by its plain name, quality in three named steps, whole timeline or the
 * marked range, a file name, and an Advanced fold with the encoder, speed and loudness details. Every value is an
 * allowlisted choice or a clamped number, because the server validates the same ranges and a value outside them is
 * a 400, never an argv.
 */
export function RenderSettings({
  inOut, revision, readOnly, busy, onRender, projectId = null, maxOutputHeight = null, masterShortSide = null, timelineSize = null,
  rangeRequest = 0, onRequestChange, blockedReason = null, blockedAction = null, estimateLine = null, needMoreLine = null, footerSlot = null, moreSlot = null, sizeLine = null,
  cost = { kind: 'idle' }, captionsSlot = null, captionsSummary = null, programFrames = null, timelineEmpty = false, onOpenEdit, prefill = null, focusPresetsRequest = 0,
}: RenderSettingsProps) {
  const t = useT('editor.deliver.renderSettings');
  const phone = useDeviceClass() === 'phone';
  // T-014: the chords a person set, read live; outside the editor frame (and on a phone) there are none to show.
  const markInChord = useLiveChord('markIn');
  const markOutChord = useLiveChord('markOut');
  const editChord = useLiveChord('pageEdit');
  const [preset, setPreset] = useState<TimelineRenderPreset>(() => defaultPresetFor(timelineSize, phone));
  // The timeline size can arrive after the first render: follow it until the user picks a format themselves.
  const [presetTouched, setPresetTouched] = useState(false);
  const sizeW = timelineSize?.width ?? 0;
  const sizeH = timelineSize?.height ?? 0;
  const [seenSize, setSeenSize] = useState(`${sizeW}x${sizeH}`);
  if (seenSize !== `${sizeW}x${sizeH}`) {
    setSeenSize(`${sizeW}x${sizeH}`);
    if (!presetTouched) setPreset(defaultPresetFor(timelineSize, phone));
  }
  // W0.6: the stored in/out range drives the export: it starts switched on whenever both marks are set.
  const [useRange, setUseRange] = useState(() => inOut.inFrame !== null && inOut.outFrame !== null && inOut.outFrame > inOut.inFrame);
  const [fileName, setFileName] = useState('');
  const [quality, setQuality] = useState<RenderQuality>('good');
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const [normalise, setNormalise] = useState(DEFAULT_FORM_SETTINGS.normalise);
  const [targetLufs, setTargetLufs] = useState(DEFAULT_FORM_SETTINGS.targetLufs);
  const [truePeak, setTruePeak] = useState(DEFAULT_FORM_SETTINGS.truePeak);
  const [loudnessRange, setLoudnessRange] = useState(DEFAULT_FORM_SETTINGS.loudnessRange);
  const [custom, setCustom] = useState(DEFAULT_FORM_SETTINGS.custom);
  const [codec, setCodec] = useState<TimelineRenderVideoCodec>(DEFAULT_FORM_SETTINGS.codec);
  const [crf, setCrf] = useState(DEFAULT_FORM_SETTINGS.crf);
  const [speed, setSpeed] = useState<TimelineRenderEncoderSpeed>(DEFAULT_FORM_SETTINGS.speed);
  const [audioKbps, setAudioKbps] = useState(DEFAULT_FORM_SETTINGS.audioKbps);
  const [sizeOpen, setSizeOpen] = useState(false);
  const [rateControl, setRateControl] = useState<TimelineRenderRateControl>(DEFAULT_FORM_SETTINGS.rateControl);
  const [videoKbps, setVideoKbps] = useState(DEFAULT_FORM_SETTINGS.videoKbps);

  /** Everything a preset of your own holds: the form without the range and the file name. */
  const settings: RenderFormSettings = { preset, quality, custom, codec, crf, speed, rateControl, videoKbps, audioKbps, normalise, targetLufs, truePeak, loudnessRange };
  const applySettings = (next: RenderFormSettings) => {
    setPresetTouched(true);
    setPreset(next.preset);
    setQuality(next.quality);
    setCustom(next.custom);
    setCodec(next.codec);
    setCrf(next.crf);
    setSpeed(next.speed);
    setRateControl(next.rateControl);
    setVideoKbps(next.videoKbps);
    setAudioKbps(next.audioKbps);
    setNormalise(next.normalise);
    setTargetLufs(next.targetLufs);
    setTruePeak(next.truePeak);
    setLoudnessRange(next.loudnessRange);
  };

  // "Render again" hands over what an earlier render was started with; a new token applies it once.
  const [seenPrefill, setSeenPrefill] = useState(0);
  if (prefill && prefill.token !== seenPrefill) {
    setSeenPrefill(prefill.token);
    applySettings(prefill.settings);
    setUseRange(prefill.useRange);
    setFileName(prefill.fileName);
  }

  // "Export only <clip>" bumps rangeRequest: switch the range on during the render that sees the new value.
  const [seenRangeRequest, setSeenRangeRequest] = useState(rangeRequest);
  if (rangeRequest !== seenRangeRequest) {
    setSeenRangeRequest(rangeRequest);
    setUseRange(true);
  }

  const rangeAvailable = inOut.inFrame !== null && inOut.outFrame !== null && inOut.outFrame > inOut.inFrame;
  const safeName = fileName.trim();
  const nameOk = safeName === '' || /^[A-Za-z0-9][A-Za-z0-9._ -]*$/.test(safeName);
  // Only the explicit 4K choice is refused above the plan; shape presets and Master are clamped.
  const availability = (p: TimelineRenderPreset) => presetAvailability(p, masterShortSide, maxOutputHeight);
  const refused = (p: TimelineRenderPreset) => availability(p) === 'refused';
  const presetData = (phone ? phoneFormatOrder(FORMAT_OPTIONS) : FORMAT_OPTIONS).map((o) => ({ ...o, label: t(`formats.${o.value}`), disabled: refused(o.value) }));
  const qualityData = QUALITY_STEPS.map((q) => ({ value: q.value, label: t(`qualitySteps.${q.value}`) }));
  const anyRefused = presetData.some((o) => o.disabled);
  const presetOk = !refused(preset);
  const clamped = availability(preset) === 'clamped';
  const shapeWarningMsg = fitWarningMsg(timelineSize, preset);
  const shapeWarning = shapeWarningMsg ? t.msg(shapeWarningMsg) : null;

  const request: RenderEditTimelineRequest = buildRenderRequest({
    revision,
    preset,
    range: useRange && rangeAvailable ? { inFrame: inOut.inFrame as number, outFrame: inOut.outFrame as number } : null,
    fileName: safeName,
    quality,
    advanced: custom ? { codec, crf, speed, audioKbps } : null,
    rate: { control: rateControl, videoKbps },
    audioKbps,
    loudness: normalise ? { targetLufs, truePeakDbtp: truePeak, loudnessRange } : null,
  });
  const requestKey = JSON.stringify(request);
  const settingsKey = JSON.stringify(settings);
  useEffect(() => {
    onRequestChange?.(JSON.parse(requestKey) as RenderEditTimelineRequest, JSON.parse(settingsKey) as RenderFormSettings);
  }, [requestKey, settingsKey, onRequestChange]);

  // The phone keeps its own rules (its footer says why); the page also refuses an empty timeline and a refused estimate.
  const costRefused = !phone && cost.kind === 'refused';
  const blocked = readOnly || busy || !nameOk || !presetOk || !!blockedReason || !!needMoreLine || (!phone && timelineEmpty) || costRefused;

  // The label names what the button makes; a format the plan scales down is named at the size it will come out.
  const renderLabel = phone ? t('render') : clamped && maxOutputHeight !== null && preset !== 'AudioOnly' ? t('renderClamped', { cap: maxOutputHeight }) : t(`renderLabel.${preset}`);
  const rangeHint = !phone && markInChord.display && markOutChord.display ? t('onlyTheMarkedInChords', { markIn: markInChord.display, markOut: markOutChord.display }) : t('onlyTheMarkedIn2');

  const basics = (
    <>
      <Switch
        size="sm"
        styles={phone ? { root: { minHeight: 44, display: 'flex', alignItems: 'center' }, track: { minHeight: 24 } } : undefined}
        label={rangeAvailable ? t('onlyTheMarkedIn') : rangeHint}
        checked={useRange && rangeAvailable}
        disabled={!rangeAvailable}
        onChange={(e) => setUseRange(e.currentTarget.checked)}
        data-testid="render-range"
      />
      <TextInput
        label={t('fileName')}
        size={phone ? 'md' : 'sm'}
        styles={phone ? { input: { minHeight: 44 } } : undefined}
        placeholder={t('leaveBlankToName')}
        value={fileName}
        onChange={(e) => setFileName(e.currentTarget.value)}
        error={nameOk ? undefined : t('lettersDigitsSpacesDots')}
        data-testid="render-file-name"
      />
    </>
  );

  const qualityControl =
    preset !== 'AudioOnly' ? (
      <Stack gap={4} style={phone ? { flex: '1 1 100%', minWidth: 0 } : undefined}>
        <Text size="sm" fw={500}>
          {t('quality')}
        </Text>
        <SegmentedControl size={phone ? 'md' : 'sm'} styles={phone ? { root: { minHeight: 44 } } : undefined} fullWidth data={qualityData} value={quality} onChange={(v) => setQuality(v as RenderQuality)} disabled={custom} data-testid="render-quality" />
      </Stack>
    ) : null;

  const planNotes = (
    <>
      {shapeWarning && (
        <Text size="xs" c="orange.9" role="status" data-testid="render-fit-warning">
          {shapeWarning}
        </Text>
      )}
      {/* A shape preset above the plan still renders: the engine scales it down to the cap. */}
      {clamped && maxOutputHeight !== null && (
        <Text size="xs" c="dimmed" id="render-preset-clamp" data-testid="render-preset-clamp">
          {t('exportsAtPOn', { maxOutputHeight })}{' '}
          <Anchor component={Link} href={BILLING_SETTINGS_PATH} size="xs">
            {t('upgradeForHigherResolution')}
          </Anchor>
        </Text>
      )}
      {/* Why 4K is unavailable, in text rather than only in the disabled option. */}
      {anyRefused && maxOutputHeight !== null && (
        <Text size="xs" c="dimmed" id="render-preset-cap" data-testid="render-preset-cap">
          {t.msg(outputCapMsg(maxOutputHeight))}{' '}
          <Anchor component={Link} href={BILLING_SETTINGS_PATH} size="xs">
            {t('seePlans')}
          </Anchor>
        </Text>
      )}
    </>
  );

  const blockedLines = (
    <>
      {blockedReason ? (
        <Text size="xs" c="red" role="alert" data-testid="render-blocked">
          {blockedReason}
          {blockedAction ? (
            <>
              {' '}
              <Anchor component="button" type="button" size="xs" onClick={blockedAction.onClick} data-testid="render-blocked-action">
                {blockedAction.label}
              </Anchor>
            </>
          ) : null}
        </Text>
      ) : needMoreLine ? (
        <Text size="xs" c="red" role="status" data-testid="render-need-credits">
          {needMoreLine}.{' '}
          <Anchor component={Link} href={BILLING_SETTINGS_PATH} size="xs" data-testid="render-add-credits">
            {t('addCredits')}
          </Anchor>
        </Text>
      ) : null}
    </>
  );

  const startButton = (
    <Button leftSection={<IconSend size={16} />} onClick={() => onRender(request, settings)} disabled={blocked} loading={busy} style={{ minHeight: 44 }} data-testid="render-start">
      {renderLabel}
    </Button>
  );

  const sizeAndAdvanced = (
    <>
      {preset !== 'AudioOnly' && (
        <>
          <Anchor
            component="button"
            type="button"
            size="sm"
            onClick={() => setSizeOpen((o) => !o)}
            aria-expanded={sizeOpen}
            style={{ display: 'inline-flex', alignItems: 'center', gap: 4, minHeight: phone ? 44 : 28 }}
            data-testid="render-size-toggle"
          >
            {sizeOpen ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
            {t('sizeAndQuality')}
          </Anchor>
          <Collapse expanded={sizeOpen}>
            <Stack gap="xs" data-testid="render-size">
              <Text size="sm" fw={500}>
                {t('platformsLabel')}
              </Text>
              <Group gap="xs" data-testid="render-platforms">
                {PLATFORM_SUGGESTIONS.map((p) => (
                  <Button
                    key={p.id}
                    variant="light"
                    size={phone ? 'md' : 'xs'}
                    style={phone ? { minHeight: 44 } : undefined}
                    onClick={() => {
                      if (p.preset) {
                        setPresetTouched(true);
                        setPreset(p.preset);
                      }
                      setRateControl(p.rateControl);
                      setVideoKbps(p.videoKbps);
                      setAudioKbps(p.audioKbps);
                    }}
                    data-testid={`render-platform-${p.id}`}
                  >
                    {t(`platforms.${p.id}`)}
                  </Button>
                ))}
              </Group>
              <Text size="xs" c="dimmed">
                {t('platformsNote')}
              </Text>
              <Select
                label={t('rateControl')}
                size={phone ? 'md' : 'sm'}
                styles={phone ? { input: { minHeight: 44 } } : undefined}
                comboboxProps={phone ? { zIndex: 400 } : undefined}
                data={RATE_CONTROLS.map((v) => ({ value: v, label: t(`rateModes.${v}`) }))}
                value={rateControl}
                allowDeselect={false}
                onChange={(v) => v && setRateControl(v as TimelineRenderRateControl)}
                data-testid="render-rate-control"
              />
              <Text size="xs" c="dimmed">
                {t(`rateHints.${rateControl}`)}
              </Text>
              {rateControl !== 'Quality' && (
                <NumberInput
                  label={t('videoMbps')}
                  size={phone ? 'md' : 'sm'}
                  styles={phone ? { input: { minHeight: 44 } } : undefined}
                  min={kbpsToMbps(MIN_VIDEO_KBPS)}
                  max={kbpsToMbps(MAX_VIDEO_KBPS)}
                  step={0.5}
                  decimalScale={1}
                  value={kbpsToMbps(videoKbps)}
                  onChange={(v) => setVideoKbps(typeof v === 'number' ? mbpsToKbps(v) : 8000)}
                  data-testid="render-video-mbps"
                />
              )}
              <NumberInput
                label={t('audioBitrateKbps')}
                size={phone ? 'md' : 'sm'}
                styles={phone ? { input: { minHeight: 44 } } : undefined}
                min={MIN_AUDIO_KBPS}
                max={MAX_AUDIO_KBPS}
                value={audioKbps}
                onChange={(v) => setAudioKbps(typeof v === 'number' ? Math.min(MAX_AUDIO_KBPS, Math.max(MIN_AUDIO_KBPS, Math.round(v))) : 192)}
                data-testid="render-audio-kbps"
              />
              {sizeLine ? (
                <Text size="sm" data-testid="render-size-line">
                  {sizeLine}
                </Text>
              ) : null}
            </Stack>
          </Collapse>
        </>
      )}
      <Anchor
        component="button"
        type="button"
        size="sm"
        onClick={() => setAdvancedOpen((o) => !o)}
        aria-expanded={advancedOpen}
        style={{ display: 'inline-flex', alignItems: 'center', gap: 4, minHeight: phone ? 44 : 28 }}
        data-testid="render-advanced-toggle"
      >
        {advancedOpen ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
        {phone ? t('moreOptions') : t('advanced')}
      </Anchor>
      <Collapse expanded={advancedOpen}>
        <Stack gap="xs" data-testid="render-advanced">
          {phone && basics}
          {phone && moreSlot}
          <Switch size="sm" styles={phone ? { root: { minHeight: 44, display: 'flex', alignItems: 'center' } } : undefined} label={t('normaliseLoudness')} checked={normalise} onChange={(e) => setNormalise(e.currentTarget.checked)} />
          {normalise && (
            <Stack gap={4}>
              <PresetPicker<MasteringPresetPayload>
                projectId={projectId}
                kind="mastering"
                builtIns={MASTERING_PRESETS}
                current={{ targetLufs, truePeakDbtp: truePeak, loudnessRange }}
                readOnly={readOnly}
                onApply={(payload) => {
                  const m = clampMastering(payload);
                  setTargetLufs(m.targetLufs);
                  setTruePeak(m.truePeakDbtp);
                  setLoudnessRange(m.loudnessRange ?? 11);
                }}
                testId="mastering-presets"
              />
              <Group grow>
                <NumberInput label={t('targetLufs')} size={phone ? 'md' : 'sm'} styles={phone ? { input: { minHeight: 44 } } : undefined} min={-36} max={-5} value={targetLufs} onChange={(v) => setTargetLufs(typeof v === 'number' ? v : -16)} data-testid="render-target-lufs" />
                <NumberInput label={t('truePeakDbtp')} size={phone ? 'md' : 'sm'} styles={phone ? { input: { minHeight: 44 } } : undefined} min={-9} max={0} step={0.5} value={truePeak} onChange={(v) => setTruePeak(typeof v === 'number' ? v : -1.5)} data-testid="render-true-peak" />
              </Group>
            </Stack>
          )}
          <Switch size="sm" styles={phone ? { root: { minHeight: 44, display: 'flex', alignItems: 'center' } } : undefined} label={t('chooseCodecAndSpeed')} checked={custom} onChange={(e) => setCustom(e.currentTarget.checked)} data-testid="render-custom" />
          {custom && (
            <Stack gap={4}>
              <Select label={t('videoCodec')} size={phone ? 'md' : 'sm'} styles={phone ? { input: { minHeight: 44 } } : undefined} data={CODECS.map((c) => ({ value: c.value, label: t(`codecs.${c.value}`) }))} value={codec} allowDeselect={false} onChange={(v) => v && setCodec(v as TimelineRenderVideoCodec)} />
              <Select label={t('speedVsSize')} size={phone ? 'md' : 'sm'} styles={phone ? { input: { minHeight: 44 } } : undefined} data={SPEEDS.map((s) => ({ value: s.value, label: t(`speeds.${s.value}`) }))} value={speed} allowDeselect={false} onChange={(v) => v && setSpeed(v as TimelineRenderEncoderSpeed)} />
              <NumberInput label={t('pictureQuality0Best')} size={phone ? 'md' : 'sm'} styles={phone ? { input: { minHeight: 44 } } : undefined} min={0} max={51} value={crf} onChange={(v) => setCrf(typeof v === 'number' ? v : 18)} />
            </Stack>
          )}
        </Stack>
      </Collapse>
    </>
  );

  if (phone) {
    const renderRow = (
      <Group justify="space-between" align="center" wrap="wrap" gap="xs">
        <Stack gap={0} style={{ flex: '1 1 200px' }}>
          {blockedLines}
          {estimateLine && !needMoreLine ? (
            <Text size="xs" c="dimmed" data-testid="render-estimate">
              {estimateLine}
            </Text>
          ) : null}
        </Stack>
        {startButton}
      </Group>
    );
    return (
      <Stack gap="xs" data-testid="render-settings">
        <Group align="flex-start" gap="xs" wrap="wrap">
          <Select
            label={t('format')}
            // Phone: Format and Quality stack, each the full width, so neither label nor control is cut (F5).
            style={{ flex: '1 1 100%', minWidth: 0 }}
            size="md"
            styles={{ input: { minHeight: 44 } }}
            comboboxProps={{ zIndex: 400 }}
            data={presetData}
            value={preset}
            onChange={(v) => {
              if (!v) return;
              setPresetTouched(true);
              setPreset(v as TimelineRenderPreset);
            }}
            allowDeselect={false}
            error={presetOk ? undefined : t('yourPlanDoesNot')}
            aria-describedby={[clamped && 'render-preset-clamp', anyRefused && 'render-preset-cap'].filter(Boolean).join(' ') || undefined}
            data-testid="render-preset"
          />
          {qualityControl}
        </Group>
        {planNotes}
        {sizeAndAdvanced}
        {footerSlot ? createPortal(renderRow, footerSlot) : renderRow}
      </Stack>
    );
  }

  // Why Render is off, first reason first. Each one says what to do about it.
  const disabledReason = readOnly ? (
    <Text size="xs" role="status" data-testid="render-disabled-reason" data-reason="readOnly">
      {t('reasonReadOnly')}
    </Text>
  ) : timelineEmpty ? (
    <Text size="xs" role="status" data-testid="render-disabled-reason" data-reason="empty">
      {t('reasonEmpty')}
      {onOpenEdit ? (
        <>
          {' '}
          <Anchor component="button" type="button" size="xs" className={classes.touchTarget} onClick={onOpenEdit} aria-keyshortcuts={editChord.aria} data-testid="render-open-edit">
            {editChord.tip(t('goToEdit'))}
          </Anchor>
        </>
      ) : null}
    </Text>
  ) : !presetOk ? (
    <Text size="xs" c="red" role="alert" data-testid="render-disabled-reason" data-reason="plan">
      {t('yourPlanDoesNot')}{' '}
      <Anchor component={Link} href={`${BILLING_SETTINGS_PATH}#plans`} size="xs">
        {t('seePlans')}
      </Anchor>
    </Text>
  ) : !nameOk ? (
    <Text size="xs" c="red" role="alert" data-testid="render-disabled-reason" data-reason="fileName">
      {t('reasonFileName')}
    </Text>
  ) : blockedReason || needMoreLine ? (
    blockedLines
  ) : costRefused ? (
    <Text size="xs" c="red" role="alert" data-testid="render-disabled-reason" data-reason="cost">
      {t('reasonCostRefused')}
    </Text>
  ) : null;

  const rate = timelineSize?.rate ?? null;
  const picture = presetPicture(preset, timelineSize);
  const sizeText = preset === 'AudioOnly' ? null : picture ? t('summarySize', { width: picture.width, height: picture.height }) : null;
  const summarySize = sizeText && clamped && maxOutputHeight !== null ? t('summarySizeClamped', { size: sizeText, cap: maxOutputHeight }) : sizeText ?? (clamped && maxOutputHeight !== null ? t('summaryClampedOnly', { cap: maxOutputHeight }) : null);
  const marked = useRange && rangeAvailable;
  const summaryRange = marked
    ? rate
      ? t('summaryMarkedRange', { from: framesToTimecode(inOut.inFrame as number, rate), to: framesToTimecode(inOut.outFrame as number, rate) })
      : t('summaryMarkedRangeFrames', { from: inOut.inFrame as number, to: inOut.outFrame as number })
    : rate && programFrames !== null && programFrames > 0
      ? t('summaryWholeTimelineTo', { from: framesToTimecode(0, rate), to: framesToTimecode(programFrames, rate) })
      : t('summaryWholeTimeline');

  return (
    <Stack gap="sm" data-testid="render-settings">
      <EditorPanel title={t('presetsTitle')} description={t('presetsHint')} scroll={false} testId="render-presets-panel">
        <Stack gap="xs">
          <PresetList
            options={presetData}
            value={preset}
            onChange={(next) => {
              setPresetTouched(true);
              setPreset(next);
            }}
            availability={availability}
            maxOutputHeight={maxOutputHeight}
            timelineSize={timelineSize}
            focusRequest={focusPresetsRequest}
          />
          {planNotes}
          <MyPresets current={settings} onApply={applySettings} formatLabel={(value) => t(`formats.${value}`)} />
        </Stack>
      </EditorPanel>
      <EditorPanel title={t('optionsTitle')} description={t('optionsHint', { format: t(`formats.${preset}`) })} scroll={false} testId="render-options-panel">
        <Stack gap="xs">
          {qualityControl}
          {basics}
          {captionsSlot}
          {sizeAndAdvanced}
        </Stack>
      </EditorPanel>
      <div className={classes.costDock}>
        <EditorPanel title={t('beforeYouRender')} scroll={false} testId="render-cost-panel">
          <Stack gap="xs">
            <CostStatement summary={{ format: t(`formats.${preset}`), size: summarySize, range: summaryRange, captions: preset === 'AudioOnly' ? null : captionsSummary }} cost={cost} />
            {disabledReason}
            <Group justify="flex-end">{startButton}</Group>
          </Stack>
        </EditorPanel>
      </div>
    </Stack>
  );
}
