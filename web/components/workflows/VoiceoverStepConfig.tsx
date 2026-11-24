'use client';

import Link from 'next/link';
import {
  Stack, Autocomplete, TextInput, NumberInput, SegmentedControl, Button, Text, Divider, SimpleGrid, Group, ActionIcon, Select, Anchor,
} from '@mantine/core';
import { IconPlus, IconTrash, IconMicrophone } from '@tabler/icons-react';
import { useT, msg } from '@/i18n';
import { getVoiceoverSourceError } from '@/lib/utils/voiceover-validation';
import type {
  VoiceoverStepConfig as VoiceoverStepConfigValue,
  VoiceoverSource,
  VoiceoverLine,
  PronunciationDictionaryMode,
  VoiceCastingMode,
} from '@/lib/types/workflow';
import { useProjectVoices } from '@/lib/hooks/use-voices';
import type { StepData } from './WorkflowStepList';
import { ProviderSelect, StepOrderSelect, AskAssistantButton, CostWarning, useFd, useFdRaw, assistantPrompt, AdvancedSection } from './config-kit';
import { VoiceProviderStatusNotice } from './VoiceProviderStatusNotice';
import { useAuth } from '@/lib/hooks/use-auth';
import { useSelectableProviders } from '@/lib/hooks/use-inference-providers';

/** Builds a sensible default config for a freshly-added Voiceover step. */
export function createDefaultVoiceoverStepConfig(): VoiceoverStepConfigValue {
  return {
    source: {
      kind: 'Inline',
      lines: [],
    },
    maxBytes: 2_000_000,
  };
}

interface VoiceoverStepConfigProps {
  config: VoiceoverStepConfigValue;
  onChange: (config: VoiceoverStepConfigValue) => void;
  allSteps?: StepData[];
  currentStepIndex?: number;
  /** Lets the project-voice picker list this project's consented voices. */
  projectId?: string;
}

/**
 * Configuration form for a Voiceover step.
 * Mirrors the VideoCompile/VideoAnalyze config editor patterns with Mantine v8 conventions.
 */
export function VoiceoverStepConfig({
  config, onChange, allSteps = [], currentStepIndex = 0, projectId,
}: VoiceoverStepConfigProps) {
  const t = useT('wfSteps.voiceover');
  const { data: projectVoices } = useProjectVoices(projectId);
  // Only a SELECTABLE voice is ever offered — consent attested, not revoked, within retention. A
  // config that still names an unselectable one shows it, flagged, so the author sees why the step
  // will fail rather than seeing a blank picker.
  const selectableVoices = (projectVoices ?? []).filter((v) => v.selectable);
  const currentVoice = (projectVoices ?? []).find((v) => v.id === config.projectVoiceId);
  const voiceOptions = [
    ...selectableVoices.map((v) => ({ value: v.id, label: `${v.name} (${v.source})` })),
    ...(currentVoice && !currentVoice.selectable ? [{ value: currentVoice.id, label: `${currentVoice.name}: ${currentVoice.unselectableReason?.replaceAll('_', ' ')}`, disabled: true }] : []),
  ];
  const patch = (updates: Partial<VoiceoverStepConfigValue>) => onChange({ ...config, ...updates });

  const patchSource = (updates: Partial<VoiceoverSource>) => {
    patch({ source: { ...config.source, ...updates } });
  };

  const handleSourceKindChange = (kind: string) => {
    if (kind === config.source.kind) return;

    // Each branch clears the fields the other modes own, so a source never carries stale state
    // from a mode the user has left behind.
    switch (kind) {
      case 'Inline':
        patchSource({
          kind: 'Inline',
          lines: [],
          scriptwriterStepOrder: undefined,
          narrationWriterStepOrder: undefined,
        });
        break;
      case 'ScriptScenes':
        patchSource({
          kind: 'ScriptScenes',
          lines: undefined,
          scriptwriterStepOrder: null,
          narrationWriterStepOrder: undefined,
        });
        break;
      case 'NarrationPlan':
        // No step chosen yet: `getVoiceoverSourceError` reports the source as unconfigured.
        patchSource({
          kind: 'NarrationPlan',
          lines: undefined,
          scriptwriterStepOrder: undefined,
          narrationWriterStepOrder: null,
          pickupPlannerStepOrder: undefined,
        });
        break;
      case 'PickupPlan':
        patchSource({
          kind: 'PickupPlan',
          lines: undefined,
          scriptwriterStepOrder: undefined,
          narrationWriterStepOrder: undefined,
          pickupPlannerStepOrder: null,
        });
        break;
      default:
        break;
    }
  };

  const handleAddLine = () => {
    const currentLines = config.source.lines ?? [];
    const newLine: VoiceoverLine = {
      text: '',
      startSec: 0,
      voiceId: null,
    };
    patchSource({ lines: [...currentLines, newLine] });
  };

  const handleRemoveLine = (index: number) => {
    const currentLines = config.source.lines ?? [];
    patchSource({ lines: currentLines.filter((_, i) => i !== index) });
  };

  const handleUpdateLine = (index: number, updates: Partial<VoiceoverLine>) => {
    const currentLines = config.source.lines ?? [];
    const updated = [...currentLines];
    updated[index] = { ...updated[index], ...updates };
    patchSource({ lines: updated });
  };

  const fd = useFd();
  const fdr = useFdRaw();
  // A17-UI: the member-facing selectable list, not the platform admin's — every member of a
  // workspace may choose the voice service its narration runs on (Billing is the one exception,
  // and asking on its behalf would only spend a request on a guaranteed 403).
  const { user } = useAuth();
  const role = user?.orgRole ?? null;
  const { data: providers } = useSelectableProviders('SpeechSynthesis', !!role && role !== 'Billing');
  const tts = (providers ?? []).filter((p) => p.capability === 'SpeechSynthesis');
  // The selectable rows carry no isDefault (a member is not told which row is the default), so the
  // model suggestion falls back to the first row the workspace can use rather than the default row.
  const activeProvider = tts.find((p) => p.id === config.providerId) ?? tts[0];
  const modelSuggestions = activeProvider?.model ? [activeProvider.model] : [];

  const sourceError = getVoiceoverSourceError(config.source);

  return (
    <Stack gap="md" onClick={(e) => e.stopPropagation()}>
      <Group justify="space-between" align="center">
        <Text size="xs" c="dimmed">{t('intro')}</Text>
        <AskAssistantButton prompt={assistantPrompt(currentStepIndex, 'Voiceover', msg('wfSteps.prompts.voiceover'))} />
      </Group>
      <VoiceProviderStatusNotice />
      <Stack gap="xs">
        <Text size="sm" fw={500}>{t('source.title')}</Text>
        <Text size="xs" c="dimmed">{t('source.description')}</Text>
        <SegmentedControl
          aria-label={t('source.aria')}
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fdr('config.source.kind')}
          data={[
            { label: t('source.kind.Inline'), value: 'Inline' },
            { label: t('source.kind.ScriptScenes'), value: 'ScriptScenes' },
            { label: t('source.kind.NarrationPlan'), value: 'NarrationPlan' },
            { label: t('source.kind.PickupPlan'), value: 'PickupPlan' },
          ]}
          value={config.source.kind}
          onChange={handleSourceKindChange}
        />
      </Stack>

      {config.source.kind === 'Inline' && (
        <Stack gap="xs">
          <Group justify="space-between" align="center">
            <Text size="sm" fw={500}>
              {t('lines.title')}
            </Text>
            <Button
              size="xs"
              leftSection={<IconPlus size={14} />}
              onClick={handleAddLine}
            >
              {t('lines.add')}
            </Button>
          </Group>
          {(config.source.lines ?? []).length === 0 ? (
            <Text size="xs" c="dimmed">
              {t('lines.empty')}
            </Text>
          ) : (
            <Stack gap="xs">
              {config.source.lines?.map((line, index) => (
                <Group key={index} gap="xs" align="flex-end">
                  <TextInput
                    label={t('lines.text.label')}
                    placeholder={t('lines.text.placeholder')}
                    size="xs"
                    style={{ flex: 1 }}
                    value={line.text}
                    onChange={(e) => handleUpdateLine(index, { text: e.target.value })}
                  />
                  <NumberInput
                    label={t('lines.start')}
                    placeholder="0"
                    size="xs"
                    min={0}
                    step={0.1}
                    style={{ width: '120px' }}
                    value={line.startSec}
                    onChange={(v) => handleUpdateLine(index, { startSec: typeof v === 'number' ? v : 0 })}
                  />
                  <TextInput
                    label={t('lines.voice.label')}
                    placeholder={t('lines.voice.placeholder')}
                    size="xs"
                    style={{ width: '140px' }}
                    value={line.voiceId ?? ''}
                    onChange={(e) => handleUpdateLine(index, { voiceId: e.target.value || null })}
                  />
                  <ActionIcon
                    color="red"
                    variant="light"
                    onClick={() => handleRemoveLine(index)}
                    title={t('lines.remove')}
                  >
                    <IconTrash size={16} />
                  </ActionIcon>
                </Group>
              ))}
            </Stack>
          )}
        </Stack>
      )}

      {config.source.kind === 'ScriptScenes' && (
        <StepOrderSelect
          label={t('script.label')}
          description={t('script.description')}
          size="xs"
          steps={allSteps}
          currentIndex={currentStepIndex}
          scope="before"
          value={config.source.scriptwriterStepOrder}
          onChange={(n) => patchSource({ scriptwriterStepOrder: n })}
          wrapperProps={fd('config.source.scriptwriterStepOrder').wrapperProps}
        />
      )}

      {config.source.kind === 'NarrationPlan' && (
        <Stack gap="xs">
          <StepOrderSelect
            label={t('narration.label')}
            description={t('narration.description')}
            size="xs"
            steps={allSteps}
            currentIndex={currentStepIndex}
            scope="before"
            value={config.source.narrationWriterStepOrder}
            onChange={(n) => patchSource({ narrationWriterStepOrder: n })}
            placeholder={currentStepIndex > 0 ? t('selectStep') : t('noEarlierSteps')}
            wrapperProps={fd('config.source.narrationWriterStepOrder').wrapperProps}
          />
          {sourceError && (
            <Text size="xs" c="red">
              {sourceError}
            </Text>
          )}
          <Text size="xs" c="dimmed">
            {t('narration.readOnly')}
          </Text>
        </Stack>
      )}

      {config.source.kind === 'PickupPlan' && (
        <Stack gap="xs">
          <StepOrderSelect
            label={t('pickup.label')}
            description={t('pickup.description')}
            size="xs"
            steps={allSteps}
            currentIndex={currentStepIndex}
            scope="before"
            value={config.source.pickupPlannerStepOrder}
            onChange={(n) => patchSource({ pickupPlannerStepOrder: n })}
            placeholder={currentStepIndex > 0 ? t('selectStep') : t('noEarlierSteps')}
            wrapperProps={fd('config.source.pickupPlannerStepOrder').wrapperProps}
          />
          {sourceError && (
            <Text size="xs" c="red">
              {sourceError}
            </Text>
          )}
        </Stack>
      )}

      <Divider label={t('voice.divider')} labelPosition="left" />
      <SimpleGrid cols={{ base: 1, sm: 2 }}>
        <Select
          label={t('voice.label')}
          description={projectId
            ? t('voice.descriptionProject')
            : t('voice.descriptionNoProject')}
          size="xs"
          data={voiceOptions}
          value={config.projectVoiceId ?? null}
          onChange={(v) => patch({ projectVoiceId: v })}
          placeholder={voiceOptions.length > 0 ? t('voice.defaultPlaceholder') : t('voice.none')}
          clearable
          disabled={!projectId}
          wrapperProps={fd('config.projectVoiceId').wrapperProps}
        />
        <TextInput
          label={t('voice.language.label')}
          description={t('voice.language.description')}
          // eslint-disable-next-line i18next/no-literal-string -- syntax example, not copy
          placeholder="en-US"
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.language')}
          size="xs"
          value={config.language ?? ''}
          onChange={(e) => patch({ language: e.target.value || null })}
        />
      </SimpleGrid>
      {projectId && (
        <Text size="xs">
          <IconMicrophone size={12} style={{ verticalAlign: -1 }} />{' '}
          <Anchor component={Link} href={`/projects/${projectId}?tab=voices`} size="xs">
            {t('voice.manage')}
          </Anchor>
        </Text>
      )}

      <Divider label={t('service.divider')} labelPosition="left" />
      <CostWarning>
        {t('service.cost')}
      </CostWarning>
      <ProviderSelect
        capability="SpeechSynthesis"
        label={t('service.label')}
        description={t('service.description')}
        value={config.providerId}
        onChange={(providerId) => patch({ providerId })}
        nullMeans={t('service.nullMeans')}
        wrapperProps={fd('config.providerId').wrapperProps}
      />

      <AdvancedSection description={t('advanced.description')}>
        <SimpleGrid cols={{ base: 1, sm: 2 }}>
          <Autocomplete
            label={t('advanced.model.label')}
            description={activeProvider ? t('advanced.model.descriptionProvider', { name: activeProvider.name, model: activeProvider.model || t('advanced.providerDefault') }) : t('advanced.model.descriptionService')}
            placeholder={activeProvider?.model || t('advanced.providerDefaultCapital')}
            size="xs"
            data={modelSuggestions}
            value={config.model ?? ''}
            onChange={(v) => patch({ model: v || null })}
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.model')}
          />
          <TextInput
            label={t('advanced.voiceId.label')}
            description={t('advanced.voiceId.description')}
            placeholder={t('advanced.providerDefaultCapital')}
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.defaultVoiceId')}
            size="xs"
            value={config.defaultVoiceId ?? ''}
            onChange={(e) => patch({ defaultVoiceId: e.target.value || null })}
          />
          <NumberInput
            label={t('advanced.maxBytes.label')}
            description={t('advanced.maxBytes.description')}
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.maxBytes')}
            size="xs"
            min={1}
            value={config.maxBytes ?? 2_000_000}
            onChange={(v) => patch({ maxBytes: typeof v === 'number' ? v : 2_000_000 })}
          />
          <Select
            label={t('advanced.pronunciation.label')}
            description={t('advanced.pronunciation.description')}
            size="xs"
            data={[{ value: 'Off', label: t('advanced.pronunciation.Off') }, { value: 'Auto', label: t('advanced.pronunciation.Auto') }]}
            value={config.pronunciationDictionary ?? 'Off'}
            onChange={(v) => patch({ pronunciationDictionary: (v as PronunciationDictionaryMode) ?? 'Off' })}
            allowDeselect={false}
            wrapperProps={fd('config.pronunciationDictionary').wrapperProps}
          />
          <Select
            label={t('advanced.casting.label')}
            description={t('advanced.casting.description')}
            size="xs"
            data={[{ value: 'Off', label: t('advanced.casting.Off') }, { value: 'Decision', label: t('advanced.casting.Decision') }]}
            value={config.voiceCasting ?? 'Off'}
            onChange={(v) => patch({ voiceCasting: (v as VoiceCastingMode) ?? 'Off' })}
            allowDeselect={false}
            wrapperProps={fd('config.voiceCasting').wrapperProps}
          />
        </SimpleGrid>
      </AdvancedSection>
    </Stack>
  );
}
