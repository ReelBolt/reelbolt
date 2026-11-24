'use client';

import { useRef, useState } from 'react';
import { useT, msg } from '@/i18n';
import {
  Stack, Select, SegmentedControl, TextInput, NumberInput, Switch, Button, Text, Code, Group, Paper, ActionIcon, SimpleGrid, Fieldset,
} from '@mantine/core';
import { EgressRunNote } from './EgressRunNote';
import type { NumberInputProps } from '@mantine/core';
import { IconPlus, IconTrash } from '@tabler/icons-react';
import {
  OBJECT_MATCH_SCOPES,
  OBJECT_TRACK_METHODS,
  type ObjectMatchScope,
  type ObjectTrackMethod,
  type ObjectTrackTarget,
  type VideoDerushPrePassMode,
  type VideoAnalyzeStepConfig as VideoAnalyzeStepConfigValue,
  type VideoSourceRef,
  type VideoSourceKind,
  type VideoTranscriptionMode,
  type VideoVisualDetail,
  type VideoVisionMode,
  type VideoCaptionSelection,
  type VideoSubShotMode,
} from '@/lib/types/workflow';
import { useProjectFiles } from '@/lib/hooks/use-files';
import { MediaPickerField } from '@/components/shared/media/MediaPickerField';
import type { StepData } from './WorkflowStepList';
import {
  useFd, useFdRaw, ProviderSelect, AskAssistantButton, EgressWarning, CostWarning, ModeHint, assistantPrompt, AdvancedSection, NoAudioFilesNotice,
} from './config-kit';

/** One gigabyte as the platform counts it; the form shows whole-ish GB, the config keeps bytes. */
const GIB = 1024 * 1024 * 1024;

/** Builds a sensible default config for a freshly-added VideoAnalyze step. Mirrors the C# record defaults exactly. */
export function createDefaultVideoAnalyzeStepConfig(): VideoAnalyzeStepConfigValue {
  return {
    version: 1,
    source: { kind: 'PreviousStepOutput', projectFileId: null, stepOrder: null },
    detectSilence: true,
    silenceThresholdDb: -34.0,
    minSilenceMs: 350,
    detectShots: true,
    sceneThreshold: 0.3,
    transcription: 'Optional',
    transcriptionProviderId: null,
    language: null,
    wordTimestamps: true,
    // Mirrors the C# defaults: 5 MB chunks; 0 = no limit of its own, the platform limit applies.
    maxAsrChunkBytes: 5_000_000,
    maxDurationSeconds: 0,
    maxInputBytes: 0,
    maxOutputChars: 24_000,
    maxViewSegments: 400,
    maxSegmentTextChars: 160,
    analyzeVisuals: true,
    visualSampleFps: 2.0,
    visualGridWidth: 32,
    visualGridHeight: 18,
    maxVisualSampleFrames: 4000,
    stillMotionThreshold: 0.02,
    minStillWindowMs: 400,
    maxStillWindowsPerShot: 3,
    detectLetterbox: true,
    detectSharpness: false,
    analyzeAudioLevels: true,
    detectNearDuplicates: true,
    duplicateSimilarityThreshold: 0.90,
    duplicateWindowShots: 20,
    visualDetail: 'Compact',
    maxViewDuplicateGroups: 20,
    analyzeColorGrading: true,
    detectLookGroups: true,
    lookSimilarityThreshold: 0.88,
    maxViewLookGroups: 12,
    keyframesPerShot: 1,
    maxSharpnessShots: 24,
    expect: null,
  };
}

/** Plain-language labels for the sub-shot cut mode; the engine member names stay internal. */
// Labels: wfSteps.videoAnalyze.subShotMode.<value>
const SUB_SHOT_MODE_VALUES: VideoSubShotMode[] = ['Off', 'Even', 'Content'];

function clampTo(value: number, lo: number, hi: number): number {
  return Math.min(hi, Math.max(lo, value));
}

// Labels: wfSteps.videoAnalyze.sourceKind.<value>
const SOURCE_KIND_VALUES: VideoSourceKind[] = ['ProjectFile', 'PreviousStepOutput', 'StepOutput', 'StepResult'];

/**
 * What a new clip row starts as: an uploaded project file. "Previous step output" used to be the
 * default, which is meaningless for the first step and hid the file picker on every row.
 */
export const NEW_CLIP_SOURCE_REF: VideoSourceRef = { kind: 'ProjectFile', projectFileId: null, stepOrder: null };

/** Fixed allowlist — the plate colour word reaches the tracker's own config, never free text. */
// Labels: wfSteps.videoAnalyze.insertRegionColor.<value>
const INSERT_REGION_COLOR_VALUES = ['green', 'blue', 'magenta'];

/**
 * The object prompt is user text. It is capped here to the same 120 characters the backend keeps,
 * sanitized on the server before it is sent to a provider, and never used to build a command line.
 */
const TRACK_PROMPT_MAX_CHARS = 120;

/** The method choices come from the shared member list so the Select can never offer a stray value. Labels: wfSteps.videoAnalyze.trackMethod.<value>. */
const TRACK_METHOD_VALUES: readonly ObjectTrackMethod[] = OBJECT_TRACK_METHODS;

/** The match-scope choices, built the same way. Labels: wfSteps.videoAnalyze.matchScope.<value>. */
const MATCH_SCOPE_VALUES: readonly ObjectMatchScope[] = OBJECT_MATCH_SCOPES;

interface VideoSourceRefPickerProps {
  value: VideoSourceRef;
  onChange: (ref: VideoSourceRef) => void;
  priorStepOptions: { value: string; label: string }[];
  projectId?: string;
}

/** Picks which video a VideoAnalyze step operates on — an uploaded file, a specific step, or the previous step. */
function VideoSourceRefPicker({ value, onChange, priorStepOptions, projectId }: VideoSourceRefPickerProps) {
  const t = useT('wfSteps.videoAnalyze');
  // With no earlier step there is nothing to take a video FROM: only an uploaded file makes sense.
  const hasPriorSteps = priorStepOptions.length > 0;
  // "Output of an earlier run" names a step result of an earlier run, so it is offered even for a first step.
  const kind: VideoSourceKind = hasPriorSteps || value.kind === 'StepResult' ? value.kind : 'ProjectFile';
  const kindOptions = SOURCE_KIND_VALUES.filter(
    (v) => hasPriorSteps || v === 'ProjectFile' || v === 'StepResult',
  ).map((v) => ({ value: v, label: t(`sourceKind.${v}`) }));

  return (
    <Stack gap="xs" onClick={(e) => e.stopPropagation()}>
      {(hasPriorSteps || kind === 'StepResult') && (
        <div>
          <Text size="xs" fw={500} mb={4}>{t('videoSourceRefPicker.sourceVideo.title')}</Text>
          <SegmentedControl
            aria-label={t('videoSourceRefPicker.sourceVideo.aria')}
            fullWidth
            size="xs"
            data={kindOptions.map((o) => ({ value: o.value, label: o.label }))}
            value={kind}
            onChange={(v) => onChange({ kind: v as VideoSourceKind, projectFileId: null, stepOrder: null })}
          />
        </div>
      )}

      {kind === 'StepOutput' && (
        <Select
          label={t('videoSourceRefPicker.step.label')}
          size="xs"
          data={priorStepOptions}
          value={value.stepOrder != null ? String(value.stepOrder) : null}
          onChange={(v) => onChange({ ...value, stepOrder: v ? Number(v) : null })}
          placeholder={t('videoSourceRefPicker.step.placeholder')}
        />
      )}

      {kind === 'StepResult' && (
        <TextInput
          label={t('videoSourceRefPicker.stepResultId.label')}
          description={t('videoSourceRefPicker.stepResultId.description')}
          size="xs"
          value={value.stepResultId ?? ''}
          onChange={(e) => onChange({ ...value, kind: 'StepResult', projectFileId: null, stepOrder: null, stepResultId: e.target.value.trim() || null })}
        />
      )}

      {kind === 'ProjectFile' && (
        <MediaPickerField
          projectId={projectId}
          kind="video"
          label={t('videoSourceRefPicker.videoFile.label')}
          value={value.kind === 'ProjectFile' && value.projectFileId ? [value.projectFileId] : []}
          onChange={(picked) => onChange({ kind: 'ProjectFile', projectFileId: picked[0]?.id ?? null, stepOrder: null })}
        />
      )}

      <SimpleGrid cols={2}>
        <NumberInput
          label={t('videoSourceRefPicker.useFromSeconds.label')}
          description={t('videoSourceRefPicker.useFromSeconds.description')}
          size="xs"
          min={0}
          decimalScale={2}
          value={value.inSec ?? ''}
          onChange={(v) => onChange({ ...value, inSec: typeof v === 'number' ? Math.max(0, v) : null })}
        />
        <NumberInput
          label={t('videoSourceRefPicker.useUntilSeconds.label')}
          description={t('videoSourceRefPicker.useUntilSeconds.description')}
          size="xs"
          min={0}
          decimalScale={2}
          value={value.outSec ?? ''}
          onChange={(v) => onChange({ ...value, outSec: typeof v === 'number' ? Math.max(0, v) : null })}
          error={value.inSec != null && value.outSec != null && value.outSec <= value.inSec ? t('videoSourceRefPicker.mustBeAfterStart') : undefined}
        />
      </SimpleGrid>
    </Stack>
  );
}

interface VideoMultiSourceRefPickerProps {
  value: VideoSourceRef[];
  onChange: (refs: VideoSourceRef[]) => void;
  priorStepOptions: { value: string; label: string }[];
  projectId?: string;
}

/**
 * Applies a "Choose clips…" multi-selection to the clip list: rows of files still chosen keep
 * their place (so a hand-made order survives), newly chosen files are appended in the order they
 * were picked, and empty file rows are dropped. Rows that take their video from a step are kept.
 */
export function applyChosenClips(sources: VideoSourceRef[], chosenFileIds: string[]): VideoSourceRef[] {
  const chosen = new Set(chosenFileIds);
  const kept = sources.filter((s) =>
    s.kind !== 'ProjectFile' ? true : s.projectFileId != null && chosen.has(s.projectFileId),
  );
  const present = new Set(kept.filter((s) => s.kind === 'ProjectFile').map((s) => s.projectFileId));
  const added = chosenFileIds
    .filter((id) => !present.has(id))
    .map((id): VideoSourceRef => ({ kind: 'ProjectFile', projectFileId: id, stepOrder: null }));
  const next = [...kept, ...added];
  return next.length > 0 ? next : [NEW_CLIP_SOURCE_REF];
}

/**
 * The plural `sources` field: a "Choose clips…" multi-select of the project's videos (pick eight
 * clips in one go rather than eight rows of three clicks each), plus one row per clip in cut order
 * for fine control. A one-element list is functionally identical to the singular `source` field.
 */
function VideoMultiSourceRefPicker({ value, onChange, priorStepOptions, projectId }: VideoMultiSourceRefPickerProps) {
  const t = useT('wfSteps.videoAnalyze');
  const sources = value.length > 0 ? value : [NEW_CLIP_SOURCE_REF];
  const chosenIds = sources
    .filter((s) => s.kind === 'ProjectFile' && s.projectFileId)
    .map((s) => s.projectFileId as string);

  const updateAt = (index: number, ref: VideoSourceRef) => {
    onChange(sources.map((s, i) => (i === index ? ref : s)));
  };
  const removeAt = (index: number) => {
    onChange(sources.filter((_, i) => i !== index));
  };
  const add = () => onChange([...sources, NEW_CLIP_SOURCE_REF]);

  return (
    <Stack gap="xs" onClick={(e) => e.stopPropagation()}>
      <MediaPickerField
        projectId={projectId}
        kind="video"
        multiple
        label={t('videoMultiSourceRefPicker.chooseClips.label')}
        description={t('videoMultiSourceRefPicker.chooseClips.description')}
        value={chosenIds}
        onChange={(picked) => onChange(applyChosenClips(sources, picked.map((f) => f.id)))}
      />
      <Text size="xs" fw={500}>{t('videoMultiSourceRefPicker.clipsTitle', { count: sources.length })}</Text>
      {sources.map((ref, index) => (
        <Paper key={index} withBorder p="xs" radius="md">
          <Group justify="space-between" align="flex-start" wrap="nowrap" mb={4}>
            <Text size="xs" c="dimmed" fw={600}>{t('videoMultiSourceRefPicker.clipN', { n: index + 1 })}</Text>
            <ActionIcon
              size="xs"
              color="red"
              variant="subtle"
              disabled={sources.length <= 1}
              onClick={() => removeAt(index)}
              aria-label={t('videoMultiSourceRefPicker.removeClip', { n: index + 1 })}
            >
              <IconTrash size={14} />
            </ActionIcon>
          </Group>
          <VideoSourceRefPicker
            value={ref}
            onChange={(next) => updateAt(index, next)}
            priorStepOptions={priorStepOptions}
            projectId={projectId}
          />
        </Paper>
      ))}
      <Button
        size="xs"
        variant="light"
        leftSection={<IconPlus size={14} />}
        onClick={add}
        style={{ alignSelf: 'flex-start' }}
      >
        {t('videoMultiSourceRefPicker.addClip')}
      </Button>
    </Stack>
  );
}

interface VideoAnalyzeStepConfigProps {
  config: VideoAnalyzeStepConfigValue;
  onChange: (config: VideoAnalyzeStepConfigValue) => void;
  allSteps?: StepData[];
  currentStepIndex?: number;
  projectId?: string;
}

/**
 * Configuration form for a VideoAnalyze step ("Watch footage"). Mirrors the backend
 * `VideoAnalyzeStepConfig` record field-for-field. The everyday choices (which clips, transcribe
 * speech, describe shots, what to prepare for later steps) come first, in plain words; thresholds,
 * budgets, the decision-model options and the output format sit behind "Advanced".
 */
export function VideoAnalyzeStepConfig({
  config, onChange, allSteps = [], currentStepIndex = 0, projectId,
}: VideoAnalyzeStepConfigProps) {
  const t = useT('wfSteps.videoAnalyze');
  const fd = useFd();
  // The tracked-object list is an array of rows rather than one input, so its proposed change is
  // highlighted on the container (the same treatment SegmentedControl and plain Boxes get).
  const trackTargetsFd = useFdRaw()('config.trackObjects');
  const priorStepOptions = allSteps
    .slice(0, currentStepIndex)
    .map((previousStep, index) => {
      const stepOrder = index + 1;
      const label = previousStep.label?.trim() || `Step ${stepOrder}`;
      return { value: String(stepOrder), label: `${stepOrder}. ${label}` };
    });

  const patch = (updates: Partial<VideoAnalyzeStepConfigValue>) => onChange({ ...config, ...updates });
  const { data: projectFiles } = useProjectFiles(projectId ?? '');
  const audioFiles = (projectFiles ?? []).filter((f) => f.mimeType?.startsWith('audio/'));

  // Multi-source mode is "on" whenever the step is currently saved with the plural `sources`
  // field (even a one-element list) rather than the singular `source` — mirrors the backend's
  // own two-representation record exactly, so toggling never silently drops the other field.
  const isMultiSource = Boolean(config.sources && config.sources.length > 0);

  const toggleMultiSource = (multi: boolean) => {
    if (multi) {
      // Carry a real choice over; a placeholder "previous step" source becomes an empty file row.
      const carried = config.source && (config.source.projectFileId || config.source.stepOrder || priorStepOptions.length > 0)
        ? config.source
        : NEW_CLIP_SOURCE_REF;
      patch({ source: undefined, sources: config.sources?.length ? config.sources : [carried] });
    } else {
      patch({ source: config.sources?.[0] ?? NEW_CLIP_SOURCE_REF, sources: null });
    }
  };

  const vision = config.vision ?? 'Off';
  const prePass = config.derushPrePass ?? 'Off';
  const subShotMode: VideoSubShotMode = config.subShotMode ?? 'Off';
  // The engine's default minimum is 1.2 s; the longest part may not be less than twice the shortest.
  const minSubShotSec = clampTo(config.minSubShotSec ?? 1.2, 0.5, 10);

  // -- Tracked objects (prompted region effects). A target with a `region` was marked by hand or by
  //    the assistant and is carried through untouched; these rows own only the labelled targets, so
  //    editing one never rewrites a quad. --
  const trackTargets = config.trackObjects ?? [];
  const markedTargets = trackTargets.filter((target) => target.region);
  const namedTargets = trackTargets.filter((target) => !target.region);
  // Absent method means Feature — the C# record's own default — so an untouched target keeps its meaning.
  const segmentationOn = namedTargets.some((target) => (target.method ?? 'Feature') === 'Segmentation');
  const egressAllowed = config.allowSourceMediaEgress ?? false;

  // Where each rendered row sits in `trackObjects`, so a row edits ITS entry and the order of the
  // hand-marked areas is never disturbed by a labelled one.
  const namedIndexes = trackTargets.reduce<number[]>((acc, target, i) => (target.region ? acc : [...acc, i]), []);
  const updateTarget = (row: number, changes: Partial<ObjectTrackTarget>) => {
    const at = namedIndexes[row];
    patch({ trackObjects: trackTargets.map((target, i) => (i === at ? { ...target, ...changes } : target)) });
  };
  const removeTarget = (row: number) => {
    const at = namedIndexes[row];
    patch({ trackObjects: trackTargets.filter((_, i) => i !== at) });
  };
  const addTarget = () => patch({ trackObjects: [...trackTargets, { label: '' }] });

  return (
    <Stack gap="md" onClick={(e) => e.stopPropagation()}>
      <Group justify="space-between" align="center">
        <Text size="xs" c="dimmed">
          {t('intro')}
        </Text>
        <AskAssistantButton prompt={assistantPrompt(currentStepIndex, 'VideoAnalyze', msg('wfSteps.prompts.videoAnalyze'))} />
      </Group>

      <Fieldset legend={t('yourFootage.legend')}>
        <Stack gap="xs">
          <Switch
            label={t('useSeveralClips.label')}
            description={t('useSeveralClips.description')}
            checked={isMultiSource}
            onChange={(e) => toggleMultiSource(e.currentTarget.checked)}
          />
          {isMultiSource ? (
            <VideoMultiSourceRefPicker
              value={config.sources ?? []}
              onChange={(sources) => patch({ sources })}
              priorStepOptions={priorStepOptions}
              projectId={projectId}
            />
          ) : (
            <VideoSourceRefPicker
              value={config.source ?? NEW_CLIP_SOURCE_REF}
              onChange={(source) => patch({ source })}
              priorStepOptions={priorStepOptions}
              projectId={projectId}
            />
          )}
        </Stack>
      </Fieldset>

      <Fieldset legend={t('whatToLook.legend')}>
        <SimpleGrid cols={{ base: 1, sm: 2 }}>
          <Switch
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.detectSilence')}
            label={t('detectSilence.label')}
            description={t('detectSilence.description')}
            checked={config.detectSilence}
            onChange={(e) => patch({ detectSilence: e.currentTarget.checked })}
          />
          <Switch
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.detectShots')}
            label={t('detectShots.label')}
            description={t('detectShots.description')}
            checked={config.detectShots}
            onChange={(e) => patch({ detectShots: e.currentTarget.checked })}
          />
          <Switch
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.analyzeVisuals')}
            label={t('analyzeVisuals.label')}
            description={t('analyzeVisuals.description')}
            checked={config.analyzeVisuals}
            onChange={(e) => patch({ analyzeVisuals: e.currentTarget.checked })}
          />
          <Switch
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.analyzeAudioLevels')}
            label={t('analyzeAudioLevels.label')}
            description={t('analyzeAudioLevels.description')}
            checked={config.analyzeAudioLevels}
            onChange={(e) => patch({ analyzeAudioLevels: e.currentTarget.checked })}
          />
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.maxShotSec')}
            label={t('maxShotSec.label')}
            description={t('maxShotSec.description')}
            size="xs"
            min={0}
            max={600}
            value={config.maxShotSec ?? 0}
            onChange={(v) => patch({ maxShotSec: typeof v === 'number' ? Math.max(0, v) : 0 })}
          />
          <Select
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.subShotMode')}
            label={t('subShotMode.label')}
            description={t('subShotMode.description')}
            size="xs"
            allowDeselect={false}
            data={SUB_SHOT_MODE_VALUES.map((v) => ({ value: v, label: t(`subShotMode.${v}`) }))}
            value={subShotMode}
            onChange={(v) => v && patch({ subShotMode: v as VideoSubShotMode })}
          />
          {subShotMode !== 'Off' && (
            <>
              <TypingSafeNumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.minSubShotSec')}
                label={t('minSubShotSec.label')}
                description={t('minSubShotSec.description')}
                size="xs"
                min={0.5}
                max={10}
                clampBehavior="blur"
                placeholder="1.2"
                value={config.minSubShotSec}
                onValue={(v) => patch({ minSubShotSec: v })}
              />
              <TypingSafeNumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.maxSubShotSec')}
                label={t('maxSubShotSec.label')}
                description={t('maxSubShotSec.description')}
                size="xs"
                min={2 * minSubShotSec}
                max={60}
                clampBehavior="blur"
                placeholder="4"
                value={config.maxSubShotSec}
                onValue={(v) => patch({ maxSubShotSec: v })}
              />
              <TypingSafeNumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.maxSubShotsPerShot')}
                label={t('maxSubShotsPerShot.label')}
                description={t('maxSubShotsPerShot.description')}
                size="xs"
                min={2}
                max={64}
                allowDecimal={false}
                clampBehavior="blur"
                placeholder="8"
                value={config.maxSubShotsPerShot}
                onValue={(v) => patch({ maxSubShotsPerShot: v })}
              />
            </>
          )}
        </SimpleGrid>
      </Fieldset>

      <Fieldset legend={<Group gap="xs" wrap="nowrap"><span>{t('speech.legend')}</span><AskAssistantButton label={t('ask.label')} prompt={assistantPrompt(currentStepIndex, 'VideoAnalyze', msg('wfSteps.prompts.videoAnalyzeSpeech'))} /></Group>}>
        <Stack gap="xs">
          <Select
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.transcription')}
            label={t('transcription.label')}
            size="xs"
            value={config.transcription}
            data={[
              { value: 'Off', label: t('transcriptionMode.Off') },
              { value: 'Optional', label: t('transcriptionMode.Optional') },
              { value: 'Required', label: t('transcriptionMode.Required') },
            ]}
            onChange={(v) => v && patch({ transcription: v as VideoTranscriptionMode })}
          />
          {config.transcription !== 'Off' && (
            <>
              <ProviderSelect
                capability="Transcription"
                label={t('speechToText.label')}
                description={t('speechToText.description')}
                value={config.transcriptionProviderId ?? null}
                onChange={(v) => patch({ transcriptionProviderId: v })}
                nullMeans={t('speechToText.nullMeans')}
                wrapperProps={fd('config.transcriptionProviderId').wrapperProps}
              />
              <TextInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.language')}
                label={t('language.label')}
                description={t('language.description')}
                placeholder={t('language.placeholder')}
                size="xs"
                value={config.language ?? ''}
                onChange={(e) => patch({ language: e.target.value || null })}
              />
            </>
          )}
        </Stack>
      </Fieldset>

      <Fieldset legend={<Group gap="xs" wrap="nowrap"><span>{t('describeShots.legend')}</span><AskAssistantButton label={t('ask.label')} prompt={assistantPrompt(currentStepIndex, 'VideoAnalyze', msg('wfSteps.prompts.videoAnalyzeShots'))} /></Group>}>
        <Stack gap="xs">
          <Text size="xs" c="dimmed">
            {t('describeShots.intro')}
          </Text>
          <Select
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.vision')}
            label={t('vision.label')}
            size="xs"
            value={vision}
            data={[
              { value: 'Off', label: t('visionMode.Off') },
              { value: 'Optional', label: t('visionMode.Optional') },
              { value: 'Required', label: t('visionMode.Required') },
            ]}
            onChange={(v) => v && patch({ vision: v as VideoVisionMode })}
          />
          {vision !== 'Off' && (
            <>
              <ProviderSelect
                capability="Vision"
                label={t('imageAi.label')}
                description={t('imageAi.description')}
                value={config.visionProviderId ?? null}
                onChange={(v) => patch({ visionProviderId: v })}
                nullMeans={t('imageAi.nullMeans')}
                wrapperProps={fd('config.visionProviderId').wrapperProps}
              />
              <SimpleGrid cols={{ base: 1, sm: 2 }}>
                <Select
                  // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                  {...fd('config.captionSelection')}
                  label={t('captionSelection.label')}
                  size="xs"
                  value={config.captionSelection ?? 'PerDuplicateGroup'}
                  data={[
                    { value: 'PerDuplicateGroup', label: t('captionSelectionMode.PerDuplicateGroup') },
                    { value: 'LongestShots', label: t('captionSelectionMode.LongestShots') },
                    { value: 'EvenlySpaced', label: t('captionSelectionMode.EvenlySpaced') },
                  ]}
                  onChange={(v) => v && patch({ captionSelection: v as VideoCaptionSelection })}
                />
                <NumberInput
                  // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                  {...fd('config.maxCaptionedShots')}
                  label={t('maxCaptionedShots.label')}
                  description={t('maxCaptionedShots.description')}
                  size="xs"
                  min={1}
                  value={config.maxCaptionedShots ?? 50}
                  onChange={(v) => patch({ maxCaptionedShots: typeof v === 'number' ? v : 50 })}
                />
              </SimpleGrid>
            </>
          )}
          <Switch
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.locateSubjects')}
            mt="xs"
            label={t('locateSubjects.label')}
            description={t('locateSubjects.description')}
            checked={config.locateSubjects ?? false}
            onChange={(e) => patch({ locateSubjects: e.currentTarget.checked })}
          />
          {config.locateSubjects && (
            <SimpleGrid cols={{ base: 1, sm: 2 }}>
              <TextInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.subjectLabel')}
                label={t('subjectLabel.label')}
                description={t('subjectLabel.description')}
                placeholder={t('subjectLabel.placeholder')}
                size="xs"
                value={config.subjectLabel ?? ''}
                onChange={(e) => patch({ subjectLabel: e.currentTarget.value.trim() ? e.currentTarget.value : null })}
              />
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.maxSubjectShots')}
                label={t('maxSubjectShots.label')}
                description={t('maxSubjectShots.description')}
                size="xs"
                min={0}
                value={config.maxSubjectShots ?? 40}
                onChange={(v) => patch({ maxSubjectShots: typeof v === 'number' ? v : 40 })}
              />
            </SimpleGrid>
          )}
        </Stack>
      </Fieldset>

      <Fieldset legend={t('prepareMaterialFor.legend')}>
        <Stack gap="xs">
          <Text size="xs" c="dimmed">{t('prepareHint')}</Text>
          <SimpleGrid cols={{ base: 1, sm: 2 }}>
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.emitOverlayPlacements')}
              label={t('emitOverlayPlacements.label')}
              description={t('emitOverlayPlacements.description')}
              checked={config.emitOverlayPlacements ?? false}
              onChange={(e) => patch({ emitOverlayPlacements: e.currentTarget.checked })}
            />
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.offerMusicTracks')}
              label={t('offerMusicTracks.label')}
              description={t('offerMusicTracks.description')}
              checked={config.offerMusicTracks ?? false}
              onChange={(e) => patch({ offerMusicTracks: e.currentTarget.checked })}
            />
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.offerSfxClips')}
              label={t('offerSfxClips.label')}
              description={t('offerSfxClips.description')}
              checked={config.offerSfxClips ?? false}
              onChange={(e) => patch({ offerSfxClips: e.currentTarget.checked })}
            />
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.detectInsertRegions')}
              label={t('detectInsertRegions.label')}
              description={t('detectInsertRegions.description')}
              checked={config.detectInsertRegions ?? false}
              onChange={(e) => patch({ detectInsertRegions: e.currentTarget.checked })}
            />
          </SimpleGrid>
          <Switch
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.analyzeMusicBeats')}
            label={t('analyzeMusicBeats.label')}
            description={t('analyzeMusicBeats.description')}
            checked={config.analyzeMusicBeats ?? false}
            onChange={(e) => patch({ analyzeMusicBeats: e.currentTarget.checked })}
          />
          {config.analyzeMusicBeats && (
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.musicBeatTrackProjectFileId')}
              label={t('musicBeatTrackProjectFileId.label')}
              description={t('musicBeatTrackProjectFileId.description')}
              size="xs"
              data={audioFiles.map((f) => ({ value: f.id, label: f.originalFileName }))}
              value={config.musicBeatTrackProjectFileId ?? null}
              onChange={(v) => patch({ musicBeatTrackProjectFileId: v })}
              placeholder={audioFiles.length > 0 ? t('musicBeatTrackProjectFileId.onlyOffered') : t('musicBeatTrackProjectFileId.empty')}
              clearable
              searchable
            />
          )}
          {(config.offerMusicTracks || config.offerSfxClips) && (
            <NoAudioFilesNotice
              projectId={projectId}
              kind={config.offerMusicTracks && config.offerSfxClips ? 'both' : config.offerMusicTracks ? 'music' : 'sfx'}
            />
          )}
          {config.detectInsertRegions && (
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.insertRegionColor')}
              label={t('insertRegionColor.label')}
              size="xs"
              value={config.insertRegionColor ?? 'green'}
              data={INSERT_REGION_COLOR_VALUES.map((v) => ({ value: v, label: t(`insertRegionColor.${v}`) }))}
              onChange={(v) => v && patch({ insertRegionColor: v })}
            />
          )}
        </Stack>
      </Fieldset>

      <Fieldset legend={<Group gap="xs" wrap="nowrap"><span>{t('trackObjects.legend')}</span><AskAssistantButton label={t('ask.label')} prompt={assistantPrompt(currentStepIndex, 'VideoAnalyze', msg('wfSteps.prompts.videoAnalyzeTrack'))} /></Group>}>
        <Stack gap="xs" {...trackTargetsFd}>
          <Text size="xs" c="dimmed">
            {t('trackObjects.intro')}
          </Text>
          {namedTargets.length === 0 && (
            <Text size="xs" c="dimmed">
              {t('trackObjects.empty')}
            </Text>
          )}
          {namedTargets.map((target, index) => {
            // eslint-disable-next-line i18next/no-literal-string -- enum value / fallback, not copy
            const method = target.method ?? 'Feature';
            // eslint-disable-next-line i18next/no-literal-string -- enum value / fallback, not copy
            const matchScope = target.matchScope ?? 'AllInstances';
            return (
              <Paper key={index} withBorder p="xs" radius="md">
                <SimpleGrid cols={{ base: 1, sm: 2 }}>
                  <TextInput
                    label={t('whatToFollow.label')}
                    description={t('whatToFollow.description', { max: TRACK_PROMPT_MAX_CHARS })}
                    placeholder={t('whatToFollow.placeholder')}
                    size="xs"
                    maxLength={TRACK_PROMPT_MAX_CHARS}
                    value={target.label}
                    onChange={(e) => updateTarget(index, { label: e.currentTarget.value })}
                  />
                  <Select
                    label={t('howToFollow.label')}
                    description={method === 'Segmentation'
                      ? t('howToFollow.descriptionSegmentation')
                      : t('howToFollow.descriptionFeature')}
                    size="xs"
                    data={TRACK_METHOD_VALUES.map((v) => ({ value: v, label: t(`trackMethod.${v}`) }))}
                    value={method}
                    onChange={(v) => v && updateTarget(index, { method: v as ObjectTrackMethod })}
                    allowDeselect={false}
                  />
                  {method === 'Segmentation' && (
                    <Select
                      label={t('howManyTo.label')}
                      description={matchScope === 'SingleTrack'
                        ? t('howManyTo.descriptionSingle')
                        : t('howManyTo.descriptionAll')}
                      size="xs"
                      data={MATCH_SCOPE_VALUES.map((v) => ({ value: v, label: t(`matchScope.${v}`) }))}
                      value={matchScope}
                      onChange={(v) => v && updateTarget(index, { matchScope: v as ObjectMatchScope })}
                      allowDeselect={false}
                    />
                  )}
                </SimpleGrid>
                <Button
                  size="compact-xs"
                  variant="subtle"
                  color="red"
                  mt="xs"
                  onClick={() => removeTarget(index)}
                >
                  {t('trackObjects.remove')}
                </Button>
              </Paper>
            );
          })}
          <Button size="compact-xs" variant="light" leftSection={<IconPlus size={14} />} onClick={addTarget}>
            {t('trackObjects.add')}
          </Button>
          {markedTargets.length > 0 && (
            <Text size="xs" c="dimmed">
              {t('trackObjects.marked', { count: markedTargets.length })}
            </Text>
          )}
          {segmentationOn && (
            <Paper withBorder p="xs" radius="md">
              <Stack gap="xs">
                <Text size="xs" fw={600}>{t('segmentation.title')}</Text>
                <EgressWarning>
                  {t('segmentation.egress')}
                </EgressWarning>
                <Switch
                  label={t('sendThisClip.label')}
                  description={t('sendThisClip.description')}
                  checked={egressAllowed}
                  onChange={(e) => patch({ allowSourceMediaEgress: e.currentTarget.checked })}
                />
                <EgressRunNote />
                {!egressAllowed && (
                  <Text size="xs" c="orange">
                    {t('segmentation.blocked')}
                  </Text>
                )}
                <ProviderSelect
                  capability="VideoSegmentation"
                  label={t('segmentationProvider.label')}
                  description={t('segmentationProvider.description')}
                  nullMeans={t('segmentationProvider.nullMeans')}
                  value={config.segmentationProviderId}
                  onChange={(id) => patch({ segmentationProviderId: id })}
                  wrapperProps={fd('config.segmentationProviderId').wrapperProps}
                />
                <SimpleGrid cols={{ base: 1, sm: 2 }}>
                  <NumberInput
                    // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                    {...fd('config.maxSegmentationSeconds')}
                    label={t('maxSegmentationSeconds.label')}
                    description={t('maxSegmentationSeconds.description')}
                    size="xs"
                    min={1}
                    max={600}
                    value={config.maxSegmentationSeconds ?? 60}
                    onChange={(v) => patch({ maxSegmentationSeconds: typeof v === 'number' ? v : 60 })}
                  />
                  <NumberInput
                    // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                    {...fd('config.segmentationDetectionThreshold')}
                    label={t('segmentationDetectionThreshold.label')}
                    description={t('segmentationDetectionThreshold.description')}
                    size="xs"
                    min={0.01}
                    max={1}
                    step={0.01}
                    decimalScale={2}
                    value={config.segmentationDetectionThreshold ?? 0.5}
                    onChange={(v) => patch({ segmentationDetectionThreshold: typeof v === 'number' ? v : 0.5 })}
                  />
                  <NumberInput
                    // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                    {...fd('config.maxSegmentationObjects')}
                    label={t('maxSegmentationObjects.label')}
                    description={t('maxSegmentationObjects.description')}
                    size="xs"
                    min={1}
                    max={16}
                    value={config.maxSegmentationObjects ?? 4}
                    onChange={(v) => patch({ maxSegmentationObjects: typeof v === 'number' ? v : 4 })}
                  />
                  <NumberInput
                    // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                    {...fd('config.segmentationTimeoutSeconds')}
                    label={t('segmentationTimeoutSeconds.label')}
                    description={t('segmentationTimeoutSeconds.description')}
                    size="xs"
                    min={60}
                    max={3600}
                    step={30}
                    value={config.segmentationTimeoutSeconds ?? 900}
                    onChange={(v) => patch({ segmentationTimeoutSeconds: typeof v === 'number' ? v : 900 })}
                  />
                </SimpleGrid>
                <CostWarning>
                  {t('segmentation.cost')}
                </CostWarning>
              </Stack>
            </Paper>
          )}
        </Stack>
      </Fieldset>

      <AdvancedSection description={t('detectionSensitivityLimits.description')}>
        <Fieldset legend={t('silenceDetection.legend')}>
          <SimpleGrid cols={{ base: 1, sm: 2 }}>
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.silenceThresholdDb')}
              label={t('silenceThresholdDb.label')}
              size="xs"
              value={config.silenceThresholdDb}
              onChange={(v) => patch({ silenceThresholdDb: typeof v === 'number' ? v : -34 })}
              disabled={!config.detectSilence}
            />
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.minSilenceMs')}
              label={t('minSilenceMs.label')}
              size="xs"
              min={0}
              value={config.minSilenceMs}
              onChange={(v) => patch({ minSilenceMs: typeof v === 'number' ? v : 350 })}
              disabled={!config.detectSilence}
            />
          </SimpleGrid>
        </Fieldset>

        <Fieldset legend={t('shotDetection.legend')}>
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.sceneThreshold')}
            label={t('sceneThreshold.label')}
            description={t('sceneThreshold.description')}
            size="xs"
            min={0}
            max={1}
            step={0.01}
            decimalScale={2}
            value={config.sceneThreshold}
            onChange={(v) => patch({ sceneThreshold: typeof v === 'number' ? v : 0.3 })}
            disabled={!config.detectShots}
          />
        </Fieldset>

        <Fieldset legend={t('visualDetail.legend')}>
          <SimpleGrid cols={{ base: 1, sm: 2 }}>
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.detectNearDuplicates')}
              label={t('detectNearDuplicates.label')}
              checked={config.detectNearDuplicates}
              onChange={(e) => patch({ detectNearDuplicates: e.currentTarget.checked })}
              disabled={!config.analyzeVisuals}
            />
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.analyzeColorGrading')}
              label={t('analyzeColorGrading.label')}
              checked={config.analyzeColorGrading}
              onChange={(e) => patch({ analyzeColorGrading: e.currentTarget.checked })}
              disabled={!config.analyzeVisuals}
            />
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.detectLookGroups')}
              label={t('detectLookGroups.label')}
              checked={config.detectLookGroups}
              onChange={(e) => patch({ detectLookGroups: e.currentTarget.checked })}
              disabled={!config.analyzeVisuals}
            />
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.detectLetterbox')}
              label={t('detectLetterbox.label')}
              checked={config.detectLetterbox}
              onChange={(e) => patch({ detectLetterbox: e.currentTarget.checked })}
              disabled={!config.analyzeVisuals}
            />
          </SimpleGrid>
          {config.analyzeVisuals && (
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.visualDetail')}
              label={t('visualDetail.label')}
              description={t('visualDetail.description')}
              size="xs"
              value={config.visualDetail}
              data={[
                { value: 'None', label: t('visualDetailMode.None') },
                { value: 'Compact', label: t('visualDetailMode.Compact') },
                { value: 'Full', label: t('visualDetailMode.Full') },
              ]}
              onChange={(v) => v && patch({ visualDetail: v as VideoVisualDetail })}
            />
          )}
        </Fieldset>

        {vision !== 'Off' && (
          <Fieldset legend={t('shotDescriptions.legend')}>
            <SimpleGrid cols={{ base: 1, sm: 2 }}>
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.keyframesPerShot')}
                label={t('keyframesPerShot.label')}
                description={t('keyframesPerShot.description')}
                size="xs"
                min={1}
                max={3}
                value={config.keyframesPerShot}
                onChange={(v) => patch({ keyframesPerShot: typeof v === 'number' ? v : 1 })}
              />
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.keyframeMaxWidth')}
                label={t('keyframeMaxWidth.label')}
                size="xs"
                min={64}
                value={config.keyframeMaxWidth ?? 512}
                onChange={(v) => patch({ keyframeMaxWidth: typeof v === 'number' ? v : 512 })}
              />
            </SimpleGrid>
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.persistKeyframes')}
              label={t('persistKeyframes.label')}
              description={t('persistKeyframes.description')}
              checked={config.persistKeyframes ?? false}
              onChange={(e) => patch({ persistKeyframes: e.currentTarget.checked })}
            />
          </Fieldset>
        )}

        <Fieldset legend={t('howManyOptions.legend')}>
          <SimpleGrid cols={{ base: 1, sm: 2 }}>
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.maxPlacements')}
              label={t('maxPlacements.label')}
              size="xs"
              min={1}
              value={config.maxPlacements ?? 40}
              onChange={(v) => patch({ maxPlacements: typeof v === 'number' ? v : 40 })}
              disabled={!config.emitOverlayPlacements}
            />
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.maxMusicTracks')}
              label={t('maxMusicTracks.label')}
              size="xs"
              min={1}
              value={config.maxMusicTracks ?? 20}
              onChange={(v) => patch({ maxMusicTracks: typeof v === 'number' ? v : 20 })}
              disabled={!config.offerMusicTracks}
            />
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.maxSfxClips')}
              label={t('maxSfxClips.label')}
              size="xs"
              min={1}
              value={config.maxSfxClips ?? 40}
              onChange={(v) => patch({ maxSfxClips: typeof v === 'number' ? v : 40 })}
              disabled={!config.offerSfxClips}
            />
          </SimpleGrid>
          {config.detectInsertRegions && (
            <SimpleGrid cols={{ base: 1, sm: 2 }}>
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.minInsertRegionSeconds')}
                label={t('minInsertRegionSeconds.label')}
                size="xs"
                min={0.1}
                step={0.1}
                decimalScale={1}
                value={config.minInsertRegionSeconds ?? 1.0}
                onChange={(v) => patch({ minInsertRegionSeconds: typeof v === 'number' ? v : 1.0 })}
              />
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.maxInsertRegions')}
                label={t('maxInsertRegions.label')}
                size="xs"
                min={1}
                value={config.maxInsertRegions ?? 8}
                onChange={(v) => patch({ maxInsertRegions: typeof v === 'number' ? v : 8 })}
              />
            </SimpleGrid>
          )}
        </Fieldset>

        {(config.trackObjects ?? []).length > 0 && (
          <Fieldset legend={t('objectTracking.legend')}>
            <SimpleGrid cols={{ base: 1, sm: 3 }}>
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.objectDetectEverySec')}
                label={t('objectDetectEverySec.label')}
                description={t('objectDetectEverySec.description')}
                size="xs"
                min={0.25}
                step={0.25}
                decimalScale={2}
                value={config.objectDetectEverySec ?? 1}
                onChange={(v) => patch({ objectDetectEverySec: typeof v === 'number' ? v : 1 })}
              />
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.maxObjectDetectFrames')}
                label={t('maxObjectDetectFrames.label')}
                description={t('maxObjectDetectFrames.description')}
                size="xs"
                min={1}
                max={60}
                value={config.maxObjectDetectFrames ?? 12}
                onChange={(v) => patch({ maxObjectDetectFrames: typeof v === 'number' ? v : 12 })}
              />
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.objectTrackMaxSide')}
                label={t('objectTrackMaxSide.label')}
                description={t('objectTrackMaxSide.description')}
                size="xs"
                min={320}
                max={1920}
                step={160}
                value={config.objectTrackMaxSide ?? 960}
                onChange={(v) => patch({ objectTrackMaxSide: typeof v === 'number' ? v : 960 })}
              />
            </SimpleGrid>
          </Fieldset>
        )}

        {config.transcription !== 'Off' && (
          <Fieldset legend={t('transcriptionDetail.legend')}>
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.wordTimestamps')}
              label={t('wordTimestamps.label')}
              description={t('wordTimestamps.description')}
              checked={config.wordTimestamps}
              onChange={(e) => patch({ wordTimestamps: e.currentTarget.checked })}
            />
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.maxAsrChunkBytes')}
              label={t('maxAsrChunkBytes.label')}
              size="xs"
              min={1_000_000}
              value={config.maxAsrChunkBytes}
              onChange={(v) => patch({ maxAsrChunkBytes: typeof v === 'number' ? v : 5_000_000 })}
            />
          </Fieldset>
        )}

        <Fieldset legend={t('limits.legend')}>
          <SimpleGrid cols={{ base: 1, sm: 2 }}>
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.maxDurationSeconds')}
              label={t('maxDurationSeconds.label')}
              size="xs"
              description={t('maxDurationSeconds.description')}
              min={1}
              decimalScale={0}
              value={config.maxDurationSeconds > 0 ? Math.ceil(config.maxDurationSeconds / 60) : ''}
              onChange={(v) => patch({ maxDurationSeconds: typeof v === 'number' && v > 0 ? Math.round(v * 60) : 0 })}
            />
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.maxInputBytes')}
              label={t('maxInputBytes.label')}
              size="xs"
              description={t('maxInputBytes.description')}
              min={0.1}
              decimalScale={1}
              value={config.maxInputBytes > 0 ? Math.round((config.maxInputBytes / GIB) * 10) / 10 : ''}
              onChange={(v) => patch({ maxInputBytes: typeof v === 'number' && v > 0 ? Math.round(v * GIB) : 0 })}
            />
          </SimpleGrid>
        </Fieldset>

        <Fieldset legend={t('howMuchThe.legend')}>
          <Text size="xs" c="dimmed">{t('limitsHint')}</Text>
          <SimpleGrid cols={{ base: 1, sm: 3 }}>
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.maxOutputChars')}
              label={t('maxOutputChars.label')}
              size="xs"
              min={256}
              max={200_000}
              value={config.maxOutputChars}
              onChange={(v) => patch({ maxOutputChars: typeof v === 'number' ? v : 24_000 })}
            />
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.maxViewSegments')}
              label={t('maxViewSegments.label')}
              size="xs"
              min={1}
              value={config.maxViewSegments}
              onChange={(v) => patch({ maxViewSegments: typeof v === 'number' ? v : 400 })}
            />
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.maxSegmentTextChars')}
              label={t('maxSegmentTextChars.label')}
              size="xs"
              min={1}
              value={config.maxSegmentTextChars}
              onChange={(v) => patch({ maxSegmentTextChars: typeof v === 'number' ? v : 160 })}
            />
          </SimpleGrid>
        </Fieldset>

        <Fieldset legend={t('checksBeforeThe.legend')}>
          <Text size="xs" c="dimmed">{t('checksHint')}</Text>
          <SimpleGrid cols={{ base: 1, sm: 3 }}>
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.expect')}
              label={t('expect.label')}
              min={0}
              size="xs"
              value={config.expect?.minShots ?? undefined}
              onChange={(v) => patch({ expect: { ...config.expect, minShots: typeof v === 'number' ? v : null } })}
            />
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.expect')}
              label={t('expect_atLeastThis.label')}
              min={0}
              size="xs"
              value={config.expect?.minTranscriptSegments ?? undefined}
              onChange={(v) =>
                patch({ expect: { ...config.expect, minTranscriptSegments: typeof v === 'number' ? v : null } })
              }
            />
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.expect')}
              label={t('expect_atMostThis.label')}
              description={t('expect.description')}
              min={0}
              max={1}
              step={0.01}
              decimalScale={2}
              size="xs"
              value={config.expect?.maxSilenceRatio ?? undefined}
              onChange={(v) =>
                patch({ expect: { ...config.expect, maxSilenceRatio: typeof v === 'number' ? v : null } })
              }
            />
          </SimpleGrid>
        </Fieldset>

        <Fieldset legend={t('decisionModelFor.legend')}>
          <Stack gap="xs">
            <ModeHint>
              {t('decision.hint')}
            </ModeHint>
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.derushPrePass')}
              label={t('derushPrePass.label')}
              description={t('derushPrePass.description')}
              size="xs"
              allowDeselect={false}
              // "Fast" is accepted by the server but behaves exactly like "Prior", so it is shown as On.
              value={prePass === 'Off' ? 'Off' : 'Prior'}
              onChange={(v) => v && patch({ derushPrePass: v as VideoDerushPrePassMode })}
              data={[
                { value: 'Off', label: t('decision.Off') },
                { value: 'Prior', label: t('decision.Prior') },
              ]}
            />
            {prePass !== 'Off' && (
              <>
                <CostWarning>
                  {t('decision.cost')}
                </CostWarning>
                <SimpleGrid cols={{ base: 1, sm: 2 }}>
                  <NumberInput
                    // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                    {...fd('config.derushAcceptAt')}
                    label={t('derushAcceptAt.label')}
                    description={t('derushAcceptAt.description')}
                    size="xs" min={0} max={1} step={0.01} decimalScale={2}
                    value={config.derushAcceptAt ?? 0.85}
                    onChange={(v) => patch({ derushAcceptAt: typeof v === 'number' ? v : 0.85 })}
                  />
                  <NumberInput
                    // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                    {...fd('config.derushMinMargin')}
                    label={t('derushMinMargin.label')}
                    description={t('derushMinMargin.description')}
                    size="xs" min={0} max={1} step={0.01} decimalScale={2}
                    value={config.derushMinMargin ?? 0.25}
                    onChange={(v) => patch({ derushMinMargin: typeof v === 'number' ? v : 0.25 })}
                  />
                </SimpleGrid>
              </>
            )}
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.screenInstructionLikeSpans')}
              label={t('screenInstructionLikeSpans.label')}
              description={t('screenInstructionLikeSpans.description')}
              checked={config.screenInstructionLikeSpans ?? false}
              onChange={(e) => patch({ screenInstructionLikeSpans: e.currentTarget.checked })}
            />
            {config.screenInstructionLikeSpans && (
              <EgressWarning>
                {t('decision.egress')}
              </EgressWarning>
            )}
          </Stack>
        </Fieldset>

        <Paper p="xs" withBorder>
          <Text size="xs" fw={600} c="dimmed" mb={4}>{t('technical')}</Text>
        {/* eslint-disable i18next/no-literal-string -- sample of the step's JSON output, not copy */}
        <Code block style={{ fontSize: 12 }}>
{`{
  "view": {
    "media": { "durationSec": 184.32, "fpsNum": 30, "fpsDen": 1, "width": 1920, "height": 1080 },
    "shots": [ { "id": "s0", "startSec": 0.0, "endSec": 12.4, "durationSec": 12.4${
      config.analyzeVisuals && config.visualDetail !== 'None'
        ? ',\n      "v": { "motion": 12, "move": "Pan", "cutIn": "still", "cutOut": "moving",\n             "bright": 41, "contrast": 22, "colors": ["#2b3a4f", "#c9b48a"],\n             "safe": { "region": "LowerThird", "fit": 88, "text": "Light" },\n             "dup": "d2", "best": true }' + (config.analyzeAudioLevels ? ',\n      "a": { "rms": -21, "speech": 82 }' : '')
        : ''
    } } ],
    "silences": [ { "id": "g3", "startSec": 12.1, "endSec": 13.9, "durationSec": 1.8, "afterShot": "s0" } ],
    "segments": [ { "id": "t7", "shot": "s0", "startSec": 1.2, "endSec": 4.9, "text": "so what we built here is" } ]${
      config.analyzeVisuals && config.visualDetail !== 'None'
        ? ',\n    "pacing": { "meanShotSec": 4.1, "medianShotSec": 3.8, "cutsPerMinute": 14.6, "motionTimeline": [12, 30, 8], "timelineBinSec": 5.0 },\n    "duplicateGroups": [ { "id": "d2", "shotIds": ["s4", "s7"], "bestShotId": "s7", "similarity": 94 } ]'
        : ''
    }
  },
  "meta": {
    "operation": "videoAnalyze",
    "artifactStorageKey": "projects/.../step-1-analysis.json",
    "offeredIdCount": 412, "truncated": false, "droppedItems": 0,
    "transcription": { "mode": "${config.transcription}", "applied": true, "provider": "whisper-local", "degraded": false },
    "visual": { "applied": ${config.analyzeVisuals}, "degraded": false, "sampleFps": ${config.visualSampleFps}, "gridWidth": ${config.visualGridWidth}, "gridHeight": ${config.visualGridHeight}, "detail": "${config.analyzeVisuals ? config.visualDetail : 'None'}" },
    "audioLevels": { "applied": ${config.analyzeAudioLevels} },
    "sourceChars": 1843201, "outputChars": 23117
  }
}`}
        </Code>
        {/* eslint-enable i18next/no-literal-string */}
        </Paper>
      </AdvancedSection>
    </Stack>
  );
}

/**
 * A NumberInput that never rewrites what the user is typing. While a value is half-typed ("1.", "0.")
 * Mantine reports it as a string; writing that back as "no value" would clear the controlled field and
 * turn "1.5" into 5. The raw text is kept locally while typing and only complete numbers reach the
 * config. On blur the last text (Mantine may hand it over as "10." after its own clamp) is parsed with
 * any trailing separator removed, clamped to min/max, and saved, so the field and the config agree.
 */
function TypingSafeNumberInput({
  value,
  onValue,
  onBlur,
  min,
  max,
  ...props
}: Omit<NumberInputProps, 'value' | 'onChange'> & { value: number | undefined; onValue: (value: number | undefined) => void }) {
  const [raw, setRaw] = useState<string | number | null>(null);
  const latest = useRef<string | number | null>(null);
  const clamp = (n: number) => Math.min(max ?? Number.POSITIVE_INFINITY, Math.max(min ?? Number.NEGATIVE_INFINITY, n));
  return (
    <NumberInput
      {...props}
      min={min}
      max={max}
      value={raw ?? value ?? ''}
      onChange={(v) => {
        latest.current = v;
        setRaw(v);
        if (typeof v === 'number') onValue(v);
        else if (v === '') onValue(undefined);
      }}
      onBlur={(event) => {
        const last = latest.current;
        if (typeof last === 'number') onValue(clamp(last));
        else if (typeof last === 'string' && last.trim() !== '') {
          const parsed = Number.parseFloat(last.trim().replace(/[.,]+$/, ''));
          if (Number.isFinite(parsed)) onValue(clamp(parsed));
        }
        latest.current = null;
        setRaw(null);
        onBlur?.(event);
      }}
    />
  );
}
