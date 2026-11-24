'use client';

import { useState } from 'react';
import { useT } from '@/i18n';
import { msg, type Msg } from '@/i18n/msg';
import {
  Stack, Group, Select, TextInput, NumberInput, Switch, Text, SimpleGrid, Fieldset, Button, Alert,
  Paper, Combobox, InputBase, useCombobox,
} from '@mantine/core';
import { IconAlertTriangle, IconPlus } from '@tabler/icons-react';
import {
  REGION_EFFECTS,
  type RegionEffect,
  type VideoRegionEffect,
} from '@/lib/types/workflow';
import type {
  VideoCompileStepConfig as VideoCompileStepConfigValue,
  VideoCompileMode,
  VideoTransitionPolicy,
  MusicDuckingMode,
  MusicFit,
  VoiceoverMode,
  VideoOutputFormat,
  VideoOutputFit,
  VideoKeepOrder,
  VideoBeatSync,
  ProgramLoudness,
  VideoInsertContentAssignment,
  InsertContentDipMode,
  CensorStyle,
} from '@/lib/types/workflow';
import type { TimelineRenderRateControl } from '@/lib/types/workflow';
import { useProjectFiles } from '@/lib/hooks/use-files';
import { VideoCompileReframeFields } from './VideoCompileReframeFields';
import { InputRefPicker } from './ExtractStepConfig';
import type { StepData } from './WorkflowStepList';
import {
  useFd, AskAssistantButton, assistantPrompt, type WrapProps, StepOrderSelect, AdvancedSection, NoAudioFilesNotice,
} from './config-kit';
import { OUTPUT_FORMAT_OPTIONS, OUTPUT_FIT_OPTIONS } from './output-format';
import { CaptionStyleFields } from './CaptionStyleFields';

/** Mirrors `VoiceoverMode` in VideoCompileStepConfig.cs. Fixed list — these words reach the ffmpeg filtergraph through first-party tables. */
// Labels for the option tables below: wfSteps.videoCompile.<table>.<value>
const VOICEOVER_MODE_VALUES: VoiceoverMode[] = ['OverMusic', 'DuckDialogue', 'ReplaceDialogue'];

/** Mirrors `ProgramLoudness` in VideoCompileStepConfig.cs. */
const LOUDNESS_VALUES: ProgramLoudness[] = ['Off', 'Social', 'Web', 'Broadcast'];

/** Mirrors `VideoBeatSync` in VideoCompileStepConfig.cs. */
const BEAT_SYNC_VALUES: VideoBeatSync[] = ['Off', 'Beat', 'Bar'];

/** Mirrors `VideoKeepOrder` in VideoCompileStepConfig.cs. */
const KEEP_ORDER_VALUES: VideoKeepOrder[] = ['SourceOrder', 'AsListed'];

const INSERT_FIT_VALUES = ['Auto', 'Contain', 'Cover', 'Stretch'];

/** Mirrors `InsertContentDipMode` in VideoCompileStepConfig.cs. */
const INSERT_DIP_VALUES: InsertContentDipMode[] = ['Freeze', 'Cut', 'Keep'];

/**
 * One plain-words label per `RegionEffect` member; the Record makes a new member a type error, and
 * the shared list below means the Select can never offer a value the engine does not implement.
 */
const REGION_EFFECT_VALUES: readonly RegionEffect[] = REGION_EFFECTS;

/** The engine refuses a ninth entry with EFFECT_LIMIT_EXCEEDED rather than dropping one. */
const MAX_REGION_EFFECTS = 8;

/** Fixed allowlist — these config values reach ffmpeg argv, so they must never be free text (plan R11). */
const VIDEO_CODEC_VALUES = ['libx264', 'libx265'];
const AUDIO_CODEC_OPTIONS = ['aac'];
const PRESET_OPTIONS = ['ultrafast', 'veryfast', 'fast', 'medium'];
const PROGRAM_FADE_COLOR_OPTIONS = ['black', 'white'];
const TRANSITION_POLICY_VALUES: VideoTransitionPolicy[] = ['Off', 'AudioOnly', 'Auto', 'Expressive', 'Editor'];

/** Mirrors `TimelineRenderRateControl`. */
const RATE_CONTROLS: TimelineRenderRateControl[] = ['Quality', 'Capped', 'Average'];

/** Builds a sensible default config for a freshly-added VideoCompile step. Mirrors the C# record defaults exactly. */
export function createDefaultVideoCompileStepConfig(): VideoCompileStepConfigValue {
  return {
    version: 1,
    decision: { from: 'Previous' },
    analysisStepOrder: 1,
    analysisStepResultId: null,
    mode: 'Reencode',
    prePaddingMs: 80,
    postPaddingMs: 120,
    minSegmentMs: 250,
    maxSegments: 200,
    allowKeyframeSnapping: false,
    outputFileName: 'edited.mp4',
    videoCodec: 'libx264',
    audioCodec: 'aac',
    crf: 20,
    preset: 'veryfast',
    registerProjectFile: true,
    graphicsPlan: null,
    enableGraphics: false,
    maxOverlays: 20,
    overlayShortMs: 1500,
    overlayMediumMs: 3000,
    overlayHoldMs: 6000,
    overlayFadeMs: 300,
    overlayFontSizePct: 5,
    overlayFontColor: 'white',
    overlayBoxColor: 'black@0.45',
    maxOverlayTextChars: 80,
    maxOverlaySubtextChars: 60,
    programFadeInMs: 0,
    programFadeOutMs: 0,
    programAudioFadeInMs: 0,
    programAudioFadeOutMs: 0,
    programFadeColor: 'black',
    transitionPolicy: 'Off',
    audioSeamRampMs: 24,
    softCutMs: 200,
    dissolveMs: 500,
    dipToBlackMs: 600,
    dipCutMs: 220,
    maxTransitionMs: 1200,
    maxTransitionRatioPct: 35,
    maxTransitionSegments: 80,
    sectionBreakGapMs: 8000,
    generatedClips: null,
    enableGeneratedClips: false,
    maxGeneratedClips: 8,
    takeSelection: null,
    expect: null,
  };
}

/**
 * The plain-language warning for a config the compile step would reject because of the inserted
 * clip's sound (INSERT_AUDIO_REQUIRES_INSERTS / INSERT_AUDIO_REQUIRES_AUDIO_REENCODE), or null when
 * the run is fine. Exported so its wording is pinned by a test without rendering the form.
 */
export function describeInsertAudioProblem(
  config: VideoCompileStepConfigValue,
): { title: string; body: string } | null {
  if (config.mixInsertAudio !== true) return null;
  if (!config.enableInserts) {
    return {
      title: 'Screen inserts are switched off',
      body: 'Your inserted clip\'s own sound can only be heard when screen inserts are on. Switch on "Show content on a green screen" above, or switch this sound back off.',
    };
  }
  if (config.audioCodec === 'copy') {
    return {
      title: 'This sound cannot be mixed in',
      body: 'The program\'s audio is set to be copied as it is, which leaves no room to add anything to it. Choose another audio codec under Advanced below, or switch this sound back off.',
    };
  }
  return null;
}

/**
 * Catalogue-message form of `describeInsertAudioProblem`: what the form shows. The plain-English function above is kept
 * for its test; both decide the same way.
 */
export function describeInsertAudioProblemMsg(
  config: VideoCompileStepConfigValue,
): { title: Msg; body: Msg } | null {
  if (config.mixInsertAudio !== true) return null;
  if (!config.enableInserts) {
    return { title: msg('wfSteps.videoCompile.insertAudioProblem.insertsOff.title'), body: msg('wfSteps.videoCompile.insertAudioProblem.insertsOff.body') };
  }
  if (config.audioCodec === 'copy') {
    return { title: msg('wfSteps.videoCompile.insertAudioProblem.copyAudio.title'), body: msg('wfSteps.videoCompile.insertAudioProblem.copyAudio.body') };
  }
  return null;
}

/**
 * The TWO engines this one list drives, and they are not interchangeable: `Tracker` is the free
 * planar tracker, whose quads are hidden by `censorStyle` (Blur/Pixelate/Fill, with padding and
 * hold), while `Segmentation` follows the object with a paid, text-prompted model and is graded by
 * one of the six `REGION_EFFECTS` (with strength, soft edge, opacity and invert). Only the LIST is
 * shared — the two JSON shapes stay exactly as they were, so a saved step renders and saves as
 * before until it is edited.
 */
type EffectEngine = 'Tracker' | 'Segmentation';

/** How an option says which engine will follow it: "speedometer — tracked", "speedometer — segmentation (paid)". */
// Engine words: wfSteps.videoCompile.engine.<Tracker|Segmentation>

/** One choice in the object control: the exact words written to the config, and the engine that follows them. */
interface ObjectChoice { text: string; method: EffectEngine }

/** The free tracker's own closed set (`CensorStyle`) — the letters that reach the ffmpeg hide path. */
const TRACKED_EFFECT_VALUES: CensorStyle[] = ['Blur', 'Pixelate', 'Fill'];

/**
 * The object control: a combobox over the objects the earlier 'Watch footage' step(s) follow, each
 * option labelled with the engine that follows it. Typing stays available as an explicit escape
 * hatch — a segmentation target may legitimately be named before that step has run — and free words
 * are offered once per engine, because "how will this be followed" is the one thing the words
 * themselves cannot say.
 */
function ObjectCombobox({
  label, value, method, choices, onChange, wrapperProps,
}: {
  label: string;
  value: string;
  method: EffectEngine;
  choices: ObjectChoice[];
  onChange: (text: string, chosen: EffectEngine) => void;
  wrapperProps?: WrapProps;
}) {
  const t = useT('wfSteps.videoCompile');
  const combobox = useCombobox();
  /** `null` while the control shows the row's own object; the typed words while the user is searching. */
  const [query, setQuery] = useState<string | null>(null);
  const shown = query ?? value;
  // Only what the user typed filters the list: opening the control on a row's own object still shows
  // every object, or a row could never be re-pointed at a different one.
  const typed = (query ?? '').trim();
  const needle = typed.toLowerCase();
  const offered: ObjectChoice[] = needle
    ? choices.filter((choice) => choice.text.toLowerCase().includes(needle))
    : [...choices];
  // Free words become two options, one per engine, unless they already name a known object exactly.
  if (typed && !choices.some((choice) => choice.text.trim().toLowerCase() === needle)) {
    offered.push({ text: typed, method: 'Tracker' }, { text: typed, method: 'Segmentation' });
  }

  const submit = (optionValue: string) => {
    const choice = offered[Number(optionValue)];
    setQuery(null);
    combobox.closeDropdown();
    if (choice) onChange(choice.text, choice.method);
  };

  return (
    <Combobox store={combobox} onOptionSubmit={submit}>
      <Combobox.Target withExpandedAttribute>
        <InputBase
          type="text"
          size="xs"
          label={label}
          placeholder={t('objectCombobox.pickAnObject.placeholder')}
          autoComplete="off"
          maxLength={120}
          wrapperProps={wrapperProps}
          rightSection={<Combobox.Chevron size="xs" />}
          rightSectionPointerEvents="none"
          value={shown}
          onChange={(event) => {
            setQuery(event.currentTarget.value);
            combobox.openDropdown();
            combobox.selectFirstOption();
          }}
          onClick={() => combobox.openDropdown()}
          onFocus={() => combobox.openDropdown()}
          onBlur={() => {
            combobox.closeDropdown();
            // Nothing is written on blur: a half-typed word must never reach the saved config.
            setQuery(null);
          }}
        />
      </Combobox.Target>
      <Combobox.Dropdown>
        <Combobox.Options>
          {offered.map((choice, index) => (
            <Combobox.Option
              key={`${choice.method}: ${choice.text}`}
              value={String(index)}
              active={choice.method === method && choice.text === value}
            >
              {t('objectOption', { text: choice.text, engine: t(`engine.${choice.method}`) })}
            </Combobox.Option>
          ))}
        </Combobox.Options>
      </Combobox.Dropdown>
    </Combobox>
  );
}

interface VideoCompileStepConfigProps {
  config: VideoCompileStepConfigValue;
  onChange: (config: VideoCompileStepConfigValue) => void;
  allSteps?: StepData[];
  currentStepIndex?: number;
  /** Lets the deterministic (no-agent) background-music picker list this project's audio files. */
  projectId?: string;
}

/**
 * Configuration form for a VideoCompile step (deterministic ffmpeg-based cutting — no model call).
 * Mirrors the backend `VideoCompileStepConfig` record field-for-field.
 */
export function VideoCompileStepConfig({
  config, onChange, allSteps = [], currentStepIndex = 0, projectId,
}: VideoCompileStepConfigProps) {
  const t = useT('wfSteps.videoCompile');
  const fd = useFd();

  const priorStepOptions = allSteps
    .slice(0, currentStepIndex)
    .map((previousStep, index) => {
      const stepOrder = index + 1;
      const label = previousStep.label?.trim() || `Step ${stepOrder}`;
      return { value: String(stepOrder), label: `${stepOrder}. ${label}` };
    });

  const analyzeStepOptions = allSteps
    .slice(0, currentStepIndex)
    .map((previousStep, index) => ({ step: previousStep, stepOrder: index + 1 }))
    .filter(({ step }) => step.stepType === 'VideoAnalyze')
    .map(({ step, stepOrder }) => ({
      value: String(stepOrder),
      label: `${stepOrder}. ${step.label?.trim() || `Step ${stepOrder}`}`,
    }));

  const { data: projectFiles } = useProjectFiles(projectId ?? '');
  const audioFiles = (projectFiles ?? []).filter((f) => f.mimeType?.startsWith('audio/'));
  const insertableFiles = (projectFiles ?? []).filter(
    (f) => f.mimeType?.startsWith('video/') || f.mimeType?.startsWith('image/'),
  );

  const patch = (updates: Partial<VideoCompileStepConfigValue>) => onChange({ ...config, ...updates });

  /** Region-effect rows, always an array: absent or null means "no effect on any object". */
  const regionEffects = config.regionEffects ?? [];

  // What the run would reject about the insert sound, said on the screen instead of after it fails.
  const insertAudioProblem = describeInsertAudioProblemMsg(config);

  /**
   * Every optional post-production stage below (grade, inserts, SFX) is documented as requiring
   * `Mode = Reencode`; stream-copy can't touch pixels or remix audio. Flipping one on while
   * stream-copy is selected would be a config error the backend rejects at execution time, so the
   * toggle corrects the mode here instead — exactly what the pre-existing graphics switch does.
   */
  const enableRequiringReencode = (updates: Partial<VideoCompileStepConfigValue>, on: boolean) =>
    patch({ ...updates, mode: on && config.mode === 'StreamCopy' ? 'Reencode' : config.mode });

  /** True while a new row is waiting for its object: nothing is written until one is named. */
  const [draftOpen, setDraftOpen] = useState(false);

  /**
   * Every object an earlier 'Watch footage' step says it will follow, plus whatever this config
   * already names, each with the engine that follows it. A target with no `method` is followed by
   * the free tracker — the C# record's own default — and marked/hand-placed areas are offered too,
   * because they carry a label as well.
   */
  const objectChoices: ObjectChoice[] = [];
  const seenObjects = new Set<string>();
  const addObjectChoice = (text: string, method: EffectEngine) => {
    const words = text.trim();
    if (!words) return;
    const key = `${method}\u0000${words}`;
    if (seenObjects.has(key)) return;
    seenObjects.add(key);
    objectChoices.push({ text: words, method });
  };
  for (const step of allSteps.slice(0, currentStepIndex)) {
    if (step.stepType !== 'VideoAnalyze') continue;
    for (const target of step.videoAnalyzeConfig?.trackObjects ?? []) {
      addObjectChoice(
        target.label ?? '',
        (target.method ?? 'Feature') === 'Segmentation' ? 'Segmentation' : 'Tracker',
      );
    }
  }
  for (const label of config.censorLabels ?? []) addObjectChoice(label, 'Tracker');
  for (const entry of regionEffects) addObjectChoice(entry.target, 'Segmentation');

  /**
   * A row is written where the engine that follows its object reads it: the free tracker's labels
   * live in `censorLabels` (+ the shared style/padding/hold), a segmentation target is one
   * `regionEffects` entry. Re-pointing an existing row at an object the other engine follows moves
   * the row, because neither engine can render the other's effects.
   */
  const writeObject = (from: EffectEngine, index: number, text: string, to: EffectEngine) => {
    const words = text.trim();
    if (!words) return;
    if (from === 'Tracker' && to === 'Tracker') {
      patch({ censorLabels: (config.censorLabels ?? []).map((label, i) => (i === index ? words : label)) });
      return;
    }
    if (from === 'Segmentation' && to === 'Segmentation') {
      patch({ regionEffects: regionEffects.map((entry, i) => (i === index ? { ...entry, target: words } : entry)) });
      return;
    }
    if (from === 'Tracker') {
      // EFFECT_LIMIT_EXCEEDED: the engine refuses a ninth entry rather than dropping one, so it is
      // never written here either — the section says why.
      if (regionEffects.length >= MAX_REGION_EFFECTS) return;
      const labels = (config.censorLabels ?? []).filter((_, i) => i !== index);
      enableRequiringReencode({
        censorLabels: labels.length > 0 ? labels : null,
        regionEffects: [...regionEffects, { target: words, effect: 'Blur' }],
      }, true);
      return;
    }
    const entries = regionEffects.filter((_, i) => i !== index);
    enableRequiringReencode({
      regionEffects: entries,
      censorLabels: [...(config.censorLabels ?? []), words],
    }, true);
  };

  /** A new row has no shape until its object is named: the engine the object is followed with decides it. */
  const addObjectRow = (text: string, method: EffectEngine) => {
    const words = text.trim();
    if (!words) return;
    if (method === 'Tracker') {
      if ((config.censorLabels ?? []).includes(words)) return;
      enableRequiringReencode({
        censorLabels: [...(config.censorLabels ?? []), words],
        // The style is what the row's effect control shows; it is written so the row reads back as chosen.
        censorStyle: config.censorStyle ?? 'Blur',
      }, true);
      setDraftOpen(false);
      return;
    }
    if (regionEffects.length >= MAX_REGION_EFFECTS) return;
    enableRequiringReencode({ regionEffects: [...regionEffects, { target: words, effect: 'Blur' }] }, true);
    setDraftOpen(false);
  };

  const removeObjectRow = (from: EffectEngine, index: number) => {
    if (from === 'Tracker') {
      const labels = (config.censorLabels ?? []).filter((_, i) => i !== index);
      patch({ censorLabels: labels.length > 0 ? labels : null });
      return;
    }
    patch({ regionEffects: regionEffects.filter((_, i) => i !== index) });
  };

  /**
   * A saved step is capped at eight `regionEffects`, while the tracker's label list has no cap — so
   * the button is only unavailable when the paid list is full AND no tracked object is left to add.
   */
  const trackedObjectsLeft = objectChoices.some(
    (choice) => choice.method === 'Tracker' && !(config.censorLabels ?? []).includes(choice.text),
  );
  const canAddObject = regionEffects.length < MAX_REGION_EFFECTS || trackedObjectsLeft;

  return (
    <Stack gap="md" onClick={(e) => e.stopPropagation()}>
      <Group justify="space-between" align="center">
        <Text size="xs" c="dimmed">{t('intro')}</Text>
        <AskAssistantButton prompt={assistantPrompt(currentStepIndex, 'VideoCompile', msg('wfSteps.prompts.videoCompile'))} />
      </Group>
      <Switch
        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
        {...fd('config.keepWholeSources')}
        label={t('keepWholeSources.label')}
        description={t('keepWholeSources.description')}
        checked={config.keepWholeSources ?? false}
        onChange={(e) => patch({ keepWholeSources: e.currentTarget.checked })}
      />
      {!config.keepWholeSources && (
        <InputRefPicker
          label={t('whichClipsTo.label')}
          value={config.decision}
          onChange={(decision) => patch({ decision })}
          priorStepOptions={priorStepOptions}
          allowedSources={['Previous', 'Step']}
        />
      )}
      {!config.keepWholeSources && (
        <Select
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.keepOrder')}
          label={t('keepOrder.label')}
          description={t('keepOrder.description')}
          size="xs"
          data={KEEP_ORDER_VALUES.map((v) => ({ value: v, label: t(`keepOrderMode.${v}`) }))}
          value={config.keepOrder ?? 'SourceOrder'}
          onChange={(v) => v && patch({ keepOrder: v as VideoKeepOrder })}
          allowDeselect={false}
        />
      )}

      {analyzeStepOptions.length > 0 ? (
        <Select
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.analysisStepOrder')}
          label={t('analysisStepOrder.label')}
          description={t('analysisStepOrder.description')}
          size="xs"
          data={analyzeStepOptions}
          value={String(config.analysisStepOrder)}
          onChange={(v) => v && patch({ analysisStepOrder: Number(v) })}
        />
      ) : (
        <NumberInput
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.analysisStepOrder')}
          label={t('analysisStepOrder_footageStepNumber.label')}
          description={t('analysisStepOrder_footageStepNumber.description')}
          size="xs"
          min={1}
          value={config.analysisStepOrder}
          onChange={(v) => patch({ analysisStepOrder: typeof v === 'number' ? v : 1 })}
        />
      )}

      <Fieldset legend={t('formatAndLength.legend')}>
        <Stack gap="xs">
          <SimpleGrid cols={{ base: 1, sm: 2 }}>
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.outputFormat')}
              label={t('outputFormat.label')}
              description={t('outputFormat.description')}
              size="xs"
              data={OUTPUT_FORMAT_OPTIONS.map(({ value }) => ({ value, label: t(`outputFormatMode.${value}`) }))}
              value={config.outputFormat ?? 'Source'}
              onChange={(v) => v && patch({ outputFormat: v as VideoOutputFormat })}
              allowDeselect={false}
            />
            {(config.outputFormat ?? 'Source') !== 'Source' && (
              <Select
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.outputFit')}
                label={t('outputFit.label')}
                size="xs"
                data={OUTPUT_FIT_OPTIONS.map(({ value }) => ({ value, label: t(`outputFitMode.${value}`) }))}
                value={config.outputFit ?? 'Crop'}
                onChange={(v) => v && patch({ outputFit: v as VideoOutputFit })}
                allowDeselect={false}
              />
            )}
            {(config.outputFormat ?? 'Source') !== 'Source' && config.outputFit === 'Subject' && (
              <Text size="xs" c="dimmed">
                {t.rich('subjectFitNote', { b: (chunks) => <b>{chunks}</b> })}
              </Text>
            )}
          </SimpleGrid>
          <Select
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.loudnessTarget')}
            label={t('loudnessTarget.label')}
            description={t('loudnessTarget.description')}
            size="xs"
            data={LOUDNESS_VALUES.map((v) => ({ value: v, label: t(`loudnessMode.${v}`) }))}
            value={config.loudnessTarget ?? 'Off'}
            onChange={(v) => v && patch({ loudnessTarget: v as ProgramLoudness })}
            allowDeselect={false}
          />
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.targetDurationSec')}
            label={t('targetDurationSec.label')}
            description={t('targetDurationSec.description')}
            size="xs"
            min={0}
            max={3600}
            value={config.targetDurationSec ?? 0}
            onChange={(v) => patch({ targetDurationSec: typeof v === 'number' ? Math.max(0, Math.round(v)) : 0 })}
          />
        </Stack>
      </Fieldset>

      <Fieldset legend={t('saving.legend')}>
        <SimpleGrid cols={{ base: 1, sm: 2 }}>
          <TextInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.outputFileName')}
            label={t('outputFileName.label')}
            size="xs"
            value={config.outputFileName}
            onChange={(e) => patch({ outputFileName: e.target.value || 'edited.mp4' })}
          />
          <Switch
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.registerProjectFile')}
            label={t('registerProjectFile.label')}
            description={t('registerProjectFile.description')}
            checked={config.registerProjectFile}
            onChange={(e) => patch({ registerProjectFile: e.currentTarget.checked })}
          />
        </SimpleGrid>
      </Fieldset>

      <Fieldset legend={<Group gap="xs" wrap="nowrap"><span>{t('motionLegend')}</span><AskAssistantButton label={t('ask.label')} prompt={assistantPrompt(currentStepIndex, 'VideoCompile', msg('wfSteps.prompts.compileMotion'))} /></Group>}>
        <Switch
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.enableGraphics')}
          label={t('enableGraphics.label')}
          description={t('enableGraphics.description')}
          checked={config.enableGraphics}
          onChange={(e) => {
            const enableGraphics = e.currentTarget.checked;
            patch({
              enableGraphics,
              mode: enableGraphics && config.mode === 'StreamCopy' ? 'Reencode' : config.mode,
            });
          }}
        />
        {config.enableGraphics && (
          <InputRefPicker
            label={t('titlesGraphicsPlan.label')}
            value={config.graphicsPlan ?? { from: 'Previous' }}
            onChange={(graphicsPlan) => patch({ graphicsPlan })}
            priorStepOptions={priorStepOptions}
            allowedSources={['Previous', 'Step']}
          />
        )}
      </Fieldset>

      <Fieldset legend={<Group gap="xs" wrap="nowrap"><span>{t('insertsLegend')}</span><AskAssistantButton label={t('ask.label')} prompt={assistantPrompt(currentStepIndex, 'VideoCompile', msg('wfSteps.prompts.compileInserts'))} /></Group>}>
        <Switch
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.enableInserts')}
          label={t('enableInserts.label')}
          description={t('enableInserts.description')}
          checked={config.enableInserts ?? false}
          onChange={(e) => enableRequiringReencode({ enableInserts: e.currentTarget.checked }, e.currentTarget.checked)}
        />
        {config.enableInserts && (
          <Stack gap="xs">
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.insertContentProjectFileId')}
              label={t('insertContentProjectFileId.label')}
              description={t('insertContentProjectFileId.description')}
              size="xs"
              data={insertableFiles.map((f) => ({ value: f.id, label: f.originalFileName }))}
              value={config.insertContentProjectFileId ?? null}
              onChange={(v) => patch({ insertContentProjectFileId: v })}
              placeholder={insertableFiles.length > 0 ? t('insertContentProjectFileId.none') : t('insertContentProjectFileId.empty')}
              disabled={insertableFiles.length === 0}
              clearable
              searchable
            />
            {(config.insertContents ?? []).map((entry, index) => {
              const update = (changes: Partial<VideoInsertContentAssignment>) => patch({
                insertContents: (config.insertContents ?? []).map((e, i) => (i === index ? { ...e, ...changes } : e)),
              });
              return (
                <Fieldset key={index} legend={`Content ${index + 2}`} variant="filled">
                  <SimpleGrid cols={{ base: 1, sm: 2 }}>
                    <Select
                      label={t('file.label')}
                      size="xs"
                      data={insertableFiles.map((f) => ({ value: f.id, label: f.originalFileName }))}
                      value={entry.projectFileId || null}
                      onChange={(v) => update({ projectFileId: v ?? '' })}
                      searchable
                    />
                    <NumberInput
                      label={t('onlyOnScreens.label')}
                      description={t('onlyOnScreens.description')}
                      size="xs"
                      min={1}
                      value={entry.sourceIndex == null ? '' : entry.sourceIndex + 1}
                      onChange={(v) => update({ sourceIndex: typeof v === 'number' ? Math.max(0, Math.round(v) - 1) : null })}
                    />
                    <TextInput
                      label={t('onlyOnScreen.label')}
                      description={t('onlyOnScreen.description')}
                      size="xs"
                      value={entry.regionId ?? ''}
                      onChange={(e) => update({ regionId: e.target.value.trim() || null })}
                    />
                    <Select
                      label={t('fit.label')}
                      size="xs"
                      data={[{ value: '', label: t('insertFitMode.sameAsAbove') }, ...INSERT_FIT_VALUES.map((v) => ({ value: v, label: t(`insertFitMode.${v}`) }))]}
                      value={entry.fit ?? ''}
                      onChange={(v) => update({ fit: (v || null) as VideoInsertContentAssignment['fit'] })}
                      allowDeselect={false}
                    />
                  </SimpleGrid>
                  <Button
                    size="compact-xs"
                    variant="subtle"
                    color="red"
                    mt="xs"
                    onClick={() => patch({ insertContents: (config.insertContents ?? []).filter((_, i) => i !== index) })}
                  >
                    {t('insertContents.remove')}
                  </Button>
                </Fieldset>
              );
            })}
            <Button
              size="compact-xs"
              variant="light"
              disabled={insertableFiles.length === 0}
              onClick={() => patch({ insertContents: [...(config.insertContents ?? []), { projectFileId: insertableFiles[0]?.id ?? '' }] })}
            >
              {t('insertContents.add')}
            </Button>
            <Text size="xs" c="dimmed">
              {t('insertContents.hint')}
            </Text>
            {!config.graphicsPlan && !config.insertContentProjectFileId && !(config.insertContents?.length) && (
              <Text size="xs" c="orange">
                {t('insertContents.nothingShown')}
              </Text>
            )}
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.insertContentDips')}
              label={t('insertContentDips.label')}
              description={t('insertContentDips.description')}
              size="xs"
              data={INSERT_DIP_VALUES.map((v) => ({ value: v, label: t(`insertDipMode.${v}`) }))}
              value={config.insertContentDips ?? 'Freeze'}
              onChange={(v) => v && patch({ insertContentDips: v as InsertContentDipMode })}
              allowDeselect={false}
            />
            <SimpleGrid cols={{ base: 1, sm: 2 }}>
              <Select
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.insertFit')}
                label={t('insertFit.label')}
                description={t('insertFit.description')}
                size="xs"
                data={INSERT_FIT_VALUES.map((v) => ({ value: v, label: t(`insertFitMode.${v}`) }))}
                value={config.insertFit ?? 'Auto'}
                onChange={(v) => patch({ insertFit: (v ?? 'Auto') as VideoCompileStepConfigValue['insertFit'] })}
                allowDeselect={false}
              />
              <Select
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.insertSurface')}
                label={t('insertSurface.label')}
                description={t('insertSurface.description')}
                size="xs"
                data={[
                  { value: 'Auto', label: t('insertSurface.Auto') },
                  { value: 'Planar', label: t('insertSurface.Planar') },
                  { value: 'Mesh', label: t('insertSurface.Mesh') },
                ]}
                value={config.insertSurface ?? 'Auto'}
                onChange={(v) => patch({ insertSurface: (v ?? 'Auto') as VideoCompileStepConfigValue['insertSurface'] })}
                allowDeselect={false}
              />
            </SimpleGrid>
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.enableInsertMatte')}
              label={t('enableInsertMatte.label')}
              description={t('enableInsertMatte.description')}
              size="xs"
              checked={config.enableInsertMatte ?? true}
              onChange={(e) => patch({ enableInsertMatte: e.currentTarget.checked })}
            />
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.mixInsertAudio')}
              label={t('mixInsertAudio.label')}
              description={t('mixInsertAudio.description')}
              size="xs"
              checked={config.mixInsertAudio ?? false}
              onChange={(e) => patch({ mixInsertAudio: e.currentTarget.checked })}
            />
            {config.mixInsertAudio && (
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.insertAudioGainDb')}
                label={t('insertAudioGainDb.label')}
                description={t('insertAudioGainDb.description')}
                size="xs"
                min={-24}
                max={12}
                value={config.insertAudioGainDb ?? 0}
                onChange={(v) => patch({ insertAudioGainDb: typeof v === 'number' ? Math.min(12, Math.max(-24, Math.round(v))) : 0 })}
              />
            )}
            <SimpleGrid cols={{ base: 1, sm: 2 }}>
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.maxInserts')}
                label={t('maxInserts.label')}
                size="xs"
                min={1}
                value={config.maxInserts ?? 3}
                onChange={(v) => patch({ maxInserts: typeof v === 'number' ? v : 3 })}
              />
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.minInsertConfidence')}
                label={t('minInsertConfidence.label')}
                description={t('minInsertConfidence.description')}
                size="xs"
                min={0}
                max={1}
                step={0.05}
                decimalScale={2}
                value={config.minInsertConfidence ?? 0.5}
                onChange={(v) => patch({ minInsertConfidence: typeof v === 'number' ? v : 0.5 })}
              />
            </SimpleGrid>
          </Stack>
        )}
        {insertAudioProblem && (
          <Alert
            color="orange"
            variant="light"
            p="xs"
            icon={<IconAlertTriangle size={14} />}
            title={t.msg(insertAudioProblem.title)}
          >
            <Text size="xs">{t.msg(insertAudioProblem.body)}</Text>
          </Alert>
        )}
      </Fieldset>

      <Fieldset legend={<Group gap="xs" wrap="nowrap"><span>{t('effects.legend')}</span><AskAssistantButton label={t('ask.label')} prompt={assistantPrompt(currentStepIndex, 'VideoCompile', msg('wfSteps.prompts.compileEffects'))} /></Group>}>
        <Stack gap="xs">
          <Text size="xs" c="dimmed">
            {t('effects.intro')}
          </Text>
          {(config.censorLabels ?? []).length === 0 && regionEffects.length === 0 && !draftOpen && (
            <Text size="xs" c="dimmed">
              {t('effects.empty')}
            </Text>
          )}
          {(config.censorLabels ?? []).map((label, index) => (
            <Paper key={`tracked-${index}`} withBorder p="xs" radius="md">
              <SimpleGrid cols={{ base: 1, sm: 2 }}>
                <ObjectCombobox
                  label={t('object.label')}
                  value={label}
                  method="Tracker"
                  choices={objectChoices}
                  onChange={(text, chosen) => writeObject('Tracker', index, text, chosen)}
                  wrapperProps={fd('config.censorLabels').wrapperProps}
                />
                <Select
                  // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                  {...fd('config.censorStyle')}
                  label={t('censorStyle.label')}
                  size="xs"
                  data={TRACKED_EFFECT_VALUES.map((v) => ({ value: v, label: t(`trackedEffect.${v}`) }))}
                  value={config.censorStyle ?? 'Blur'}
                  onChange={(v) => patch({ censorStyle: (v ?? 'Blur') as CensorStyle })}
                  allowDeselect={false}
                />
              </SimpleGrid>
              <SimpleGrid cols={{ base: 1, sm: 2 }} mt="xs">
                <NumberInput
                  // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                  {...fd('config.censorPadding')}
                  label={t('censorPadding.label')}
                  description={t('censorPadding.description')}
                  size="xs"
                  min={0}
                  max={1}
                  step={0.05}
                  decimalScale={2}
                  value={config.censorPadding ?? 0.25}
                  onChange={(v) => patch({ censorPadding: typeof v === 'number' ? v : 0.25 })}
                />
                <NumberInput
                  // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                  {...fd('config.censorHoldSec')}
                  label={t('censorHoldSec.label')}
                  description={t('censorHoldSec.description')}
                  size="xs"
                  min={0}
                  max={3}
                  step={0.1}
                  decimalScale={1}
                  value={config.censorHoldSec ?? 0.4}
                  onChange={(v) => patch({ censorHoldSec: typeof v === 'number' ? v : 0.4 })}
                />
              </SimpleGrid>
              <Button
                size="compact-xs"
                variant="subtle"
                color="red"
                mt="xs"
                onClick={() => removeObjectRow('Tracker', index)}
              >
                {t('effects.remove')}
              </Button>
            </Paper>
          ))}
          {regionEffects.map((entry, index) => {
            const update = (changes: Partial<VideoRegionEffect>) => patch({
              regionEffects: regionEffects.map((e, i) => (i === index ? { ...e, ...changes } : e)),
            });
            return (
              <Paper key={`segmentation-${index}`} withBorder p="xs" radius="md">
                <SimpleGrid cols={{ base: 1, sm: 2 }}>
                  <ObjectCombobox
                    label={t('object.label')}
                    value={entry.target ?? ''}
                    method="Segmentation"
                    choices={objectChoices}
                    onChange={(text, chosen) => writeObject('Segmentation', index, text, chosen)}
                    wrapperProps={fd('config.regionEffects').wrapperProps}
                  />
                  <Select
                    label={t('effect.label')}
                    size="xs"
                    data={REGION_EFFECT_VALUES.map((v) => ({ value: v, label: t(`regionEffect.${v}`) }))}
                    value={entry.effect}
                    onChange={(v) => v && update({ effect: v as RegionEffect })}
                    allowDeselect={false}
                  />
                </SimpleGrid>
                <SimpleGrid cols={{ base: 1, sm: 2 }} mt="xs">
                  <NumberInput
                    label={t('strength.label')}
                    description={t('strength.description')}
                    size="xs"
                    min={0}
                    max={2}
                    step={0.1}
                    decimalScale={2}
                    value={entry.strength ?? 1}
                    onChange={(v) => update({ strength: typeof v === 'number' ? v : 1 })}
                  />
                  <NumberInput
                    label={t('softEdgePx.label')}
                    description={t('softEdgePx.description')}
                    size="xs"
                    min={0}
                    max={64}
                    step={1}
                    value={entry.featherPx ?? 0}
                    onChange={(v) => update({ featherPx: typeof v === 'number' ? v : 0 })}
                  />
                  <NumberInput
                    label={t('opacity.label')}
                    description={t('opacity.description')}
                    size="xs"
                    min={0}
                    max={1}
                    step={0.05}
                    decimalScale={2}
                    value={entry.opacity ?? 1}
                    onChange={(v) => update({ opacity: typeof v === 'number' ? v : 1 })}
                  />
                  <Switch
                    label={t('everythingExceptThis.label')}
                    description={t('everythingExceptThis.description')}
                    size="xs"
                    checked={entry.invert ?? false}
                    onChange={(e) => update({ invert: e.currentTarget.checked })}
                  />
                </SimpleGrid>
                {!(entry.target ?? '').trim() && (
                  <Text size="xs" c="orange" mt="xs">
                    {t('effects.invalid')}
                  </Text>
                )}
                <Button
                  size="compact-xs"
                  variant="subtle"
                  color="red"
                  mt="xs"
                  onClick={() => removeObjectRow('Segmentation', index)}
                >
                  {t('effects.remove')}
                </Button>
              </Paper>
            );
          })}
          {draftOpen && (
            <Paper withBorder p="xs" radius="md">
              <ObjectCombobox
                label={t('object.label')}
                value=""
                method="Tracker"
                choices={objectChoices}
                onChange={addObjectRow}
              />
              <Button
                size="compact-xs"
                variant="subtle"
                color="red"
                mt="xs"
                onClick={() => setDraftOpen(false)}
              >
                {t('effects.cancel')}
              </Button>
            </Paper>
          )}
          {!draftOpen && (
            <Button
              size="compact-xs"
              variant="light"
              leftSection={<IconPlus size={14} />}
              disabled={!canAddObject}
              onClick={() => setDraftOpen(true)}
            >
              {t('effects.add')}
            </Button>
          )}
          {regionEffects.length >= MAX_REGION_EFFECTS && (
            <Text size="xs" c="dimmed">
              {t('effects.limit', { max: MAX_REGION_EFFECTS })}
            </Text>
          )}
          {regionEffects.length > MAX_REGION_EFFECTS && (
            <Text size="xs" c="orange">
              {t('effects.limitExceeded', { count: regionEffects.length, max: MAX_REGION_EFFECTS })}
            </Text>
          )}
          {regionEffects.length > 0 && config.mode === 'StreamCopy' && (
            <Text size="xs" c="orange">
              {t('effects.requiresReencode')}
            </Text>
          )}
          {(config.censorLabels ?? []).length > 0 && (
            <Text size="xs" c="dimmed">
              {t('effects.trackedShared')}
            </Text>
          )}
          {(regionEffects.length > 0 || (config.censorLabels ?? []).length > 0) && (
            <Text size="xs" c="dimmed">
              {t('effects.order')}
            </Text>
          )}
        </Stack>
      </Fieldset>

      <Fieldset legend={<Group gap="xs" wrap="nowrap"><span>{t('colourLegend')}</span><AskAssistantButton label={t('ask.label')} prompt={assistantPrompt(currentStepIndex, 'VideoCompile', msg('wfSteps.prompts.compileColour'))} /></Group>}>
        <Switch
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.enableColorGrade')}
          label={t('enableColorGrade.label')}
          description={t('enableColorGrade.description')}
          checked={config.enableColorGrade ?? false}
          onChange={(e) => {
            const on = e.currentTarget.checked;
            enableRequiringReencode(
              { enableColorGrade: on, colorGradePlan: on ? (config.colorGradePlan ?? { from: 'Previous' }) : config.colorGradePlan },
              on,
            );
          }}
        />
        {config.enableColorGrade && (
          <InputRefPicker
            label={t('colourLookFrom.label')}
            value={config.colorGradePlan ?? { from: 'Previous' }}
            onChange={(colorGradePlan) => patch({ colorGradePlan })}
            priorStepOptions={priorStepOptions}
            allowedSources={['Previous', 'Step']}
          />
        )}
      </Fieldset>

      <Fieldset legend={<Group gap="xs" wrap="nowrap"><span>{t('musicLegend')}</span><AskAssistantButton label={t('ask.label')} prompt={assistantPrompt(currentStepIndex, 'VideoCompile', msg('wfSteps.prompts.compileMusic'))} /></Group>}>
        <Switch
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.enableMusic')}
          label={t('enableMusic.label')}
          description={t('enableMusic.description')}
          checked={config.enableMusic ?? false}
          onChange={(e) => enableRequiringReencode({ enableMusic: e.currentTarget.checked }, e.currentTarget.checked)}
        />
        {config.enableMusic && <NoAudioFilesNotice projectId={projectId} kind="music" />}
        <Switch
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.muteSourceAudio')}
          mt="xs"
          label={t('muteSourceAudio.label')}
          description={t('muteSourceAudio.description')}
          checked={config.muteSourceAudio ?? false}
          onChange={(e) => patch({ muteSourceAudio: e.currentTarget.checked })}
        />
        {config.enableMusic && (
          <Stack gap="xs">
            <Text size="xs" c="dimmed">
              {t('musicTrackProjectFileId.hint')}
            </Text>
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.musicTrackProjectFileId')}
              label={t('musicTrackProjectFileId.label')}
              size="xs"
              data={audioFiles.map((f) => ({ value: f.id, label: f.originalFileName }))}
              value={config.musicTrackProjectFileId ?? null}
              onChange={(v) => patch({ musicTrackProjectFileId: v })}
              placeholder={audioFiles.length > 0 ? t('musicTrackProjectFileId.letStep') : t('musicTrackProjectFileId.empty')}
              disabled={audioFiles.length === 0}
              clearable
              searchable
            />
            {!config.musicTrackProjectFileId && (
              <InputRefPicker
                label={t('musicChoiceFrom.label')}
                value={config.musicPlan ?? { from: 'Previous' }}
                onChange={(musicPlan) => patch({ musicPlan })}
                priorStepOptions={priorStepOptions}
                allowedSources={['Previous', 'Step']}
              />
            )}
            <SimpleGrid cols={{ base: 1, sm: 2 }}>
              <Select
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.musicDucking')}
                label={t('musicDucking.label')}
                description={t('musicDucking.description')}
                size="xs"
                value={config.musicDucking ?? 'SpeechEnvelope'}
                data={[
                  { value: 'SpeechEnvelope', label: t('musicDuckingMode.SpeechEnvelope') },
                  { value: 'Off', label: t('musicDuckingMode.Off') },
                ]}
                onChange={(v) => v && patch({ musicDucking: v as MusicDuckingMode })}
              />
              <Select
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.musicFitPolicy')}
                label={t('musicFitPolicy.label')}
                size="xs"
                value={config.musicFitPolicy ?? 'LoopToFit'}
                data={[
                  { value: 'LoopToFit', label: t('musicFitMode.LoopToFit') },
                  { value: 'PlayOnce', label: t('musicFitMode.PlayOnce') },
                ]}
                onChange={(v) => v && patch({ musicFitPolicy: v as MusicFit })}
              />
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.musicFadeInMs')}
                label={t('musicFadeInMs.label')}
                size="xs"
                min={0}
                value={config.musicFadeInMs ?? 1500}
                onChange={(v) => patch({ musicFadeInMs: typeof v === 'number' ? v : 1500 })}
              />
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.musicFadeOutMs')}
                label={t('musicFadeOutMs.label')}
                size="xs"
                min={0}
                value={config.musicFadeOutMs ?? 2500}
                onChange={(v) => patch({ musicFadeOutMs: typeof v === 'number' ? v : 2500 })}
              />
              <Select
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.beatSync')}
                label={t('beatSync.label')}
                description={t('beatSync.description')}
                size="xs"
                data={BEAT_SYNC_VALUES.map((v) => ({ value: v, label: t(`beatSyncMode.${v}`) }))}
                value={config.beatSync ?? 'Off'}
                onChange={(v) => v && patch({ beatSync: v as VideoBeatSync })}
                allowDeselect={false}
              />
            </SimpleGrid>
          </Stack>
        )}
      </Fieldset>

      <Fieldset legend={<Group gap="xs" wrap="nowrap"><span>{t('sfxLegend')}</span><AskAssistantButton label={t('ask.label')} prompt={assistantPrompt(currentStepIndex, 'VideoCompile', msg('wfSteps.prompts.compileSfx'))} /></Group>}>
        <Switch
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.enableSfx')}
          label={t('enableSfx.label')}
          description={t('enableSfx.description')}
          checked={config.enableSfx ?? false}
          onChange={(e) => {
            const on = e.currentTarget.checked;
            enableRequiringReencode(
              { enableSfx: on, sfxPlan: on ? (config.sfxPlan ?? { from: 'Previous' }) : config.sfxPlan },
              on,
            );
          }}
        />
        {config.enableSfx && <NoAudioFilesNotice projectId={projectId} kind="sfx" />}
        {config.enableSfx && (
          <Stack gap="xs">
            <InputRefPicker
              label={t('soundEffectsFrom.label')}
              value={config.sfxPlan ?? { from: 'Previous' }}
              onChange={(sfxPlan) => patch({ sfxPlan })}
              priorStepOptions={priorStepOptions}
              allowedSources={['Previous', 'Step']}
            />
            <SimpleGrid cols={{ base: 1, sm: 2 }}>
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.maxSfxCues')}
                label={t('maxSfxCues.label')}
                size="xs"
                min={1}
                value={config.maxSfxCues ?? 8}
                onChange={(v) => patch({ maxSfxCues: typeof v === 'number' ? v : 8 })}
              />
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.maxSfxCueSeconds')}
                label={t('maxSfxCueSeconds.label')}
                description={t('maxSfxCueSeconds.description')}
                size="xs"
                min={0.25}
                step={0.25}
                decimalScale={2}
                value={config.maxSfxCueSeconds ?? 4.0}
                onChange={(v) => patch({ maxSfxCueSeconds: typeof v === 'number' ? v : 4.0 })}
              />
            </SimpleGrid>
          </Stack>
        )}
      </Fieldset>

      <Fieldset legend={<Group gap="xs" wrap="nowrap"><span>{t('narration.legend')}</span><AskAssistantButton label={t('ask.label')} prompt={assistantPrompt(currentStepIndex, 'VideoCompile', msg('wfSteps.prompts.compileNarration'))} /></Group>}>
        <Switch
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.enableVoiceover')}
          label={t('enableVoiceover.label')}
          description={t('enableVoiceover.description')}
          checked={config.enableVoiceover ?? false}
          onChange={(e) => enableRequiringReencode({ enableVoiceover: e.currentTarget.checked }, e.currentTarget.checked)}
        />
        {config.enableVoiceover && (
          <Stack gap="xs">
            <StepOrderSelect
              label={t('voiceoverStep.label')}
              description={t('voiceoverStep.description')}
              size="xs"
              steps={allSteps}
              currentIndex={currentStepIndex}
              scope="before"
              value={config.voiceoverStepOrder}
              onChange={(n) => patch({ voiceoverStepOrder: n })}
              wrapperProps={fd('config.voiceoverStepOrder').wrapperProps}
            />
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.voiceoverMode')}
              label={t('voiceoverMode.label')}
              description={t('voiceoverMode.description')}
              size="xs"
              data={VOICEOVER_MODE_VALUES.map((v) => ({ value: v, label: t(`voiceoverModeOption.${v}`) }))}
              value={config.voiceoverMode ?? 'OverMusic'}
              onChange={(v) => patch({ voiceoverMode: (v as VoiceoverMode) ?? 'OverMusic' })}
              allowDeselect={false}
            />
            {config.voiceoverMode === 'DuckDialogue' && (
              <SimpleGrid cols={{ base: 1, sm: 2 }}>
                <NumberInput
                  // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                  {...fd('config.voiceoverDuckDb')}
                  label={t('voiceoverDuckDb.label')}
                  size="xs"
                  min={-40}
                  max={0}
                  value={config.voiceoverDuckDb ?? -12}
                  onChange={(v) => patch({ voiceoverDuckDb: typeof v === 'number' ? v : -12 })}
                />
                <NumberInput
                  // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                  {...fd('config.voiceoverDuckRampMs')}
                  label={t('voiceoverDuckRampMs.label')}
                  size="xs"
                  min={0}
                  max={1000}
                  value={config.voiceoverDuckRampMs ?? 120}
                  onChange={(v) => patch({ voiceoverDuckRampMs: typeof v === 'number' ? v : 120 })}
                />
              </SimpleGrid>
            )}
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.enableCaptions')}
              label={t('enableCaptions.label')}
              description={t('enableCaptions.description')}
              checked={config.enableCaptions ?? false}
              onChange={(e) => patch({ enableCaptions: e.currentTarget.checked })}
            />
            {config.enableCaptions && <CaptionStyleFields config={config} patch={patch} />}
          </Stack>
        )}
        <Switch
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.enablePickups')}
          label={t('enablePickups.label')}
          description={t('enablePickups.description')}
          checked={config.enablePickups ?? false}
          onChange={(e) => enableRequiringReencode({ enablePickups: e.currentTarget.checked }, e.currentTarget.checked)}
        />
        {config.enablePickups && (
          <StepOrderSelect
            label={t('pickupVoiceoverStep.label')}
            description={t('pickupVoiceoverStep.description')}
            size="xs"
            steps={allSteps}
            currentIndex={currentStepIndex}
            scope="before"
            value={config.pickupVoiceoverStepOrder}
            onChange={(n) => patch({ pickupVoiceoverStepOrder: n })}
            wrapperProps={fd('config.pickupVoiceoverStepOrder').wrapperProps}
          />
        )}
      </Fieldset>

      <Fieldset legend={<Group gap="xs" wrap="nowrap"><span>{t('generatedLegend')}</span><AskAssistantButton label={t('ask.label')} prompt={assistantPrompt(currentStepIndex, 'VideoCompile', msg('wfSteps.prompts.compileGenerated'))} /></Group>}>
        <Switch
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.enableGeneratedClips')}
          label={t('enableGeneratedClips.label')}
          description={t('enableGeneratedClips.description')}
          checked={config.enableGeneratedClips ?? false}
          onChange={(e) => enableRequiringReencode({ enableGeneratedClips: e.currentTarget.checked }, e.currentTarget.checked)}
        />
        {config.enableGeneratedClips && (
          <Stack gap="xs">
            <InputRefPicker
              label={t('generatedClipsFrom.label')}
              value={config.generatedClips ?? { from: 'Previous' }}
              onChange={(generatedClips) => patch({ generatedClips })}
              priorStepOptions={priorStepOptions}
              allowedSources={['Previous', 'Step']}
            />
            <SimpleGrid cols={{ base: 1, sm: 2 }}>
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.maxGeneratedClips')}
                label={t('maxGeneratedClips.label')}
                description={t('maxGeneratedClips.description')}
                size="xs"
                min={1}
                value={config.maxGeneratedClips ?? 8}
                onChange={(v) => patch({ maxGeneratedClips: typeof v === 'number' ? v : 8 })}
              />
            </SimpleGrid>
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.takeSelection')}
              label={t('takeSelection.label')}
              description={t('takeSelection.description')}
              checked={Boolean(config.takeSelection)}
              onChange={(e) => patch({ takeSelection: e.currentTarget.checked ? { from: 'Previous' } : null })}
            />
            {config.takeSelection && (
              <InputRefPicker
                label={t('takeSelectionStep.label')}
                value={config.takeSelection}
                onChange={(takeSelection) => patch({ takeSelection })}
                priorStepOptions={priorStepOptions}
                allowedSources={['Previous', 'Step']}
              />
            )}
          </Stack>
        )}
      </Fieldset>

      <Fieldset legend={<Group gap="xs" wrap="nowrap"><span>{t('transitionsLegend')}</span><AskAssistantButton label={t('ask.label')} prompt={assistantPrompt(currentStepIndex, 'VideoCompile', msg('wfSteps.prompts.compileTransitions'))} /></Group>}>
        <Select
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.transitionPolicy')}
          label={t('transitionPolicy.label')}
          description={t('transitionPolicy.description')}
          size="xs"
          data={TRANSITION_POLICY_VALUES.map((v) => ({ value: v, label: t(`transitionPolicyMode.${v}`) }))}
          value={config.transitionPolicy ?? 'Off'}
          onChange={(v) => v && patch({ transitionPolicy: v as VideoTransitionPolicy })}
        />

        <Text size="xs" fw={500}>{t('fades.title')}</Text>
        <SimpleGrid cols={{ base: 1, sm: 2, md: 4 }}>
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.programFadeInMs')}
            label={t('programFadeInMs.label')}
            size="xs"
            min={0}
            value={config.programFadeInMs ?? 0}
            onChange={(v) => patch({ programFadeInMs: typeof v === 'number' ? v : 0 })}
          />
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.programFadeOutMs')}
            label={t('programFadeOutMs.label')}
            size="xs"
            min={0}
            value={config.programFadeOutMs ?? 0}
            onChange={(v) => patch({ programFadeOutMs: typeof v === 'number' ? v : 0 })}
          />
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.programAudioFadeInMs')}
            label={t('programAudioFadeInMs.label')}
            size="xs"
            min={0}
            value={config.programAudioFadeInMs ?? 0}
            onChange={(v) => patch({ programAudioFadeInMs: typeof v === 'number' ? v : 0 })}
          />
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.programAudioFadeOutMs')}
            label={t('programAudioFadeOutMs.label')}
            size="xs"
            min={0}
            value={config.programAudioFadeOutMs ?? 0}
            onChange={(v) => patch({ programAudioFadeOutMs: typeof v === 'number' ? v : 0 })}
          />
        </SimpleGrid>
        <Select
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.programFadeColor')}
          label={t('programFadeColor.label')}
          size="xs"
          data={PROGRAM_FADE_COLOR_OPTIONS}
          value={config.programFadeColor ?? 'black'}
          onChange={(v) => v && patch({ programFadeColor: v })}
        />
      </Fieldset>

      <AdvancedSection description={t('encodingCutTiming.description')}>
        <Fieldset legend={<Group gap="xs" wrap="nowrap"><span>{t('encoding.legend')}</span></Group>}>
          <Select
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.mode')}
            label={t('mode.label')}
            size="xs"
            value={config.mode}
            data={[
              { value: 'Reencode', label: t('encodingMode.Reencode') },
              { value: 'StreamCopy', label: t('encodingMode.StreamCopy') },
            ]}
            onChange={(v) => {
              if (!v) return;
              const mode = v as VideoCompileMode;
              patch({
                mode,
                allowKeyframeSnapping: mode === 'StreamCopy' ? config.allowKeyframeSnapping : false,
                // Graphics overlays require Re-encode mode (drawtext/drawbox have no stream-copy equivalent).
                enableGraphics: mode === 'StreamCopy' ? false : config.enableGraphics,
              });
            }}
          />
          {config.mode === 'StreamCopy' && (
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.allowKeyframeSnapping')}
              label={t('allowKeyframeSnapping.label')}
              description={t('allowKeyframeSnapping.description')}
              checked={config.allowKeyframeSnapping}
              onChange={(e) => patch({ allowKeyframeSnapping: e.currentTarget.checked })}
            />
          )}

          <SimpleGrid cols={{ base: 1, sm: 2, md: 4 }}>
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.videoCodec')}
              label={t('videoCodec.label')}
              description={t('videoCodec.description')}
              size="xs"
              data={VIDEO_CODEC_VALUES.map((v) => ({ value: v, label: t(`videoCodecMode.${v}`) }))}
              value={config.videoCodec}
              onChange={(v) => v && patch({ videoCodec: v })}
              disabled={config.mode === 'StreamCopy'}
            />
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.audioCodec')}
              label={t('audioCodec.label')}
              size="xs"
              data={AUDIO_CODEC_OPTIONS}
              value={config.audioCodec}
              onChange={(v) => v && patch({ audioCodec: v })}
              disabled={config.mode === 'StreamCopy'}
            />
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.preset')}
              label={t('preset.label')}
              size="xs"
              data={PRESET_OPTIONS}
              value={config.preset}
              onChange={(v) => v && patch({ preset: v })}
              disabled={config.mode === 'StreamCopy'}
            />
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.crf')}
              label={t('crf.label')}
              description={t('crf.description')}
              size="xs"
              min={0}
              max={51}
              value={config.crf}
              onChange={(v) => patch({ crf: typeof v === 'number' ? Math.min(51, Math.max(0, v)) : 20 })}
              disabled={config.mode === 'StreamCopy'}
            />
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.rateControl')}
              label={t('rateControl.label')}
              description={t('rateControl.description')}
              size="xs"
              data={RATE_CONTROLS.map((v) => ({ value: v, label: t(`rateControl.options.${v}`) }))}
              value={config.rateControl ?? 'Quality'}
              allowDeselect={false}
              onChange={(v) => {
                if (!v) return;
                patch(v === 'Quality' ? { rateControl: null, videoBitrateKbps: null } : { rateControl: v as TimelineRenderRateControl, videoBitrateKbps: config.videoBitrateKbps ?? 8000 });
              }}
              disabled={config.mode === 'StreamCopy'}
            />
            {config.rateControl && config.rateControl !== 'Quality' && (
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.videoBitrateKbps')}
                label={t('videoBitrateKbps.label')}
                size="xs"
                min={200}
                max={100000}
                step={500}
                value={config.videoBitrateKbps ?? 8000}
                onChange={(v) => patch({ videoBitrateKbps: typeof v === 'number' ? Math.min(100000, Math.max(200, Math.round(v))) : 8000 })}
                disabled={config.mode === 'StreamCopy'}
              />
            )}
          </SimpleGrid>
        </Fieldset>

        <Fieldset legend={<Group gap="xs" wrap="nowrap"><span>{t('cutTiming.legend')}</span></Group>}>
          <SimpleGrid cols={{ base: 1, sm: 2, md: 4 }}>
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.prePaddingMs')}
              label={t('prePaddingMs.label')}
              size="xs"
              min={0}
              value={config.prePaddingMs}
              onChange={(v) => patch({ prePaddingMs: typeof v === 'number' ? v : 80 })}
            />
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.postPaddingMs')}
              label={t('postPaddingMs.label')}
              size="xs"
              min={0}
              value={config.postPaddingMs}
              onChange={(v) => patch({ postPaddingMs: typeof v === 'number' ? v : 120 })}
            />
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.minSegmentMs')}
              label={t('minSegmentMs.label')}
              size="xs"
              min={0}
              value={config.minSegmentMs}
              onChange={(v) => patch({ minSegmentMs: typeof v === 'number' ? v : 250 })}
            />
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.maxSegments')}
              label={t('maxSegments.label')}
              size="xs"
              min={1}
              value={config.maxSegments}
              onChange={(v) => patch({ maxSegments: typeof v === 'number' ? v : 200 })}
            />
          </SimpleGrid>
        </Fieldset>

        <TextInput
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.analysisStepResultId')}
          label={t('analysisStepResultId.label')}
          placeholder={t('analysisStepResultId.placeholder')}
          size="xs"
          value={config.analysisStepResultId ?? ''}
          onChange={(e) => patch({ analysisStepResultId: e.target.value || null })}
        />

        <Text size="xs" fw={500} mt="sm">{t('transitionTiming.title')}</Text>
        <Text size="xs" c="dimmed" mb="xs">
          {t('transitionTiming.hint')}
        </Text>
        <SimpleGrid cols={{ base: 1, sm: 2, md: 3 }}>
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.audioSeamRampMs')}
            label={t('audioSeamRampMs.label')}
            description={t('audioSeamRampMs.description')}
            size="xs"
            min={0}
            value={config.audioSeamRampMs ?? 24}
            onChange={(v) => patch({ audioSeamRampMs: typeof v === 'number' ? v : 24 })}
          />
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.softCutMs')}
            label={t('softCutMs.label')}
            description={t('softCutMs.description')}
            size="xs"
            min={0}
            value={config.softCutMs ?? 200}
            onChange={(v) => patch({ softCutMs: typeof v === 'number' ? v : 200 })}
          />
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.dissolveMs')}
            label={t('dissolveMs.label')}
            description={t('dissolveMs.description')}
            size="xs"
            min={0}
            value={config.dissolveMs ?? 500}
            onChange={(v) => patch({ dissolveMs: typeof v === 'number' ? v : 500 })}
          />
        </SimpleGrid>
        <SimpleGrid cols={{ base: 1, sm: 2, md: 3 }} mt="xs">
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.dipToBlackMs')}
            label={t('dipToBlackMs.label')}
            description={t('dipToBlackMs.description')}
            size="xs"
            min={0}
            value={config.dipToBlackMs ?? 600}
            onChange={(v) => patch({ dipToBlackMs: typeof v === 'number' ? v : 600 })}
          />
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.dipCutMs')}
            label={t('dipCutMs.label')}
            description={t('dipCutMs.description')}
            size="xs"
            min={0}
            value={config.dipCutMs ?? 220}
            onChange={(v) => patch({ dipCutMs: typeof v === 'number' ? v : 220 })}
          />
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.sectionBreakGapMs')}
            label={t('sectionBreakGapMs.label')}
            description={t('sectionBreakGapMs.description')}
            size="xs"
            min={0}
            value={config.sectionBreakGapMs ?? 8000}
            onChange={(v) => patch({ sectionBreakGapMs: typeof v === 'number' ? v : 8000 })}
          />
        </SimpleGrid>
        <SimpleGrid cols={{ base: 1, sm: 2, md: 3 }} mt="xs">
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.maxTransitionMs')}
            label={t('maxTransitionMs.label')}
            description={t('maxTransitionMs.description')}
            size="xs"
            min={0}
            value={config.maxTransitionMs ?? 1200}
            onChange={(v) => patch({ maxTransitionMs: typeof v === 'number' ? v : 1200 })}
          />
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.maxTransitionRatioPct')}
            label={t('maxTransitionRatioPct.label')}
            description={t('maxTransitionRatioPct.description')}
            size="xs"
            min={0}
            max={100}
            value={config.maxTransitionRatioPct ?? 35}
            onChange={(v) => patch({ maxTransitionRatioPct: typeof v === 'number' ? v : 35 })}
          />
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.maxTransitionSegments')}
            label={t('maxTransitionSegments.label')}
            description={t('maxTransitionSegments.description')}
            size="xs"
            min={0}
            value={config.maxTransitionSegments ?? 80}
            onChange={(v) => patch({ maxTransitionSegments: typeof v === 'number' ? v : 80 })}
          />
        </SimpleGrid>
        <Fieldset legend={t('checksBeforeEncoding.legend')}>
          <Text size="xs" c="dimmed">{t('checksHint')}</Text>
        <SimpleGrid cols={{ base: 1, sm: 2, md: 4 }}>
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.expect')}
            label={t('expect.label')}
            min={0}
            size="xs"
            value={config.expect?.minOutputSeconds ?? undefined}
            onChange={(v) => patch({ expect: { ...config.expect, minOutputSeconds: typeof v === 'number' ? v : null } })}
          />
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.expect')}
            label={t('expect_maxOutputSeconds.label')}
            min={0}
            size="xs"
            value={config.expect?.maxOutputSeconds ?? undefined}
            onChange={(v) => patch({ expect: { ...config.expect, maxOutputSeconds: typeof v === 'number' ? v : null } })}
          />
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.expect')}
            label={t('expect_minRetainedRatio.label')}
            description={t('expect.description')}
            min={0}
            max={1}
            step={0.01}
            decimalScale={2}
            size="xs"
            value={config.expect?.minRetainedRatio ?? undefined}
            onChange={(v) => patch({ expect: { ...config.expect, minRetainedRatio: typeof v === 'number' ? v : null } })}
          />
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.expect')}
            label={t('expect_maxRetainedRatio.label')}
            min={0}
            max={1}
            step={0.01}
            decimalScale={2}
            size="xs"
            value={config.expect?.maxRetainedRatio ?? undefined}
            onChange={(v) => patch({ expect: { ...config.expect, maxRetainedRatio: typeof v === 'number' ? v : null } })}
          />
        </SimpleGrid>
        </Fieldset>
        <VideoCompileReframeFields
          config={config}
          patch={patch}
          allSteps={allSteps}
          currentStepIndex={currentStepIndex}
          projectId={projectId}
        />
      </AdvancedSection>
    </Stack>
  );
}
