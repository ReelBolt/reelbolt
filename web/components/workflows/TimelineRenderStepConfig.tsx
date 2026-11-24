'use client';

import useSWR from 'swr';
import { useT } from '@/i18n';
import { msg, type Msg } from '@/i18n/msg';
import {
  Stack, Group, Select, TextInput, NumberInput, Switch, Text, SimpleGrid, Fieldset,
} from '@mantine/core';
import type {
  TimelineRenderStepConfig as TimelineRenderStepConfigValue,
  TimelineRenderPreset,
  TimelineRenderVideoCodec,
  TimelineRenderEncoderSpeed,
  TimelineRenderRateControl,
} from '@/lib/types/workflow';
import { editTimelinesKey, listEditTimelines, type EditTimelineSummary } from '@/lib/editing/sync/api';
import { useFd, AskAssistantButton, assistantPrompt, AdvancedSection } from './config-kit';

/**
 * Legacy English labels, kept only for `timelineRenderPresetLabel` (nodes/TimelineRenderNode.tsx still reads a string).
 * New code uses `timelineRenderPresetMsg` + the catalogue (wfSteps.timelineRender.preset.<value>).
 */
/** Mirrors `TimelineRenderPreset`. Plain words; the renderer's own table resolves each to dimensions and encoder flags. */
export const TIMELINE_RENDER_PRESET_OPTIONS: { value: TimelineRenderPreset; label: string }[] = [
  { value: 'Hd1080p16x9', label: 'HD 1080p (16:9)' },
  { value: 'Uhd4k16x9', label: '4K (16:9)' },
  { value: 'Vertical9x16', label: 'Vertical (9:16, phones)' },
  { value: 'Square1x1', label: 'Square (1:1)' },
  { value: 'Portrait4x5', label: 'Portrait (4:5)' },
  { value: 'Master', label: 'Master — the timeline\'s own size, highest quality' },
  { value: 'AudioOnly', label: 'Audio only (.m4a)' },
];

const PRESET_VALUES: TimelineRenderPreset[] = ['Hd1080p16x9', 'Uhd4k16x9', 'Vertical9x16', 'Square1x1', 'Portrait4x5', 'Master', 'AudioOnly'];

/** Mirrors `TimelineRenderVideoCodec`. */
const CODEC_VALUES: TimelineRenderVideoCodec[] = ['H264', 'H265'];

/** Mirrors `TimelineRenderEncoderSpeed`. */
const SPEED_VALUES: TimelineRenderEncoderSpeed[] = ['Fast', 'Medium', 'Slow'];

/** Mirrors `TimelineRenderRateControl`. */
const RATE_VALUES: TimelineRenderRateControl[] = ['Quality', 'Capped', 'Average'];

/** Builds the default config for a freshly-added TimelineRender step. Mirrors the C# record defaults exactly. */
export function createDefaultTimelineRenderStepConfig(): TimelineRenderStepConfigValue {
  return {
    editTimelineId: '',
    version: 1,
    revision: null,
    range: null,
    preset: 'Hd1080p16x9',
    encode: null,
    outputFileName: 'timeline-render.mp4',
    registerProjectFile: true,
    loudness: null,
  };
}

/** The preset's human label, for cards and chips. */
export function timelineRenderPresetLabel(preset: TimelineRenderPreset): string {
  return TIMELINE_RENDER_PRESET_OPTIONS.find((o) => o.value === preset)?.label ?? preset;
}

/** The preset's label as a catalogue message, for components that resolve it with `useT().msg`. */
export function timelineRenderPresetMsg(preset: TimelineRenderPreset): Msg {
  return msg(`wfSteps.timelineRender.preset.${preset}`);
}

interface TimelineRenderStepConfigProps {
  config: TimelineRenderStepConfigValue;
  onChange: (config: TimelineRenderStepConfigValue) => void;
  /** The project whose editor timelines the picker lists. Without it the id is typed by hand. */
  projectId?: string;
  currentStepIndex?: number;
}

/**
 * Configuration form for a TimelineRender step (deterministic, no model call): which editor
 * timeline to render, at which revision, to which delivery preset. Mirrors the backend
 * `TimelineRenderStepConfig` record field-for-field; every number the renderer accepts is clamped
 * server-side by `Validate()`, so this form only offers the allowed ranges.
 */
export function TimelineRenderStepConfig({
  config, onChange, projectId, currentStepIndex = 0,
}: TimelineRenderStepConfigProps) {
  const t = useT('wfSteps.timelineRender');
  const fd = useFd();
  const { data: timelines, error: timelinesError } = useSWR<EditTimelineSummary[]>(
    projectId ? editTimelinesKey(projectId) : null,
    () => listEditTimelines(projectId as string),
  );

  const patch = (updates: Partial<TimelineRenderStepConfigValue>) => onChange({ ...config, ...updates });

  const timelineOptions = (timelines ?? []).map((tl) => ({
    value: tl.id,
    label: t('timeline.option', { name: tl.name, revision: tl.revision }),
  }));
  // A config may name a timeline this project no longer lists (deleted, or another project's
  // through a copied workflow). Shown as its own option so the author sees what the step will try.
  const knownTimeline = timelineOptions.some((o) => o.value === config.editTimelineId);
  const timelineData = config.editTimelineId && !knownTimeline
    ? [...timelineOptions, { value: config.editTimelineId, label: t('timeline.notListed', { id: config.editTimelineId }) }]
    : timelineOptions;

  const encode = config.encode;
  const loudness = config.loudness;
  const range = config.range;
  const audioOnly = config.preset === 'AudioOnly';

  return (
    <Stack gap="md" onClick={(e) => e.stopPropagation()}>
      <Group justify="space-between" align="center">
        <Text size="xs" c="dimmed">
          {t('intro')}
        </Text>
        <AskAssistantButton prompt={assistantPrompt(currentStepIndex, 'TimelineRender', msg('wfSteps.prompts.timelineRender'))} />
      </Group>

      <Fieldset legend={t('what.legend')}>
        <Stack gap="xs">
          {projectId && !timelinesError ? (
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.editTimelineId')}
              label={t('timeline.label')}
              description={t('timeline.description')}
              size="xs"
              data={timelineData}
              value={config.editTimelineId || null}
              placeholder={timelines ? (timelineOptions.length === 0 ? t('timeline.none') : t('timeline.choose')) : t('timeline.loading')}
              onChange={(v) => patch({ editTimelineId: v ?? '' })}
              searchable
              nothingFoundMessage={t('timeline.noMatch')}
            />
          ) : (
            <TextInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.editTimelineId')}
              label={t('timeline.idLabel')}
              description={t('timeline.idDescription')}
              size="xs"
              value={config.editTimelineId}
              onChange={(e) => patch({ editTimelineId: e.currentTarget.value.trim() })}
            />
          )}
          {!config.editTimelineId && (
            <Text size="xs" c="orange">{t('timeline.required')}</Text>
          )}
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.revision')}
            label={t('revision.label')}
            description={t('revision.description')}
            size="xs"
            min={1}
            value={config.revision ?? ''}
            onChange={(v) => patch({ revision: typeof v === 'number' ? v : null })}
          />
        </Stack>
      </Fieldset>

      <Fieldset legend={t('delivery.legend')}>
        <Stack gap="xs">
          <Select
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.preset')}
            label={t('delivery.format')}
            size="xs"
            data={PRESET_VALUES.map((v) => ({ value: v, label: t(`preset.${v}`) }))}
            value={config.preset}
            onChange={(v) => v && patch({ preset: v as TimelineRenderPreset })}
            allowDeselect={false}
          />
          <TextInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.outputFileName')}
            label={t('delivery.fileName.label')}
            description={t('delivery.fileName.description')}
            size="xs"
            value={config.outputFileName}
            onChange={(e) => patch({ outputFileName: e.currentTarget.value })}
          />
          <Switch
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.registerProjectFile')}
            label={t('delivery.register')}
            size="xs"
            checked={config.registerProjectFile}
            onChange={(e) => patch({ registerProjectFile: e.currentTarget.checked })}
          />
        </Stack>
      </Fieldset>

      <AdvancedSection label={t('advanced.label')} description={t('advanced.description')}>
        <Stack gap="sm">
          <Fieldset legend={t('range.legend')}>
            <Stack gap="xs">
              <Switch
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.range')}
                label={t('range.only')}
                size="xs"
                checked={range !== null}
                onChange={(e) => patch({ range: e.currentTarget.checked ? { inFrame: 0, outFrame: 1 } : null })}
              />
              {range && (
                <SimpleGrid cols={2}>
                  <NumberInput
                    // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                    {...fd('config.range.inFrame')}
                    label={t('range.first')} size="xs" min={0} value={range.inFrame}
                    onChange={(v) => patch({ range: { ...range, inFrame: typeof v === 'number' ? v : 0 } })}
                  />
                  <NumberInput
                    // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                    {...fd('config.range.outFrame')}
                    label={t('range.end')} size="xs" min={1} value={range.outFrame}
                    onChange={(v) => patch({ range: { ...range, outFrame: typeof v === 'number' ? v : 1 } })}
                  />
                </SimpleGrid>
              )}
            </Stack>
          </Fieldset>

          {!audioOnly && (
            <Fieldset legend={t('encoding.legend')}>
              <Stack gap="xs">
                <Switch
                  // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                  {...fd('config.encode')}
                  label={t('encoding.override')}
                  size="xs"
                  checked={encode !== null}
                  onChange={(e) => patch({
                    encode: e.currentTarget.checked
                      ? { width: null, height: null, codec: 'H264', crf: 18, speed: 'Medium', audioBitrateKbps: 192 }
                      : null,
                  })}
                />
                {encode && (
                  <>
                    <SimpleGrid cols={2}>
                      <Select
                        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                        {...fd('config.encode.codec')}
                        label={t('encoding.codec')} size="xs" data={CODEC_VALUES.map((v) => ({ value: v, label: t(`codec.${v}`) }))} value={encode.codec} allowDeselect={false}
                        onChange={(v) => v && patch({ encode: { ...encode, codec: v as TimelineRenderVideoCodec } })}
                      />
                      <Select
                        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                        {...fd('config.encode.speed')}
                        label={t('encoding.speed')} size="xs" data={SPEED_VALUES.map((v) => ({ value: v, label: t(`speed.${v}`) }))} value={encode.speed} allowDeselect={false}
                        onChange={(v) => v && patch({ encode: { ...encode, speed: v as TimelineRenderEncoderSpeed } })}
                      />
                      <NumberInput
                        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                        {...fd('config.encode.crf')}
                        label={t('encoding.quality')} size="xs" min={0} max={51} value={encode.crf}
                        onChange={(v) => patch({ encode: { ...encode, crf: typeof v === 'number' ? v : 18 } })}
                      />
                      <NumberInput
                        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                        {...fd('config.encode.audioBitrateKbps')}
                        label={t('encoding.audioBitrate')} size="xs" min={64} max={320} value={encode.audioBitrateKbps}
                        onChange={(v) => patch({ encode: { ...encode, audioBitrateKbps: typeof v === 'number' ? v : 192 } })}
                      />
                    </SimpleGrid>
                    <SimpleGrid cols={2}>
                      <Select
                        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                        {...fd('config.encode.rateControl')}
                        label={t('encoding.rateControl')} size="xs" data={RATE_VALUES.map((v) => ({ value: v, label: t(`encoding.rateMode.${v}`) }))} value={encode.rateControl ?? 'Quality'} allowDeselect={false}
                        onChange={(v) => {
                          if (!v) return;
                          const { rateControl: _rc, videoBitrateKbps: _kb, ...rest } = encode;
                          patch({ encode: v === 'Quality' ? rest : { ...rest, rateControl: v as TimelineRenderRateControl, videoBitrateKbps: encode.videoBitrateKbps ?? 8000 } });
                        }}
                      />
                      {encode.rateControl && encode.rateControl !== 'Quality' && (
                        <NumberInput
                          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                          {...fd('config.encode.videoBitrateKbps')}
                          label={t('encoding.videoBitrate')} size="xs" min={200} max={100000} step={500} value={encode.videoBitrateKbps ?? 8000}
                          onChange={(v) => patch({ encode: { ...encode, videoBitrateKbps: typeof v === 'number' ? Math.min(100000, Math.max(200, Math.round(v))) : 8000 } })}
                        />
                      )}
                    </SimpleGrid>
                    <Switch
                      label={t('encoding.customSize')}
                      size="xs"
                      checked={encode.width !== null}
                      onChange={(e) => patch({
                        encode: e.currentTarget.checked
                          ? { ...encode, width: 1920, height: 1080 }
                          : { ...encode, width: null, height: null },
                      })}
                    />
                    {encode.width !== null && (
                      <SimpleGrid cols={2}>
                        <NumberInput
                          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                          {...fd('config.encode.width')}
                          label={t('encoding.width')} size="xs" min={16} max={7680} step={2} value={encode.width}
                          onChange={(v) => patch({ encode: { ...encode, width: typeof v === 'number' ? v : 1920 } })}
                        />
                        <NumberInput
                          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                          {...fd('config.encode.height')}
                          label={t('encoding.height')} size="xs" min={16} max={4320} step={2} value={encode.height ?? 1080}
                          onChange={(v) => patch({ encode: { ...encode, height: typeof v === 'number' ? v : 1080 } })}
                        />
                      </SimpleGrid>
                    )}
                  </>
                )}
              </Stack>
            </Fieldset>
          )}

          <Fieldset legend={t('loudness.legend')}>
            <Stack gap="xs">
              <Switch
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.loudness')}
                label={t('loudness.normalise.label')}
                description={t('loudness.normalise.description')}
                size="xs"
                checked={loudness !== null}
                onChange={(e) => patch({
                  loudness: e.currentTarget.checked ? { targetLufs: -16, truePeakDbtp: -1.5, loudnessRange: 11 } : null,
                })}
              />
              {loudness && (
                <SimpleGrid cols={3}>
                  <NumberInput
                    // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                    {...fd('config.loudness.targetLufs')}
                    label={t('loudness.target')} size="xs" min={-36} max={-5} value={loudness.targetLufs}
                    onChange={(v) => patch({ loudness: { ...loudness, targetLufs: typeof v === 'number' ? v : -16 } })}
                  />
                  <NumberInput
                    // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                    {...fd('config.loudness.truePeakDbtp')}
                    label={t('loudness.truePeak')} size="xs" min={-9} max={0} step={0.5} value={loudness.truePeakDbtp}
                    onChange={(v) => patch({ loudness: { ...loudness, truePeakDbtp: typeof v === 'number' ? v : -1.5 } })}
                  />
                  <NumberInput
                    // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                    {...fd('config.loudness.loudnessRange')}
                    label={t('loudness.range')} size="xs" min={1} max={30} value={loudness.loudnessRange}
                    onChange={(v) => patch({ loudness: { ...loudness, loudnessRange: typeof v === 'number' ? v : 11 } })}
                  />
                </SimpleGrid>
              )}
            </Stack>
          </Fieldset>
        </Stack>
      </AdvancedSection>
    </Stack>
  );
}
