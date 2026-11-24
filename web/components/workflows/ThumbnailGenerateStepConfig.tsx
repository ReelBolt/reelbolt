'use client';

import {
  Stack, Group, Select, TextInput, NumberInput, Switch, Text, SimpleGrid, Fieldset,
} from '@mantine/core';
import { useT, msg } from '@/i18n';
import type {
  ThumbnailGenerateStepConfig as ThumbnailGenerateStepConfigValue,
  ThumbnailSelection,
  ThumbnailFit,
  ThumbnailOverlayPosition,
} from '@/lib/types/workflow';
import type { StepData } from './WorkflowStepList';
import { useFd, AskAssistantButton, assistantPrompt, AdvancedSection } from './config-kit';

/** Mirrors `NamedOverlayFontColors` in the C# executor. A fixed list, never free text: the value reaches ffmpeg's filter STRING (plan R11). */
const FONT_COLOR_VALUES = ['white', 'black', 'yellow'];

/** Mirrors `AllowedBoxColors` in the C# executor — same fixed-list discipline as the font colour above. */
const BOX_COLOR_VALUES = ['black@0.45', 'black@0.6', 'white@0.4', 'none'];
// Labels: wfSteps.thumbnailGenerate.fontColor.<value> / boxColor.<value> ('@' and '.' become '_' in the key)
const boxKey = (value: string) => value.replace(/[@.]/g, '_');

/** Mirrors `ThumbnailSelection`. */
const SELECTION_VALUES: ThumbnailSelection[] = ['Auto', 'AtTime'];

/** Mirrors `ThumbnailFit`. */
const FIT_VALUES: ThumbnailFit[] = ['Cover', 'Contain'];

/** Mirrors `ThumbnailOverlayPosition`. */
const POSITION_VALUES: ThumbnailOverlayPosition[] = ['None', 'LowerThird', 'UpperThird', 'Center'];

/** Builds a sensible default config for a freshly-added ThumbnailGenerate step. Mirrors the C# record defaults exactly. */
export function createDefaultThumbnailGenerateStepConfig(): ThumbnailGenerateStepConfigValue {
  return {
    version: 1,
    analysisStepOrder: 1,
    analysisStepResultId: null,
    selection: 'Auto',
    atTimeSec: 0,
    sourceIndex: 0,
    width: 1280,
    height: 720,
    fit: 'Cover',
    jpegQuality: 4,
    outputFileName: 'thumbnail.jpg',
    registerProjectFile: true,
    maxCandidates: 24,
    minShotSeconds: 0.4,
    preferBestTake: true,
    overlayText: null,
    overlayPosition: 'None',
    overlayFontSizePct: 9,
    overlayFontColor: 'white',
    overlayBoxColor: 'black@0.45',
    maxOverlayChars: 90,
  };
}

interface ThumbnailGenerateStepConfigProps {
  config: ThumbnailGenerateStepConfigValue;
  onChange: (config: ThumbnailGenerateStepConfigValue) => void;
  allSteps?: StepData[];
  currentStepIndex?: number;
}

/**
 * Configuration form for a ThumbnailGenerate step (deterministic frame extraction — no model call).
 * Mirrors the backend `ThumbnailGenerateStepConfig` record field-for-field.
 */
export function ThumbnailGenerateStepConfig({
  config, onChange, allSteps = [], currentStepIndex = 0,
}: ThumbnailGenerateStepConfigProps) {
  const t = useT('wfSteps.thumbnailGenerate');
  const fd = useFd();

  // Only a Watch footage step can supply the candidates: the whole `Auto` strategy is built on that
  // step's measurements, and `AtTime` still reads the clip list it produced. Filtered exactly like
  // VideoCompile's own footage picker, including the step-number fallback for a workflow whose
  // analysis step is not in this list (an imported template, say).
  const analyzeStepOptions = allSteps
    .slice(0, currentStepIndex)
    .map((previousStep, index) => ({ step: previousStep, stepOrder: index + 1 }))
    .filter(({ step }) => step.stepType === 'VideoAnalyze')
    .map(({ step, stepOrder }) => ({
      value: String(stepOrder),
      label: `${stepOrder}. ${step.label?.trim() || t('stepFallback', { n: stepOrder })}`,
    }));

  const patch = (updates: Partial<ThumbnailGenerateStepConfigValue>) => onChange({ ...config, ...updates });

  // A config may carry a colour this build does not offer (a hex value, written by an older editor or
  // the assistant). Shown as its own option rather than blanking the field, so the author sees what
  // the step will actually draw instead of an empty picker.
  const fontColorBase = FONT_COLOR_VALUES.map((v) => ({ value: v, label: t(`fontColor.${v}`) }));
  const boxColorBase = BOX_COLOR_VALUES.map((v) => ({ value: v, label: t(`boxColor.${boxKey(v)}`) }));
  const fontColorOptions = FONT_COLOR_VALUES.includes(config.overlayFontColor)
    ? fontColorBase
    : [...fontColorBase, { value: config.overlayFontColor, label: t('setElsewhere', { value: config.overlayFontColor }) }];
  const boxColorOptions = BOX_COLOR_VALUES.includes(config.overlayBoxColor)
    ? boxColorBase
    : [...boxColorBase, { value: config.overlayBoxColor, label: t('setElsewhere', { value: config.overlayBoxColor }) }];

  const overlayText = config.overlayText?.trim() ?? '';

  // A cross-execution analysis reference is the one way a step with no Watch footage step ABOVE it
  // still has candidates, so the "add one" notice must not fire on a perfectly runnable step.
  const hasFootage = analyzeStepOptions.length > 0 || Boolean(config.analysisStepResultId);

  return (
    <Stack gap="md" onClick={(e) => e.stopPropagation()}>
      <Group justify="space-between" align="center">
        <Text size="xs" c="dimmed">{t('intro')}</Text>
        <AskAssistantButton prompt={assistantPrompt(currentStepIndex, 'ThumbnailGenerate', msg('wfSteps.prompts.thumbnailGenerate'))} />
      </Group>

      <Fieldset legend={t('frame.legend')}>
        <Stack gap="xs">
          <Select
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.selection')}
            label={t('frame.how')}
            size="xs"
            data={SELECTION_VALUES.map((v) => ({ value: v, label: t(`selection.${v}`) }))}
            value={config.selection}
            onChange={(v) => v && patch({ selection: v as ThumbnailSelection })}
            allowDeselect={false}
          />
          {config.selection === 'Auto' ? (
            <Text size="xs" c="dimmed">
              {t.rich('frame.autoHint', { b: (chunks) => <b>{chunks}</b> })}
            </Text>
          ) : (
            <Text size="xs" c="dimmed">
              {t('frame.atTimeHint')}
            </Text>
          )}

          {analyzeStepOptions.length > 0 ? (
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.analysisStepOrder')}
              label={t('frame.footage.label')}
              description={t('frame.footage.description')}
              size="xs"
              data={analyzeStepOptions}
              value={String(config.analysisStepOrder)}
              onChange={(v) => v && patch({ analysisStepOrder: Number(v) })}
            />
          ) : (
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.analysisStepOrder')}
              label={t('frame.footageNumber.label')}
              description={t('frame.footageNumber.description')}
              size="xs"
              min={1}
              value={config.analysisStepOrder}
              onChange={(v) => patch({ analysisStepOrder: typeof v === 'number' ? v : 1 })}
            />
          )}
          {!hasFootage && (
            <Text size="xs" c="orange">
              {t('frame.noFootage')}
            </Text>
          )}

          {config.selection === 'AtTime' && (
            <SimpleGrid cols={{ base: 1, sm: 2 }}>
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.atTimeSec')}
                label={t('frame.moment.label')}
                description={t('frame.moment.description')}
                size="xs"
                min={0}
                step={0.1}
                decimalScale={2}
                value={config.atTimeSec}
                onChange={(v) => patch({ atTimeSec: typeof v === 'number' ? Math.max(0, v) : 0 })}
              />
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.sourceIndex')}
                label={t('frame.clip.label')}
                description={t('frame.clip.description')}
                size="xs"
                min={1}
                value={config.sourceIndex + 1}
                onChange={(v) => patch({ sourceIndex: typeof v === 'number' ? Math.max(0, Math.round(v) - 1) : 0 })}
              />
            </SimpleGrid>
          )}

          {config.selection === 'Auto' && (
            <>
              <SimpleGrid cols={{ base: 1, sm: 2 }}>
                <NumberInput
                  // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                  {...fd('config.maxCandidates')}
                  label={t('frame.candidates.label')}
                  description={t('frame.candidates.description')}
                  size="xs"
                  min={1}
                  max={500}
                  value={config.maxCandidates}
                  onChange={(v) => patch({ maxCandidates: typeof v === 'number' ? v : 24 })}
                />
                <NumberInput
                  // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                  {...fd('config.minShotSeconds')}
                  label={t('frame.minShot.label')}
                  description={t('frame.minShot.description')}
                  size="xs"
                  min={0}
                  step={0.1}
                  decimalScale={2}
                  value={config.minShotSeconds}
                  onChange={(v) => patch({ minShotSeconds: typeof v === 'number' ? Math.max(0, v) : 0.4 })}
                />
              </SimpleGrid>
              <Switch
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.preferBestTake')}
                label={t('frame.bestTake.label')}
                description={t('frame.bestTake.description')}
                checked={config.preferBestTake}
                onChange={(e) => patch({ preferBestTake: e.currentTarget.checked })}
              />
            </>
          )}
        </Stack>
      </Fieldset>

      <Fieldset legend={t('output.legend')}>
        <Stack gap="xs">
          <SimpleGrid cols={{ base: 1, sm: 2 }}>
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.width')}
              label={t('output.width.label')}
              description={t('output.width.description')}
              size="xs"
              min={16}
              max={4096}
              value={config.width}
              onChange={(v) => patch({ width: typeof v === 'number' ? Math.min(4096, Math.max(16, Math.round(v))) : 1280 })}
            />
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.height')}
              label={t('output.height.label')}
              description={t('output.height.description')}
              size="xs"
              min={16}
              max={4096}
              value={config.height}
              onChange={(v) => patch({ height: typeof v === 'number' ? Math.min(4096, Math.max(16, Math.round(v))) : 720 })}
            />
          </SimpleGrid>
          <SimpleGrid cols={{ base: 1, sm: 2 }}>
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.fit')}
              label={t('output.fit')}
              size="xs"
              data={FIT_VALUES.map((v) => ({ value: v, label: t(`fit.${v}`) }))}
              value={config.fit}
              onChange={(v) => v && patch({ fit: v as ThumbnailFit })}
              allowDeselect={false}
            />
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.jpegQuality')}
              label={t('output.quality.label')}
              description={t('output.quality.description')}
              size="xs"
              min={2}
              max={31}
              value={config.jpegQuality}
              onChange={(v) => patch({ jpegQuality: typeof v === 'number' ? Math.min(31, Math.max(2, Math.round(v))) : 4 })}
            />
          </SimpleGrid>
          <SimpleGrid cols={{ base: 1, sm: 2 }}>
            <TextInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.outputFileName')}
              label={t('output.fileName.label')}
              description={t('output.fileName.description')}
              size="xs"
              value={config.outputFileName}
              onChange={(e) => patch({ outputFileName: e.target.value || 'thumbnail.jpg' })}
            />
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.registerProjectFile')}
              label={t('output.save.label')}
              description={t('output.save.description')}
              checked={config.registerProjectFile}
              onChange={(e) => patch({ registerProjectFile: e.currentTarget.checked })}
            />
          </SimpleGrid>
        </Stack>
      </Fieldset>

      <Fieldset legend={t('overlay.legend')}>
        <Stack gap="xs">
          <TextInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.overlayText')}
            label={t('overlay.text.label')}
            description={t('overlay.text.description')}
            size="xs"
            maxLength={config.maxOverlayChars}
            value={config.overlayText ?? ''}
            onChange={(e) => patch({ overlayText: e.target.value || null })}
          />
          <Select
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.overlayPosition')}
            label={t('overlay.position')}
            size="xs"
            data={POSITION_VALUES.map((v) => ({ value: v, label: t(`position.${v}`) }))}
            value={config.overlayPosition}
            onChange={(v) => v && patch({ overlayPosition: v as ThumbnailOverlayPosition })}
            allowDeselect={false}
          />
          {config.overlayPosition !== 'None' && !overlayText && (
            <Text size="xs" c="orange">
              {t('overlay.nothingDrawn')}
            </Text>
          )}
          {config.overlayPosition !== 'None' && (
            <>
              <Text size="xs" c="dimmed">
                {t('overlay.burnedIn')}
              </Text>
              <SimpleGrid cols={{ base: 1, sm: 2 }}>
                <NumberInput
                  // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                  {...fd('config.overlayFontSizePct')}
                  label={t('overlay.size')}
                  size="xs"
                  min={2}
                  max={25}
                  value={config.overlayFontSizePct}
                  onChange={(v) => patch({ overlayFontSizePct: typeof v === 'number' ? Math.min(25, Math.max(2, Math.round(v))) : 9 })}
                />
                <NumberInput
                  // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                  {...fd('config.maxOverlayChars')}
                  label={t('overlay.maxChars.label')}
                  description={t('overlay.maxChars.description')}
                  size="xs"
                  min={1}
                  max={400}
                  value={config.maxOverlayChars}
                  onChange={(v) => patch({ maxOverlayChars: typeof v === 'number' ? Math.min(400, Math.max(1, Math.round(v))) : 90 })}
                />
              </SimpleGrid>
              <SimpleGrid cols={{ base: 1, sm: 2 }}>
                <Select
                  // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                  {...fd('config.overlayFontColor')}
                  label={t('overlay.fontColor')}
                  size="xs"
                  data={fontColorOptions}
                  value={config.overlayFontColor}
                  onChange={(v) => v && patch({ overlayFontColor: v })}
                  allowDeselect={false}
                />
                <Select
                  // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                  {...fd('config.overlayBoxColor')}
                  label={t('overlay.boxColor.label')}
                  description={t('overlay.boxColor.description')}
                  size="xs"
                  data={boxColorOptions}
                  value={config.overlayBoxColor}
                  onChange={(v) => v && patch({ overlayBoxColor: v })}
                  allowDeselect={false}
                />
              </SimpleGrid>
            </>
          )}
        </Stack>
      </Fieldset>

      <AdvancedSection description={t('advanced.description')}>
        <TextInput
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.analysisStepResultId')}
          label={t('advanced.reuse.label')}
          placeholder={t('advanced.reuse.placeholder')}
          size="xs"
          value={config.analysisStepResultId ?? ''}
          onChange={(e) => patch({ analysisStepResultId: e.target.value || null })}
        />
      </AdvancedSection>
    </Stack>
  );
}
